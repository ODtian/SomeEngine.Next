using SomeEngine.Job;
using SomeEngine.Physics.Research;

namespace SomeEngine.Physics.Tests;

public sealed class PhysicsResearchJobTests
{
    [Theory]
    [InlineData(2, 11, PhysicsMaterialModel.RigidConstraints)]
    [InlineData(3, 103, PhysicsMaterialModel.RigidConstraints)]
    [InlineData(3, (int)PhysicsScene.RotatingRamp, PhysicsMaterialModel.CompliantMaterials)]
    public void CollisionStepsKeepTheSameBodiesWithCallerOnlyAndSharedWorkers(int dimension, int scene, PhysicsMaterialModel material)
    {
        using var caller = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using var workers = new Scheduler(new JobRuntimeConfig { WorkerCount = 2 });
        using var expected = new PhysicsDemo(dimension, scene, materialModel: material) { SleepingEnabled = false };
        using var actual = new PhysicsDemo(dimension, scene, materialModel: material) { SleepingEnabled = false };
        for (int frame = 0; frame < 96; frame++)
        {
            caller.ExecuteParallel(new StepSceneJob(expected), 1);
            workers.ExecuteParallel(new StepSceneJob(actual), 1);
        }
        Assert.Equal(expected.ReadBodies(), actual.ReadBodies());
        Assert.Equal(expected.ReadPenetration(), actual.ReadPenetration());
    }

    private readonly struct StepSceneJob(PhysicsDemo scene) : IJobParallelFor
    {
        public void Execute(int index) => scene.Step();
    }
}
