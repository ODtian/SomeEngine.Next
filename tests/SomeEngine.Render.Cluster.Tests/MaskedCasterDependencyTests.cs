using System.Numerics;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Math;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Shadows;
using SomeEngine.Resources;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Render.Cluster.Tests;

[Trait("Category", "Hardware")]
public sealed class MaskedCasterDependencyTests : IClassFixture<MaskedCasterDependencyTests.MaskedShaders>
{
    private readonly MaskedShaders _shaders;

    public MaskedCasterDependencyTests(MaskedShaders shaders) => _shaders = shaders;

    [Fact]
    public async Task ShadingOnlyRoughnessChangesKeepMaskedShadowsEqualWhileTheCameraReturns()
    {
        Shader declaredShader = _shaders.Mask;
        Shader conservativeShader = _shaders.Conservative;
        Mesh plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var declared = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        await using var conservative = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        await using var fixedRoughness = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        using Texture alpha = AlphaPattern(0);
        StandardMaterial[] materials =
        [
            declared.StandardForCasterChecks,
            conservative.StandardForCasterChecks,
            fixedRoughness.StandardForCasterChecks,
        ];
        FrameStageScene[] scenes = [declared, conservative, fixedRoughness];
        for (int index = 0; index < scenes.Length; index++)
        {
            Configure(scenes[index]);
            materials[index].Shader = index == 1 ? conservativeShader : declaredShader;
            materials[index].AlbedoMap = alpha;
            materials[index].Roughness = .3f;
            materials[index].MetallicFactor = .7f;
        }

        // Real transform edits enter Auto's dynamic layer before its completed-frame aging.
        foreach (FrameStageScene scene in scenes)
        {
            scene.SetView(Camera(0));
            await scene.ReadNextRasterImageAsync();
            scene.SetMeshTransform(0, Receiver(.2f));
            scene.SetMeshTransform(1, Caster(.2f));
            await scene.ReadNextRasterImageAsync();
            scene.SetMeshTransform(0, Receiver(0));
            scene.SetMeshTransform(1, Caster(0));
            await scene.ReadNextRasterImageAsync();
        }
        for (int frame = 0; frame < 104; frame++)
        {
            foreach (FrameStageScene scene in scenes)
            {
                scene.SetView(Camera(frame));
                await scene.ReadNextRasterImageAsync();
            }
        }

        ulong declaredRevision = declared.CasterRevisionForChecks();
        ulong conservativeRevision = conservative.CasterRevisionForChecks();
        int shadingChanges = 0;
        for (int frame = 0; frame < 64; frame++)
        {
            float roughness = .5f + .2f * MathF.Sin((frame + 1) * .37f);
            materials[0].Roughness = materials[1].Roughness = roughness;
            foreach (FrameStageScene scene in scenes) scene.SetView(Camera(frame));
            RasterImage expected = await conservative.ReadNextRasterImageAsync();
            RasterImage actual = await declared.ReadNextRasterImageAsync();
            RasterImage unchanged = await fixedRoughness.ReadNextRasterImageAsync();
            RasterImage.AssertEqual(expected, actual);
            if (!actual.Color.AsSpan().SequenceEqual(unchanged.Color)) shadingChanges++;
            var kept = declared.ReadCasterEditsForChecks(declaredRevision);
            var refreshed = conservative.ReadCasterEditsForChecks(conservativeRevision);
            Assert.Equal(declaredRevision, kept.Revision);
            Assert.Equal(0, kept.Rows);
            Assert.Equal(0, kept.StreamingRows);
            Assert.True(refreshed.Revision > conservativeRevision);
            Assert.Equal(2, refreshed.Rows);
            Assert.Equal(0, refreshed.StreamingRows);
            conservativeRevision = refreshed.Revision;
        }
        Assert.True(shadingChanges > 0);
    }

