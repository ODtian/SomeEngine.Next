using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.GI;

public sealed partial class ScreenProbeFeature
{
    private void CreateBuffers(ref RenderGraphEdit edit)
    {
        AddBuffer(ref edit, "GiProbeSurfaces", _probeCount * 2u, 64);
        AddBuffer(ref edit, "GiProbeRadiance", _probeCount * 2u * 9u, 16);
        AddBuffer(ref edit, "GiFilteredProbeRadiance", _probeCount * 2u * 9u, 16);
        AddBuffer(ref edit, "GiProbeSamples", _probeCount * 2u, 8);
        AddBuffer(ref edit, "GiProbeTileRanges", (NearTileCount + 1u) * 2u, 8);
        AddBuffer(ref edit, "GiProbeTileCounts", NearTileCount, 4);
        AddBuffer(ref edit, "GiProbeReprojection", _probeCount, 4);
        AddBuffer(ref edit, "GiProbeRequests", NearTileCount * 2u, 4);
        AddBuffer(ref edit, "GiPixelHistory", (uint)_width * (uint)_height * _views * 2u, 16);
        AddBuffer(ref edit, "GiPixelSurfaces", (uint)_width * (uint)_height * _views * 2u, 32);
        AddBuffer(ref edit, "GiScreenHits", _rayCount, 16);
        AddBuffer(ref edit, "GiProbeIncoming", _probeCount, 16);
        AddBuffer(ref edit, "GiGlobalFields", 1, 64);
        AddBuffer(ref edit, "GiGlobalDistances", GridSide * GridSide * GridSide, 4);
        AddBuffer(ref edit, "GiStaticDistances", GridSide * GridSide * GridSide, 4);
        AddBuffer(ref edit, "GiInstanceMovement", 32, 4);
        AddBuffer(ref edit, "GiGlobalFieldChanges", 1, 4);
        _buffers.Add("GiGlobalDistanceWrites", _buffers["GiGlobalDistances"]);
        AddBuffer(ref edit, "GiRays", Math.Max(_rayCount, (uint)_width * (uint)_height * _views), 32);
        AddBuffer(ref edit, "GiMeshQueries", _candidateCapacity, 32);
        AddBuffer(ref edit, "GiNearObjectWords", NearTileCount, 4);
        AddBuffer(ref edit, "GiCandidates", _candidateCapacity, 32);
        AddBuffer(ref edit, "GiCandidateHits", _candidateCapacity, 16);
        AddBuffer(ref edit, "GiSurfaceSamples", _candidateCapacity, 80);
        AddBuffer(ref edit, "GiTrainingSurfaces", TrainingCount, 80);
        AddBuffer(ref edit, "GiClosestDistances", _rayCount, 4);
        AddBuffer(ref edit, "GiClosestCandidates", _rayCount, 4);
        AddBuffer(ref edit, "GiQueueCounts", 2, 4);
        AddBuffer(ref edit, "GiMaterialWorkCounts", 1, 4);
        AddBuffer(ref edit, "GiMaterialWorkWrites", 1, 4);
        AddBuffer(ref edit, "GiMaterialWorkRanges", 1, 8);
        AddBuffer(ref edit, "GiMaterialWorkIndices", _candidateCapacity, 4);
        AddBuffer(ref edit, "GiMaterialDispatch", 3, 4, BufferUsages.Indirect);
        AddBuffer(ref edit, "GiRadianceKeys", HashCount, 4);
        AddBuffer(ref edit, "GiRadianceSums", HashCount, 16);
        AddBuffer(ref edit, "GiRadianceValues", HashCount, 16);
        AddBuffer(ref edit, "GiRadianceTraining", TrainingCount, 48);
        AddBuffer(ref edit, "GiTrainingProbes", TrainingCount, 4);
        AddBuffer(ref edit, "GiTrainingWork", 1, 4);
        AddBuffer(ref edit, "GiTrainingDispatch", 15, 4, BufferUsages.Indirect);
        AddBuffer(ref edit, "GiQueryDispatch", 3, 4, BufferUsages.Indirect);
        AddBuffer(ref edit, "GiNeuralWeights", 899, 4);
        AddBuffer(ref edit, "GiNeuralMoments", 899, 8);
        AddBuffer(ref edit, "GiNeuralActivations", TrainingCount * 96u, 4);
        AddBuffer(ref edit, "GiNeuralGradients", 899 * 8, 4);
        AddBuffer(ref edit, "GiNeuralUpdates", 1, 4);
    }

