using System.Numerics;
using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;
using SomeEngine.Assets.Schema;
using SomeEngine.Assets.Importers;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class GltfVertexInputTests
{
    [Theory]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public async Task ImportConvertsSourceLayoutsBeforeCallingTheMaterialEncoder(int topology, bool binary)
    {
        string directory = Path.Combine(Path.GetTempPath(), "someengine-gltf-input-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] data = new byte[256];
            for (int primitive = 0; primitive < 2; primitive++)
            for (int vertex = 0; vertex < 3; vertex++)
            {
                int start = primitive * 120 + vertex * 40;
                PutFloat(data, start + 4, primitive * 2 + (vertex == 1 ? 1 : 0));
                PutFloat(data, start + 8, vertex == 2 ? 1 : 0);
                PutFloat(data, start + 24, 1); // Normal Z; other components are zero.
                BitConverter.TryWriteBytes(data.AsSpan(start + 28), (ushort)(vertex == 1 ? 65535 : 0));
                BitConverter.TryWriteBytes(data.AsSpan(start + 30), (ushort)(vertex == 2 ? 65535 : 0));
            }
            data[240] = 2; // Sparse UV1 only changes vertex two.
            PutFloat(data, 244, .25f);
            PutFloat(data, 248, .75f);
            File.WriteAllBytes(Path.Combine(directory, "vertices.bin"), data);
            var accessors = new List<object>();
            for (int primitive = 0; primitive < 2; primitive++)
            {
                accessors.Add(new { bufferView = primitive, byteOffset = 4, componentType = 5126, count = 3, type = "VEC3", min = new[] { primitive * 2, 0, 0 }, max = new[] { primitive * 2 + 1, 1, 0 } });
                accessors.Add(new { bufferView = primitive, byteOffset = 16, componentType = 5126, count = 3, type = "VEC3" });
                accessors.Add(new { bufferView = primitive, byteOffset = 28, componentType = 5123, normalized = true, count = 3, type = "VEC2" });
            }
            accessors.Add(new { componentType = 5126, count = 3, type = "VEC2", sparse = new
            {
                count = 1, indices = new { bufferView = 2, componentType = 5121 }, values = new { bufferView = 3 },
            } });
            string path = Path.Combine(directory, binary ? "mesh.glb" : "mesh.gltf");
            string json = JsonSerializer.Serialize(new
            {
                asset = new { version = "2.0" }, buffers = new[] { new { uri = "vertices.bin", byteLength = data.Length } },
                bufferViews = new object[]
                {
                    new { buffer = 0, byteOffset = 0, byteLength = 120, byteStride = 40, target = 34962 },
                    new { buffer = 0, byteOffset = 120, byteLength = 120, byteStride = 40, target = 34962 },
                    new { buffer = 0, byteOffset = 240, byteLength = 1 },
                    new { buffer = 0, byteOffset = 244, byteLength = 8 },
                },
                accessors,
                meshes = new[] { new { primitives = new object[]
                {
                    new { attributes = new { POSITION = 0, NORMAL = 1, TEXCOORD_0 = 2 }, mode = topology },
                    new { attributes = new { POSITION = 3, NORMAL = 4, TEXCOORD_0 = 5, TEXCOORD_1 = 6 }, mode = topology },
                } } },
                nodes = new[] { new { mesh = 0 } }, scenes = new[] { new { nodes = new[] { 0 } } }, scene = 0,
            });
            if (binary)
            {
                JsonNode document = JsonNode.Parse(json)!;
                document["buffers"]![0]!.AsObject().Remove("uri");
                byte[] text = Encoding.UTF8.GetBytes(document.ToJsonString());
                int paddedLength = (text.Length + 3) & ~3;
                using var writer = new BinaryWriter(File.Create(path));
                writer.Write(0x46546C67u);
                writer.Write(2u);
                writer.Write(checked((uint)(28 + paddedLength + data.Length)));
                writer.Write(paddedLength);
                writer.Write(0x4E4F534Au);
                writer.Write(text);
                for (int padding = text.Length; padding < paddedLength; padding++) writer.Write((byte)' ');
                writer.Write(data.Length);
                writer.Write(0x004E4942u);
                writer.Write(data);
            }
            else File.WriteAllText(path, json);
            ObservingEncoder[] encoders = [new(), new()];
            Task[] imports = encoders.Select(encoder => Task.Run(() =>
            {
                ModelRoot model = ModelRoot.Load(path);
                using MeshAsset asset = ClusterBuilder.ProcessMesh(model.LogicalMeshes[0], [], "Mixed source layouts",
                    new ClusterBuilderOptions { VertexEncoder = encoder });
                Assert.Equal(encoder.Compatibility, asset.GetBlock<ClusterMeshBlock>().VertexEncoding);
            })).ToArray();
            await Task.WhenAll(imports).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.All(encoders, encoder =>
            {
                Assert.True(encoder.SawNormalizedUv);
                Assert.True(encoder.SawSparseUv);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void PutFloat(byte[] bytes, int offset, float value) => BitConverter.TryWriteBytes(bytes.AsSpan(offset), value);

    // Observe the public import extension point, not the private packed format.
    private sealed class ObservingEncoder : IClusterVertexEncoder
    {
        public string Compatibility => StandardClusterVertexEncoder.EncodingCompatibility;
        private int _normalizedUv;
        private int _sparseUv;
        public bool SawNormalizedUv => Volatile.Read(ref _normalizedUv) != 0;
        public bool SawSparseUv => Volatile.Read(ref _sparseUv) != 0;
        public EncodedClusterVertices Encode(ReadOnlySpan<Vector3> positions, IReadOnlyList<RawAttribute> attributes,
            ReadOnlySpan<uint> clusterVertices)
        {
            RawAttribute uv0 = attributes.Single(x => x.Name == "TEXCOORD_0");
            RawAttribute uv1 = attributes.Single(x => x.Name == "TEXCOORD_1");
            Assert.Equal(2, uv0.Dimension);
            Assert.Equal(2, uv1.Dimension);
            for (int index = 0; index < positions.Length; index++)
            {
                Vector3 position = positions[index];
                Assert.Equal(position.X is 1 or 3 ? 1f : 0f, uv0.Data[index * 2]);
                Assert.Equal(position.Y == 1 ? 1f : 0f, uv0.Data[index * 2 + 1]);
                bool sparseVertex = position.X >= 2 && position.Y == 1;
                Assert.Equal(sparseVertex ? .25f : 0f, uv1.Data[index * 2]);
                Assert.Equal(sparseVertex ? .75f : 0f, uv1.Data[index * 2 + 1]);
            }
            ReadOnlySpan<float> normalizedUv = uv0.Data.AsSpan(0, positions.Length * uv0.Dimension);
            ReadOnlySpan<float> sparseUv = uv1.Data.AsSpan(0, positions.Length * uv1.Dimension);
            if (normalizedUv.Contains(1f)) Interlocked.Exchange(ref _normalizedUv, 1);
            if (sparseUv.Contains(.25f) && sparseUv.Contains(.75f)) Interlocked.Exchange(ref _sparseUv, 1);
            return StandardClusterVertexEncoder.Instance.Encode(positions, attributes.Where(x => x.Name != "TEXCOORD_1").ToArray(), clusterVertices);
        }
    }
}
