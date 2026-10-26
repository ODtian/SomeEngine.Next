using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void ProjectBodyAdjustmentUsesTheSameDeviceSolveWithoutConstraintRows()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.Zero, BodyMotion.Dynamic);
        QueryHandle members = world.Query(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().All<DeviceAvbdEquationBinding>());
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileBodyAdjustmentGraph(),
            new(1, 1), bindings, default, 64);
        using var solve = new DeviceAvbdSolver(backend, device, bodies, equations, graph, CompileProjectBodyAdjustment(),
            new(2, 1, 0, 0), bindings, default, 64);
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(world.Read<BodyPose>(moving).Position.X, .999999f, 1.000001f);
        Assert.InRange(world.Read<BodyVelocity>(moving).Linear.X, 9.99999f, 10.00001f);
        world.ReleaseQuery(members);
    }

    private DeviceGraphKernels CompileBodyAdjustmentGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("body_adjustment_graph", """
            #include "physics_constraint_graph.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID)
            { DeviceNoConstraintBodies rows; writeDeviceConstraintGraphRows(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }

    private DeviceAvbdKernels CompileProjectBodyAdjustment()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("project_body_adjustment", """
            #include "physics_avbd.slang"
            struct ProjectBodyAdjustment : IDeviceAvbdConstraints
            {
                uint count() { return 0; }
                bool active(uint row) { return false; }
                uint bodyCount(uint row) { return 0; }
                uint body(uint row,uint end) { return 0; }
                bool writes(uint row,uint end) { return false; }
                uint groupCount() { return 0; }
                uint group(uint row) { return 0; }
                void initialize(uint row,DeviceAvbdStep step) { }
                void accumulate(uint row,uint body,DeviceAvbdStep step,inout DeviceAvbdEquation equation) { }
                void updateDuals(uint row,DeviceAvbdStep step) { }
                void adjust(uint body,DeviceAvbdStep step,DeviceAvbdEquation equation,
                    inout float3 translation,inout float3 rotation)
                { translation.x=1-bodyCenterOfMass(DeviceBodyStates[body]).x; }
            };
            void updateProjectBody<T : IDeviceAvbdConstraints>(uint index,T rows)
            {
                if(rows.adjustsBody()) updateDeviceAvbdBody(index,rows);
            }
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID)
            { ProjectBodyAdjustment rows; initializeDeviceAvbd(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID)
            { ProjectBodyAdjustment rows; updateProjectBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID)
            { ProjectBodyAdjustment rows; updateDeviceAvbdDuals(id.x,rows); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)],
            "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2));
    }
}
