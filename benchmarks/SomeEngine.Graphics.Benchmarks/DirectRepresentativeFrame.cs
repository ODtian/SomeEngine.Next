using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using NativeBarrier = Silk.NET.Direct3D12.ResourceBarrier;
using NativeBuffer = Silk.NET.Direct3D12.ID3D12Resource;
using NativeViewport = Silk.NET.Direct3D12.Viewport;

namespace SomeEngine.Graphics.Benchmarks;

internal static unsafe partial class DirectSilkBenchmarkRunner
{
    private static WorkloadRun RunRepresentativeFrame(
        DirectSilkContext context,
        string shaderManifest,
        in WorkerConfiguration configuration,
        GraphicsWorkload workload,
        bool fastCalls) =>
        fastCalls
            ? RunRepresentativeFrame<DirectFastRepresentativeCalls>(
                context,
                shaderManifest,
                configuration,
                workload)
            : RunRepresentativeFrame<DirectDefaultRepresentativeCalls>(
                context,
                shaderManifest,
                configuration,
                workload);

    private static WorkloadRun RunRepresentativeFrame<TCalls>(
        DirectSilkContext context,
        string shaderManifest,
        in WorkerConfiguration configuration,
        GraphicsWorkload workload)
        where TCalls : struct, IDirectRepresentativeCalls
    {
        byte[] materialSequence = RepresentativeFrameProfile.LoadMaterials(configuration.ShaderDirectory);
        NativeBuffer* target = null;
        NativeBuffer* materialBuffer = null;
        NativeBuffer* objectBuffer = null;
        ID3D12DescriptorHeap* rtvHeap = null;
        byte* materialData = null;
        byte* objectData = null;
        DirectRecordingContext[] contexts = [];
        DirectRepresentativeWorker<TCalls>[] workers = [];
        try
        {
            target = context.CreateTargetTexture();
            rtvHeap = context.CreateRtvHeap(1);
            CpuDescriptorHandle rtv = context.CreateRtv(target, rtvHeap);
            materialBuffer = context.CreateBuffer(
                RepresentativeFrameProfile.MaterialCount * RepresentativeFrameProfile.MaterialStride,
                HeapType.Upload,
                ResourceStates.GenericRead);
            materialData = DirectSilkContext.MapWrite(materialBuffer);
            for (int material = 0; material < RepresentativeFrameProfile.MaterialCount; material++)
            {
                WriteTint(
                    new Span<byte>(
                        materialData + material * RepresentativeFrameProfile.MaterialStride,
                        16),
                    ((material * 17) & 255) / 255f,
                    ((material * 29 + 31) & 255) / 255f,
                    ((material * 43 + 7) & 255) / 255f,
                    1);
            }
            objectBuffer = context.CreateBuffer(
                RepresentativeFrameProfile.ObjectCount * RepresentativeFrameProfile.ObjectPacketSize,
                HeapType.Upload,
                ResourceStates.GenericRead);
            objectData = DirectSilkContext.MapWrite(objectBuffer);
            ulong materialBase = materialBuffer->GetGPUVirtualAddress();
            ulong[] materialAddresses = CreateRepresentativeMaterialAddresses(materialBase);
            ValidateRepresentativeInputs(materialAddresses, materialSequence);
            contexts = CreateDirectRecordingContexts(context.Device);
            if (workload == GraphicsWorkload.RepresentativeFrameParallel)
            {
                workers = CreateDirectRepresentativeWorkers<TCalls>(
                    contexts,
                    context,
                    rtv,
                    materialAddresses,
                    materialSequence);
            }

            var samples = new FrameSample[configuration.MeasuredFrames];
            for (int frame = 0; frame < configuration.WarmupFrames; frame++)
            {
                _ = ExecuteDirectRepresentativeFrame<TCalls>(
                    context,
                    contexts,
                    workers,
                    rtv,
                    materialAddresses,
                    materialSequence,
                    new Span<byte>(
                        objectData,
                        RepresentativeFrameProfile.ObjectCount * RepresentativeFrameProfile.ObjectPacketSize),
                    workload == GraphicsWorkload.RepresentativeFrameParallel,
                    frame);
            }
            for (int frame = 0; frame < samples.Length; frame++)
            {
                samples[frame] = ExecuteDirectRepresentativeFrame<TCalls>(
                    context,
                    contexts,
                    workers,
                    rtv,
                    materialAddresses,
                    materialSequence,
                    new Span<byte>(
                        objectData,
                        RepresentativeFrameProfile.ObjectCount * RepresentativeFrameProfile.ObjectPacketSize),
                    workload == GraphicsWorkload.RepresentativeFrameParallel,
                    frame);
            }

            BarrierEvidence[] barriers = RepresentativeFrameProfile.NativeBarrierCommandCount == 0
                ? []
                :
                [
                    new(0, "MemoryBarrier", 0, 1, "pre-pass dependency"),
                    new(1, "MemoryBarrier", 1, 1, "shadow-to-scene dependency"),
                    new(2, "MemoryBarrier", 2, 1, "shadow-to-scene dependency"),
                    new(3, "MemoryBarrier", 3, 1, "post-pass dependency"),
                ];
            return BenchmarkOutput.Complete(
                workload,
                configuration.Profile,
                configuration.WarmupFrames,
                configuration.MeasuredFrames,
                RepresentativeFrameProfile.LogicalDrawRequestCount,
                RepresentativeFrameProfile.NativeBarrierCommandCount,
                samples,
                [],
                RepresentativeFrameProfile.MaterialSequenceSha256,
                shaderManifest,
                barriers,
                RepresentativeFrameProfile.CreateWorkloadEvidence());
        }
        finally
        {
            DisposeDirectAll(workers);
            DisposeDirectAll(contexts);
            if (objectData is not null)
                objectBuffer->Unmap(0, null);
            if (materialData is not null)
                materialBuffer->Unmap(0, null);
            DirectSilkContext.Release(objectBuffer);
            DirectSilkContext.Release(materialBuffer);
            DirectSilkContext.Release(rtvHeap);
            DirectSilkContext.Release(target);
        }
    }

