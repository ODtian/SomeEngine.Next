namespace SomeEngine.Resources;

public sealed record VoxelFieldChange(VoxelField Field, VoxelRegion Range, int ReadBorder, TerrainEdit Edit)
    : TerrainChange(Field.Terrain, Edit.Revision)
{
    public override bool Affects(TerrainSurface surface, TerrainBounds bounds, float readDistance)
    {
        float spacing = Field.Grid.SampleSpacing;
        float border = ReadBorder * spacing + readDistance;
        return surface.Uses(Field)
            && Range.MinX * spacing - border <= bounds.Max.X
            && (Range.MaxX - 1) * spacing + border >= bounds.Min.X
            && Range.MinY * spacing - border <= bounds.Max.Y
            && (Range.MaxY - 1) * spacing + border >= bounds.Min.Y
            && Range.MinZ * spacing - border <= bounds.Max.Z
            && (Range.MaxZ - 1) * spacing + border >= bounds.Min.Z;
    }
}
