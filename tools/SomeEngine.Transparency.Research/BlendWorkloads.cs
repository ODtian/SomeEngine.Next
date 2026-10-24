namespace SomeEngine.Transparency.Research;

public sealed record BlendWorkload(string Name, BlendEffect Effect, int Count, float Distance, bool Spheres = false,
    BlendScene Scene = BlendScene.DepthBands)
{
    public BlendEffectData CreateData(int width, int height, float depthOffset = 0) =>
        new(Effect, Count, width, height, Spheres, depthOffset, Scene);
}

public static class BlendWorkloads
{
    public static IReadOnlyList<BlendWorkload> Scenes { get; } = new BlendWorkload[]
    {
        new("Xray assembly 64", BlendEffect.Xray, 64, 6, true, BlendScene.Assembly),
        new("Decal layers 256", BlendEffect.Decals, 256, 4, Scene: BlendScene.DecalLayers),
        new("Sparse decals 64", BlendEffect.Decals, 64, 4, Scene: BlendScene.SparseDecals),
    };
}
