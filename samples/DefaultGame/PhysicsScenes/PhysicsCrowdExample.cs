using System.Numerics;
using SomeEngine.Animation.Pose;
using SomeEngine.ECS;
using SomeEngine.Graphics;

namespace DefaultGame;

/// <summary>The original crowd character with a driven arm and a four-by-four bone cloth.</summary>
public sealed class PhysicsCrowdExample
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;

    private PhysicsCrowdExample(int characters, IGraphicsBackend backend, Device device, PhysicsSceneTimings? timings)
    {
        CharacterCount = characters; _backend = backend; _device = device; Timings = timings;
        Character = new();
        var source = new GpuPoseBlend(Character.Skeleton, new GpuBlendClip(Character.Clips[1], Loop: true));
        CharacterRig = new(new GpuXpbdPose(source, new(Character.Skeleton,
            [new(20), new(21), new(22), new(23)], new(0, -9.81f, 0),
            lengthCompliance: 1e-7f, maximumBend: MathF.PI / 2, damping: .3f)));

        var parents = new short[16];
        var inherited = new short[16];
        var local = new BoneTransform[16];
        Array.Fill(inherited, (short)-1);
        for (int column = 0; column < 4; column++)
        for (int row = 0; row < 4; row++)
        {
            int bone = column * 4 + row;
            parents[bone] = (short)(row == 0 ? -1 : bone - 1);
            if (row == 0) inherited[bone] = 11;
            local[bone] = new() { Translation = row == 0 ? new(-.3f + column * .2f, 0, .15f) : new(0, -.2f, 0),
                Rotation = Quaternion.Identity, Scale = Vector3.One };
        }
        ClothSkeleton = new(Character.Skeleton, parents, local,
            Enumerable.Range(1, 16).Select(value => (uint)value).ToArray(), inherited);
        var clip = QuantizedClipData.Encode(new AnimationClip(ClothSkeleton, 2,
            ClipSampler.Create<RawClipData, RawClipSampler>(new(2, []))), 1);
        GpuPoseSource cloth = new GpuPoseBlend(ClothSkeleton, new GpuBlendClip(clip, Loop: true));
        for (int column = 0; column < 4; column++)
            cloth = new GpuXpbdPose(cloth, new(ClothSkeleton,
                Enumerable.Range(column * 4, 4).Select(bone => new GpuXpbdBone(bone, 20)).ToArray(),
                new(0, -9.81f, 0), lengthCompliance: 1e-6f, bendCompliance: 1e-5f,
                maximumBend: MathF.PI / 3, damping: .5f));
        ClothRig = new(cloth);
    }

    public int CharacterCount { get; }
    public CrowdExample Character { get; }
    public Skeleton ClothSkeleton { get; }
    internal GpuPoseRig CharacterRig { get; }
    internal GpuPoseRig ClothRig { get; }
    internal PhysicsSceneTimings? Timings { get; }
    public static PhysicsCrowdExample CreateDevice(int characters, IGraphicsBackend backend, Device device) =>
        new(characters, backend, device, null);
    public static PhysicsCrowdExample CreateDevice(int characters, IGraphicsBackend backend, Device device, PhysicsSceneTimings timings) =>
        new(characters, backend, device, timings);
    public PhysicsCrowdScene CreateSimulation(World world) => new(this, _backend, _device, world);
}
