using System.Numerics;
using SomeEngine.Animation.Pose;

namespace DefaultGame;

/// <summary>A procedural character example using the ordinary skeleton, clip and material APIs.</summary>
public sealed class CrowdExample
{
    public const int CharacterCount = 1000;
    public const int BoneCount = 96;
    public const int Rings = 18;
    public const int Segments = 48;
    public const int VertexCount = 24 * (2 + (Rings - 1) * Segments);
    public const int TriangleCount = 24 * 2 * (Rings - 1) * Segments;
    public const string VertexEncoding = "someengine.example.crowd.lbs4.v1";
    public static readonly string[] StateNames = ["idle", "walk", "run", "turn"];

    public Skeleton Skeleton { get; }
    public SkinBinding Binding { get; }
    public QuantizedClipData[] Clips { get; }
    public GpuPoseStateMachine Machine { get; }
    public Vector3[] Positions { get; }
    public Vector3[] Normals { get; }
    public Vector4[] Tangents { get; }
    public uint[] Indices { get; }
    public int[] VertexParts { get; }

    public CrowdExample()
    {
        var parents = new short[BoneCount];
        var local = new BoneTransform[BoneCount];
        var origins = new Vector3[BoneCount];
        var inverse = new Matrix4x4[BoneCount];
        for (int part = 0; part < Parts.Length; part++)
        for (int joint = 0; joint < 4; joint++)
        {
            int bone = part * 4 + joint;
            parents[bone] = (short)(joint == 0 ? Parts[part].Parent * 4 + 3 : bone - 1);
            if (part == 0 && joint == 0) parents[bone] = -1;
            origins[bone] = Vector3.Lerp(Parts[part].Start, Parts[part].End, joint / 4f);
            local[bone] = new()
            {
                Translation = origins[bone] - (parents[bone] < 0 ? Vector3.Zero : origins[parents[bone]]),
                Rotation = Quaternion.Identity, Scale = Vector3.One,
            };
            inverse[bone] = Matrix4x4.CreateTranslation(-origins[bone]);
        }
        short[] bones = Enumerable.Range(0, BoneCount).Select(value => (short)value).ToArray();
        Skeleton = new(parents, local, Enumerable.Range(1, BoneCount).Select(value => (uint)value).ToArray(), bones);
        Binding = new(inverse, bones);
        Clips = Enumerable.Range(0, 4).Select(state => QuantizedClipData.Encode(new AnimationClip(Skeleton, 2,
            ClipSampler.Create<Motion, MotionSampler>(new(Skeleton, state))), 60)).ToArray();
        var transitions = new List<GpuPoseTransition>();
        for (int source = 0; source < 4; source++)
        for (int target = 0; target < 4; target++)
            if (source != target) transitions.Add(new(source, target, 0, GpuPoseComparison.Equal, target, .3f));
        Machine = new(Clips.Select(clip => new GpuPoseState(clip, Loop: true)).ToArray(), transitions.ToArray());

        var positions = new List<Vector3>(VertexCount);
        var normals = new List<Vector3>(VertexCount);
        var tangents = new List<Vector4>(VertexCount);
        var vertexParts = new List<int>(VertexCount);
        var indices = new List<uint>(TriangleCount * 3);
        for (int part = 0; part < Parts.Length; part++)
        {
            BodyPart shape = Parts[part];
            Vector3 axis = Vector3.Normalize(shape.End - shape.Start);
            Vector3 across = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitZ));
            Vector3 depth = Vector3.Cross(across, axis);
            float length = Vector3.Distance(shape.Start, shape.End) * .5f + shape.Radius * .5f;
            Vector3 center = (shape.Start + shape.End) * .5f;
            uint first = (uint)positions.Count;
            Add(0, 0);
            for (int ring = 1; ring < Rings; ring++)
            for (int segment = 0; segment < Segments; segment++) Add(MathF.PI * ring / Rings, MathF.Tau * segment / Segments);
            uint bottom = (uint)positions.Count;
            Add(MathF.PI, 0);
            for (uint segment = 0; segment < Segments; segment++)
            {
                uint next = (segment + 1) % Segments;
                indices.AddRange([first, first + 1 + next, first + 1 + segment]);
                for (uint ring = 0; ring < Rings - 2; ring++)
                {
                    uint row = first + 1 + ring * Segments;
                    indices.AddRange([row + segment, row + next, row + Segments + segment,
                        row + next, row + Segments + next, row + Segments + segment]);
                }
                uint last = first + 1 + (Rings - 2) * Segments;
                indices.AddRange([last + segment, last + next, bottom]);
            }
            void Add(float latitude, float longitude)
            {
                Vector3 radial = across * MathF.Cos(longitude) + depth * MathF.Sin(longitude);
                positions.Add(center + axis * (MathF.Cos(latitude) * length) + radial * (MathF.Sin(latitude) * shape.Radius));
                normals.Add(Vector3.Normalize(axis * (MathF.Cos(latitude) / length) + radial * (MathF.Sin(latitude) / shape.Radius)));
                tangents.Add(new(-across * MathF.Sin(longitude) + depth * MathF.Cos(longitude), 1));
                vertexParts.Add(part);
            }
        }
        Positions = positions.ToArray(); Normals = normals.ToArray(); Tangents = tangents.ToArray();
        Indices = indices.ToArray(); VertexParts = vertexParts.ToArray();
    }

    /// <summary>Each instance has a different initial phase and a staggered four-state control cycle.</summary>
    public static float Phase(int character) => character * (2f / CharacterCount);
    public static int RequestedState(int character, double seconds) => ((int)((seconds + character * .037) / 1.25)) % 4;

    private sealed record Motion(Skeleton Skeleton, int State);
    private readonly struct MotionSampler : IClipSampler<Motion>
    {
        public static void SampleLocal(Motion motion, float time, Span<BoneTransform> destination)
        {
            float cycle = MathF.Tau * time * (motion.State == 2 ? 2 : 1);
            for (int bone = 0; bone < destination.Length; bone++)
            {
                int part = bone / 4;
                float side = part is >= 9 and <= 12 or >= 16 and <= 18 or >= 21 and <= 22 ? -1 : 1;
                float amplitude = motion.State switch { 0 => .025f, 1 => .12f, 2 => .24f, _ => .07f };
                float wave = MathF.Sin(cycle + part * .31f) * amplitude * side;
                Vector3 axis = part is >= 5 and <= 22 ? Vector3.UnitX : Vector3.UnitZ;
                if (motion.State == 3) axis = Vector3.UnitY;
                destination[bone].Rotation = Quaternion.CreateFromAxisAngle(axis, wave);
                destination[bone].Translation = motion.Skeleton.ReferencePose[bone].Translation;
                if (bone == 0) destination[bone].Translation.Y += MathF.Sin(cycle * 2) * amplitude * .15f;
            }
        }
    }

    private readonly record struct BodyPart(Vector3 Start, Vector3 End, float Radius, int Parent);
    // These 24 overlapping ellipsoids are the example fixture's body surfaces, each weighted to four bones.
    private static readonly BodyPart[] Parts =
    [
        new(new(0,.85f,0), new(0,1,0), .17f, -1),
        new(new(0,1,0), new(0,1.2f,0), .16f, 0),
        new(new(0,1.2f,0), new(0,1.45f,0), .2f, 1),
        new(new(0,1.45f,0), new(0,1.57f,0), .07f, 2),
        new(new(0,1.57f,0), new(0,1.85f,0), .13f, 3),
        new(new(0,1.4f,0), new(-.25f,1.4f,0), .085f, 2),
        new(new(-.25f,1.4f,0), new(-.45f,1.15f,0), .07f, 5),
        new(new(-.45f,1.15f,0), new(-.5f,.9f,0), .06f, 6),
        new(new(-.5f,.9f,0), new(-.5f,.78f,0), .07f, 7),
        new(new(0,1.4f,0), new(.25f,1.4f,0), .085f, 2),
        new(new(.25f,1.4f,0), new(.45f,1.15f,0), .07f, 9),
        new(new(.45f,1.15f,0), new(.5f,.9f,0), .06f, 10),
        new(new(.5f,.9f,0), new(.5f,.78f,0), .07f, 11),
        new(new(-.12f,.85f,0), new(-.15f,.45f,0), .1f, 0),
        new(new(-.15f,.45f,0), new(-.15f,.1f,0), .075f, 13),
        new(new(-.15f,.1f,0), new(-.15f,.07f,-.2f), .08f, 14),
        new(new(.12f,.85f,0), new(.15f,.45f,0), .1f, 0),
        new(new(.15f,.45f,0), new(.15f,.1f,0), .075f, 16),
        new(new(.15f,.1f,0), new(.15f,.07f,-.2f), .08f, 17),
        new(new(-.5f,.85f,0), new(-.59f,.76f,0), .025f, 8),
        new(new(-.5f,.78f,0), new(-.5f,.67f,0), .04f, 8),
        new(new(.5f,.85f,0), new(.59f,.76f,0), .025f, 12),
        new(new(.5f,.78f,0), new(.5f,.67f,0), .04f, 12),
        new(new(0,1.63f,-.07f), new(0,1.59f,-.1f), .07f, 4),
    ];
}
