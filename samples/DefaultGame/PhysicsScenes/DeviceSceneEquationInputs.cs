using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneEquationInputs(DeviceAvbdSolver solver, bool bindingsFixed, int iterations)
{
    private bool _uploaded;

    internal SolveDeviceSceneEquations Solve() => new(this);

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        DeviceInputTransfer transfer = bindingsFixed && _uploaded ? DeviceInputTransfer.Resident : DeviceInputTransfer.Upload;
        _uploaded = true;
        JobHandle initialized = solver.Initialize(buildGraph: false, transfer).Schedule(in context, dependency);
        return solver.Iterate(iterations, buildGraph: false).Schedule(in context, initialized);
    }
}

internal readonly struct SolveDeviceSceneEquations(DeviceSceneEquationInputs inputs) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
}
