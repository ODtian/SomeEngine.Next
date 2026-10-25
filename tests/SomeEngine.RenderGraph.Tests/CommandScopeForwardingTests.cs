using System.Numerics;
using SomeEngine.Tests;

namespace SomeEngine.RenderGraph.Tests;

public sealed class CommandScopeForwardingTests
{
    [Fact]
    public void RasterVertexBindingsReachBackendAfterDeclarationChecks()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Buffer first = support.Backend.CreateBuffer(support.Device, new BufferDesc(64, BufferUsages.Vertex));
        using Buffer second = support.Backend.CreateBuffer(support.Device, new BufferDesc(96, BufferUsages.Vertex));
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        Exception? emptyError = null, declaredError = null;
#if DEBUG
        Exception? laterError = null;
#endif
        bool recorded = false;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId firstId = RegisterBuffer(edit, first);
            GraphBufferId secondId = RegisterBuffer(edit, second);
            GraphColorAttachmentViewId target = CreateTarget(edit);
            edit.AddRasterPass<byte, byte>("Raster Vertex declarations",
                PassQueueSelection.Exact(support.GraphicsQueue), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                (ref PassDefinition access, ref byte _) =>
                {
                    access.ColorAttachment(0, target, LoadType.Clear, StoreType.Store,
                        WriteCoverage.Complete, Vector4.Zero);
                    access.Read(firstId, new BufferRange(16, 32), PipelineSync.Draw,
                        ResourceAccess.VertexBuffer);
                    access.Read(secondId, new BufferRange(32, 16), PipelineSync.Draw,
                        ResourceAccess.VertexBuffer);
                },
                (ref RasterPassCommandScope commands, in byte _, in byte _) =>
                {
                    recorded = true;
                    try { commands.SetVertexBuffers(uint.MaxValue, []); }
                    catch (Exception error) { emptyError = error; }
                    try
                    {
                        commands.SetVertexBuffers(uint.MaxValue,
                            [new(first, 16, 4, 32), new(second, 32, 8, 16)]);
                    }
                    catch (Exception error) { declaredError = error; }
#if DEBUG
                    // The later operand is passed directly: GetBuffer must not reject it first.
                    try
                    {
                        commands.SetVertexBuffers(uint.MaxValue,
                            [new(first, 16, 4, 32), new(second, 48, 8, 16)]);
                    }
                    catch (Exception error) { laterError = error; }
#endif
                });
            edit.Commit();
        }
        Execute(graph, support);
        Assert.True(recorded);
        Assert.Equal("bindings", Assert.IsType<ArgumentOutOfRangeException>(emptyError).ParamName);
        Assert.Equal("bindings", Assert.IsType<ArgumentOutOfRangeException>(declaredError).ParamName);
#if DEBUG
        Assert.IsType<InvalidOperationException>(laterError);
#endif
    }

    [Fact]
    public void RasterStreamOutputBindingsReachBackendAfterDeclarationChecks()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Buffer first = support.Backend.CreateBuffer(support.Device, new BufferDesc(64, BufferUsages.StreamOutput));
        using Buffer second = support.Backend.CreateBuffer(support.Device, new BufferDesc(96, BufferUsages.StreamOutput));
        using Buffer filled = support.Backend.CreateBuffer(support.Device, new BufferDesc(32, BufferUsages.StreamOutput));
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        Exception? emptyError = null, declaredError = null;
        Exception? filledError = null;
#if DEBUG
        Exception? laterError = null;
        Exception? missingFilledError = null;
