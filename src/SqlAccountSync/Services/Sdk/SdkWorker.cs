using System.Collections.Concurrent;

namespace SqlAccountSync.Services.Sdk;

/// <summary>
/// Dedicated STA thread that owns all SQLAcc.BizApp COM interactions.
/// Never share COM objects across threads.
/// </summary>
public sealed class SdkWorker : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _thread;
    private volatile bool _disposed;

    public SdkWorker()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SqlSdk-STA-Worker"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public bool IsWorkerThread => Thread.CurrentThread == _thread;

    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsWorkerThread)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }), cancellationToken);
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(func);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsWorkerThread)
            return Task.FromResult(func());

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }), cancellationToken);
        return tcs.Task;
    }

    public T Invoke<T>(Func<T> func) =>
        InvokeAsync(func).GetAwaiter().GetResult();

    public void Invoke(Action action) =>
        InvokeAsync(action).GetAwaiter().GetResult();

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try { item.Action(); }
            catch { /* captured by WorkItem wrapper */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _ = _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }

    private sealed class WorkItem(Action action)
    {
        public Action Action { get; } = action;
    }
}