    private void InitializeBuffers(ref RenderGraphEdit edit)
    {
        GraphBufferId[] buffers = _buffers.Values.Select(value => value.Id).Distinct().ToArray();
        _clearBuffers = edit.AddCopyFramePass("Initialize GI buffers", PassQueueSelection.AnyOfType(QueueType.Graphics), buffers,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition pass, ref GraphBufferId[] values) =>
            {
                foreach (GraphBufferId buffer in values)
                    pass.Write(buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in GraphBufferId[] values) =>
            {
                foreach (GraphBufferId buffer in values) commands.ClearBuffer(commands.GetBuffer(buffer), BufferRange.Whole);
            }, region: _sceneRegion);
        GraphPassId cache = AddPass(ref edit, "InitializeGiRadianceCache", HashCount, initializesBuffers: true);
        _initializeProbes = AddPass(ref edit, "InitializeGiScreenProbes", _probeCount * 2u, initializesBuffers: true);
        _resets.Add(cache);
        edit.OrderAfter(cache, _clearBuffers);
        edit.OrderAfter(_initializeProbes, cache);
    }

    private void Connect(ref RenderGraphEdit edit)
    {
        _trainingRegion = edit.AddRegion("GI sparse radiance training", _traceRegion);
        edit.OrderAfter(ClearSurfacesPass, _initializeProbes);
        edit.OrderAfter(_surfaces.Geometry.CompletePass, ClearSurfacesPass);
        GraphPassId last = AddPass(ref edit, "BeginGiGlobalUpdate", 1, threads: 1);
        _fieldPasses.Add(last);
        edit.OrderAfter(last, _surfaces.Geometry.CompletePass);
        edit.OrderAfter(last, _scene!.UploadPass);
        _markMoving = Then(ref edit, last, "UpdateGiInstanceMovement", 64);
        _fieldPasses.Add(_markMoving);
        last = Then(ref edit, _markMoving, "BuildGiGlobalBounds", 64);
        _fieldPasses.Add(last);
        last = Then(ref edit, last, "ClearGiStaticDistances", GridSide * GridSide * GridSide);
        _fieldPasses.Add(last);
        _composeStatic = Then(ref edit, last, "ComposeGiStaticDistances", 64);
        _fieldPasses.Add(_composeStatic);
        last = Then(ref edit, _composeStatic, "CopyGiStaticDistances", GridSide * GridSide * GridSide);
        _fieldPasses.Add(last);
        _composeField = Then(ref edit, last, "ComposeGiGlobalDistances", 64);
        _fieldPasses.Add(_composeField);
        last = ConnectEmitters(ref edit, _composeField);
        last = Then(ref edit, last, "ClassifyGiProbeTiles", NearTileCount * 64u);
        last = Then(ref edit, last, "AllocateGiProbeTiles", 1, threads: 1);
        last = Then(ref edit, last, "ClearGiProbeSources", _probeCount);
        last = Then(ref edit, last, "PlaceGiScreenProbes", NearTileCount * 64u);
        last = Then(ref edit, last, "ClearGiProbeReprojection", _probeCount);
        for (int attempt = 0; attempt < 4; attempt++) last = Then(ref edit, last, "ScatterGiProbeReprojection", _probeCount);
        last = Then(ref edit, last, "ReprojectGiScreenProbes", _probeCount * 64u);
        last = Then(ref edit, last, "TraceGiScreenProbes", _probeCount);
        last = Then(ref edit, last, "BuildGiNearObjectWords", NearTileCount * 64u);
        _queries[0] = AddQueries(ref edit, last, 0);
        last = Then(ref edit, _queries[0].Finish, "IntegrateGiScreenProbes", _probeCount);
        last = Then(ref edit, last, "FilterGiScreenProbes", _probeCount * 64u);
        GraphPassId filteredProbes = last;
        last = Then(ref edit, last, "ClearGiRadianceTraining", TrainingCount, stage: 1);
        last = Then(ref edit, last, "SelectGiTrainingProbes", TrainingCount, stage: 1);
        last = Then(ref edit, last, "ContinueGiTrainingRays", TrainingCount, stage: 1);
        _queries[1] = AddQueries(ref edit, last, 1);
        last = Then(ref edit, _queries[1].Finish, "PrepareGiRadianceTraining", 1, stage: 1, threads: 1);
        last = ThenIndirect(ref edit, last, "UpdateGiHashedRadiance", "GiTrainingDispatch", 0, 1);
        last = ThenIndirect(ref edit, last, "ResolveGiHashedRadiance", "GiTrainingDispatch", 12, 1);
        last = ThenIndirect(ref edit, last, "EvaluateGiNeuralTraining", "GiTrainingDispatch", 24, 1);
        last = ThenIndirect(ref edit, last, "DifferentiateGiNeuralTraining", "GiTrainingDispatch", 36, 1);
        last = ThenIndirect(ref edit, last, "UpdateGiNeuralRadiance", "GiTrainingDispatch", 48, 1);
        last = ConnectEmissive(ref edit, filteredProbes, last);
        _complete = Then(ref edit, last, "IntegrateGiPixelRadiance", (uint)_width * (uint)_height * _views);
        GraphReadbackId capacity = edit.AddReadback(_buffers["GiQueueCounts"].Id,
            new BufferRange(4, 4), new CandidateReadback(_candidateCapacity), "GI triangle work capacity", _traceRegion);
        edit.OrderAfter(capacity, _complete);
        edit.OrderAfter(_surfaces.Geometry.CompletePageRequests, _complete);
    }