#endif
        bool recorded = false;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId firstId = RegisterBuffer(edit, first);
            GraphBufferId secondId = RegisterBuffer(edit, second);
            GraphBufferId filledId = RegisterBuffer(edit, filled);
            GraphColorAttachmentViewId target = CreateTarget(edit);
            edit.AddRasterPass<byte, byte>("Raster StreamOutput declarations",
                PassQueueSelection.Exact(support.GraphicsQueue), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                (ref PassDefinition access, ref byte _) =>
                {
                    access.ColorAttachment(0, target, LoadType.Clear, StoreType.Store,
                        WriteCoverage.Complete, Vector4.Zero);
                    access.Write(firstId, new BufferRange(16, 32), PipelineSync.Draw,
                        ResourceAccess.StreamOutput, WriteCoverage.Complete);
                    access.Write(secondId, new BufferRange(32, 16), PipelineSync.Draw,
                        ResourceAccess.StreamOutput, WriteCoverage.Complete);
                    access.Write(filledId, new BufferRange(8, sizeof(uint)), PipelineSync.Draw,
                        ResourceAccess.StreamOutput, WriteCoverage.Complete);
                },
                (ref RasterPassCommandScope commands, in byte _, in byte _) =>
                {
                    recorded = true;
                    try { commands.SetStreamOutputBuffers(uint.MaxValue, []); }
                    catch (Exception error) { emptyError = error; }
                    try
                    {
                        commands.SetStreamOutputBuffers(uint.MaxValue,
                            new StreamOutputBufferBinding[] { new(first, 16, 32), new(second, 32, 16) });
                    }
                    catch (Exception error) { declaredError = error; }
                    try
                    {
                        commands.SetStreamOutputBuffers(uint.MaxValue,
                            new StreamOutputBufferBinding[] { new(first, 16, 32), new(second, 32, 16, filled, 8) });
                    }
                    catch (Exception error) { filledError = error; }
#if DEBUG
                    // The later operand is passed directly: GetBuffer must not reject it first.
                    try
                    {
                        commands.SetStreamOutputBuffers(uint.MaxValue,
                            new StreamOutputBufferBinding[] { new(first, 16, 32), new(second, 48, 16) });
                    }
                    catch (Exception error) { laterError = error; }
                    try
                    {
                        commands.SetStreamOutputBuffers(uint.MaxValue,
                            new StreamOutputBufferBinding[] { new(first, 16, 32), new(second, 32, 16, filled, 16) });
                    }
                    catch (Exception error) { missingFilledError = error; }
#endif
                });
            edit.Commit();
        }
        Execute(graph, support);
        Assert.True(recorded);
        Assert.Equal("bindings", Assert.IsType<ArgumentOutOfRangeException>(emptyError).ParamName);
        Assert.Equal("bindings", Assert.IsType<ArgumentOutOfRangeException>(declaredError).ParamName);
        Assert.Equal("bindings", Assert.IsType<ArgumentOutOfRangeException>(filledError).ParamName);
#if DEBUG
        Assert.IsType<InvalidOperationException>(laterError);
        Assert.IsType<InvalidOperationException>(missingFilledError);
#endif
    }

    [Fact]
    public void GeneralVertexBindingsReachBackendAfterDeclarationChecks()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Buffer first = support.Backend.CreateBuffer(support.Device, new BufferDesc(64, BufferUsages.Vertex));
        using Buffer second = support.Backend.CreateBuffer(support.Device, new BufferDesc(96, BufferUsages.Vertex));
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        Exception? emptyError = null, declaredError = null;
#if DEBUG
        Exception? laterError = null;
#endif
        bool recorded = false;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId firstId = RegisterBuffer(edit, first);
            GraphBufferId secondId = RegisterBuffer(edit, second);
            edit.AddGeneralPass<byte, byte>("General Vertex declarations",
                PassQueueSelection.Exact(support.GraphicsQueue), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                (ref PassDefinition access, ref byte _) =>
                {
                    access.Read(firstId, new BufferRange(16, 32), PipelineSync.Draw,
                        ResourceAccess.VertexBuffer);
                    access.Read(secondId, new BufferRange(32, 16), PipelineSync.Draw,
                        ResourceAccess.VertexBuffer);
                },
                (ref GeneralPassCommandScope commands, in byte _, in byte _) =>
                {
                    recorded = true;
                    try { commands.SetVertexBuffers(uint.MaxValue, []); }
                    catch (Exception error) { emptyError = error; }
                    try
                    {
                        commands.SetVertexBuffers(uint.MaxValue,
                            [new(first, 16, 4, 32), new(second, 32, 8, 16)]);
                    }
                    catch (Exception error) { declaredError = error; }
#if DEBUG
                    // The later operand is passed directly: GetBuffer must not reject it first.
                    try
                    {
                        commands.SetVertexBuffers(uint.MaxValue,
                            [new(first, 16, 4, 32), new(second, 48, 8, 16)]);
                    }
                    catch (Exception error) { laterError = error; }
#endif
                });
            edit.Commit();
        }
        Execute(graph, support);
        Assert.True(recorded);
        Assert.Equal("bindings", Assert.IsType<ArgumentOutOfRangeException>(emptyError).ParamName);
        Assert.Equal("bindings", Assert.IsType<ArgumentOutOfRangeException>(declaredError).ParamName);
