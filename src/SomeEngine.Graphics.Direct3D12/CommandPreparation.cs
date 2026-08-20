namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private sealed partial class D3D12CommandContext
    {
        internal void PrepareCaptures(
            int resources,
            int descriptors = 0,
            int sparseGenerations = 0) =>
            Recording.PrepareCaptures(resources, descriptors, sparseGenerations);

        internal void PrepareSwapchainUses(int count) => Recording.PrepareSwapchainUses(count);

        internal void PrepareBundles(int count) => Recording.PrepareBundles(count);

        internal bool TryCapturePersistentParameterData(
            D3D12PersistentParameterData data) =>
            Recording.TryCapturePersistentParameterData(data);

        internal void PrepareTransientObjects(int count) =>
            Recording.PrepareTransientObjects(count);

        internal void PrepareOrdinaryData(ulong byteCount) =>
            Recording.PrepareOrdinaryDataCapacity(byteCount);

        internal void PrepareDescriptors(uint resourceCount, uint samplerCount) =>
            Recording.PrepareDescriptors(resourceCount, samplerCount);

        internal void PrepareDescriptorTables(ReadOnlySpan<DefaultRootTable> tables) =>
            Recording.PrepareDescriptorTables(tables);

        internal void PrepareAttachmentDescriptors(uint renderTargetCount, uint depthStencilCount) =>
            Recording.PrepareAttachmentDescriptors(renderTargetCount, depthStencilCount);

        internal void PrepareViewports(int count)
        {
            if (count > _viewports.Length)
                Array.Resize(ref _viewports, count);
        }

        internal void PrepareScissors(int count)
        {
            if (count > _scissors.Length)
                Array.Resize(ref _scissors, count);
        }

        internal void PrepareBindingStorage(
            int resourceCount,
            int ordinaryDataByteCount)
        {
            if (resourceCount > _transientBindingResources.Length)
                Array.Resize(ref _transientBindingResources, resourceCount);
            if (ordinaryDataByteCount > _transientBindingOrdinaryData.Length)
                Array.Resize(ref _transientBindingOrdinaryData, ordinaryDataByteCount);
        }

        internal void PrepareRootConstants(
            uint rootParameter,
            uint constantCount)
        {
            int slot = EnsureRootStateCapacity(rootParameter);
            int byteCount = checked((int)(constantCount * sizeof(uint)));
            if (_rootConstants[slot] is not byte[] values || values.Length != byteCount)
                _rootConstants[slot] = new byte[byteCount];
        }

        internal void PrepareResolvedResources(int count)
        {
            if (count > _resolvedEncodeResources.Length)
                Array.Resize(ref _resolvedEncodeResources, count);
        }

        internal void PrepareRecordedRayTable(
            int tableCount,
            int poolCount,
            int parameterBlockCount,
            int resourceCount,
            int ordinaryDataByteCount,
            int rayGenerationCount,
            int missCount,
            int hitCount,
            int callableCount) =>
            Recording.PrepareRecordedRayTable(
                tableCount,
                poolCount,
                parameterBlockCount,
                resourceCount,
                ordinaryDataByteCount,
                rayGenerationCount,
                missCount,
                hitCount,
                callableCount);

        internal void PrepareAccelerationStructureGeometries(int count) =>
            Recording.PrepareAccelerationStructureGeometries(count);

        internal void StoreResolvedResource(int index, object resource) =>
            _resolvedEncodeResources[index] = resource;

        internal T GetResolvedResource<T>(int index) where T : class =>
            (T)_resolvedEncodeResources[index];

        internal void ClearResolvedResources(int count) =>
            Array.Clear(_resolvedEncodeResources, 0, count);

        internal void CaptureResolvedResources(int count)
        {
            try
            {
                for (int index = 0; index < count; index++)
                {
                    switch (_resolvedEncodeResources[index])
                    {
                        case D3D12Buffer buffer:
                            Capture(buffer);
                            break;
                        case D3D12TextureResource texture:
                            Capture(texture);
                            break;
                    }
                }
            }
            finally
            {
                Array.Clear(_resolvedEncodeResources, 0, count);
            }
        }

    }

    private sealed partial class D3D12CommandSlot
    {
        internal void PrepareCaptures(
            int resourceCount,
            int descriptorCount,
            int sparseGenerationCount) =>
            _captures.PrepareCapacity(
                resourceCount,
                descriptorCount,
                sparseGenerationCount);

        internal void PrepareSwapchainUses(int count)
        {
            if (count == 0)
                return;
            int required = checked(_swapchainUses.Count + count);
            if (required > _swapchainUseCapacity)
                _swapchainUseCapacity = _swapchainUses.EnsureCapacity(required);
            if (_swapchainSequences.Length < count)
                Array.Resize(ref _swapchainSequences, count);
        }

        internal void PrepareBundles(int count)
        {
            if (count == 0)
                return;
            int required = checked(_capturedBundles.Count + count);
            if (required > _capturedBundleCapacity)
                _capturedBundleCapacity = _capturedBundles.EnsureCapacity(required);
        }

        internal bool TryCapturePersistentParameterData(
            D3D12PersistentParameterData data)
        {
            if (!_capturedParameterData.Add(data))
                return true;
            try
            {
                if (!data.TryRetain())
                {
                    _capturedParameterData.Remove(data);
                    return false;
                }
                CaptureSwapchainUses(data.SwapchainImages);
                return true;
            }
            catch
            {
                _capturedParameterData.Remove(data);
                data.Release();
                throw;
            }
        }

        internal void PrepareTransientObjects(int count)
        {
            if (count == 0)
                return;
            int required = checked(_transientObjects.Count + count);
            if (required > _transientObjectCapacity)
                _transientObjectCapacity = _transientObjects.EnsureCapacity(required);
        }

        internal void PrepareDescriptors(uint resourceCount, uint samplerCount)
        {
            if (resourceCount == 0 && samplerCount == 0)
                return;
            EnsureDescriptorArenaReady();
            EnsureRecordingCapacity(
                checked(_resourceUsed + resourceCount),
                checked(_samplerUsed + samplerCount));
        }

        internal void PrepareAttachmentDescriptors(
            uint renderTargetCount,
            uint depthStencilCount) =>
            PrepareTemporaryAttachmentCapacity(renderTargetCount, depthStencilCount);

        internal void PrepareInitialCaptureCapacity(uint capacity)
        {
            if (capacity == 0)
                return;
            int resolved = checked((int)capacity);
            _captures.PrepareCapacity(resolved, resolved, resolved);
        }
    }
}
