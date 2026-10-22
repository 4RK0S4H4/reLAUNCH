using System.Collections.Concurrent;

namespace ReLaunch.Agent;

/// <summary>Runs shell / COM work on one long-lived STA thread.</summary>
internal sealed class StaWorker
{
    private readonly BlockingCollection<Action> _queue = new();

    public StaWorker()
    {
        var thread = new Thread(() =>
        {
            foreach (var work in _queue.GetConsumingEnumerable()) work();
        })
        { IsBackground = true, Name = "reLAUNCH STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
