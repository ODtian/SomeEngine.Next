namespace SomeEngine.ECS.Systems;

/// <summary>One system's position in its group and the version window it observes.</summary>
public sealed partial class SystemState
{
    public ulong LastSystemVersion { get; internal set; }
    public ulong CurrentSystemVersion { get; internal set; }
    public int Index { get; internal set; }
    public bool Created { get; internal set; }
    public bool Enabled { get; internal set; } = true;
}
