using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

#if DEBUG
public sealed class AtomicMergeableAccessTests
{


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
