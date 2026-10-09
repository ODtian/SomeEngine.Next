using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private void InstallLeafFlagFixups(ref RenderGraphEdit graph)
    {
        GraphData.LeafFlagRegion = graph.AddRegion("Cluster streaming fixups", _regions.PrimaryGeometry);
        GraphData.LeafFlagPass = AddComputePass(ref graph, "Apply Cluster streaming leaf flags", _pipelines!.LeafFlagFixups,
            new ClusterDispatchParameters(new DispatchArguments(0, 1, 1)), 0, ClusterDispatchParameters.Record,
            region: GraphData.LeafFlagRegion);
        GraphData.LeafFlagParameters = graph.CreateBufferInput(BufferUsages.Constant);
        GraphData.LeafFlagData = graph.CreateBufferInput(BufferUsages.ShaderRead);
        GraphData.LeafFlagEmptyData = CreateBufferData(ref graph, ClusterBytes.Zero(8), BufferUsages.ShaderRead, "Cluster empty fixup records");
        GraphData.LeafFlagEmptyParameters = CreateBufferData(ref graph, ClusterBytes.Zero(16), BufferUsages.Constant, "Cluster empty fixup parameters");
        PassDefinition pass = graph.GetPass(GraphData.LeafFlagPass);
        _ = pass.Bind(GraphData.LeafFlagParameters, PipelineSync.ComputeShading);
        _ = pass.Bind(graph.CreateBufferSrv(GraphData.LeafFlagData, structureStride: 8), PipelineSync.ComputeShading);
        pass.Bind(GraphParameterResourceBinding.WritableBufferDescriptor(graph.CreateBufferUav(GraphData.Resources.PageHeap)));
        graph.OrderAfter(GraphData.LeafFlagPass, GraphData.PageUpload);
        graph.OrderAfter(GraphData.SceneBounds.Pass, GraphData.LeafFlagPass);
    }

    private void BindLeafFlagFixups(ref RenderGraphFrame graph, ClusterResidencyPublication? residencyUpdates)
    {
        ReadOnlySpan<ClusterLeafFlagFixup> data = residencyUpdates?.Meshes.LeafFlagFixups ?? [];
        if (data.IsEmpty)
        {
            graph.SetBufferInput(GraphData.LeafFlagParameters, new GraphBufferSlice(GraphData.LeafFlagEmptyParameters, BufferRange.Whole));
            graph.SetBufferInput(GraphData.LeafFlagData, new GraphBufferSlice(GraphData.LeafFlagEmptyData, BufferRange.Whole));
            graph.SetRegionEnabled(GraphData.LeafFlagRegion, false);
            return;
        }
        Span<uint> parameters = stackalloc uint[4];
        parameters.Clear(); parameters[0] = checked((uint)data.Length);
        graph.SetBufferInput(GraphData.LeafFlagParameters, graph.Upload(parameters, BufferUsages.Constant,
            "Cluster streaming fixup parameters"));
        graph.SetBufferInput(GraphData.LeafFlagData, graph.Upload(data,
            BufferUsages.ShaderRead, "Cluster streaming fixup records"));
        graph.SetPassData(GraphData.LeafFlagPass, new ClusterDispatchParameters(new DispatchArguments(
            (uint)((data.Length + 63) / 64), 1, 1)));
        graph.SetRegionEnabled(GraphData.LeafFlagRegion, !data.IsEmpty);
        PassDefinition pass = graph.GetPass(GraphData.LeafFlagPass);
        uint[] addresses = data.ToArray().Select(static fixup => fixup.ClusterByteAddress + 60u).ToArray();
        Array.Sort(addresses);
        uint first = addresses[0], end = first + sizeof(uint);
        for (int index = 1; index <= addresses.Length; index++)
        {
            if (index < addresses.Length && addresses[index] <= end + PackedCluster.SizeInBytes - sizeof(uint))
            {
                end = addresses[index] + sizeof(uint);
                continue;
            }
            _ = pass.ReadWrite(GraphData.Resources.PageHeap, new BufferRange(first, end - first),
                PipelineSync.ComputeShading, ResourceAccess.UnorderedAccess);
            if (index < addresses.Length) { first = addresses[index]; end = first + sizeof(uint); }
        }
    }
}
