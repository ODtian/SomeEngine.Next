using SomeEngine.Transparency.Measurements;
using System.Numerics;
using DefaultGame.Content;
using SomeEngine.Core.Math;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Resources;
using SomeEngine.Resources.Layers;
using SomeEngine.Render.Features.Layers;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SlangShaderSharp;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Hardware")]
[Collection("Ordered content")]
public sealed class OrderedRenderingTests(OrderedContentFixture content)
{
    [SceneTheory]
    [InlineData("d3d12")]
    public async Task ReadOnlyStencilMaskKeepsTheHardwareBackground(string backend)
    {
        SceneLinearImage? hardware = null, masked = null;
        Entity red = default;
        Matrix4x4 view = default;
        var face = new StencilFaceState(StencilOperation.Keep, StencilOperation.Keep,
            StencilOperation.Keep, CompareOperation.Equal);
        HardwareOrderedState state = new()
        {
            TwoSided = true, StencilRef = 1,
            DepthStencil = new(StencilTest: true, StencilWriteMask: 0, Front: face, Back: face),
        };
        var reference = new HardwareOrderedDraw(RedPanel(new(new Vector3(0, 0, 6), Quaternion.Identity)), new(1, 0, 0, 1), state)
            { Enabled = true };
        UnlitMaterial? layerMaterial = null;
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 16,
        [
            new(0, edit =>
            {
                view = edit.CameraView;
                foreach (Entity entity in edit.MeshEntities)
                    if (edit.GetMaterial(entity) is UnlitMaterial { Name: "red sphere" } material)
                    {
                        red = entity;
                        material.BaseColorTint = new(1, 0, 0, 1);
                        material.Shader = content.BothFacesShader;
                        layerMaterial = material;
                        edit.SetEntityTransform(red, new(new Vector3(20, 0, 6), Quaternion.Identity));
                    }
            }),
            new(7, edit => edit.CaptureLinearImage(image => hardware = image)),
            new(8, edit =>
            {
                reference.Enabled = false;
                edit.SetEntityTransform(red, new(new Vector3(0, 0, 6), Quaternion.Identity));
            }),
            new(15, edit => edit.CaptureLinearImage(image => masked = image)),
        ])
        {
            WaitForFrames = false,
            CreateColorFeature = rendering =>
            {
                LayerPassId pass = rendering.Layers!.AddPass(new("Read-only stencil", LayerSortMode.DrawOrderThenDepth,
                    ClipByOpaqueDepth: false, Accumulator: LayerAccumulator.ColorDepthStencil));
                layerMaterial!.Layer = new(pass, 0, new LayerOperation
                {
                    Opaque = true, StencilTest = true, StencilWriteMask = 0, Front = face, Back = face,
                }, 1);
                return new HardwareOrderedFeature(rendering, [reference], view);
            },
        },
            "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        SceneImages.AssertLinearEqual(hardware!, masked!, .001f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    public async Task MaterialDrawOrderAndColorOperationsMatchHardwareBlending(string backend)
    {
        SceneLinearImage? tile = null, hardware = null;
        Matrix4x4 view = default;
        Vector4 greenColor = new(.15f, .8f, .4f, .65f), redColor = new(.8f, .2f, .5f, 1);
        HardwareOrderedState greenState = new()
        {
            DrawOrder = 10, TwoSided = true, DepthStencil = new(),
            Blend = new(Enabled: true, SourceColor: BlendFactor.SourceAlpha, DestinationColor: BlendFactor.OneMinusSourceAlpha,
                SourceAlpha: BlendFactor.Zero, DestinationAlpha: BlendFactor.One),
        };
        HardwareOrderedState redState = greenState with
        {
            DrawOrder = 20,
            Blend = new(Enabled: true, SourceColor: BlendFactor.DestinationColor, DestinationColor: BlendFactor.Zero,
                SourceAlpha: BlendFactor.Zero, DestinationAlpha: BlendFactor.One,
                WriteMask: ColorWriteMasks.Red | ColorWriteMasks.Green | ColorWriteMasks.Blue),
        };
        var origin = new TransformQvvs(new(0, 0, 6), Quaternion.Identity);
        HardwareOrderedDraw[] references = [new(RedPanel(origin), redColor, redState), new(GreenPanel(origin), greenColor, greenState)];
        var materials = new List<Material>();
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 16,
        [
            new(0, edit =>
            {
                view = edit.CameraView;
                foreach (Entity entity in edit.MeshEntities)
                {
                    var material = (UnlitMaterial)edit.GetMaterial(entity);
                    bool red = material.Name == "red sphere";
                    material.BaseColorTint = red ? redColor : greenColor;
                    material.Shader = content.BothFacesShader;
                    materials.Add(material);
                }
            }),
            new(7, edit => edit.CaptureLinearImage(image => tile = image)),
            new(8, _ =>
            {
                foreach (Material material in materials)
                {
                    MaterialLayer layer = material.Layer!.Value;
                    material.Layer = layer with { Operation = layer.Operation with { ColorWriteMask = ColorWriteMasks.None } };
                }
                foreach (HardwareOrderedDraw draw in references) draw.Enabled = true;
            }),
            new(15, edit => edit.CaptureLinearImage(image => hardware = image)),
        ])
        {
            WaitForFrames = false,
            CreateColorFeature = rendering =>
            {
                LayerPassId pass = rendering.Layers!.AddPass(new("Ordered color operations", LayerSortMode.DrawOrderThenDepth,
                    ClipByOpaqueDepth: false, Accumulator: LayerAccumulator.ColorDepthStencil));
                foreach (Material material in materials)
                    material.Layer = material.Name == "red sphere"
                        ? new(pass, 20, new LayerOperation { Color = LayerColorOperation.Multiply,
                            ColorWriteMask = ColorWriteMasks.Red | ColorWriteMasks.Green | ColorWriteMasks.Blue })
                        : new(pass, 10, new LayerOperation { Color = LayerColorOperation.Over });
                return new HardwareOrderedFeature(rendering, references, view);
            },
        },
            "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm");
        SceneImages.AssertLinearEqual(hardware!, tile!, .001f);
    }

    [SceneTheory]
    [InlineData("d3d12")]
    [InlineData("vulkan")]
    public async Task StencilFailDepthFailAndFaceOperationsMatchHardwareMasks(string backend)
    {
        SceneLinearImage? tile = null, hardware = null;
        var measurements = new List<BlendFrameMetrics>();
        Matrix4x4 view = default;
        var writerFace = new StencilFaceState(StencilOperation.Keep, StencilOperation.IncrementWrap,
            StencilOperation.Replace, CompareOperation.Always);
        HardwareOrderedState writerState = new()
        {
            TwoSided = true, StencilRef = 3,
            Blend = new(WriteMask: ColorWriteMasks.None),
            DepthStencil = new(DepthTest: true, DepthComparison: CompareOperation.LessOrEqual, StencilTest: true,
                StencilReadMask: 3, StencilWriteMask: 3, Front: writerFace, Back: writerFace),
        };
        HardwareOrderedState backState = writerState with
        {
            DrawOrder = 1, StencilRef = 1,
            DepthStencil = writerState.DepthStencil with
            {
                DepthTest = false,
                Front = new(StencilOperation.Replace, StencilOperation.Keep, StencilOperation.Keep, CompareOperation.Never),
                Back = new(StencilOperation.Invert, StencilOperation.Keep, StencilOperation.Keep, CompareOperation.Never),
            },
        };
        var readFace = new StencilFaceState(StencilOperation.Keep, StencilOperation.Keep, StencilOperation.Keep, CompareOperation.Equal);
        HardwareOrderedState readState = writerState with
        {
            DrawOrder = 2, Blend = new(),
            DepthStencil = new(StencilTest: true, StencilReadMask: 3, StencilWriteMask: 0, Front: readFace, Back: readFace),
        };
        LayerOperation writerOperation = new()
        {
            Opaque = true, ColorWriteMask = ColorWriteMasks.None,
            DepthTestTarget = LayerDepthTarget.Opaque, DepthComparison = CompareOperation.LessOrEqual,
            StencilTest = true, StencilReadMask = 3, StencilWriteMask = 3, Front = writerFace, Back = writerFace,
        };
        LayerOperation backOperation = writerOperation with
        {
            DepthTestTarget = LayerDepthTarget.None,
            Front = new(StencilOperation.Replace, StencilOperation.Keep, StencilOperation.Keep, CompareOperation.Never),
            Back = new(StencilOperation.Invert, StencilOperation.Keep, StencilOperation.Keep, CompareOperation.Never),
        };
        LayerOperation readOperation = new()
        {
            Opaque = true, StencilTest = true, StencilReadMask = 3, StencilWriteMask = 0, Front = readFace, Back = readFace,
        };
        TransformQvvs origin = new(new(0, 0, 6), Quaternion.Identity);
        TransformQvvs backTransform = new(new(.6f, 0, 6), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI));
        TransformQvvs readerTransform = new(new(0, 0, 4), Quaternion.Identity, 2);
        Vector4 green = new(.1f, .8f, .2f, 1), blue = new(.1f, .2f, .9f, 1), red = new(.9f, .2f, .1f, 1);
        HardwareOrderedState blueState = readState with { DrawOrder = 3, StencilRef = 1 };
        HardwareOrderedState redState = readState with { DrawOrder = 4, StencilRef = 2 };
        var roundTripProbe = new HardwareOrderedDraw(RedPanel(readerTransform), new(.8f, .5f, 1, 1), redState)
            { Enabled = true };
        HardwareOrderedDraw[] references =
        [
            new(RedPanel(origin), Vector4.One, writerState), new(RedPanel(backTransform), Vector4.One, backState),
            new(RedPanel(readerTransform), green, readState), new(RedPanel(readerTransform), blue, blueState),
            new(RedPanel(readerTransform), red, redState),
        ];
        var back = new UnlitMaterial(); var greenMaterial = new UnlitMaterial();
        var blueMaterial = new UnlitMaterial(); var redMaterial = new UnlitMaterial();
        var materials = new List<Material>();
        var assignments = new List<(Material Material, byte Order, LayerOperation Operation, byte Reference)>();
        await SceneImages.RunAsync(backend, new SceneScript(content.DirectoryPath, 16,
        [
            new(0, edit =>
            {
                view = edit.CameraView;
                Entity writer = edit.MeshEntities.Single(entity => edit.GetMaterial(entity).Name == "red sphere");
                var source = (UnlitMaterial)edit.GetMaterial(writer);
                source.BaseColorTint = Vector4.One; source.Shader = content.BothFacesShader;
                materials.Add(source);
                assignments.Add((source, 0, writerOperation, 3));
                var backdrop = (UnlitMaterial)edit.GetMaterial(edit.MeshEntities.Single(entity => entity != writer));
                backdrop.BaseColorTint = new(.05f, .05f, .05f, 1);
                foreach (var item in new[] { (back, backTransform, Vector4.One, backOperation, (byte)1, (byte)1),
                    (greenMaterial, readerTransform, green, readOperation, (byte)2, (byte)3),
                    (blueMaterial, readerTransform, blue, readOperation, (byte)3, (byte)1),
                    (redMaterial, readerTransform, red, readOperation, (byte)4, (byte)2) })
                {
                    item.Item1.Shader = source.Shader; item.Item1.AlbedoMap = source.AlbedoMap;
                    item.Item1.MaterialSampler = source.MaterialSampler; item.Item1.BaseColorTint = item.Item3;
                    Entity entity = edit.AddEntity(writer, item.Item2);
                    edit.ReplaceMaterial(entity, item.Item1);
                    materials.Add(item.Item1);
                    assignments.Add((item.Item1, item.Item5, item.Item4, item.Item6));
                }
            }),
            new(7, edit => edit.CaptureLinearImage(image => tile = image)),
            new(8, _ =>
            {
                foreach (Material material in materials)
                {
                    MaterialLayer layer = material.Layer!.Value;
                    material.Layer = layer with
                    {
                        Operation = layer.Operation with { ColorWriteMask = ColorWriteMasks.None, StencilTest = false },
                    };
                }
                foreach (HardwareOrderedDraw draw in references) draw.Enabled = true;
            }),
            new(15, edit => edit.CaptureLinearImage(image => hardware = image)),
        ])
        {
            WaitForFrames = false, CreateFrameFeatures = rendering => [new BlendMeasurementFeature(rendering, measurements.Add)],
            CreateColorFeature = rendering =>
            {
                LayerPassId pass = rendering.Layers!.AddPass(new("Stencil face and depth operations", LayerSortMode.DrawOrderThenDepth,
                    ClipByOpaqueDepth: false, Accumulator: LayerAccumulator.ColorDepthStencil));
                foreach (var assignment in assignments)
                    assignment.Material.Layer = new(pass, assignment.Order, assignment.Operation, assignment.Reference);
                return new HardwareOrderedFeature(rendering, references, view, roundTripProbe);
            },
        },
            "--hardware-raster", "--shading-path", "forward", "--no-taa", "--no-vsm", "--measure-blending");
        SceneImages.AssertLinearEqual(hardware!, tile!, .001f);
        Assert.Equal(16, measurements.Count);
        Assert.True(measurements.Single(frame => frame.SimulationFrame == 7).LayerCompositeMs > 0);
        Assert.True(measurements.Single(frame => frame.SimulationFrame == 15).LayerCompositeMs > 0);
    }

    private static Vector3[] RedPanel(TransformQvvs transform) => new Vector3[]
    {
        new(-2,-2,-1), new(2,2,1), new(2,-2,1), new(-2,-2,-1), new(-2,2,-1), new(2,2,1),
    }.Select(position => Vector3.Transform(position, transform.ToMatrix())).ToArray();

    private static Vector3[] GreenPanel(TransformQvvs transform) => new Vector3[]
    {
        new(-2,-2,1), new(2,2,-1), new(2,-2,-1), new(-2,-2,1), new(-2,2,1), new(2,2,-1),
    }.Select(position => Vector3.Transform(position, transform.ToMatrix())).ToArray();
}

