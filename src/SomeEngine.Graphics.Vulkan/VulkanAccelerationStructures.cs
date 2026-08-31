namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private AccelerationStructure CreateAccelerationStructureCore(
        RhiDevice device,
        RhiBuffer storage,
        in BufferRange storageRange,
        AccelerationStructureType type,
        string? label)
    {
        VulkanDevice nativeDevice = RequireRayTracingDevice(device);
        VulkanBuffer nativeStorage = RequireBuffer(nativeDevice, storage, nameof(storage));
        if ((nativeStorage.Info.Usages & BufferUsages.AccelerationStructure) == 0)
            throw new ArgumentException("Acceleration-structure storage requires AccelerationStructure usage.", nameof(storage));
        BufferRange range = storageRange.Resolve(nativeStorage.Info.Size);
        if ((range.Offset & 255) != 0)
            throw new ArgumentOutOfRangeException(nameof(storageRange));
        AccelerationStructureCreateInfoKHR createInfo = new()
        {
            SType = StructureType.AccelerationStructureCreateInfoKhr,
            Buffer = nativeStorage.Native,
            Offset = range.Offset,
            Size = range.Size,
            Type = ToNative(type),
        };
        VkAccelerationStructure native = default;
        nativeDevice.ThrowIfDeviceCallFailed(
            nativeDevice.AccelerationStructureApi.CreateAccelerationStructure(
                nativeDevice.Native,
                &createInfo,
                null,
                &native),
            "vkCreateAccelerationStructureKHR");
        try
        {
            var result = new VulkanAccelerationStructure(
                nativeDevice,
                native,
                nativeStorage,
                new AccelerationStructureInfo(type, range.Size, storage, range),
                label);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            nativeDevice.AccelerationStructureApi.DestroyAccelerationStructure(
                nativeDevice.Native,
                native,
                null);
            throw;
        }
    }

    private AccelerationStructureSrv CreateAccelerationStructureSrvCore(
        RhiDevice device,
        in AccelerationStructureSrvDesc desc)
    {
        VulkanDevice nativeDevice = RequireRayTracingDevice(device);
        VulkanAccelerationStructure structure = RequireAccelerationStructure(
            nativeDevice,
            desc.AccelerationStructure,
            nameof(desc));
        var result = new VulkanAccelerationStructureSrv(nativeDevice, structure, desc);
        nativeDevice.RegisterChild(result);
        return result;
    }

    private AccelerationStructureBuildInfo GetAccelerationStructureBuildInfoCore(
        RhiDevice device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries)
    {
        VulkanDevice nativeDevice = RequireRayTracingDevice(device);
        VulkanGeometryPack pack = CreateGeometryPack(nativeDevice, type, geometries, capture: null);
        fixed (AccelerationStructureGeometryKHR* geometryPointer = pack.Geometries)
        fixed (uint* primitivePointer = pack.PrimitiveCounts)
        {
            AccelerationStructureBuildGeometryInfoKHR build = new()
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = ToNative(type),
                Flags = ToNative(options),
                Mode = BuildAccelerationStructureModeKHR.BuildKhr,
                GeometryCount = checked((uint)pack.Geometries.Length),
                PGeometries = geometryPointer,
            };
            AccelerationStructureBuildSizesInfoKHR sizes = new()
            {
                SType = StructureType.AccelerationStructureBuildSizesInfoKhr,
            };
            nativeDevice.AccelerationStructureApi.GetAccelerationStructureBuildSizes(
                nativeDevice.Native,
                AccelerationStructureBuildTypeKHR.DeviceKhr,
                &build,
                primitivePointer,
                &sizes);
            RayTracing capability = GetRayTracingCapability(nativeDevice);
            return new AccelerationStructureBuildInfo(
                sizes.AccelerationStructureSize,
                capability.AccelerationStructureAlignment,
                sizes.BuildScratchSize,
                capability.ScratchAlignment,
                sizes.UpdateScratchSize,
                capability.ScratchAlignment);
        }
    }

    private void BuildAccelerationStructureCore(
        CommandContext context,
        in AccelerationStructureBuildDesc desc)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = RequireRayTracingDevice(command.Device);
        VulkanAccelerationStructure destination = RequireAccelerationStructure(
            device,
            desc.Destination,
            nameof(desc));
        if (destination.Info.Type != desc.Type)
            throw new ArgumentException("The build type does not match the destination structure.", nameof(desc));
        VulkanAccelerationStructure? source = desc.Source is null
            ? null
            : RequireAccelerationStructure(device, desc.Source, nameof(desc));
        bool update = (desc.Options & AccelerationStructureBuildOptions.PerformUpdate) != 0;
        if (update && source is null)
            throw new ArgumentException("An update build requires a source acceleration structure.", nameof(desc));
        VulkanBuffer scratch = RequireBuffer(device, desc.Scratch, nameof(desc));
        BufferRange scratchRange = desc.ScratchRange.Resolve(scratch.Info.Size);
        RayTracing capability = GetRayTracingCapability(device);
        if (scratchRange.Offset % capability.ScratchAlignment != 0)
            throw new ArgumentOutOfRangeException(nameof(desc.ScratchRange));
        command.Capture(destination);
        if (source is not null) command.Capture(source);
        command.Capture(scratch);
        VulkanGeometryPack pack = CreateGeometryPack(device, desc.Type, desc.Geometries, command);
        fixed (AccelerationStructureGeometryKHR* geometryPointer = pack.Geometries)
        fixed (AccelerationStructureBuildRangeInfoKHR* rangePointer = pack.Ranges)
        {
            AccelerationStructureBuildGeometryInfoKHR build = new()
            {
                SType = StructureType.AccelerationStructureBuildGeometryInfoKhr,
                Type = ToNative(desc.Type),
                Flags = ToNative(desc.Options),
                Mode = update
                    ? BuildAccelerationStructureModeKHR.UpdateKhr
                    : BuildAccelerationStructureModeKHR.BuildKhr,
                SrcAccelerationStructure = source?.Native ?? default,
                DstAccelerationStructure = destination.Native,
                GeometryCount = checked((uint)pack.Geometries.Length),
                PGeometries = geometryPointer,
                ScratchData = new DeviceOrHostAddressKHR
                {
                    DeviceAddress = checked(scratch.DeviceAddress + scratchRange.Offset),
                },
            };
            AccelerationStructureBuildRangeInfoKHR* ranges = rangePointer;
            device.AccelerationStructureApi.CmdBuildAccelerationStructures(
                command.NativeRecording,
                1,
                &build,
                &ranges);
        }
    }

    private void CopyAccelerationStructureCore(
        CommandContext context,
        AccelerationStructure destination,
        AccelerationStructure source,
        AccelerationStructureCopyType type)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = RequireRayTracingDevice(command.Device);
        VulkanAccelerationStructure nativeDestination = RequireAccelerationStructure(device, destination, nameof(destination));
        VulkanAccelerationStructure nativeSource = RequireAccelerationStructure(device, source, nameof(source));
        command.Capture(nativeDestination);
        command.Capture(nativeSource);
        CopyAccelerationStructureInfoKHR copy = new()
        {
            SType = StructureType.CopyAccelerationStructureInfoKhr,
            Src = nativeSource.Native,
            Dst = nativeDestination.Native,
            Mode = type == AccelerationStructureCopyType.Compact
                ? CopyAccelerationStructureModeKHR.CompactKhr
                : CopyAccelerationStructureModeKHR.CloneKhr,
        };
        device.AccelerationStructureApi.CmdCopyAccelerationStructure(command.NativeRecording, &copy);
    }

    private void SerializeAccelerationStructureCore(
        CommandContext context,
        in BufferRegion destination,
        AccelerationStructure source)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = RequireRayTracingDevice(command.Device);
        VulkanBuffer buffer = RequireBuffer(device, destination.Buffer, nameof(destination));
        BufferRange range = destination.Range.Resolve(buffer.Info.Size);
        if ((buffer.Info.Usages & BufferUsages.CopyDestination) == 0 ||
            ((buffer.DeviceAddress + range.Offset) & 255) != 0)
        {
            throw new ArgumentException(
                "Serialized acceleration-structure storage requires CopyDestination usage and 256-byte address alignment.",
                nameof(destination));
        }
        VulkanAccelerationStructure structure = RequireAccelerationStructure(device, source, nameof(source));
        command.Capture(buffer);
        command.Capture(structure);
        CopyAccelerationStructureToMemoryInfoKHR copy = new()
        {
            SType = StructureType.CopyAccelerationStructureToMemoryInfoKhr,
            Src = structure.Native,
            Dst = new DeviceOrHostAddressKHR
            {
                DeviceAddress = checked(buffer.DeviceAddress + range.Offset),
            },
            Mode = CopyAccelerationStructureModeKHR.SerializeKhr,
        };
        device.AccelerationStructureApi.CmdCopyAccelerationStructureToMemory(
            command.NativeRecording,
            &copy);
    }

    private void DeserializeAccelerationStructureCore(
        CommandContext context,
        AccelerationStructure destination,
        in BufferRegion source)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = RequireRayTracingDevice(command.Device);
        VulkanBuffer buffer = RequireBuffer(device, source.Buffer, nameof(source));
        BufferRange range = source.Range.Resolve(buffer.Info.Size);
        if ((buffer.Info.Usages & BufferUsages.CopySource) == 0 ||
            ((buffer.DeviceAddress + range.Offset) & 255) != 0)
        {
            throw new ArgumentException(
                "Serialized acceleration-structure input requires CopySource usage and 256-byte address alignment.",
                nameof(source));
        }
        VulkanAccelerationStructure structure = RequireAccelerationStructure(device, destination, nameof(destination));
        command.Capture(buffer);
        command.Capture(structure);
        CopyMemoryToAccelerationStructureInfoKHR copy = new()
        {
            SType = StructureType.CopyMemoryToAccelerationStructureInfoKhr,
            Src = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = checked(buffer.DeviceAddress + range.Offset),
            },
            Dst = structure.Native,
            Mode = CopyAccelerationStructureModeKHR.DeserializeKhr,
        };
        device.AccelerationStructureApi.CmdCopyMemoryToAccelerationStructure(
            command.NativeRecording,
            &copy);
    }

    private void EmitAccelerationStructurePostBuildInfoCore(
        CommandContext context,
        AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type,
        RhiBuffer destination,
        ulong destinationOffset)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = RequireRayTracingDevice(command.Device);
        VulkanAccelerationStructure structure = RequireAccelerationStructure(device, source, nameof(source));
        VulkanBuffer buffer = RequireBuffer(device, destination, nameof(destination));
        if ((buffer.Info.Usages & BufferUsages.QueryResolve) == 0 ||
            destinationOffset > buffer.Info.Size - Math.Min(buffer.Info.Size, 8) ||
            (destinationOffset & 7) != 0)
            throw new ArgumentOutOfRangeException(nameof(destinationOffset));
        var query = new VulkanAccelerationStructurePropertyQuery(device, ToNative(type));
        try
        {
            command.Capture(query);
            command.Capture(structure);
            command.Capture(buffer);
            Api.CmdResetQueryPool(command.NativeRecording, query.Native, 0, 1);
            VkAccelerationStructure native = structure.Native;
            device.AccelerationStructureApi.CmdWriteAccelerationStructuresProperties(
                command.NativeRecording,
                1,
                &native,
                query.Type,
                query.Native,
                0);
            Api.CmdCopyQueryPoolResults(
                command.NativeRecording,
                query.Native,
                0,
                1,
                buffer.Native,
                destinationOffset,
                8,
                QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
        }
        finally
        {
            query.ReleaseNative();
        }
    }

    private VulkanGeometryPack CreateGeometryPack(
        VulkanDevice device,
        AccelerationStructureType type,
        ReadOnlySpan<AccelerationStructureGeometry> geometries,
        VulkanCommandContext? capture)
    {
        if (geometries.IsEmpty)
            throw new ArgumentException("At least one acceleration-structure geometry is required.", nameof(geometries));
        if (type == AccelerationStructureType.TopLevel &&
            (geometries.Length != 1 || geometries[0].Type != AccelerationStructureGeometryType.Instances))
            throw new ArgumentException("A top-level build requires exactly one Instances geometry.", nameof(geometries));
        var native = new AccelerationStructureGeometryKHR[geometries.Length];
        var ranges = new AccelerationStructureBuildRangeInfoKHR[geometries.Length];
        var primitiveCounts = new uint[geometries.Length];
        for (int index = 0; index < geometries.Length; index++)
        {
            CreateGeometry(
                device,
                geometries[index],
                capture,
                out native[index],
                out ranges[index],
                out primitiveCounts[index]);
        }
        return new VulkanGeometryPack(native, ranges, primitiveCounts);
    }

    private void CreateGeometry(
        VulkanDevice device,
        in AccelerationStructureGeometry geometry,
        VulkanCommandContext? capture,
        out AccelerationStructureGeometryKHR native,
        out AccelerationStructureBuildRangeInfoKHR range,
        out uint primitiveCount)
    {
        VulkanBuffer primary = RequireBuffer(device, geometry.Primary.Buffer, nameof(geometry));
        BufferRange primaryRange = geometry.Primary.Range.Resolve(primary.Info.Size);
        capture?.Capture(primary);
        native = new AccelerationStructureGeometryKHR
        {
            SType = StructureType.AccelerationStructureGeometryKhr,
            GeometryType = ToNative(geometry.Type),
            Flags = ToNative(geometry.Options),
        };
        range = default;
        switch (geometry.Type)
        {
            case AccelerationStructureGeometryType.Triangles:
                CreateTriangleGeometry(device, geometry, primary, primaryRange, capture, ref native, out primitiveCount);
                break;
            case AccelerationStructureGeometryType.AxisAlignedBoundingBoxes:
                if (geometry.PrimaryStride == 0 || geometry.Count == 0)
                    throw new ArgumentOutOfRangeException(nameof(geometry));
                native.Geometry.Aabbs = new AccelerationStructureGeometryAabbsDataKHR
                {
                    SType = StructureType.AccelerationStructureGeometryAabbsDataKhr,
                    Data = new DeviceOrHostAddressConstKHR
                    {
                        DeviceAddress = checked(primary.DeviceAddress + primaryRange.Offset),
                    },
                    Stride = geometry.PrimaryStride,
                };
                primitiveCount = geometry.Count;
                break;
            case AccelerationStructureGeometryType.Instances:
                if (geometry.Count == 0)
                    throw new ArgumentOutOfRangeException(nameof(geometry));
                native.Geometry.Instances = new AccelerationStructureGeometryInstancesDataKHR
                {
                    SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                    ArrayOfPointers = false,
                    Data = new DeviceOrHostAddressConstKHR
                    {
                        DeviceAddress = checked(primary.DeviceAddress + primaryRange.Offset),
                    },
                };
                primitiveCount = geometry.Count;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(geometry));
        }
        range.PrimitiveCount = primitiveCount;
    }

    private void CreateTriangleGeometry(
        VulkanDevice device,
        in AccelerationStructureGeometry geometry,
        VulkanBuffer vertices,
        in BufferRange vertexRange,
        VulkanCommandContext? capture,
        ref AccelerationStructureGeometryKHR native,
        out uint primitiveCount)
    {
        if (geometry.PrimaryStride == 0 || geometry.Count < 3)
            throw new ArgumentOutOfRangeException(nameof(geometry));
        bool indexed = geometry.Secondary.Buffer is not null;
        VulkanBuffer? indices = null;
        BufferRange indexRange = default;
        if (indexed)
        {
            indices = RequireBuffer(device, geometry.Secondary.Buffer!, nameof(geometry));
            indexRange = geometry.Secondary.Range.Resolve(indices.Info.Size);
            capture?.Capture(indices);
        }
        ulong transformAddress = 0;
        if (geometry.Transform.Buffer is not null)
        {
            VulkanBuffer transform = RequireBuffer(device, geometry.Transform.Buffer, nameof(geometry));
            BufferRange transformRange = geometry.Transform.Range.Resolve(transform.Info.Size);
            capture?.Capture(transform);
            transformAddress = checked(transform.DeviceAddress + transformRange.Offset);
        }
        native.Geometry.Triangles = new AccelerationStructureGeometryTrianglesDataKHR
        {
            SType = StructureType.AccelerationStructureGeometryTrianglesDataKhr,
            VertexFormat = VulkanFormats.ToNative(geometry.PrimaryFormat),
            VertexData = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = checked(vertices.DeviceAddress + vertexRange.Offset),
            },
            VertexStride = geometry.PrimaryStride,
            MaxVertex = geometry.Count - 1,
            IndexType = indexed
                ? geometry.IndexType == IndexType.UInt16
                    ? Silk.NET.Vulkan.IndexType.Uint16
                    : Silk.NET.Vulkan.IndexType.Uint32
                : Silk.NET.Vulkan.IndexType.NoneKhr,
            IndexData = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = indexed
                    ? checked(indices!.DeviceAddress + indexRange.Offset)
                    : 0,
            },
            TransformData = new DeviceOrHostAddressConstKHR
            {
                DeviceAddress = transformAddress,
            },
        };
        primitiveCount = geometry.Count / 3;
    }

    private VulkanDevice RequireRayTracingDevice(RhiDevice device)
    {
        VulkanDevice native = RequireDevice(device, nameof(device));
        _ = GetRayTracingCapability(native);
        return native;
    }

    private static RayTracing GetRayTracingCapability(VulkanDevice device) =>
        device.TryGetCapability(out RayTracing? capability) && capability is not null
            ? capability
            : throw new NotSupportedException("The Device was not created with RayTracing support.");

    private static VulkanAccelerationStructure RequireAccelerationStructure(
        VulkanDevice device,
        AccelerationStructure value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value is not VulkanAccelerationStructure native || !ReferenceEquals(native.Device, device))
            throw new ArgumentException("The acceleration structure belongs to a different Vulkan Device.", parameterName);
        native.ThrowIfDisposed();
        return native;
    }

    private static AccelerationStructureTypeKHR ToNative(AccelerationStructureType type) => type switch
    {
        AccelerationStructureType.BottomLevel => AccelerationStructureTypeKHR.BottomLevelKhr,
        AccelerationStructureType.TopLevel => AccelerationStructureTypeKHR.TopLevelKhr,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static GeometryTypeKHR ToNative(AccelerationStructureGeometryType type) => type switch
    {
        AccelerationStructureGeometryType.Triangles => GeometryTypeKHR.TrianglesKhr,
        AccelerationStructureGeometryType.AxisAlignedBoundingBoxes => GeometryTypeKHR.AabbsKhr,
        AccelerationStructureGeometryType.Instances => GeometryTypeKHR.InstancesKhr,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static BuildAccelerationStructureFlagsKHR ToNative(AccelerationStructureBuildOptions options)
    {
        BuildAccelerationStructureFlagsKHR result = 0;
        if ((options & AccelerationStructureBuildOptions.AllowUpdate) != 0) result |= BuildAccelerationStructureFlagsKHR.AllowUpdateBitKhr;
        if ((options & AccelerationStructureBuildOptions.AllowCompaction) != 0) result |= BuildAccelerationStructureFlagsKHR.AllowCompactionBitKhr;
        if ((options & AccelerationStructureBuildOptions.PreferFastTrace) != 0) result |= BuildAccelerationStructureFlagsKHR.PreferFastTraceBitKhr;
        if ((options & AccelerationStructureBuildOptions.PreferFastBuild) != 0) result |= BuildAccelerationStructureFlagsKHR.PreferFastBuildBitKhr;
        if ((options & AccelerationStructureBuildOptions.MinimizeMemory) != 0) result |= BuildAccelerationStructureFlagsKHR.LowMemoryBitKhr;
        return result;
    }

    private static GeometryFlagsKHR ToNative(AccelerationStructureGeometryOptions options)
    {
        GeometryFlagsKHR result = 0;
        if ((options & AccelerationStructureGeometryOptions.Opaque) != 0) result |= GeometryFlagsKHR.OpaqueBitKhr;
        if ((options & AccelerationStructureGeometryOptions.NoDuplicateAnyHitInvocation) != 0)
            result |= GeometryFlagsKHR.NoDuplicateAnyHitInvocationBitKhr;
        return result;
    }

    private static Silk.NET.Vulkan.QueryType ToNative(AccelerationStructurePostBuildInfoType type) => type switch
    {
        AccelerationStructurePostBuildInfoType.CompactedSize => Silk.NET.Vulkan.QueryType.AccelerationStructureCompactedSizeKhr,
        AccelerationStructurePostBuildInfoType.SerializationSize => Silk.NET.Vulkan.QueryType.AccelerationStructureSerializationSizeKhr,
        AccelerationStructurePostBuildInfoType.CurrentSize => Silk.NET.Vulkan.QueryType.AccelerationStructureSizeKhr,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private sealed class VulkanAccelerationStructure : AccelerationStructure, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanBuffer _storage;
        private readonly VulkanLifetime _lifetime;
        private VkAccelerationStructure _native;

        internal VulkanAccelerationStructure(
            VulkanDevice device,
            VkAccelerationStructure native,
            VulkanBuffer storage,
            in AccelerationStructureInfo info,
            string? label)
            : base(device, info, label)
        {
            _device = device;
            _native = native;
            _storage = storage;
            _storage.RetainNative();
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VkAccelerationStructure Native => _native;
        internal ulong DeviceAddress
        {
            get
            {
                AccelerationStructureDeviceAddressInfoKHR info = new()
                {
                    SType = StructureType.AccelerationStructureDeviceAddressInfoKhr,
                    AccelerationStructure = _native,
                };
                return _device.AccelerationStructureApi.GetAccelerationStructureDeviceAddress(
                    _device.Native,
                    &info);
            }
        }
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative()
        {
            if (_native.Handle != 0)
                _device.AccelerationStructureApi.DestroyAccelerationStructure(_device.Native, _native, null);
            _native = default;
            _storage.ReleaseNative();
        }
    }

    private sealed class VulkanAccelerationStructureSrv : AccelerationStructureSrv, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanAccelerationStructure _structure;
        private readonly VulkanLifetime _lifetime;

        internal VulkanAccelerationStructureSrv(
            VulkanDevice device,
            VulkanAccelerationStructure structure,
            in AccelerationStructureSrvDesc desc)
            : base(device, desc)
        {
            _device = device;
            _structure = structure;
            _structure.RetainNative();
            _lifetime = new VulkanLifetime(_structure.ReleaseNative);
        }

        internal VkAccelerationStructure Native => _structure.Native;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
    }

    private sealed class VulkanAccelerationStructurePropertyQuery : IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanLifetime _lifetime;
        private VkQueryPool _native;

        internal VulkanAccelerationStructurePropertyQuery(
            VulkanDevice device,
            Silk.NET.Vulkan.QueryType type)
        {
            _device = device;
            Type = type;
            QueryPoolCreateInfo createInfo = new()
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = type,
                QueryCount = 1,
            };
            VkQueryPool native = default;
            device.ThrowIfDeviceCallFailed(
                device.Backend.Api.CreateQueryPool(device.Native, &createInfo, null, &native),
                "vkCreateQueryPool(acceleration structure property)");
            _native = native;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VkQueryPool Native => _native;
        internal Silk.NET.Vulkan.QueryType Type { get; }
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        private void DestroyNative()
        {
            if (_native.Handle != 0)
                _device.Backend.Api.DestroyQueryPool(_device.Native, _native, null);
            _native = default;
        }
    }

    private sealed record VulkanGeometryPack(
        AccelerationStructureGeometryKHR[] Geometries,
        AccelerationStructureBuildRangeInfoKHR[] Ranges,
        uint[] PrimitiveCounts);
}
