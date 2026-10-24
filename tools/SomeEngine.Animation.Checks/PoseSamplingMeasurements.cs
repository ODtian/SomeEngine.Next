using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Animation.Pose;

namespace SomeEngine.Animation.Checks;

internal static class PoseSamplingMeasurements
{
    private const int BoneCount = 96;
    private const int SampleCount = 1_000;

    internal static void Run()
    {
        var parents = new short[BoneCount];
        var reference = new BoneTransform[BoneCount];
        var channels = ImmutableArray.CreateBuilder<RawClipChannel>(BoneCount);
        for (int bone = 0; bone < BoneCount; bone++)
        {
            parents[bone] = (short)(bone == 0 ? -1 : (bone - 1) / 2);
            reference[bone] = new()
            {
                Translation = new(0, 0.025f, 0), Rotation = Quaternion.Identity, Scale = Vector3.One,
            };
            channels.Add(new((short)bone, BoneTransformProperty.Translation, ClipInterpolation.Linear,
                [0, 2], [new(0, 0.025f, 0, 0), new(0.2f, 0.025f, 0.1f, 0)]));
        }
        var skeleton = new Skeleton(parents, reference, new uint[BoneCount]);
        var curves = new RawClipData(2, channels.MoveToImmutable());
        var raw = new AnimationClip(skeleton, 2, ClipSampler.Create<RawClipData, RawClipSampler>(curves));
        var referenceClip = new AnimationClip(skeleton, 2,
            ClipSampler.Create<ReferenceClipData, ReferenceClipSampler>(new()));

        string tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default";
        Console.WriteLine($"framework={RuntimeInformation.FrameworkDescription}; architecture={RuntimeInformation.ProcessArchitecture}; nativeAot={!RuntimeFeature.IsDynamicCodeSupported}; tieredCompilation={tieredCompilation}; warmup=64; samples=96");
        Console.WriteLine("codec,poses,bones,selected,space,path,median_ns_per_pose,p95_ns_per_pose,allocated_bytes_per_batch");
        Measure("reference", referenceClip);
        Measure("raw", raw);
    }

    private static void Measure(string codec, AnimationClip clip)
    {
        var selection = new ClipSampleSelection(clip, [31, 47, 63, 79, 95]);
        var selections = new ClipSampleSelection[SampleCount];
        Array.Fill(selections, selection);
        var batch = new ClipSampleBatch(selections);
        var single = new ClipSampleBatch([selection]);
        var times = new float[SampleCount];
        for (int sample = 0; sample < times.Length; sample++) times[sample] = (sample % 256) / 128f;
        var local = new BoneTransform[batch.OutputCount];
        var model = new Matrix4x4[batch.OutputCount];
        int selected = selection.Bones.Length;

        Record(codec, selected, "local", "batch", () => batch.SampleLocal(times, local));
        Record(codec, selected, "local", "individual", () =>
        {
            for (int sample = 0; sample < SampleCount; sample++)
                single.SampleLocal(times.AsSpan(sample, 1), local.AsSpan(sample * selected, selected));
        });
        Record(codec, selected, "model", "batch", () => batch.SampleModel(times, model));
        Record(codec, selected, "model", "individual", () =>
        {
            for (int sample = 0; sample < SampleCount; sample++)
                single.SampleModel(times.AsSpan(sample, 1), model.AsSpan(sample * selected, selected));
        });
        GC.KeepAlive(local);
        GC.KeepAlive(model);
    }

    private static void Record(string codec, int selected, string space, string path, Action sample)
    {
        for (int warmup = 0; warmup < 64; warmup++) sample();
        var times = new double[96];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < times.Length; iteration++)
        {
            long start = Stopwatch.GetTimestamp();
            sample();
            times[iteration] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / SampleCount;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(times);
        Console.WriteLine(FormattableString.Invariant(
            $"{codec},{SampleCount},{BoneCount},{selected},{space},{path},{times[48]:F3},{times[91]:F3},{allocated / 96d:F3}"));
    }

    private sealed class ReferenceClipData { }

    private readonly struct ReferenceClipSampler : IClipSampler<ReferenceClipData>
    {
        public static void SampleLocal(ReferenceClipData data, float time, Span<BoneTransform> destination) { }

        public static void SampleLocalBatch<TSampler>(ReferenceClipData data, ref ClipSamples samples)
            where TSampler : struct, IClipSampler<ReferenceClipData> { }
    }
}
