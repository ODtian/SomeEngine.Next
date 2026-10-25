using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Components;
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Lighting;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class LightingViewRenderingTests
{
    [Fact]
    public void LightingViewsFollowActiveCountAndClearUnusedViews()
    {
        using var backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        backend.TryEnumerateAdapters(enumeration, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(backend.TryEnumerateAdapters(enumeration, adapters, out int confirmed));
        Assert.Equal(count, confirmed);
        AdapterInfo warp = adapters.First(adapter => !adapter.HardwareAccelerated);
        using Device device = backend.CreateDevice(new DeviceDesc(warp.Id, [new(QueueType.Graphics)]));
        using var graph = new global::SomeEngine.RenderGraph.RenderGraph(backend, device,
            [backend.GetQueue(device, QueueType.Graphics)], new RenderGraphDesc(MaximumFramesInFlight: 1));
        Shader lightingShader = LoadShader("LightingViewAssignment", "#include \"clustered_light_grid.slang\"",
            ["ClearClusteredLightGrid", "BuildClusteredLightGrid", "CompactClusteredLightGrid"]);
        using var lighting = new LightingFeature(backend, device, graph, 64, 64, 2,
            lightingShader, "BuildClusteredLightGrid",
            new ClusteredLightGridSettings { TileSize = 16, DepthSliceCount = 4, MaxLightsPerCell = 4, MaxLights = 4 });
        Shader readerShader = LoadShader("LightingViewValues", ViewValuesShader, ["ReadLightingViewValues"]);
        using var reader = LinkedComputePipeline.Create(backend, device, readerShader,
            "ReadLightingViewValues", "Read lighting view values");
        var results = new ViewValues();
        RenderGraphEdit edit = graph.BeginEdit();
        try
        {
            GraphBufferId values = edit.CreateBuffer(new BufferDesc(24 * 16,
                BufferUsages.ShaderWrite | BufferUsages.CopySource, "Lighting view values"));
            GraphPassId read = edit.AddComputeFramePass("Read lighting view values",
                PassQueueSelection.AnyCompatible(), new DispatchArguments(2, 1, 1),
                new PassOptions(Culling: PassCullingMode.NeverCull), reader.Pipeline, reader.ParameterLayout, default,
                static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch));
            PassDefinition pass = edit.GetPass(read);
            int[] resources = ShaderResourceLayout.Resolve<int>(reader.ParameterLayout,
                static (name, type) => (name, type) switch
                {
                    ("LightingViews", ResourceBindingType.BufferSrv) => 0,
                    ("ViewValues", ResourceBindingType.BufferUav) => 1,
                    _ => null,
                });
            foreach (int resource in resources)
            {
                if (resource == 0)
                {
                    Assert.True(lighting.TryResolve("LightingViews", ResourceBindingType.BufferSrv, out var bind));
                    bind(ref edit, ref pass, PipelineSync.ComputeShading);
                }
                else
                    pass.Bind(edit.CreateBufferUav(values, structureStride: 16),
                        PipelineSync.ComputeShading, GraphAccessMode.Write, WriteCoverage.Complete);
            }
            edit.AddReadback(values, BufferRange.Whole, results, "Lighting view results");
            edit.Commit();
        }
        finally { edit.Dispose(); }

        var lights = new RenderLightSet();
        lights.AddDirectional(new RenderDirectionalLight(-Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
        lights.AddPoint(new RenderPointLight(new(0, 0, -8), 1, Vector3.One, 1, uint.MaxValue));
        lights.AddPoint(new RenderPointLight(new(1, 0, -8), 1, Vector3.One, 1, uint.MaxValue));
        RenderView first = new(Matrix4x4.CreateTranslation(1, 2, -3),
            Matrix4x4.CreatePerspectiveOffCenter(-1, 2, -2, 1, 1, 16), 61, 45);
        RenderView second = new(Matrix4x4.CreateTranslation(-4, 5, -6),
            Matrix4x4.CreatePerspectiveOffCenter(-2, 1, -1, 3, 2, 32), 29, 47);
        Vector4[] two = Read([first, second], enabled: true);
        AssertView(two.AsSpan(0, 12), first, new(16, 16, 4, 3), new(4, 4, 1, 2), new(61, 45, 64, 2));
        AssertView(two.AsSpan(12, 12), second, new(16, 16, 2, 3), new(4, 4, 1, 2), new(29, 47, 64, 2));

        RenderView changed = new(Matrix4x4.CreateTranslation(7, -8, -9),
            Matrix4x4.CreatePerspectiveOffCenter(-3, 2, -2, 4, 1, 64), 37, 19);
        Vector4[] one = Read([changed], enabled: false);
        AssertView(one.AsSpan(0, 12), changed, new(16, 16, 3, 2), new(4, 4, 0, 0), new(37, 19, 64, 4));
        Assert.All(one.AsSpan(12, 12).ToArray(), value => Assert.Equal(Vector4.Zero, value));

        lights.Clear();
        lights.AddDirectional(new RenderDirectionalLight(-Vector3.UnitY, Vector3.One, 1, uint.MaxValue));
        lights.AddDirectional(new RenderDirectionalLight(-Vector3.UnitX, Vector3.One, 1, uint.MaxValue));
        lights.AddPoint(new RenderPointLight(new(0, 0, -8), 1, Vector3.One, 1, uint.MaxValue));
        RenderView restoredFirst = new(Matrix4x4.CreateTranslation(-10, 11, -12),
            Matrix4x4.CreatePerspectiveOffCenter(-2, 3, -4, 1, 2, 48), 48, 31);
        RenderView restoredSecond = new(Matrix4x4.CreateTranslation(13, -14, -15),
            Matrix4x4.CreatePerspectiveOffCenter(-4, 2, -1, 2, 1, 128), 33, 17);
        Vector4[] restored = Read([restoredFirst, restoredSecond], enabled: true);
        AssertView(restored.AsSpan(0, 12), restoredFirst, new(16, 16, 3, 2), new(4, 4, 2, 1), new(48, 31, 64, 4));
        AssertView(restored.AsSpan(12, 12), restoredSecond, new(16, 16, 3, 2), new(4, 4, 2, 1), new(33, 17, 64, 4));

        Vector4[] Read(RenderView[] views, bool enabled)
        {
            results.Values.Clear();
            var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            var frame = graph.BeginFrame();
            try
            {
                lighting.Record(ref frame, lights, views, enabled);
                frame.Execute(completions);
            }
            finally { frame.Dispose(); }
            foreach (QueueCompletion completion in completions)
                if (completion != default)
                    Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            graph.CollectCompleted();
            Vector4[] values = Assert.Single(results.Values);
            Assert.Equal(24, values.Length);
            return values;
        }
    }

    private static void AssertView(ReadOnlySpan<Vector4> actual, RenderView expected,
        Vector4 tiles, Vector4 lights, Vector4 screen)
    {
        Vector4[] basis = [Vector4.UnitX, Vector4.UnitY, Vector4.UnitZ, Vector4.UnitW];
        for (int index = 0; index < basis.Length; index++)
        {
            AssertVector(Vector4.Transform(basis[index], expected.View), actual[index]);
            AssertVector(Vector4.Transform(basis[index], expected.Projection), actual[index + 4]);
        }
        Assert.Equal(tiles, actual[8]);
        float scale = 4 / MathF.Log2(10000f / .1f);
        AssertVector(new(.1f, 10000f, scale, -MathF.Log2(.1f) * scale), actual[9]);
        Assert.Equal(lights, actual[10]);
        Assert.Equal(screen, actual[11]);
    }

    private static void AssertVector(Vector4 expected, Vector4 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0, .00001f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0, .00001f);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0, .00001f);
        Assert.InRange(MathF.Abs(expected.W - actual.W), 0, .00001f);
    }

    private static Shader LoadShader(string name, string source, string[] entryNames)
    {
        IGlobalSession slang = TestShaderLoading.Slang;
        var description = new SessionDesc
        {
            Targets = [new TargetDesc { Format = SlangCompileTarget.Dxil, Profile = slang.FindProfile("sm_6_0") }],
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            SearchPaths = [Path.Combine(FrameStageScene.FindRoot(), "assets", "Shaders")],
            CompilerOptionEntries =
            [
                new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
            ],
        };
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        IModule? unit = session.LoadModuleFromSource(name, name + ".slang",
            Slang.CreateBlob(Encoding.UTF8.GetBytes(source)), out ISlangBlob? diagnostics);
        Assert.True(unit is not null, DiagnosticText(diagnostics));
        var entries = new List<IEntryPoint>();
        foreach (string entryName in entryNames)
        {
            Assert.True(unit!.FindEntryPointByName(entryName, out IEntryPoint entry).Succeeded);
            entries.Add(entry);
        }
        Assert.True(session.CreateCompositeComponentType([unit!, .. entries], out IComponentType combined,
            out diagnostics).Succeeded, DiagnosticText(diagnostics));
        Assert.True(combined.Link(out IComponentType linked, out diagnostics).Succeeded, DiagnosticText(diagnostics));
        return Shader.CreateFromSlang(name, slang, session, [unit!], entries, linked, description.Targets);
    }

    private static string DiagnosticText(ISlangBlob? value) =>
        value is null ? "" : Encoding.UTF8.GetString(value.Buffer);

    private sealed class ViewValues : IGraphReadbackReceiver
    {
        internal List<Vector4[]> Values { get; } = [];
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) =>
            Values.Add(MemoryMarshal.Cast<byte, Vector4>(bytes).ToArray());
    }

    // These vectors are this consumer's named results, not a copy of the upload's byte layout.
    private const string ViewValuesShader = """
        #include "render_lighting.slang"
        StructuredBuffer<ClusteredLightGridParameters> LightingViews;
        RWStructuredBuffer<float4> ViewValues;
        [shader("compute")]
        [numthreads(1, 1, 1)]
        void ReadLightingViewValues(uint3 id : SV_DispatchThreadID)
        {
            if (id.x >= 2u) return;
            ClusteredLightGridParameters view = LightingViews[id.x];
            uint first = id.x * 12u;
            ViewValues[first] = mul(float4(1, 0, 0, 0), view.View);
            ViewValues[first + 1u] = mul(float4(0, 1, 0, 0), view.View);
            ViewValues[first + 2u] = mul(float4(0, 0, 1, 0), view.View);
            ViewValues[first + 3u] = mul(float4(0, 0, 0, 1), view.View);
            ViewValues[first + 4u] = mul(float4(1, 0, 0, 0), view.Projection);
            ViewValues[first + 5u] = mul(float4(0, 1, 0, 0), view.Projection);
            ViewValues[first + 6u] = mul(float4(0, 0, 1, 0), view.Projection);
            ViewValues[first + 7u] = mul(float4(0, 0, 0, 1), view.Projection);
            ViewValues[first + 8u] = float4(view.TileSizeX, view.TileSizeY, view.TileCountX, view.TileCountY);
            ViewValues[first + 9u] = view.ZParams;
            ViewValues[first + 10u] = float4(view.DepthSliceCount, view.LightEntriesPerCell,
                view.DirectionalCount, view.NonDirectionalCount);
            ViewValues[first + 11u] = float4(view.ScreenWidth, view.ScreenHeight,
                view.CellCountPerView, view.GroupsPerLight);
        }
        """;
}