    private QueryPasses AddQueries(ref RenderGraphEdit edit, GraphPassId previous, int stage)
    {
        GraphPassId begin = Then(ref edit, previous, "BeginGiTriangleQueries", Math.Max(_rayCount, _materialCapacity), stage);
        GraphPassId last = Then(ref edit, begin, "CollectGiMeshQueries", stage == 0 ? _probeCount : TrainingCount, stage);
        last = Then(ref edit, last, "BuildGiQueryDispatch", 1, stage, 1);
        last = Then(ref edit, last, "BuildGiMaterialWorkRanges", 1, stage, 1);
        GraphPassId triangles = ThenIndirect(ref edit, last, "ScatterGiMeshQueries", "GiQueryDispatch", 0, stage);
        GraphPassId reduce = ThenIndirect(ref edit, triangles, "ReduceGiCandidateDistances", "GiQueryDispatch", 0, stage);
        last = ThenIndirect(ref edit, reduce, "SelectGiClosestCandidates", "GiQueryDispatch", 0, stage);
        GraphPassId clear = Then(ref edit, last, "ClearGiMaterialWork", _materialCapacity, stage);
        last = Then(ref edit, clear, "CountGiSurfaceWork", _rayCount, stage);
        last = Then(ref edit, last, "BuildGiMaterialWorkRanges", 1, stage, 1);
        GraphPassId surfaces = Then(ref edit, last, "ScatterGiSurfaceWork", _rayCount, stage);
        GraphPassId finish = Then(ref edit, surfaces, stage == 0 ? "ResolveGiProbeRays" : "FinishGiRadianceTraining",
            stage == 0 ? _probeCount : TrainingCount, stage);
        return new(begin, clear, triangles, reduce, surfaces, finish);
    }

    private GraphPassId Then(ref RenderGraphEdit edit, GraphPassId previous, string entry, uint count, int stage = 0, uint threads = 64)
    {
        GraphPassId pass = AddPass(ref edit, entry, count, stage, threads);
        edit.OrderAfter(pass, previous);
        return pass;
    }

    private GraphPassId ThenIndirect(ref RenderGraphEdit edit, GraphPassId previous, string entry, string arguments, ulong offset, int stage)
    {
        GraphPassId pass = AddPass(ref edit, entry, 0, stage, indirect: new GiIndirectDispatch(_dispatchLayout!, _buffers[arguments].Id, offset));
        edit.OrderAfter(pass, previous);
        return pass;
    }