[CollectionDefinition("Ordered content")]
public sealed class OrderedContentCollection : ICollectionFixture<OrderedContentFixture>;

public sealed class OrderedContentFixture : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Ordered-{Guid.NewGuid():N}");
    private ShaderLibrary? _shaders;
    public string DirectoryPath => Path.Combine(_directory, "panels");
    public Shader BothFacesShader { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        try
        {
            string spheres = Path.Combine(_directory, "spheres");
            string? engine = AppContext.BaseDirectory;
            while (engine is not null && !File.Exists(Path.Combine(engine, "SomeEngine.slnx"))) engine = Path.GetDirectoryName(engine);
            string? cooked = Environment.GetEnvironmentVariable("SOMEENGINE_TEST_COOKED_CONTENT");
            if (cooked is null)
            {
                await SomeEngine.SceneContent.SceneContent.GenerateSpheresFromSharedAsync(spheres,
                    Path.Combine(engine!, "samples", "DefaultGame"), engine!);
            }
            else await SomeEngine.SceneContent.SceneContent.GenerateSpheresFromCookedAsync(spheres, cooked);
            await SomeEngine.SceneContent.BehaviorScenes.CreateIntersectingPanelsAsync(spheres, DirectoryPath);
            string sampleRoot = Path.Combine(engine!, "samples", "DefaultGame");
            string sampleShaders = Path.Combine(sampleRoot, "assets", "Shaders");
            string engineShaders = Path.Combine(engine!, "assets", "Shaders");
            string path = Path.Combine(sampleShaders, "unlit-both.shader");
            SlangCompileTarget[] targets = [SlangCompileTarget.Dxil, SlangCompileTarget.Spirv];
            IGlobalSession slang = Shader.CreateSlangGlobalSession();
            SessionDesc settings = SlangShaderImporter.CreateSessionDescription(path,
                SlangShaderImporter.CreateTargets(slang, targets), [sampleShaders, engineShaders]);
            _shaders = new ShaderLibrary(slang, settings);
            AssetManifest manifest = AssetManifest.Load(Path.Combine(sampleRoot, "Library", "AssetManifest"));
            await using var files = EngineShaderContent.OpenFiles(sampleRoot, manifest);
            BothFacesShader = await _shaders.GetAsync(files,
                new AssetId<ShaderAsset>(EngineShaderContent.GetShaderId(sampleRoot, "unlit-both.shader")));
        }
        catch { await DisposeAsync(); throw; }
    }

    public Task DisposeAsync()
    {
        _shaders?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }
}
