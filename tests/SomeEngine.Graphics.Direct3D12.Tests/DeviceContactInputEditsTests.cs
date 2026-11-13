using System.Numerics;
using System.Runtime.InteropServices;
using Buffer = SomeEngine.Graphics.Buffer;
using SomeEngine.ECS;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void DeviceContactLinkEditChangesTheNextResidentBodyResponse()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        var moving = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Set(moving, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1, InverseInertia = Matrix4x4.Identity });
        using var source = CreateAvbdContactValues(jobs, floor, moving,
            new() { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1, WarmStart = true },
            new() { NormalStiffness = 100 }, [new(floor, Vector3.Zero, 1), new(moving, Vector3.Zero, 1)],
            new() { NormalPenalty = 100, TangentPenalty = 100 });
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(1, 1, 2), default);
        using var input = new DeviceContactInputs<DeviceAvbdContactState>(backend, device, world, source, contacts);
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>(); contacts.Current.AddBindings(bindings);
        JobResourceAccess[] reads = [];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(1, 2), bindings, reads, 64, contacts: contacts);
        using var solver = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileContactAvbd(), new(1, 1, 1, 0), bindings, reads, 64);
        using var response = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileContactActions(), bindings, reads, 64,
            Array.Empty<IDeviceConstraintSelection>());
        var stages = bodyInputs.Upload().Then(dynamics.Predict(new(0, -10, 0))).Then(response.Initialize()).Then(solver.Solve())
            .Then(dynamics.UpdateVelocities()).Then(results.Readback(64));
        var simulation = PhysicsSimulation.Create(world, members, stages, 1);
        var context = new PhysicsStepContext(world, members, 0, 0, 1, 0, .1);
        input.Upload().Schedule(in context, default).Complete();

        simulation.Schedule(0, 0, .1).Complete();
        using var edit = new DeviceContactLinkWrite(backend, device, contacts.Current.BodyLinks, 1, new(moving, new(.5f, 0, 0), 1));
        DevicePhysicsJobs.Schedule(jobs, edit,
            [JobResourceAccess.Write(jobs.GetResourceToken(contacts.Current.BodyLinks), 28, 28)], default).Complete();
        simulation.Schedule(1, .1, .1).Complete();

        Assert.InRange(world.ReadComponentData<BodyPose>(moving).Position.Y, .84870f, .84874f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(moving).Linear.Y, -.8463f, -.8460f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(moving).Angular.Z, .4100f, .4104f);
    }
    private sealed class DeviceContactLinkWrite : IDevicePhysicsSubmission, IDisposable
    {
        public IGraphicsBackend Backend { get; }
        public Device Device { get; }
        private readonly Buffer _target, _upload;
        private readonly CommandContext _commands;
        private readonly ulong _offset;
        internal DeviceContactLinkWrite(IGraphicsBackend backend, Device device, Buffer target, int row, ContactBodyLink value)
        {
            Backend = backend; Device = device; _target = target; _offset = (ulong)row * 28;
            _upload = backend.CreateBuffer(device, new(28, BufferUsages.CopySource), MemoryType.Upload);
            using (MappedBuffer mapping = backend.Map(_upload, MapType.Write, BufferRange.Whole))
            { MemoryMarshal.Write(mapping.Bytes, in value); mapping.Flush(mapping.Range); }
            _commands = backend.CreateCommandContext(device, new(QueueType.Compute, 0, 1));
        }
        public QueueCompletion Submit()
        {
            Backend.Begin(_commands, new(0, 0, 8));
            Backend.Barrier(_commands, new BufferBarrier(_target, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopyDestination));
            Backend.CopyBuffer(_commands, new(_upload, 0, _target, _offset, 28));
            Backend.Barrier(_commands, new BufferBarrier(_target, PipelineSync.Copy, PipelineSync.ComputeShading,
                ResourceAccess.CopyDestination, ResourceAccess.UnorderedAccess));
            using RecordedCommands commands = Backend.End(_commands);
            return Backend.Submit(Backend.GetQueue(Device, QueueType.Compute), new([], [], [commands], [], []));
        }
        public void Dispose() { _commands.Dispose(); _upload.Dispose(); }
    }

}
