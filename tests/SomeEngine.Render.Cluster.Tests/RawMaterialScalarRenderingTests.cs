using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
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
