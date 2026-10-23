namespace SomeEngine.Resources;

/// <summary>A submitted data change and its terrain-local geometric influence.</summary>
public abstract record TerrainChange(Terrain Terrain, ulong Revision)
{
    public abstract bool Affects(TerrainSurface surface, TerrainBounds bounds, float readDistance);
}

public sealed record TerrainSurfaceChange(Terrain Terrain, ulong Revision) : TerrainChange(Terrain, Revision)
{
    public override bool Affects(TerrainSurface surface, TerrainBounds bounds, float readDistance) => true;
}

public sealed record HeightFieldChange(HeightField Field, HeightRegion Range, int ReadBorder, TerrainEdit Edit)
    : TerrainChange(Field.Terrain, Edit.Revision)
{
    public override bool Affects(TerrainSurface surface, TerrainBounds bounds, float readDistance)
        => surface.Uses(Field) && Affects(bounds, readDistance);

    public bool Affects(TerrainBounds bounds, float readDistance)
    {
        float spacing = Field.Grid.SampleSpacing;
        // Bilinear interpolation spreads each changed height through its adjacent cells.
        float border = (ReadBorder + 1) * spacing + readDistance;
        return Range.MinX * spacing - border <= bounds.Max.X
            && (Range.MaxX - 1) * spacing + border >= bounds.Min.X
            && Range.MinZ * spacing - border <= bounds.Max.Z
            && (Range.MaxZ - 1) * spacing + border >= bounds.Min.Z;
    }
}
