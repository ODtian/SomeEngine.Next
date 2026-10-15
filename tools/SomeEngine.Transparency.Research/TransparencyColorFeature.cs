using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Runtime;
using Buffer = SomeEngine.Graphics.Buffer;
using Shader = SomeEngine.Resources.Shader;

namespace SomeEngine.Transparency.Research;

public sealed class TransparencyColorFeature : IApplicationColorFeature
{
    private static readonly TextureSubresourceRange ColorRange = new(0, 1, 0, 1, TextureAspects.Color);
    private readonly List<Buffer> _buffers = [];
    private readonly LinkedComputePipeline _pipeline;
    private readonly Shader _shader;
    private readonly GraphTextureInput _color, _depth;

    public TransparencyColorFeature(ApplicationRendering rendering, TriangleHierarchy geometry, float colorError)
    {
        _shader = ResearchShader.Compile("resolve.slang", ["ResolveTransparency"],
            definitions: geometry.SeparatedCoverage ? "#define TRANSPARENCY_SEPARATED_COVERAGE 1\n" : "");
        _pipeline = LinkedComputePipeline.Create(rendering.Backend, rendering.Device, _shader, "ResolveTransparency", "Transparency traversal");
        using RenderGraphEdit edit = rendering.Graph.BeginEdit();
        _color = edit.CreateTextureInput();
        _depth = edit.CreateTextureInput();
        var data = new TransparencyGeometry(geometry);
        Buffer source = UploadGeometry(data.Bytes);
        uint[] settings = data.CreateSettings(rendering.Width, rendering.Height, geometry, colorError);
        Buffer uniforms = Upload(settings.AsSpan(), BufferUsages.Constant);
        GraphBufferId geometryBuffer = edit.RegisterExternalBuffer(source,
            [new(BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.ShaderResource, ResourceContentState.Defined)]);
        GraphBufferId uniformBuffer = edit.RegisterExternalBuffer(uniforms,
            [new(BufferRange.Whole, PipelineSync.ComputeShading, ResourceAccess.ConstantBuffer, ResourceContentState.Defined)]);
        CompletePass = edit.AddComputeFramePass("Resolve transparency", PassQueueSelection.AnyOfType(QueueType.Graphics),
            new DispatchArguments((uint)(rendering.Width + 7) / 8, (uint)(rendering.Height + 7) / 8, 1),
            new PassOptions(RepeatCommands: true), _pipeline.Pipeline, _pipeline.ParameterLayout, default,
            static (ref PassDefinition _, ref DispatchArguments _) => { },
            static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch), region: rendering.Region);
        PassDefinition pass = edit.GetPass(CompletePass);
        TypeLayoutReflection root = _pipeline.ParameterLayout.TypeLayout;
        if (root.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock) root = root.ElementTypeLayout;
        for (nint index = 0; index < root.BindingRangeCount; index++)
        {
            if (root.GetBindingRangeDescriptorRangeCount(index) == 0) continue;
            switch (root.GetBindingRangeLeafVariable(index).Name)
            {
                case "Settings":
                    _ = pass.Bind(edit.CreateBufferCbv(uniformBuffer, BufferRange.Whole), PipelineSync.ComputeShading);
                    break;
                case "Geometry":
                    _ = pass.Bind(edit.CreateBufferSrv(geometryBuffer, BufferRange.Whole), PipelineSync.ComputeShading);
                    break;
                case "OpaqueDepth":
                    _ = pass.Bind(edit.CreateTextureSrv(_depth, new(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float,
                        TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading);
                    break;
                case "SceneColor":
                    _ = pass.Bind(edit.CreateTextureUav(_color, ColorRange, Format.R16G16B16A16Float,
                        TextureViewDimension.Texture2DArray), PipelineSync.ComputeShading, GraphAccessMode.ReadWrite);
                    break;
            }
        }
        edit.Commit();

        Buffer UploadGeometry(byte[] bytes)
        {
            Buffer buffer = rendering.Backend.CreateBuffer(rendering.Device,
                new((ulong)bytes.Length, BufferUsages.ShaderRead | BufferUsages.CopyDestination), MemoryType.DeviceLocal);
            _buffers.Add(buffer);
            using Buffer upload = rendering.Backend.CreateBuffer(rendering.Device,
                new((ulong)bytes.Length, BufferUsages.CopySource), MemoryType.Upload);
            using (MappedBuffer mapping = rendering.Backend.Map(upload, MapType.Write, BufferRange.Whole))
            {
                bytes.CopyTo(mapping.Bytes);
                mapping.Flush(mapping.Range);
            }
            using CommandContext context = rendering.Backend.CreateCommandContext(rendering.Device, new(QueueType.Graphics, 0, 1));
            rendering.Backend.Begin(context);
            rendering.Backend.Barrier(context, new BufferBarrier(buffer, PipelineSync.None, PipelineSync.Copy,
                ResourceAccess.NoAccess, ResourceAccess.CopyDestination));
            rendering.Backend.CopyBuffer(context, new(upload, 0, buffer, 0, (ulong)bytes.Length));
            rendering.Backend.Barrier(context, new BufferBarrier(buffer, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.ShaderResource));
            using RecordedCommands commands = rendering.Backend.End(context);
            QueueCompletion completion = rendering.Backend.Submit(rendering.Backend.GetQueue(rendering.Device, QueueType.Graphics),
                new([], [], [commands], [], []));
            if (rendering.Backend.WaitCpu(completion, TimeSpan.FromSeconds(30)) != WaitStatus.Completed)
                throw new TimeoutException("Transparency geometry upload timed out.");
            return buffer;
        }

        Buffer Upload<T>(Span<T> values, BufferUsages usages) where T : unmanaged
        {
            Buffer buffer = rendering.Backend.CreateBuffer(rendering.Device,
                new((ulong)values.Length * (ulong)Marshal.SizeOf<T>(), usages), MemoryType.Upload);
            _buffers.Add(buffer);
            using MappedBuffer mapping = rendering.Backend.Map(buffer, MapType.Write, BufferRange.Whole);
            MemoryMarshal.AsBytes(values).CopyTo(mapping.Bytes);
            mapping.Flush(mapping.Range);
            return buffer;
        }
    }

    public GraphPassId CompletePass { get; }
    public void OrderAfter(ref RenderGraphEdit graph, GraphPassId colorReady) => graph.OrderAfter(CompletePass, colorReady);
    public GraphTextureId Record(ref RenderGraphFrame graph, GraphTextureId color, GraphTextureId depth)
    {
        graph.SetTextureInput(_color, color);
        graph.SetTextureInput(_depth, depth);
        return color;
    }
    public void Dispose()
    {
        _pipeline.Dispose();
        for (int index = _buffers.Count - 1; index >= 0; index--) _buffers[index].Dispose();
    }
}
