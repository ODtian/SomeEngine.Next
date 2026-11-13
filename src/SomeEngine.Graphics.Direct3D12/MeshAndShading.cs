using Silk.NET.Direct3D12;
using NativeIndirectArgumentDesc = Silk.NET.Direct3D12.IndirectArgumentDesc;
using NativeIndirectArgumentType = Silk.NET.Direct3D12.IndirectArgumentType;
using NativeShadingRate = Silk.NET.Direct3D12.ShadingRate;
using NativeShadingRateCombiner = Silk.NET.Direct3D12.ShadingRateCombiner;

namespace SomeEngine.Graphics.Direct3D12;
internal sealed unsafe partial class D3D12Backend
{
    public void DispatchMesh(CommandContext context, in DispatchArguments arguments)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();

        CommandExecutionTrace.DispatchMesh(command, command.List, arguments.X, arguments.Y, arguments.Z);
    }

    public void DispatchMeshIndirect(CommandContext context, in BufferRegion arguments)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();

        D3D12Buffer buffer = RequireBuffer(arguments.Buffer);
        BufferRange range = arguments.Range.Expand(buffer.Info.Size);
        CommandExecutionTrace.ExecuteIndirect(command, command.List, command.NativeDevice.MeshDispatchSignature, 1, buffer.Native, range.Offset, null, 0);
    }

    public void SetShadingRate(
        CommandContext context,
        ShadingRate rate,
        ShadingRateCombiner primitiveCombiner,
        ShadingRateCombiner imageCombiner
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();

        if (command.ShadingRateEquals(rate, primitiveCombiner, imageCombiner))
            return;
        NativeShadingRateCombiner* combiners = stackalloc NativeShadingRateCombiner[2]
        {
            ToNativeShadingRateCombiner(primitiveCombiner),
            ToNativeShadingRateCombiner(imageCombiner),
        };
        CommandBindingTrace.RSSetShadingRate(command, command.List, ToNativeShadingRate(rate), combiners);
        command.RememberShadingRate(rate, primitiveCombiner, imageCombiner);
    }

    public void SetShadingRateImage(CommandContext context, Texture? texture)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();

        if (command.ShadingRateImageEquals(texture))
            return;
        D3D12TextureResource? native = null;
        if (texture is not null)
        {
            native = RequireTexture(texture);
            command.PrepareSwapchainUses(1);
        }

        ID3D12Resource* shadingRateImage = native is null ? null : native.Native;
        CommandBindingTrace.RSSetShadingRateImage(command, command.List, shadingRateImage);
        command.RememberShadingRateImage(texture);
    }

    private static NativeShadingRate ToNativeShadingRate(ShadingRate value) => value switch
    {
        ShadingRate.Rate1x1 => NativeShadingRate.Rate1X1,
        ShadingRate.Rate1x2 => NativeShadingRate.Rate1X2,
        ShadingRate.Rate2x1 => NativeShadingRate.Rate2X1,
        ShadingRate.Rate2x2 => NativeShadingRate.Rate2X2,
        ShadingRate.Rate2x4 => NativeShadingRate.Rate2X4,
        ShadingRate.Rate4x2 => NativeShadingRate.Rate4X2,
        ShadingRate.Rate4x4 => NativeShadingRate.Rate4X4,
        _ => default!,
    };
    private static NativeShadingRateCombiner ToNativeShadingRateCombiner(ShadingRateCombiner value) => value switch
    {
        ShadingRateCombiner.Passthrough => NativeShadingRateCombiner.Passthrough,
        ShadingRateCombiner.Override => NativeShadingRateCombiner.Override,
        ShadingRateCombiner.Minimum => NativeShadingRateCombiner.Min,
        ShadingRateCombiner.Maximum => NativeShadingRateCombiner.Max,
        ShadingRateCombiner.Sum => NativeShadingRateCombiner.Sum,
        _ => default!,
    };
    private sealed partial class D3D12Device
    {
        private ID3D12CommandSignature* _meshDispatchSignature;
        private ID3D12CommandSignature* _rayDispatchSignature;
        internal ID3D12CommandSignature* MeshDispatchSignature => _meshDispatchSignature;
        internal ID3D12CommandSignature* RayDispatchSignature => _rayDispatchSignature;

        private void InitializeRayDispatchSignature()
        {
            NativeIndirectArgumentDesc argument = new()
            {
                Type = NativeIndirectArgumentType.DispatchRays,
            };
            CommandSignatureDesc description = new(checked((uint)sizeof(Silk.NET.Direct3D12.DispatchRaysDesc)), 1, &argument, EnabledNodeMask);
            Guid iid = ID3D12CommandSignature.Guid;
            ID3D12CommandSignature* signature = null;
            ThrowIfFailed(
                this,
                DevicePipelineTrace.CreateCommandSignature(Native, &description, null, &iid, (void**)&signature),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateCommandSignature(DispatchRays)"
            );
            _rayDispatchSignature = signature;
        }

        private void InitializeMeshDispatchSignature()
        {
            NativeIndirectArgumentDesc argument = new()
            {
                Type = NativeIndirectArgumentType.DispatchMesh,
            };
            CommandSignatureDesc description = new(12, 1, &argument, EnabledNodeMask);
            Guid iid = ID3D12CommandSignature.Guid;
            ID3D12CommandSignature* signature = null;
            ThrowIfFailed(
                this,
                DevicePipelineTrace.CreateCommandSignature(Native, &description, null, &iid, (void**)&signature),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateCommandSignature(DispatchMesh)"
            );
            _meshDispatchSignature = signature;
        }

        private void ReleaseAdvancedCommandSignatures()
        {
            ID3D12CommandSignature* ray = _rayDispatchSignature;
            _rayDispatchSignature = null;
            if (ray is not null)
                _ = ResourceTrace.Release(ray);
            ID3D12CommandSignature* mesh = _meshDispatchSignature;
            _meshDispatchSignature = null;
            if (mesh is not null)
                _ = ResourceTrace.Release(mesh);
        }
    }

    private sealed partial class D3D12CommandContext
    {
        private (ShadingRate Rate, ShadingRateCombiner Primitive, ShadingRateCombiner Image)? _shadingRate;
        private Texture? _shadingRateImage;
        private bool _hasShadingRateImage;
        internal bool ShadingRateEquals(ShadingRate rate, ShadingRateCombiner primitive, ShadingRateCombiner image) => _shadingRate is { } current && current.Rate == rate && current.Primitive == primitive && current.Image == image;
        internal void RememberShadingRate(ShadingRate rate, ShadingRateCombiner primitive, ShadingRateCombiner image) => _shadingRate = (rate, primitive, image);
        internal bool ShadingRateImageEquals(Texture? texture) => _hasShadingRateImage && ReferenceEquals(_shadingRateImage, texture);
        internal void RememberShadingRateImage(Texture? texture)
        {
            _shadingRateImage = texture;
            _hasShadingRateImage = true;
        }

        internal void ResetMeshAndShadingState()
        {
            _shadingRate = null;
            _shadingRateImage = null;
            _hasShadingRateImage = false;
        }
    }
}
