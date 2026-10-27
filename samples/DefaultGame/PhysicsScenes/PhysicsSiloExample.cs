using SomeEngine.Graphics;

namespace DefaultGame;

public sealed partial class PhysicsSceneExample
{
    private int _siloParticles;

    public static PhysicsSceneExample CreateSiloDevice(int particles, IGraphicsBackend backend, Device device,
        PhysicsSceneTimings? timings = null) =>
        new(new($"silo-{particles}", $"{particles:N0} mixed-shape silo", 3, 408,
                "Mixed shapes", "Gate opens at step 180", "Drop, pile and discharge"),
            new(16, 2, 1000d / 60)
            {
                ContactDistance = .02f, ContactNormalPenalty = 100, ContactTangentPenalty = 100,
                GeometryInputsFixed = true, EquationBindingsFixed = true, Timings = timings,
            })
        { _devicePhysics = true, _backend = backend, _device = device, _siloParticles = particles };

    private PhysicsSceneData ReadInput() => _siloParticles > 0
        ? PhysicsSiloScenes.Create(_siloParticles) : PhysicsScenes.Create(description);
}
