using System.Text.Json.Nodes;
using SomeEngine.Core.ECS;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.Graphics;
using SomeEngine.Physics;

namespace DefaultGame;

internal readonly record struct PhysicsCrowdScale(string Id, int Characters, bool JointNet)
{
    internal const int MaxResourceStates = 65_536;
    internal string Name => JointNet ? "Physics / rigid bodies and bone cloth"
        : Characters == 2 ? "Physics / character and cloth" : $"Physics / {Characters} characters and cloth";

    internal static PhysicsCrowdScale? Read(string? id) => id switch
    {
        "character-cloth" => new(id, 2, false),
        "character-cloth-2000" => new(id, 2000, false),
        "character-cloth-5000" => new(id, 5000, false),
        "character-cloth-10000" => new(id, 10000, false),
        "rigid-cloth-mixed" => new(id, 625, true),
        _ => null,
    };

    internal PhysicsCrowdExample Create(IGraphicsBackend backend, Device device, PhysicsSceneTimings? timings = null) => JointNet
        ? PhysicsCrowdExample.CreateMixedDevice(backend, device, timings)
        : timings is null ? PhysicsCrowdExample.CreateDevice(Characters, backend, device)
            : PhysicsCrowdExample.CreateDevice(Characters, backend, device, timings);

    internal JsonObject Describe(PhysicsCrowdExample example, PhysicsCrowdScene scene, World world) => new()
    {
        ["Id"] = Id, ["Device"] = "GPU", ["Characters"] = example.CharacterCount,
        ["BonesPerCharacter"] = example.Character.Skeleton.BoneCount,
        ["ClothNodesPerCharacter"] = example.ClothSkeleton.BoneCount,
        ["Bodies"] = scene.Bodies.Count, ["RigidBodies"] = scene.Bodies.Count - scene.ClothBodies.Count,
        ["ClothNodes"] = scene.ClothBodies.Count,
        ["LengthConstraints"] = Count<XpbdLength>(world), ["BendConstraints"] = Count<XpbdBend>(world),
        ["Joints"] = Count<JointConstraint>(world),
        ["WorldDrivers"] = scene.WorldDrivers.Count, ["ParentDrivers"] = scene.ParentDrivers.Count,
        ["StepSeconds"] = scene.StepSeconds, ["StepBudgetMilliseconds"] = 1000d / 60,
        ["BroadPhase"] = "Hierarchical grid",
        ["NarrowPhase"] = example.JointNet is null ? "Capsule / dynamic triangle" : "Box / capsule / dynamic triangle",
        ["Response"] = "Contact and motor AVBD with length and bend XPBD",
        ["Iterations"] = 8, ["BodyIterations"] = 1, ["VelocityUpdatesPerStep"] = 1,
        ["Rendering"] = "DevicePoseOutput through MeshSkin and ordinary Game-world meshes",
        ["JobMaxResourceStates"] = MaxResourceStates,
        ["JobConfiguration"] = "DefaultGame selects the shared JobSystem before opening assets and scenes",
    };

    private static int Count<T>(World world) where T : unmanaged, IComponent
    {
        QueryHandle query = world.Query(new QueryDefinitionBuilder().Read<T>().Build());
        int count = 0;
        world.ExecuteReadQuery(query, ref count, static (scoped QueryCursor rows, ref int count) =>
        {
            foreach (QueryRow row in rows.Rows) count++;
        });
        world.ReleaseQuery(query);
        return count;
    }
}
