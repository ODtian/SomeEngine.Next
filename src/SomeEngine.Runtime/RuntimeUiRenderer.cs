using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;
using Texture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Runtime;

internal sealed class RuntimeUiRenderer : IDisposable
{
    private const int FontGraphId = 1;
    private const int InitialVertexCapacity = 10_000;
    private const int InitialIndexCapacity = 20_000;
    private const ulong UniformBufferSize = 256;
    private static readonly TimeSpan ResourceTimeout = TimeSpan.FromSeconds(60);

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly NativeWindow _window;
    private readonly nint _context;
    private nint _iniFilename;
    private AssetRead<Shader>? _shaderRead;
    private LiveShaderProgram? _program;
    private Pipeline? _pipeline;
    private readonly Buffer?[] _vertexBuffers = new Buffer?[2];
    private readonly Buffer?[] _indexBuffers = new Buffer?[2];
    private readonly Buffer?[] _uniformBuffers = new Buffer?[2];
    private readonly QueueCompletion[][] _readiness = [[], []];
    private Texture? _fontTexture;
    private Sampler? _fontSampler;
    private readonly int[] _vertexCapacities = new int[2];
    private readonly int[] _indexCapacities = new int[2];
    private ImDrawDataPtr _pendingDrawData;
    private int _admittedGeneration = -1;
    private int _writeGeneration = -1;
    private int _preferredGeneration;
    private bool _disposed;

    internal RuntimeUiRenderer(
        IGraphicsBackend backend,
        Device device,
        NativeWindow window,
        AssetLoader assets,
        AssetHandle<Shader> shaderHandle,
        Format outputFormat)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        ArgumentNullException.ThrowIfNull(assets);
        if (!shaderHandle.IsValid || shaderHandle.LoadState != AssetLoadState.Ready)
            throw new ArgumentException("The Runtime ImGui shader must be ready.", nameof(shaderHandle));

