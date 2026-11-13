using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public interface IDeviceSdfReader<TField> : IJobComponentResources<SdfGeometry<TField>> where TField : struct, ISdfField<TField>
{
    DeviceSdfInput Read(in SdfGeometry<TField> geometry, DeviceSdfFields destination, int index);
}

public readonly struct DeviceTrilinearSdfReader : IDeviceSdfReader<TrilinearSdfGrid>
{
    public static void Collect(in SdfGeometry<TrilinearSdfGrid> component, ref JobResourceAccessBuilder resources) =>
        SdfGeometryResources<TrilinearSdfGrid>.Collect(in component, ref resources);
    public DeviceSdfInput Read(in SdfGeometry<TrilinearSdfGrid> geometry, DeviceSdfFields destination, int index)
    {
        TrilinearSdfGrid field = geometry.Field;
        CollisionBounds bounds = TrilinearSdfGrid.Bounds(in field);
        return new()
        {
            Origin = field.Sampling.Origin,
            CellSize = field.Sampling.CellSize,
            SampleStart = destination.WriteGridSamples(field.Data),
            XCount = (uint)field.Data.XCount,
            YCount = (uint)field.Data.YCount,
            ZCount = (uint)field.Data.ZCount,
            Scale = geometry.Scale,
            TraceStepScale = geometry.TraceStepScale,
            Min = bounds.Min,
            Max = bounds.Max
        };
    }
}

public readonly struct DeviceSdfSource<TField, TReader>(TReader reader) : IDeviceGeometrySource<SdfGeometry<TField>>
    where TField : struct, ISdfField<TField> where TReader : struct, IDeviceSdfReader<TField>
{
    public static void Collect(in SdfGeometry<TField> component, ref JobResourceAccessBuilder resources) =>
        TReader.Collect(in component, ref resources);
    public static long GetVersion(in SdfGeometry<TField> component) => TReader.GetVersion(in component);
    public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<DeviceBodyBinding>(true), ChunkAccess.Read<DeviceSdfBinding>(true)];
    public JobHandle Prepare(in PhysicsStepContext context, Query geometries, int rowsPerChunk, JobHandle dependency) =>
        new SystemRunner(context.World, rowsPerChunk == 0 ? 1024 : rowsPerChunk).Schedule(geometries, new WriteDeviceSdfInputJob<TField, TReader>(reader), dependency);
    public DeviceGeometryInput Read(in SdfGeometry<TField> component, SystemChunk chunk, int row, ReadOnlySpan<DeviceBodyBinding> bindings,
        scoped ReadLookup<DeviceBodyBinding> bodies, DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = new() { BodyCount = 1, FirstBody = (uint)bindings[row].Index, FieldIndex = (uint)chunk.Read<DeviceSdfBinding>()[row].Index };
        return new();
    }
}

public readonly struct DeviceSdfReferenceResources : IJobComponentResources<DeviceSdfBinding>
{
    public static void Collect(in DeviceSdfBinding component, ref JobResourceAccessBuilder resources) { }
}
public readonly struct DeviceSdfUploadResources : IJobComponentResources<DeviceSdfBinding>
{
    public static void Collect(in DeviceSdfBinding component, ref JobResourceAccessBuilder resources) =>
        component.Fields.CollectUploadWrites(component.Index, ref resources);
}
internal readonly struct WriteDeviceSdfInputJob<TField, TReader>(TReader reader) : IChunkAction
    where TField : struct, ISdfField<TField> where TReader : struct, IDeviceSdfReader<TField>
{
    public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<SdfGeometry<TField>>(true), ChunkAccess.Read<DeviceSdfBinding>(true)];
    public static void CollectResources(SystemChunk rows, ref JobResourceAccessBuilder resources)
    {
        ComponentResources.CollectRange<SdfGeometry<TField>, TReader>(rows.Read<SdfGeometry<TField>>(), ref resources);
        ComponentResources.CollectRange<DeviceSdfBinding, DeviceSdfUploadResources>(rows.Read<DeviceSdfBinding>(), ref resources);
    }
    public void Execute(SystemChunk rows)
    {
        var geometries = rows.Read<SdfGeometry<TField>>(); var bindings = rows.Read<DeviceSdfBinding>();
        for (int row = 0; row < rows.Count; row++)
        {
            DeviceSdfBinding binding = bindings[row];
            DeviceSdfInput input = reader.Read(in geometries[row], binding.Fields, binding.Index);
            input.FieldIndex = (uint)binding.Index; binding.Fields.WriteField(binding.Index, in input);
        }
    }
}
