using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.Graphics.Vulkan;
using SlangShaderSharp;
using Buffer = SomeEngine.Graphics.Buffer;
using GraphicsPipeline = SomeEngine.Graphics.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

internal static class RayShadowImage
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Inputs
    {
        internal Matrix4x4 ClipToWorld;
        internal Vector3 CameraPosition;
        internal uint Width;
        internal Vector3 LightDirection;
        internal uint Height;
        internal Vector3 LightPosition;
        internal uint LightKind;
        internal float LightRange;
        internal float NearPlane;
        internal float FarPlane;
        internal float OuterConeCos;
        internal float SourceAngleRadians;
        internal float SourceRadius;
        internal uint SampleCount;
        internal uint SamplePadding;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal readonly record struct SoftPixel(float Visibility, uint Flags);

    internal static uint SourceSampleCount => uint.TryParse(
        Environment.GetEnvironmentVariable("SOMEENGINE_SHADOW_REFERENCE_SAMPLES"), out uint value) ? value : 2048u;

    internal static async Task<uint[]> TraceAsync(string engine, RayShadowScene scene, Inputs inputs, bool vulkan)
    {
        byte[] pixels = await TraceBytesAsync(engine, scene, inputs, vulkan, "TraceRayShadow", sizeof(uint));
        return MemoryMarshal.Cast<byte, uint>(pixels).ToArray();
    }

    internal static async Task<SoftPixel[]> TraceSoftAsync(string engine, RayShadowScene scene, Inputs inputs, bool vulkan)
    {
        ArgumentOutOfRangeException.ThrowIfZero(inputs.SampleCount);
        byte[] pixels = await TraceBytesAsync(engine, scene, inputs, vulkan, "TraceSoftRayShadow", Marshal.SizeOf<SoftPixel>());
        return MemoryMarshal.Cast<byte, SoftPixel>(pixels).ToArray();
    }

    private static async Task<byte[]> TraceBytesAsync(string engine, RayShadowScene scene, Inputs inputs, bool vulkan,
        string entryName, int pixelBytes)
    {
        using IGraphicsBackend backend = new ValidationLayer(vulkan
            ? VulkanGraphicsBackend.Create() : D3D12GraphicsBackend.Create(),
            new ValidationOptions(NativeValidationDisabled: vulkan));
        var selection = new AdapterEnumerationOptions(AdapterPreference.HighPerformance);
        backend.TryEnumerateAdapters(selection, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(selection, adapters, out _);
        using Device device = backend.CreateDevice(new DeviceDesc(adapters.First(value => value.HardwareAccelerated).Id,
            [new(QueueType.Compute)], requiredFeatures: DeviceFeatures.RayTracing));
        Assert.True(backend.TryGetCapability(device, out RayTracing? rays) && rays!.InlineRayQuery,
            "The shadow ground truth requires hardware inline ray tracing.");
        var resources = new List<IDisposable>();
        try
        {
            T Keep<T>(T value) where T : IDisposable { resources.Add(value); return value; }
            Buffer Upload<T>(T[] values, BufferUsages usage) where T : unmanaged
            {
                Buffer buffer = Keep(backend.CreateBuffer(device,
                    new BufferDesc((ulong)(values.Length * Marshal.SizeOf<T>()), usage), MemoryType.Upload));
                using MappedBuffer mapping = backend.Map(buffer, MapType.Write, BufferRange.Whole);
                MemoryMarshal.AsBytes(values.AsSpan()).CopyTo(mapping.Bytes);
                mapping.Flush(mapping.Range);
                return buffer;
            }
            Buffer vertexBuffer = Upload(scene.Vertices, BufferUsages.AccelerationStructureInput | BufferUsages.ShaderRead);
            Buffer indexBuffer = Upload(scene.Indices, BufferUsages.AccelerationStructureInput | BufferUsages.ShaderRead);
            Buffer geometryBuffer = Upload(scene.Geometries, BufferUsages.ShaderRead);
            Buffer texels = Upload(scene.Texels, BufferUsages.ShaderRead);
            const AccelerationStructureBuildOptions buildOptions = AccelerationStructureBuildOptions.PreferFastTrace;
            var structures = new AccelerationStructure[scene.Meshes.Length];
            var descriptions = new AccelerationStructureGeometry[scene.Meshes.Length][];
            ulong scratchBytes = 0;
            for (int mesh = 0; mesh < scene.Meshes.Length; mesh++)
            {
                descriptions[mesh] = scene.Meshes[mesh].Geometries.Select(value => new AccelerationStructureGeometry(
                    AccelerationStructureGeometryType.Triangles,
                    new BufferRegion(vertexBuffer, new(value.VertexStart * 48ul, value.VertexCount * 48ul)),
                    Format.R32G32B32Float, 48, value.VertexCount,
                    new BufferRegion(indexBuffer, new(value.IndexStart * 4ul, value.IndexCount * 4ul)),
                    Format.R32UInt, value.Opaque ? AccelerationStructureGeometryOptions.Opaque : AccelerationStructureGeometryOptions.None)).ToArray();
                AccelerationStructureBuildInfo size = backend.GetAccelerationStructureBuildInfo(device,
                    AccelerationStructureType.BottomLevel, buildOptions, descriptions[mesh]);
                Buffer storage = Keep(backend.CreateBuffer(device,
                    new BufferDesc(Align(size.ResultSize, size.ResultAlignment),
                        BufferUsages.AccelerationStructure | BufferUsages.ShaderWrite), MemoryType.DeviceLocal));
                structures[mesh] = Keep(backend.CreateAccelerationStructure(device, storage,
                    BufferRange.Whole, AccelerationStructureType.BottomLevel));
                scratchBytes = Math.Max(scratchBytes, Align(size.BuildScratchSize, size.BuildScratchAlignment));
            }
            var instanceBytes = new byte[scene.Instances.Length * 64];
            for (int index = 0; index < scene.Instances.Length; index++)
            {
                RayShadowScene.Instance instance = scene.Instances[index];
                Matrix4x4 matrix = instance.Transform;
                float[] transform = [matrix.M11, matrix.M21, matrix.M31, matrix.M41,
                    matrix.M12, matrix.M22, matrix.M32, matrix.M42,
                    matrix.M13, matrix.M23, matrix.M33, matrix.M43];
                MemoryMarshal.AsBytes(transform.AsSpan()).CopyTo(instanceBytes.AsSpan(index * 64));
                BinaryPrimitives.WriteUInt32LittleEndian(instanceBytes.AsSpan(index * 64 + 48), instance.GeometryStart | 0xff000000u);
                BinaryPrimitives.WriteUInt32LittleEndian(instanceBytes.AsSpan(index * 64 + 52), 1u << 24);
                BinaryPrimitives.WriteUInt64LittleEndian(instanceBytes.AsSpan(index * 64 + 56), structures[instance.Mesh].DeviceAddress);
            }
            Buffer instances = Upload(instanceBytes, BufferUsages.AccelerationStructureInput);
            AccelerationStructureGeometry[] topGeometry = [new(AccelerationStructureGeometryType.Instances,
                new BufferRegion(instances, BufferRange.Whole), default, 0, (uint)scene.Instances.Length, default, default)];
            AccelerationStructureBuildInfo topSize = backend.GetAccelerationStructureBuildInfo(device,
                AccelerationStructureType.TopLevel, buildOptions, topGeometry);
            Buffer topStorage = Keep(backend.CreateBuffer(device,
                new BufferDesc(Align(topSize.ResultSize, topSize.ResultAlignment),
                    BufferUsages.AccelerationStructure | BufferUsages.ShaderWrite), MemoryType.DeviceLocal));
            AccelerationStructure top = Keep(backend.CreateAccelerationStructure(device, topStorage,
                BufferRange.Whole, AccelerationStructureType.TopLevel));
            scratchBytes = Math.Max(scratchBytes, Align(topSize.BuildScratchSize, topSize.BuildScratchAlignment));
            Buffer scratch = Keep(backend.CreateBuffer(device,
                new BufferDesc(scratchBytes, BufferUsages.ShaderWrite), MemoryType.DeviceLocal));
            using (CommandContext commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1)))
            {
                backend.Begin(commands);
                backend.Barrier(commands, new BufferBarrier(scratch, PipelineSync.None,
                    PipelineSync.BuildRayTracingAccelerationStructure, ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
                for (int mesh = 0; mesh < structures.Length; mesh++)
                {
                    backend.Barrier(commands, new BufferBarrier(structures[mesh].Info.Storage, PipelineSync.None,
                        PipelineSync.BuildRayTracingAccelerationStructure, ResourceAccess.NoAccess, ResourceAccess.RayTracingAccelerationStructureWrite));
                    backend.BuildAccelerationStructure(commands, new(AccelerationStructureType.BottomLevel,
                        buildOptions, descriptions[mesh], structures[mesh], scratch, BufferRange.Whole));
                    backend.Barrier(commands, new MemoryBarrier(PipelineSync.BuildRayTracingAccelerationStructure,
                        PipelineSync.BuildRayTracingAccelerationStructure,
                        ResourceAccess.RayTracingAccelerationStructureWrite | ResourceAccess.UnorderedAccess,
                        ResourceAccess.RayTracingAccelerationStructureRead | ResourceAccess.UnorderedAccess));
                }
                backend.Barrier(commands, new BufferBarrier(topStorage, PipelineSync.None,
                    PipelineSync.BuildRayTracingAccelerationStructure, ResourceAccess.NoAccess, ResourceAccess.RayTracingAccelerationStructureWrite));
                backend.BuildAccelerationStructure(commands, new(AccelerationStructureType.TopLevel,
                    buildOptions, topGeometry, top, scratch, BufferRange.Whole));
                backend.Barrier(commands, new BufferBarrier(topStorage, PipelineSync.BuildRayTracingAccelerationStructure,
                    PipelineSync.ComputeShading, ResourceAccess.RayTracingAccelerationStructureWrite,
                    ResourceAccess.RayTracingAccelerationStructureRead));
                using RecordedCommands recorded = backend.End(commands);
                QueueCompletion completed = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [recorded], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completed, TimeSpan.FromSeconds(30)));
            }

            var description = TestShaderLoading.CreateSessionDescription(vulkan ? SlangCompileTarget.Spirv : SlangCompileTarget.Dxil);
            description.SearchPaths = [Path.Combine(engine, "tests", "SomeEngine.Render.Cluster.Tests", "Shaders")];
            using var source = new ShaderSourceBlock { SourceNames = ["ray_shadow_reference.slang"] };
            SomeEngine.Resources.Shader shader = await source.CreateAsync(TestShaderLoading.Slang, description, "RT shadow ground truth");
            var reflection = shader.GetSlangReflection(0);
            var entry = shader.RequireSlangEntry(0, entryName, SlangStage.Compute);
            GraphicsPipeline pipeline = Keep(backend.CreateComputePipeline(device,
                new(shader.SlangCode, reflection, entry, shader.GetSlangEntryBytes(0, entry),
                    CodeBlob: shader.GetSlangEntryBlob(0, entry))));
            Buffer constants = Keep(backend.CreateBuffer(device, new BufferDesc(256, BufferUsages.Constant), MemoryType.Upload));
            using (MappedBuffer mapping = backend.Map(constants, MapType.Write, BufferRange.Whole))
            {
                mapping.Bytes.Clear();
                MemoryMarshal.Write(mapping.Bytes, in inputs);
                mapping.Flush(mapping.Range);
            }
            ulong outputBytes = inputs.Width * (ulong)inputs.Height * checked((uint)pixelBytes);
            Buffer output = Keep(backend.CreateBuffer(device,
                new BufferDesc(outputBytes, BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal));
            Buffer readback = Keep(backend.CreateBuffer(device, new BufferDesc(outputBytes, BufferUsages.CopyDestination), MemoryType.Readback));
            ResourceBinding[] bindings =
            [
                ResourceBinding.ConstantBuffer(Keep(backend.CreateBufferCbv(device, new(constants, BufferRange.Whole)))),
                ResourceBinding.AccelerationStructure(Keep(backend.CreateAccelerationStructureSrv(device, new(top)))),
                ResourceBinding.ReadOnlyBuffer(Keep(backend.CreateBufferSrv(device, new(vertexBuffer, BufferRange.Whole)))),
                ResourceBinding.ReadOnlyBuffer(Keep(backend.CreateBufferSrv(device, new(indexBuffer, BufferRange.Whole, StructureStride: 4)))),
                ResourceBinding.ReadOnlyBuffer(Keep(backend.CreateBufferSrv(device, new(geometryBuffer, BufferRange.Whole, StructureStride: 40)))),
                ResourceBinding.ReadOnlyBuffer(Keep(backend.CreateBufferSrv(device, new(texels, BufferRange.Whole)))),
                ResourceBinding.WritableBuffer(Keep(backend.CreateBufferUav(device, new(output, BufferRange.Whole, StructureStride: 4)))),
            ];
            using (CommandContext commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1)))
            {
                backend.Begin(commands);
                backend.Barrier(commands, new BufferBarrier(output, PipelineSync.None, PipelineSync.ComputeShading,
                    ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
                backend.SetPipeline(commands, pipeline);
                backend.SetTransientParameterBindings(commands, new(reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null, bindings, []));
                backend.Dispatch(commands, new((inputs.Width + 7) / 8, (inputs.Height + 7) / 8, 1));
                backend.Barrier(commands, new BufferBarrier(output, PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                backend.CopyBuffer(commands, new(output, 0, readback, 0, outputBytes));
                using RecordedCommands recorded = backend.End(commands);
                QueueCompletion completed = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [recorded], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completed, TimeSpan.FromSeconds(30)));
            }
            using MappedBuffer result = backend.Map(readback, MapType.Read, BufferRange.Whole);
            result.Invalidate(result.Range);
            return result.Bytes.ToArray();
        }
        finally
        {
            for (int index = resources.Count - 1; index >= 0; index--) resources[index].Dispose();
        }
    }

    private static ulong Align(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);
}
