using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using Xunit;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Tests;

internal static class RayTracingInstanceAssertions
{
    internal const string QuerySource = """
        RaytracingAccelerationStructure scene;
        struct Probe { float4 origin; uint4 control; };
        StructuredBuffer<Probe> probes;
        RWStructuredBuffer<uint4> results;
        [shader("compute")]
        [numthreads(1, 1, 1)]
        void queryMain(uint3 id : SV_DispatchThreadID)
        {
            Probe probe = probes[id.x];
            RayDesc ray;
            ray.Origin = probe.origin.xyz;
            ray.Direction = float3(0, 0, -1);
            ray.TMin = 0.01;
            ray.TMax = 10;
            uint flags = probe.control.y == 0u ? RAY_FLAG_NONE : RAY_FLAG_CULL_BACK_FACING_TRIANGLES;
            RayQuery<RAY_FLAG_NONE> query;
            query.TraceRayInline(scene, flags, probe.control.x, ray);
            while (query.Proceed())
                if (query.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE)
                    query.CommitNonOpaqueTriangleHit();
            bool hit = query.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
            results[id.x] = hit ? uint4(query.CommittedInstanceID(),
                query.CommittedTriangleFrontFace() ? 1u : 0u, 1u, asuint(query.CommittedRayT()))
                : uint4(0xffffffffu, 0u, 0u, 0u);
        }
        """;

    internal static void VerifyEncoding(IGraphicsBackend backend, Device device, Func<Device> createOtherDevice)
    {
        RayTracing capability = RequireCapability(backend, device);
        using var objects = new RayObjects(backend, device);
        Blas bottom = objects.CreateBlas();
        var valid = new AccelerationStructureInstance(bottom.Structure, Matrix4x4.Identity);
        int size = checked((int)capability.InstanceSize);
        byte[] destination = Enumerable.Repeat((byte)0xa5, checked(size * 2 + 17)).ToArray();
        byte[] original = destination.ToArray();
        backend.EncodeAccelerationStructureInstances(device, [], destination);
        Assert.Equal(original, destination);
        backend.EncodeAccelerationStructureInstances(device, [valid, valid], destination);
        Assert.Equal(original.AsSpan(size * 2).ToArray(), destination.AsSpan(size * 2).ToArray());

        // The public CPU span has no native-address alignment requirement.
        byte[] offsetDestination = Enumerable.Repeat((byte)0x6d, checked(size + 19)).ToArray();
        backend.EncodeAccelerationStructureInstances(device, [valid], offsetDestination.AsSpan(1));
        Assert.Equal((byte)0x6d, offsetDestination[0]);
        Assert.All(offsetDestination.Skip(size + 1), value => Assert.Equal((byte)0x6d, value));
        foreach (Matrix4x4 transform in new[]
        {
            Matrix4x4.CreateScale(1e-20f), Matrix4x4.CreateScale(1e20f), Matrix4x4.CreateScale(-1, 1, 1),
        })
            backend.EncodeAccelerationStructureInstances(device, [valid with { Transform = transform }], destination);

        byte[] shortDestination = Enumerable.Repeat((byte)0x39, checked(size * 2 - 1)).ToArray();
        byte[] shortOriginal = shortDestination.ToArray();
        Assert.ThrowsAny<ArgumentException>(() => backend.EncodeAccelerationStructureInstances(
            device, [valid, valid], shortDestination));
        Assert.Equal(shortOriginal, shortDestination);

        Matrix4x4 nan = Matrix4x4.Identity; nan.M11 = float.NaN;
        Matrix4x4 infinity = Matrix4x4.Identity; infinity.M43 = float.PositiveInfinity;
        Matrix4x4 perspective = Matrix4x4.Identity; perspective.M14 = 1;
        Matrix4x4 homogeneous = Matrix4x4.Identity; homogeneous.M44 = 2;
        foreach (AccelerationStructureInstance invalid in new[]
        {
            valid with { InstanceId = 0x01000000 },
            valid with { HitGroupOffset = 0x01000000 },
            valid with { Options = (AccelerationStructureInstanceOptions)0x80 },
            valid with { Options = AccelerationStructureInstanceOptions.ForceOpaque | AccelerationStructureInstanceOptions.ForceNonOpaque },
            valid with { Transform = nan }, valid with { Transform = infinity },
            valid with { Transform = perspective }, valid with { Transform = homogeneous },
            valid with { Transform = Matrix4x4.CreateScale(0, 1, 1) },
            valid with { AccelerationStructure = null! },
        })
            Reject(invalid);

        Buffer input = objects.Keep(backend.CreateBuffer(device,
            new BufferDesc(capability.InstanceSize, BufferUsages.AccelerationStructureInput), MemoryType.Upload));
        AccelerationStructureGeometry[] topGeometry = InstanceGeometry(input, 1);
        AccelerationStructureBuildInfo topSize = backend.GetAccelerationStructureBuildInfo(
            device, AccelerationStructureType.TopLevel, AccelerationStructureBuildOptions.None, topGeometry);
        (AccelerationStructure top, _) = objects.CreateStructure(topSize, AccelerationStructureType.TopLevel);
        Reject(valid with { AccelerationStructure = top });
        (AccelerationStructure disposed, _) = objects.CreateStructure(bottom.Size, AccelerationStructureType.BottomLevel);
        disposed.Dispose();
        RejectDisposed(valid with { AccelerationStructure = disposed });
        Blas disposedStorage = objects.CreateBlas();
        disposedStorage.Storage.Dispose();
        RejectDisposed(valid with { AccelerationStructure = disposedStorage.Structure });

        using (Device otherDevice = createOtherDevice())
        using (var otherObjects = new RayObjects(backend, otherDevice))
        {
            Blas foreign = otherObjects.CreateBlas();
            Reject(valid with { AccelerationStructure = foreign.Structure });
        }

        void Reject(AccelerationStructureInstance invalid)
        {
            Array.Fill(destination, (byte)0xa5);
            Assert.ThrowsAny<ArgumentException>(() => backend.EncodeAccelerationStructureInstances(
                device, [valid, invalid], destination));
            Assert.Equal(original, destination);
        }
        void RejectDisposed(AccelerationStructureInstance invalid)
        {
            Array.Fill(destination, (byte)0xa5);
            Assert.Throws<ObjectDisposedException>(() => backend.EncodeAccelerationStructureInstances(
                device, [valid, invalid], destination));
            Assert.Equal(original, destination);
        }
    }

