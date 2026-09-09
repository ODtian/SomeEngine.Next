using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

#if DEBUG
public sealed class AtomicMergeableAccessTests
{
    [Fact]
    public void AtomicWritersFanOutFromInitializationAndFanIntoReader()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphBufferId buffer;
        GraphPassId clear;
        GraphPassId first;
        GraphPassId second;
        GraphPassId resolve;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.CreateTransientBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination | BufferUsages.ShaderRead | BufferUsages.ShaderWrite));
            clear = AddAccess(
                edit,
                support.GraphicsQueue,
                "clear",
                buffer,
                GraphAccessMode.Write,
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                WriteCoverage.Complete,
                UnorderedAccessHazardMode.Ordered);
            first = AddAccess(
                edit,
                support.GraphicsQueue,
                "atomic first",
                buffer,
                GraphAccessMode.ReadWrite,
                PipelineSync.ComputeShading,
                ResourceAccess.UnorderedAccess,
                WriteCoverage.Partial,
                UnorderedAccessHazardMode.AtomicMergeable);
            second = AddAccess(
                edit,
                support.GraphicsQueue,
                "atomic second",
                buffer,
                GraphAccessMode.ReadWrite,
                PipelineSync.PixelShading,
                ResourceAccess.UnorderedAccess,
                WriteCoverage.Partial,
                UnorderedAccessHazardMode.AtomicMergeable);
            resolve = AddAccess(
                edit,
                support.GraphicsQueue,
                "resolve",
                buffer,
                GraphAccessMode.Read,
                PipelineSync.ComputeShading,
                ResourceAccess.ShaderResource,
                WriteCoverage.Partial,
                UnorderedAccessHazardMode.Ordered);
            edit.Commit();
        }

        GraphDependencyEdge[] edges = graph.StructureIndex.HazardTimelines[buffer.Value].Edges;
        Assert.Contains(edges, edge => edge.Predecessor == clear.Value && edge.Consumer == first.Value);
        Assert.Contains(edges, edge => edge.Predecessor == clear.Value && edge.Consumer == second.Value);
        Assert.DoesNotContain(edges, edge =>
            edge.Predecessor == first.Value && edge.Consumer == second.Value);
        Assert.DoesNotContain(edges, edge =>
            edge.Predecessor == second.Value && edge.Consumer == first.Value);
        Assert.Contains(edges, edge => edge.Predecessor == first.Value && edge.Consumer == resolve.Value);
        Assert.Contains(edges, edge => edge.Predecessor == second.Value && edge.Consumer == resolve.Value);
    }

    [Fact]
    public void AtomicMergeableRejectsAnythingExceptPartialReadWriteUav()
    {
        Assert.Throws<ArgumentException>(() =>
            GraphParameterResourceBinding.WritableBuffer(
                default,
                PipelineSync.ComputeShading,
                GraphAccessMode.Write,
                WriteCoverage.Partial,
                unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable));
        Assert.Throws<ArgumentException>(() =>
            GraphParameterResourceBinding.StorageTexture(
                default,
                PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite,
                WriteCoverage.Complete,
                unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable));
        Assert.Throws<ArgumentException>(() =>
            GraphParameterResourceBinding.StorageTexture(
                default,
                PipelineSync.ComputeShading,
                GraphAccessMode.ReadWrite,
                WriteCoverage.Partial,
                layout: TextureLayout.General,
                unorderedAccessHazards: UnorderedAccessHazardMode.AtomicMergeable));
    }

    private static GraphPassId AddAccess(
        RenderGraphEdit edit,
        Queue queue,
        string label,
        GraphBufferId buffer,
        GraphAccessMode mode,
        PipelineSync sync,
        ResourceAccess access,
        WriteCoverage coverage,
        UnorderedAccessHazardMode hazards)
    {
        var state = new AccessState(buffer, mode, sync, access, coverage, hazards);
        return edit.AddComputePass<AccessState, byte>(
            label,
            PassQueueSelection.Exact(queue),
            state,
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition pass, ref AccessState value) =>
            {
                if (value.Mode == GraphAccessMode.Read)
                {
                    _ = pass.Read(
                        value.Buffer,
                        BufferRange.Whole,
                        value.Sync,
                        value.Access);
                    return;
                }
                if (value.Mode == GraphAccessMode.Write)
                {
                    _ = pass.Write(
                        value.Buffer,
                        BufferRange.Whole,
                        value.Sync,
                        value.Access,
                        value.Coverage);
                    return;
                }
                _ = pass.ReadWrite(
                    value.Buffer,
                    BufferRange.Whole,
                    value.Sync,
                    value.Access,
                    unorderedAccessHazards: value.Hazards);
            },
            static (ref ComputePassCommandScope _, in AccessState _, in byte _) => { });
    }

    private readonly record struct AccessState(
        GraphBufferId Buffer,
        GraphAccessMode Mode,
        PipelineSync Sync,
        ResourceAccess Access,
        WriteCoverage Coverage,
        UnorderedAccessHazardMode Hazards);
}
#endif
