using SomeEngine.Render;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ImGuiNET;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SlangShaderSharp;
using Buffer = SomeEngine.Graphics.Buffer;
using Texture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Runtime;

internal sealed class DebugInterfaceRenderer : IDisposable
{

    private readonly RenderPipelines _renderPipelines;
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
    private ShaderReflection _reflection = ShaderReflection.Null;
    private Pipeline? _pipeline;
    private Shader _shader;
    private global::SomeEngine.RenderGraph.RenderGraph? _graph;
    private GraphTextureId _target;
    private GraphPassId _pass;
    private GraphBufferInput _vertices = null!;
    private GraphBufferInput _indices = null!;
    private GraphBufferInput _uniform = null!;
    private GraphTextureInput _font = null!;
    private DebugInterfacePassData _passData;
    private GraphTextureId _fontTexture;
    private uint _fontWidth, _fontHeight;
    private TextureDesc FontDescription => new(TextureDimension.Texture2D, _fontWidth, _fontHeight, 1, 1, 1, 1,
        Format.R8G8B8A8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination, label: "Runtime ImGui font atlas");
    private byte[] _fontPixels = [];
    private bool _fontUploaded;
    private Sampler? _fontSampler;
    private bool _disposed;

    internal DebugInterfaceRenderer(
        IGraphicsBackend backend,
        Device device,
        RenderPipelines pipelines,
        NativeWindow window,
        Shader shader,
        string vertexEntryPoint,
        string pixelEntryPoint,
        Format outputFormat)
    {

        _renderPipelines = pipelines;
        _shader = shader;
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        ArgumentNullException.ThrowIfNull(shader);
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexEntryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(pixelEntryPoint);

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

            CreatePipeline(shader, vertexEntryPoint, pixelEntryPoint, outputFormat);
            UploadFontAtlas(io);
            _fontSampler = _backend.CreateSampler(_device, new SamplerDesc(
                FilterType.Linear,
                FilterType.Linear,
                FilterType.Linear,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                AddressType.ClampToEdge,
                Label: "Runtime ImGui font sampler"));
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

    internal void ProcessEvent(in NativeWindowEvent windowEvent, ApplicationInput input)
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
            case NativeWindowEventKind.TextCommitted:
                foreach (char character in windowEvent.Text!)
                    io.AddInputCharacterUTF16(character);
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
        in DebugInterfaceMetrics metrics, Action? drawDebugUi = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MakeCurrent();
        if (!open)
            return;

        ImGui.SetNextWindowPos(new Vector2(24.0f, 24.0f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new Vector2(460.0f, 580.0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("SomeEngine Debug", ref open))
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
        drawDebugUi?.Invoke();
        ImGui.Separator();
        _ = ImGui.Checkbox("Animate scene", ref animateScene);
        ImGui.TextUnformatted(metrics.TemporalAntialiasing
            ? "Temporal resolve: enabled (8-sample centered Halton)"
            : "Temporal resolve: disabled");
        ImGui.Separator();
        ImGui.TextUnformatted("WASD + Space/Ctrl: move camera");
        ImGui.TextUnformatted("Hold right mouse: look");
        ImGui.TextUnformatted("Shift: speed boost");
        ImGui.TextUnformatted("F1: toggle this panel   Esc: close runtime");
        ImGui.End();
    }

    internal void InstallGraph(global::SomeEngine.RenderGraph.RenderGraph graph, GraphTextureId target, GraphRegionId region)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_graph, graph) && _target == target && _pass != default) return;
        using RenderGraphEdit edit = graph.BeginEdit();
        if (ReferenceEquals(_graph, graph) && _pass != default)
        {
            edit.Remove(_pass);
        }
        GraphBufferInput vertices = edit.CreateBufferInput(BufferUsages.Vertex);
        GraphBufferInput indices = edit.CreateBufferInput(BufferUsages.Index);
        GraphBufferInput uniform = edit.CreateBufferInput(BufferUsages.Constant);
        GraphTextureInput font = edit.CreateTextureInput();
        if (_fontTexture == default) _fontTexture = edit.CreatePersistentTexture(FontDescription);
        GraphColorAttachmentViewId targetView = edit.CreateColorAttachmentView(target, label: "Runtime ImGui presentation view");
        GraphTextureSrvId fontView = edit.CreateTextureSrv(font, Format.R8G8B8A8UNorm,
            SomeEngine.Graphics.TextureViewDimension.Texture2D, "Runtime ImGui font atlas view");
        DebugInterfacePassData data = new(this,
            _pipeline ?? throw new InvalidOperationException("The ImGui pipeline is unavailable."),
            _reflection != ShaderReflection.Null
                ? _reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null
                : throw new InvalidOperationException("The ImGui shader is unavailable."),
            targetView, vertices, indices, uniform, fontView,
            _fontSampler ?? throw new InvalidOperationException("The ImGui sampler is unavailable."), 0, 0, default);
        GraphPassId pass = edit.AddRasterFramePass("Runtime ImGui overlay",
            PassQueueSelection.AnyOfType(QueueType.Graphics), data,
            new PassOptions(Recording: PassRecordingMode.CallingThread), DebugInterfacePassData.Declare, DebugInterfacePassData.Record,
            region: region);
        edit.Commit();
        _graph = graph;
        _target = target;
        _pass = pass;
        _vertices = vertices;
        _indices = indices;
        _uniform = uniform;
        _font = font;
        _passData = data;
    }

    internal unsafe void Record(ref RenderGraphFrame graph, int width, int height)
    {
        MakeCurrent();
        ImGui.Render();
        ImDrawDataPtr drawData = ImGui.GetDrawData();
        graph.SetTextureInput(_font, _fontTexture);
        if (!_fontUploaded) UploadFont(ref graph);
        if (drawData.TotalVtxCount <= 0 || drawData.TotalIdxCount <= 0)
        {
            graph.SetPassEnabled(_pass, false);
            return;
        }
        UploadDrawData(ref graph, drawData);
        graph.SetPassData(_pass, _passData with { Width = width, Height = height, DrawData = drawData });
        graph.SetPassEnabled(_pass, true);
    }

    private void UploadFont(ref RenderGraphFrame frame)
    {
        var copy = new BufferTextureCopy(null!, 0, 0, 0, _graph!.GetTexture(_fontTexture),
            0, 0, TextureAspects.Color, 0, 0, 0, _fontWidth, _fontHeight, 1);
        TextureCopyFootprint footprint = _backend.GetTextureCopyFootprint(_device, FontDescription, copy);
        GraphBufferSlice upload = frame.Upload((int)footprint.TotalSize, BufferUsages.CopySource,
            (Pixels: _fontPixels, Width: (int)_fontWidth, Height: (int)_fontHeight, Footprint: footprint),
            static (bytes, data) =>
            {
                bytes.Clear();
                for (int row = 0; row < data.Height; row++)
                    data.Pixels.AsSpan(row * data.Width * 4, data.Width * 4)
                        .CopyTo(bytes.Slice((int)data.Footprint.Offset + row * (int)data.Footprint.RowPitch, data.Width * 4));
            });
        frame.AddCopyPass("Upload ImGui font", PassQueueSelection.AnyOfType(QueueType.Graphics),
            (Upload: upload, Texture: _fontTexture, Footprint: footprint, Width: _fontWidth, Height: _fontHeight), default,
            static (ref PassDefinition pass, ref (GraphBufferSlice Upload, GraphTextureId Texture, TextureCopyFootprint Footprint, uint Width, uint Height) data) =>
            {
                pass.Read(data.Upload.Buffer, data.Upload.Range, PipelineSync.Copy, ResourceAccess.CopySource);
                pass.Write(data.Texture, new(0, 1, 0, 1, TextureAspects.Color), PipelineSync.Copy,
                    ResourceAccess.CopyDestination, TextureLayout.CopyDestination, WriteCoverage.Complete, ResourceContentState.Defined);
            },
            static (ref CopyPassCommandScope commands, in (GraphBufferSlice Upload, GraphTextureId Texture, TextureCopyFootprint Footprint, uint Width, uint Height) data) =>
                commands.CopyBufferToTexture(new(commands.GetBuffer(data.Upload.Buffer),
                    data.Upload.Range.Offset + data.Footprint.Offset, data.Footprint.RowPitch, data.Footprint.RowCount,
                    commands.GetTexture(data.Texture), 0, 0, TextureAspects.Color, 0, 0, 0, data.Width, data.Height, 1)));
        frame.ObserveExecution(new FontSubmission(this));
    }

    private sealed class FontSubmission(DebugInterfaceRenderer renderer) : IRenderGraphFrameExecutionObserver
    {
        public void OnFrameSubmitted(in RenderGraphSubmission submission) => renderer._fontUploaded = true;
    }

    internal void ReplaceShader(Shader previous, Shader next)
    {
        if (!ReferenceEquals(_shader, previous)) return;
        _shader = next;
        _pipeline = _renderPipelines.CreateWithShader(_pipeline!, next).GetAwaiter().GetResult();
        _reflection = next.GetSlangReflection(next.RequireSlangTarget(_device.Capabilities.CompileTarget));
        var layout = _reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        _passData = _passData with { Pipeline = _pipeline, ParameterLayout = layout };
        if (_pass == default) return;
        using RenderGraphEdit edit = _graph!.BeginEdit();
        PassDefinition pass = edit.GetPass(_pass);
        pass.SetPipeline(_pipeline);
        pass.SetParameterBlock(layout);
        edit.Commit();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        List<Exception>? failures = null;
        Destroy(_fontSampler, ref failures);
        _fontSampler = null;
        if (_graph is { IsLost: false, IsDisposed: false } && _fontTexture != default)
        {
            using RenderGraphEdit edit = _graph.BeginEdit();
            edit.Remove(_fontTexture);
            edit.Commit();
        }

        _pipeline = null;
        _reflection = ShaderReflection.Null;
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

    private void CreatePipeline(
        Shader shader,
        string vertexEntryPoint,
        string pixelEntryPoint,
        Format outputFormat)
    {
        int targetIndex = shader.RequireSlangTarget(_device.Capabilities.CompileTarget);
        _reflection = shader.GetSlangReflection(targetIndex);
        EntryPointReflection vertex = shader.RequireSlangEntry(
            targetIndex,
            vertexEntryPoint,
            SlangStage.Vertex);
        EntryPointReflection pixel = shader.RequireSlangEntry(
            targetIndex,
            pixelEntryPoint,
            SlangStage.Fragment);
        System.Buffers.ReadOnlySequence<byte> vertexCode =
            shader.GetSlangEntryBytes(targetIndex, vertex);
        System.Buffers.ReadOnlySequence<byte> pixelCode =
            shader.GetSlangEntryBytes(targetIndex, pixel);
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
            shader.SlangCode,
            _reflection,
            vertex,
            vertexCode,
            pixel,
            pixelCode,
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
            "Runtime ImGui pipeline",
            vertexCodeBlob: shader.GetSlangEntryBlob(targetIndex, vertex),
            pixelCodeBlob: shader.GetSlangEntryBlob(targetIndex, pixel));
        _pipeline = _renderPipelines.CreateGraphics(description).GetAwaiter().GetResult();
    }

    private unsafe void UploadFontAtlas(ImGuiIOPtr io)
    {
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);
        _fontWidth = (uint)width;
        _fontHeight = (uint)height;
        _fontPixels = new ReadOnlySpan<byte>(pixels, width * height * bytesPerPixel).ToArray();
        io.Fonts.SetTexID((nint)FontGraphId);
        io.Fonts.ClearTexData();
    }

    private unsafe void UploadDrawData(ref RenderGraphFrame graph, ImDrawDataPtr drawData)
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
        graph.SetBufferInput(_uniform, graph.Upload(uniform, BufferUsages.Constant));
        GraphBufferSlice vertices = graph.Upload(drawData.TotalVtxCount * Unsafe.SizeOf<ImDrawVert>(), BufferUsages.Vertex,
            drawData, static (bytes, draws) =>
            {
                int offset = 0;
                for (int index = 0; index < draws.CmdListsCount; index++)
                {
                    ImDrawListPtr list = draws.CmdLists[index];
                    int count = list.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>();
                    new ReadOnlySpan<byte>((void*)list.VtxBuffer.Data, count).CopyTo(bytes[offset..]);
                    offset += count;
                }
            });
        GraphBufferSlice indices = graph.Upload(drawData.TotalIdxCount * sizeof(ushort), BufferUsages.Index,
            drawData, static (bytes, draws) =>
            {
                int offset = 0;
                for (int index = 0; index < draws.CmdListsCount; index++)
                {
                    ImDrawListPtr list = draws.CmdLists[index];
                    int count = list.IdxBuffer.Size * sizeof(ushort);
                    new ReadOnlySpan<byte>((void*)list.IdxBuffer.Data, count).CopyTo(bytes[offset..]);
                    offset += count;
                }
            });
        graph.SetBufferInput(_vertices, vertices);
        graph.SetBufferInput(_indices, indices);

    }

    internal unsafe void DrawGraphCommands(
        ref RasterPassCommandScope commands,
        in DebugInterfacePassData parameters)
    {
        ImDrawDataPtr drawData = parameters.DrawData;
        if (drawData.NativePtr is null)
            throw new InvalidOperationException("Runtime ImGui draw data was not retained for graph execution.");
        Viewport viewport = new(0, 0, parameters.Width, parameters.Height);
        commands.SetViewports(MemoryMarshal.CreateReadOnlySpan(ref viewport, 1));
        BufferRegion vertices = commands.GetBufferRegion(parameters.Vertices);
        Buffer vertexBuffer = vertices.Buffer;
        VertexBufferBinding vertexBinding = new(
            vertexBuffer,
            vertices.Range.Offset,
            checked((uint)Unsafe.SizeOf<ImDrawVert>()),
            vertices.Range.Size);
        commands.SetVertexBuffers(
            0,
            MemoryMarshal.CreateReadOnlySpan(ref vertexBinding, 1));
        BufferRegion indices = commands.GetBufferRegion(parameters.Indices);
        Buffer indexBuffer = indices.Buffer;
        commands.SetIndexBuffer(new IndexBufferBinding(
            indexBuffer,
            indices.Range.Offset,
            indices.Range.Size,
            IndexType.UInt16));

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
                ScissorRect scissor = new(minX, minY, maxX - minX, maxY - minY);
                commands.SetScissors(MemoryMarshal.CreateReadOnlySpan(ref scissor, 1));
                commands.DrawIndexed(new DrawIndexedArguments(
                    command.ElemCount,
                    1,
                    checked(indexOffset + command.IdxOffset),
                    checked(vertexOffset + (int)command.VtxOffset),
                    0));
            }
            vertexOffset = checked(vertexOffset + commandList.VtxBuffer.Size);
            indexOffset = checked(indexOffset + (uint)commandList.IdxBuffer.Size);
        }
    }

    private void MakeCurrent()
    {
        if (_context != 0 && ImGui.GetCurrentContext() != _context)
            ImGui.SetCurrentContext(_context);
    }

    private static void UpdateModifiers(ImGuiIOPtr io, ApplicationInput input)
    {
        io.AddKeyEvent(
            ImGuiKey.ModCtrl,
            input.IsKeyDown(ApplicationInput.KeyControl)
            || input.IsKeyDown(ApplicationInput.KeyLeftControl)
            || input.IsKeyDown(ApplicationInput.KeyRightControl));
        io.AddKeyEvent(
            ImGuiKey.ModShift,
            input.IsKeyDown(ApplicationInput.KeyShift)
            || input.IsKeyDown(ApplicationInput.KeyLeftShift)
            || input.IsKeyDown(ApplicationInput.KeyRightShift));
        io.AddKeyEvent(
            ImGuiKey.ModAlt,
            input.IsKeyDown(ApplicationInput.KeyMenu)
            || input.IsKeyDown(ApplicationInput.KeyLeftMenu)
            || input.IsKeyDown(ApplicationInput.KeyRightMenu));
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

    private static void Destroy(IDisposable? value, ref List<Exception>? failures)
    {
        if (value is null)
            return;
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

internal readonly record struct DebugInterfaceMetrics(
    int FrameIndex,
    float DeltaSeconds,
    int Width,
    int Height,
    float DpiScale,
    Vector2 JitterPixels,
    Vector3 CameraPosition,
    bool Focused,
    bool TemporalAntialiasing);

internal readonly record struct DebugInterfacePassData(
    DebugInterfaceRenderer Renderer,
    Pipeline Pipeline,
    VariableLayoutReflection ParameterLayout,
    GraphColorAttachmentViewId Target,
    GraphBufferInput Vertices,
    GraphBufferInput Indices,
    GraphBufferInput Uniform,
    GraphTextureSrvId Font,
    Sampler FontSampler,
    int Width,
    int Height,
    ImDrawDataPtr DrawData)
{
    internal static void Declare(ref PassDefinition access, ref DebugInterfacePassData data)
    {
        access.SetPipeline(data.Pipeline);
        access.SetParameterBlock(data.ParameterLayout);
        access.ColorAttachment(
            0,
            data.Target,
            LoadType.Load,
            StoreType.Store,
            WriteCoverage.Partial,
            default);
        _ = access.Read(
            data.Vertices,
            BufferRange.Whole,
            PipelineSync.Draw,
            ResourceAccess.VertexBuffer);
        _ = access.Read(
            data.Indices,
            BufferRange.Whole,
            PipelineSync.IndexInput,
            ResourceAccess.IndexBuffer);
        _ = access.Bind(data.Uniform, PipelineSync.VertexShading);
        _ = access.Bind(data.Font, PipelineSync.PixelShading, TextureLayout.ShaderResource);
        access.Bind(data.FontSampler);
    }

    internal static void Record(ref RasterPassCommandScope commands, in DebugInterfacePassData data) =>
        data.Renderer.DrawGraphCommands(ref commands, data);
}
