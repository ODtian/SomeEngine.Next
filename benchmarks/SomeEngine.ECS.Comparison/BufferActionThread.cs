using System.Runtime.ExceptionServices;

namespace SomeEngine.ECS.Comparison;

internal static class BufferActionThread
{
    internal static bool UsesActionThread(string workload)
        => workload.StartsWith("buffer-") || workload is "clone-set" or "prefab-set";

    internal static Action Wrap(Action action) => () => Run(action);

    internal static void Run(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                BenchmarkCpu.BindCurrentThread();
                action();
            }
            catch (Exception exception) { error = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.Start();
        thread.Join();
        error?.Throw();
    }
}
