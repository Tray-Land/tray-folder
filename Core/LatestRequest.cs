namespace TrayFolder.Core;

/// <summary>
/// "Newest wins" for async work: beginning a request cancels the one before it, so a slow older
/// result can't overwrite a newer one. UI thread only.
/// </summary>
public sealed class LatestRequest
{
    private CancellationTokenSource? _current;

    /// <summary>Starts a request that also ends with <paramref name="lifetime"/>. Dispose it when the work finishes.</summary>
    public Scope Begin(CancellationToken lifetime)
    {
        Cancel();
        _current = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        return new Scope(this, _current);
    }

    public void Cancel() => _current?.Cancel();

    public sealed class Scope(LatestRequest owner, CancellationTokenSource source) : IDisposable
    {
        public CancellationToken Token { get; } = source.Token;

        public void Dispose()
        {
            if (ReferenceEquals(owner._current, source))
            {
                owner._current = null;
            }

            source.Dispose();
        }
    }
}