    private GraphPassId AddPass(ref RenderGraphEdit edit, string entry, uint count, int stage = 0, uint threads = 64,
        bool initializesBuffers = false, GiIndirectDispatch? indirect = null)
    {
        if (!_pipelines.TryGetValue(entry, out LinkedComputePipeline? pipeline))
        {
            pipeline = LinkedComputePipeline.Create(_backend, _device, _shader!, entry, entry);
            _pipelines.Add(entry, pipeline);
        }
        string label = stage == 0 ? entry : $"{entry} training {stage}";
        GraphRegionId region = initializesBuffers ? _sceneRegion : stage == 0 ? _traceRegion : _trainingRegion;
        GraphPassId id = indirect is { } dispatch
            ? edit.AddComputeFramePass(label, PassQueueSelection.AnyOfType(QueueType.Graphics), dispatch,
                new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
                pipeline.Pipeline, pipeline.ParameterLayout, default, GiIndirectDispatch.Declare, GiIndirectDispatch.Record, region: region)
            : edit.AddComputeFramePass(label, PassQueueSelection.AnyOfType(QueueType.Graphics),
            new DispatchArguments((count + threads - 1u) / threads, 1, 1),
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            pipeline.Pipeline, pipeline.ParameterLayout, default,
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data),
            region: region);
        PassDefinition pass = edit.GetPass(id);
        int target = _shader!.RequireSlangTarget(_device.Capabilities.CompileTarget);
        var resources = ShaderResourceLayout.Resolve<(string Name, ResourceBindingType Type, bool Used)>(pipeline.ParameterLayout,
            static (name, type, used) => (name, type, used),
            (category, space, register) => _shader.IsSlangParameterLocationUsed(target, entry, category, space, register));
        foreach ((string name, ResourceBindingType type, bool used) in resources)
        {
            if (!used) { pass.Bind(GraphParameterResourceBinding.Null(type)); continue; }
            if (initializesBuffers && type == ResourceBindingType.BufferUav)
            {
                BindBuffer(ref edit, ref pass, name, type, PipelineSync.ComputeShading,
                    GraphAccessMode.Write, WriteCoverage.Complete);
                continue;
            }
            if ((name == "GiEmitterTriangleTree" && entry != "BuildGiEmitterTriangleTrees") ||
                (name == "GiEmitterObjectTree" && entry != "BuildGiEmitterObjectTree") ||
                (entry == "IntegrateGiPixelRadiance" && name is "GiEmitterChanges" or "GiInstanceMovement" or
                    "GiProbeSamples" or "GiProbeSurfaces" or "GiFilteredProbeRadiance"))
                BindBuffer(ref edit, ref pass, name, type, PipelineSync.ComputeShading, GraphAccessMode.Read);
            else BindSceneResource(ref edit, ref pass, name, type, PipelineSync.ComputeShading, stage);
        }
        return id;
    }

    private readonly record struct GiIndirectDispatch(IndirectCommandLayout Layout, GraphBufferId Arguments, ulong Offset)
    {
        internal static void Declare(ref PassDefinition pass, ref GiIndirectDispatch data) =>
            pass.Read(data.Arguments, new BufferRange(data.Offset, 12), PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument);
        internal static void Record(ref ComputePassCommandScope commands, in GiIndirectDispatch data) =>
            commands.ExecuteIndirect(data.Layout, new BufferRegion(commands.GetBuffer(data.Arguments), new BufferRange(data.Offset, 12)), 1);
    }

    private void AddBuffer(ref RenderGraphEdit edit, string name, uint count, uint stride, BufferUsages extra = default)
    {
        BufferUsages usages = BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | extra;
        GraphBufferId id = edit.CreatePersistentBuffer(new BufferDesc((ulong)count * stride, usages, name));
        _buffers.Add(name, new(id, stride, usages));
    }

    private void ResizeMaterialBuffers(ref RenderGraphEdit edit, uint count)
    {
        foreach (string name in new[] { "GiMaterialWorkCounts", "GiMaterialWorkWrites", "GiMaterialWorkRanges", "GiMaterialDispatch" })
        {
            BufferData buffer = _buffers[name];
            edit.SetBufferDescription(buffer.Id, new BufferDesc((ulong)count * buffer.Stride * (name == "GiMaterialDispatch" ? 3u : 1u), buffer.Usages, name));
        }
    }

    private sealed class CandidateReadback(uint capacity) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag)
        {
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0u)
                throw new InvalidOperationException($"GI triangle work exceeded its {capacity} entry capacity.");
        }
    }
}
