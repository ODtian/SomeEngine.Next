using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private sealed class CullWork
    {
        internal GraphBufferId Nodes, Clusters, NodeReady, ClusterReady, Arguments;
        internal GraphRegionId Initialization;
        internal GraphPassId Initialize;
        internal GraphPassId[] Phases = new GraphPassId[4];
        internal bool Initialized;
        internal uint FramePhases;
    }

    private CullWork CreateCullWork(ref RenderGraphEdit graph, ulong capacity)
    {
        var work = new CullWork();
        work.Nodes = Create(ref graph, "Cluster node queue", capacity * TraversalNodeStride);
        work.Clusters = Create(ref graph, "Cluster cluster queue", capacity * CandidateStride);
        work.NodeReady = Create(ref graph, "Cluster node readiness", capacity * sizeof(uint));
        work.ClusterReady = Create(ref graph, "Cluster batch readiness", ((capacity + 63u) / 64u) * sizeof(uint));
        work.Arguments = Create(ref graph, "Cluster work queue counts", TraversalArgumentStride, BufferUsages.Indirect);
        work.Initialization = graph.AddRegion("Initialize Cluster work queues", _regions.PrimaryGeometry);
        work.Initialize = graph.AddCopyFramePass("Initialize Cluster work queue data",
            PassQueueSelection.AnyOfType(QueueType.Graphics),
            new[] { work.Nodes, work.Clusters, work.NodeReady, work.ClusterReady },
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            static (ref PassDefinition pass, ref GraphBufferId[] buffers) =>
            {
                foreach (GraphBufferId buffer in buffers)
                    pass.Write(buffer, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in GraphBufferId[] buffers) =>
            {
                foreach (GraphBufferId id in buffers)
                {
                    var buffer = commands.GetBuffer(id);
                    commands.ClearBuffer(buffer, BufferRange.Whole);
                }
            }, region: work.Initialization);
        return work;

        static GraphBufferId Create(ref RenderGraphEdit graph, string label, ulong bytes, BufferUsages extra = BufferUsages.None) =>
            graph.CreatePersistentBuffer(new BufferDesc(Math.Max(bytes, 4),
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopyDestination | extra, label));
    }

    private GraphPassId RecordPersistentCull(ref RenderGraphEdit graph, in FrameResources frame,
        GraphBufferId views, uint mode, GraphBufferId drawArguments,
        GraphBufferId base0, GraphBufferId base1, GraphBufferId base2, bool target,
        int phase, string label, GraphRegionId region)
    {
        uint maximumViews = target ? _renderContext.MaximumInvocationCount : frame.ViewCount;
        ClusterComputePipeline traversal = _pipelines!.GetTraversal(target, maximumViews > 1u);
        GraphBufferSlice parameters = UploadUniform(ref graph,
            new ClusterTraversalLevelUniforms { MaxNodes = _options.MaxCandidates, TraversalMode = mode },
            $"Cluster {label} work parameters");
        var dispatch = new ClusterDispatchParameters(new DispatchArguments(1, 1, 1));
        GraphPassId id = graph.AddComputeFramePass($"Cull Cluster {label} work", PassQueueSelection.AnyOfType(QueueType.Graphics),
            dispatch, new PassOptions(RepeatCommands: true), traversal.Pipeline, traversal.ParameterLayout,
            default, static (ref PassDefinition _, ref ClusterDispatchParameters _) => { },
            ClusterDispatchParameters.Record, region: region);
        PassDefinition pass = graph.GetPass(id);
        var inputs = ShaderResourceLayout.Resolve<(string Name, ResourceBindingType Type)>(
            traversal.ParameterLayout, static (name, type) => (name, type));
        bool filters = false;
        foreach (var input in inputs)
        {
            if (input.Name.StartsWith("RasterTarget", StringComparison.Ordinal))
            {
                if (filters) continue;
                if (target) _renderContext.FilterResourcesBinder(ref graph, ref pass, PipelineSync.ComputeShading);
                else BindNeutralTargetPolicyFilterResources(ref graph, ref pass);
                filters = true;
                continue;
            }
            switch (input.Name)
            {
                case "Views": Read(ref graph, ref pass, views, ClusterViewUniforms.SizeInBytes); break;
                case "GlobalBVH": pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(CreateReadOnlyBufferView(graph, frame.Bvh))); break;
                case "PageHeap": pass.Bind(GraphParameterResourceBinding.ReadOnlyBufferDescriptor(CreateReadOnlyBufferView(graph, frame.PageHeap))); break;
                case "InstanceData": Read(ref graph, ref pass, frame.InstanceData); break;
                case "InstanceProperties": pass.Bind(graph.CreateBufferCbv(frame.InstanceProperties, frame.InstancePropertiesRange), PipelineSync.ComputeShading); break;
                case "MaterialBounds": Read(ref graph, ref pass, frame.MaterialBounds, sizeof(uint)); break;
                case "CandidateArgs": Write(ref graph, ref pass, frame.CandidateArgs); break;
                case "CandidateClusters": Write(ref graph, ref pass, frame.CandidateClusters, CandidateStride); break;
                case "CandidateCount": Write(ref graph, ref pass, frame.CandidateCount, sizeof(uint)); break;
                case "PageFaultBuffer": Write(ref graph, ref pass, frame.PageFaults); break;
                case "TraversalLevel": pass.Bind(graph.CreateBufferCbv(parameters.Buffer, parameters.Range), PipelineSync.ComputeShading); break;
                case "TraversalNodes": Write(ref graph, ref pass, frame.Work.Nodes, TraversalNodeStride); break;
                case "NodeIndirectArgs": Write(ref graph, ref pass, frame.Work.Arguments); break;
                case "TraversalReady": Write(ref graph, ref pass, frame.Work.NodeReady, sizeof(uint)); break;
                case "ClusterBatchReady": Write(ref graph, ref pass, frame.Work.ClusterReady, sizeof(uint)); break;
                case "WorkStatus": BindWorkStatus(ref graph, ref pass); break;
                case "HierarchyHiZ": pass.Bind(graph.CreateTextureSrv(mode == 2u ? frame.CurrentHiZ : frame.PreviousHiZ,
                    format: Format.R32Float, dimension: TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading); break;
                case "DeferredWork": Write(ref graph, ref pass, frame.Phase2CandidateClusters, CandidateStride, GraphAccessMode.Write); break;
                case "DeferredWorkCount": Write(ref graph, ref pass, frame.Phase2CandidateCount, sizeof(uint)); break;
                case "DeferredWorkDispatchArgs": Write(ref graph, ref pass, frame.Phase2CandidateArgs); break;
                case "PolicyClusterViews": BindPolicyClusterViews(ref graph, ref pass, PipelineSync.ComputeShading, target); break;
                case "DrawArgs": Write(ref graph, ref pass, drawArguments); break;
                case "BaseDrawArgs0": Read(ref graph, ref pass, base0); break;
                case "BaseDrawArgs1": Read(ref graph, ref pass, base1); break;
                case "BaseDrawArgs2": Read(ref graph, ref pass, base2); break;
                case "VisibleClusters": Write(ref graph, ref pass, frame.VisibleClusters, VisibleClusterStride, phase % 2 == 0 ? GraphAccessMode.Write : GraphAccessMode.ReadWrite); break;
                default: throw new InvalidDataException($"Cluster work input '{input.Name}' has no resource.");
            }
        }
        frame.Work.Phases[phase] = id;
        return id;

        static void Read(ref RenderGraphEdit graph, ref PassDefinition pass, GraphBufferId buffer, uint stride = 0) =>
            pass.Bind(graph.CreateBufferSrv(buffer, structureStride: stride), PipelineSync.ComputeShading);
        static void Write(ref RenderGraphEdit graph, ref PassDefinition pass, GraphBufferId buffer, uint stride = 0, GraphAccessMode access = GraphAccessMode.ReadWrite) =>
            pass.Bind(graph.CreateBufferUav(buffer, structureStride: stride), PipelineSync.ComputeShading,
                access, WriteCoverage.Partial, ResourceContentState.Defined,
                stride is CandidateStride or TraversalNodeStride ? BufferContentScope.CountBoundedPrefix : BufferContentScope.FullRange);
    }
}