    [Fact]
    public async Task ADeclaredRoughnessCoverageInputRefreshesAnAgedMaskedCaster()
    {
        Shader shader = _shaders.Coverage;
        Mesh plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var changed = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        await using var fresh = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        Configure(changed);
        Configure(fresh);
        using Texture alpha = AlphaPattern(0);
        StandardMaterial material = changed.CreateCasterCheckMaterial(shader, alpha);
        StandardMaterial expectedMaterial = fresh.CreateCasterCheckMaterial(shader, alpha);
        material.Roughness = .2f;
        expectedMaterial.Roughness = .8f;
        changed.UseMaterial(1, material);
        fresh.UseMaterial(1, expectedMaterial);
        for (int frame = 0; frame < 104; frame++)
        {
            changed.SetView(Camera(frame));
            fresh.SetView(Camera(frame));
            await changed.ReadNextRasterImageAsync();
            await fresh.ReadNextRasterImageAsync();
        }
        ulong revision = changed.CasterRevisionForChecks();
        changed.SetView(Camera(104));
        fresh.SetView(Camera(104));
        RasterImage before = await changed.ReadNextRasterImageAsync();
        await fresh.ReadNextRasterImageAsync();
        material.Roughness = .8f;
        RasterImage actual = await changed.ReadNextRasterImageAsync();
        RasterImage expected = await fresh.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(expected, actual);
        Assert.NotEqual(before.Color, actual.Color);
        var edits = changed.ReadCasterEditsForChecks(revision);
        Assert.True(edits.Revision > revision);
        Assert.Equal(1, edits.Rows);
        Assert.Equal(0, edits.StreamingRows);
    }

    [Fact]
    public async Task ReplacingAlphaTextureRefreshesAStaticCasterAfterCompletedFrameAging()
    {
        Shader shader = _shaders.Mask;
        Mesh plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var changed = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        await using var fresh = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        Configure(changed);
        Configure(fresh);
        using Texture first = AlphaPattern(0);
        using Texture second = AlphaPattern(1);
        StandardMaterial material = changed.CreateCasterCheckMaterial(shader, first);
        changed.UseMaterial(1, material);
        fresh.UseMaterial(1, fresh.CreateCasterCheckMaterial(shader, second));
        await changed.ReadShadowQualityAsync();
        await fresh.ReadShadowQualityAsync();
        changed.SetMeshTransform(0, Receiver(.2f));
        changed.SetMeshTransform(1, Caster(.2f));
        fresh.SetMeshTransform(0, Receiver(.2f));
        fresh.SetMeshTransform(1, Caster(.2f));
        await changed.ReadShadowQualityAsync();
        await fresh.ReadShadowQualityAsync();
        changed.SetMeshTransform(0, Receiver(0));
        changed.SetMeshTransform(1, Caster(0));
        fresh.SetMeshTransform(0, Receiver(0));
        fresh.SetMeshTransform(1, Caster(0));
        await changed.ReadShadowQualityAsync();
        await fresh.ReadShadowQualityAsync();
        uint dynamicWork = 0, returnedStaticWork = 0;
        RasterImage? before = null;
        for (int frame = 0; frame < 104; frame++)
        {
            changed.SetView(Camera(frame));
            fresh.SetView(Camera(frame));
            var completed = await changed.ReadShadowQualityAsync();
            await fresh.ReadShadowQualityAsync();
            before = completed.Image;
            if (frame < 8) dynamicWork += DynamicItems(completed.Quality);
            if (frame >= 100) returnedStaticWork += StaticItems(completed.Quality);
        }
        Assert.True(dynamicWork > 0);
        Assert.True(returnedStaticWork > 0);
        ulong revision = changed.CasterRevisionForChecks();
        changed.SetView(Camera(104));
        fresh.SetView(Camera(104));
        before = (await changed.ReadShadowQualityAsync()).Image;
        await fresh.ReadShadowQualityAsync();
        material.AlbedoMap = second;
        var after = await changed.ReadShadowQualityAsync();
        var expected = await fresh.ReadShadowQualityAsync();
        RasterImage.AssertEqual(expected.Image, after.Image);
        Assert.NotEqual(before!.Color, after.Image.Color);
        Assert.True(DynamicItems(after.Quality) > 0);
        var edits = changed.ReadCasterEditsForChecks(revision);
        Assert.True(edits.Revision > revision);
        Assert.Equal(1, edits.Rows);
        Assert.Equal(0, edits.StreamingRows);
        // Return to the same camera views after both layers have been touched.
        for (int frame = 105; frame < 137; frame++)
        {
            changed.SetView(Camera(frame));
            fresh.SetView(Camera(frame));
            RasterImage.AssertEqual((await fresh.ReadShadowQualityAsync()).Image,
                (await changed.ReadShadowQualityAsync()).Image);
        }
    }

