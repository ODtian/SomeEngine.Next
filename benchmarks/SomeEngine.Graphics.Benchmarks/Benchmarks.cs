using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SomeEngine.RenderGraph;
using D3D12Device = SomeEngine.Graphics.Direct3D12.Device;
using D3D12Options = SomeEngine.Graphics.Direct3D12.Options;
using ImmediateRenderGraph = SomeEngine.RenderGraph.RenderGraph;
using NullDevice = SomeEngine.Graphics.Null.Device;

namespace SomeEngine.Graphics;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class BenchmarkScenarioAttribute : Attribute;

/// <summary>
/// Executable correctness-oriented benchmark and soak scenarios. Timing values are evidence only;
/// run 0004 deliberately does not impose performance thresholds before a representative renderer
/// workload exists.
/// </summary>
public sealed class Benchmarks
{
    [BenchmarkScenario]
    public void CompilerCacheScenario()
    {
        using NullDevice device = new();
        using ImmediateRenderGraph graph = new(device, new RenderGraphOptions
        {
            CompileOptimizedPlansAsynchronously = false,
            CompilationCacheEntryLimit = 16,
        });
        BufferHandle output = device.CreateBuffer(
            new BufferDesc(256, BufferUsage.CopyDestination),
            MemoryType.Readback);

        const int iterations = 128;
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            for (int index = 0; index < iterations; index++)
            {
                GraphBuilder builder = graph.Begin();
                BufferId destination = builder.ImportBuffer(
                    output,
                    BufferUse.CopyDestination,
                    BufferUse.CopyDestination,
                    contentsAvailable: false);
                PassBuilder pass = builder.AddPass("benchmark-copy-root", QueueSelection.Copy);
                _ = pass.Write(destination, BufferUse.CopyDestination);
                pass.Execute(static (ICommandContext _, in PassResources _) => { });
                GraphExecution execution = graph.Execute(ref builder);
                Require(execution.Wait(TimeSpan.FromSeconds(5)), "Compiler/cache scenario did not complete.");
            }
        }
        finally
        {
            elapsed.Stop();
            device.DestroyBuffer(output);
            device.CollectGarbage();
        }

