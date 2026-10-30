using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster.Pipeline;

/// <summary>A material's existing scalar-table index and user shader interpretation.</summary>
public readonly record struct ClusterMaterialEntry(uint Index, Material Material, string VertexEncoding);

public sealed partial class ClusterPipelineSystem
{
    public uint MaterialIndexCapacity => _materialAssignments.Current.SlotCapacity;

    /// <summary>Enumerates the material indices used by the current rendering-world instances.</summary>
    public IEnumerable<ClusterMaterialEntry> EnumerateMaterials()
    {
        ClusterMaterialAssignmentSet assignments = _materialAssignments.Current;
        foreach (int index in assignments.ActiveOffsets)
        {
            assignments.TryGetEntry(index, out Material material, out string encoding);
            yield return new((uint)index, material, encoding);
        }
    }

    /// <summary>Connects a material compute entry to the same geometry, scalar and texture resources as surface shading.</summary>
    /// <remarks>Finish the batch by calling <see cref="ReconcileMaterialResources"/>.</remarks>
    public GraphPassId AddMaterialComputePass(ref RenderGraphEdit graph, uint materialIndex,
        Shader shader, string entryPoint, GraphBufferId dispatchArguments, ulong dispatchOffset,
        IGraphResourceProvider resources, GraphRegionId region = default)
    {
        _materialAssignments.Current.TryGetEntry((int)materialIndex, out Material material, out string encoding);
        ClusterShadeInvocation stage = _shadeStages!.ResolveCompute(material, encoding, shader, entryPoint, resources);
        var dispatch = new ClusterIndirectDispatchParameters(RequireDispatchIndirectLayout(), dispatchArguments, dispatchOffset);
        GraphPassId id = graph.AddComputeFramePass($"{entryPoint} material {materialIndex}",
            PassQueueSelection.AnyOfType(QueueType.Graphics), dispatch,
            new PassOptions(Culling: PassCullingMode.NeverCull, RepeatCommands: true),
            stage.Pipeline.Pipeline, stage.Pipeline.ParameterLayout, default,
            static (ref PassDefinition pass, ref ClusterIndirectDispatchParameters data) =>
                _ = pass.Read(data.Arguments, new BufferRange(data.Offset, ClusterIndirectAbi.DispatchBytes),
                    PipelineSync.ExecuteIndirect, ResourceAccess.IndirectArgument),
            ClusterIndirectDispatchParameters.Record, region: region);
        PassDefinition access = graph.GetPass(id);
        BindMaterialComputeInputs(ref graph, ref access, stage, default);
        foreach (uint property in stage.Scalars.TextureProperties)
        {
            MaterialInput input = GetMaterialInput(ref graph, material, property,
                material.Properties[(int)property].Name, ResourceBindingType.TextureSrv);
            access.Read(input.Input, PipelineSync.ComputeShading);
        }
        return id;
    }

    /// <summary>Refreshes material buffers and textures after a batch of compute passes has changed.</summary>
    public void ReconcileMaterialResources(ref RenderGraphEdit graph)
    {
        var resources = UpdateMaterialResources(ref graph);
        GraphData.MaterialBuffers = resources.Buffers;
        AcceptMaterialTextures(resources.Textures, GraphData.MissingMaterialTexture);
    }

    /// <summary>Orders material compute after this frame's geometry builds.</summary>
    public void OrderMaterialGeometryBuilds(ref RenderGraphFrame graph, GraphPassId pass)
    {
        foreach (GraphPassId build in _resources.GeometryBuilds(graph.FrameIdentity)) graph.OrderAfter(pass, build);
    }
}