    internal static void VerifyQueries(IGraphicsBackend backend, Device device,
        Pipeline pipeline, VariableLayoutReflection globals)
    {
        RayTracing capability = RequireCapability(backend, device);
        Assert.True(capability.InlineRayQuery);
        using var objects = new RayObjects(backend, device);
        Blas bottom = objects.CreateBlas();
        Matrix4x4 transformed = Matrix4x4.CreateScale(2, .5f, 1) *
            Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(-6, 0, 2);
        Matrix4x4 mirrored = Matrix4x4.CreateScale(-1, 1, 1) * Matrix4x4.CreateTranslation(6, 0, 1);
        const AccelerationStructureInstanceOptions ccw = AccelerationStructureInstanceOptions.TriangleFrontCounterClockwise;
        const AccelerationStructureInstanceOptions noCull = AccelerationStructureInstanceOptions.DisableTriangleFacingCull;
        AccelerationStructureInstance[] instances =
        [
            new(bottom.Structure, transformed, 17, 1, 0x00ffffff, noCull),
            new(bottom.Structure, Matrix4x4.CreateTranslation(-2, 0, 1), 0x00ffffff, 2),
            new(bottom.Structure, Matrix4x4.CreateTranslation(2, 0, 1), 0x00234567, 4, Options: ccw),
            new(bottom.Structure, mirrored, 89, 8, Options: noCull),
            new(bottom.Structure, Matrix4x4.CreateTranslation(10, 0, 1), 101, 16, Options: ccw | noCull),
            new(bottom.Structure, Matrix4x4.CreateTranslation(14, 0, 1), 103, 0),
        ];
        int encodedSize = checked(instances.Length * (int)capability.InstanceSize);
        byte[] encoded = Enumerable.Repeat((byte)0x5a, encodedSize + 23).ToArray();
        // Encoding is valid before the BLAS GPU build, as long as build dependencies are ordered below.
        backend.EncodeAccelerationStructureInstances(device, instances, encoded);
        Assert.All(encoded.Skip(encodedSize), value => Assert.Equal((byte)0x5a, value));
        Buffer instanceBuffer = objects.Upload(encoded.AsSpan(0, encodedSize).ToArray(), BufferUsages.AccelerationStructureInput);
        AccelerationStructureGeometry[] topGeometry = InstanceGeometry(instanceBuffer, checked((uint)instances.Length));
        AccelerationStructureBuildInfo topSize = backend.GetAccelerationStructureBuildInfo(device,
            AccelerationStructureType.TopLevel, AccelerationStructureBuildOptions.None, topGeometry);
        (AccelerationStructure top, Buffer topStorage) = objects.CreateStructure(topSize, AccelerationStructureType.TopLevel);
        ulong scratchBytes = Math.Max(Align(bottom.Size.BuildScratchSize, bottom.Size.BuildScratchAlignment),
            Align(topSize.BuildScratchSize, topSize.BuildScratchAlignment));
        Buffer scratch = objects.Keep(backend.CreateBuffer(device,
            new BufferDesc(scratchBytes, BufferUsages.ShaderWrite), MemoryType.DeviceLocal));
        Vector3 transformedPoint = Vector3.Transform(new(.75f, -.75f, 0), transformed);
        Vector3 outsidePoint = Vector3.Transform(new(-.375f, 1, 0), transformed);
        Vector3 mirroredPoint = Vector3.Transform(new(1.5f, -.75f, 0), mirrored);
        Probe[] probes =
        [
            At(transformedPoint, 1), At(transformedPoint, 2), At(new(-2, 0, 1), 2),
            At(new(2, 0, 1), 4), At(mirroredPoint, 8), At(mirroredPoint, 1),
            At(new(100, 0, 0), 255), At(outsidePoint, 255), At(new(2, 0, 1), 4, cullBack: true),
            At(new(10, 0, 1), 16, cullBack: true), At(new(14, 0, 1), 255),
        ];
        Buffer probeBuffer = objects.Upload(probes, BufferUsages.ShaderRead);
        ulong outputBytes = checked((ulong)probes.Length * 4 * sizeof(uint));
        Buffer output = objects.Keep(backend.CreateBuffer(device,
            new BufferDesc(outputBytes, BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal));
        Buffer readback = objects.Keep(backend.CreateBuffer(device,
            new BufferDesc(outputBytes, BufferUsages.CopyDestination), MemoryType.Readback));
        ResourceBinding[] bindings =
        [
            ResourceBinding.AccelerationStructure(objects.Keep(backend.CreateAccelerationStructureSrv(device, new(top)))),
            ResourceBinding.ReadOnlyBuffer(objects.Keep(backend.CreateBufferSrv(device,
                new(probeBuffer, BufferRange.Whole, StructureStride: (uint)Marshal.SizeOf<Probe>())))),
            ResourceBinding.WritableBuffer(objects.Keep(backend.CreateBufferUav(device,
                new(output, BufferRange.Whole, StructureStride: 4 * sizeof(uint))))),
        ];
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(bottom.Storage, PipelineSync.None,
            PipelineSync.BuildRayTracingAccelerationStructure, ResourceAccess.NoAccess, ResourceAccess.RayTracingAccelerationStructureWrite));
        backend.Barrier(context, new BufferBarrier(scratch, PipelineSync.None,
            PipelineSync.BuildRayTracingAccelerationStructure, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        backend.BuildAccelerationStructure(context, new(AccelerationStructureType.BottomLevel,
            AccelerationStructureBuildOptions.None, bottom.Geometry, bottom.Structure, scratch, BufferRange.Whole));
        backend.Barrier(context, new MemoryBarrier(PipelineSync.BuildRayTracingAccelerationStructure,
            PipelineSync.BuildRayTracingAccelerationStructure,
            ResourceAccess.RayTracingAccelerationStructureWrite | ResourceAccess.UnorderedAccess,
            ResourceAccess.RayTracingAccelerationStructureRead | ResourceAccess.UnorderedAccess));
        backend.Barrier(context, new BufferBarrier(topStorage, PipelineSync.None,
            PipelineSync.BuildRayTracingAccelerationStructure, ResourceAccess.NoAccess, ResourceAccess.RayTracingAccelerationStructureWrite));
        backend.BuildAccelerationStructure(context, new(AccelerationStructureType.TopLevel,
            AccelerationStructureBuildOptions.None, topGeometry, top, scratch, BufferRange.Whole));
        // Trace reads both the TLAS and its referenced BLAS, not just topStorage.
        backend.Barrier(context, new MemoryBarrier(PipelineSync.BuildRayTracingAccelerationStructure,
            PipelineSync.ComputeShading, ResourceAccess.RayTracingAccelerationStructureWrite, ResourceAccess.RayTracingAccelerationStructureRead));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading,
            ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        backend.SetPipeline(context, pipeline);
        backend.SetTransientParameterBindings(context, new(globals, bindings, []));
        backend.Dispatch(context, new(checked((uint)probes.Length), 1, 1));
        backend.Barrier(context, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
            ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new(output, 0, readback, 0, outputBytes));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
        using MappedBuffer mapped = backend.Map(readback, MapType.Read, BufferRange.Whole);
        mapped.Invalidate(mapped.Range);
        uint[] values = MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray();
        Assert.Equal(probes.Length * 4, values.Length);
        Hit(0, 17, 3); Miss(1); Hit(2, 0x00ffffff, 4); Hit(3, 0x00234567, 4);
        Assert.Equal(1u, values[2 * 4 + 1]);
        Assert.Equal(0u, values[3 * 4 + 1]);
        Assert.Equal(1u, values[4 * 4 + 1]);
        Hit(4, 89, 4); Miss(5); Miss(6); Miss(7); Miss(8); Hit(9, 101, 4); Miss(10);

        void Hit(int index, uint id, float distance)
        {
            Assert.Equal(1u, values[index * 4 + 2]);
            Assert.Equal(id, values[index * 4]);
            Assert.InRange(BitConverter.UInt32BitsToSingle(values[index * 4 + 3]), distance - 1e-5f, distance + 1e-5f);
        }
        void Miss(int index) => Assert.Equal(new[] { uint.MaxValue, 0u, 0u, 0u }, values.AsSpan(index * 4, 4).ToArray());
        static Probe At(Vector3 point, uint mask, bool cullBack = false) =>
            new(new(point.X, point.Y, 5, 0), mask, cullBack ? 1u : 0u, 0, 0);
    }

    internal static Device CreateHardwareDevice(IGraphicsBackend backend)
    {
        var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: false);
        _ = backend.TryEnumerateAdapters(options, [], out int count);
        Assert.True(count > 0, "The typed instance contract requires a hardware ray-tracing adapter.");
        var adapters = new AdapterInfo[count];
        Assert.True(backend.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(count, confirmed);
        AdapterInfo[] hardware = adapters.Where(static adapter => adapter.HardwareAccelerated).ToArray();
        Assert.NotEmpty(hardware);
        return backend.CreateDevice(new DeviceDesc(hardware[0].Id, [new(QueueType.Compute)],
            requiredFeatures: DeviceFeatures.RayTracing, label: "typed instance hardware contract"));
    }

    private static RayTracing RequireCapability(IGraphicsBackend backend, Device device)
    {
        Assert.True(backend.TryGetCapability(device, out RayTracing? capability));
        Assert.NotNull(capability);
        Assert.NotEqual(0u, capability.InstanceSize);
        Assert.NotEqual(0ul, capability.InstanceAlignment);
        return capability;
    }
    private static AccelerationStructureGeometry[] InstanceGeometry(Buffer input, uint count) =>
        [new(AccelerationStructureGeometryType.Instances, new(input, BufferRange.Whole), default, 0, count, default, default)];
    private static ulong Align(ulong size, ulong alignment) => checked((size + alignment - 1) / alignment * alignment);
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct Probe(Vector4 Origin, uint Mask, uint CullBack, uint Reserved0, uint Reserved1);
    private readonly record struct Blas(AccelerationStructure Structure, Buffer Storage,
        AccelerationStructureGeometry[] Geometry, AccelerationStructureBuildInfo Size);

    private sealed class RayObjects(IGraphicsBackend backend, Device device) : IDisposable
    {
        private readonly List<IDisposable> _resources = [];
        internal T Keep<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
        internal Buffer Upload<T>(T[] data, BufferUsages usage) where T : unmanaged
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data.AsSpan());
            Buffer buffer = Keep(backend.CreateBuffer(device, new((ulong)bytes.Length, usage), MemoryType.Upload));
            using MappedBuffer mapping = backend.Map(buffer, MapType.Write, BufferRange.Whole);
            bytes.CopyTo(mapping.Bytes); mapping.Flush(mapping.Range);
            return buffer;
        }
        internal (AccelerationStructure, Buffer) CreateStructure(AccelerationStructureBuildInfo size, AccelerationStructureType type)
        {
            Buffer storage = Keep(backend.CreateBuffer(device,
                new(Align(size.ResultSize, size.ResultAlignment), BufferUsages.AccelerationStructure | BufferUsages.ShaderWrite),
                MemoryType.DeviceLocal));
            return (Keep(backend.CreateAccelerationStructure(device, storage, BufferRange.Whole, type)), storage);
        }
        internal Blas CreateBlas()
        {
            // For rays along -Z this winding has negative native ray-space area: front-facing by default.
            Buffer vertices = Upload<float>([-1, -1, 0, 2, -1, 0, 0, 1, 0], BufferUsages.AccelerationStructureInput);
            AccelerationStructureGeometry[] geometry =
                [new(AccelerationStructureGeometryType.Triangles, new(vertices, BufferRange.Whole),
                    Format.R32G32B32Float, 3 * sizeof(float), 3, default, default, AccelerationStructureGeometryOptions.Opaque)];
            AccelerationStructureBuildInfo size = backend.GetAccelerationStructureBuildInfo(device,
                AccelerationStructureType.BottomLevel, AccelerationStructureBuildOptions.None, geometry);
            (AccelerationStructure structure, Buffer storage) = CreateStructure(size, AccelerationStructureType.BottomLevel);
            return new(structure, storage, geometry, size);
        }
        public void Dispose()
        {
            for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose();
        }
    }
}