        _context = ImGui.CreateContext();
        if (_context == 0)
            throw new InvalidOperationException("ImGui failed to create a context.");
        try
        {
            MakeCurrent();
            ImGui.StyleColorsDark();
            ImGuiIOPtr io = ImGui.GetIO();
            _iniFilename = Marshal.StringToCoTaskMemUTF8(
                Path.Combine(AppContext.BaseDirectory, "imgui.ini"));
            unsafe
            {
                io.NativePtr->IniFilename = (byte*)_iniFilename;
            }
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

            _shaderRead = assets.Read(shaderHandle);
            CreatePipeline(_shaderRead.Value, outputFormat);
            UploadFontAtlas(io);
            _fontSampler = _backend.CreateSampler(_device, new SamplerDesc(
                FilterType.Linear,
                FilterType.Linear,
                FilterType.Linear,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                Label: "Runtime ImGui font sampler"));
            for (int generation = 0; generation < 2; generation++)
            {
                _uniformBuffers[generation] = _backend.CreateBuffer(
                    _device,
                    new BufferDesc(
                        UniformBufferSize,
                        BufferUsages.Constant,
                        $"Runtime ImGui transform {generation}"),
                    MemoryType.Upload);
                EnsureBuffers(
                    generation,
                    InitialVertexCapacity,
                    InitialIndexCapacity);
            }
        }
        catch (Exception failure)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Runtime ImGui initialization and cleanup both failed.",
                    failure,
                    cleanupFailure);
            }
            throw;
        }
    }

    internal bool WantCaptureKeyboard
    {
        get
        {
            MakeCurrent();
            return ImGui.GetIO().WantCaptureKeyboard;
        }
    }

    internal bool WantCaptureMouse
    {
        get
        {
            MakeCurrent();
            return ImGui.GetIO().WantCaptureMouse;
        }
    }

    internal void ProcessEvent(in NativeWindowEvent windowEvent, RuntimeInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(input);
        MakeCurrent();
        ImGuiIOPtr io = ImGui.GetIO();
        switch (windowEvent.Kind)
        {
            case NativeWindowEventKind.KeyChanged:
            {
                ImGuiKey key = MapKey(windowEvent.VirtualKey);
                if (key != ImGuiKey.None)
                    io.AddKeyEvent(key, windowEvent.IsDown);
                UpdateModifiers(io, input);
                break;
            }
            case NativeWindowEventKind.TextInput:
                io.AddInputCharacterUTF16(windowEvent.Utf16Character);
                break;
            case NativeWindowEventKind.MouseMoved:
            case NativeWindowEventKind.MouseButtonChanged:
            case NativeWindowEventKind.MouseWheel:
                io.AddMousePosEvent(windowEvent.MousePosition.X, windowEvent.MousePosition.Y);
                if (windowEvent.Kind == NativeWindowEventKind.MouseButtonChanged)
                    io.AddMouseButtonEvent(checked((int)windowEvent.MouseButton), windowEvent.IsDown);
                else if (windowEvent.Kind == NativeWindowEventKind.MouseWheel)
                    io.AddMouseWheelEvent(windowEvent.Wheel.X, windowEvent.Wheel.Y);
                break;
            case NativeWindowEventKind.MouseLeft:
                io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
                break;
            case NativeWindowEventKind.FocusChanged:
                io.AddFocusEvent(windowEvent.Focused);
                UpdateModifiers(io, input);
                break;
        }
    }

    internal void BeginFrame(float deltaSeconds, int width, int height, float dpiScale)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        MakeCurrent();
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new Vector2(width, height);
        io.DisplayFramebufferScale = Vector2.One;
        io.DeltaTime = Math.Clamp(deltaSeconds, 1.0f / 1000.0f, 1.0f / 10.0f);
        io.FontGlobalScale = Math.Clamp(dpiScale, 0.75f, 3.0f);
        ImGui.NewFrame();
    }

    internal void DrawDebugWindow(
        ref bool open,
        ref bool animateScene,
        in RuntimeUiMetrics metrics)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MakeCurrent();
        if (!open)
            return;

        ImGui.SetNextWindowPos(new Vector2(24.0f, 24.0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(420.0f, 330.0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("SomeEngine Runtime", ref open))
        {
            ImGui.End();
            return;
        }

        float milliseconds = metrics.DeltaSeconds * 1000.0f;
        float framesPerSecond = metrics.DeltaSeconds > 1.0e-6f
            ? 1.0f / metrics.DeltaSeconds
            : 0.0f;
        ImGui.Text($"Frame {metrics.FrameIndex}  {milliseconds:F2} ms  {framesPerSecond:F1} FPS");
        ImGui.Text($"Backbuffer {metrics.Width} x {metrics.Height}  DPI {metrics.DpiScale:F2}x");
        ImGui.Text($"TAA jitter ({metrics.JitterPixels.X:F4}, {metrics.JitterPixels.Y:F4}) px");
        ImGui.Text($"Camera ({metrics.CameraPosition.X:F2}, {metrics.CameraPosition.Y:F2}, {metrics.CameraPosition.Z:F2})");
        ImGui.Text(metrics.Focused ? "Window focus: active" : "Window focus: inactive");
        ImGui.Separator();
        _ = ImGui.Checkbox("Animate scene", ref animateScene);
        ImGui.TextUnformatted("Temporal resolve: enabled (8-sample centered Halton)");
        ImGui.Separator();
        ImGui.TextUnformatted("WASD + Space/Ctrl: move camera");
        ImGui.TextUnformatted("Hold right mouse: look");
        ImGui.TextUnformatted("Shift: speed boost");
        ImGui.TextUnformatted("F1: toggle this panel   Esc: close runtime");
        ImGui.End();
    }

    internal void Record(
        global::SomeEngine.RenderGraph.RenderGraph graph,
        Texture target,
        int width,
        int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(graph);
        if (!ReferenceEquals(target.Device, _device))
            throw new ArgumentException("ImGui requires a live target from its device.", nameof(target));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_writeGeneration >= 0)
            throw new InvalidOperationException("The previous Runtime ImGui graph has not been committed or discarded.");

        MakeCurrent();
        ImGui.Render();
        ImDrawDataPtr drawData = ImGui.GetDrawData();
        if (drawData.TotalVtxCount <= 0 || drawData.TotalIdxCount <= 0)
        {
            _admittedGeneration = -1;
            return;
        }

        int generation = _admittedGeneration >= 0
            ? _admittedGeneration
            : AcquireGeneration();
        _admittedGeneration = -1;
        EnsureBuffers(
            generation,
            drawData.TotalVtxCount,
            drawData.TotalIdxCount);
        UploadDrawData(drawData, generation);

        TextureHandle targetTexture = graph.GetImported(target);
        TextureViewHandle targetView = graph.CreateTextureView(
            targetTexture,
            null,
            GraphTextureViewUsage.ColorAttachment,
            name: "Runtime ImGui presentation view");
        BufferHandle vertices = graph.Import(
            _vertexBuffers[generation]!,
            GraphResourceUsage.VertexOrConstantBuffer,
            GraphResourceUsage.VertexOrConstantBuffer,
            _readiness[generation]);
        BufferHandle indices = graph.Import(
            _indexBuffers[generation]!,
            GraphResourceUsage.IndexBuffer,
            GraphResourceUsage.IndexBuffer,
            _readiness[generation]);
        BufferHandle uniform = graph.Import(
            _uniformBuffers[generation]
                ?? throw new InvalidOperationException("The Runtime ImGui uniform buffer is unavailable."),
            GraphResourceUsage.VertexOrConstantBuffer,
            GraphResourceUsage.VertexOrConstantBuffer,
            _readiness[generation]);
        BufferViewHandle uniformView = graph.CreateBufferView(
            uniform,
            new BufferRange(0, UniformBufferSize),
            GraphBindingType.ConstantBuffer,
            name: "Runtime ImGui transform view");
        TextureHandle font = graph.Import(
            _fontTexture!,
            GraphResourceUsage.ShaderResource,
            GraphResourceUsage.ShaderResource);
        TextureViewHandle fontView = graph.CreateTextureView(
            font,
            null,
            GraphTextureViewUsage.ShaderResource,
            name: "Runtime ImGui font atlas view");
        SamplerHandle fontSampler = graph.Import(
            _fontSampler ?? throw new InvalidOperationException("The Runtime ImGui sampler is unavailable."));

        try
        {
            _pendingDrawData = drawData;
            _writeGeneration = generation;
            using IRasterRenderGraphBuilder builder =
                graph.AddRasterRenderPass<RuntimeUiPassData>(
                "Runtime ImGui overlay",
                out RuntimeUiPassData passData,
                flags: PassFlags.NeverParallel);
            passData.Renderer = this;
            passData.Vertices = vertices;
            passData.Indices = indices;
            passData.Width = width;
            passData.Height = height;

            builder.SetPipeline(_pipeline!);
            builder.SetParameterBlock(
                (_program ?? throw new InvalidOperationException(
                    "The Runtime ImGui Slang program is unavailable.")).ParameterLayout);
            builder.SetRenderAttachment(
                targetView,
                0,
                GraphAccess.Write,
                LoadType.Load);
            builder.UseBuffer(
                vertices,
                GraphResourceUsage.VertexOrConstantBuffer,
                GraphAccess.Read);
            builder.UseBuffer(
                indices,
                GraphResourceUsage.IndexBuffer,
                GraphAccess.Read);
            builder.UseBuffer(uniformView);
            builder.UseTexture(fontView);
            builder.UseSampler(fontSampler);
            builder.SetRenderFunc<RuntimeUiPassData>(
                static (data, context) =>
                    data.Renderer.DrawGraphCommands(context, data));
        }
        catch
        {
            _pendingDrawData = default;
            _admittedGeneration = -1;
            _writeGeneration = -1;
            throw;
        }
    }

    internal int AdmitFrameResources()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writeGeneration >= 0)
        {
            throw new InvalidOperationException(
                "The previous Runtime ImGui graph has not been committed or discarded.");
        }
        if (_admittedGeneration >= 0)
            return 1;
        _admittedGeneration = AcquireGeneration(out int availableGenerationCount);
        return availableGenerationCount;
    }

    internal bool TryAdmitFrameResources(
        out int availableGenerationCount,
        out QueueCompletion[] retirementFences)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writeGeneration >= 0)
        {
            throw new InvalidOperationException(
                "The previous Runtime ImGui graph has not been committed or discarded.");
        }
        if (_admittedGeneration >= 0)
        {
            availableGenerationCount = 1;
            retirementFences = [];
            return true;
        }
        if (!TryAcquireGeneration(
                out int generation,
                out availableGenerationCount,
                out retirementFences))
        {
            return false;
        }
        _admittedGeneration = generation;
        return true;
    }

    internal void Commit(ReadOnlySpan<QueueCompletion> completions)
    {
        if (_writeGeneration < 0)
            return;
        _readiness[_writeGeneration] =
            MergeCompletions(_readiness[_writeGeneration], completions);
        _preferredGeneration = 1 - _writeGeneration;
        _pendingDrawData = default;
        _admittedGeneration = -1;
        _writeGeneration = -1;
    }

    internal void Discard()
    {
        _pendingDrawData = default;
        _admittedGeneration = -1;
        _writeGeneration = -1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        List<Exception>? failures = null;
        Destroy(_fontSampler, ref failures);
        _fontSampler = null;
        for (int generation = 0; generation < 2; generation++)
        {
            int capturedGeneration = generation;
            Destroy(_vertexBuffers[generation], ref failures);
            _vertexBuffers[capturedGeneration] = null;
            Destroy(_indexBuffers[generation], ref failures);
            _indexBuffers[capturedGeneration] = null;
            Destroy(_uniformBuffers[generation], ref failures);
            _uniformBuffers[capturedGeneration] = null;
        }
        Destroy(_fontTexture, ref failures);
        _fontTexture = null;
        Destroy(_pipeline, ref failures);
        _pipeline = null;
        Destroy(_program, ref failures);
        _program = null;
        try { _shaderRead?.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
        _shaderRead = null;
        if (_context != 0)
        {
            try { ImGui.DestroyContext(_context); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (_iniFilename != 0)
        {
            Marshal.FreeCoTaskMem(_iniFilename);
            _iniFilename = 0;
        }
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private void CreatePipeline(Shader shader, Format outputFormat)
    {
        _program = LiveShaderProgram.Link(
            shader,
            [
                new LiveShaderEntry("VSMain", LiveShaderStage.Vertex),
                new LiveShaderEntry("PSMain", LiveShaderStage.Pixel),
            ]);
        SomeEngine.Graphics.VertexAttribute[] attributes =
        [
            new(0, 0, Format.R32G32Float, 0),
            new(1, 0, Format.R32G32Float, 8),
            new(2, 0, Format.R8G8B8A8UNorm, 16),
        ];
        VertexBufferLayout[] buffers =
            [new(0, checked((uint)Unsafe.SizeOf<ImDrawVert>()))];
        BlendAttachmentState[] blendAttachments =
        [
            new(
                Enabled: true,
                SourceColor: BlendFactor.SourceAlpha,
                DestinationColor: BlendFactor.OneMinusSourceAlpha,
                ColorOperation: BlendOperation.Add,
                SourceAlpha: BlendFactor.One,
                DestinationAlpha: BlendFactor.OneMinusSourceAlpha,
                AlphaOperation: BlendOperation.Add),
        ];
        var blend = new BlendState(blendAttachments);
        var attachments = new AttachmentFormatSignature([outputFormat], null);
        var description = new GraphicsPipelineDesc(
            _program.Program,
            _program.GetEntryPoint(0),
            _program.GetEntryPoint(1),
            buffers,
            attributes,
            PrimitiveTopology.TriangleList,
            StripCut.Disabled,
            new RasterizerState(Cull: CullType.None),
            new MultisampleState(),
            new DepthStencilState(),
            blend,
            attachments,
            DynamicStates.Viewport | DynamicStates.Scissor,
            "Runtime ImGui pipeline");
        _pipeline = _backend.CreateGraphicsPipeline(_device, description);
    }

    private unsafe void UploadFontAtlas(ImGuiIOPtr io)
    {
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);
        if (pixels is null || width <= 0 || height <= 0 || bytesPerPixel != 4)
            throw new InvalidOperationException("ImGui produced an invalid RGBA32 font atlas.");

        TextureDesc description = new(
            TextureDimension.Texture2D,
            checked((uint)width),
            checked((uint)height),
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination,
            label: "Runtime ImGui font atlas");
        _fontTexture = _backend.CreateTexture(_device, description);
        BufferTextureCopy copy = new(
            null!,
            0,
            0,
            0,
            _fontTexture,
            0,
            0,
            TextureAspects.Color,
            0,
            0,
            0,
            checked((uint)width),
            checked((uint)height),
            1);
        TextureCopyFootprint footprint = _backend.GetTextureCopyFootprint(
            _device,
            description,
            copy);
        byte[] uploadBytes = new byte[checked((int)footprint.TotalSize)];
        ReadOnlySpan<byte> source = new(pixels, checked(width * height * bytesPerPixel));
        int sourceRowBytes = checked(width * bytesPerPixel);
        for (int row = 0; row < height; row++)
        {
            source.Slice(checked(row * sourceRowBytes), sourceRowBytes).CopyTo(
                uploadBytes.AsSpan(
                    checked((int)(footprint.Offset + (ulong)row * footprint.RowPitch)),
                    sourceRowBytes));
        }

        Buffer upload = _backend.CreateBuffer(
            _device,
            new BufferDesc(footprint.TotalSize, BufferUsages.CopySource, "Runtime ImGui font upload"),
            MemoryType.Upload);
        try
        {
            WriteMappedBuffer(upload, uploadBytes);
            using CommandContext commands = _backend.CreateCommandContext(
                _device,
                new CommandContextDesc(
                    QueueType.Graphics,
                    0,
                    1,
                    Label: "Upload Runtime ImGui font atlas"));
            _backend.Begin(commands);
            bool recording = true;
            RecordedCommands recorded;
            try
            {
                TextureSubresourceRange textureRange =
                    new(0, 1, 0, 1, TextureAspects.Color);
                _backend.Barrier(commands, new TextureBarrier(
                    _fontTexture,
                    textureRange,
                    _fontTexture.InitialSync,
                    PipelineSync.Copy,
                    _fontTexture.InitialAccess,
                    ResourceAccess.CopyDestination,
                    _fontTexture.InitialLayout,
                    TextureLayout.CopyDestination));
                _backend.Barrier(commands, new BufferBarrier(
                    upload,
                    upload.InitialSync,
                    PipelineSync.Copy,
                    upload.InitialAccess,
                    ResourceAccess.CopySource));
                _backend.CopyBufferToTexture(
                    commands,
                    copy with
                    {
                        Buffer = upload,
                        BufferOffset = footprint.Offset,
                        BufferRowPitch = footprint.RowPitch,
                        BufferImageHeight = footprint.RowCount,
                    });
                _backend.Barrier(commands, new TextureBarrier(
                    _fontTexture,
                    textureRange,
                    PipelineSync.Copy,
                    PipelineSync.AllShading,
                    ResourceAccess.CopyDestination,
                    ResourceAccess.ShaderResource,
                    TextureLayout.CopyDestination,
                    TextureLayout.ShaderResource));
                recorded = _backend.End(commands);
                recording = false;
            }
            catch
            {
                if (recording)
                    _backend.Discard(commands);
                throw;
            }

            using (recorded)
            {
                RecordedCommands[] payload = [recorded];
                QueueSubmitDesc submission = new(default, default, payload, default, default);
                QueueCompletion completion = _backend.Submit(
                    _backend.GetQueue(_device, QueueType.Graphics),
                    submission);
                RuntimeWait.Position(_backend, completion, _window, ResourceTimeout);
            }
        }
        finally
        {
            upload.Dispose();
        }

        io.Fonts.SetTexID((nint)FontGraphId);
        io.Fonts.ClearTexData();
    }

    private int AcquireGeneration() => AcquireGeneration(out _);

    private int AcquireGeneration(out int availableGenerationCount)
    {
        if (TryAcquireGeneration(
                out int generation,
                out availableGenerationCount,
                out _))
        {
            return generation;
        }

        int preferred = _preferredGeneration;
        int alternate = 1 - preferred;

        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < ResourceTimeout)
        {
            _ = _window.PumpMessages();
            if (WaitForAll(_readiness[preferred], TimeSpan.FromMilliseconds(1)))
            {
                availableGenerationCount = 1;
                return preferred;
            }
            if (WaitForAll(_readiness[alternate], TimeSpan.Zero))
            {
                availableGenerationCount = 1;
                return alternate;
            }
        }
        throw new TimeoutException(
            "Both Runtime ImGui resource generations remained in flight past the retirement timeout.");
    }

    private bool TryAcquireGeneration(
        out int generation,
        out int availableGenerationCount,
        out QueueCompletion[] retirementFences)
    {
        int preferred = _preferredGeneration;
        int alternate = 1 - preferred;
        bool preferredAvailable = IsGenerationAvailable(preferred);
        bool alternateAvailable = IsGenerationAvailable(alternate);
        availableGenerationCount =
            (preferredAvailable ? 1 : 0) + (alternateAvailable ? 1 : 0);
        if (preferredAvailable)
        {
            generation = preferred;
            retirementFences = [];
            return true;
        }
        if (alternateAvailable)
        {
            generation = alternate;
            retirementFences = [];
            return true;
        }

        generation = -1;
        retirementFences = _readiness[preferred];
        if (retirementFences.Length == 0)
        {
            throw new InvalidOperationException(
                "An unavailable Runtime ImGui generation has no retirement position.");
        }
        return false;
    }

    private bool IsGenerationAvailable(int generation)
    {
        QueueCompletion[] readiness = _readiness[generation];
        return readiness.Length == 0 || WaitForAll(readiness, TimeSpan.Zero);
    }

    private void EnsureBuffers(int generation, int vertexCount, int indexCount)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)generation, 1u);
        if (vertexCount <= 0 || indexCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));
        if (_vertexBuffers[generation] is null
            || vertexCount > _vertexCapacities[generation])
        {
            _vertexBuffers[generation]?.Dispose();
            _vertexCapacities[generation] = Grow(_vertexCapacities[generation], vertexCount);
            _vertexBuffers[generation] = _backend.CreateBuffer(
                _device,
                new BufferDesc(
                    checked((ulong)_vertexCapacities[generation] * (ulong)Unsafe.SizeOf<ImDrawVert>()),
                    BufferUsages.Vertex,
                    $"Runtime ImGui vertices {generation}"),
                MemoryType.Upload);
        }
        if (_indexBuffers[generation] is null
            || indexCount > _indexCapacities[generation])
        {
            _indexBuffers[generation]?.Dispose();
            _indexCapacities[generation] = Grow(_indexCapacities[generation], indexCount);
            _indexBuffers[generation] = _backend.CreateBuffer(
                _device,
                new BufferDesc(
                    checked((ulong)_indexCapacities[generation] * sizeof(ushort)),
                    BufferUsages.Index,
                    $"Runtime ImGui indices {generation}"),
                MemoryType.Upload);
        }
    }

    private unsafe void UploadDrawData(ImDrawDataPtr drawData, int generation)
    {
        float left = drawData.DisplayPos.X;
        float right = drawData.DisplayPos.X + drawData.DisplaySize.X;
        float top = drawData.DisplayPos.Y;
        float bottom = drawData.DisplayPos.Y + drawData.DisplaySize.Y;
        if (!(right > left) || !(bottom > top))
            throw new InvalidOperationException("ImGui produced an empty display rectangle.");
        Vector4 transform = new(
            2.0f / (right - left),
            -2.0f / (bottom - top),
            -1.0f - left * 2.0f / (right - left),
            1.0f + top * 2.0f / (bottom - top));
        Span<byte> uniform = stackalloc byte[Unsafe.SizeOf<Vector4>()];
        MemoryMarshal.Write(uniform, in transform);
        WriteMappedBuffer(
            _uniformBuffers[generation]
                ?? throw new InvalidOperationException("The Runtime ImGui uniform buffer is unavailable."),
            uniform);

        ulong vertexOffset = 0;
        ulong indexOffset = 0;
        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            ImDrawListPtr commandList = drawData.CmdLists[listIndex];
            int vertexBytes = checked(commandList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>());
            int indexBytes = checked(commandList.IdxBuffer.Size * sizeof(ushort));
            WriteMappedBuffer(
                _vertexBuffers[generation]!,
                vertexOffset,
                new ReadOnlySpan<byte>((void*)commandList.VtxBuffer.Data, vertexBytes));
            WriteMappedBuffer(
                _indexBuffers[generation]!,
                indexOffset,
                new ReadOnlySpan<byte>((void*)commandList.IdxBuffer.Data, indexBytes));
            vertexOffset = checked(vertexOffset + (ulong)vertexBytes);
            indexOffset = checked(indexOffset + (ulong)indexBytes);
        }
    }

    private unsafe void DrawGraphCommands(
        UnsafeGraphContext commands,
        RuntimeUiPassData parameters)
    {
        ImDrawDataPtr drawData = _pendingDrawData;
        if (drawData.NativePtr is null)
            throw new InvalidOperationException("Runtime ImGui draw data was not retained for graph execution.");
        commands.SetViewport(new Viewport(0, 0, parameters.Width, parameters.Height));
        commands.BindVertexBuffers(
            0,
            parameters.Vertices,
            0,
            checked((uint)Unsafe.SizeOf<ImDrawVert>()));
        commands.BindIndexBuffer(parameters.Indices, 0, IndexType.UInt16);

        Vector2 clipOffset = drawData.DisplayPos;
        Vector2 clipScale = drawData.FramebufferScale;
        int vertexOffset = 0;
        uint indexOffset = 0;
        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            ImDrawListPtr commandList = drawData.CmdLists[listIndex];
            for (int commandIndex = 0; commandIndex < commandList.CmdBuffer.Size; commandIndex++)
            {
                ImDrawCmdPtr command = commandList.CmdBuffer[commandIndex];
                if (command.UserCallback != 0)
                    throw new NotSupportedException("Runtime ImGui user callbacks are not supported.");
                if (command.ElemCount == 0)
                    continue;
                nint textureId = command.TextureId;
                if (textureId != 0 && textureId != FontGraphId)
                    throw new NotSupportedException($"Runtime ImGui texture id {textureId} is not registered.");

                Vector4 clip = command.ClipRect;
                int minX = (int)MathF.Max((clip.X - clipOffset.X) * clipScale.X, 0.0f);
                int minY = (int)MathF.Max((clip.Y - clipOffset.Y) * clipScale.Y, 0.0f);
                int maxX = (int)MathF.Min(
                    (clip.Z - clipOffset.X) * clipScale.X,
                    parameters.Width);
                int maxY = (int)MathF.Min(
                    (clip.W - clipOffset.Y) * clipScale.Y,
                    parameters.Height);
                if (maxX <= minX || maxY <= minY)
                    continue;
                commands.SetScissor(new ScissorRect(minX, minY, maxX - minX, maxY - minY));
                commands.DrawIndexed(
                    command.ElemCount,
                    firstIndex: checked(indexOffset + command.IdxOffset),
                    vertexOffset: checked(vertexOffset + (int)command.VtxOffset));
            }
            vertexOffset = checked(vertexOffset + commandList.VtxBuffer.Size);
            indexOffset = checked(indexOffset + (uint)commandList.IdxBuffer.Size);
        }
    }

    private void WriteMappedBuffer(Buffer destination, ReadOnlySpan<byte> contents) =>
        WriteMappedBuffer(destination, 0, contents);

    private void WriteMappedBuffer(
        Buffer destination,
        ulong offset,
        ReadOnlySpan<byte> contents)
    {
        BufferRange range = new(offset, checked((ulong)contents.Length));
        using MappedBuffer mapping = _backend.Map(destination, MapType.Write, range);
        contents.CopyTo(mapping.Bytes);
        mapping.Flush(range);
    }

    private bool WaitForAll(ReadOnlySpan<QueueCompletion> completions, TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (timeout == TimeSpan.Zero)
        {
            foreach (ref readonly QueueCompletion completion in completions)
                if (!_backend.IsComplete(completion))
                    return false;
            return true;
        }

        long started = Environment.TickCount64;
        foreach (ref readonly QueueCompletion completion in completions)
        {
            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : timeout - TimeSpan.FromMilliseconds(Environment.TickCount64 - started);
            if (remaining < TimeSpan.Zero)
                remaining = TimeSpan.Zero;
            if (_backend.WaitCpu(completion, remaining) != WaitStatus.Completed)
                return false;
        }
        return true;
    }

    private void MakeCurrent()
    {
        if (_context != 0 && ImGui.GetCurrentContext() != _context)
            ImGui.SetCurrentContext(_context);
    }

    private static void UpdateModifiers(ImGuiIOPtr io, RuntimeInput input)
    {
        io.AddKeyEvent(
            ImGuiKey.ModCtrl,
            input.IsKeyDown(RuntimeInput.KeyControl)
            || input.IsKeyDown(RuntimeInput.KeyLeftControl)
            || input.IsKeyDown(RuntimeInput.KeyRightControl));
        io.AddKeyEvent(
            ImGuiKey.ModShift,
            input.IsKeyDown(RuntimeInput.KeyShift)
            || input.IsKeyDown(RuntimeInput.KeyLeftShift)
            || input.IsKeyDown(RuntimeInput.KeyRightShift));
        io.AddKeyEvent(
            ImGuiKey.ModAlt,
            input.IsKeyDown(RuntimeInput.KeyMenu)
            || input.IsKeyDown(RuntimeInput.KeyLeftMenu)
            || input.IsKeyDown(RuntimeInput.KeyRightMenu));
    }

    private static ImGuiKey MapKey(int key)
    {
        if (key is >= 0x30 and <= 0x39)
            return (ImGuiKey)((int)ImGuiKey._0 + key - 0x30);
        if (key is >= 0x41 and <= 0x5A)
            return (ImGuiKey)((int)ImGuiKey.A + key - 0x41);
        if (key is >= 0x70 and <= 0x87)
            return (ImGuiKey)((int)ImGuiKey.F1 + key - 0x70);
        return key switch
        {
            0x08 => ImGuiKey.Backspace,
            0x09 => ImGuiKey.Tab,
            0x0D => ImGuiKey.Enter,
            0x1B => ImGuiKey.Escape,
            0x20 => ImGuiKey.Space,
            0x21 => ImGuiKey.PageUp,
            0x22 => ImGuiKey.PageDown,
            0x23 => ImGuiKey.End,
            0x24 => ImGuiKey.Home,
            0x25 => ImGuiKey.LeftArrow,
            0x26 => ImGuiKey.UpArrow,
            0x27 => ImGuiKey.RightArrow,
            0x28 => ImGuiKey.DownArrow,
            0x2D => ImGuiKey.Insert,
            0x2E => ImGuiKey.Delete,
            0xA0 => ImGuiKey.LeftShift,
            0xA1 => ImGuiKey.RightShift,
            0xA2 => ImGuiKey.LeftCtrl,
            0xA3 => ImGuiKey.RightCtrl,
            0xA4 => ImGuiKey.LeftAlt,
            0xA5 => ImGuiKey.RightAlt,
            0xBA => ImGuiKey.Semicolon,
            0xBB => ImGuiKey.Equal,
            0xBC => ImGuiKey.Comma,
            0xBD => ImGuiKey.Minus,
            0xBE => ImGuiKey.Period,
            0xBF => ImGuiKey.Slash,
            0xC0 => ImGuiKey.GraveAccent,
            0xDB => ImGuiKey.LeftBracket,
            0xDC => ImGuiKey.Backslash,
            0xDD => ImGuiKey.RightBracket,
            0xDE => ImGuiKey.Apostrophe,
            _ => ImGuiKey.None,
        };
    }

    private static int Grow(int current, int required)
    {
        if (required <= current)
            return current;
        int baseline = Math.Max(current, 256);
        return Math.Max(required, checked(baseline + baseline / 2));
    }

    private static QueueCompletion[] MergeCompletions(
        ReadOnlySpan<QueueCompletion> left,
        ReadOnlySpan<QueueCompletion> right)
    {
        QueueCompletion[] merged = new QueueCompletion[checked(left.Length + right.Length)];
        int count = 0;
        Add(left);
        Add(right);
        return merged.AsSpan(0, count).ToArray();

        void Add(ReadOnlySpan<QueueCompletion> values)
        {
            foreach (ref readonly QueueCompletion value in values)
            {
                int existing = -1;
                for (int index = 0; index < count; index++)
                {
                    if (ReferenceEquals(merged[index].Queue, value.Queue))
                    {
                        existing = index;
                        break;
                    }
                }
                if (existing < 0)
                    merged[count++] = value;
                else if (value.Value > merged[existing].Value)
                    merged[existing] = value;
            }
        }
    }

    private static void Destroy(IDisposable? value, ref List<Exception>? failures)
    {
        if (value is null)
            return;
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

internal readonly record struct RuntimeUiMetrics(
    int FrameIndex,
    float DeltaSeconds,
    int Width,
    int Height,
    float DpiScale,
    Vector2 JitterPixels,
    Vector3 CameraPosition,
    bool Focused);

internal sealed class RuntimeUiPassData
{
    public RuntimeUiRenderer Renderer { get; set; } = null!;
    public BufferHandle Vertices { get; set; }
    public BufferHandle Indices { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