    [Fact]
    public async Task InPlaceAlphaUploadRefreshesTheCasterAtItsPublicCompletionBoundary()
    {
        Shader shader = _shaders.Mask;
        Mesh plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var changed = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        await using var fresh = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        Configure(changed);
        Configure(fresh);
        using Texture texture = AlphaPattern(0);
        using Texture replacement = AlphaPattern(1);
        changed.UseMaterial(1, changed.CreateCasterCheckMaterial(shader, texture));
        fresh.UseMaterial(1, fresh.CreateCasterCheckMaterial(shader, replacement));
        for (int frame = 0; frame < 104; frame++)
        {
            changed.SetView(Camera(frame));
            fresh.SetView(Camera(frame));
            await changed.ReadNextRasterImageAsync();
            await fresh.ReadNextRasterImageAsync();
        }
        ulong revision = changed.CasterRevisionForChecks();
        Task transfer = texture.UpdateAsync(AlphaPattern(1)).AsTask();
        changed.SetView(Camera(104));
        fresh.SetView(Camera(104));
        await changed.ReadNextRasterImageAsync();
        await fresh.ReadNextRasterImageAsync();
        await transfer;
        changed.SetView(Camera(105));
        fresh.SetView(Camera(105));
        RasterImage actual = await changed.ReadNextRasterImageAsync();
        RasterImage.AssertEqual(await fresh.ReadNextRasterImageAsync(), actual);
        var edits = changed.ReadCasterEditsForChecks(revision);
        Assert.True(edits.Revision > revision);
        Assert.Equal(1, edits.Rows);
        Assert.Equal(0, edits.StreamingRows);
    }

    [Fact]
    public async Task ChangingTheAlphaSamplerRefreshesTheAgedCaster()
    {
        Shader shader = _shaders.Mask;
        Mesh plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var changed = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        await using var fresh = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        Configure(changed);
        Configure(fresh);
        using Texture alpha = AlphaPattern(0);
        StandardMaterial material = changed.CreateCasterCheckMaterial(shader, alpha);
        StandardMaterial expectedMaterial = fresh.CreateCasterCheckMaterial(shader, alpha);
        SamplerDesc nearest = new(FilterType.Nearest, FilterType.Nearest, FilterType.Nearest,
            AddressType.Repeat, AddressType.Repeat, AddressType.Repeat);
        expectedMaterial.MaterialSampler = nearest;
        changed.UseMaterial(1, material);
        fresh.UseMaterial(1, expectedMaterial);
        for (int frame = 0; frame < 104; frame++)
        {
            changed.SetView(Camera(frame));
            fresh.SetView(Camera(frame));
            await changed.ReadNextRasterImageAsync();
            await fresh.ReadNextRasterImageAsync();
        }
        ulong revision = changed.CasterRevisionForChecks();
        material.MaterialSampler = nearest;
        for (int frame = 104; frame < 112; frame++)
        {
            changed.SetView(Camera(frame));
            fresh.SetView(Camera(frame));
            RasterImage.AssertEqual(await fresh.ReadNextRasterImageAsync(), await changed.ReadNextRasterImageAsync());
        }
        var edits = changed.ReadCasterEditsForChecks(revision);
        Assert.True(edits.Revision > revision);
        Assert.Equal(1, edits.Rows);
        Assert.Equal(0, edits.StreamingRows);
    }

