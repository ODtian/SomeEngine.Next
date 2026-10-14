using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Instances;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class RawMaterialScalarRenderingTests
{
    [Theory]
    [InlineData("D3D12")]
    [InlineData("Vulkan")]
    public async Task MixedScalarOrdersPreserveIndependentMaterialValuesAndLocalEdits(string backend)
    {
        var firstShader = await LoadShaderAsync(backend, reverse: false);
        var secondShader = await LoadShaderAsync(backend, reverse: true);
        using var first = new ScalarProbeMaterial
        {
            Shader = firstShader, Name = "First raw scalar region",
            Leading = .5f, Triple = new(.25f, .5f, .75f), Trailing = .25f, Pair = new(.125f, .375f),
            Tint = new(.5f, .25f, .75f, .875f), Signed = -2, Unsigned = 3, Enabled = true,
        };
        using var second = new ScalarProbeMaterial
        {
            Shader = secondShader, Name = "Second reordered raw scalar region",
            Leading = .125f, Triple = new(.75f, .25f, .5f), Trailing = .75f, Pair = new(.5f, .25f),
            Tint = new(.25f, .75f, .5f, .625f), Signed = 2, Unsigned = 1, Enabled = false,
        };
        var plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var scene = await FrameStageScene.CreateAsync(backend, false,
            new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true,
                EnableFrameMetricsReadback = true }, plane);
        scene.SetMeshTransform(0, new(new Vector3(-.8f, 0, -3), Quaternion.Identity)
            { Stretch = new(.6f, .6f, 1) });
        scene.SetMeshTransform(1, new(new Vector3(.8f, 0, -3), Quaternion.Identity)
            { Stretch = new(.6f, .6f, 1) });
        scene.UseMaterial(0, first);
        scene.UseMaterial(1, second);
        RasterImage before = await scene.ReadRasterImageAsync();
        AssertPixel(before, 16, 32, Expected(first));
        AssertPixel(before, 48, 32, Expected(second));

        first.Triple = new(.75f, .125f, .25f);
        first.Pair = new(.375f, .5f);
        first.Signed = 3;
        first.Unsigned = 2;
        first.Enabled = false;
        RasterImage changed = await scene.ReadNextRasterImageAsync();
        AssertPixel(changed, 16, 32, Expected(first));
        AssertPixel(changed, 48, 32, Expected(second));
        Assert.False(before.Color.AsSpan((32 * 64 + 16) * 8, 8)
            .SequenceEqual(changed.Color.AsSpan((32 * 64 + 16) * 8, 8)));
    }

    [Fact]
    public async Task ManyMaterialEditsSurviveDiscardedFrameWithoutNewEdits()
    {
        const int materialCount = 131;
        Shader shader = await LoadShaderAsync("D3D12", reverse: false);
        var materials = Enumerable.Range(0, materialCount).Select(index => new ScalarProbeMaterial
        {
            Shader = shader, Name = $"Scalar region {index:D3}",
            Leading = (index % 8) / 16f, Triple = new(.25f, .5f, .75f), Trailing = .25f,
            Pair = new(.125f, .375f), Tint = new(.5f, .25f, .75f, (index + 1) / 256f),
            Signed = 1, Unsigned = 2, Enabled = true,
        }).ToArray();
        try
        {
            Mesh plane = await RasterTestGeometry.CreatePlaneAsync();
            await using var scene = await FrameStageScene.CreateAsync("D3D12", false,
                new ClusterPipelineOptions { MaxCandidates = 1024, ForceHardwareRaster = true,
                    EnableFrameMetricsReadback = true }, plane,
                instanceOptions: new RenderInstanceOptions { RowCapacity = 256, BatchCapacity = 8 },
                imageSize: 128);
            var completed = new MaterialFrameReadbacks();
            scene.ObserveCompletedFrames(completed);
            for (int index = 0; index < materialCount; index++)
            {
                (int x, int y) = MaterialSample(index);
                float half = 3f * MathF.Tan(.5f);
                var transform = new TransformQvvs(new Vector3(
                    ((x + .5f) / 128f * 2 - 1) * half,
                    (1 - (y + .5f) / 128f * 2) * half, -3), Quaternion.Identity)
                    { Stretch = new Vector3(6f / 128f * half, 6f / 128f * half, 1) };
                if (index < 2)
                {
                    scene.SetMeshTransform(index, transform);
                    scene.UseMaterial(index, materials[index]);
                }
                else
                {
                    var entity = scene.AddReceiver(transform);
                    scene.UseEntityMaterial(entity, materials[index]);
                }
            }

            RasterImage? initial = null;
            for (int frame = 0; frame < 4; frame++) initial = await scene.ReadNextRasterImageAsync();
            AssertMaterialSamples(initial!, materials, "initial");
            Assert.Equal(materialCount, materials.Select(material =>
                BitConverter.HalfToUInt16Bits((Half)material.Tint.W)).Distinct().Count());
            ulong[] revisions = materials.Select(material => material.Revision).ToArray();

            for (int index = 0; index < materialCount; index++)
                materials[index].Leading = .5f + (index % 8) / 16f;
            revisions = AssertMaterialRevisions(materials, revisions, materialCount);
            RasterImage allChanged = await scene.ReadNextRasterImageAsync();
            AssertMaterialSamples(allChanged, materials, "all-edited");
            AssertMaterialChanges(initial!, allChanged, Enumerable.Range(0, materialCount), "all-edited");

            revisions = AssertMaterialRevisions(materials, revisions, 0);
            RasterImage unchanged = await scene.ReadNextRasterImageAsync();
            AssertMaterialSamples(unchanged, materials, "no-edits");
            AssertMaterialImageEqual(allChanged, unchanged, "no-edits");

            materials[65].Leading = 1f + (65 % 8) / 16f;
            revisions = AssertMaterialRevisions(materials, revisions, 1);
            RasterImage oneChanged = await scene.ReadNextRasterImageAsync();
            AssertMaterialSamples(oneChanged, materials, "one-edited");
            AssertMaterialChanges(unchanged, oneChanged, [65], "one-edited");

            for (int index = 0; index < materialCount; index++)
                materials[index].Leading = 1.5f + (index % 8) / 16f;
            revisions = AssertMaterialRevisions(materials, revisions, materialCount);
            Assert.Equal(7, completed.Frames.Count);
            var discarded = new DiscardedMaterialFrame();
            ulong discardedIdentity = await scene.DiscardNextFrameAsync(discarded);
            Assert.Equal(1, discarded.FinishedFrames);
            Assert.Equal(0, discarded.SubmissionNotifications);
            Assert.Empty(discarded.SubmittedCompletions);
            scene.CollectCompletedFrames();
            Assert.Equal(7, completed.Frames.Count);
            AssertMaterialImageEqual(oneChanged, scene.ReadCurrentRasterImage(), "discarded");

            // The first submitted frame after Dispose must carry every edit without another setter call.
            revisions = AssertMaterialRevisions(materials, revisions, 0);
            RasterImage recovered = await scene.ReadNextRasterImageAsync();
            AssertMaterialSamples(recovered, materials, "recovered");
            AssertMaterialChanges(oneChanged, recovered, Enumerable.Range(0, materialCount), "recovered");
            Assert.Equal(8, completed.Frames.Count);
            Assert.True(completed.Frames[^1].Identity > discardedIdentity);

            AssertMaterialRevisions(materials, revisions, 0);
            RasterImage confirmed = await scene.ReadNextRasterImageAsync();
            AssertMaterialSamples(confirmed, materials, "recovery-no-edits");
            AssertMaterialImageEqual(recovered, confirmed, "recovery-no-edits");
            Assert.Equal(9, completed.Frames.Count);
            Assert.Equal(9, completed.Frames.Select(frame => frame.Identity).Distinct().Count());
            Assert.DoesNotContain(completed.Frames, frame => frame.Identity == discardedIdentity);
            Assert.All(completed.Frames, frame => Assert.Equal(new byte[4], frame.Bytes));
        }
        finally
        {
            foreach (Material material in materials) material.Dispose();
        }
    }

    private static ulong[] AssertMaterialRevisions(ScalarProbeMaterial[] materials, ulong[] before, int changed)
    {
        ulong[] after = materials.Select(material => material.Revision).ToArray();
        Assert.Equal(changed, after.Where((revision, index) => revision != before[index]).Count());
        return after;
    }

    private static void AssertMaterialSamples(RasterImage image, ScalarProbeMaterial[] materials, string stage)
    {
        for (int index = 0; index < materials.Length; index++)
        {
            Vector4 expected = Expected(materials[index]);
            Half[] channels = [(Half)expected.X, (Half)expected.Y, (Half)expected.Z, (Half)expected.W];
            byte[] expectedColor = new byte[9 * 8];
            byte[] actualColor = new byte[9 * 8];
            float[] expectedCoverage = Enumerable.Repeat(1f, 9).ToArray();
            float[] actualCoverage = new float[9];
            (int x, int y) = MaterialSample(index);
            for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++)
            {
                int sample = row * 3 + column;
                int pixel = (y + row - 1) * 128 + x + column - 1;
                MemoryMarshal.AsBytes(channels.AsSpan()).CopyTo(expectedColor.AsSpan(sample * 8, 8));
                image.Color.AsSpan(pixel * 8, 8).CopyTo(actualColor.AsSpan(sample * 8, 8));
                float depth = MemoryMarshal.Cast<byte, float>(image.Depth.AsSpan(pixel * 4, 4))[0];
                actualCoverage[sample] = float.IsFinite(depth) && depth > 0 && depth < 1 ? 1 : 0;
            }
            ImageArtifacts.AssertEqual(new(3, 3, Format.R16G16B16A16Float, expectedColor),
                new(3, 3, Format.R16G16B16A16Float, actualColor), $"material-{stage}-{index:D3}-color");
            ImageArtifacts.AssertEqual(new(3, 3, Format.R32Float, MemoryMarshal.AsBytes(expectedCoverage.AsSpan()).ToArray()),
                new(3, 3, Format.R32Float, MemoryMarshal.AsBytes(actualCoverage.AsSpan()).ToArray()),
                $"material-{stage}-{index:D3}-coverage");
        }
    }

    private static void AssertMaterialChanges(RasterImage before, RasterImage after,
        IEnumerable<int> changedMaterials, string stage)
    {
        float[] expected = new float[131];
        float[] actual = new float[131];
        foreach (int index in changedMaterials) expected[index] = 1;
        for (int index = 0; index < actual.Length; index++)
        {
            (int x, int y) = MaterialSample(index);
            int offset = (y * 128 + x) * 8;
            actual[index] = before.Color.AsSpan(offset, 8).SequenceEqual(after.Color.AsSpan(offset, 8)) ? 0 : 1;
        }
        ImageArtifacts.AssertEqual(new(131, 1, Format.R32Float, MemoryMarshal.AsBytes(expected.AsSpan()).ToArray()),
            new(131, 1, Format.R32Float, MemoryMarshal.AsBytes(actual.AsSpan()).ToArray()),
            $"material-{stage}-changes");
    }

    private static void AssertMaterialImageEqual(RasterImage expected, RasterImage actual, string stage)
    {
        ImageArtifacts.AssertEqual(new(128, 128, Format.R16G16B16A16Float, expected.Color),
            new(128, 128, Format.R16G16B16A16Float, actual.Color), $"material-{stage}-color");
        ImageArtifacts.AssertEqual(new(128, 128, Format.D32Float, expected.Depth),
            new(128, 128, Format.D32Float, actual.Depth), $"material-{stage}-depth");
    }

    private static (int X, int Y) MaterialSample(int index) => (8 + index % 12 * 10, 14 + index / 12 * 10);

    private sealed class MaterialFrameReadbacks : IGraphReadbackReceiver
    {
        internal List<(ulong Identity, byte[] Bytes)> Frames { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => Frames.Add((tag, bytes.ToArray()));
    }

    private sealed class DiscardedMaterialFrame : IRenderGraphFrameExecutionObserver
    {
        internal int FinishedFrames { get; private set; }
        internal int SubmissionNotifications { get; private set; }
        internal QueueCompletion[] SubmittedCompletions { get; private set; } = [];
        public void OnRenderGraphSubmitted(RenderGraphFrameResult result) => SubmissionNotifications++;
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            FinishedFrames++;
            SubmittedCompletions = result.SubmittedCompletions.ToArray();
        }
    }

    private static Vector4 Expected(ScalarProbeMaterial value)
    {
        Vector3 color = new Vector3(value.Tint.X, value.Tint.Y, value.Tint.Z) * .25f
            + value.Triple * .125f
            + new Vector3(value.Leading, value.Trailing, value.Pair.X) * .0625f
            + new Vector3(value.Pair.Y, value.Signed, value.Unsigned) * .03125f;
        if (value.Enabled) color += new Vector3(.125f, .0625f, .03125f);
        return new Vector4(color, value.Tint.W);
    }

    private static void AssertPixel(RasterImage image, int x, int y, Vector4 expected)
    {
        Half[] channels = [(Half)expected.X, (Half)expected.Y, (Half)expected.Z, (Half)expected.W];
        ImageArtifacts.AssertEqual(new(1, 1, Format.R16G16B16A16Float,
            MemoryMarshal.AsBytes(channels.AsSpan()).ToArray()),
            new(1, 1, Format.R16G16B16A16Float, image.Color.AsSpan((y * 64 + x) * 8, 8).ToArray()));
    }

    private static async Task<Shader> LoadShaderAsync(string backend, bool reverse)
    {
        string root = FrameStageScene.FindRoot();
        using var source = new ShaderSourceBlock { SourceNames = ["raw_material_scalars.slang"] };
        var description = TestShaderLoading.CreateSessionDescription(
            backend == "Vulkan" ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil);
        description.SearchPaths = [Path.Combine(root, "tests/SomeEngine.Render.Cluster.Tests/Shaders"),
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"), Path.Combine(root, "assets/Shaders")];
        description.PreprocessorMacros = [new PreprocessorMacroDesc("RAW_SCALAR_REVERSE", reverse ? "1" : "0")];
        return await source.CreateAsync(TestShaderLoading.Slang, description, "Mixed raw material scalar order");
    }

    private sealed class ScalarProbeMaterial : Material
    {
        private float _leading, _trailing;
        private Vector3 _triple;
        private Vector2 _pair;
        private Vector4 _tint;
        private int _signed;
        private uint _unsigned;
        private bool _enabled;
        private static readonly MaterialPropertyDefinition[] Definitions =
        [
            new(0, nameof(Leading), typeof(float)), new(1, nameof(Triple), typeof(Vector3)),
            new(2, nameof(Trailing), typeof(float)), new(3, nameof(Pair), typeof(Vector2)),
            new(4, nameof(Tint), typeof(Vector4)), new(5, nameof(Signed), typeof(int)),
            new(6, nameof(Unsigned), typeof(uint)), new(7, nameof(Enabled), typeof(bool)),
        ];
        public override Guid MaterialTypeId => new("60f47bb9-6308-485d-b6a8-df459ee9e5db");
        public override IReadOnlyList<MaterialPropertyDefinition> Properties => Definitions;
        public float Leading { get => _leading; set => SetProperty(ref _leading, value, 0); }
        public Vector3 Triple { get => _triple; set => SetProperty(ref _triple, value, 1); }
        public float Trailing { get => _trailing; set => SetProperty(ref _trailing, value, 2); }
        public Vector2 Pair { get => _pair; set => SetProperty(ref _pair, value, 3); }
        public Vector4 Tint { get => _tint; set => SetProperty(ref _tint, value, 4); }
        public int Signed { get => _signed; set => SetProperty(ref _signed, value, 5); }
        public uint Unsigned { get => _unsigned; set => SetProperty(ref _unsigned, value, 6); }
        public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value, 7); }
        public override Vector4 GetFloatComponents(uint index) => index switch
        {
            0 => new(Leading, 0, 0, 0), 1 => new(Triple, 0), 2 => new(Trailing, 0, 0, 0),
            3 => new(Pair, 0, 0), 4 => Tint, _ => default,
        };
        public override uint GetInteger(uint index) => index switch
        { 5 => unchecked((uint)Signed), 6 => Unsigned, 7 => Enabled ? 1u : 0u, _ => 0u };
        public override void ApplyParameter(MaterialValue parameter, Texture? texture)
            => throw new NotSupportedException("This fixture uses public typed material properties.");
        protected override Material CreateEmpty() => new ScalarProbeMaterial();
        protected override void CopyProperties(Material source)
        {
            var other = (ScalarProbeMaterial)source;
            Leading = other.Leading; Triple = other.Triple; Trailing = other.Trailing; Pair = other.Pair;
            Tint = other.Tint; Signed = other.Signed; Unsigned = other.Unsigned; Enabled = other.Enabled;
        }
    }
}
