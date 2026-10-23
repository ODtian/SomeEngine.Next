using System.Numerics;
using SomeEngine.Serialization;

namespace SomeEngine.Animation.Pose;

public enum FootContactKind : byte { Free, Support, Sliding, ToePivot }
public readonly record struct FootBones(short Heel, short Toe);
public readonly record struct FootContactSegment(float Start, float End, FootContactKind Kind);
public readonly record struct FootContactMarker(int Foot, float Start, float End, FootContactKind Kind);
public readonly record struct FootAnalysisSettings(float SampleRate, float GroundHeight = 0,
    float HeightTolerance = 0.03f, float SpeedTolerance = 0.1f, int UpAxis = 1);
public readonly record struct FootTrajectory(Matrix4x4 Root, Vector3 Heel, Vector3 Toe);

/// <summary>Shared precomputed root and foot trajectories with contact intervals in clip seconds.</summary>
[AssetUse]
public sealed class FootMotion
{
    private readonly FootTrack[] _feet;
    private readonly FootContactMarker[] _markers;
    private readonly Matrix4x4 _initialRoot;
    public RootMotionPath Root { get; }
    public short RootBone { get; }
    public float Duration => Root.Duration;
    public int FootCount => _feet.Length;
    public FootAnalysisSettings Settings { get; }
    internal ReadOnlySpan<FootTrack> Feet => _feet;
    internal ReadOnlySpan<FootContactMarker> Markers => _markers;

    internal FootMotion(short rootBone, RootMotionPath root, FootTrack[] feet,
        FootAnalysisSettings settings, FootContactMarker[] markers)
    {
        RootBone = rootBone;
        Root = root;
        _feet = feet;
        Settings = settings;
        _markers = markers;
        _initialRoot = root.Keys[0].ToMatrix();
    }

    public FootBones Bones(int foot) => _feet[foot].Bones;
    public ReadOnlySpan<FootContactSegment> Contacts(int foot) => _feet[foot].Contacts;

    public FootContactSegment ContactAt(int foot, float time)
    {
        ReadOnlySpan<FootContactSegment> contacts = _feet[foot].Contacts;
        int first = 0, last = contacts.Length - 1;
        while (first < last)
        {
            int middle = (first + last + 1) / 2;
            if (contacts[middle].Start <= time) first = middle; else last = middle - 1;
        }
        return contacts[first];
    }

    /// <summary>Interpolates the precomputed model-space root, heel and toe trajectories.</summary>
    public FootTrajectory Sample(int foot, float time)
    {
        time = Math.Clamp(time, 0, Duration);
        ReadOnlySpan<RootMotionKey> keys = Root.Keys;
        int first = 0, last = keys.Length - 1;
        while (last - first > 1)
        {
            int middle = (first + last) / 2;
            if (keys[middle].Time <= time) first = middle; else last = middle;
        }
        float amount = (time - keys[first].Time) / (keys[last].Time - keys[first].Time);
        FootTrack track = _feet[foot];
        return new(_initialRoot * Root.Sample(time), Vector3.Lerp(track.Heel[first], track.Heel[last], amount),
            Vector3.Lerp(track.Toe[first], track.Toe[last], amount));
    }

    /// <summary>Samples the required chains together, classifies height and speed, then applies action markers.</summary>
    public static FootMotion Analyze(AnimationClip clip, short rootBone, ReadOnlySpan<FootBones> feet,
        in FootAnalysisSettings settings, ReadOnlySpan<FootContactMarker> markers = default)
    {
        int intervals = (int)MathF.Ceiling(clip.Duration * settings.SampleRate);
        var sampleTimes = new List<float>(intervals + 1 + markers.Length * 2);
        for (int sample = 0; sample <= intervals; sample++) sampleTimes.Add(clip.Duration * sample / intervals);
        foreach (FootContactMarker marker in markers) { sampleTimes.Add(marker.Start); sampleTimes.Add(marker.End); }
        float[] times = sampleTimes.Distinct().Order().ToArray();
        var bones = new short[1 + feet.Length * 2];
        bones[0] = rootBone;
        for (int foot = 0; foot < feet.Length; foot++)
        {
            bones[1 + foot * 2] = feet[foot].Heel;
            bones[2 + foot * 2] = feet[foot].Toe;
        }
        var selections = new ClipSampleSelection[times.Length];
        for (int sample = 0; sample < times.Length; sample++) selections[sample] = new(clip, bones);
        var batch = new ClipSampleBatch(selections);
        var transforms = new Matrix4x4[batch.OutputCount];
        batch.SampleModel(times, transforms);
        return FromSamples(rootBone, feet, in settings, times, transforms, markers);
    }

