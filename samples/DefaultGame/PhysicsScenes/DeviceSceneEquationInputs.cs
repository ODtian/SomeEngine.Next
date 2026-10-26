using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceSceneEquationInputs(DeviceAvbdSolver solver, bool bindingsFixed)
{
    private bool _uploaded;

    internal SolveDeviceSceneEquations Solve() => new(this);

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        DeviceInputTransfer transfer = bindingsFixed && _uploaded ? DeviceInputTransfer.Resident : DeviceInputTransfer.Upload;
        _uploaded = true;
        return solver.Solve(transfer).Schedule(in context, dependency);
    }
}

internal readonly struct SolveDeviceSceneEquations(DeviceSceneEquationInputs inputs) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
}
