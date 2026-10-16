using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using GeometryValueType = SomeEngine.Resources.Geometry.ValueType;

namespace Game.Build;

internal static class DefaultBlendContent
{
    internal static async Task BuildAsync(AssetProject project, CancellationToken cancellationToken)
    {
        using var panel = ClusterBuilder.ProcessRaw(
            [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0)],
            [
                new("NORMAL", new float[] { 0, 0, -1, 0, 0, -1, 0, 0, -1, 0, 0, -1 }, 3, GeometryValueType.Int8, 3, true),
                new("TANGENT", new float[] { 1, 0, 0, -1, 1, 0, 0, -1, 1, 0, 0, -1, 1, 0, 0, -1 }, 4, GeometryValueType.Int8, 4, true),
                new("TEXCOORD_0", new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, 2, GeometryValueType.Float16, 2, false),
            ], [0, 2, 1, 0, 3, 2], [new(DefaultContentIds.StandardMaterial)], "Blend panel");
        await SaveMeshAsync(panel, new("b6ed17c9-e818-4d3f-a352-e127532c8e5c"), "blend-panel");

        const int latitudeCount = 7, longitudeCount = 14;
        var positions = new Vector3[(latitudeCount + 1) * (longitudeCount + 1)];
        var normals = new float[positions.Length * 3];
        var tangents = new float[positions.Length * 4];
        var uv = new float[positions.Length * 2];
        for (int latitude = 0; latitude <= latitudeCount; latitude++)
        for (int longitude = 0; longitude <= longitudeCount; longitude++)
        {
            int vertex = latitude * (longitudeCount + 1) + longitude;
            float polar = latitude * MathF.PI / latitudeCount;
            float angle = longitude * 2 * MathF.PI / longitudeCount;
            Vector3 position = new(MathF.Sin(polar) * MathF.Cos(angle), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(angle));
            positions[vertex] = position;
            normals[vertex * 3] = position.X;
            normals[vertex * 3 + 1] = position.Y;
            normals[vertex * 3 + 2] = position.Z;
            tangents[vertex * 4] = -MathF.Sin(angle);
            tangents[vertex * 4 + 2] = MathF.Cos(angle);
            tangents[vertex * 4 + 3] = 1;
            uv[vertex * 2] = (float)longitude / longitudeCount;
            uv[vertex * 2 + 1] = (float)latitude / latitudeCount;
        }
        var indices = new List<uint>();
        for (uint latitude = 0; latitude < latitudeCount; latitude++)
        for (uint longitude = 0; longitude < longitudeCount; longitude++)
        {
            uint first = latitude * (longitudeCount + 1) + longitude, second = first + longitudeCount + 1;
            if (latitude != 0) indices.AddRange([first, first + 1, second]);
            if (latitude != latitudeCount - 1) indices.AddRange([first + 1, second + 1, second]);
        }
        using var part = ClusterBuilder.ProcessRaw(positions,
            [new("NORMAL", normals, 3, GeometryValueType.Int8, 3, true),
             new("TANGENT", tangents, 4, GeometryValueType.Int8, 4, true),
             new("TEXCOORD_0", uv, 2, GeometryValueType.Float16, 2, false)],
            indices.ToArray(), [new(DefaultContentIds.StandardMaterial)], "Blend part");
        await SaveMeshAsync(part, new("ca223a36-4a3e-4d58-a279-53d27b589459"), "blend-part");
        await SaveTextureAsync(new("89397749-1f68-4d23-b624-9a82b0d788d3"), "Blend smoke", (x, y) =>
        {
            float radius = x * x + y * y;
            return new(1, 1, 1, MathF.Exp(-3 * radius) * MathF.Max(0, 1 - radius));
        });
        await SaveTextureAsync(new("a0876f89-922b-40cb-af18-f909b4b6f257"), "Blend leaf", (x, y) =>
            new(.35f + .15f * y, .8f, .2f,
                MathF.Abs(x) < .65f * MathF.Sin((y + 1) * MathF.PI * .5f) ? 1 : 0));

        await SaveTextureAsync(DefaultContentIds.BlendCheckerTexture, "Blend checker", (x, y) =>
        {
            float value = (((int)MathF.Floor((x + 1) * 4) + (int)MathF.Floor((y + 1) * 4)) & 1) == 0 ? .3f : 1;
            return new(value, value, value, 1);
        });

        async Task SaveMeshAsync(MeshAsset source, Guid id, string name)
        {
            using MeshAsset mesh = source.CreateCopy(id);
            project.RegisterAsset(await mesh.SaveAsync(Path.Combine(project.RootPath, $"assets/Meshes/{name}.mesh.asset"), cancellationToken));
        }

        async Task SaveTextureAsync(Guid id, string name, Func<float, float, Vector4> sample)
        {
            const uint size = 32;
            byte[] rgba = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector4 value = sample((x + .5f) * 2 / size - 1, (y + .5f) * 2 / size - 1);
                int pixel = (y * (int)size + x) * 4;
                rgba[pixel] = (byte)(Math.Clamp(value.X, 0, 1) * 255);
                rgba[pixel + 1] = (byte)(Math.Clamp(value.Y, 0, 1) * 255);
                rgba[pixel + 2] = (byte)(Math.Clamp(value.Z, 0, 1) * 255);
                rgba[pixel + 3] = (byte)(Math.Clamp(value.W, 0, 1) * 255);
            }
            using var pixels = new TextureBlock
            {
                Dimension = TextureDimension.Texture2D, Width = size, Height = size, Depth = 1,
                MipLevelCount = 1, ArrayLayerCount = 1, Format = Format.R8G8B8A8UNorm,
                SampledFormat = Format.R8G8B8A8UNorm, SampledDimension = TextureViewDimension.Texture2D,
            };
            ulong key = TextureTiles.ChunkKey(0, 0, 0);
            pixels.MipTiles.Add(new TextureMipTile { Width = size, Height = size, RowPitch = size * 4,
                SlicePitch = size * size * 4, ChunkKey = key, DecodedLength = (ulong)rgba.Length });
            pixels.SetBytes(key, rgba);
            using var texture = new TextureAsset(id) { Name = name };
            texture.SetBlock(pixels);
            project.RegisterAsset(await texture.SaveAsync(Path.Combine(project.RootPath, $"assets/Textures/{name.Replace(' ', '-')}.texture.asset"), cancellationToken));
        }
    }
}