    [Fact]
    public async Task ReplacingTheCutoffShaderRefreshesTheAgedCaster()
    {
        Shader firstShader = _shaders.Mask;
        Shader secondShader = _shaders.Cutoff;
        Mesh plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var changed = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        await using var fresh = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        Configure(changed);
        Configure(fresh);
        using Texture alpha = AlphaPattern(0);
        StandardMaterial material = changed.CreateCasterCheckMaterial(firstShader, alpha);
        changed.UseMaterial(1, material);
        fresh.UseMaterial(1, fresh.CreateCasterCheckMaterial(secondShader, alpha));
        for (int frame = 0; frame < 104; frame++)
        {
            changed.SetView(Camera(frame));
            fresh.SetView(Camera(frame));
            await changed.ReadNextRasterImageAsync();
            await fresh.ReadNextRasterImageAsync();
        }
        ulong revision = changed.CasterRevisionForChecks();
        material.Shader = secondShader;
        changed.SetView(Camera(104));
        fresh.SetView(Camera(104));
        RasterImage.AssertEqual(await fresh.ReadNextRasterImageAsync(), await changed.ReadNextRasterImageAsync());
        var edits = changed.ReadCasterEditsForChecks(revision);
        Assert.True(edits.Revision > revision);
        Assert.Equal(1, edits.Rows);
        Assert.Equal(0, edits.StreamingRows);
    }

    [Fact]
    public async Task MovingAnAgedMaskedCasterUpdatesItsOldAndNewShadowBounds()
    {
        Shader shader = _shaders.Mask;
        Mesh plane = await RasterTestGeometry.CreatePlaneAsync();
        await using var changed = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        await using var fresh = await FrameStageScene.CreateAsync("D3D12", true, Options(), plane, Shadows());
        Configure(changed);
        Configure(fresh);
        using Texture alpha = AlphaPattern(0);
        changed.UseMaterial(1, changed.CreateCasterCheckMaterial(shader, alpha));
        fresh.UseMaterial(1, fresh.CreateCasterCheckMaterial(shader, alpha));
        fresh.SetMeshTransform(1, Caster(.7f));
        for (int frame = 0; frame < 104; frame++)
        {
            changed.SetView(Camera(frame));
            fresh.SetView(Camera(frame));
            await changed.ReadShadowQualityAsync();
            await fresh.ReadShadowQualityAsync();
        }
        changed.SetView(Camera(104));
        fresh.SetView(Camera(104));
        RasterImage before = (await changed.ReadShadowQualityAsync()).Image;
        await fresh.ReadShadowQualityAsync();
        changed.SetMeshTransform(1, Caster(.7f));
        for (int frame = 104; frame < 136; frame++)
        {
            changed.SetView(Camera(frame));
            fresh.SetView(Camera(frame));
            var expected = await fresh.ReadShadowQualityAsync();
            var actual = await changed.ReadShadowQualityAsync();
            RasterImage.AssertEqual(expected.Image, actual.Image);
            if (frame == 104)
            {
                Assert.NotEqual(before.Color, actual.Image.Color);
                Assert.True(DynamicItems(actual.Quality) > 0);
            }
        }
    }

    // Only immutable shaders are shared; every behavior owns its worlds and edited resources.
    public sealed class MaskedShaders
    {
        private readonly Lazy<Shader> _mask = new(() => Compile("Declared masked depth inputs", MaskSource()));
        private readonly Lazy<Shader> _conservative = new(() => Compile("Conservative masked depth inputs",
            MaskSource().Replace("[MaterialDepthInputs(\"AlbedoMap,MaterialSampler\")]", "")));
        private readonly Lazy<Shader> _coverage = new(() => Compile("Roughness controls alpha coverage", MaskSource()
            .Replace("AlbedoMap,MaterialSampler", "AlbedoMap,MaterialSampler,Roughness")
            .Replace(">= 0.5", ">= scalars.Roughness")));
        private readonly Lazy<Shader> _cutoff = new(() => Compile("Masked cutoff four fifths",
            MaskSource().Replace(">= 0.5", ">= 0.8")));

        internal Shader Mask => _mask.Value;
        internal Shader Conservative => _conservative.Value;
        internal Shader Coverage => _coverage.Value;
        internal Shader Cutoff => _cutoff.Value;
    }