    private static DirectRecordingContext[] CreateDirectRecordingContexts(ID3D12Device* device)
    {
        var result = new DirectRecordingContext[RepresentativeFrameProfile.CommandListCount];
        try
        {
            for (int index = 0; index < result.Length; index++)
                result[index] = new DirectRecordingContext(device);
            return result;
        }
        catch
        {
            DisposeDirectAll(result);
            throw;
        }
    }

    private static ulong[] CreateRepresentativeMaterialAddresses(ulong materialBase)
    {
        var result = new ulong[RepresentativeFrameProfile.MaterialCount];
        ulong address = materialBase;
        for (int material = 0; material < result.Length; material++)
        {
            result[material] = address;
            address = unchecked(address + RepresentativeFrameProfile.MaterialStride);
        }
        return result;
    }

    private static void ValidateRepresentativeInputs(
        ulong[] materialAddresses,
        byte[] materialSequence)
    {
        if (materialAddresses.Length != RepresentativeFrameProfile.MaterialCount)
        {
            throw new ArgumentException(
                "The representative material address table is invalid.",
                nameof(materialAddresses));
        }
        if (materialSequence.Length != RepresentativeFrameProfile.ObjectCount)
        {
            throw new ArgumentException(
                "The representative material sequence is invalid.",
                nameof(materialSequence));
        }
        for (int worker = 0; worker < RepresentativeFrameProfile.WorkerCount; worker++)
        {
            (int start, int count) = RepresentativeFrameProfile.GetWorkerRange(worker);
            int end = checked(start + count);
            if ((uint)start > (uint)materialSequence.Length ||
                end < start ||
                (uint)end > (uint)materialSequence.Length)
            {
                throw new InvalidDataException(
                    "A representative worker range is outside the material sequence.");
            }
        }
    }

