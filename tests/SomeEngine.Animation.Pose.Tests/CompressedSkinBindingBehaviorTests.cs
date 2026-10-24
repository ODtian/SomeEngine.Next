using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;
using SomeEngine.Animation.Pose.Importers;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Importing;
using SomeEngine.Assets.Schema;

namespace SomeEngine.Animation.Pose.Tests;

public sealed class CompressedSkinBindingBehaviorTests
{
    [Theory]
    [InlineData(false, "ordinary")]
    [InlineData(true, "ordinary")]
    [InlineData(false, "zeros")]
    [InlineData(true, "zeros")]
    [InlineData(false, "identity")]
    [InlineData(true, "identity")]
    public async Task SourceMatricesSurviveCompressionFallbackAndSaveOpen(bool binary, string fallback)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "compressed-binding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Matrix4x4 expected = new(2,0,0,0, 0,3,0,0, 0,0,4,0, 5,-6,7,1);
            float[] values = [2,0,0,0, 0,3,0,0, 0,0,4,0, 5,-6,7,1];
            byte[] ordinary = System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
            byte[] payload = fallback == "ordinary" ? ordinary : Convert.FromBase64String(
                "oaqqqqqqqqqqqqqqqqqqqqoAAABAAAAAAAAAAAAAAAAAAAAAAAAAQEAAAAAAAAAAAAAAAAAAAAAAAACAQAAAAAAAAKBAAADAwAAA4EAAAIA/AAAAAAAAAAAAAAAAAAAAAA==");
            JsonObject view = new() { ["buffer"] = fallback == "ordinary" ? 0 : 1, ["byteLength"] = 64, ["byteStride"] = 64 };
            JsonArray buffers = [new JsonObject { ["byteLength"] = payload.Length,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(payload) }];
            if (fallback != "ordinary")
            {
                var extra = new JsonObject { ["byteLength"] = 64 };
                if (fallback == "identity") extra["uri"] = "data:application/octet-stream;base64,AACAPwAAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAAAAAAAAAAAACAPw==";
                buffers.Add(extra);
                view["extensions"] = new JsonObject { ["EXT_meshopt_compression"] = new JsonObject
                { ["buffer"] = 0, ["byteLength"] = payload.Length, ["count"] = 1, ["byteStride"] = 64, ["mode"] = "ATTRIBUTES", ["filter"] = "NONE" } };
            }
            JsonObject document = new()
            {
                ["asset"] = new JsonObject { ["version"] = "2.0" }, ["buffers"] = buffers,
                ["bufferViews"] = new JsonArray(view),
                ["accessors"] = new JsonArray(new JsonObject { ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = 1, ["type"] = "MAT4" }),
                ["nodes"] = new JsonArray(new JsonObject { ["name"] = "joint" }),
                ["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(0), ["inverseBindMatrices"] = 0 }),
            };
            if (fallback != "ordinary")
            {
                document["extensionsUsed"] = new JsonArray("EXT_meshopt_compression");
                document["extensionsRequired"] = new JsonArray("EXT_meshopt_compression");
            }
            string path = Path.Combine(directory, binary ? "binding.glb" : "binding.gltf");
            if (binary)
            {
                buffers[0]!.AsObject().Remove("uri");
                byte[] json = Encoding.UTF8.GetBytes(document.ToJsonString());
                int jsonSize = (json.Length + 3) & ~3, payloadSize = (payload.Length + 3) & ~3;
                using var writer = new BinaryWriter(File.Create(path));
                writer.Write(0x46546C67u); writer.Write(2u); writer.Write((uint)(28 + jsonSize + payloadSize));
                writer.Write(jsonSize); writer.Write(0x4E4F534Au); writer.Write(json);
                for (int index = json.Length; index < jsonSize; index++) writer.Write((byte)' ');
                writer.Write(payloadSize); writer.Write(0x004E4942u); writer.Write(payload);
                for (int index = payload.Length; index < payloadSize; index++) writer.Write((byte)0);
            }
            else File.WriteAllText(path, document.ToJsonString());
            ModelRoot source = await new GltfSourceImporter(new Microsoft.Extensions.DependencyInjection.ServiceCollection(), "compressed-bindings").ReadAsync(path, default);
            var decoded = new GltfAccessorReader(source).ReadComponents(source.LogicalAccessors[0]);
            Assert.Equal(values.Select(value => (double)value), decoded.Values.ToArray());
            await using var context = new ImportContext();
            context.EnableImporter<Skin, SkeletonAsset>(Guid.NewGuid(), [SkeletonBlock.BlockTypeId],
                (skin, imports, output, token) => new GltfSkeletonImporter().ImportAsync(skin, new(), imports, output, token));
            context.EnableImporter<Skin, SkeletonAsset>(Guid.NewGuid(), [SkinBindingBlock.BlockTypeId],
                (skin, imports, output, token) => new SkinBindingImporter().ImportAsync(skin, new(), imports, output, token));
            Skin input = source.LogicalSkins[0];
            string key = "skin/" + input.LogicalIndex;
            Guid id = context.GetAssetId(key);
            context.AddAsset(input, new SkeletonAsset(id), key, id + ".skeleton.asset");
            SkeletonAsset asset = await context.GetAssetAsync<Skin, SkeletonAsset>(input);
            SkinBindingBlock binding = asset.GetBlock<SkinBindingBlock>();
            Assert.Equal(expected, Assert.Single(binding.InverseBindMatrices.ToArray()));
            Assert.Equal(new short[] {0}, binding.JointBones.ToArray());
            string outputPath = Path.Combine(directory, "saved.skeleton.asset");
            await asset.SaveAsync(outputPath);
            using var reopened = await SkeletonAsset.OpenAsync(outputPath);
            SkinBindingBlock persisted = await reopened.GetBlockAsync<SkinBindingBlock>();
            Assert.Equal(expected, Assert.Single(persisted.InverseBindMatrices.ToArray()));
            Assert.Equal(binding.JointBones.ToArray(), persisted.JointBones.ToArray());
        }
        finally { Directory.Delete(directory, true); }
    }
}
