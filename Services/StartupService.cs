using System.Runtime.InteropServices;
using TrayFolder.Core;
using Windows.ApplicationModel;

namespace TrayFolder.Services;

internal sealed class StartupService : IStartupService
{
    private const string TaskId = "TrayFolderStartup";

    public async Task<StartupState> GetStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return Map(task.State);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80073D54))
        {
            return StartupState.Unavailable;
        }
    }

    public async Task<StartupState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        StartupTask task = await StartupTask.GetAsync(TaskId).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (enabled)
        {
            return Map(await task.RequestEnableAsync().AsTask(cancellationToken));
        }

        task.Disable();
        return Map(task.State);
    }

    private static StartupState Map(StartupTaskState state) => state switch
    {
        StartupTaskState.Disabled => StartupState.Disabled,
        StartupTaskState.Enabled => StartupState.Enabled,
        StartupTaskState.DisabledByUser => StartupState.DisabledByUser,
        StartupTaskState.DisabledByPolicy => StartupState.DisabledByPolicy,
        StartupTaskState.EnabledByPolicy => StartupState.EnabledByPolicy,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