    private static DirectRepresentativeWorker<TCalls>[] CreateDirectRepresentativeWorkers<TCalls>(
        DirectRecordingContext[] contexts,
        DirectSilkContext context,
        CpuDescriptorHandle rtv,
        ulong[] materialAddresses,
        byte[] materialSequence)
        where TCalls : struct, IDirectRepresentativeCalls
    {
        var result = new DirectRepresentativeWorker<TCalls>[
            RepresentativeFrameProfile.WorkerCount];
        try
        {
            for (int worker = 0; worker < result.Length; worker++)
            {
                (int start, int count) = RepresentativeFrameProfile.GetWorkerRange(worker);
                result[worker] = new DirectRepresentativeWorker<TCalls>(
                    contexts[3 + worker],
                    contexts[6 + worker],
                    context,
                    rtv,
                    materialAddresses,
                    materialSequence,
                    start,
                    count,
                    worker);
            }
            return result;
        }
        catch
        {
            DisposeDirectAll(result);
            throw;
        }
    }

    private static FrameSample ExecuteDirectRepresentativeFrame<TCalls>(
        DirectSilkContext context,
        DirectRecordingContext[] contexts,
        DirectRepresentativeWorker<TCalls>[] workers,
        CpuDescriptorHandle rtv,
        ulong[] materialAddresses,
        byte[] materialSequence,
        Span<byte> objectBytes,
        bool parallel,
        int frameIndex)
        where TCalls : struct, IDirectRepresentativeCalls
    {
#if !SOMEENGINE_DISABLE_REPRESENTATIVE_ALLOCATION_MEASUREMENT
        long beforeBytes = GC.GetAllocatedBytesForCurrentThread();
#endif
        long started = Stopwatch.GetTimestamp();
        RepresentativeFrameProfile.WriteObjectPacketsUnchecked(objectBytes, frameIndex);
        RecordDirectMainList<TCalls>(
            contexts[0],
            context,
            rtv,
            barrierCount: 1,
            clear: true);

        long workerBytes = 0;
        if (parallel)
        {
            foreach (DirectRepresentativeWorker<TCalls> worker in workers)
                worker.StartShadow();
            foreach (DirectRepresentativeWorker<TCalls> worker in workers)
                worker.WaitShadow();
            workerBytes += SumDirectWorkerAllocations(workers);
        }
        else
        {
            for (int worker = 0; worker < RepresentativeFrameProfile.WorkerCount; worker++)
            {
                (int start, int count) = RepresentativeFrameProfile.GetWorkerRange(worker);
                RecordDirectPass<TCalls>(
                    contexts[3 + worker],
                    context,
                    rtv,
                    materialAddresses,
                    materialSequence,
                    start,
                    count,
                    scene: false);
            }
        }

        RecordDirectMainList<TCalls>(
            contexts[1],
            context,
            rtv,
            barrierCount: 2,
            clear: false);
        if (parallel)
        {
            foreach (DirectRepresentativeWorker<TCalls> worker in workers)
                worker.StartScene();
            foreach (DirectRepresentativeWorker<TCalls> worker in workers)
                worker.WaitScene();
            workerBytes += SumDirectWorkerAllocations(workers);
        }
        else
        {
            for (int worker = 0; worker < RepresentativeFrameProfile.WorkerCount; worker++)
            {
                (int start, int count) = RepresentativeFrameProfile.GetWorkerRange(worker);
                RecordDirectPass<TCalls>(
                    contexts[6 + worker],
                    context,
                    rtv,
                    materialAddresses,
                    materialSequence,
                    start,
                    count,
                    scene: true);
            }
        }

        RecordDirectMainList<TCalls>(
            contexts[2],
            context,
            rtv,
            barrierCount: 1,
            clear: false);
        long stopped = Stopwatch.GetTimestamp();
#if SOMEENGINE_DISABLE_REPRESENTATIVE_ALLOCATION_MEASUREMENT
        const long bytes = 0;
#else
        long bytes = checked(GC.GetAllocatedBytesForCurrentThread() - beforeBytes + workerBytes);
#endif
        long ticks = stopped - started;
        return new FrameSample(
            frameIndex,
            ticks,
            BenchmarkClock.TicksToMicroseconds(ticks),
            null,
            bytes,
            0,
            checked((ulong)frameIndex + 1),
            0,
            0);
    }

