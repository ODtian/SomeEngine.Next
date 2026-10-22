namespace SomeEngine.Resources;

/// <summary>Editable terrain fields and their shared revision sequence.</summary>
public sealed partial class Terrain
{
    private readonly List<HeightField> _heightFields = [];
    private readonly List<VoxelField> _voxelFields = [];
    private ulong _revision;

    internal object Gate { get; } = new();
    internal TerrainEditing Editing { get; }
    internal ulong NextRevision() => ++_revision;

    public Terrain(TerrainEditing editing)
    {
        Editing = editing;
        HeightFields = _heightFields.AsReadOnly();
        VoxelFields = _voxelFields.AsReadOnly();
    }

    /// <summary>Latest submitted terrain revision. Field reads wait for its data.</summary>
    public ulong Revision { get { lock (Gate) return _revision; } }
    public IReadOnlyList<HeightField> HeightFields { get; }
    public IReadOnlyList<VoxelField> VoxelFields { get; }

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

    public VoxelField CreateVoxelField(string name, VoxelGrid grid, float initialValue = 1f)
    {
        lock (Gate)
        {
            var data = new VoxelFieldData([], new TerrainEdit(NextRevision(), default));
            var field = new VoxelField(this, _voxelFields.Count, name, grid, initialValue, data);
            _voxelFields.Add(field);
            return field;
        }
    }

    /// <summary>Creates an independent edit branch sharing the current immutable page versions.</summary>
    public Terrain Duplicate()
    {
        lock (Gate)
        {
            var terrain = new Terrain(Editing) { _revision = _revision, _requestSequence = _requestSequence };
            terrain._requests.AddRange(_requests.Where(static request => !request.IsSubmitted));
            foreach (HeightField field in _heightFields)
                terrain._heightFields.Add(new HeightField(terrain, field.Id, field.Name,
                    field.Grid, field.InitialHeight, field.Data));
            foreach (VoxelField field in _voxelFields)
                terrain._voxelFields.Add(new VoxelField(terrain, field.Id, field.Name,
                    field.Grid, field.InitialValue, field.Data));
            return terrain;
        }
    }
}
