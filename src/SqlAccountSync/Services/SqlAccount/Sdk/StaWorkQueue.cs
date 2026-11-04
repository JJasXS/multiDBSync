using System.Collections.Concurrent;

namespace SqlAccountSync.Services.SqlAccount.Sdk;

/// <summary>
/// Runs COM work on a dedicated STA thread. SQLAcc.BizApp is an out-of-proc LocalServer;
/// callers from MTA ASP.NET threads must marshal via STA.
/// </summary>
public sealed class StaWorkQueue : IDisposable
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _thread;
    private int _disposed;

    public StaWorkQueue()
    {
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "SqlAccSdk-STA"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task RunAsync(Action action, CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            action();
            return 0;
        }, cancellationToken);

    public Task<T> RunAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        }

        _queue.Add(new WorkItem(
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }), cancellationToken);

        return tcs.Task;
    }

    private void ThreadMain()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
            item.Execute();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(5)))
        {
            // Background STA thread — avoid Abort; leave process cleanup to host shutdown.
        }

        _queue.Dispose();
    }

    private sealed class WorkItem(Action execute)
    {
        public void Execute() => execute();
    }
}
