using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SomeEngine.Serialization;

const int elementCount = 512 * 1024;
const int sampleCount = 11;
const int copiesPerSample = 4;
const int nativeBlockFixedHeaderSize = 48;
if (args.Length != 0)
    throw new ArgumentException("This measurement tool takes no arguments.");

NativeValue[] values = GC.AllocateUninitializedArray<NativeValue>(elementCount);
for (int index = 0; index < values.Length; index++)
{
    values[index] = new NativeValue
    {
        A = unchecked((ulong)index * 0x9E3779B185EBCA87UL),
        B = unchecked((ulong)index * 0xC2B2AE3D27D4EB4FUL),
        C = unchecked((uint)index * 2654435761U),
        D = unchecked((uint)~index),
    };
}

ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(values.AsSpan());
NativeLayoutProof<NativeValue> proof = NativeValue.NativeLayoutProof;
int nativeBlockLength = GetNativeBlockLength(elementCount, proof);
byte[] nativeDestination = GC.AllocateUninitializedArray<byte>(nativeBlockLength);

for (int warmup = 0; warmup < 5; warmup++)
{
    CopyDirect(payload, nativeDestination, copiesPerSample);
    WriteNative(values, proof, nativeDestination, copiesPerSample);
}

double[] ratios = new double[sampleCount];
long directTicksTotal = 0;
long nativeTicksTotal = 0;
for (int sample = 0; sample < sampleCount; sample++)
{
    long directTicks;
    long nativeTicks;
    if ((sample & 1) == 0)
    {
        directTicks = MeasureDirect(payload, nativeDestination, copiesPerSample);
        nativeTicks = MeasureNative(values, proof, nativeDestination, copiesPerSample);
    }
    else
    {
        nativeTicks = MeasureNative(values, proof, nativeDestination, copiesPerSample);
        directTicks = MeasureDirect(payload, nativeDestination, copiesPerSample);
    }

    directTicksTotal += directTicks;
    nativeTicksTotal += nativeTicks;
    ratios[sample] = (double)directTicks / nativeTicks;
}

Array.Sort(ratios);
double medianRatio = ratios[ratios.Length / 2];
double copiedGiB = (double)payload.Length * copiesPerSample * sampleCount / (1024 * 1024 * 1024);
double directSeconds = directTicksTotal / (double)Stopwatch.Frequency;
double nativeSeconds = nativeTicksTotal / (double)Stopwatch.Frequency;
double directGiBs = copiedGiB / directSeconds;
double nativeGiBs = copiedGiB / nativeSeconds;

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"serialization-native-performance: payload={payload.Length} samples={sampleCount} direct={directGiBs:F3}GiB/s native={nativeGiBs:F3}GiB/s median-ratio={medianRatio:P2}"));

ViewMeasurements.Run();
PageMeasurements.Run();
await PageWorkloads.RunAsync();
return 0;

static long MeasureDirect(ReadOnlySpan<byte> source, byte[] destination, int copies)
{
    long start = Stopwatch.GetTimestamp();
    CopyDirect(source, destination, copies);
    long elapsed = Stopwatch.GetTimestamp() - start;
    GC.KeepAlive(destination);
    return elapsed;
}

static void CopyDirect(ReadOnlySpan<byte> source, byte[] destination, int copies)
{
    for (int iteration = 0; iteration < copies; iteration++)
        source.CopyTo(destination);
}

static long MeasureNative(
    NativeValue[] values,
    in NativeLayoutProof<NativeValue> proof,
    byte[] destination,
    int copies)
{
    long start = Stopwatch.GetTimestamp();
    WriteNative(values, proof, destination, copies);
    long elapsed = Stopwatch.GetTimestamp() - start;
    GC.KeepAlive(destination);
    return elapsed;
}

static void WriteNative(
    NativeValue[] values,
    in NativeLayoutProof<NativeValue> proof,
    byte[] destination,
    int copies)
{
    for (int iteration = 0; iteration < copies; iteration++)
    {
        if (!TryWriteNativeBlock(destination, values.AsSpan(), proof, out int written)
            || written != destination.Length)
        {
            throw new InvalidOperationException(
                "The fixed performance-gate destination is too small; resizing and codec retries are forbidden.");
        }
    }
}

static bool TryWriteNativeBlock<T>(
    Span<byte> destination,
    ReadOnlySpan<T> values,
    in NativeLayoutProof<T> proof,
    out int written)
    where T : unmanaged
{
    int requiredLength = GetNativeBlockLength(values.Length, proof);
    if (destination.Length < requiredLength)
    {
        written = 0;
        return false;
    }

    var writer = new BinaryDataWriter(destination[..requiredLength]);
    NativeBlock.Write(ref writer, values, proof);
    written = writer.WrittenCount;
    return written == requiredLength;
}

static int GetNativeBlockLength<T>(
    int count,
    in NativeLayoutProof<T> proof)
    where T : unmanaged
{
    ArgumentOutOfRangeException.ThrowIfNegative(count);
    int payloadOffset = checked(
        (nativeBlockFixedHeaderSize + proof.Alignment - 1) & -proof.Alignment);
    return checked(payloadOffset + checked(count * proof.Size));
}

[BinarySerializable(BinaryCompatibility.ExactSchema)]
[BinaryNativeLayout("SomeEngine.Serialization.Benchmarks.NativeValue.v1")]
[StructLayout(LayoutKind.Sequential, Pack = 8)]
public partial struct NativeValue
{
    public ulong A;
    public ulong B;
    public uint C;
    public uint D;
}
