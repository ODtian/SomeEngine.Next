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
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        _ = command.NativeDevice.RequireCapability<MeshShaders>(nameof(DispatchMesh));
        command.List->DispatchMesh(arguments.X, arguments.Y, arguments.Z);
    }

    public void DispatchMeshIndirect(CommandContext context, in BufferRegion arguments)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        MeshShaders capability =
            command.NativeDevice.RequireCapability<MeshShaders>(nameof(DispatchMeshIndirect));
        if (!capability.IndirectDispatch)
            throw new NotSupportedException("Indirect mesh dispatch is unavailable.");
        D3D12Buffer buffer = RequireBuffer(arguments.Buffer);
        BufferRange range = arguments.Range.Resolve(buffer.Info.Size);
        if ((buffer.Info.Usages & BufferUsages.Indirect) == 0 ||
            range.Offset % 4 != 0 ||
            range.Size < 12)
        {
            throw new ArgumentException(
                "The indirect mesh arguments must name an aligned 12-byte Indirect Buffer range.",
                nameof(arguments));
        }
        command.PrepareCaptures(1, 0, 1);
        command.Capture(buffer);
        command.List->ExecuteIndirect(
            command.NativeDevice.MeshDispatchSignature,
            1,
            buffer.Native,
            range.Offset,
            null,
            0);
    }

    public void SetShadingRate(
        CommandContext context,
        ShadingRate rate,
        ShadingRateCombiner primitiveCombiner,
        ShadingRateCombiner imageCombiner)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        VariableRateShading capability =
            command.NativeDevice.RequireCapability<VariableRateShading>(nameof(SetShadingRate));
        if (!capability.Rates.Contains(rate) ||
            !capability.Combiners.Contains(primitiveCombiner) ||
            !capability.Combiners.Contains(imageCombiner))
        {
            throw new NotSupportedException(
                "The requested shading rate or combiner is not advertised by the Device.");
        }
        if (command.ShadingRateEquals(rate, primitiveCombiner, imageCombiner))
            return;

        NativeShadingRateCombiner* combiners = stackalloc NativeShadingRateCombiner[2]
        {
            ToNativeShadingRateCombiner(primitiveCombiner),
            ToNativeShadingRateCombiner(imageCombiner),
        };
        command.List->RSSetShadingRate(ToNativeShadingRate(rate), combiners);
        command.RememberShadingRate(rate, primitiveCombiner, imageCombiner);
    }

    public void SetShadingRateImage(CommandContext context, Texture? texture)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        VariableRateShading capability = command.NativeDevice
            .RequireCapability<VariableRateShading>(nameof(SetShadingRateImage));
        if (!capability.ShadingRateImage)
            throw new NotSupportedException("Shading-rate images are unavailable.");
        if (command.ShadingRateImageEquals(texture))
            return;

        D3D12TextureResource? native = null;
        if (texture is not null)
        {
            native = RequireTexture(texture);
            command.PrepareCaptures(1, 0, 1);
            command.PrepareSwapchainUses(1);
            command.Capture(native);
        }

        ID3D12Resource* shadingRateImage = native is null ? null : native.Native;
        command.List->RSSetShadingRateImage(shadingRateImage);
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
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static NativeShadingRateCombiner ToNativeShadingRateCombiner(
        ShadingRateCombiner value) => value switch
    {
        ShadingRateCombiner.Passthrough => NativeShadingRateCombiner.Passthrough,
        ShadingRateCombiner.Override => NativeShadingRateCombiner.Override,
        ShadingRateCombiner.Minimum => NativeShadingRateCombiner.Min,
        ShadingRateCombiner.Maximum => NativeShadingRateCombiner.Max,
        ShadingRateCombiner.Sum => NativeShadingRateCombiner.Sum,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private sealed partial class D3D12Device
    {
        private ID3D12CommandSignature* _meshDispatchSignature;
        private ID3D12CommandSignature* _rayDispatchSignature;

        internal ID3D12CommandSignature* MeshDispatchSignature =>
            _meshDispatchSignature;

        internal ID3D12CommandSignature* RayDispatchSignature =>
            _rayDispatchSignature;

        private void InitializeRayDispatchSignature()
        {
            NativeIndirectArgumentDesc argument = new()
            {
                Type = NativeIndirectArgumentType.DispatchRays,
            };
            CommandSignatureDesc description = new(
                checked((uint)sizeof(Silk.NET.Direct3D12.DispatchRaysDesc)),
                1,
                &argument,
                EnabledNodeMask);
            Guid iid = ID3D12CommandSignature.Guid;
            ID3D12CommandSignature* signature = null;
            ThrowIfFailed(
                this,
                Native->CreateCommandSignature(
                    &description,
                    null,
                    &iid,
                    (void**)&signature),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateCommandSignature(DispatchRays)");
            _rayDispatchSignature = signature;
        }

        private void InitializeMeshDispatchSignature()
        {
            NativeIndirectArgumentDesc argument = new()
            {
                Type = NativeIndirectArgumentType.DispatchMesh,
            };
            CommandSignatureDesc description = new(
                12,
                1,
                &argument,
                EnabledNodeMask);
            Guid iid = ID3D12CommandSignature.Guid;
            ID3D12CommandSignature* signature = null;
            ThrowIfFailed(
                this,
                Native->CreateCommandSignature(
                    &description,
                    null,
                    &iid,
                    (void**)&signature),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateCommandSignature(DispatchMesh)");
            _meshDispatchSignature = signature;
        }

        private void ReleaseAdvancedCommandSignatures()
        {
            ID3D12CommandSignature* ray = _rayDispatchSignature;
            _rayDispatchSignature = null;
            if (ray is not null)
                _ = ray->Release();
            ID3D12CommandSignature* mesh = _meshDispatchSignature;
            _meshDispatchSignature = null;
            if (mesh is not null)
                _ = mesh->Release();
        }
    }

    private sealed partial class D3D12CommandContext
    {
        private (ShadingRate Rate, ShadingRateCombiner Primitive, ShadingRateCombiner Image)?
            _shadingRate;
        private Texture? _shadingRateImage;
        private bool _hasShadingRateImage;

        internal bool ShadingRateEquals(
            ShadingRate rate,
            ShadingRateCombiner primitive,
            ShadingRateCombiner image) =>
            _shadingRate is { } current &&
            current.Rate == rate &&
            current.Primitive == primitive &&
            current.Image == image;

        internal void RememberShadingRate(
            ShadingRate rate,
            ShadingRateCombiner primitive,
            ShadingRateCombiner image) =>
            _shadingRate = (rate, primitive, image);

        internal bool ShadingRateImageEquals(Texture? texture) =>
            _hasShadingRateImage && ReferenceEquals(_shadingRateImage, texture);

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
