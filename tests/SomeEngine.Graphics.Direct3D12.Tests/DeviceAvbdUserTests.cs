using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void ProjectEnergyAndBuiltInElasticityEnterTheSameDeviceBodySolve()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity anchor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, new(2, 0, 0), BodyMotion.Dynamic);
        using var elastic = new DeviceConstraintData<DeviceElasticConstraint>(backend, device, 1, "DeviceElasticConstraints");
        using var elasticStates = new DeviceConstraintState<DeviceAvbdElasticState>(backend, device, 1, "DeviceAvbdElasticStates");
        Entity connection = world.CreateRelation(anchor, moving, new ElasticConnection
        { FirstAnchor = Vector3.UnitX, RestRotation = Quaternion.Identity, LinearStiffness = 100 }).Entity;
        world.Add(connection, new AvbdElasticState { LinearPenalty = 900 });
        world.Add(connection, new DeviceConstraintBinding<DeviceElasticConstraint>(elastic, 0));
        world.Add(connection, new DeviceAvbdState<DeviceAvbdElasticState>(elasticStates));
        using var targets = new DeviceConstraintData<ProjectEnergyData>(backend, device, 1, "ProjectEnergies");
        using var targetStates = new DeviceConstraintState<ProjectEnergyState>(backend, device, 1, "ProjectEnergyStates");
        Entity target = world.CreateEntity(new ProjectEnergy(moving, new(4, 0, 0), 400));
        world.Add(target, new ProjectEnergyScale(.5f));
        world.Add(target, new ProjectEnergyHistory { Multiplier = Vector3.Zero, Penalty = 900, InitialError = Vector3.Zero });
        world.Add(target, new DeviceConstraintBinding<ProjectEnergyData>(targets, 0));
        world.Add(target, new DeviceAvbdState<ProjectEnergyState>(targetStates));
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        QueryHandle connections = world.Query(new QueryDefinitionBuilder().Read<ElasticConnection>().Read<UndirectedRelationEndpoints<ElasticConnection>>());
        QueryHandle elasticRows = world.Query(new QueryDefinitionBuilder().Read<AvbdElasticState>()
            .Read<DeviceConstraintBinding<DeviceElasticConstraint>>().Read<DeviceAvbdState<DeviceAvbdElasticState>>());
        QueryHandle energies = world.Query(new QueryDefinitionBuilder().Read<ProjectEnergy>().Read<ProjectEnergyScale>());
        QueryHandle targetRows = world.Query(new QueryDefinitionBuilder().Read<ProjectEnergyHistory>()
            .Read<DeviceConstraintBinding<ProjectEnergyData>>().Read<DeviceAvbdState<ProjectEnergyState>>());
        using var elasticInput = elastic.WriteInputs<ElasticConnection, DeviceElasticSource>(world, connections, default, 64);
        using var targetInput = targets.WriteInputs<ProjectEnergy, ProjectEnergySource>(world, energies, default, 64);
        using var elasticStateInput = new DeviceAvbdStateInputs<AvbdElasticState, DeviceElasticConstraint, DeviceAvbdElasticState, DeviceAvbdElasticStateSource>(
            backend, device, world, elasticRows, elastic, elasticStates, default);
        using var targetStateInput = new DeviceAvbdStateInputs<ProjectEnergyHistory, ProjectEnergyData, ProjectEnergyState, ProjectEnergyHistorySource>(
            backend, device, world, targetRows, targets, targetStates, default);
        using var elasticSelection = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint>(
            backend, device, world, connections, elastic, "DeviceElasticConstraintsSelection");
        using var targetSelection = new DeviceConstraintSelection<ProjectEnergy, ProjectEnergyData>(
            backend, device, world, energies, targets, "ProjectEnergySelection");
        using var elasticHistorySelection = new DeviceConstraintSelection<ElasticConnection, DeviceElasticConstraint, DeviceAvbdStateRows<DeviceAvbdElasticState>>(
            backend, device, world, connections, elastic, new(elasticStates), "DeviceElasticAvbdSelection");
        using var targetHistorySelection = new DeviceConstraintSelection<ProjectEnergy, ProjectEnergyData, DeviceAvbdStateRows<ProjectEnergyState>>(
            backend, device, world, energies, targets, new(targetStates), "ProjectEnergyAvbdSelection");
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using SomeEngine.Graphics.Buffer scale = backend.CreateBuffer(device, new(4, BufferUsages.ShaderRead), MemoryType.Upload);
        using (MappedBuffer mapping = backend.Map(scale, MapType.Write, BufferRange.Whole))
        {
            float value = .5f;
            MemoryMarshal.Write(mapping.Bytes, in value); mapping.Flush(mapping.Range);
        }
        using BufferSrv scaleView = backend.CreateBufferSrv(device, new(scale, new(0, 4), StructureStride: 4));
        var bindings = new Dictionary<string, ResourceBinding[]> { ["ProjectEnergyScales"] = [ResourceBinding.ReadOnlyBuffer(scaleView)] };
        elastic.AddBindings(bindings); elasticStates.AddBindings(bindings); targets.AddBindings(bindings); targetStates.AddBindings(bindings);
        JobResourceAccess[] reads = [.. elastic.GetReadAccesses(jobs), .. targets.GetReadAccesses(jobs), JobResourceAccess.Read(jobs.GetResourceToken(scale), 0, 4)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileProjectEnergyGraph(), new(2, 2), bindings, reads,
            64, new IDeviceConstraintSelection[] { elasticSelection, targetSelection });
        using var solver = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileProjectEnergyAvbd(), new(32, 1, 0, 0), bindings,
            reads.Concat(new[] { elasticStates.GetWriteAccess(jobs), targetStates.GetWriteAccess(jobs) }).ToArray(), 64,
            new IDeviceConstraintSelection[] { elasticHistorySelection, targetHistorySelection });
        var step = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(elastic.BeginInputs()).Then(elasticInput).Then(elastic.UploadInputs()).Then(elasticStateInput.Upload())
            .Then(targets.BeginInputs()).Then(targetInput).Then(targets.UploadInputs()).Then(targetStateInput.Upload())
            .Then(dynamics.Predict(Vector3.Zero)).Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, step, 1).Schedule(1, 0, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(moving).Position.X, 2.33332f, 2.33335f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Linear.X, 3.3332f, 3.3335f);
        Assert.Equal(Vector3.Zero, world.Read<BodyPose>(anchor).Position);
        world.ReleaseQuery(targetRows); world.ReleaseQuery(energies); world.ReleaseQuery(elasticRows);
        world.ReleaseQuery(connections); world.ReleaseQuery(members);
    }

    private readonly record struct ProjectEnergy(Entity Body, Vector3 Position, float Stiffness) : IComponent;
    private readonly record struct ProjectEnergyScale(float Value) : IComponent;
    private struct ProjectEnergyHistory : IComponent
    {
        public Vector3 Multiplier;
        public float Penalty;
        public Vector3 InitialError;
    }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct ProjectEnergyData
    {
        [FieldOffset(0)] public uint Body;
        [FieldOffset(4)] public float Stiffness;
        [FieldOffset(16)] public Vector3 Position;
    }
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct ProjectEnergyState
    {
        [FieldOffset(0)] public Vector3 Multiplier;
        [FieldOffset(12)] public float Penalty;
        [FieldOffset(16)] public Vector3 InitialError;
    }
    private readonly struct ProjectEnergySource : IDeviceConstraintSource<ProjectEnergy, ProjectEnergyData>
    {
        public static void Collect(in ProjectEnergy component, ref JobResourceAccessBuilder resources) { }
        public ProjectEnergyData Read(in ProjectEnergy component, scoped QueryRow row, scoped QueryCursor access)
        {
            access.TryGetRow(component.Body, out QueryRow body);
            return new() { Body = (uint)body.Read<DeviceBodyBinding>().Index, Position = component.Position,
                Stiffness = component.Stiffness * row.Read<ProjectEnergyScale>().Value };
        }
    }
    private readonly struct ProjectEnergyHistorySource : IDeviceAvbdStateSource<ProjectEnergyHistory, ProjectEnergyState>
    {
        public ProjectEnergyState Read(in ProjectEnergyHistory component) => new()
        { Multiplier = component.Multiplier, Penalty = component.Penalty, InitialError = component.InitialError };
    }
    private DeviceGraphKernels CompileProjectEnergyGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("project_energy_graph", ProjectEnergyBodies + """
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID)
            { DeviceConstraintBodyGroups<DeviceElasticBodies,ProjectEnergyBodies> rows; writeDeviceConstraintGraphRows(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private DeviceAvbdKernels CompileProjectEnergyAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("project_energy_avbd", "#include \"physics_avbd_elastic.slang\"\n"
            + ProjectEnergyBodies + ProjectEnergyMath + """
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceElasticAvbdConstraints,ProjectEnergyAvbd> rows; initializeDeviceAvbd(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceElasticAvbdConstraints,ProjectEnergyAvbd> rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceElasticAvbdConstraints,ProjectEnergyAvbd> rows; updateDeviceAvbdDuals(id.x,rows); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2));
    }
    private const string ProjectEnergyBodies = """
        #include "physics_elastic_data.slang"
        struct ProjectEnergyData { uint body; float stiffness; uint2 padding; float3 position; uint padding1; };
        RWStructuredBuffer<ProjectEnergyData> ProjectEnergies;
        RWStructuredBuffer<DeviceConstraintMetadata> ProjectEnergiesMetadata;
        StructuredBuffer<uint2> ProjectEnergySelection;
        struct ProjectEnergyBodies : IDeviceConstraintBodies
        {
            uint count() { uint rows,stride; ProjectEnergies.GetDimensions(rows,stride); return rows; }
            bool active(uint row) { return deviceConstraintSelected(ProjectEnergiesMetadata[row],ProjectEnergySelection[row]); }
            uint bodyCount(uint row) { return 1; }
            uint body(uint row,uint end) { return ProjectEnergies[row].body; }
            bool writes(uint row,uint end) { return true; }
            uint groupCount() { return 1; }
            uint group(uint row) { return 0; }
        };

        """;
    private const string ProjectEnergyMath = """
        struct ProjectEnergyState { float3 multiplier; float penalty; float3 initialError; uint padding; };
        RWStructuredBuffer<ProjectEnergyState> ProjectEnergyStates;
        StructuredBuffer<float> ProjectEnergyScales;
        StructuredBuffer<uint2> ProjectEnergyAvbdSelection;
        struct ProjectEnergyAvbd : IDeviceAvbdConstraints
        {
            ProjectEnergyBodies participants;
            uint count() { return participants.count(); }
            bool active(uint row)
            { return participants.active(row)&&deviceConstraintSelected(ProjectEnergiesMetadata[row],ProjectEnergyAvbdSelection[row]); }
            uint bodyCount(uint row) { return 1; }
            uint body(uint row,uint end) { return participants.body(row,end); }
            bool writes(uint row,uint end) { return true; }
            uint groupCount() { return 1; }
            uint group(uint row) { return 0; }
            void initialize(uint row,DeviceAvbdStep context)
            {
                ProjectEnergyState state=ProjectEnergyStates[row];
                state.multiplier*=context.retention;
                state.initialError=DeviceBodyStates[ProjectEnergies[row].body].startCenterOfMass-ProjectEnergies[row].position;
                ProjectEnergyStates[row]=state;
            }
            void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
            {
                ProjectEnergyData target=ProjectEnergies[row]; ProjectEnergyState state=ProjectEnergyStates[row];
                float2 terms=deviceAvbdMaterial(target.stiffness*ProjectEnergyScales[0],state.penalty);
                float3 error=bodyCenterOfMass(DeviceBodyStates[bodyIndex])-target.position-state.initialError*context.initialErrorFraction;
                float3 force=deviceAvbdForce(terms,error,state.multiplier);
                deviceAvbdAddRow(equation,float3(1,0,0),float3(0),force.x,terms.y);
                deviceAvbdAddRow(equation,float3(0,1,0),float3(0),force.y,terms.y);
                deviceAvbdAddRow(equation,float3(0,0,1),float3(0),force.z,terms.y);
            }
            void updateDuals(uint row,DeviceAvbdStep context)
            {
                ProjectEnergyData target=ProjectEnergies[row]; ProjectEnergyState state=ProjectEnergyStates[row];
                float3 error=bodyCenterOfMass(DeviceBodyStates[target.body])-target.position-state.initialError*context.initialErrorFraction;
                state.multiplier=deviceAvbdForce(deviceAvbdMaterial(target.stiffness*ProjectEnergyScales[0],state.penalty),error,state.multiplier);
                ProjectEnergyStates[row]=state;
            }
            void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
        };

        """;
}
