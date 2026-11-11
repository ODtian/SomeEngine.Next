namespace SomeEngine.ECS;

public interface ISystemVersions
{
    int VersionCount { get; }
    void Complete() { }
    void CopyVersions(Span<ulong> versions);
    void RestoreVersions(ReadOnlySpan<ulong> versions);
}
