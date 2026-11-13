namespace SomeEngine.Render;

[Flags]
public enum RenderGeometryWorkFlags : uint
{
    None = 0,
    Hierarchy = 1,
    Candidates = 2,
    Deferred = 4,
    Visible = 8,
    RasterBatches = 16,
    Tessellation = 32,
}

