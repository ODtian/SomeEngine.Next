using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features.GI;

public sealed partial class ScreenProbeFeature
{
    private void CreateBuffers(ref RenderGraphEdit edit)
    {
        AddBuffer(ref edit, "GiProbeSurfaces", _probeCount * 2u, 32);
        AddBuffer(ref edit, "GiProbeRadiance", _probeCount * 2u * 9u, 16);
        AddBuffer(ref edit, "GiProbeRequests", _probeCount, 4);
        AddBuffer(ref edit, "GiProbeHistoryData", _probeCount, 8);
        AddBuffer(ref edit, "GiScreenHits", _probeCount, 16);
        AddBuffer(ref edit, "GiProbeIncoming", _probeCount, 16);
        AddBuffer(ref edit, "GiGlobalFields", 1, 64);
        AddBuffer(ref edit, "GiGlobalDistances", GridSide * GridSide * GridSide, 4);
        _buffers.Add("GiGlobalDistanceWrites", _buffers["GiGlobalDistances"]);
        AddBuffer(ref edit, "GiRays", Math.Max(_rayCount, (uint)_width * (uint)_height * _views), 32);
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
        AddBuffer(ref edit, "GiRadianceTraining", TrainingCount * 2u, 48);
        AddBuffer(ref edit, "GiNeuralWeights", 899, 4);
        AddBuffer(ref edit, "GiNeuralMoments", 899, 8);
        AddBuffer(ref edit, "GiNeuralActivations", TrainingCount * 2u * 96u, 4);
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
        edit.OrderAfter(ClearSurfacesPass, _initializeProbes);
        edit.OrderAfter(_surfaces.Geometry.CompletePass, ClearSurfacesPass);
        GraphPassId last = AddPass(ref edit, "BuildGiGlobalBounds", 64);
        _fieldPasses.Add(last);
        edit.OrderAfter(last, _surfaces.Geometry.CompletePass);
        edit.OrderAfter(last, _scene!.UploadPass);
        last = Then(ref edit, last, "ClearGiGlobalDistances", GridSide * GridSide * GridSide);
        _fieldPasses.Add(last);
        _composeField = Then(ref edit, last, "ComposeGiGlobalDistances", 64);
        _fieldPasses.Add(_composeField);
        last = Then(ref edit, _composeField, "TraceGiScreenProbes", _probeCount);
        last = Then(ref edit, last, "CreateGiTrainingRays", TrainingCount);
        _queries[0] = AddQueries(ref edit, last, 0);
        last = Then(ref edit, _queries[0].Finish, "ContinueGiTrainingRays", TrainingCount);
        _queries[1] = AddQueries(ref edit, last, 1);
        last = Then(ref edit, _queries[1].Finish, "UpdateGiHashedRadiance", TrainingCount * 2u);
        last = Then(ref edit, last, "ResolveGiHashedRadiance", HashCount);
        last = Then(ref edit, last, "EvaluateGiNeuralTraining", TrainingCount * 2u, threads: 32);
        last = Then(ref edit, last, "DifferentiateGiNeuralTraining", 899 * 8);
        last = Then(ref edit, last, "BeginGiNeuralUpdate", 1, threads: 1);
        last = Then(ref edit, last, "UpdateGiNeuralRadiance", 899);
        last = Then(ref edit, last, "IntegrateGiScreenProbes", _probeCount);
        _complete = ConnectEmissive(ref edit, last);
        GraphReadbackId capacity = edit.AddReadback(_buffers["GiQueueCounts"].Id,
            new BufferRange(4, 4), new CandidateReadback(_candidateCapacity), "GI triangle work capacity", _traceRegion);
        edit.OrderAfter(capacity, _complete);
        edit.OrderAfter(_surfaces.Geometry.CompletePageRequests, _complete);
    }

    private QueryPasses AddQueries(ref RenderGraphEdit edit, GraphPassId previous, int stage)
    {
        GraphPassId begin = Then(ref edit, previous, "BeginGiTriangleQueries", Math.Max(_rayCount, _materialCapacity), stage);
        GraphPassId last = Then(ref edit, begin, "CollectGiTriangleCandidates", stage == 0 ? _rayCount : TrainingCount, stage);
        last = Then(ref edit, last, "BuildGiMaterialWorkRanges", 1, stage, 1);
        GraphPassId triangles = Then(ref edit, last, "ScatterGiTriangleCandidates", _candidateCapacity, stage);
        GraphPassId reduce = Then(ref edit, triangles, "ReduceGiCandidateDistances", _candidateCapacity, stage);
        last = Then(ref edit, reduce, "SelectGiClosestCandidates", _candidateCapacity, stage);
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

    private GraphPassId AddPass(ref RenderGraphEdit edit, string entry, uint count, int stage = 0, uint threads = 64, bool initializesBuffers = false)
    {
        if (!_pipelines.TryGetValue(entry, out LinkedComputePipeline? pipeline))
        {
            pipeline = LinkedComputePipeline.Create(_backend, _device, _shader!, entry, entry);
            _pipelines.Add(entry, pipeline);
        }
        GraphPassId id = edit.AddComputeFramePass(entry, PassQueueSelection.AnyOfType(QueueType.Graphics),
            new DispatchArguments((count + threads - 1u) / threads, 1, 1),
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            pipeline.Pipeline, pipeline.ParameterLayout, default,
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments data) => commands.Dispatch(data),
            region: initializesBuffers ? _sceneRegion : _traceRegion);
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
            if (name is "PageHeap" or "GiHierarchy") _geometryReaders.Add(id);
            BindSceneResource(ref edit, ref pass, name, type, PipelineSync.ComputeShading, stage);
        }
        return id;
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
