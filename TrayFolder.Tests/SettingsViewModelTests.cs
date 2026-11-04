using TrayFolder.Core;
using TrayFolder.ViewModels;

namespace TrayFolder.Tests;

[TestClass]
public sealed class SettingsViewModelTests
{
    private static SettingsViewModel Create(TestStartup startup, TestErrors? errors = null, TestFolders? folders = null, TestNavigation? navigation = null) =>
        new(folders ?? new TestFolders(), startup, navigation ?? new TestNavigation(), new TestTextProvider(), errors ?? new TestErrors(), "Test");

    private static async Task<SettingsViewModel> ShowAsync(TestStartup startup, TestErrors? errors = null, TestFolders? folders = null, TestNavigation? navigation = null)
    {
        SettingsViewModel vm = Create(startup, errors, folders, navigation);
        vm.OnShown();
        await vm.LoadStartupCommand.ExecutionTask!;
        return vm;
    }

    [TestMethod]
    public async Task OnShown_EnabledState_DoesNotWriteProgrammaticToggle()
    {
        TestStartup startup = new() { State = StartupState.Enabled };

        using SettingsViewModel vm = await ShowAsync(startup);

        Assert.IsTrue(vm.IsStartupOn);
        Assert.IsTrue(vm.CanChangeStartup);
        Assert.AreEqual(0, startup.WriteCalls.Count);
    }

    [TestMethod]
    public async Task IsStartupOn_UserToggles_WritesTheNewState()
    {
        TestStartup startup = new();
        TaskCompletionSource<StartupState> write = new();
        startup.Writes.Enqueue(write);
        using SettingsViewModel vm = await ShowAsync(startup);

        vm.IsStartupOn = true;
        write.SetResult(StartupState.Enabled);
        await vm.SetStartupCommand.ExecutionTask!;

        Assert.IsTrue(startup.WriteCalls.Single().Enabled);
        Assert.IsTrue(vm.CanChangeStartup);
    }

