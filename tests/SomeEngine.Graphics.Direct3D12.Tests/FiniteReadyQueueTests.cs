// Hardware queue-protocol fixture; complements, but does not replace, renderer-quality validation.
// Root owns integration/compilation/execution. No production capability changes.
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using Xunit;
using Xunit.Abstractions;
using Buffer = SomeEngine.Graphics.Buffer;
namespace SomeEngine.Graphics.Direct3D12.Tests;
[Collection("Direct3D12 shader compilation")]
public sealed class FiniteReadyQueueTests(D3D12TestCompiler shaderCompiler, ITestOutputHelper output)
{
    private const uint Canary = 0xcafef00du;
    private static string[] ResourceNames =>
    [
    "TraversalNodes", "NodeIndirectArgs", "TraversalReady", "ClusterBatchReady",
    "NodeConsumed", "NodeAccepted", "ClusterConsumed", "ClusterPayload", "Diagnostics", "DelaySink",
    ];
    private sealed record Case(string Name, uint Roots, uint Depth, uint Capacity, uint MaxIds,
    uint Clusters, uint Delay, uint Views, bool Overflow);
    private static Case[] Cases =>
    [
    new("cluster_only_0", 0u, 0u, 128u, 1u, 0u, 0u, 1u, false),
    new("cluster_only_1", 0u, 0u, 128u, 1u, 1u, 0u, 1u, false),
    new("cluster_only_63", 0u, 0u, 128u, 1u, 63u, 0u, 1u, false),
    new("cluster_only_64", 0u, 0u, 128u, 1u, 64u, 0u, 1u, false),
    new("cluster_only_65", 0u, 0u, 128u, 1u, 65u, 0u, 1u, false),
    new("competing_roots", 17u, 0u, 32u, 17u, 0u, 0u, 1u, false),
    new("finite_delayed_forest", 2u, 4u, 682u, 682u, 0u, 1024u, 1u, false),
    new("wide_view_delayed_forest", 2u, 3u, 170u, 170u, 0u, 1024u, 65535u, false),
    new("exact_capacity", 2u, 3u, 170u, 170u, 0u, 0u, 1u, false),
    new("intentional_overflow", 2u, 4u, 37u, 682u, 0u, 1024u, 1u, true),
    new("mixed_nodes_and_partial_batch", 17u, 1u, 85u, 85u, 65u, 256u, 3u, false),
    ];
    private static D3D12TestEntry[] Entries =>
    [
    new("FixtureInitialize", SlangStage.Compute), new("Fixture00", SlangStage.Compute),
    new("Fixture01", SlangStage.Compute), new("Fixture10", SlangStage.Compute), new("Fixture11", SlangStage.Compute),
    ];
    [Fact]
    [Trait("Category", "Performance")]
    public void Production_ready_work_queue_compiles_all_view_specializations()
    {
        string directory = Environment.GetEnvironmentVariable("SE_FINITE_READY_SHADER_DIRECTORY")
        ?? throw new InvalidOperationException("Set SE_FINITE_READY_SHADER_DIRECTORY to the owned integration assets/Shaders directory.");
        Assert.True(Path.IsPathFullyQualified(directory));
        string traversal = Path.Combine(directory, "cluster_bvh_traverse.slang");
        string include = Path.GetFullPath(traversal).Replace('\\', '/');
        Assert.DoesNotContain("\"", include);
        string source = "#include \"" + include + "\"\n" + ProductionWrappers;
        D3D12TestEntry[] entries =
        [
        new("Production00", SlangStage.Compute),
        new("Production01", SlangStage.Compute),
        new("Production10", SlangStage.Compute),
        new("Production11", SlangStage.Compute),
        ];
        foreach ((SlangCompileTarget target, string profile) in new[]
        {
            (SlangCompileTarget.Dxil, "sm_6_8"),
            (SlangCompileTarget.Spirv, "spirv_1_5"),
        }
        )
        {
            D3D12TestShader shader = shaderCompiler.CompileForReflection(
            "production_finite_ready_queue", source, entries, target, profile);
            for (int i = 0; i < entries.Length; i++)
            {
                byte[] code = shader.GetEntryCode(i).ToArray();
                Assert.NotEmpty(code);
                output.WriteLine($"FINITE_READY_PRODUCTION_CODE target={target} entry={entries[i].Name} bytes={code.Length}");
            }
            GC.KeepAlive(shader);
        }
    }
    private const string ProductionWrappers = """
[shader("compute")][numthreads(64, 1, 1)]
void Production00(uint thread : SV_GroupThreadID) { CullClusterWork<false, false>(thread); }
[shader("compute")][numthreads(64, 1, 1)]
void Production01(uint thread : SV_GroupThreadID) { CullClusterWork<false, true>(thread); }
[shader("compute")][numthreads(64, 1, 1)]
void Production10(uint thread : SV_GroupThreadID) { CullClusterWork<true, false>(thread); }
[shader("compute")][numthreads(64, 1, 1)]
void Production11(uint thread : SV_GroupThreadID) { CullClusterWork<true, true>(thread); }
""";
    [Fact]
    public void Finite_ready_fixture_compiles_dxil_and_spirv()
    {
        foreach ((SlangCompileTarget target, string profile) in new[]
        {
            (SlangCompileTarget.Dxil, "sm_6_8"), (SlangCompileTarget.Spirv, "spirv_1_5"),
        }
        )
        {
            D3D12TestShader shader = shaderCompiler.CompileForReflection(
            "finite_ready_queue", ShaderSource, Entries, target, profile);
            for (int i = 0; i < Entries.Length; i++)
            {
                byte[] code = shader.GetEntryCode(i).ToArray();
                Assert.NotEmpty(code);
                output.WriteLine($"FINITE_READY_CODE target={target} entry={Entries[i].Name} bytes={code.Length} sha256={Convert.ToHexString(SHA256.HashData(code))}");
            }
            GC.KeepAlive(shader);
        }
        // Compilation/hash records are not an emitted synchronization certificate.
    }
    [Theory]
    [Trait("Category", "Hardware")]
    [InlineData(false)]
    [InlineData(true)]
    public void Hardware_finite_workers_drain_exact_work_and_reuse_ready_words(bool rotatedConsumers)
    {
        string source = ShaderSource;
        if (rotatedConsumers)
        {
            // Test-only relay stress: use a different group invocation to load
            // each owned payload. The exact-source fixture remains the false case.
            const string oldBlock = """
            if (thread < WorkNodeCount)
            {
                WorkNodes[thread] = TraversalNodes[WorkNodeStart + thread];
                uint ignored;
                InterlockedExchange(TraversalReady[WorkNodeStart + thread], 0u, ignored);
            }
""";
            const string newBlock = """
            uint payloadThread = (thread + 32u) & 63u;
            if (payloadThread < WorkNodeCount)
            {
                WorkNodes[payloadThread] = TraversalNodes[WorkNodeStart + payloadThread];
                uint ignored;
                InterlockedExchange(TraversalReady[WorkNodeStart + payloadThread], 0u, ignored);
            }
""";
            Assert.Equal(1, source.Split(oldBlock, StringSplitOptions.None).Length - 1);
            source = source.Replace(oldBlock, newBlock, StringComparison.Ordinal);
        }
        output.WriteLine($"FINITE_READY_SOURCE rotated={rotatedConsumers} sha256={Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))}");
        D3D12TestShader shader = shaderCompiler.Compile("finite_ready_queue", source, Entries, "sm_6_8");
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        TypeLayoutReflection dataLayout = layout.TypeLayout.UnwrapArray();
        if (dataLayout.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
        {
            dataLayout = dataLayout.ElementTypeLayout.UnwrapArray();
        }
        int ordinarySize = checked((int)dataLayout.GetSize(SlangParameterCategory.Uniform));
        Assert.True(ordinarySize >= 40, $"Fixture ordinary layout is too small: {ordinarySize}.");
        AssertFixtureUniformLayout(dataLayout);
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        AdapterEnumerationOptions adapterOptions = new(AdapterPreference.HighPerformance, IncludeSoftware: false);
        _ = backend.TryEnumerateAdapters(adapterOptions, [], out int adapterCount);
        AdapterInfo[] adapters = new AdapterInfo[adapterCount];
        Assert.True(backend.TryEnumerateAdapters(adapterOptions, adapters, out int confirmed));
        Assert.Equal(adapterCount, confirmed);
        AdapterInfo adapter = adapters.FirstOrDefault(static a => a.HardwareAccelerated);
        Assert.True(adapter.HardwareAccelerated, "This gate requires a hardware adapter; WARP does not certify it.");
        DeviceQueueDesc[] queues = [new(QueueType.Compute)];
        using Device device = backend.CreateDevice(new DeviceDesc(adapter.Id, queues, label: "finite ready queue fixture"));
        output.WriteLine($"FINITE_READY_ADAPTER name={adapter.Name} driver={adapter.DriverVersion} id={adapter.Id.Low:X16}:{adapter.Id.High:X16}");
        uint stressGroups = 64u;
        string? overrideGroups = Environment.GetEnvironmentVariable("SE_FINITE_READY_STRESS_GROUPS");
        if (overrideGroups is not null)
        {
            Assert.True(uint.TryParse(overrideGroups, out stressGroups) && stressGroups >= 3u && stressGroups <= 65535u,
            "SE_FINITE_READY_STRESS_GROUPS must be 3..65535.");
        }
        Pipeline[] pipelines = new Pipeline[Entries.Length];
        try
        {
            for (int i = 0; i < pipelines.Length; i++)
            {
                pipelines[i] = backend.CreateComputePipeline(device, new ComputePipelineDesc(
                shader.SlangCode, shader.Reflection, shader.GetEntryPoint(i), shader.GetEntryCode(i)));
            }
            foreach (Case c in Cases)
            {
                foreach (uint groups in new[]
                {
                    1u, 2u, stressGroups
                }
                )
                {
                    for (int specialization = 1; specialization <= 4; specialization++)
                    {
                        RunCase(backend, device, layout, ordinarySize, pipelines[0], pipelines[specialization],
                        c, groups, specialization, rotatedConsumers);
                    }
                }
            }
        }
        finally
        {
            foreach (Pipeline? pipeline in pipelines)
            {
                pipeline?.Dispose();
            }
            GC.KeepAlive(shader);
        }
    }
    private void RunCase(IGraphicsBackend backend, Device device, VariableLayoutReflection layout,
    int ordinarySize, Pipeline initialize, Pipeline worker, Case c, uint groups, int specialization, bool rotated)
    {
        uint[] words = [4u * (c.Capacity + 1u), 9u, c.Capacity + 1u, 3u, c.MaxIds + 1u, c.MaxIds + 1u, 66u, 66u, 9u, c.MaxIds + 1u];
        uint[] strides = [16u, 0u, 4u, 4u, 4u, 4u, 4u, 4u, 4u, 4u];
        Buffer[] buffers = new Buffer[words.Length];
        BufferUav[] views = new BufferUav[words.Length];
        ulong[] offsets = new ulong[words.Length];
        ulong totalBytes = 0;
        for (int i = 0; i < words.Length; i++)
        {
            offsets[i] = totalBytes;
            totalBytes += words[i] * 4ul;
        }
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(totalBytes, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new CommandContextDesc(QueueType.Compute, 0, 1));
        try
        {
            for (int i = 0; i < words.Length; i++)
            {
                buffers[i] = backend.CreateBuffer(device, new BufferDesc(words[i] * 4ul,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource), MemoryType.DeviceLocal);
                views[i] = backend.CreateBufferUav(device, new BufferUavDesc(buffers[i], BufferRange.Whole, StructureStride: strides[i]));
            }
            ResourceBinding[] resources = OrderNamedBindings(layout, views.Select((view, index) =>
            (ResourceNames[index], ResourceBinding.WritableBuffer(view))).ToArray());
            for (uint phase = 0u; phase < 3u; phase++)
            {
                uint[] fields = [c.Capacity, c.Roots, c.Depth, c.Clusters, c.MaxIds, 65u, c.Delay,
                0x10203040u + phase * 0x01010101u, phase == 0u ? 1u: 0u, c.Views];
                byte[] ordinary = new byte[ordinarySize];
                MemoryMarshal.AsBytes(fields.AsSpan()).CopyTo(ordinary);
                backend.Begin(context, new CommandRecordingDesc(64, 0, 4096));
                foreach (Buffer buffer in buffers)
                {
                    backend.Barrier(context, new BufferBarrier(buffer, phase == 0u ? PipelineSync.None: PipelineSync.Copy,
                    PipelineSync.ComputeShading, phase == 0u ? ResourceAccess.NoAccess: ResourceAccess.CopySource,
                    ResourceAccess.UnorderedAccess));
                }
                backend.SetPipeline(context, initialize);
                backend.SetTransientParameterBindings(context, new(layout, resources, ordinary));
                uint initializeItems = Math.Max(Math.Max(c.Capacity, c.MaxIds), 65u);
                backend.Dispatch(context, new((initializeItems + 63u) / 64u, 1u, 1u));
                foreach (Buffer buffer in buffers)
                {
                    backend.Barrier(context, new BufferBarrier(buffer, PipelineSync.ComputeShading, PipelineSync.ComputeShading,
                    ResourceAccess.UnorderedAccess, ResourceAccess.UnorderedAccess));
                }
                backend.SetPipeline(context, worker);
                backend.SetTransientParameterBindings(context, new(layout, resources, ordinary));
                backend.Dispatch(context, new(groups, 1u, 1u));
                for (int i = 0; i < buffers.Length; i++)
                {
                    backend.Barrier(context, new BufferBarrier(buffers[i], PipelineSync.ComputeShading, PipelineSync.Copy,
                    ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
                    backend.CopyBuffer(context, new BufferCopy(buffers[i], 0, readback, offsets[i], words[i] * 4ul));
                }
                using RecordedCommands commands = backend.End(context);
                QueueCompletion completion = backend.Submit(backend.GetQueue(device, QueueType.Compute), new([], [], [commands], [], []));
                Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(30)));
                uint[][] values = new uint[words.Length][];
                using (MappedBuffer map = backend.Map(readback, MapType.Read, new BufferRange(0, totalBytes)))
                {
                    map.Invalidate(new BufferRange(0, totalBytes));
                    for (int i = 0; i < words.Length; i++)
                    {
                        values[i] = MemoryMarshal.Cast<byte, uint>(map.Bytes.Slice(checked((int)offsets[i]), checked((int)(words[i] * 4u)))).ToArray();
                    }
                }
                Verify(c, groups, phase, values);
                output.WriteLine($"FINITE_READY_PASS case={c.Name} groups={groups} specialization={specialization} phase={phase} rotated={rotated} accepted={values[1][3]} rejections={values[8][2]} earlyRetire={values[8][5]}");
                backend.CollectCompleted(device);
            }
        }
        finally
        {
            foreach (BufferUav? view in views)
            {
                view?.Dispose();
            }
            foreach (Buffer? buffer in buffers)
            {
                buffer?.Dispose();
            }
        }
    }
    private static void Verify(Case c, uint groups, uint phase, uint[][] b)
    {
        for (int i = 0; i < b.Length; i++)
        {
            Assert.Equal(Canary, b[i][^1]);
        }
        Assert.All(b[0][^4..], static x => Assert.Equal(Canary, x));
        uint[] args = b[1], diag = b[8];
        Assert.Equal(args[3], args[4]);
        Assert.True(args[3] <= c.Capacity);
        Assert.Equal((c.Clusters + 63u) / 64u, args[5]);
        Assert.Equal(0u, args[6]);
        Assert.Equal(c.Clusters, args[7]);
        Assert.All(b[2][..^1], static x => Assert.Equal(0u, x));
        Assert.All(b[3][..^1], static x => Assert.Equal(0u, x));
        Assert.Equal(0u, diag[0]);
        Assert.Equal(0u, diag[1]);
        Assert.Equal(0u, diag[6]);
        Assert.Equal(0u, diag[7]);
        Assert.Equal(groups, diag[3]);
        Assert.Equal(groups, diag[4]);
        Assert.True(diag[5] <= groups);
        Assert.Equal(c.Overflow, diag[2] != 0u);
        uint accepted = 0u;
        uint salt = 0x10203040u + phase * 0x01010101u;
        for (uint i = 0u; i < c.MaxIds; i++)
        {
            Assert.True(b[5][i] <= 1u);
            Assert.Equal(b[5][i], b[4][i]);
            accepted += b[5][i];
            uint delay = 0u;
            if (b[5][i] != 0u && i >= c.Roots)
            {
                uint parent = (i - c.Roots) / 4u;
                delay = Token(parent, Depth(i, c.Roots) - 1u, c.Views, salt) ^ i;
                uint n = c.Delay == 0u ? 0u: c.Delay + ((i * 97u)&1023u);
                for (uint step = 0u; step < n; step++)
                {
                    delay = unchecked((delay ^ (delay>>13)) * 1664525u + step + 1013904223u);
                }
            }
            Assert.Equal(delay, b[9][i]);
        }
        Assert.Equal(args[3], accepted);
        Assert.Equal(c.Roots == 0u ? 0u: Math.Min(c.Capacity, c.MaxIds), accepted);
        HashSet < uint> ids = [];
        for (uint slot = 0u; slot < args[3]; slot++)
        {
            uint i = b[0][slot * 4u], depth = b[0][slot * 4u + 1u];
            Assert.True(i < c.MaxIds);
            Assert.True(ids.Add(i));
            Assert.Equal(1u, b[5][i]);
            Assert.Equal(Depth(i, c.Roots), depth);
            Assert.Equal(c.Views, b[0][slot * 4u + 3u]);
            Assert.Equal(Token(i, depth, c.Views, salt), b[0][slot * 4u + 2u]);
        }
        for (uint i = 0u; i < 65u; i++)
        {
            Assert.Equal(i < c.Clusters ? 1u: 0u, b[6][i]);
            Assert.Equal(unchecked(i * 0x27d4eb2du) ^ salt ^ 0xbad00badu, b[7][i]);
        }
    }
    private static uint Token(uint i, uint depth, uint views, uint salt) =>
    unchecked(i * 0x9e3779b9u) ^ unchecked(depth * 0x85ebca6bu) ^ views ^ salt;
    private static uint Depth(uint i, uint roots)
    {
        uint depth = 0u, end = roots, level = roots;
        while (i >= end)
        {
            depth++;
            level *= 4u;
            end += level;
        }
        return depth;
    }
    private static void AssertFixtureUniformLayout(TypeLayoutReflection globals)
    {
        VariableLayoutReflection fixture = FindField(globals, "Fixture");
        Assert.Equal((nuint)0, fixture.GetOffset(SlangParameterCategory.Uniform));
        TypeLayoutReflection fields = fixture.TypeLayout;
        string[] names =
        [
        "MaxNodes", "RootCount", "MaxDepth", "TotalClusters", "MaxIds", "ClusterCapacity",
        "DelayIterations", "PhaseSalt", "InitialPhase", "ViewMask",
        ];
        Assert.True(fields.GetSize(SlangParameterCategory.Uniform) >= 40u);
        for (uint i = 0; i < names.Length; i++)
        {
            VariableLayoutReflection field = FindField(fields, names[i]);
            Assert.Equal((nuint)(i * 4u), field.GetOffset(SlangParameterCategory.Uniform));
            Assert.Equal((nuint)4, field.TypeLayout.GetSize(SlangParameterCategory.Uniform));
        }
    }
    private static VariableLayoutReflection FindField(TypeLayoutReflection layout, string name)
    {
        for (uint i = 0; i < layout.FieldCount; i++)
        {
            VariableLayoutReflection field = layout.GetFieldByIndex(i);
            if (field.Name == name) return field;
        }
        throw new InvalidDataException($"Missing reflected fixture field '{name}'.");
    }
    private static ResourceBinding[] OrderNamedBindings(
    VariableLayoutReflection root,
    IReadOnlyList<(string Name, ResourceBinding Binding)> bindings)
    {
        var byName = new Dictionary < string, Queue < ResourceBinding>>(StringComparer.Ordinal);
        foreach ((string name, ResourceBinding binding) in bindings)
        {
            if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("Named test bindings cannot be mixed with ordinal bindings.");
            if (!byName.TryGetValue(name, out Queue < ResourceBinding>? values))
            byName.Add(name, values = new Queue < ResourceBinding>());
            values.Enqueue(binding);
        }
        if (root == VariableLayoutReflection.Null)
        throw new InvalidDataException("The Slang test entry has no global parameter layout.");
        TypeLayoutReflection contents = root.TypeLayout.UnwrapArray();
        if (contents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
        contents = contents.ElementTypeLayout.UnwrapArray();
        var result = new List < ResourceBinding>(bindings.Count);
        for (nint range = 0; range < contents.BindingRangeCount; range++)
        {
            nint descriptorRangeCount = contents.GetBindingRangeDescriptorRangeCount(range);
            if (descriptorRangeCount <= 0)
            continue;
            string name = contents.GetBindingRangeLeafVariable(range).Name;
            nint set = contents.GetBindingRangeDescriptorSetIndex(range);
            nint first = contents.GetBindingRangeFirstDescriptorRangeIndex(range);
            for (nint relative = 0; relative < descriptorRangeCount; relative++)
            {
                nint count = contents.GetDescriptorSetDescriptorRangeDescriptorCount(
                set,
                first + relative);
                nuint countMarker = unchecked((nuint)count);
                if (count < 0 || countMarker == Slang.UnknownSize || countMarker == Slang.UnboundedSize)
                {
                    throw new InvalidDataException(
                    $"Slang test resource '{name}' has an unresolved descriptor count.");
                }
                for (nint element = 0; element < count; element++)
                {
                    if (!byName.TryGetValue(name, out Queue < ResourceBinding>? values) ||
                    !values.TryDequeue(out ResourceBinding binding))
                    {
                        throw new InvalidDataException(
                        $"The test did not supply Slang resource '{name}'.");
                    }
                    result.Add(binding);
                }
            }
        }
        string[] remaining = byName
        .Where(static pair => pair.Value.Count != 0)
        .Select(static pair => pair.Key)
        .ToArray();
        if (remaining.Length != 0)
        {
            throw new InvalidDataException(
            "The test supplied resources absent from Slang reflection: " +
            string.Join(", ", remaining));
        }
        return [.. result];
    }
    // CRLF normalized to LF here for stable C# raw-string storage. Copied shader
    // sections are otherwise byte-identical to the frozen standalone fixture.
    private const string ShaderSource = """
// Synthetic queue fixture, not a replacement renderer and not GPU validation evidence.
// Scheduler and producer reservation bodies are extracted byte-for-byte from
// the exact guarded candidate/current core. Scene/view tests are intentionally
// replaced by deterministic finite producers and payload checksums.
static const uint CLUSTER_BVH_FANOUT = 4u;
static const uint CLUSTER_BVH_PARENTS_PER_GROUP = 16u;
static const uint CLUSTER_BATCH_SIZE = 64u;
static const uint CLUSTER_NODE_WRITE = 12u;
static const uint CLUSTER_NODE_READ = 16u;
static const uint CLUSTER_BATCH_READ = 20u;
static const uint CLUSTER_NODE_OUTSTANDING = 24u;
static const uint CLUSTER_BATCH_COUNT = 28u;
static const uint FIXTURE_CANARY = 0xcafef00du;
struct ClusterTraversalNode { uint Id; uint Depth; uint Token; uint ViewMask; };
globallycoherent RWStructuredBuffer<ClusterTraversalNode> TraversalNodes;
globallycoherent RWByteAddressBuffer NodeIndirectArgs;
globallycoherent RWStructuredBuffer<uint> TraversalReady;
globallycoherent RWStructuredBuffer<uint> ClusterBatchReady;
globallycoherent RWStructuredBuffer<uint> NodeConsumed;
globallycoherent RWStructuredBuffer<uint> NodeAccepted;
globallycoherent RWStructuredBuffer<uint> ClusterConsumed;
globallycoherent RWStructuredBuffer<uint> ClusterPayload;
globallycoherent RWStructuredBuffer<uint> Diagnostics;
globallycoherent RWStructuredBuffer<uint> DelaySink;
struct FixtureConfiguration
{
    uint MaxNodes;
    uint RootCount;
    uint MaxDepth;
    uint TotalClusters;
    uint MaxIds;
    uint ClusterCapacity;
    uint DelayIterations;
    uint PhaseSalt;
    uint InitialPhase;
    uint ViewMask;
};
FixtureConfiguration Fixture;
#define TraversalLevel Fixture

uint NodeToken(uint id, uint depth, uint viewMask)
{
    return (id * 0x9e3779b9u) ^ (depth * 0x85ebca6bu) ^ viewMask ^ Fixture.PhaseSalt;
}
uint ClusterToken(uint id) { return (id * 0x27d4eb2du) ^ Fixture.PhaseSalt ^ 0xbad00badu; }
void RejectClusterWork(uint ignoredReason)
{
    uint ignored;
    InterlockedAdd(Diagnostics[2], 1u, ignored);
}
void ProcessTraversalNode(ClusterTraversalNode node, bool valid, uint child,
    uint viewLane, uint viewLanes, uint slice, bool targetFilters, bool multipleViews);
void CullClusterCandidate(uint index, bool targetFilters);

uint WaveMaskBitCount(uint4 mask)
{
    return countbits(mask.x) + countbits(mask.y) + countbits(mask.z) + countbits(mask.w);
}

uint WaveMaskFirstLane(uint4 mask)
{
    if (mask.x != 0u) return firstbitlow(mask.x);
    if (mask.y != 0u) return 32u + firstbitlow(mask.y);
    if (mask.z != 0u) return 64u + firstbitlow(mask.z);
    return 96u + firstbitlow(mask.w);
}

uint WaveMaskPrefix(uint4 mask, uint lane)
{
    uint word = lane >> 5u;
    uint bit = lane & 31u;
    uint prefix = 0u;
    if (word > 0u) prefix += countbits(mask.x);
    if (word > 1u) prefix += countbits(mask.y);
    if (word > 2u) prefix += countbits(mask.z);
    uint current = word == 0u ? mask.x : word == 1u ? mask.y : word == 2u ? mask.z : mask.w;
    uint lower = bit == 0u ? 0u : ((1u << bit) - 1u);
    return prefix + countbits(current & lower);
}

uint ClusterParallelViewLanes(uint viewCount)
{
    uint count = min(max(viewCount, 1u), 16u);
    return count == 1u ? 1u : 1u << (firstbithigh(count - 1u) + 1u);
}

uint ReservePersistentNodes(uint amount)
{
    uint current = NodeIndirectArgs.Load(CLUSTER_NODE_WRITE);
    for (;;)
    {
        uint admitted = min(amount, TraversalLevel.MaxNodes - min(current, TraversalLevel.MaxNodes));
        uint observed;
        NodeIndirectArgs.InterlockedCompareExchange(CLUSTER_NODE_WRITE, current, current + admitted, observed);
        if (observed == current)
        {
            if (admitted != amount) RejectClusterWork(1u);
            return current;
        }
        current = observed;
    }
}
groupshared uint WorkNodeStart;
groupshared uint WorkNodeCount;
groupshared ClusterTraversalNode WorkNodes[CLUSTER_BVH_PARENTS_PER_GROUP];
groupshared uint WorkClusterBatch;
groupshared uint WorkClusterReady;
groupshared uint WorkViewLanes;
groupshared bool WorkFinished;

void ClaimReadyNodes(uint thread, bool multipleViews)
{
    uint leader = WaveMaskFirstLane(WaveActiveBallot(thread == 0u));
    uint4 active = WaveActiveBallot(true);
    uint index = WaveMaskPrefix(active, WaveGetLaneIndex());
    [allow_uav_condition]
    for (;;)
    {
        uint first = 0u;
        uint available = 0u;
        uint viewLanes = 1u;
        if (thread == 0u)
        {
            NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_READ, 0u, first);
            if (first < TraversalLevel.MaxNodes)
            {
                uint ready;
                InterlockedAdd(TraversalReady[first], 0u, ready);
                if (ready != 0u)
                {
                    // The publisher orders its payload before the ready exchange.
                    DeviceMemoryBarrier();
                    viewLanes = multipleViews ? min(ClusterParallelViewLanes(countbits(TraversalNodes[first].ViewMask)), CLUSTER_BVH_PARENTS_PER_GROUP) : 1u;
                    available = min(CLUSTER_BVH_PARENTS_PER_GROUP / viewLanes, TraversalLevel.MaxNodes - first);
                }
            }
        }

        first = WaveReadLaneAt(first, leader);
        available = WaveReadLaneAt(available, leader);
        uint ready = 0u;
        if (index < available) InterlockedAdd(TraversalReady[first + index], 0u, ready);
        uint4 unavailable = WaveActiveBallot(ready == 0u);
        uint count = min(available, WaveMaskBitCount(active));
        if (any(unavailable != 0u)) count = min(count, WaveMaskPrefix(active, WaveMaskFirstLane(unavailable)));
        uint retry = 0u;
        if (thread == 0u)
        {
            uint observed;
            if (count != 0u)
                NodeIndirectArgs.InterlockedCompareExchange(CLUSTER_NODE_READ, first, first + count, observed);
            else
                NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_READ, 0u, observed);
            retry = observed != first ? 1u : 0u;
            if (retry == 0u)
            {
                WorkNodeStart = first;
                WorkNodeCount = count;
                WorkViewLanes = viewLanes;
            }
        }
        // Only a successful CAS owns a ready prefix; a stale scan is discarded.
        if (WaveReadLaneAt(retry, leader) == 0u) return;
    }
}

bool ClaimReadyClusters(out uint batch, out uint count)
{
    // Resume completed this queue before the dispatch; inline leaves do not append here.
    uint total = NodeIndirectArgs.Load(CLUSTER_BATCH_COUNT);
    uint batches = (total + CLUSTER_BATCH_SIZE - 1u) / CLUSTER_BATCH_SIZE;
    batch = 0u;
    count = 0u;
    [allow_uav_condition]
    for (;;)
    {
        uint first;
        NodeIndirectArgs.InterlockedAdd(CLUSTER_BATCH_READ, 0u, first);
        if (first >= batches) return false;
        uint expected = min(CLUSTER_BATCH_SIZE, total - first * CLUSTER_BATCH_SIZE);
        uint ready;
        InterlockedAdd(ClusterBatchReady[first], 0u, ready);
        uint observed;
        if (ready == expected)
        {
            NodeIndirectArgs.InterlockedCompareExchange(CLUSTER_BATCH_READ, first, first + 1u, observed);
            if (observed == first)
            {
                batch = first;
                count = expected;
                return true;
            }
        }
        else
        {
            NodeIndirectArgs.InterlockedAdd(CLUSTER_BATCH_READ, 0u, observed);
            if (observed == first) return false;
        }
    }
}

void CullPersistentWork(uint thread, bool targetFilters = true, bool multipleViews = true)
{
    for (;;)
    {
        GroupMemoryBarrierWithGroupSync();
        if (WaveActiveAnyTrue(thread == 0u)) ClaimReadyNodes(thread, multipleViews);
        if (thread == 0u)
        {
            WorkClusterReady = 0u;
            if (WorkNodeCount == 0u && !ClaimReadyClusters(WorkClusterBatch, WorkClusterReady))
                WorkClusterReady = 0u;
            // No absent work is reserved. Any remaining publisher owns live work
            // and must finish publishing, synchronize its group, then probe again.
            WorkFinished = WorkNodeCount == 0u && WorkClusterReady == 0u;
        }
        AllMemoryBarrierWithGroupSync();
        if (WorkNodeCount != 0u)
        {
            DeviceMemoryBarrier();
            if (thread < WorkNodeCount)
            {
                WorkNodes[thread] = TraversalNodes[WorkNodeStart + thread];
                uint ignored;
                InterlockedExchange(TraversalReady[WorkNodeStart + thread], 0u, ignored);
            }
            GroupMemoryBarrierWithGroupSync();
            uint count = WorkNodeCount;
            uint viewLanes = multipleViews ? WorkViewLanes : 1u;
            uint parentIndex = thread / (CLUSTER_BVH_FANOUT * viewLanes);
            uint parent = min(parentIndex, count - 1u);
            uint child = thread & (CLUSTER_BVH_FANOUT - 1u);
            uint viewLane = (thread / CLUSTER_BVH_FANOUT) % viewLanes;
            ProcessTraversalNode(WorkNodes[parent], parentIndex < count, child,
                viewLane, viewLanes, parentIndex * CLUSTER_BVH_FANOUT + child, targetFilters, multipleViews);
            DeviceMemoryBarrierWithGroupSync();
            if (thread == 0u)
            {
                uint ignored;
                NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_OUTSTANDING, 0u - count, ignored);
            }
            continue;
        }
        uint count = WorkClusterReady;
        if (count != 0u)
        {
            DeviceMemoryBarrier();
            if (thread == 0u)
            {
                uint ignored;
                InterlockedExchange(ClusterBatchReady[WorkClusterBatch], 0u, ignored);
            }
            if (thread < count) CullClusterCandidate(WorkClusterBatch * CLUSTER_BATCH_SIZE + thread, targetFilters);
        }
        else if (WorkFinished)
            break;
    }
}


void ProcessTraversalNode(ClusterTraversalNode node, bool valid, uint child,
    uint viewLane, uint viewLanes, uint slice, bool targetFilters, bool multipleViews)
{
    if (!valid || viewLane != 0u) return;
    uint ignored;
    if (node.Id >= Fixture.MaxIds)
    {
        InterlockedAdd(Diagnostics[6], 1u, ignored);
        return;
    }
    if (child == 0u)
    {
        uint previous;
        InterlockedAdd(NodeConsumed[node.Id], 1u, previous);
        if (previous != 0u) InterlockedAdd(Diagnostics[1], 1u, ignored);
        if (node.Token != NodeToken(node.Id, node.Depth, node.ViewMask))
            InterlockedAdd(Diagnostics[0], 1u, ignored);
    }
    if (node.Depth >= Fixture.MaxDepth) return;
    uint id = Fixture.RootCount + node.Id * CLUSTER_BVH_FANOUT + child;
    if (id >= Fixture.MaxIds)
    {
        InterlockedAdd(Diagnostics[6], 1u, ignored);
        return;
    }
    uint output = ReservePersistentNodes(1u);
    if (output >= Fixture.MaxNodes) return;
    NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_OUTSTANDING, 1u, ignored);

    // Delay only finite local ALU work after reservation. Never wait on another
    // workgroup, a clock, a signal, a lock, or an atomic condition.
    uint delay = node.Token ^ id;
    uint iterations = Fixture.DelayIterations == 0u ? 0u :
        Fixture.DelayIterations + ((id * 97u) & 1023u);
    for (uint i = 0u; i < iterations; i++)
        delay = (delay ^ (delay >> 13u)) * 1664525u + i + 1013904223u;
    InterlockedExchange(DelaySink[id], delay, ignored);
    ClusterTraversalNode next = { id, node.Depth + 1u,
        NodeToken(id, node.Depth + 1u, node.ViewMask), node.ViewMask };
    InterlockedExchange(NodeAccepted[id], 1u, ignored);
    TraversalNodes[output] = next;
    DeviceMemoryBarrier();
    InterlockedExchange(TraversalReady[output], 1u, ignored);
}

void CullClusterCandidate(uint index, bool targetFilters)
{
    uint ignored;
    uint previous;
    InterlockedAdd(ClusterConsumed[index], 1u, previous);
    if (previous != 0u) InterlockedAdd(Diagnostics[1], 1u, ignored);
    if (ClusterPayload[index] != ClusterToken(index))
        InterlockedAdd(Diagnostics[0], 1u, ignored);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void FixtureInitialize(uint3 dispatchThread : SV_DispatchThreadID)
{
    uint i = dispatchThread.x;
    if (i < Fixture.MaxIds) { NodeConsumed[i] = 0u; NodeAccepted[i] = 0u; DelaySink[i] = 0u; }
    if (i < Fixture.ClusterCapacity) { ClusterConsumed[i] = 0u; ClusterPayload[i] = ClusterToken(i); }
    if (i < 8u) Diagnostics[i] = 0u;
    if (Fixture.InitialPhase != 0u)
    {
        if (i < Fixture.MaxNodes) TraversalReady[i] = 0u;
        if (i < (Fixture.ClusterCapacity + 63u) / 64u) ClusterBatchReady[i] = 0u;
    }
    if (i < Fixture.RootCount)
    {
        ClusterTraversalNode root = { i, 0u, NodeToken(i, 0u, Fixture.ViewMask), Fixture.ViewMask };
        TraversalNodes[i] = root;
        NodeAccepted[i] = 1u;
        TraversalReady[i] = 1u;
    }
    uint batchStart = i * CLUSTER_BATCH_SIZE;
    if (batchStart < Fixture.TotalClusters)
        ClusterBatchReady[i] = min(CLUSTER_BATCH_SIZE, Fixture.TotalClusters - batchStart);
    if (i == 0u)
    {
        NodeIndirectArgs.Store(0u, 1u); NodeIndirectArgs.Store(4u, 1u); NodeIndirectArgs.Store(8u, 1u);
        NodeIndirectArgs.Store(CLUSTER_NODE_WRITE, Fixture.RootCount);
        NodeIndirectArgs.Store(CLUSTER_NODE_READ, 0u);
        NodeIndirectArgs.Store(CLUSTER_BATCH_READ, 0u);
        NodeIndirectArgs.Store(CLUSTER_NODE_OUTSTANDING, Fixture.RootCount);
        NodeIndirectArgs.Store(CLUSTER_BATCH_COUNT, Fixture.TotalClusters);
        if (Fixture.InitialPhase != 0u)
        {
            ClusterTraversalNode guard = { FIXTURE_CANARY, FIXTURE_CANARY, FIXTURE_CANARY, FIXTURE_CANARY };
            TraversalNodes[Fixture.MaxNodes] = guard;
            NodeIndirectArgs.Store(32u, FIXTURE_CANARY);
            TraversalReady[Fixture.MaxNodes] = FIXTURE_CANARY;
            ClusterBatchReady[(Fixture.ClusterCapacity + 63u) / 64u] = FIXTURE_CANARY;
            NodeConsumed[Fixture.MaxIds] = FIXTURE_CANARY;
            NodeAccepted[Fixture.MaxIds] = FIXTURE_CANARY;
            ClusterConsumed[Fixture.ClusterCapacity] = FIXTURE_CANARY;
            ClusterPayload[Fixture.ClusterCapacity] = FIXTURE_CANARY;
            Diagnostics[8] = FIXTURE_CANARY;
            DelaySink[Fixture.MaxIds] = FIXTURE_CANARY;
        }
    }
}

void RunFixture(uint thread, bool filters, bool views)
{
    uint ignored;
    if (thread == 0u) InterlockedAdd(Diagnostics[3], 1u, ignored);
    CullPersistentWork(thread, filters, views);
    if (thread == 0u)
    {
        uint outstanding;
        NodeIndirectArgs.InterlockedAdd(CLUSTER_NODE_OUTSTANDING, 0u, outstanding);
        if (outstanding != 0u) InterlockedAdd(Diagnostics[5], 1u, ignored);
        InterlockedAdd(Diagnostics[4], 1u, ignored);
    }
}
[shader("compute")][numthreads(64,1,1)]
void Fixture00(uint thread : SV_GroupThreadID) { RunFixture(thread, false, false); }
[shader("compute")][numthreads(64,1,1)]
void Fixture01(uint thread : SV_GroupThreadID) { RunFixture(thread, false, true); }
[shader("compute")][numthreads(64,1,1)]
void Fixture10(uint thread : SV_GroupThreadID) { RunFixture(thread, true, false); }
[shader("compute")][numthreads(64,1,1)]
void Fixture11(uint thread : SV_GroupThreadID) { RunFixture(thread, true, true); }
""";
}
