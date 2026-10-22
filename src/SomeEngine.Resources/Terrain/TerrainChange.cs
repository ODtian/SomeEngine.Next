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
    {
        float spacing = Field.Grid.SampleSpacing;
        float border = ReadBorder * spacing + readDistance;
        return surface.Uses(Field)
            && Range.MinX * spacing - border <= bounds.Max.X
            && (Range.MaxX - 1) * spacing + border >= bounds.Min.X
            && Range.MinZ * spacing - border <= bounds.Max.Z
            && (Range.MaxZ - 1) * spacing + border >= bounds.Min.Z;
    }
}
