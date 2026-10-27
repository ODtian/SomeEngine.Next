using SomeEngine.Graphics;

namespace DefaultGame;

public sealed partial class PhysicsSceneExample
{
    private int _siloParticles;

    public static PhysicsSceneExample CreateSiloDevice(int particles, IGraphicsBackend backend, Device device,
        PhysicsSceneTimings? timings = null)
    {
        PhysicsSceneExample example = CreateSiloDevice(particles, timings);
        example._backend = backend;
        example._device = device;
        return example;
    }

    private static PhysicsSceneExample CreateSiloDevice(int particles, PhysicsSceneTimings? timings) =>
        new(PhysicsSiloScenes.Describe(particles), new(16, 2, 1000d / 60)
            {
                ContactDistance = .02f, ContactNormalPenalty = 100, ContactTangentPenalty = 100,
                GeometryInputsFixed = true, EquationBindingsFixed = true, Timings = timings,
            })
        { _devicePhysics = true, _siloParticles = particles };

    private PhysicsSceneData ReadInput() => _siloParticles > 0
        ? PhysicsSiloScenes.Create(_siloParticles) : PhysicsScenes.Create(description);
}
