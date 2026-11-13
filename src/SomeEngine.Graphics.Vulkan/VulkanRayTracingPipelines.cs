using System.Buffers;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan;
internal sealed unsafe partial class VulkanBackend
{
    private Pipeline CreateRayTracingPipelineCore(
        RhiDevice device,
        in RayTracingPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache,
        SlangShaderData? capturedSlangCode = null
    )
    {
        VulkanDevice nativeDevice = RequireRayTracingDevice(device);
        VulkanPipelineCache? nativeCache = ResolvePipelineCache(nativeDevice, cache);
        ShaderReflection reflection = desc.Reflection;
        RayPipelineBuild build = BuildRayPipeline(desc, reflection);
        VulkanPipelineLayoutState layout = VulkanPipelineLayoutCompiler.Compile(nativeDevice, reflection, build.Entries, desc.StaticSamplers);
        VkPipeline native = default;
        VulkanPipeline? pipeline = null;
        SlangShaderData? retainedCode = null;
        try
        {
            if (nativeCache is null)
                native = CreateRayPipelineNative(nativeDevice, layout, desc, build, default);
            else
                lock (nativeCache.Gate)
                    native = CreateRayPipelineNative(nativeDevice, layout, desc, build, nativeCache.Native);
            PhysicalDeviceRayTracingPipelinePropertiesKHR properties = GetRayPipelineProperties(nativeDevice);
            var state = new VulkanRayTracingPipelineState(
                build.GeneralGroups,
                build.HitGroups,
                checked((uint)build.Groups.Length),
                properties.ShaderGroupHandleSize,
                properties.ShaderGroupHandleAlignment,
                properties.ShaderGroupBaseAlignment,
                properties.MaxShaderGroupStride
            );
            retainedCode = capturedSlangCode ?? desc.SlangCode;
            pipeline = new VulkanPipeline(nativeDevice, native, layout, retainedCode, PipelineType.RayTracing, desc.Label, state);
            retainedCode = null;
            return RegisterChildOrDispose(nativeDevice, pipeline);
        }
        catch
        {
            if (pipeline is null && native.Handle != 0)
                Api.DestroyPipeline(nativeDevice.Native, native, null);
            if (pipeline is null)
                layout.Release();
            throw;
        }
    }

    private Task<Pipeline> CreateRayTracingPipelineAsyncCore(RhiDevice device, in RayTracingPipelineDesc desc, SomeEngine.Graphics.PipelineCache? cache)
    {
        VulkanDevice nativeDevice = RequireRayTracingDevice(device);
        VulkanPipelineCache? nativeCache = ResolvePipelineCache(nativeDevice, cache);
        SlangShaderData slangCode = desc.SlangCode;
        var data = new RayPipelineData(desc);
        return EnqueuePipelineCreation(nativeDevice, nativeCache, slangCode, data, () => data.Create(this, nativeDevice, nativeCache, slangCode));
    }

    private RayTracingShaderTable CreateRayTracingShaderTableCore(RhiDevice device, in RayTracingShaderTableDesc desc)
    {
        VulkanDevice nativeDevice = RequireRayTracingDevice(device);
        VulkanPipeline pipeline = RequirePipeline(nativeDevice, desc.Pipeline, nameof(desc));
        pipeline.RetainNative();
        VulkanRayTracingShaderTable table;
        try
        {
            table = new VulkanRayTracingShaderTable(nativeDevice, pipeline, desc);
        }
        catch
        {
            pipeline.ReleaseNative();
            throw;
        }

        return RegisterChildOrDispose(nativeDevice, table);
    }

