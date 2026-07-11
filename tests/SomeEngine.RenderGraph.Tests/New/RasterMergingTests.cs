using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RasterMergingTests
{
    private static readonly PassExecution Noop = static (ICommandContext _, in PassResources _) => { };

    [Fact]
    public void Adjacent_identical_load_continuation_forms_one_raster_record_unit()
    {
        using Device device = new();
        FrozenGraph frozen = FreezePair(device, BreakMode.None, out TextureHandle first, out TextureHandle second);
        try
        {
            CompiledGraph compiled = Compiler.Compile(
                frozen,
                device.Compilation,
                optimized: true,
                enableTransientAliasing: false,
                enableRenderPassMerging: true);

            CompiledRecordUnit unit = Assert.Single(compiled.RecordUnits);
            Assert.Equal(CompiledRecordUnitKind.RasterScope, unit.Kind);
            Assert.Equal([0, 1], unit.LogicalPassOrdinals);
            Assert.Single(compiled.ExecutionBatches);
            Assert.True(compiled.Raster.Enabled);
            Assert.Equal(2, compiled.Raster.LiveRasterPasses);
            Assert.Equal(1, compiled.Raster.CandidateScopes);
            Assert.Equal(2, compiled.Raster.MergedLogicalPasses);
        }
        finally
        {
            Destroy(device, first, second);
        }
    }

    [Theory]
    [InlineData(BreakMode.Clear, (int)RasterMergeBreakReason.LoadAction)]
    [InlineData(BreakMode.Attachment, (int)RasterMergeBreakReason.AttachmentSet)]
    [InlineData(BreakMode.Barrier, (int)RasterMergeBreakReason.Barrier)]
    [InlineData(BreakMode.RecordingLane, (int)RasterMergeBreakReason.RecordingLane)]
    public void Semantic_boundaries_report_a_stable_first_break_reason(
        BreakMode mode,
        int expected)
    {
        using Device device = new();
        FrozenGraph frozen = FreezePair(device, mode, out TextureHandle first, out TextureHandle second);
        try
        {
            CompiledGraph compiled = Compiler.Compile(
                frozen,
                device.Compilation,
                optimized: true,
                enableTransientAliasing: false,
                enableRenderPassMerging: true);

            Assert.Equal(2, compiled.RecordUnits.Length);
            Assert.All(compiled.RecordUnits, unit => Assert.Equal(CompiledRecordUnitKind.Standalone, unit.Kind));
            Assert.Equal(1, compiled.Raster.BreakReasonCounts[expected]);
            Assert.Equal(0, compiled.Raster.MergedLogicalPasses);
        }
        finally
        {
            Destroy(device, first, second);
        }
    }

    [Fact]
    public void Default_compilation_skips_candidate_analysis_and_keeps_standalone_lowering()
    {
        using Device device = new();
        FrozenGraph frozen = FreezePair(device, BreakMode.None, out TextureHandle first, out TextureHandle second);
        try
        {
            CompiledGraph compiled = Compiler.Compile(frozen, device.Compilation, optimized: true);

            Assert.False(new RenderGraphOptions().EnableRenderPassMerging);
            Assert.False(compiled.Raster.Enabled);
            Assert.Equal(0, compiled.Raster.CandidateScopes);
            Assert.Equal(2, compiled.RecordUnits.Length);
            Assert.All(compiled.RecordUnits, unit => Assert.Equal(CompiledRecordUnitKind.Standalone, unit.Kind));
        }
        finally
        {
            Destroy(device, first, second);
        }
    }

    [Fact]
    public void First_cross_queue_import_readiness_in_continuation_keeps_a_submission_boundary()
    {
        using Device device = new();
        TextureDesc textureDesc = new(8, 8, Format.R8G8B8A8UNorm, TextureUsage.ColorAttachment);
        TextureHandle textureHandle = device.CreateTexture(textureDesc);
        BufferDesc bufferDesc = new(16, BufferUsage.ShaderRead);
        BufferHandle bufferHandle = device.CreateBuffer(bufferDesc);
        GpuCompletion readiness;
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy))
        {
            CommandListHandle list = commands.Finish();
            readiness = device.Submit(QueueType.Copy, [list], []);
        }

        try
        {
            GraphRecording recording = new();
            TextureId texture = recording.AddTexture(textureDesc, new ImportedTexture(
                textureHandle,
                device.GetTextureMetadata(textureHandle),
                TextureUse.ColorAttachment,
                TextureUse.ColorAttachment,
                ContentsAvailable: false));
            TextureViewId view = recording.AddTextureView(
                texture,
                TextureSubresourceRange.WholeColor,
                TextureViewUsage.ColorAttachment,
                Format.Unknown,
                null);
            BufferId buffer = recording.AddBuffer(bufferDesc, new ImportedBuffer(
                bufferHandle,
                device.GetBufferMetadata(bufferHandle),
                BufferUse.ShaderRead,
                BufferUse.ShaderRead,
                ContentsAvailable: true,
                [readiness]));

            int first = recording.AddPass("first", QueueSelection.Graphics);
            _ = recording.AddColorAttachment(first, 0, view, LoadAction.Clear, default);
            recording.SetExecution(first, Noop);

            int second = recording.AddPass("second", QueueSelection.Graphics);
            _ = recording.AddColorAttachment(second, 0, view, LoadAction.Load, default);
            _ = recording.AddBufferAccess(
                second,
                buffer,
                ResourceEffect.Read,
                BufferUse.ShaderRead,
                BufferRange.Whole,
                PriorContents.Required,
                WriteCoverage.Partial);
            recording.SetExecution(second, Noop);

            FrozenGraph frozen = recording.Freeze(device);
            CompiledGraph compiled = Compiler.Compile(
                frozen,
                device.Compilation,
                optimized: true,
                enableTransientAliasing: false,
                enableRenderPassMerging: true);

            Assert.Equal(2, compiled.RecordUnits.Length);
            Assert.Equal(1, compiled.Raster.BreakReasonCounts[(int)RasterMergeBreakReason.ExternalReadiness]);
            Assert.Equal(0, compiled.Raster.MergedLogicalPasses);

            FrozenGraph detached = frozen.DetachForCompilation();
            GpuCompletion detachedReadiness = Assert.Single(detached.Resources[1].ImportedBuffer.Readiness!);
            Assert.Equal(QueueType.Copy, detachedReadiness.Queue);
            Assert.False(detachedReadiness.IsValid);
            CompiledGraph optimized = Compiler.Compile(
                detached,
                device.Compilation,
                optimized: true,
                enableTransientAliasing: false,
                enableRenderPassMerging: true);
            Assert.Equal(2, optimized.RecordUnits.Length);
            Assert.Equal(1, optimized.Raster.BreakReasonCounts[(int)RasterMergeBreakReason.ExternalReadiness]);
            Assert.Equal(0, optimized.Raster.MergedLogicalPasses);
        }
        finally
        {
            device.DestroyBuffer(bufferHandle);
            device.DestroyTexture(textureHandle);
            device.CollectGarbage();
        }
    }

    [Fact]
    public void Published_merged_scope_executes_both_callbacks_and_preserves_clear_output()
    {
        using Device device = new();
        TextureDesc textureDesc = new(
            4,
            4,
            Format.R32Float,
            TextureUsage.ColorAttachment | TextureUsage.CopySource,
            Name: "merged-output");
        TextureHandle texture = device.CreateTexture(textureDesc);
        Transition(device, texture.Resource, ResourceState.Common, ResourceState.CopySource);
        TextureCopyRegion region = new(0, 0, TextureAspect.Color, 4, 4);
        TextureCopyFootprint footprint = device.GetTextureCopyFootprint(textureDesc, region);
        BufferHandle readback = device.CreateBuffer(
            new BufferDesc(footprint.RequiredBufferSize, BufferUsage.CopyDestination),
            MemoryType.Readback);
        try
        {
            using RenderGraph graph = new(device, new RenderGraphOptions
            {
                EnableRenderPassMerging = true,
            });
            int callbacks = 0;
            bool selectedMerged = false;
            for (int attempt = 0; attempt < 64 && !selectedMerged; attempt++)
            {
                Statistics before = device.Statistics;
                GraphBuilder builder = graph.Begin();
                TextureId imported = builder.ImportTexture(
                    texture,
                    TextureUse.CopySource,
                    TextureUse.CopySource,
                    contentsAvailable: true);
                TextureViewId view = builder.CreateTextureView(
                    imported,
                    TextureSubresourceRange.WholeColor,
                    TextureViewUsage.ColorAttachment);
                BufferId output = builder.ImportBuffer(
                    readback,
                    BufferUse.CopyDestination,
                    BufferUse.CopyDestination,
                    contentsAvailable: false);

                PassBuilder clear = builder.AddPass("clear", QueueSelection.Graphics);
                _ = clear.ColorAttachment(0, view, LoadAction.Clear, new System.Numerics.Vector4(0.25f));
                clear.Execute((ICommandContext _, in PassResources _) => Interlocked.Increment(ref callbacks));

                PassBuilder load = builder.AddPass("load", QueueSelection.Graphics);
                _ = load.ColorAttachment(0, view, LoadAction.Load);
                load.Execute((ICommandContext _, in PassResources _) => Interlocked.Increment(ref callbacks));

                PassBuilder copy = builder.AddPass("readback", QueueSelection.Copy);
                TextureAccess source = copy.Read(imported, TextureUse.CopySource);
                BufferAccess destination = copy.Write(output, BufferUse.CopyDestination);
                copy.Execute((ICommandContext commands, in PassResources resources) => commands.CopyTextureToBuffer(
                    new TextureBufferCopy(
                        resources.Get(source),
                        region,
                        resources.Get(destination),
                        footprint.Layout)));

                GraphExecution execution = graph.Execute(ref builder);
                Assert.True(execution.Wait(TimeSpan.FromSeconds(2)));
                if (!graph.Statistics.LastRaster.Enabled) continue;

                selectedMerged = true;
                Statistics after = device.Statistics;
                Assert.Equal(3, after.Submissions - before.Submissions);
                Assert.Equal(3, after.SubmittedCommandLists - before.SubmittedCommandLists);
                Assert.Equal(2, graph.Statistics.LastRaster.MergedLogicalPasses);
            }

            Assert.True(selectedMerged);
            Assert.True(Volatile.Read(ref callbacks) >= 2);
            byte[] bytes = new byte[checked((int)footprint.RequiredBufferSize)];
            device.ReadBuffer(readback, 0, bytes);
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int offset = checked((int)(y * footprint.Layout.BytesPerRow + x * sizeof(float)));
                Assert.Equal(0.25f, BitConverter.ToSingle(bytes, offset));
            }
        }
        finally
        {
            device.DestroyBuffer(readback);
            device.DestroyTexture(texture);
            device.CollectGarbage();
        }
    }

    private static FrozenGraph FreezePair(
        Device device,
        BreakMode mode,
        out TextureHandle firstHandle,
        out TextureHandle secondHandle)
    {
        GraphRecording recording = new();
        TextureDesc desc = new(8, 8, Format.R8G8B8A8UNorm, TextureUsage.ColorAttachment);
        firstHandle = device.CreateTexture(desc);
        TextureId first = recording.AddTexture(desc, new ImportedTexture(
            firstHandle,
            device.GetTextureMetadata(firstHandle),
            TextureUse.ColorAttachment,
            TextureUse.ColorAttachment,
            ContentsAvailable: false));
        TextureViewId firstView = recording.AddTextureView(
            first,
            TextureSubresourceRange.WholeColor,
            TextureViewUsage.ColorAttachment,
            Format.Unknown,
            null);

        secondHandle = default;
        TextureViewId secondView = firstView;
        if (mode == BreakMode.Attachment)
        {
            secondHandle = device.CreateTexture(desc);
            TextureId second = recording.AddTexture(desc, new ImportedTexture(
                secondHandle,
                device.GetTextureMetadata(secondHandle),
                TextureUse.ColorAttachment,
                TextureUse.ColorAttachment,
                ContentsAvailable: false));
            secondView = recording.AddTextureView(
                second,
                TextureSubresourceRange.WholeColor,
                TextureViewUsage.ColorAttachment,
                Format.Unknown,
                null);
        }

        BufferId boundary = default;
        if (mode == BreakMode.Barrier)
            boundary = recording.AddBuffer(new BufferDesc(16, BufferUsage.ShaderWrite), default);

        int firstPass = recording.AddPass("first", QueueSelection.Graphics);
        _ = recording.AddColorAttachment(firstPass, 0, firstView, LoadAction.Clear, default);
        if (mode == BreakMode.Barrier)
        {
            _ = recording.AddBufferAccess(
                firstPass,
                boundary,
                ResourceEffect.Write,
                BufferUse.ShaderWrite,
                BufferRange.Whole,
                PriorContents.Discard,
                WriteCoverage.Full);
        }
        recording.SetExecution(firstPass, Noop);

        int secondPass = recording.AddPass(
            "second",
            QueueSelection.Graphics,
            mode == BreakMode.RecordingLane ? PassRecordingLane.Coordinator : PassRecordingLane.Worker);
        _ = recording.AddColorAttachment(
            secondPass,
            0,
            secondView,
            mode is BreakMode.Clear or BreakMode.Attachment ? LoadAction.Clear : LoadAction.Load,
            default);
        if (mode == BreakMode.Barrier)
        {
            _ = recording.AddBufferAccess(
                secondPass,
                boundary,
                ResourceEffect.Read,
                BufferUse.ShaderWrite,
                BufferRange.Whole,
                PriorContents.Required,
                WriteCoverage.Partial);
        }
        recording.SetExecution(secondPass, Noop);
        return recording.Freeze(device);
    }

    private static void Transition(Device device, ResourceHandle resource, ResourceState before, ResourceState after)
    {
        using ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics);
        commands.Barriers([ResourceBarrier.Transition(resource, before, after, TextureSubresourceRange.WholeColor)]);
        CommandListHandle list = commands.Finish();
        GpuCompletion completion = device.Submit(QueueType.Graphics, [list], []);
        Assert.True(device.Wait(completion, TimeSpan.FromSeconds(2)));
    }

    private static void Destroy(Device device, TextureHandle first, TextureHandle second)
    {
        if (second.IsValid) device.DestroyTexture(second);
        device.DestroyTexture(first);
        device.CollectGarbage();
    }

    public enum BreakMode : byte
    {
        None,
        Clear,
        Attachment,
        Barrier,
        RecordingLane,
    }
}
