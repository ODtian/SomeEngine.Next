using SomeEngine.ECS.Systems;

namespace SomeEngine.ECS.Comparison;

// Normal-path code-generation probe only. Reuse the existing comparison
// payload and result oracle; no production API or JIT policy is changed.
internal static class PublicQueryPolicyChecks
{
    private const string Workload = "iterate-two";

    internal static void Run()
    {
        using var world = new SomeEngineWorld(
            1_024, connectJobs: true, initialWorkload: Workload);
        Action action = world.GetAction(Workload);
        world.CheckPublicQueryPolicyRoute();
        for (int runs = 1; runs <= 3; runs++)
        {
            action();
            world.CheckResult(Workload, runs);
            world.CheckPublicQueryPolicyRoute();
        }
        Console.WriteLine("Public Query policy: connected WorldJobs, direct public two-column ForEach, "
            + "one initial action and two same-instance reuses passed; TryGetValues bypass remained unavailable. "
            + "No timing, allocation, code-generation verdict or whole-API zero-overhead acceptance is claimed.");
    }
}

internal sealed partial class SomeEngineWorld
{
    internal void CheckPublicQueryPolicyRoute()
    {
        if (_world.Execution is not WorldJobs)
            throw new InvalidOperationException("The public Query policy probe requires a connected WorldJobs world.");
        if (_two.TryGetValues<Position, Velocity>(out _, out _))
            throw new InvalidOperationException("TryGetValues would bypass the direct public ForEach route.");
    }
}
