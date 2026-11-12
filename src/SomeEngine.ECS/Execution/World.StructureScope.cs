namespace SomeEngine.ECS;

/// <summary>Holds World structure write access while a batch of typed operations runs.</summary>
public readonly ref struct WorldStructureWriteScope
{
    private readonly WorldAccess _access;
    internal WorldStructureWriteScope(WorldAccess access) => _access = access;
    public void Dispose() => _access.Dispose();
}

public sealed partial class World
{
    /// <summary>Opens one structure write range for creation, value setup and topology edits.</summary>
    public WorldStructureWriteScope OpenStructureWrite()
    {
        WorldAccess access = OpenStructure();
        try
        {
            RequireStructure();
            return new(access);
        }
        catch
        {
            access.Dispose();
            throw;
        }
    }
}
