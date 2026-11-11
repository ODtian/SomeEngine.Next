namespace SomeEngine.ECS.Comparison;

internal sealed class ArchiveResourceCopy : IDisposable
{
    internal readonly NativeArchiveResources Values;
    internal ArchiveResourceCopy(NativeArchiveResources source, ResourceCounter counter)
    {
        Values = new(new ReferenceResources(), counter);
        ArchiveResources.Copy(source, Values);
    }
    public void Dispose() => ArchiveResources.Release(Values);
}
