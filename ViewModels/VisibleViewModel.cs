using CommunityToolkit.Mvvm.ComponentModel;
using TrayFolder.Core;

namespace TrayFolder.ViewModels;

/// <summary>
/// A ViewModel that works only while its page is on screen: <see cref="VisibleToken"/> ends all of
/// its async work when the page hides, so nothing runs in the background.
/// </summary>
public abstract partial class VisibleViewModel(ITextProvider text, IErrorReporter errors) : ObservableObject, IDisposable
{
    private CancellationTokenSource? _lifetime;
    private bool _disposed;

    protected ITextProvider Text { get; } = text;

    protected IErrorReporter Errors { get; } = errors;

    protected CancellationToken VisibleToken => _lifetime?.Token ?? new CancellationToken(canceled: true);

    public bool HasError => ErrorMessage.Length > 0;

    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; protected set; } = string.Empty;

    public void OnShown()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsVisible)
        {
            return;
        }

        _lifetime = new CancellationTokenSource();
        IsVisible = true;
        OnActivated();
    }

    public void OnHidden()
    {
        if (!IsVisible)
        {
            return;
        }

        IsVisible = false;
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        OnDeactivated();
    }

    public virtual void Dispose()
    {
        OnHidden();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    /// <summary>The page just came on screen.</summary>
    protected virtual void OnActivated() { }

    /// <summary>The page just left the screen; its <see cref="VisibleToken"/> is already canceled.</summary>
    protected virtual void OnDeactivated() { }

    /// <summary>Runs an action the user asked for. A failure is reported rather than thrown; hiding the page ends it quietly.</summary>
    protected async Task RunAsync(string operation, Func<CancellationToken, Task> action)
    {
        CancellationToken token = VisibleToken;
        try
        {
            await action(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ReportFailure(operation, ex, token);
        }
    }

    protected void Run(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ReportFailure(operation, ex);
        }
    }

    protected void ReportFailure(string operation, Exception exception, CancellationToken token = default)
    {
        Errors.Report(operation, exception);
        if (IsVisible && !token.IsCancellationRequested)
        {
            ErrorMessage = Text.Format("OperationFailed", exception.Message);
            OnFailureReported(exception);
        }
    }

    protected virtual void OnFailureReported(Exception exception) { }
}
