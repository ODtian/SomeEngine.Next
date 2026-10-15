using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using SomeEngine.Runtime;

namespace SomeEngine.Transparency.Research;

public sealed class BlendEffectColorFeature : IApplicationColorFeature
{
    private static readonly TextureSubresourceRange ColorRange = new(0, 1, 0, 1, TextureAspects.Color);
    private readonly BlendEffectResources _effect;
    private readonly GraphTextureInput _color, _depth;

    public BlendEffectColorFeature(ApplicationRendering rendering, BlendEffectData data, BlendMethod method, float xrayDistance)
    {
        _effect = new(rendering.Backend, rendering.Device, data, method, rendering.Projection, xrayDistance, new());
        using RenderGraphEdit edit = rendering.Graph.BeginEdit();
        _color = edit.CreateTextureInput();
        _depth = edit.CreateTextureInput();
        var buffers = new Dictionary<string, GraphBufferSrvId>();
        GraphBufferUavId cellWrite = default;
        foreach (var (name, resource) in _effect.Buffers)
        {
            bool freshCells = name == "CellSlices" && method == BlendMethod.Cluster;
            GraphBufferId id = edit.RegisterExternalBuffer(resource.Buffer, freshCells ? [] :
                [new(BufferRange.Whole, PipelineSync.AllShading, ResourceAccess.ShaderResource, ResourceContentState.Defined)]);
            buffers[name] = edit.CreateBufferSrv(id, structureStride: resource.Stride);
            if (name == "CellSlices") cellWrite = edit.CreateBufferUav(id, structureStride: resource.Stride);
        }
        GraphBufferId uniforms = edit.RegisterExternalBuffer(_effect.Uniforms,
            [new(BufferRange.Whole, PipelineSync.AllShading, ResourceAccess.ConstantBuffer, ResourceContentState.Defined)]);
        GraphBufferCbvId settings = edit.CreateBufferCbv(uniforms);
        GraphTextureSrvId depth = edit.CreateTextureSrv(_depth, new(0, 1, 0, 1, TextureAspects.Depth),
            Format.D32Float, TextureViewDimension.Texture2DArray);
        GraphTextureUavId color = edit.CreateTextureUav(_color, ColorRange, Format.R16G16B16A16Float, TextureViewDimension.Texture2DArray);
        GraphPassId buildPass = default;
        if (method == BlendMethod.Cluster)
        {
            var build = _effect.BuildCells!;
            buildPass = edit.AddComputeFramePass("Build blend depth cells", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new DispatchArguments(((uint)data.Bounds.Length + 63) / 64, 1, 1), new PassOptions(RepeatCommands: true),
                build.Pipeline, build.ParameterLayout, default, static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch));
            Bind(edit.GetPass(buildPass), build.ParameterLayout, PipelineSync.ComputeShading, true);
        }
        if (method == BlendMethod.Forward)
        {
            CompletePass = edit.AddRasterFramePass("Draw forward blend effects", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new BlendDrawSize((uint)data.Width, (uint)data.Height, _effect.VertexCount), new PassOptions(RepeatCommands: true),
                _effect.Draw!, _effect.DrawLayout, default,
                static (ref PassDefinition pass, ref BlendDrawSize size) => pass.DefineRasterExtent(size.Width, size.Height),
                static (ref RasterPassCommandScope commands, in BlendDrawSize size) =>
                {
                    commands.SetViewports([new(0, 0, size.Width, size.Height)]);
                    commands.SetScissors([new(0, 0, (int)size.Width, (int)size.Height)]);
                    commands.Draw(new(size.Vertices, 1, 0, 0));
                });
            Bind(edit.GetPass(CompletePass), _effect.DrawLayout, PipelineSync.VertexShading | PipelineSync.PixelShading, false);
            GraphColorAttachmentViewId target = edit.CreateColorAttachmentView(_color, ColorRange,
                Format.R16G16B16A16Float, TextureViewDimension.Texture2D);
            edit.GetPass(CompletePass).ColorAttachment(0, target, LoadType.Load, StoreType.Store, WriteCoverage.Partial, Vector4.Zero);
            GraphDepthStencilViewId depthTarget = edit.CreateDepthStencilView(_depth,
                new(0, 1, 0, 1, TextureAspects.Depth), Format.D32Float, TextureViewDimension.Texture2D, readOnlyDepth: true);
            edit.GetPass(CompletePass).DepthStencilAttachment(depthTarget, LoadType.Load, StoreType.Store,
                WriteCoverage.Partial, 0, LoadType.Discard, StoreType.Discard, WriteCoverage.Partial, 0);
        }
        else
        {
            var resolve = _effect.Resolve!;
            CompletePass = edit.AddComputeFramePass("Resolve blend effects", PassQueueSelection.AnyOfType(QueueType.Graphics),
                new DispatchArguments((uint)data.TileColumns, (uint)data.TileRows, 1), new PassOptions(RepeatCommands: true),
                resolve.Pipeline, resolve.ParameterLayout, default, static (ref PassDefinition _, ref DispatchArguments _) => { },
                static (ref ComputePassCommandScope commands, in DispatchArguments dispatch) => commands.Dispatch(dispatch));
            Bind(edit.GetPass(CompletePass), resolve.ParameterLayout, PipelineSync.ComputeShading, false);
        }
        if (buildPass != default) edit.OrderAfter(CompletePass, buildPass);
        edit.Commit();

        void Bind(PassDefinition pass, VariableLayoutReflection layout, PipelineSync sync, bool writesCells)
        {
            TypeLayoutReflection root = layout.TypeLayout;
            if (root.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock) root = root.ElementTypeLayout;
            for (nint index = 0; index < root.BindingRangeCount; index++)
            {
                if (root.GetBindingRangeDescriptorRangeCount(index) == 0) continue;
                string name = root.GetBindingRangeLeafVariable(index).Name;
                switch (name)
                {
                    case "Settings": _ = pass.Bind(settings, sync); break;
                    case "OpaqueDepth":
                        _ = pass.Bind(depth, sync, method == BlendMethod.Forward
                            ? TextureLayout.DepthStencilRead : TextureLayout.ShaderResource);
                        break;
                    case "SceneColor": _ = pass.Bind(color, sync, GraphAccessMode.ReadWrite); break;
                    case "CellSlices" when writesCells:
                        _ = pass.Bind(cellWrite, sync, GraphAccessMode.Write, WriteCoverage.Complete, ResourceContentState.Defined);
                        break;
                    default: _ = pass.Bind(buffers[name], sync); break;
                }
            }
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
    public void Dispose() => _effect.Dispose();
    private readonly record struct BlendDrawSize(uint Width, uint Height, uint Vertices);
}
