namespace CloudFlow.App.Infrastructure;

/// <summary>页面读取会话：仅当前且未取消的读取可以应用结果。由 UI 线程调用。</summary>
public sealed class LatestRead
{
    private Lease? _current;
    public Lease Begin()
    {
        Cancel();
        return _current = new Lease(this);
    }
    public void Cancel()
    {
        var previous = _current;
        _current = null;
        previous?.Cancel();
    }

    public sealed class Lease : IDisposable
    {
        private readonly LatestRead _owner;
        private readonly CancellationTokenSource _source = new();
        private bool _disposed;
        internal Lease(LatestRead owner) { _owner = owner; Token = _source.Token; }
        public CancellationToken Token { get; }
        public bool IsCurrent => ReferenceEquals(_owner._current, this) && !Token.IsCancellationRequested;
        internal void Cancel() { if (!_disposed) _source.Cancel(); }
        public void Dispose()
        {
            if (_disposed) return;
            if (ReferenceEquals(_owner._current, this)) _owner._current = null;
            _disposed = true;
            _source.Dispose();
        }
    }
}