#if DEBUG
        Assert.IsType<InvalidOperationException>(laterError);
#endif
    }

    [Fact]
    public void GeneralStreamOutputBindingsReachBackendAfterDeclarationChecks()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using Buffer first = support.Backend.CreateBuffer(support.Device, new BufferDesc(64, BufferUsages.StreamOutput));
        using Buffer second = support.Backend.CreateBuffer(support.Device, new BufferDesc(96, BufferUsages.StreamOutput));
        using Buffer filled = support.Backend.CreateBuffer(support.Device, new BufferDesc(32, BufferUsages.StreamOutput));
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        Exception? emptyError = null, declaredError = null;
        Exception? filledError = null;
#if DEBUG
        Exception? laterError = null;
        Exception? missingFilledError = null;
#endif
        bool recorded = false;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId firstId = RegisterBuffer(edit, first);
            GraphBufferId secondId = RegisterBuffer(edit, second);
            GraphBufferId filledId = RegisterBuffer(edit, filled);
            edit.AddGeneralPass<byte, byte>("General StreamOutput declarations",
                PassQueueSelection.Exact(support.GraphicsQueue), 0,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                (ref PassDefinition access, ref byte _) =>
                {
                    access.Write(firstId, new BufferRange(16, 32), PipelineSync.Draw,
                        ResourceAccess.StreamOutput, WriteCoverage.Complete);
                    access.Write(secondId, new BufferRange(32, 16), PipelineSync.Draw,
                        ResourceAccess.StreamOutput, WriteCoverage.Complete);
                    access.Write(filledId, new BufferRange(8, sizeof(uint)), PipelineSync.Draw,
                        ResourceAccess.StreamOutput, WriteCoverage.Complete);
                },
                (ref GeneralPassCommandScope commands, in byte _, in byte _) =>
                {
                    recorded = true;
                    try { commands.SetStreamOutputBuffers(uint.MaxValue, []); }
                    catch (Exception error) { emptyError = error; }
                    try
                    {
                        commands.SetStreamOutputBuffers(uint.MaxValue,
                            new StreamOutputBufferBinding[] { new(first, 16, 32), new(second, 32, 16) });
                    }
                    catch (Exception error) { declaredError = error; }
                    try
                    {
                        commands.SetStreamOutputBuffers(uint.MaxValue,
                            new StreamOutputBufferBinding[] { new(first, 16, 32), new(second, 32, 16, filled, 8) });
                    }
                    catch (Exception error) { filledError = error; }
#if DEBUG
                    // The later operand is passed directly: GetBuffer must not reject it first.
                    try
                    {
                        commands.SetStreamOutputBuffers(uint.MaxValue,
                            new StreamOutputBufferBinding[] { new(first, 16, 32), new(second, 48, 16) });
                    }
                    catch (Exception error) { laterError = error; }
                    try
                    {
                        commands.SetStreamOutputBuffers(uint.MaxValue,
                            new StreamOutputBufferBinding[] { new(first, 16, 32), new(second, 32, 16, filled, 16) });
                    }
                    catch (Exception error) { missingFilledError = error; }
#endif
                });
            edit.Commit();
        }
        Execute(graph, support);
        Assert.True(recorded);
        Assert.Equal("bindings", Assert.IsType<ArgumentOutOfRangeException>(emptyError).ParamName);
        Assert.Equal("bindings", Assert.IsType<ArgumentOutOfRangeException>(declaredError).ParamName);
        Assert.Equal("bindings", Assert.IsType<ArgumentOutOfRangeException>(filledError).ParamName);
#if DEBUG
        Assert.IsType<InvalidOperationException>(laterError);
        Assert.IsType<InvalidOperationException>(missingFilledError);