        Require(graph.Statistics.CacheHits > 0, "Repeated canonical recordings did not hit the transparent compilation cache.");
        BenchmarkArtifact.Write(nameof(CompilerCacheScenario), iterations, elapsed.Elapsed, device.Info);
    }

    [BenchmarkScenario]
    public void RhiDescriptorResourceScenario()
    {
        using D3D12Device device = new(new D3D12Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
        });
        const int iterations = 512;
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int index = 0; index < iterations; index++)
        {
            BufferHandle buffer = device.CreateBuffer(new BufferDesc(256, BufferUsage.ShaderWrite));
            BufferViewHandle view = device.CreateBufferView(new BufferViewDesc(
                buffer,
                BufferRange.Whole,
                BindingKind.StorageBuffer));
            SamplerHandle sampler = device.CreateSampler(new SamplerDesc());
            BindGroupLayoutHandle layout = device.CreateBindGroupLayout([
                new BindingDesc(0, BindingKind.StorageBuffer, 1, ShaderStage.Compute),
                new BindingDesc(1, BindingKind.Sampler, 1, ShaderStage.Compute),
            ]);
            BindGroupHandle group = device.CreateBindGroup(layout, [
                BindingWrite.Buffer(0, view),
                BindingWrite.SamplerValue(1, sampler),
            ]);

            using ICommandContext commands = device.AcquireCommandContext(QueueType.Compute, "benchmark-descriptor");
            CommandListHandle list = commands.Finish();
            GpuCompletion completion = device.Submit(QueueType.Compute, [list]);
            Require(completion.IsValid, "Descriptor scenario submission did not publish a completion.");
            Require(device.Wait(completion, TimeSpan.FromSeconds(5)), "Descriptor scenario native submission timed out.");

            device.DestroyBindGroup(group);
            device.DestroyBufferView(view);
            device.DestroyBuffer(buffer);
            device.DestroySampler(sampler);
            device.DestroyBindGroupLayout(layout);
            device.CollectGarbage();
        }
        elapsed.Stop();

        Require(!device.DrainDiagnostics().Any(static diagnostic =>
            diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption),
            "Descriptor scenario produced a D3D12 validation error.");
        Require(!string.IsNullOrWhiteSpace(device.Info.DriverVersion),
            "D3D12 adapter metadata did not report a driver/UMD version.");
        BenchmarkArtifact.Write(nameof(RhiDescriptorResourceScenario), iterations, elapsed.Elapsed, device.Info);
    }

    [BenchmarkScenario]
    public void LightweightTenThousandFrameSoak()
    {
        using NullDevice device = new();
        BufferDesc desc = new(4_096, BufferUsage.CopySource | BufferUsage.CopyDestination);
        const int frames = 10_000;
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int frame = 0; frame < frames; frame++)
        {
            ResourceRequirements requirements = device.GetBufferRequirements(desc);
            Require(requirements.Size >= desc.Size, "Null requirements under-reported the requested buffer size.");
            using ICommandContext commands = device.AcquireCommandContext(QueueType.Copy, "benchmark-soak");
            CommandListHandle list = commands.Finish();
            GpuCompletion completion = device.Submit(QueueType.Copy, [list]);
            Require(completion.IsValid, "Soak submission did not publish a completion.");
            if ((frame & 255) == 255) device.CollectGarbage();
        }
        elapsed.Stop();

        Require(device.Statistics.Submissions == frames, "The 10k-frame soak did not execute every frame.");
        BenchmarkArtifact.Write(nameof(LightweightTenThousandFrameSoak), frames, elapsed.Elapsed, device.Info);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal static class BenchmarkArtifact
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static void Write(string scenario, int iterations, TimeSpan elapsed, DeviceInfo device)
    {
        string root = FindRepositoryRoot(AppContext.BaseDirectory);
        string directory = Path.Combine(root, "harness", "artifacts", "graphics-benchmarks");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "graphics-rendergraph.v1.json");
        BenchmarkEnvelope envelope = File.Exists(path)
            ? JsonSerializer.Deserialize<BenchmarkEnvelope>(File.ReadAllText(path), Json) ?? CreateEnvelope(device)
            : CreateEnvelope(device);
        if (device.Backend == BackendKind.Direct3D12 || envelope.Adapter.Backend != BackendKind.Direct3D12.ToString())
            envelope.Adapter = DescribeAdapter(device);
        envelope.Results.RemoveAll(result => string.Equals(result.Scenario, scenario, StringComparison.Ordinal));
        envelope.Results.Add(new BenchmarkResult(
            scenario,
            iterations,
            elapsed.TotalMilliseconds,
            device.Name,
            device.Backend.ToString(),
            device.DriverVersion));
        envelope.Results.Sort(static (left, right) => string.CompareOrdinal(left.Scenario, right.Scenario));
        File.WriteAllText(path, JsonSerializer.Serialize(envelope, Json));

        using JsonDocument verification = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement rootElement = verification.RootElement;
        if (rootElement.GetProperty("schemaVersion").GetInt32() != SchemaVersion
            || string.IsNullOrWhiteSpace(rootElement.GetProperty("machine").GetProperty("cpu").GetString())
            || string.IsNullOrWhiteSpace(rootElement.GetProperty("adapter").GetProperty("name").GetString())
            || string.IsNullOrWhiteSpace(rootElement.GetProperty("build").GetProperty("configuration").GetString()))
        {
            throw new InvalidDataException("Graphics benchmark artifact is missing required schema or machine/adapter/build metadata.");
        }
    }

    private static BenchmarkEnvelope CreateEnvelope(DeviceInfo device) => new()
    {
        SchemaVersion = SchemaVersion,
        Machine = new BenchmarkMachine(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount),
        Adapter = DescribeAdapter(device),
        Build = new BenchmarkBuild(
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            Environment.GetEnvironmentVariable("GITHUB_SHA") ?? Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION") ?? "local-worktree"),
    };

    private static BenchmarkAdapter DescribeAdapter(DeviceInfo device) => new(
        device.Name,
        device.Backend.ToString(),
        device.VendorId,
        device.DeviceId,
        string.IsNullOrWhiteSpace(device.DriverVersion) ? "not-applicable:null" : device.DriverVersion,
        device.ApiVersion,
        device.ValidationEnabled);

    private static string FindRepositoryRoot(string start)
    {
        DirectoryInfo? current = new(start);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SomeEngine.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate SomeEngine.slnx from '{start}'.");
    }

    private sealed class BenchmarkEnvelope
    {
        public int SchemaVersion { get; set; }
        public BenchmarkMachine Machine { get; set; } = null!;
        public BenchmarkAdapter Adapter { get; set; } = null!;
        public BenchmarkBuild Build { get; set; } = null!;
        public List<BenchmarkResult> Results { get; set; } = [];
    }

    private sealed record BenchmarkMachine(string OperatingSystem, string Architecture, string Cpu, int LogicalProcessorCount);
    private sealed record BenchmarkAdapter(
        string Name,
        string Backend,
        uint VendorId,
        uint DeviceId,
        string Driver,
        string ApiVersion,
        bool ValidationEnabled);
    private sealed record BenchmarkBuild(string Configuration, string AssemblyVersion, string Commit);
    private sealed record BenchmarkResult(
        string Scenario,
        int Iterations,
        double ElapsedMilliseconds,
        string Adapter,
        string Backend,
        string Driver);
}