    internal static FootMotion FromSamples(short rootBone, ReadOnlySpan<FootBones> feet,
        in FootAnalysisSettings settings, ReadOnlySpan<float> times, ReadOnlySpan<Matrix4x4> transforms,
        ReadOnlySpan<FootContactMarker> markers)
    {
        int stride = 1 + feet.Length * 2;
        var root = new RootMotionKey[times.Length];
        var tracks = new FootTrack[feet.Length];
        for (int sample = 0; sample < times.Length; sample++)
        {
            Matrix4x4 value = transforms[sample * stride];
            Matrix4x4.Decompose(value, out _, out Quaternion rotation, out Vector3 translation);
            root[sample] = new(times[sample], translation, rotation);
        }
        for (int foot = 0; foot < tracks.Length; foot++)
        {
            var heel = new Vector3[times.Length];
            var toe = new Vector3[times.Length];
            for (int sample = 0; sample < times.Length; sample++)
            {
                heel[sample] = transforms[sample * stride + 1 + foot * 2].Translation;
                toe[sample] = transforms[sample * stride + 2 + foot * 2].Translation;
            }
            var contacts = new List<FootContactSegment>();
            for (int sample = 0; sample < times.Length - 1; sample++)
            {
                float start = times[sample], end = times[sample + 1];
                FootContactKind kind = Classify(heel[sample], heel[sample + 1], toe[sample], toe[sample + 1],
                    end - start, in settings);
                float middle = (start + end) * 0.5f;
                foreach (FootContactMarker marker in markers)
                    if (marker.Foot == foot && middle >= marker.Start && middle < marker.End) kind = marker.Kind;
                if (contacts.Count > 0 && contacts[^1].Kind == kind)
                    contacts[^1] = contacts[^1] with { End = end };
                else contacts.Add(new(start, end, kind));
            }
            tracks[foot] = new(feet[foot], heel, toe, contacts.ToArray());
        }
        return new(rootBone, new RootMotionPath(root), tracks, settings, markers.ToArray());
    }

    private static FootContactKind Classify(Vector3 heelFrom, Vector3 heelTo, Vector3 toeFrom, Vector3 toeTo,
        float delta, in FootAnalysisSettings settings)
    {
        bool heelLow = Height(heelFrom, settings.UpAxis) <= settings.GroundHeight + settings.HeightTolerance
            && Height(heelTo, settings.UpAxis) <= settings.GroundHeight + settings.HeightTolerance;
        bool toeLow = Height(toeFrom, settings.UpAxis) <= settings.GroundHeight + settings.HeightTolerance
            && Height(toeTo, settings.UpAxis) <= settings.GroundHeight + settings.HeightTolerance;
        bool heelStill = heelLow && Vector3.Distance(heelFrom, heelTo) <= settings.SpeedTolerance * delta;
        bool toeStill = toeLow && Vector3.Distance(toeFrom, toeTo) <= settings.SpeedTolerance * delta;
        if (heelStill) return FootContactKind.Support;
        if (toeStill) return FootContactKind.ToePivot;
        return heelLow && toeLow ? FootContactKind.Sliding : FootContactKind.Free;
    }

    private static float Height(Vector3 point, int axis) => axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;
}

internal sealed record FootTrack(FootBones Bones, Vector3[] Heel, Vector3[] Toe, FootContactSegment[] Contacts);
