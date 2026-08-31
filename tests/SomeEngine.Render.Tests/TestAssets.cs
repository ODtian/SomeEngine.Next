using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Tests;

internal static class TestAssets
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(int Id, int Revision), Mesh> Meshes = [];
    private static readonly Dictionary<(int Id, int Revision), Material> Materials = [];
    private static readonly Dictionary<(int Id, int Revision), Texture> Textures = [];

    internal static Mesh Mesh(int id, int revision = 1)
    {
        lock (Gate)
        {
            if (!Meshes.TryGetValue((id, revision), out Mesh? asset))
            {
                asset = new Mesh { Name = $"mesh-{id}-r{revision}" };
                Meshes.Add((id, revision), asset);
            }
            return asset;
        }
    }

    internal static Material Material(int id, int revision = 1)
    {
        lock (Gate)
        {
            if (!Materials.TryGetValue((id, revision), out Material? asset))
            {
                asset = new Material { Name = $"material-{id}-r{revision}" };
                Materials.Add((id, revision), asset);
            }
            return asset;
        }
    }

    internal static Texture Texture(int id, int revision = 1)
    {
        lock (Gate)
        {
            if (!Textures.TryGetValue((id, revision), out Texture? asset))
            {
                asset = new Texture { Name = $"texture-{id}-r{revision}" };
                Textures.Add((id, revision), asset);
            }
            return asset;
        }
    }
}
