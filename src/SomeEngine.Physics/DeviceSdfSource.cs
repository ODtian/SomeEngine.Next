using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
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
        return new() { Origin = field.Sampling.Origin, CellSize = field.Sampling.CellSize,
            SampleStart = destination.WriteGridSamples(field.Data), XCount = (uint)field.Data.XCount,
            YCount = (uint)field.Data.YCount, ZCount = (uint)field.Data.ZCount, Scale = geometry.Scale,
            TraceStepScale = geometry.TraceStepScale, Min = bounds.Min, Max = bounds.Max };
    }
}

public readonly struct DeviceSdfSource<TField, TReader>(TReader reader) : IDeviceGeometrySource<SdfGeometry<TField>>
    where TField : struct, ISdfField<TField> where TReader : struct, IDeviceSdfReader<TField>
{
    public static void Collect(in SdfGeometry<TField> component, ref JobResourceAccessBuilder resources) =>
        TReader.Collect(in component, ref resources);
    public static long GetVersion(in SdfGeometry<TField> component) => TReader.GetVersion(in component);
    public QueryDefinition AddReads(QueryDefinition definition) =>
        definition.WithOptionalRead<DeviceBodyBinding>().WithOptionalRead<DeviceSdfBinding>();
    public void AddReadResources(QueryReadResources resources)
    {
        resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        resources.Add<DeviceSdfBinding, DeviceSdfReferenceResources>();
    }
    public JobHandle Prepare(in PhysicsStepContext context, QueryDefinition geometries, int rowsPerPacket, JobHandle dependency) =>
        new WriteDeviceSdfInputJob<TField, TReader>(reader).ScheduleParallel(context.World,
            new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket, filter: geometries), dependency);
    public DeviceGeometryInput Read(in SdfGeometry<TField> component, scoped QueryRow row, scoped QueryCursor access,
        DeviceCollisionGeometry destination, out DeviceGeometryReference reference)
    {
        reference = new() { BodyCount = 1, FirstBody = (uint)row.Read<DeviceBodyBinding>().Index,
            FieldIndex = (uint)row.Read<DeviceSdfBinding>().Index };
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
internal readonly partial struct WriteDeviceSdfInputJob<TField, TReader>(TReader reader) : IJobEntity
    where TField : struct, ISdfField<TField> where TReader : struct, IDeviceSdfReader<TField>
{
    public void Execute([JobResources(nameof(TReader))] in SdfGeometry<TField> geometry,
        [JobResources<DeviceSdfUploadResources>] in DeviceSdfBinding binding)
    {
        DeviceSdfInput input = reader.Read(in geometry, binding.Fields, binding.Index);
        input.FieldIndex = (uint)binding.Index;
        binding.Fields.WriteField(binding.Index, in input);
    }
}