    private void UpdateRayTracingShaderTableCore(CommandContext context, RayTracingShaderTable table, in RayTracingShaderTableUpdate update)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanRayTracingShaderTable native = (VulkanRayTracingShaderTable)table;
        VulkanShaderTableGeneration generation = native.CreateGeneration(update);
        native.Publish(generation);
        command.RetainDescriptorData(generation);
    }

    private void DispatchRaysCore(CommandContext context, in DispatchRaysDesc desc)
    {
        VulkanCommandContext command = ((VulkanCommandContext)context);
        VulkanRayTracingShaderTable table = (VulkanRayTracingShaderTable)desc.ShaderTable;
        VulkanShaderTableGeneration generation = table.AcquireGeneration();
        try
        {
            command.RetainDescriptorData(generation);
            VulkanDevice device = (VulkanDevice)command.Device;
            StridedDeviceAddressRegionKHR rayGeneration = generation.RayGeneration;
            StridedDeviceAddressRegionKHR miss = generation.Miss;
            StridedDeviceAddressRegionKHR hit = generation.Hit;
            StridedDeviceAddressRegionKHR callable = generation.Callable;
            device.RayTracingPipelineApi.CmdTraceRays(command.NativeRecording, &rayGeneration, &miss, &hit, &callable, desc.Width, desc.Height, desc.Depth);
        }
        finally
        {
            generation.ReleaseNative();
        }
    }

    private VkPipeline CreateRayPipelineNative(
        VulkanDevice device,
        VulkanPipelineLayoutState layout,
        in RayTracingPipelineDesc desc,
        RayPipelineBuild build,
        VkPipelineCache cache
    )
    {
        VkShaderModule[] modules = new VkShaderModule[build.Stages.Length];
        nint[] names = new nint[build.Stages.Length];
        PipelineShaderStageCreateInfo[] stages = new PipelineShaderStageCreateInfo[build.Stages.Length];
        try
        {
            for (int index = 0; index < stages.Length; index++)
            {
                CompiledSpirv shader = CompileSpirv(
                    build.Reflection,
                    layout,
                    build.Stages[index].Entry,
                    desc.Code,
                    desc.CodeBlob,
                    build.Stages[index].SlangStage,
                    "ray tracing"
                );
                modules[index] = CreateShaderBinary(device, shader.Code);
                names[index] = SilkMarshal.StringToPtr(shader.Name);
                stages[index] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = build.Stages[index].NativeStage,
                    Module = modules[index],
                    PName = (byte*)names[index],
                };
            }

            fixed (PipelineShaderStageCreateInfo* stagePointer = stages)
            fixed (RayTracingShaderGroupCreateInfoKHR* groupPointer = build.Groups)
            {
                RayTracingPipelineCreateInfoKHR createInfo = new()
                {
                    SType = StructureType.RayTracingPipelineCreateInfoKhr,
                    Flags = ToNative(desc.Options),
                    StageCount = checked((uint)stages.Length),
                    PStages = stagePointer,
                    GroupCount = checked((uint)build.Groups.Length),
                    PGroups = groupPointer,
                    MaxPipelineRayRecursionDepth = desc.MaximumRecursionDepth,
                    Layout = layout.Native,
                };
                VkPipeline native = default;
                Result result = device.RayTracingPipelineApi.CreateRayTracingPipelines(device.Native, default, cache, 1, &createInfo, null, &native);
                ThrowPipelineFailure(device, result, "vkCreateRayTracingPipelinesKHR");
                return native;
            }
        }
        finally
        {
            for (int index = 0; index < modules.Length; index++)
            {
                if (names[index] != 0)
                    SilkMarshal.Free(names[index]);
                if (modules[index].Handle != 0)
                    Api.DestroyShaderModule(device.Native, modules[index], null);
            }
        }
    }

    private static RayPipelineBuild BuildRayPipeline(in RayTracingPipelineDesc desc, ShaderReflection reflection)
    {
        var stages = new List<RayStageBuild>();
        var groups = new List<RayTracingShaderGroupCreateInfoKHR>();
        var generalGroups = new Dictionary<EntryPointReflection, uint>();
        var hitGroups = new Dictionary<string, uint>(StringComparer.Ordinal);
        var entries = new List<EntryPointReflection>();
        foreach (EntryPointReflection entry in desc.RayGeneration)
            AddGeneral(entry, SlangStage.RayGeneration, ShaderStageFlags.RaygenBitKhr);
        foreach (EntryPointReflection entry in desc.Miss)
            AddGeneral(entry, SlangStage.Miss, ShaderStageFlags.MissBitKhr);
        foreach (RayTracingHitGroup hit in desc.HitGroups)
        {
            uint closest = AddOptionalStage(hit.ClosestHit, SlangStage.ClosestHit, ShaderStageFlags.ClosestHitBitKhr);
            uint any = AddOptionalStage(hit.AnyHit, SlangStage.AnyHit, ShaderStageFlags.AnyHitBitKhr);
            uint intersection = AddOptionalStage(hit.Intersection, SlangStage.Intersection, ShaderStageFlags.IntersectionBitKhr);
            uint groupIndex = checked((uint)groups.Count);
            hitGroups.TryAdd(hit.Name, groupIndex);
            groups.Add(new RayTracingShaderGroupCreateInfoKHR { SType = StructureType.RayTracingShaderGroupCreateInfoKhr, Type = intersection == Vk.ShaderUnusedKhr ? RayTracingShaderGroupTypeKHR.TrianglesHitGroupKhr : RayTracingShaderGroupTypeKHR.ProceduralHitGroupKhr, GeneralShader = Vk.ShaderUnusedKhr, ClosestHitShader = closest, AnyHitShader = any, IntersectionShader = intersection, });
        }

        foreach (EntryPointReflection entry in desc.Callable)
            AddGeneral(entry, SlangStage.Callable, ShaderStageFlags.CallableBitKhr);
        return new RayPipelineBuild(reflection, stages.ToArray(), groups.ToArray(), generalGroups, hitGroups, entries.Distinct().ToArray());
        void AddGeneral(EntryPointReflection entry, SlangStage slangStage, ShaderStageFlags nativeStage)
        {
            uint stage = AddStage(entry, slangStage, nativeStage);
            uint group = checked((uint)groups.Count);
            generalGroups.TryAdd(entry, group);
            groups.Add(new RayTracingShaderGroupCreateInfoKHR { SType = StructureType.RayTracingShaderGroupCreateInfoKhr, Type = RayTracingShaderGroupTypeKHR.GeneralKhr, GeneralShader = stage, ClosestHitShader = Vk.ShaderUnusedKhr, AnyHitShader = Vk.ShaderUnusedKhr, IntersectionShader = Vk.ShaderUnusedKhr, });
        }

        uint AddOptionalStage(EntryPointReflection entry, SlangStage slangStage, ShaderStageFlags nativeStage) => entry == EntryPointReflection.Null ? Vk.ShaderUnusedKhr : AddStage(entry, slangStage, nativeStage);
        uint AddStage(EntryPointReflection entry, SlangStage slangStage, ShaderStageFlags nativeStage)
        {
            uint index = checked((uint)stages.Count);
            stages.Add(new RayStageBuild(entry, slangStage, nativeStage));
            entries.Add(entry);
            return index;
        }
    }

    private static PhysicalDeviceRayTracingPipelinePropertiesKHR GetRayPipelineProperties(VulkanDevice device)
    {
        PhysicalDeviceRayTracingPipelinePropertiesKHR properties = new()
        {
            SType = StructureType.PhysicalDeviceRayTracingPipelinePropertiesKhr,
        };
        PhysicalDeviceProperties2 root = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &properties,
        };
        device.Backend.Api.GetPhysicalDeviceProperties2(device.AdapterDevice, &root);
        return properties;
    }

    private static PipelineCreateFlags ToNative(RayTracingPipelineOptions options)
    {
        PipelineCreateFlags result = 0;
        if ((options & RayTracingPipelineOptions.SkipTriangles) != 0)
            result |= PipelineCreateFlags.CreateRayTracingSkipTrianglesBitKhr;
        if ((options & RayTracingPipelineOptions.SkipProceduralPrimitives) != 0)
            result |= PipelineCreateFlags.CreateRayTracingSkipAabbsBitKhr;
        return result;
    }

    private sealed record RayPipelineBuild(
        ShaderReflection Reflection,
        RayStageBuild[] Stages,
        RayTracingShaderGroupCreateInfoKHR[] Groups,
        Dictionary<EntryPointReflection, uint> GeneralGroups,
        Dictionary<string, uint> HitGroups,
        EntryPointReflection[] Entries
    );
    private readonly record struct RayStageBuild(EntryPointReflection Entry, SlangStage SlangStage, ShaderStageFlags NativeStage);
    private sealed class VulkanRayTracingPipelineState(
        Dictionary<EntryPointReflection, uint> generalGroups,
        Dictionary<string, uint> hitGroups,
        uint groupCount,
        uint handleSize,
        uint handleAlignment,
        uint baseAlignment,
        uint maximumStride
    )
    {
        internal Dictionary<EntryPointReflection, uint> GeneralGroups { get; } = generalGroups;
        internal Dictionary<string, uint> HitGroups { get; } = hitGroups;
        internal uint GroupCount { get; } = groupCount;
        internal uint HandleSize { get; } = handleSize;
        internal uint HandleAlignment { get; } = handleAlignment;
        internal uint BaseAlignment { get; } = baseAlignment;
        internal uint MaximumStride { get; } = maximumStride;

        internal uint Resolve(in RayTracingShaderRecord record)
        {
            if (record.HitGroupName is string hit)
                return HitGroups.TryGetValue(hit, out uint hitGroup) ? hitGroup : default!;
            return GeneralGroups.TryGetValue(record.EntryPoint, out uint group) ? group : default!;
        }
    }

    private sealed class VulkanRayTracingShaderTable : RayTracingShaderTable
    {
        private readonly VulkanDevice _device;
        private VulkanShaderTableGeneration? _generation;
        internal VulkanRayTracingShaderTable(VulkanDevice device, VulkanPipeline pipeline, in RayTracingShaderTableDesc desc) : base(device, desc)
        {
            _device = device;
            Pipeline = pipeline;
        }

        internal VulkanPipeline Pipeline { get; }

        internal VulkanShaderTableGeneration CreateGeneration(in RayTracingShaderTableUpdate update) => VulkanShaderTableGeneration.Create(_device, Pipeline, Description, update);
        internal void Publish(VulkanShaderTableGeneration generation)
        {
            VulkanShaderTableGeneration? previous = Interlocked.Exchange(ref _generation, generation);
            previous?.ReleaseNative();
        }

        internal VulkanShaderTableGeneration AcquireGeneration()
        {
            while (true)
            {
                VulkanShaderTableGeneration generation = Volatile.Read(ref _generation)!;
                try
                {
                    generation.RetainNative();
                }
                catch (ObjectDisposedException)
                {
                    continue;
                }

                if (ReferenceEquals(generation, Volatile.Read(ref _generation)))
                    return generation;
                generation.ReleaseNative();
            }
        }

        internal override void Release(bool fromParent)
        {
            Interlocked.Exchange(ref _generation, null)?.ReleaseNative();
            Pipeline.ReleaseNative();
            _device.UnregisterChild(this);
        }
    }

    private sealed class VulkanShaderTableGeneration : IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanMemoryBlock _memory;
        private readonly VulkanLifetime _lifetime;
        private VkBuffer _buffer;
        private VulkanShaderTableGeneration(
            VulkanDevice device,
            VkBuffer buffer,
            VulkanMemoryBlock memory,
            StridedDeviceAddressRegionKHR rayGeneration,
            StridedDeviceAddressRegionKHR miss,
            StridedDeviceAddressRegionKHR hit,
            StridedDeviceAddressRegionKHR callable
        )
        {
            _device = device;
            _buffer = buffer;
            _memory = memory;
            RayGeneration = rayGeneration;
            Miss = miss;
            Hit = hit;
            Callable = callable;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal StridedDeviceAddressRegionKHR RayGeneration { get; }
        internal StridedDeviceAddressRegionKHR Miss { get; }
        internal StridedDeviceAddressRegionKHR Hit { get; }
        internal StridedDeviceAddressRegionKHR Callable { get; }

        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal static VulkanShaderTableGeneration Create(
            VulkanDevice device,
            VulkanPipeline pipeline,
            in RayTracingShaderTableDesc desc,
            in RayTracingShaderTableUpdate update
        )
        {
            VulkanRayTracingPipelineState state = pipeline.RayTracing!;
            uint stride = checked((uint)AlignUp(Math.Max(desc.MaximumRecordSize, state.HandleSize), state.HandleAlignment));
            ulong rayOffset = 0;
            ulong missOffset = AlignUp(checked((ulong)stride * desc.RayGenerationRecordCount), state.BaseAlignment);
            ulong hitOffset = AlignUp(missOffset + checked((ulong)stride * desc.MissRecordCount), state.BaseAlignment);
            ulong callableOffset = AlignUp(hitOffset + checked((ulong)stride * desc.HitRecordCount), state.BaseAlignment);
            ulong totalSize = AlignUp(callableOffset + checked((ulong)stride * desc.CallableRecordCount), state.BaseAlignment);
            VkBuffer buffer = CreateBuffer(device, totalSize);
            VulkanMemoryBlock? memory = null;
            try
            {
                Silk.NET.Vulkan.MemoryRequirements requirements;
                device.Backend.Api.GetBufferMemoryRequirements(device.Native, buffer, &requirements);
                memory = device.AllocateMemory(requirements.Size, requirements.MemoryTypeBits, MemoryType.Upload, deviceAddress: true);
                device.ThrowIfDeviceCallFailed(device.Backend.Api.BindBufferMemory(device.Native, buffer, memory.Native, 0), "vkBindBufferMemory(shader table)");
                BufferDeviceAddressInfo addressInfo = new()
                {
                    SType = StructureType.BufferDeviceAddressInfo,
                    Buffer = buffer,
                };
                ulong address = device.Backend.Api.GetBufferDeviceAddress(device.Native, &addressInfo);
                byte[] handles = new byte[checked((int)(state.GroupCount * state.HandleSize))];
                fixed (byte* handlePointer = handles)
                {
                    device.ThrowIfDeviceCallFailed(
                        device.RayTracingPipelineApi.GetRayTracingShaderGroupHandles(device.Native, pipeline.Native, 0, state.GroupCount, checked((nuint)handles.Length), handlePointer),
                        "vkGetRayTracingShaderGroupHandlesKHR"
                    );
                }

                Span<byte> destination = new((void*)memory.Mapped, checked((int)totalSize));
                destination.Clear();
                WriteRecords(destination, rayOffset, stride, update.RayGeneration, update, state, handles);
                WriteRecords(destination, missOffset, stride, update.Miss, update, state, handles);
                WriteRecords(destination, hitOffset, stride, update.Hit, update, state, handles);
                WriteRecords(destination, callableOffset, stride, update.Callable, update, state, handles);
                Flush(device, memory, totalSize);
                return new VulkanShaderTableGeneration(
                    device,
                    buffer,
                    memory,
                    Region(address + rayOffset, stride, desc.RayGenerationRecordCount, rayGeneration: true),
                    Region(address + missOffset, stride, desc.MissRecordCount, rayGeneration: false),
                    Region(address + hitOffset, stride, desc.HitRecordCount, rayGeneration: false),
                    Region(address + callableOffset, stride, desc.CallableRecordCount, rayGeneration: false)
                );
            }
            catch
            {
                device.Backend.Api.DestroyBuffer(device.Native, buffer, null);
                memory?.Release();
                throw;
            }
        }

        private static VkBuffer CreateBuffer(VulkanDevice device, ulong size)
        {
            BufferCreateInfo createInfo = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = BufferUsageFlags.ShaderBindingTableBitKhr | BufferUsageFlags.ShaderDeviceAddressBit,
                SharingMode = SharingMode.Exclusive,
            };
            VkBuffer buffer = default;
            device.ThrowIfDeviceCallFailed(device.Backend.Api.CreateBuffer(device.Native, &createInfo, null, &buffer), "vkCreateBuffer(shader table)");
            return buffer;
        }

        private static void WriteRecords(
            Span<byte> destination,
            ulong baseOffset,
            uint stride,
            ReadOnlySpan<RayTracingShaderRecord> records,
            in RayTracingShaderTableUpdate update,
            VulkanRayTracingPipelineState state,
            byte[] handles
        )
        {
            for (int index = 0; index < records.Length; index++)
            {
                RayTracingShaderRecord record = records[index];
                uint group = state.Resolve(record);
                int recordOffset = checked((int)(baseOffset + checked((ulong)index * stride)));
                handles.AsSpan(checked((int)(group * state.HandleSize)), checked((int)state.HandleSize)).CopyTo(destination[recordOffset..]);
                int payloadOffset = checked(recordOffset + (int)state.HandleSize);
                for (uint blockIndex = 0; blockIndex < record.ParameterBlockCount; blockIndex++)
                {
                    uint parameterIndex = checked(record.ParameterBlockOffset + blockIndex);
                    RayTracingLocalParameterBlock block = update.ParameterBlocks[checked((int)parameterIndex)];
                    ReadOnlySpan<byte> data = update.OrdinaryData.Slice(checked((int)block.OrdinaryDataOffset), checked((int)block.OrdinaryDataSize));
                    data.CopyTo(destination[payloadOffset..]);
                    payloadOffset += data.Length;
                }
            }
        }

        private static StridedDeviceAddressRegionKHR Region(ulong address, uint stride, uint count, bool rayGeneration) => count == 0 ? default : new StridedDeviceAddressRegionKHR(address, stride, rayGeneration ? stride : checked((ulong)stride * count));
        private static void Flush(VulkanDevice device, VulkanMemoryBlock memory, ulong size)
        {
            if (memory.Coherent)
                return;
            MappedMemoryRange range = new()
            {
                SType = StructureType.MappedMemoryRange,
                Memory = memory.Native,
                Offset = 0,
                Size = Math.Min(AlignUp(size, device.NonCoherentAtomSize), memory.Size),
            };
            device.ThrowIfDeviceCallFailed(device.Backend.Api.FlushMappedMemoryRanges(device.Native, 1, &range), "vkFlushMappedMemoryRanges(shader table)");
        }

        private void DestroyNative()
        {
            if (_buffer.Handle != 0)
                _device.Backend.Api.DestroyBuffer(_device.Native, _buffer, null);
            _buffer = default;
            _memory.Release();
        }
    }

    private sealed class RayPipelineData
    {
        private readonly SlangShaderData _slangCode;
        private readonly ShaderReflection _reflection;
        private readonly ReadOnlySequence<byte>[] _code;
        private readonly ISlangBlob? [] _codeBlobs;
        private readonly EntryPointReflection[] _rayGeneration;
        private readonly EntryPointReflection[] _miss;
        private readonly EntryPointReflection[] _callable;
        private readonly RayTracingHitGroup[] _hitGroups;
        private readonly uint _recursion;
        private readonly uint _payload;
        private readonly uint _attribute;
        private readonly RayTracingPipelineOptions _options;
        private readonly uint _nodeMask;
        private readonly string? _label;
        private readonly StaticSamplerBinding[] _staticSamplers;
        internal RayPipelineData(in RayTracingPipelineDesc desc)
        {
            _slangCode = desc.SlangCode;
            _reflection = desc.Reflection;
            _rayGeneration = desc.RayGeneration.ToArray();
            _miss = desc.Miss.ToArray();
            _callable = desc.Callable.ToArray();
            _hitGroups = desc.HitGroups.ToArray();
            _recursion = desc.MaximumRecursionDepth;
            _payload = desc.MaximumPayloadSize;
            _attribute = desc.MaximumAttributeSize;
            _options = desc.Options;
            _nodeMask = desc.NodeMask;
            _label = desc.Label;
            _staticSamplers = desc.StaticSamplers.ToArray();
            _code = [desc.Code];
            _codeBlobs = [desc.CodeBlob];
        }

        internal Pipeline Create(
            VulkanBackend backend,
            VulkanDevice device,
            SomeEngine.Graphics.PipelineCache? cache,
            SlangShaderData capturedSlangCode
        )
        {
            RayTracingPipelineDesc desc = new(
                _slangCode,
                _reflection,
                _code[0],
                _rayGeneration,
                _miss,
                _callable,
                _hitGroups,
                _recursion,
                _payload,
                _attribute,
                _options,
                _nodeMask,
                _label,
                _staticSamplers,
                _codeBlobs[0]
            );
            return backend.CreateRayTracingPipelineCore(device, desc, cache, capturedSlangCode);
        }
    }
}