    private static ClusterPipelineOptions Options() => new()
    {
        MaxCandidates = 65536, ForceHardwareRaster = true, EnableFrameMetricsReadback = true,
    };

    private static VirtualShadowMapSettings Shadows() => new()
    {
        VirtualResolution = 4096, AtlasSize = 2048, WorldUnitsPerCentimeter = 1,
        MaxShadowLights = 1, MaxFilterRadiusTexels = 0,
        DirectionalClipmapLevels = 16, DirectionalFirstClipmapLevel = -4,
        DirectionalFirstCoarseClipmapLevel = 8, DirectionalLastCoarseClipmapLevel = 11,
        DynamicResolutionEnabled = false,
    };

    private static TransformQvvs Receiver(float x) =>
        new(new Vector3(x, 0, -8), Quaternion.Identity) { Stretch = new(3, 3, .1f) };

    private static TransformQvvs Caster(float x) =>
        new(new Vector3(x, 4, -4), Quaternion.Identity) { Stretch = new(2, 1.5f, .1f) };

    private static void Configure(FrameStageScene scene)
    {
        scene.SetMeshTransform(0, Receiver(0));
        scene.SetMeshTransform(1, Caster(0));
        scene.UseDirectionalLight(Vector3.Normalize(new(0, -1, -1)));
        scene.SetShadingPath(ClusterShadingPath.Forward);
    }

    private static RenderView Camera(int frame)
    {
        float angle = frame * (MathF.Tau / 32);
        return new(Matrix4x4.CreateLookAt(new(.18f * MathF.Sin(angle), .08f * MathF.Cos(angle), 0),
            new(0, 0, -6), Vector3.UnitY), Matrix4x4.CreatePerspectiveFieldOfView(1, 1, .1f, 100), 64, 64);
    }

    private static uint StaticItems(VirtualShadowFrameQuality quality) =>
        (uint)quality.Views.Sum(view => view.StaticHardwareVisibleItems + view.StaticSoftwareVisibleItems);

    private static uint DynamicItems(VirtualShadowFrameQuality quality) =>
        (uint)quality.Views.Sum(view => view.DynamicHardwareVisibleItems + view.DynamicSoftwareVisibleItems);

    private static Texture AlphaPattern(int offset)
    {
        byte[] pixels = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
        for (int x = 0; x < 4; x++)
        {
            int pixel = (y * 4 + x) * 4;
            pixels[pixel] = pixels[pixel + 1] = pixels[pixel + 2] = 255;
            pixels[pixel + 3] = (x + y + offset) % 2 == 0 ? (byte)192 : (byte)64;
        }
        return Texture.Create(new TextureDesc(TextureDimension.Texture2D, 4, 4, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination),
            [new TexturePixels(new TextureMipTile { Width = 4, Height = 4, RowPitch = 16, SlicePitch = 64 }, pixels)],
            TextureViewDimension.Texture2D);
    }

    private static string MaskSource()
    {
        string root = FrameStageScene.FindRoot();
        string standard = File.ReadAllText(Path.Combine(root, "templates/DefaultProject/assets/Shaders/standard.slang"));
        return standard.Replace("typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;", """
            struct MaskVarying { float2 UV : TEXCOORD0; };
            [VisibilityDepth("masked")]
            [MaterialDepthInputs("AlbedoMap,MaterialSampler")]
            struct AlphaMask : IVisibilityEvaluate<StandardVertex>
            {
                typealias Varying = MaskVarying;
                SamplerState MaterialSampler;
                Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input)
                { Varying value; value.UV = vertex.uv0; return value; }
                Varying readLane(Varying value, uint lane)
                { Varying result; result.UV = WaveReadLaneAt(value.UV, lane); return result; }
                Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary)
                { Varying result; result.UV = a.UV * bary.x + b.UV * bary.y + c.UV * bary.z; return result; }
                Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input)
                { return interpolateVertex(a, b, c, input.PerspectiveBarycentric); }
                VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
                {
                    VisibilityPixelResult result;
                    result.Depth = input.Depth;
                    if (input.TargetPolicy != 0u && input.EncodedTargetDepth >= asuint(1.0 - saturate(input.Depth)))
                    { result.Visible = 0u; return result; }
                    DrawRequest cluster = VisibleClusterRequest(VisibleClusters[input.VisibleClusterIndex]);
                    uint clusterOffset = cluster.PageOffset + PageHeap.Load(cluster.PageOffset + 16u) +
                        DrawRequestClusterID(cluster) * PACKED_CLUSTER_STRIDE_BYTES;
                    uint materialIndex = MaterialSlotOffset(InstanceData, InstanceProperties, cluster.InstanceID) +
                        LocalMaterial(PageHeap, clusterOffset, cluster.PageOffset, input.TriangleID);
                    StandardScalars scalars = LoadMaterialScalars<StandardScalars>(materialIndex);
                    result.Visible = StandardMaterialTextures[NonUniformResourceIndex(scalars.AlbedoMap)]
                        .SampleLevel(MaterialSampler, varying.UV, 0.0).a >= 0.5 ? 1u : 0u;
                    return result;
                }
            };
            typedef AlphaMask VisibilityEvaluate;
            """);
    }