    [SkipLocalsInit]
    private static void RecordDirectMainList<TCalls>(
        DirectRecordingContext recording,
        DirectSilkContext context,
        CpuDescriptorHandle rtv,
        int barrierCount,
        bool clear)
        where TCalls : struct, IDirectRepresentativeCalls
    {
        ID3D12GraphicsCommandList* list = recording.Begin();
#if !REPRESENTATIVE_LIFECYCLE_ONLY && !REPRESENTATIVE_STATE_ONLY
        RecordDirectMemoryBarriers<TCalls>(
            list,
            recording.EnhancedList,
            context.EnhancedBarriers,
            barrierCount);
        if (clear)
        {
            float* color = stackalloc float[4];
            color[0] = 0.0625f;
            color[1] = 0.125f;
            color[2] = 0.25f;
            color[3] = 1;
            TCalls.SetRenderTargets(list, 1, &rtv, null);
            TCalls.ClearRenderTargetView(list, rtv, color);
        }
#endif
        recording.Discard();
    }

    [SkipLocalsInit]
    private static void RecordDirectPass<TCalls>(
        DirectRecordingContext recording,
        DirectSilkContext context,
        CpuDescriptorHandle rtv,
        ulong[] materialAddresses,
        byte[] materialSequence,
        int start,
        int count,
        bool scene)
        where TCalls : struct, IDirectRepresentativeCalls
    {
        ID3D12GraphicsCommandList* list = recording.Begin();
#if !REPRESENTATIVE_LIFECYCLE_ONLY
        NativeViewport viewport = new()
        {
            TopLeftX = 0,
            TopLeftY = 0,
            Width = FixedGraphicsProtocol.RenderWidth,
            Height = FixedGraphicsProtocol.RenderHeight,
            MinDepth = 0,
            MaxDepth = 1,
        };
        Box2D<int> scissor = new(
            0,
            0,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight);
        int end =
#if REPRESENTATIVE_FIXED_ONLY || REPRESENTATIVE_LIFECYCLE_ONLY || REPRESENTATIVE_STATE_ONLY
            start;
#else
            start + count;
#endif
        TCalls.SetPipelineState(list, context.GraphicsPipeline);
        TCalls.SetGraphicsRootSignature(list, context.GraphicsRoot);
        TCalls.SetViewports(list, 1, &viewport);
        TCalls.SetScissors(list, 1, &scissor);
        TCalls.SetPrimitiveTopology(
            list,
            D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
#if !REPRESENTATIVE_STATE_ONLY
        TCalls.SetRenderTargets(list, 1, &rtv, null);
#endif
        RecordDirectDraws<TCalls>(
            list,
            materialAddresses,
            materialSequence,
            start,
            end,
            scene);
#endif
        recording.Discard();
    }

    [SkipLocalsInit]
    private static void RecordDirectDraws<TCalls>(
        ID3D12GraphicsCommandList* list,
        ulong[] materialAddresses,
        byte[] materialSequence,
        int start,
        int end,
        bool scene)
        where TCalls : struct, IDirectRepresentativeCalls
    {
        ref byte firstMaterial = ref MemoryMarshal.GetArrayDataReference(materialSequence);
        ref ulong firstAddress = ref MemoryMarshal.GetArrayDataReference(materialAddresses);
        ulong currentAddress = 0;
#if !REPRESENTATIVE_PER_DRAW_BINDINGS
        if (!scene)
        {
            currentAddress = firstAddress;
            TCalls.SetGraphicsRootConstantBufferView(
                list,
                0,
                currentAddress);
        }
#endif
#if !REPRESENTATIVE_PER_DRAW_BINDINGS
        int currentMaterial = -1;
#endif
        for (int index = start; index < end; index++)
        {
#if REPRESENTATIVE_PER_DRAW_BINDINGS
            int material = scene
                ? Unsafe.Add(ref firstMaterial, index)
                : 0;
            ulong address = Unsafe.Add(ref firstAddress, material);
            if (address != currentAddress)
            {
                TCalls.SetGraphicsRootConstantBufferView(
                    list,
                    0,
                    address);
                currentAddress = address;
            }
#else
            if (scene)
            {
#if REPRESENTATIVE_UNIFORM_MATERIAL
                int material = 0;
#else
                int material = Unsafe.Add(ref firstMaterial, index);
#endif
                if (material != currentMaterial)
                {
                    ulong address = Unsafe.Add(ref firstAddress, material);
                    if (address != currentAddress)
                    {
                        TCalls.SetGraphicsRootConstantBufferView(
                            list,
                            0,
                            address);
                        currentAddress = address;
                    }
                    currentMaterial = material;
                }
            }
#endif
#if !REPRESENTATIVE_BINDINGS_ONLY
            TCalls.DrawInstanced(list, 3, 1, 0, 0);
#endif
        }
    }

    private static void RecordDirectMemoryBarriers<TCalls>(
        ID3D12GraphicsCommandList* list,
        ID3D12GraphicsCommandList10* enhancedList,
        bool enhanced,
        int count)
        where TCalls : struct, IDirectRepresentativeCalls
    {
        if (enhanced)
        {
            GlobalBarrier barrier = new()
            {
                SyncBefore = BarrierSync.Draw,
                SyncAfter = BarrierSync.Draw,
                AccessBefore = BarrierAccess.ConstantBuffer,
                AccessAfter = BarrierAccess.ConstantBuffer,
            };
            BarrierGroup group = new()
            {
                Type = BarrierType.Global,
                NumBarriers = 1,
                Anonymous = new BarrierGroupUnion { PGlobalBarriers = &barrier },
            };
            for (int index = 0; index < count; index++)
                TCalls.Barrier(enhancedList, 1, &group);
            return;
        }

        NativeBarrier legacyBarrier = DirectSilkContext.UavBarrier();
        for (int index = 0; index < count; index++)
            TCalls.ResourceBarrier(list, 1, &legacyBarrier);
    }

    private static long SumDirectWorkerAllocations<TCalls>(
        DirectRepresentativeWorker<TCalls>[] workers)
        where TCalls : struct, IDirectRepresentativeCalls
    {
        long result = 0;
        foreach (DirectRepresentativeWorker<TCalls> worker in workers)
            result = checked(result + worker.TakeAllocatedBytes());
        return result;
    }

    private interface IDirectRepresentativeCalls
    {
        static abstract void SetPipelineState(
            ID3D12GraphicsCommandList* list,
            ID3D12PipelineState* pipeline);
        static abstract void SetGraphicsRootSignature(
            ID3D12GraphicsCommandList* list,
            ID3D12RootSignature* rootSignature);
        static abstract void SetViewports(
            ID3D12GraphicsCommandList* list,
            uint count,
            NativeViewport* viewports);
        static abstract void SetScissors(
            ID3D12GraphicsCommandList* list,
            uint count,
            Box2D<int>* scissors);
        static abstract void SetPrimitiveTopology(
            ID3D12GraphicsCommandList* list,
            D3DPrimitiveTopology topology);
        static abstract void SetRenderTargets(
            ID3D12GraphicsCommandList* list,
            uint count,
            CpuDescriptorHandle* renderTargets,
            CpuDescriptorHandle* depthStencil);
        static abstract void ClearRenderTargetView(
            ID3D12GraphicsCommandList* list,
            CpuDescriptorHandle renderTarget,
            float* color);
        static abstract void SetGraphicsRootConstantBufferView(
            ID3D12GraphicsCommandList* list,
            uint rootParameter,
            ulong address);
        static abstract void DrawInstanced(
            ID3D12GraphicsCommandList* list,
            uint vertexCount,
            uint instanceCount,
            uint firstVertex,
            uint firstInstance);
        static abstract void ResourceBarrier(
            ID3D12GraphicsCommandList* list,
            uint count,
            NativeBarrier* barriers);
        static abstract void Barrier(
            ID3D12GraphicsCommandList10* list,
            uint count,
            BarrierGroup* groups);
    }

    private readonly struct DirectDefaultRepresentativeCalls : IDirectRepresentativeCalls
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetPipelineState(
            ID3D12GraphicsCommandList* list,
            ID3D12PipelineState* pipeline) =>
            list->SetPipelineState(pipeline);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetGraphicsRootSignature(
            ID3D12GraphicsCommandList* list,
            ID3D12RootSignature* rootSignature) =>
            list->SetGraphicsRootSignature(rootSignature);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetViewports(
            ID3D12GraphicsCommandList* list,
            uint count,
            NativeViewport* viewports) =>
            list->RSSetViewports(count, viewports);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetScissors(
            ID3D12GraphicsCommandList* list,
            uint count,
            Box2D<int>* scissors) =>
            list->RSSetScissorRects(count, scissors);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetPrimitiveTopology(
            ID3D12GraphicsCommandList* list,
            D3DPrimitiveTopology topology) =>
            list->IASetPrimitiveTopology(topology);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetRenderTargets(
            ID3D12GraphicsCommandList* list,
            uint count,
            CpuDescriptorHandle* renderTargets,
            CpuDescriptorHandle* depthStencil) =>
            list->OMSetRenderTargets(count, renderTargets, false, depthStencil);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClearRenderTargetView(
            ID3D12GraphicsCommandList* list,
            CpuDescriptorHandle renderTarget,
            float* color) =>
            list->ClearRenderTargetView(renderTarget, color, 0, null);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetGraphicsRootConstantBufferView(
            ID3D12GraphicsCommandList* list,
            uint rootParameter,
            ulong address) =>
            list->SetGraphicsRootConstantBufferView(rootParameter, address);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DrawInstanced(
            ID3D12GraphicsCommandList* list,
            uint vertexCount,
            uint instanceCount,
            uint firstVertex,
            uint firstInstance) =>
            list->DrawInstanced(vertexCount, instanceCount, firstVertex, firstInstance);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ResourceBarrier(
            ID3D12GraphicsCommandList* list,
            uint count,
            NativeBarrier* barriers) =>
            list->ResourceBarrier(count, barriers);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Barrier(
            ID3D12GraphicsCommandList10* list,
            uint count,
            BarrierGroup* groups) =>
            list->Barrier(count, groups);
    }

