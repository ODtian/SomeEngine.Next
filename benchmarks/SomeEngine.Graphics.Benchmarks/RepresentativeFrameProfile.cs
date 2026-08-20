using System.Security.Cryptography;

namespace SomeEngine.Graphics.Benchmarks;

internal static class RepresentativeFrameProfile
{
    internal const string MaterialFileName = "representative-frame-materials.bin";
    internal const string MetadataFileName = "representative-frame-profile.json";
    internal const string MaterialSequenceSha256 =
        "4F69D660B527341D446A365853AA7FA8CCD853243853FE22CAB30D0608BB6AF0";
    internal const int ObjectCount = 1_025;
    internal const int MaterialCount = 40;
    internal const int WorkerCount = 3;
    internal const int CommandListCount = 9;
    internal const int DrawCount = ObjectCount * 2;
    internal const int BarrierCount = 4;
    internal const int ObjectPacketSize = 16;
    internal const int MaterialStride = 256;

#if REPRESENTATIVE_LIFECYCLE_ONLY
    internal const int PublicDrawRequestCount = 0;
    internal const int PublicPersistentBindingRequestCount = 0;
    internal const int NativePersistentBindingSetterCount = 0;
#elif REPRESENTATIVE_STATE_ONLY || REPRESENTATIVE_FIXED_ONLY
    internal const int PublicDrawRequestCount = 0;
    internal const int PublicPersistentBindingRequestCount = WorkerCount;
    internal const int NativePersistentBindingSetterCount = WorkerCount;
#elif REPRESENTATIVE_BINDINGS_ONLY
    internal const int PublicDrawRequestCount = 0;
    internal const int PublicPersistentBindingRequestCount = 107;
    internal const int NativePersistentBindingSetterCount = 107;
#elif REPRESENTATIVE_UNIFORM_MATERIAL
    internal const int PublicDrawRequestCount = DrawCount;
    internal const int PublicPersistentBindingRequestCount = WorkerCount * 2;
    internal const int NativePersistentBindingSetterCount = WorkerCount * 2;
#elif REPRESENTATIVE_PER_DRAW_BINDINGS
    internal const int PublicDrawRequestCount = DrawCount;
    internal const int PublicPersistentBindingRequestCount = DrawCount;
    internal const int NativePersistentBindingSetterCount = 107;
#else
    internal const int PublicDrawRequestCount = DrawCount;
    internal const int PublicPersistentBindingRequestCount = 107;
    internal const int NativePersistentBindingSetterCount = 107;
#endif

    internal static int LogicalDrawRequestCount
    {
        get
        {
#if REPRESENTATIVE_BINDINGS_ONLY || REPRESENTATIVE_FIXED_ONLY || REPRESENTATIVE_STATE_ONLY || REPRESENTATIVE_LIFECYCLE_ONLY
            return 0;
#else
            return DrawCount;
#endif
        }
    }

    internal static int LogicalMaterialBindingRequestCount
    {
        get
        {
#if REPRESENTATIVE_LIFECYCLE_ONLY
            return 0;
#elif REPRESENTATIVE_STATE_ONLY || REPRESENTATIVE_FIXED_ONLY
            return WorkerCount;
#elif REPRESENTATIVE_PER_DRAW_BINDINGS
            return DrawCount;
#elif REPRESENTATIVE_UNIFORM_MATERIAL
            return WorkerCount * 2;
#else
            return 107;
#endif
        }
    }

    internal static int NativeMaterialBindingCommandCount
    {
        get
        {
#if REPRESENTATIVE_LIFECYCLE_ONLY
            return 0;
#elif REPRESENTATIVE_STATE_ONLY || REPRESENTATIVE_FIXED_ONLY
            return WorkerCount;
#elif REPRESENTATIVE_UNIFORM_MATERIAL
            return WorkerCount * 2;
#else
            return 107;
#endif
        }
    }

    internal static int NativeBarrierCommandCount
    {
        get
        {
#if REPRESENTATIVE_LIFECYCLE_ONLY || REPRESENTATIVE_STATE_ONLY
            return 0;
#else
            return BarrierCount;
#endif
        }
    }

    internal static CommandWorkloadEvidence CreateWorkloadEvidence() => new(
        ObjectCount,
        LogicalDrawRequestCount,
        LogicalMaterialBindingRequestCount,
        LogicalDrawRequestCount,
        NativeMaterialBindingCommandCount,
        CommandListCount,
        CommandListCount,
        NativeBarrierCommandCount,
        WorkerCount,
        "single-call-per-draw");

    internal static byte[] LoadMaterials(string directory)
    {
        string path = Path.Combine(directory, MaterialFileName);
        byte[] result = File.ReadAllBytes(path);
        if (result.Length != ObjectCount)
        {
            throw new InvalidDataException(
                $"The representative material sequence has {result.Length} entries; {ObjectCount} are required.");
        }
        if (result.AsSpan().IndexOfAnyExceptInRange((byte)0, (byte)(MaterialCount - 1)) >= 0)
            throw new InvalidDataException("The representative material sequence contains an invalid material index.");
        string hash = Convert.ToHexString(SHA256.HashData(result));
        if (!string.Equals(hash, MaterialSequenceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The representative material sequence hash is invalid.");
        return result;
    }

    internal static void EmitSharedArtifacts(string directory)
    {
        string repository = BenchmarkOptions.FindRepositoryRoot(AppContext.BaseDirectory);
        string sourceDirectory = Path.Combine(
            repository,
            "artifacts",
            "public-render-workload-research");
        CopyVerified(sourceDirectory, directory, MaterialFileName);
        CopyVerified(sourceDirectory, directory, MetadataFileName);
        _ = LoadMaterials(directory);
    }

    internal static (int Start, int Count) GetWorkerRange(int workerIndex)
    {
        if ((uint)workerIndex >= WorkerCount)
            throw new ArgumentOutOfRangeException(nameof(workerIndex));
        int baseCount = ObjectCount / WorkerCount;
        int remainder = ObjectCount % WorkerCount;
        int count = baseCount + (workerIndex < remainder ? 1 : 0);
        int start = checked(workerIndex * baseCount + Math.Min(workerIndex, remainder));
        return (start, count);
    }

    internal static unsafe void WriteObjectPackets(Span<byte> destination, int frameIndex)
    {
        int required = ObjectCount * ObjectPacketSize;
        if (destination.Length < required)
            throw new ArgumentException("The representative object packet destination is too small.", nameof(destination));
        WriteObjectPacketsUnchecked(destination, frameIndex);
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static unsafe void WriteObjectPacketsUnchecked(
        Span<byte> destination,
        int frameIndex)
    {
        fixed (byte* first = destination)
        {
            int* output = (int*)first;
            int mixed = unchecked(frameIndex * 31);
            for (int objectIndex = 0; objectIndex < ObjectCount; objectIndex++)
            {
                *output++ = objectIndex;
                *output++ = frameIndex;
                *output++ = mixed;
                *output++ = objectIndex ^ frameIndex;
                mixed = unchecked(mixed + 17);
            }
        }
    }

    private static void CopyVerified(string sourceDirectory, string destinationDirectory, string fileName)
    {
        string source = Path.Combine(sourceDirectory, fileName);
        if (!File.Exists(source))
            throw new FileNotFoundException("The public-source representative frame artifact is missing.", source);
        string destination = Path.Combine(destinationDirectory, fileName);
        byte[] bytes = File.ReadAllBytes(source);
        if (!File.Exists(destination) || !File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(destination, bytes);
    }
}
