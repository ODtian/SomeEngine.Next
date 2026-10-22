namespace SomeEngine.Resources;

/// <summary>Editable terrain fields and their shared revision sequence.</summary>
public sealed partial class Terrain
{
    private readonly List<HeightField> _heightFields = [];
    private ulong _revision;

    internal object Gate { get; } = new();
    internal TerrainEditing Editing { get; }
    internal ulong NextRevision() => ++_revision;

    public Terrain(TerrainEditing editing)
    {
        Editing = editing;
        HeightFields = _heightFields.AsReadOnly();
    }

    /// <summary>Latest submitted terrain revision. Field reads wait for its data.</summary>
    public ulong Revision { get { lock (Gate) return _revision; } }
    public IReadOnlyList<HeightField> HeightFields { get; }

    public HeightField CreateHeightField(string name, HeightGrid grid, float initialHeight = 0f)
    {
        lock (Gate)
        {
            var data = new HeightFieldData([], new TerrainEdit(NextRevision(), default));
            var field = new HeightField(this, _heightFields.Count, name, grid, initialHeight, data);
            _heightFields.Add(field);
            return field;
        }
    }

    /// <summary>Creates an independent edit branch sharing the current immutable page versions.</summary>
    public Terrain Duplicate()
    {
        lock (Gate)
        {
            var terrain = new Terrain(Editing) { _revision = _revision };
            foreach (HeightField field in _heightFields)
                terrain._heightFields.Add(new HeightField(terrain, field.Id, field.Name,
                    field.Grid, field.InitialHeight, field.Data));
            return terrain;
        }
    }
}
