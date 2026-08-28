namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>Shared ABI for GPU work that could not be admitted by a bounded queue.</summary>
internal static class ClusterOverflowCounter
{
    internal const int TraversalCandidates = 0;
    internal const int PhaseOneVisible = 1;
    internal const int PhaseTwoCandidates = 2;
    internal const int PhaseTwoVisible = 3;
    internal const int ShadowCandidates = 4;
    internal const int LightGrid = 5;
    internal const int VirtualShadowRequests = 6;
    internal const int VirtualShadowRasterBudget = 7;
    internal const int Count = 8;
}