    private readonly struct DirectFastRepresentativeCalls : IDirectRepresentativeCalls
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetPipelineState(
            ID3D12GraphicsCommandList* list,
            ID3D12PipelineState* pipeline) =>
            DirectD3D12FastCalls.SetPipelineState(list, pipeline);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetGraphicsRootSignature(
            ID3D12GraphicsCommandList* list,
            ID3D12RootSignature* rootSignature) =>
            DirectD3D12FastCalls.SetGraphicsRootSignature(list, rootSignature);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetViewports(
            ID3D12GraphicsCommandList* list,
            uint count,
            NativeViewport* viewports) =>
            DirectD3D12FastCalls.SetViewports(list, count, viewports);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetScissors(
            ID3D12GraphicsCommandList* list,
            uint count,
            Box2D<int>* scissors) =>
            DirectD3D12FastCalls.SetScissors(list, count, scissors);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetPrimitiveTopology(
            ID3D12GraphicsCommandList* list,
            D3DPrimitiveTopology topology) =>
            DirectD3D12FastCalls.SetPrimitiveTopology(list, topology);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetRenderTargets(
            ID3D12GraphicsCommandList* list,
            uint count,
            CpuDescriptorHandle* renderTargets,
            CpuDescriptorHandle* depthStencil) =>
            DirectD3D12FastCalls.SetRenderTargets(
                list,
                count,
                renderTargets,
                depthStencil);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClearRenderTargetView(
            ID3D12GraphicsCommandList* list,
            CpuDescriptorHandle renderTarget,
            float* color) =>
            DirectD3D12FastCalls.ClearRenderTargetView(list, renderTarget, color);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetGraphicsRootConstantBufferView(
            ID3D12GraphicsCommandList* list,
            uint rootParameter,
            ulong address) =>
            DirectD3D12FastCalls.SetGraphicsRootConstantBufferView(
                list,
                rootParameter,
                address);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DrawInstanced(
            ID3D12GraphicsCommandList* list,
            uint vertexCount,
            uint instanceCount,
            uint firstVertex,
            uint firstInstance) =>
            DirectD3D12FastCalls.DrawInstanced(
                list,
                vertexCount,
                instanceCount,
                firstVertex,
                firstInstance);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ResourceBarrier(
            ID3D12GraphicsCommandList* list,
            uint count,
            NativeBarrier* barriers) =>
            DirectD3D12FastCalls.ResourceBarrier(list, count, barriers);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Barrier(
            ID3D12GraphicsCommandList10* list,
            uint count,
            BarrierGroup* groups) =>
            DirectD3D12FastCalls.Barrier(list, count, groups);
    }

    private static void DisposeDirectAll<T>(T[] values)
        where T : class, IDisposable
    {
        for (int index = values.Length - 1; index >= 0; index--)
            values[index]?.Dispose();
    }

    private sealed class DirectRecordingContext : IDisposable
    {
        private ID3D12CommandAllocator* _allocator;
        private ID3D12GraphicsCommandList10* _list;

        internal DirectRecordingContext(ID3D12Device* device)
        {
            Guid iid = ID3D12CommandAllocator.Guid;
            ID3D12CommandAllocator* allocator = null;
            DirectSilkContext.Check(
                device->CreateCommandAllocator(CommandListType.Direct, &iid, (void**)&allocator),
                "ID3D12Device::CreateCommandAllocator(representative)");
            _allocator = allocator;
            try
            {
                iid = ID3D12GraphicsCommandList10.Guid;
                ID3D12GraphicsCommandList10* list = null;
                DirectSilkContext.Check(
                    device->CreateCommandList(
                        0,
                        CommandListType.Direct,
                        _allocator,
                        null,
                        &iid,
                        (void**)&list),
                    "ID3D12Device::CreateCommandList(representative)");
                _list = list;
                DirectSilkContext.Check(
                    _list->Close(),
                    "ID3D12GraphicsCommandList::Close(representative initial)");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal ID3D12GraphicsCommandList10* EnhancedList => _list;

        internal ID3D12GraphicsCommandList* Begin()
        {
            DirectSilkContext.Check(
                _allocator->Reset(),
                "ID3D12CommandAllocator::Reset(representative)");
            DirectSilkContext.Check(
                _list->Reset(_allocator, null),
                "ID3D12GraphicsCommandList::Reset(representative)");
            return (ID3D12GraphicsCommandList*)_list;
        }

        internal void Discard() => DirectSilkContext.Check(
            _list->Close(),
            "ID3D12GraphicsCommandList::Close(representative discard)");

        public void Dispose()
        {
            DirectSilkContext.Release(_list);
            _list = null;
            DirectSilkContext.Release(_allocator);
            _allocator = null;
        }
    }

    private sealed class DirectRepresentativeWorker<TCalls> : IDisposable
        where TCalls : struct, IDirectRepresentativeCalls
    {
        private readonly DirectRecordingContext _shadowContext;
        private readonly DirectRecordingContext _sceneContext;
        private readonly DirectSilkContext _context;
        private readonly CpuDescriptorHandle _rtv;
        private readonly ulong[] _materialAddresses;
        private readonly byte[] _materialSequence;
        private readonly int _start;
        private readonly int _count;
        private readonly RepresentativeWorkerSignal _shadowStart = new();
        private readonly RepresentativeWorkerSignal _shadowDone = new();
        private readonly RepresentativeWorkerSignal _sceneStart = new();
        private readonly RepresentativeWorkerSignal _sceneDone = new();
        private readonly Thread _thread;
        private ExceptionDispatchInfo? _failure;
        private long _allocatedBytes;
        private bool _stop;

        internal DirectRepresentativeWorker(
            DirectRecordingContext shadowContext,
            DirectRecordingContext sceneContext,
            DirectSilkContext context,
            CpuDescriptorHandle rtv,
            ulong[] materialAddresses,
            byte[] materialSequence,
            int start,
            int count,
            int workerIndex)
        {
            _shadowContext = shadowContext;
            _sceneContext = sceneContext;
            _context = context;
            _rtv = rtv;
            _materialAddresses = materialAddresses;
            _materialSequence = materialSequence;
            _start = start;
            _count = count;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = $"Direct representative worker {workerIndex}",
            };
            _thread.Start();
        }

        internal void StartShadow()
        {
            _failure = null;
            _allocatedBytes = 0;
            _shadowStart.Signal();
        }

        internal void WaitShadow()
        {
            _shadowDone.Wait();
            _failure?.Throw();
        }

        internal void StartScene() => _sceneStart.Signal();

        internal void WaitScene()
        {
            _sceneDone.Wait();
            _failure?.Throw();
        }

        internal long TakeAllocatedBytes()
        {
            long result = _allocatedBytes;
            _allocatedBytes = 0;
            return result;
        }

        public void Dispose()
        {
            Volatile.Write(ref _stop, true);
            _shadowStart.Signal();
            _sceneStart.Signal();
            _thread.Join();
            _sceneDone.Dispose();
            _sceneStart.Dispose();
            _shadowDone.Dispose();
            _shadowStart.Dispose();
        }

        private void Run()
        {
            while (true)
            {
                _shadowStart.Wait();
                if (Volatile.Read(ref _stop))
                    return;
                RunPhase(_shadowContext, scene: false, _shadowDone);
                if (_failure is not null)
                    continue;
                _sceneStart.Wait();
                if (Volatile.Read(ref _stop))
                    return;
                RunPhase(_sceneContext, scene: true, _sceneDone);
            }
        }

        private void RunPhase(
            DirectRecordingContext recording,
            bool scene,
            RepresentativeWorkerSignal completed)
        {
            try
            {
#if !SOMEENGINE_DISABLE_REPRESENTATIVE_ALLOCATION_MEASUREMENT
                long before = GC.GetAllocatedBytesForCurrentThread();
#endif
                RecordDirectPass<TCalls>(
                    recording,
                    _context,
                    _rtv,
                    _materialAddresses,
                    _materialSequence,
                    _start,
                    _count,
                    scene);
#if !SOMEENGINE_DISABLE_REPRESENTATIVE_ALLOCATION_MEASUREMENT
                _allocatedBytes = checked(
                    _allocatedBytes + GC.GetAllocatedBytesForCurrentThread() - before);
#endif
            }
            catch (Exception exception)
            {
                _failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                completed.Signal();
            }
        }
    }
}
