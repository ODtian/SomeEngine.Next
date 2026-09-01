using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.RenderGraph;
using EngineTexture = SomeEngine.Assets.Schema.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly Dictionary<SamplerDesc, Sampler> _materialSamplers = [];

    private void BindShadeStages(
        ref RenderGraphFrame graph,
        ClusterShadeStageSet shadeStages,
        in RenderView view)
    {
        if (_shadePasses.Length != shadeStages.Invocations.Count)
            throw new InvalidOperationException("The retained shade passes do not match the installed invocations.");

        Matrix4x4 viewProjection = view.View * view.Projection;
        if (!Matrix4x4.Invert(viewProjection, out Matrix4x4 clipToWorld) ||
            !Matrix4x4.Invert(view.View, out Matrix4x4 worldFromView))
        {
            throw new InvalidOperationException("The Cluster shade stage requires invertible view matrices.");
        }
        Vector3 cameraPosition = new(
            worldFromView.M41,
            worldFromView.M42,
            worldFromView.M43);

        for (int index = 0; index < shadeStages.Invocations.Count; index++)
        {
            ClusterShadeInvocation invocation = shadeStages.Invocations[index];
            GraphPassId passId = _shadePasses[index];
            PassDefinition pass = graph.GetPass(passId);
            pass.SetParameterBlock(invocation.Pipeline.ParameterLayout);

            var uniforms = new ClusterShadeUniforms
            {
                ViewProj = viewProjection,
                ClipToWorld = clipToWorld,
                View = view.View,
                ScreenWidth = view.ViewportWidth,
                ScreenHeight = view.ViewportHeight,
                ShadingBin = checked((uint)index),
                ShadeBinCount = shadeStages.BinCount,
                CameraPos = cameraPosition,
            };
            int uniformBytes = checked((Unsafe.SizeOf<ClusterShadeUniforms>() + 255) & ~255);
            var uniformBytesStorage = new byte[uniformBytes];
            Span<byte> uniformPacket = uniformBytesStorage;
            uniformPacket.Clear();
            MemoryMarshal.Write(uniformPacket, in uniforms);
            GraphBufferId uniformBuffer = graph.Upload(
                uniformPacket,
                BufferUsages.Constant,
                $"Cluster shade invocation {index} uniforms");

            byte[] scalarPacket = invocation.Scalars.Encode(invocation.Material);
            GraphBufferId scalarBuffer = graph.Upload(
                scalarPacket,
                BufferUsages.ShaderRead,
                $"Cluster shade invocation {index} scalars");

            foreach (ref readonly ClusterLinkedStageResource resource in invocation.Resources.Resources)
            {
                if (resource.Element != 0)
                {
                    throw new NotSupportedException(
                        $"Cluster material resource arrays are not supported: '{resource.Name}[{resource.Element}]'.");
                }
                if (BindShadeCoreResource(
                        ref graph,
                        ref pass,
                        resource,
                        uniformBuffer,
                        scalarBuffer))
                {
                    continue;
                }
                BindShadeMaterialResource(
                    ref graph,
                    ref pass,
                    invocation,
                    resource);
            }
        }
    }

    private bool BindShadeCoreResource(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        in ClusterLinkedStageResource resource,
        GraphBufferId uniformBuffer,
        GraphBufferId scalarBuffer)
    {
        FrameResources frame = _frameResources;
        ResourceBindingType reflectedType = resource.Type;
        switch (resource.Name)
        {
            case "InstanceData":
                RequireBindingType(resource, ResourceBindingType.BufferSrv);
                _ = pass.Bind(
                    graph.CreateBufferSrv(frame.InstanceData, label: "Cluster shade instance data"),
                    PipelineSync.ComputeShading);
                return true;
            case "InstanceProperties":
                RequireBindingType(resource, ResourceBindingType.ConstantBuffer);
                _ = pass.Bind(
                    graph.CreateBufferCbv(
                        frame.InstanceProperties,
                        frame.InstancePropertiesRange,
                        "Cluster shade instance properties"),
                    PipelineSync.ComputeShading);
                return true;
            case "Uniforms":
                RequireBindingType(resource, ResourceBindingType.ConstantBuffer);
                _ = pass.Bind(
                    graph.CreateBufferCbv(uniformBuffer, label: "Cluster shade uniforms"),
                    PipelineSync.ComputeShading);
                return true;
            case "PixelCoordBuffer":
                BindReadOnly(ref graph, ref pass, frame.PixelCoordinates, sizeof(uint), resource.Name);
                return true;
            case "BinOffsets":
                BindReadOnly(ref graph, ref pass, frame.ShadeBinOffsets, sizeof(uint), resource.Name);
                return true;
            case "BinCounts":
                BindReadOnly(ref graph, ref pass, frame.ShadeBinCounts, sizeof(uint), resource.Name);
                return true;
            case "VisBuffer":
                RequireBindingType(resource, ResourceBindingType.TextureSrv);
                _ = pass.Bind(
                    graph.CreateTextureSrv(
                        frame.VisBuffer,
                        format: Format.R32UInt,
                        label: "Cluster shade visibility"),
                    PipelineSync.ComputeShading);
                return true;
            case "DepthBuffer":
                RequireBindingType(resource, ResourceBindingType.TextureSrv);
                _ = pass.Bind(
                    graph.CreateTextureSrv(
                        frame.Depth,
                        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Depth),
                        Format.D32Float,
                        label: "Cluster shade depth"),
                    PipelineSync.ComputeShading);
                return true;
            case "VisibleClusters":
                BindReadOnly(ref graph, ref pass, frame.VisibleClusters, VisibleClusterStride, resource.Name);
                return true;
            case "PageHeap":
                BindReadOnly(ref graph, ref pass, frame.PageHeap, 0, resource.Name);
                return true;
            case "OutputColor":
                RequireBindingType(resource, ResourceBindingType.TextureUav);
                _ = pass.Bind(
                    graph.CreateTextureUav(
                        frame.SceneColor,
                        format: Format.R16G16B16A16Float,
                        label: "Cluster material shade output"),
                    PipelineSync.ComputeShading,
                    GraphAccessMode.Write,
                    WriteCoverage.Partial);
                return true;
            case "DeformCache":
                BindReadOnly(ref graph, ref pass, frame.DeformCache, 0, resource.Name);
                return true;
            case "CacheOffsets":
                BindReadOnly(ref graph, ref pass, frame.CacheOffsets, sizeof(uint), resource.Name);
                return true;
            case "MaterialScalarRegion":
                BindReadOnly(ref graph, ref pass, scalarBuffer, 0, resource.Name);
                return true;
            default:
                return false;
        }

        void BindReadOnly(
            ref RenderGraphFrame activeGraph,
            ref PassDefinition activePass,
            GraphBufferId buffer,
            uint stride,
            string name)
        {
            if (reflectedType != ResourceBindingType.BufferSrv)
            {
                throw new InvalidDataException(
                    $"Linked shade resource '{name}' must be BufferSrv, not {reflectedType}.");
            }
            _ = activePass.Bind(
                activeGraph.CreateBufferSrv(
                    buffer,
                    structureStride: stride,
                    label: $"Cluster shade {name}"),
                PipelineSync.ComputeShading);
        }
    }

    private void BindShadeMaterialResource(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        ClusterShadeInvocation invocation,
        in ClusterLinkedStageResource resource)
    {
        if (!invocation.MaterialResources.TryGetValue(resource.Name, out byte kind))
        {
            throw new InvalidDataException(
                $"Final shade entry requires unknown linked resource '{resource.Name}'.");
        }

        switch (kind)
        {
            case 0:
                RequireBindingType(resource, ResourceBindingType.TextureSrv);
                BindMaterialTexture(ref graph, ref pass, invocation.Material, resource.Name);
                break;
            case 1:
                RequireBindingType(resource, ResourceBindingType.Sampler);
                pass.Bind(ResolveMaterialSampler(invocation.Material, resource.Name));
                break;
            case 2:
                throw new NotSupportedException(
                    $"Cluster shade material buffer '{resource.Name}' has no render-side realization contract.");
            default:
                throw new InvalidDataException(
                    $"Cluster shade material resource '{resource.Name}' has unsupported kind {kind}.");
        }
    }

    private void BindMaterialTexture(
        ref RenderGraphFrame graph,
        ref PassDefinition pass,
        Material material,
        string name)
    {
        object? value = FindMaterialSlot(material, name, out bool found);
        EngineTexture? asset = value switch
        {
            null when !found => null,
            null => null,
            EngineTexture assetTexture => assetTexture,
            _ => throw new InvalidDataException(
                $"Material '{material.Name}' slot '{name}' is not a Texture."),
        };

        RhiTexture textureResource;
        TextureSrv textureView;
        if (asset is null)
        {
            textureResource = _renderDevice.PrepareMissingTexture(ref graph, pass.Id);
            textureView = _renderDevice.MissingTextureSrv;
        }
        else
        {
            textureResource = _renderDevice.PrepareTexture(ref graph, pass.Id, asset);
            textureView = _renderDevice.GetTextureSrv(asset);
        }

        TextureSrvDesc description = textureView.Description;
        GraphTextureId graphTexture = graph.GetImported(textureResource);
        GraphTextureSrvId view = graph.CreateTextureSrv(
            graphTexture,
            description.Range,
            description.Format,
            description.Dimension,
            $"Cluster material {name}");
        _ = pass.Bind(view, PipelineSync.ComputeShading);
    }

    private Sampler ResolveMaterialSampler(Material material, string name)
    {
        object? value = FindMaterialSlot(material, name, out bool found);
        SamplerDesc description = value switch
        {
            SamplerDesc sampler => sampler,
            null when !found => DefaultMaterialSamplerDescription,
            null => DefaultMaterialSamplerDescription,
            _ => throw new InvalidDataException(
                $"Material '{material.Name}' slot '{name}' is not a SamplerDesc."),
        };
        if (_materialSamplers.TryGetValue(description, out Sampler? samplerResource))
            return samplerResource;
        samplerResource = _backend.CreateSampler(_device, description);
        _materialSamplers.Add(description, samplerResource);
        return samplerResource;
    }

    private static object? FindMaterialSlot(Material material, string name, out bool found)
    {
        IReadOnlyList<MaterialSlotDefinition> definitions = material.Slots;
        for (uint slot = 0; slot < definitions.Count; slot++)
        {
            if (!string.Equals(definitions[checked((int)slot)].Name, name, StringComparison.Ordinal))
                continue;
            found = true;
            return material.GetSlotValue(slot);
        }
        found = false;
        return null;
    }

    private static void RequireBindingType(
        in ClusterLinkedStageResource resource,
        ResourceBindingType expected)
    {
        if (resource.Type != expected)
        {
            throw new InvalidDataException(
                $"Linked shade resource '{resource.Name}' must be {expected}, not {resource.Type}.");
        }
    }

    private void DisposeMaterialSamplers(ref List<Exception>? failures)
    {
        foreach (Sampler sampler in _materialSamplers.Values)
        {
            try { sampler.Dispose(); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        _materialSamplers.Clear();
    }

    private static SamplerDesc DefaultMaterialSamplerDescription => new(
        FilterType.Linear,
        FilterType.Linear,
        FilterType.Linear,
        AddressType.Repeat,
        AddressType.Repeat,
        AddressType.Repeat,
        Label: "Cluster default material sampler");
}
