using System.Text.Json.Serialization;

namespace SomeEngine.ECS.Relations.Benchmarks;

public sealed record DeviceData(string Cpu, int ProcessorCount, string OperatingSystem, string Framework,
    bool ServerGc, string TieredCompilation, string ReadyToRun, long StopwatchFrequency,
    string Command, string SourceCommit, string Backend, MeasurementProcessorData Machine);

public sealed record MeasurementProcessorData(string Configuration, ulong ProcessMask, ulong MainThreadMask,
    ulong PreviousMainThreadMask, string ProcessPriority, string MainThreadPriority, uint ProcessorAfterSetting);

public sealed record PairRow(int FirstIndex, int FirstGeneration, int SecondIndex, int SecondGeneration);
public sealed record EquivalenceData(bool Equal, int VerifiedFrames, int FinalPairCount,
    PairRow[] FirstRows, int[] ContactCounts, float[] PenetrationDepths);

public sealed record FrameSample(int Round, string SideOrder, string Side, int Frame, string Boundary,
    long ElapsedTicks, long QueryTicks, long ConsumeTicks, long FrameTicks, long DataWriteTicks,
    long MaintenanceTicks, long MaintenanceAllocatedBytes, long QueryAllocatedBytes, long ConsumeAllocatedBytes,
    int OutputPairCount, long ContactIncidences, double PenetrationDepthTotal,
    long FrameAllocatedBytes, long FrameCurrentThreadAllocatedBytes, int Gen0, int Gen1, int Gen2,
    long ManagedBytesBefore, long ManagedBytesAfter, long WorkingSetBefore, long WorkingSetAfter,
    uint ProcessorBefore, uint ProcessorAfter);

public sealed record ColdSample(int Pair, string SideOrder, string Side, long SetupTicks, long IndexBuildTicks,
    long FirstQueryTicks, long FirstConsumeTicks, long TotalTicks, int OutputPairCount, long AllocatedBytes);

public sealed record Distribution(double MedianTicks, double P95Ticks, double IqrTicks, double StandardDeviationTicks,
    double MinTicks, double MaxTicks, double MeanTicks, double PairsPerSecond, long TotalFrameAllocatedBytes);

public sealed record MetricComparison(string Boundary, bool Required, Distribution Relation, Distribution Handwritten,
    double MedianRatio, double P95Ratio, bool Passed);

public sealed record MeasurementWorkspace(string UsedBy, long SetupTicks, IndexMemory Memory);

public sealed record MeasurementReport(string ReportKind, MeasurementCase Input, DeviceData Device, DateTime StartedAtUtc,
    DateTime FinishedAtUtc, string DataSharing, MeasurementWorkspace[] Workspaces,
    long RelationIndexBuildTicks, long HandwrittenIndexBuildTicks,
    string[] Boundaries, string[] MemoryScope, EquivalenceData Equivalence,
    ColdSample[] ColdSamples, FrameSample[] RawSamples, MetricComparison[] Comparisons,
    IndexMemory RelationMemory, IndexMemory HandwrittenMemory, bool? Passed);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MeasurementReport))]
[JsonSerializable(typeof(MeasurementCase[]))]
internal partial class MeasurementJson : JsonSerializerContext;
