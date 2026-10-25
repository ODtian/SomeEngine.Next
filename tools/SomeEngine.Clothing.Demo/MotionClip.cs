using System.Globalization;
using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public sealed class MotionClip
{
    private sealed class MotionBone
    {
        public string Name = "";
        public string? Parent;
        public Vector3 Direction;
        public float Length;
        public Quaternion Axes = Quaternion.Identity;
        public string[] Channels = [];
    }

    private readonly string[] names;
    private readonly Vector3[] directions;
    private readonly Quaternion[][] rotations;
    private readonly float[] heights;
    private readonly int firstFrame, lastFrame;
    private const int BlendFrames = 8;
    public int BoneCount => names.Length;
    public float ReferenceHeight { get; }

    public MotionClip()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Motion");
        Dictionary<string, MotionBone> definitions = new()
        {
            ["root"] = new() { Name = "root", Direction = Vector3.UnitY, Channels = ["rx", "ry", "rz"] },
        };
        string section = "";
        MotionBone? definition = null;
        foreach (string line in File.ReadLines(Path.Combine(directory, "16.asf")))
        {
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].StartsWith('#')) continue;
            if (parts[0].StartsWith(':')) { section = parts[0]; continue; }
            if (section == ":bonedata")
            {
                switch (parts[0])
                {
                    case "begin": definition = new(); break;
                    case "name": definition!.Name = parts[1]; break;
                    case "direction": definition!.Direction = Vector(parts, 1); break;
                    case "length": definition!.Length = Number(parts[1]); break;
                    case "axis": definition!.Axes = Euler(Vector(parts, 1)); break;
                    case "dof": definition!.Channels = parts[1..]; break;
                    case "end": definitions.Add(definition!.Name, definition); break;
                }
            }
            else if (section == ":hierarchy" && parts.Length > 1)
                foreach (string child in parts[1..]) definitions[child].Parent = parts[0];
        }
        List<MotionBone> bones = [];
        Dictionary<string, int> indices = [];
        void Add(string name)
        {
            if (indices.ContainsKey(name)) return;
            MotionBone bone = definitions[name];
            if (bone.Parent is not null) Add(bone.Parent);
            indices.Add(name, bones.Count);
            bones.Add(bone);
        }
        foreach (string name in definitions.Keys) Add(name);
        names = bones.Select(bone => bone.Name).ToArray();
        directions = bones.Select(bone => bone.Direction).ToArray();
        int[] parents = bones.Select(bone => bone.Parent is null ? -1 : indices[bone.Parent]).ToArray();
        Vector3[] ends = new Vector3[bones.Count];
        for (int i = 0; i < bones.Count; i++)
            ends[i] = (parents[i] < 0 ? Vector3.Zero : ends[parents[i]]) + bones[i].Direction * bones[i].Length;
        ReferenceHeight = -ends.Min(point => point.Y);

        List<Quaternion[]> frames = [];
        List<float> rootHeights = [];
        Quaternion[] localRotations = [];
        foreach (string line in File.ReadLines(Path.Combine(directory, "16_35.amc")))
        {
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0][0] is '#' or ':') continue;
            if (parts.Length == 1)
            {
                localRotations = Enumerable.Repeat(Quaternion.Identity, bones.Count).ToArray();
                frames.Add(localRotations);
                continue;
            }
            int index = indices[parts[0]];
            MotionBone bone = bones[index];
            Quaternion rotation = Quaternion.Identity;
            int offset = index == 0 ? 4 : 1;
            if (index == 0) rootHeights.Add(Number(parts[2]));
            for (int channel = 0; channel < bone.Channels.Length; channel++)
            {
                Vector3 axis = bone.Channels[channel] switch { "rx" => Vector3.UnitX, "ry" => Vector3.UnitY, _ => Vector3.UnitZ };
                rotation = Quaternion.CreateFromAxisAngle(axis, Number(parts[offset + channel]) * MathF.PI / 180) * rotation;
            }
            localRotations[index] = bone.Axes * rotation * Quaternion.Conjugate(bone.Axes);
        }
        rotations = frames.ToArray();
        heights = rootHeights.ToArray();
        foreach (Quaternion[] frame in rotations)
        {
            Vector3 forward = Vector3.Transform(Vector3.UnitZ, frame[0]);
            Quaternion facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.Atan2(forward.X, forward.Z));
            frame[0] = facing * frame[0];
            for (int i = 1; i < bones.Count; i++) frame[i] = frame[parents[i]] * frame[i];
        }
        float best = float.PositiveInfinity;
        for (int begin = 12; begin < 55; begin++)
        for (int end = begin + 65; end < Math.Min(rotations.Length - 8, begin + 110); end++)
        {
            float difference = (heights[begin] - heights[end]) * (heights[begin] - heights[end]) * .002f;
            for (int bone = 0; bone < bones.Count; bone++)
                difference += 1 - MathF.Abs(Quaternion.Dot(rotations[begin][bone], rotations[end][bone]));
            if (difference < best) { best = difference; firstFrame = begin; lastFrame = end; }
        }
    }

    public int FindBone(string name) => Array.IndexOf(names, name);
    public Vector3 Direction(int bone) => directions[bone];

    public float Sample(float seconds, Span<Quaternion> pose)
    {
        float duration = lastFrame - firstFrame - BlendFrames;
        float frame = firstFrame + BlendFrames + seconds * 120 % duration;
        float otherFrame = firstFrame + frame - (lastFrame - BlendFrames);
        float blend = BodyMotion.Smooth(lastFrame - BlendFrames, lastFrame, frame);
        int a = (int)frame, b = a + 1;
        int otherA = Math.Max(firstFrame, (int)otherFrame), otherB = otherA + 1;
        for (int bone = 0; bone < pose.Length; bone++)
        {
            Quaternion primary = Quaternion.Slerp(rotations[a][bone], rotations[b][bone], frame - a);
            Quaternion secondary = Quaternion.Slerp(rotations[otherA][bone], rotations[otherB][bone], MathF.Max(0, otherFrame - otherA));
            pose[bone] = Quaternion.Slerp(primary, secondary, blend);
        }
        float primaryHeight = float.Lerp(heights[a], heights[b], frame - a);
        float secondaryHeight = float.Lerp(heights[otherA], heights[otherB], MathF.Max(0, otherFrame - otherA));
        return float.Lerp(primaryHeight, secondaryHeight, blend);
    }

    private static float Number(string value) => float.Parse(value, CultureInfo.InvariantCulture);
    private static Vector3 Vector(string[] values, int offset) => new(Number(values[offset]), Number(values[offset + 1]), Number(values[offset + 2]));
    private static Quaternion Euler(Vector3 degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees.Z * MathF.PI / 180)
        * Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees.Y * MathF.PI / 180)
        * Quaternion.CreateFromAxisAngle(Vector3.UnitX, degrees.X * MathF.PI / 180);
}
