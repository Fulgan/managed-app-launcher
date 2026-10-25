using System.Collections.Concurrent;

namespace ManagedLauncher.Core.Icons;

/// <summary>Runs work on one STA thread. Shell image and naming APIs for packaged apps expect an STA caller.</summary>
public sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> queue = [];
    private readonly Thread thread;

    public StaWorker()
    {
        thread = new Thread(() =>
        {
            foreach (var work in queue.GetConsumingEnumerable())
                work();
        })
        {
            IsBackground = true,
            Name = "shell-sta",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Add(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    public void Dispose() => queue.CompleteAdding();
}