#endif
    }

    [Fact]
    public void GeneralResolveWritesTheSelectedDestinationMipAndLayer()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        FormatSupport format = support.Device.Capabilities.GetFormatSupport(Format.R8G8B8A8UNorm);
        const FormatFeatures required = FormatFeatures.MultisampleColorAttachment | FormatFeatures.MultisampleResolve;
        Assert.Equal(required, format.Features & required);
        Assert.True(format.SupportsSampleCount(4));
        using Buffer readback = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(1_024, BufferUsages.CopyDestination), MemoryType.Readback);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        TextureSubresourceRange sourceRange = new(0, 1, 1, 1, TextureAspects.Color);
        TextureSubresourceRange destinationRange = new(1, 1, 2, 1, TextureAspects.Color);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphTextureId source = edit.CreateTransientTexture(new TextureDesc(
                TextureDimension.Texture2D, 4, 4, 1, 1, 2, 4, Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopySource));
            GraphTextureId destination = edit.CreateTransientTexture(new TextureDesc(
                TextureDimension.Texture2D, 8, 8, 1, 2, 3, 1, Format.R8G8B8A8UNorm,
                TextureUsages.ColorAttachment | TextureUsages.CopyDestination | TextureUsages.CopySource));
            GraphBufferId output = edit.RegisterExternalBuffer(readback,
                [new(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
            edit.DeclareGeneralPass("Clear source layer", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition access) => access.Write(source, sourceRange,
                    PipelineSync.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget,
                    WriteCoverage.Complete),
                (ref GeneralPassCommandScope commands) =>
                    commands.ClearTexture(commands.GetTexture(source), sourceRange, new Vector4(1, 0, 0, 1)),
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread));
            // A missing resolve must leave blue pixels, not undefined destination contents.
            edit.DeclareGeneralPass("Clear destination layer", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition access) => access.Write(destination, destinationRange,
                    PipelineSync.RenderTarget, ResourceAccess.RenderTarget, TextureLayout.RenderTarget,
                    WriteCoverage.Complete),
                (ref GeneralPassCommandScope commands) =>
                    commands.ClearTexture(commands.GetTexture(destination), destinationRange, new Vector4(0, 0, 1, 1)),
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread));
            edit.DeclareGeneralPass("Resolve selected mip and layer", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition access) =>
                {
                    access.Read(source, sourceRange, PipelineSync.Resolve,
                        ResourceAccess.ResolveSource, TextureLayout.ResolveSource);
                    access.Write(destination, destinationRange, PipelineSync.Resolve,
                        ResourceAccess.ResolveDestination, TextureLayout.ResolveDestination, WriteCoverage.Complete);
                },
                (ref GeneralPassCommandScope commands) => commands.ResolveTexture(new TextureResolve(
                    commands.GetTexture(source), 0, 1, commands.GetTexture(destination), 1, 2, Format.R8G8B8A8UNorm)),
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread));
            edit.DeclareCopyPass("Read resolved layer", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition access) =>
                {
                    access.Read(destination, destinationRange, PipelineSync.Copy,
                        ResourceAccess.CopySource, TextureLayout.CopySource);
                    access.Write(output, BufferRange.Whole, PipelineSync.Copy,
                        ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                (ref CopyPassCommandScope commands) => commands.CopyTextureToBuffer(new BufferTextureCopy(
                    commands.GetBuffer(output), 0, 256, 4, commands.GetTexture(destination), 1, 2,
                    TextureAspects.Color, 0, 0, 0, 4, 4, 1)),
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread));
            edit.Commit();
        }
        Execute(graph, support);
        using MappedBuffer result = support.Backend.Map(readback, MapType.Read, BufferRange.Whole);
        result.Invalidate(result.Range);
        byte[] actual = new byte[4 * 4 * 4];
        byte[] expected = new byte[actual.Length];
        for (int row = 0; row < 4; row++)
        {
            result.Bytes.Slice(row * 256, 16).CopyTo(actual.AsSpan(row * 16, 16));
            for (int column = 0; column < 4; column++)
            {
                expected[(row * 4 + column) * 4] = 255;
                expected[(row * 4 + column) * 4 + 3] = 255;
            }
        }
        ImageArtifacts.AssertEqual(new TestImage(4, 4, Format.R8G8B8A8UNorm, expected),
            new TestImage(4, 4, Format.R8G8B8A8UNorm, actual));
    }

    private static GraphBufferId RegisterBuffer(RenderGraphEdit edit, Buffer buffer) =>
        edit.RegisterExternalBuffer(buffer,
            [new(BufferRange.Whole, buffer.InitialSync, buffer.InitialAccess, ResourceContentState.Defined)]);

    private static GraphColorAttachmentViewId CreateTarget(RenderGraphEdit edit)
    {
        GraphTextureId texture = edit.CreateTransientTexture(new TextureDesc(
            TextureDimension.Texture2D, 16, 16, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment));
        return edit.CreateColorAttachmentView(texture,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
            Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D);
    }

    private static void Execute(RenderGraph graph, WarpGraphTestSupport support)
    {
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
            frame.Execute(completions);
        support.Wait(completions);
    }
}