    [TestMethod]
    public async Task CanChangeStartup_NotifiesWhenAWriteStartsAndEnds()
    {
        TestStartup startup = new();
        TaskCompletionSource<StartupState> write = new();
        startup.Writes.Enqueue(write);
        using SettingsViewModel vm = await ShowAsync(startup);
        List<bool> seen = [];
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.CanChangeStartup))
            {
                seen.Add(vm.CanChangeStartup);
            }
        };

        vm.IsStartupOn = true;
        CollectionAssert.Contains(seen, false);
        write.SetResult(StartupState.Enabled);
        await vm.SetStartupCommand.ExecutionTask!;

        Assert.IsTrue(vm.CanChangeStartup);
    }

    [TestMethod]
    public async Task SetStartup_PendingWrite_DisablesToggleAndRejectsReentrantChange()
    {
        TestStartup startup = new();
        TaskCompletionSource<StartupState> write = new();
        startup.Writes.Enqueue(write);
        using SettingsViewModel vm = await ShowAsync(startup);

        vm.IsStartupOn = true;
        Task execution = vm.SetStartupCommand.ExecutionTask!;
        Assert.IsFalse(vm.CanChangeStartup);
        Assert.IsFalse(vm.SetStartupCommand.CanExecute(false));
        vm.IsStartupOn = false;
        Assert.AreEqual(1, startup.WriteCalls.Count);
        write.SetResult(StartupState.Enabled);
        await execution;

        Assert.IsTrue(vm.IsStartupOn);
        Assert.IsTrue(vm.CanChangeStartup);
    }

    [TestMethod]
    public async Task OnShown_ReopenedWithOlderRequestPending_DropsOldState()
    {
        TestStartup startup = new();
        TaskCompletionSource<StartupState> old = new();
        startup.Reads.Enqueue(old);
        using SettingsViewModel vm = Create(startup);
        vm.OnShown();
        Task first = vm.LoadStartupCommand.ExecutionTask!;
        vm.OnHidden();
        startup.State = StartupState.EnabledByPolicy;

        vm.OnShown();
        await vm.LoadStartupCommand.ExecutionTask!;
        old.SetResult(StartupState.Disabled);
        await first;

        Assert.IsTrue(startup.ReadTokens[0].IsCancellationRequested);
        Assert.IsTrue(vm.IsStartupOn);
        Assert.IsFalse(vm.CanChangeStartup);
        Assert.IsFalse(vm.LoadStartupCommand.IsRunning);
    }

    [TestMethod]
    public async Task SetStartup_ReopenedDuringCanceledWrite_PreventsSecondWriteUntilCompletion()
    {
        TestStartup startup = new();
        TaskCompletionSource<StartupState> write = new();
        startup.Writes.Enqueue(write);
        using SettingsViewModel vm = await ShowAsync(startup);
        vm.IsStartupOn = true;
        Task pending = vm.SetStartupCommand.ExecutionTask!;
        vm.OnHidden();

        vm.OnShown();
        await vm.LoadStartupCommand.ExecutionTask!;
        Assert.IsFalse(vm.CanChangeStartup);
        vm.IsStartupOn = true;
        Assert.AreEqual(1, startup.WriteCalls.Count);
        write.SetResult(StartupState.Enabled);
        await pending;

        Assert.IsTrue(startup.WriteCalls[0].Token.IsCancellationRequested);
        Assert.IsTrue(vm.CanChangeStartup);
    }

    [TestMethod]
    public async Task SetStartup_ServiceFails_RestoresKnownStateAndReportsError()
    {
        TestStartup startup = new();
        TaskCompletionSource<StartupState> write = new();
        startup.Writes.Enqueue(write);
        TestErrors errors = new();
        using SettingsViewModel vm = await ShowAsync(startup, errors);

        vm.IsStartupOn = true;
        Task execution = vm.SetStartupCommand.ExecutionTask!;
        write.SetException(new IOException("Failed to save"));
        await execution;

        Assert.IsFalse(vm.IsStartupOn);
        Assert.AreEqual(1, startup.WriteCalls.Count, "Restoring the known state is not a new write.");
        StringAssert.Contains(vm.ErrorMessage, "Failed to save");
        Assert.IsTrue(vm.HasError);
        Assert.AreEqual(1, errors.Exceptions.Count);
    }

    [TestMethod]
    public async Task ChooseFolder_Picked_SetsRootAndRefreshes()
    {
        TestFolders folders = new();
        TestNavigation navigation = new() { PickedFolder = @"C:\Picked" };
        using SettingsViewModel vm = await ShowAsync(new TestStartup(), folders: folders, navigation: navigation);

        await vm.ChooseFolderCommand.ExecuteAsync(null);

        Assert.AreEqual(@"C:\Picked", folders.RootPath);
        Assert.AreEqual(@"C:\Picked", vm.FolderPath);
        Assert.IsTrue(vm.CanResetFolder);
    }

    [TestMethod]
    public async Task ResetFolder_ReturnsToDownloads()
    {
        TestFolders folders = new();
        using SettingsViewModel vm = await ShowAsync(new TestStartup(), folders: folders);

        vm.ResetFolderCommand.Execute(null);

        Assert.AreEqual(folders.DownloadsPath, vm.FolderPath);
        Assert.IsFalse(vm.CanResetFolder);
    }

    [TestMethod]
    public async Task ClearRecent_EmptiesAndDisablesItself()
    {
        TestFolders folders = new() { RecentFolders = [@"C:\one", @"C:\two"] };
        using SettingsViewModel vm = await ShowAsync(new TestStartup(), folders: folders);
        Assert.IsTrue(vm.ClearRecentCommand.CanExecute(null));
        Assert.AreEqual("2 folders in the folder menu.", vm.RecentText);

        vm.ClearRecentCommand.Execute(null);

        Assert.IsFalse(vm.ClearRecentCommand.CanExecute(null));
        Assert.AreEqual("None yet.", vm.RecentText);
    }
}
