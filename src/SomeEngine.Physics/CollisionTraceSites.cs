using SomeEngine.Diagnostics;

namespace SomeEngine.Physics;

internal static class CollisionTraceSites
{
    internal static readonly TraceSite Partitions = new("physics", "Collision.Partitions");
    internal static readonly TraceSite Count = new("physics", "Collision.CountPairs");
    internal static readonly TraceSite Prefix = new("physics", "Collision.BinOffsets");
    internal static readonly TraceSite Write = new("physics", "Collision.WritePairs");
    internal static readonly TraceSite NarrowPhase = new("physics", "Collision.NarrowPhase");
    internal static readonly TraceSite CandidateCount = new("physics", "Collision.Candidates");
    internal static readonly TraceSite DetectionCount = new("physics", "Collision.DetectionPairs");
}
