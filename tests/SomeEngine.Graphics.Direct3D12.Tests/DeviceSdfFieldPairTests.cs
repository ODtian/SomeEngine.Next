using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceReferencedGeometryTests
{
    [Fact]
    public void UserDeviceFieldContactsKeepBothLobesWhenTheOtherFieldConsumesTheSameTypeBin()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var parameters = new TwinSphereParameters(backend, device, new Vector2(2, .5f));
        using var bodies = new DeviceBodies(backend, device, 2);
        using var fields = new DeviceSdfFields(backend, device, new(2, 8));
        using var grid = new DeviceCollisionGrid(backend, device, CompileGrid(), new(1, 4), new(2, 256, 257, 4, 3));
        using var geometry = new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), bodies);
        Entity first = AddBody(world, bodies, 0, Vector3.Zero);
        world.Add(first, new SdfGeometry<TwinSphereField>(new(parameters), 1, .8f));
        world.Add(first, new DeviceSdfBinding(fields, 1));
        world.Add(first, new DeviceCollisionBody(grid, 0));
        world.Add(first, new DeviceGeometryBinding(geometry, 0));
        Entity second = AddBody(world, bodies, 1, Vector3.Zero);
        var plane = new SdfGridData(2, 2, 2, new float[] { -.75f, -.75f, 1.25f, 1.25f, -.75f, -.75f, 1.25f, 1.25f });
        world.Add(second, new SdfGeometry<TrilinearSdfGrid>(new(plane, new(new(-4, -1, -1), new(8, 2, 2))), 1, .8f));
        world.Add(second, new DeviceSdfBinding(fields, 0));
        world.Add(second, new DeviceCollisionBody(grid, 1));
        world.Add(second, new DeviceGeometryBinding(geometry, 1));
        world.Add(first, new ContactMaterial { NormalStiffness = 100 });
        world.Add(second, new ContactMaterial { NormalStiffness = 100 });
        QueryHandle firstFields = world.Query(world.QueryDefinition().All<SdfGeometry<TwinSphereField>>());
        QueryHandle secondFields = world.Query(world.QueryDefinition().All<SdfGeometry<TrilinearSdfGrid>>());
        var pairs = world.Between(firstFields, secondFields);
        using var firstInputs = grid.WriteGeometryReferences<SdfGeometry<TwinSphereField>, DeviceSdfSource<TwinSphereField, TwinSphereDeviceReader>>(
            pairs, default, 0, 64);
        using var secondInputs = grid.WriteGeometryReferences<SdfGeometry<TrilinearSdfGrid>, DeviceSdfSource<TrilinearSdfGrid, DeviceTrilinearSdfReader>>(
            pairs, default, 1, 64);
        ComputePipelineDesc updateKernel = Compile("two_field_bounds", """
            #include "physics_sdf_update.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Run(uint3 id : SV_DispatchThreadID) { updateDeviceGeometry<DeviceSdfUpdate>(id.x); }
            """);
        using var firstUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry, updateKernel, 0,
            DeviceInputTransfer.Upload, fields.GetBindings(), fields.GetReadAccesses(jobs));
        using var secondUpdate = new UpdateDeviceGeometry(backend, device, grid, geometry, updateKernel, 1,
            DeviceInputTransfer.Resident, fields.GetBindings(), fields.GetReadAccesses(jobs));
        var bindings = new Dictionary<string, ResourceBinding[]>(fields.GetBindings())
        { ["ProjectFieldParameters"] = [ResourceBinding.ReadOnlyBuffer(parameters.View)] };
        JobResourceAccess[] reads = [.. fields.GetReadAccesses(jobs),
            JobResourceAccess.Read(jobs.GetResourceToken(parameters.Buffer), 0, (long)parameters.Buffer.Info.Size)];
        using var detection = new DeviceCollisionDetection(backend, device, grid, geometry,
            Compile("two_fields", """
                #include "physics_sdf_collision.slang"
                #include "physics_sdf_grid.slang"
                StructuredBuffer<float2> ProjectFieldParameters;
                struct ProjectField : IDeviceSdfField {
                    DeviceSdfSample sample(DeviceSdfInput field, float3 p) {
                        float2 parameters = ProjectFieldParameters[field.fieldIndex];
                        float3 a = p - float3(-parameters.x, 0, 0), b = p - float3(parameters.x, 0, 0);
                        bool first = dot(a, a) < dot(b, b);
                        float3 delta = first ? a : b;
                        float size = length(delta);
                        DeviceSdfSample result;
                        result.distance = size - parameters.y;
                        result.gradient = size > 0 ? delta / size : float3(1, 0, 0);
                        result.feature = first ? 0 : 1;
                        return result;
                    }
                };
                [shader("compute")][numthreads(64,1,1)]
                void Run(uint3 id : SV_DispatchThreadID) {
                    DeviceSdfFieldCollision<ProjectField, DeviceTrilinearSdfGrid> kernel;
                    kernel.firstSampling.cells = uint3(16, 4, 4);
                    kernel.firstSampling.rootIterations = 20;
                    kernel.firstSampling.tolerance = 1e-5f;
                    kernel.secondSampling = kernel.firstSampling;
                    kernel.tolerance = 1e-5f;
                    detectDeviceReferencedCollision(id.x, kernel);
                }
                """), 1, new(4, 4, 1), new(1e-5f, 32, 64), 0, DeviceInputTransfer.Resident,
            DeviceCollisionReadback.Contacts, bindings, reads);
        using var detected = new ContactPointBuffer(jobs, 4, 8);
        QueryHandle poses = world.Query(world.QueryDefinition().Read<BodyPose>());
        QueryHandle pointScope = world.Query(world.QueryDefinition().Read<ContactPoint>());
        using var points = ContactPoints.Create(world, poses, pointScope, detected, new AverageContactMaterials(),
            new AvbdContactComponents(new AvbdContactState
            { NormalPenalty = 100, TangentPenalty = 100, FrictionRegularizationSpeed = .01f }), new ContactRefreshSettings(.02f, .99f, false));
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(grid.BeginInputs()).Then(firstInputs).Then(secondInputs)
            .Then(fields.UploadInputs()).Then(firstUpdate.Update()).Then(secondUpdate.Update())
            .Then(grid.Build(pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None)).Then(detection.Detect())
            .Then(detection.WriteContacts(new ContactPointOutput(detected), new[] { detected.WriteAccess })).Then(points.Refresh());

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(5, 1, .25).Complete();

        QueryHandle results = world.Query(world.QueryDefinition().Read<ContactPoint>().ReadBuffer<ContactBodyLink>());
        var contacts = new List<ContactPoint>();
        world.ExecuteReadQuery(results, rows =>
        {
            foreach (QueryRow row in rows.Rows)
            {
                contacts.Add(row.Read<ContactPoint>());
                Assert.Equal(2, row.ReadBuffer<ContactBodyLink>().Count);
            }
        });
        Assert.Contains(contacts, point => point.FirstLocalPoint.X < -1);
        Assert.Contains(contacts, point => point.FirstLocalPoint.X > 1);
        Near(-.25f, contacts.Min(point => point.Separation));
        world.ReleaseQuery(results);
        world.ReleaseQuery(poses);
        world.ReleaseQuery(pointScope);
        world.ReleaseQuery(firstFields);
        world.ReleaseQuery(secondFields);
    }

    private readonly record struct TwinSphereField(TwinSphereParameters Data) : ISdfField<TwinSphereField>
    {
        public static CollisionBounds Bounds(in TwinSphereField field) => new(new(-4, -1, -1), new(4, 1, 1));
        public static SdfSample Sample(in TwinSphereField field, Vector3 point)
        {
            Vector3 first = point - new Vector3(-field.Data.Radii.X, 0, 0), second = point - new Vector3(field.Data.Radii.X, 0, 0);
            bool useFirst = first.LengthSquared() < second.LengthSquared();
            Vector3 delta = useFirst ? first : second;
            float size = delta.Length();
            return new(size - field.Data.Radii.Y, size > 0 ? delta / size : Vector3.UnitX, useFirst ? 0 : 1);
        }
    }
    private readonly struct TwinSphereDeviceReader : IDeviceSdfReader<TwinSphereField>
    {
        public static void Collect(in SdfGeometry<TwinSphereField> component, ref JobResourceAccessBuilder resources) =>
            resources.Write(component.Field.Data.Buffer, 0, (long)component.Field.Data.Buffer.Info.Size);
        public DeviceSdfInput Read(in SdfGeometry<TwinSphereField> geometry, DeviceSdfFields destination, int index)
        {
            geometry.Field.Data.Write(index);
            return new() { Scale = geometry.Scale, TraceStepScale = geometry.TraceStepScale, Min = new(-4, -1, -1), Max = new(4, 1, 1) };
        }
    }
    private sealed class TwinSphereParameters : IDisposable
    {
        private MappedBuffer _mapping;
        public Vector2 Radii { get; }
        public SomeEngine.Graphics.Buffer Buffer { get; }
        public BufferSrv View { get; }
        public TwinSphereParameters(IGraphicsBackend backend, Device device, Vector2 radii)
        {
            Radii = radii;
            Buffer = backend.CreateBuffer(device, new(16, BufferUsages.ShaderRead), MemoryType.Upload);
            View = backend.CreateBufferSrv(device, new(Buffer, new(0, 16), StructureStride: 8));
            _mapping = backend.Map(Buffer, MapType.Write, BufferRange.Whole);
            _mapping.Bytes.Clear();
        }
        public void Write(int index)
        {
            Vector2 radii = Radii;
            MemoryMarshal.Write(_mapping.Bytes.Slice(index * 8, 8), in radii);
            _mapping.Flush(new((ulong)index * 8, 8));
        }
        public void Dispose() { _mapping.Dispose(); View.Dispose(); Buffer.Dispose(); }
    }
}
