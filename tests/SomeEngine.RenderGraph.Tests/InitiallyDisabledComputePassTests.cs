using System.Buffers;
using SlangShaderSharp;

namespace SomeEngine.RenderGraph.Tests;

public sealed class InitiallyDisabledComputePassTests
{
    [Fact]
    public void DisabledComputePassesStayIdleAcrossEditsAndCancellationUntilEnabled()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var shader = new EmptyComputeShader(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.ComputeQueue]);
        var observation = new FrameObservation();
        int recorded = 0;
        GraphPassId first, second;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            first = AddDisabled(edit, "First disabled compute");
            edit.Commit();
        }

        for (int frameNumber = 0; frameNumber < 2; frameNumber++)
        {
            observation.Clear();
            using RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: observation.Capture));
            frame.ObserveExecution(observation);
            Execute(frame, graph, support);
            observation.AssertDisabled(1);
            Assert.Equal(0, recorded);
        }

        ulong previousVersion = graph.StructureVersion;
        observation.Clear();
        using (RenderGraphFrame canceled = graph.BeginFrame(new(Diagnostics: observation.Capture)))
        {
            canceled.ObserveExecution(observation);
            using (RenderGraphEdit edit = graph.BeginEdit())
            {
                second = AddDisabled(edit, "Second disabled compute");
                edit.Commit();
            }
            Assert.Equal(previousVersion, canceled.StructureVersion);
            Assert.Equal(previousVersion + 1, graph.StructureVersion);
        }
        Assert.Empty(observation.Completed);
        Assert.Equal(0, recorded);

        observation.Clear();
        using (RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: observation.Capture)))
        {
            Assert.Equal(previousVersion + 1, frame.StructureVersion);
            frame.ObserveExecution(observation);
            Execute(frame, graph, support);
        }
        observation.AssertDisabled(2);
        Assert.Equal(0, recorded);

        observation.Clear();
        using (RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: observation.Capture)))
        {
            frame.SetPassEnabled(first, true);
            frame.SetPassEnabled(second, true);
            frame.ObserveExecution(observation);
            Execute(frame, graph, support);
        }
        Assert.Equal(2, recorded);
        Assert.Equal(2, observation.Passes.Length);
        Assert.All(observation.Passes, pass => { Assert.True(pass.Enabled); Assert.True(pass.Live); });
        Assert.Contains(first, observation.Completed);
        Assert.Contains(second, observation.Completed);
        Assert.Equal(2, observation.Completed.Count);

        GraphPassId AddDisabled(RenderGraphEdit edit, string label) => edit.AddComputeFramePass(
            label, PassQueueSelection.AnyOfType(QueueType.Compute), new DispatchArguments(1, 1, 1),
            new PassOptions(Culling: PassCullingMode.NeverCull), shader.Pipeline, shader.GlobalLayout, default,
            static (ref PassDefinition _, ref DispatchArguments _) => { }, initiallyEnabled: false,
            callback: (ref ComputePassCommandScope commands, in DispatchArguments args) =>
            {
                recorded++;
                commands.Dispatch(args);
            });
    }

    [Fact]
    public void OriginalComputePassOverloadWithDefaultRegionStartsEnabled()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var shader = new EmptyComputeShader(support);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.ComputeQueue]);
        var observation = new FrameObservation();
        int recorded = 0;
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            pass = edit.AddComputeFramePass("Original compute overload", PassQueueSelection.AnyOfType(QueueType.Compute),
                new DispatchArguments(1, 1, 1), new PassOptions(Culling: PassCullingMode.NeverCull),
                shader.Pipeline, shader.GlobalLayout, default,
                static (ref PassDefinition _, ref DispatchArguments _) => { },
                (ref ComputePassCommandScope commands, in DispatchArguments args) =>
                {
                    recorded++;
                    commands.Dispatch(args);
                }, default);
            edit.Commit();
        }
        using (RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: observation.Capture)))
        {
            frame.ObserveExecution(observation);
            Execute(frame, graph, support);
        }
        Assert.Equal(1, recorded);
        RenderGraphPassDiagnostic diagnostic = Assert.Single(observation.Passes);
        Assert.Equal(pass, diagnostic.Id);
        Assert.True(diagnostic.Enabled);
        Assert.True(diagnostic.Live);
        Assert.Equal(pass, Assert.Single(observation.Completed));
    }

    private static void Execute(RenderGraphFrame frame, RenderGraph graph, WarpGraphTestSupport support)
    {
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int count = frame.Execute(completions);
        support.Wait(completions.AsSpan(0, count));
        graph.CollectCompleted();
    }

    private sealed class FrameObservation : IRenderGraphFrameExecutionObserver
    {
        internal RenderGraphPassDiagnostic[] Passes { get; private set; } = [];
        internal List<GraphPassId> Completed { get; } = [];
        internal void Clear() { Passes = []; Completed.Clear(); }
        internal void Capture(in RenderGraphDiagnosticsView view) => Passes = view.Passes.ToArray();
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            foreach (RenderGraphPassDiagnostic pass in Passes)
                if (result.TryGetPassCompletion(pass.Id, out _)) Completed.Add(pass.Id);
        }
        internal void AssertDisabled(int count)
        {
            Assert.Equal(count, Passes.Length);
            Assert.All(Passes, pass => { Assert.False(pass.Enabled); Assert.False(pass.Live); });
            Assert.Empty(Completed);
        }
    }

    private sealed class EmptyComputeShader : IDisposable
    {
        private readonly IGlobalSession _global;
        private readonly ISession _session;
        private readonly IModule _module;
        private readonly IComponentType _linked;
        internal Pipeline Pipeline { get; }
        internal VariableLayoutReflection GlobalLayout { get; }
        internal EmptyComputeShader(WarpGraphTestSupport support)
        {
            Assert.True(Slang.CreateGlobalSession(Slang.ApiVersion, out _global).Succeeded);
            var description = new SessionDesc
            {
                Targets = [new TargetDesc { Format = SlangCompileTarget.Dxil, Profile = _global.FindProfile("sm_6_0") }],
            };
            Assert.True(_global.CreateSession(description, out _session).Succeeded);
            _module = _session.LoadModuleFromSource("InitialCompute", "InitialCompute.slang",
                Slang.CreateBlob("""[shader("compute")][numthreads(1,1,1)] void CheckEnabled() {}"""), out var diagnostics)
                ?? throw new InvalidOperationException(diagnostics?.AsString);
            Assert.True(_module.FindEntryPointByName("CheckEnabled", out IEntryPoint entry).Succeeded);
            Assert.True(_session.CreateCompositeComponentType([_module, entry], out IComponentType combined, out diagnostics).Succeeded,
                diagnostics?.AsString);
            Assert.True(combined.Link(out _linked, out diagnostics).Succeeded, diagnostics?.AsString);
            ShaderReflection reflection = _linked.GetLayout(0, out diagnostics);
            Assert.NotEqual(ShaderReflection.Null, reflection);
            Assert.True(_linked.GetEntryPointCode(0, 0, out ISlangBlob code, out diagnostics).Succeeded, diagnostics?.AsString);
            GlobalLayout = reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            Pipeline = support.Backend.CreateComputePipeline(support.Device,
                new(_linked, reflection, reflection.GetEntryPointByIndex(0), new ReadOnlySequence<byte>(code.Buffer.ToArray())));
        }
        public void Dispose()
        {
            Pipeline.Dispose();
            GC.KeepAlive(_linked);
            GC.KeepAlive(_module);
            GC.KeepAlive(_session);
            GC.KeepAlive(_global);
        }
    }
}