    private static Shader Compile(string name, string source)
    {
        string root = FrameStageScene.FindRoot();
        var description = TestShaderLoading.CreateSessionDescription(SlangCompileTarget.Dxil);
        description.SearchPaths =
        [
            Path.Combine(root, "templates/DefaultProject/assets/Shaders"),
            Path.Combine(root, "assets/Shaders"),
        ];
        IGlobalSession slang = TestShaderLoading.Slang;
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        IModule? unit = session.LoadModuleFromSource(name, name + ".slang",
            Slang.CreateBlob(Encoding.UTF8.GetBytes(source)), out var diagnostics);
        Assert.True(unit is not null, diagnostics is null ? "" : Encoding.UTF8.GetString(diagnostics.Buffer));
        Assert.True(unit!.Serialize(out ISlangBlob serialized).Succeeded);
        using var inputs = new ShaderSourceBlock
        {
            SourceNames = [name + ".slang"],
            Specializations =
            [
                new ShaderSpecialization { EntryPoint = "ClusterForwardShade", Arguments = ["1", "0", "false"] },
                new ShaderSpecialization { EntryPoint = "ClusterForwardShade", Arguments = ["1", "0", "true"] },
                new ShaderSpecialization { EntryPoint = "ClusterShadowReceiver", Arguments = ["1", "0"] },
            ],
        };
        inputs.SetSerializedSource(0, serialized.Buffer);
        Shader shader = inputs.CreateAsync(slang, description, name).AsTask().GetAwaiter().GetResult();
        Assert.True(shader.TryGetSpecialization("ClusterForwardShade", ["1", "0", "false"], out _));
        Assert.True(shader.TryGetSpecialization("ClusterShadowReceiver", ["1", "0"], out _));
        return shader;
    }
}

internal sealed partial class FrameStageScene
{
    internal StandardMaterial StandardForCasterChecks => Assert.IsType<StandardMaterial>(_standard);

    internal StandardMaterial CreateCasterCheckMaterial(Shader shader, Texture alpha)
    {
        StandardMaterial original = StandardForCasterChecks;
        var material = new StandardMaterial
        {
            Shader = shader, AlbedoMap = alpha, NormalMap = original.NormalMap, ARMMap = original.ARMMap,
            MaterialSampler = original.MaterialSampler, BaseColorTint = original.BaseColorTint,
            MetallicFactor = original.MetallicFactor, Roughness = original.Roughness,
            EmissiveFactor = original.EmissiveFactor,
        };
        _extraMaterials.Add(material);
        return material;
    }

    internal ulong CasterRevisionForChecks()
    {
        _pipeline.CollectChangedCasters(0, [], [], out ulong revision);
        return revision;
    }

    internal (ulong Revision, int Rows, int StreamingRows) ReadCasterEditsForChecks(ulong after)
    {
        var rows = new List<uint>();
        var streaming = new List<uint>();
        Assert.True(_pipeline.CollectChangedCasters(after, rows, streaming, out ulong revision));
        return (revision, rows.Count, streaming.Count);
    }
}
