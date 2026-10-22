namespace SomeEngine.Animation.Pose;

/// <summary>An instance's contiguous section in its pose storage.</summary>
public readonly record struct PoseRange(int Start, int Count);
