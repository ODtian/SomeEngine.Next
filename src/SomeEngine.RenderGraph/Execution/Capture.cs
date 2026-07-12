using System.Text;
using System.Text.Json;

namespace SomeEngine.RenderGraph;

/// <summary>A versioned portable topology and supported-command envelope for one compiled immediate graph.</summary>
public sealed class Capture
{
    public const int CurrentSchemaVersion = 3;

    private Capture(
        int schemaVersion,
        string canonicalSignature,
        ulong deviceSemanticGeneration,
        ulong compilerSemanticGeneration,
        CaptureResource[] resources,
        CapturePass[] passes,
        CaptureBatch[] batches)
    {
        SchemaVersion = schemaVersion;
        CanonicalSignature = canonicalSignature;
        DeviceSemanticGeneration = deviceSemanticGeneration;
        CompilerSemanticGeneration = compilerSemanticGeneration;
        Resources = resources;
        Passes = passes;
        Batches = batches;
    }

    public int SchemaVersion { get; }
    public string CanonicalSignature { get; }
    public ulong DeviceSemanticGeneration { get; }
    public ulong CompilerSemanticGeneration { get; }
    public IReadOnlyList<CaptureResource> Resources { get; }
    public IReadOnlyList<CapturePass> Passes { get; }
    public IReadOnlyList<CaptureBatch> Batches { get; }

    public string ToJson(bool indented)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = indented }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("canonicalSignature", CanonicalSignature);
            writer.WriteNumber("deviceSemanticGeneration", DeviceSemanticGeneration);
            writer.WriteNumber("compilerSemanticGeneration", CompilerSemanticGeneration);
            writer.WriteStartArray("resources");
            foreach (CaptureResource resource in Resources) WriteResource(writer, resource);
            writer.WriteEndArray();
            writer.WriteStartArray("passes");
            foreach (CapturePass pass in Passes) WritePass(writer, pass);
            writer.WriteEndArray();
            writer.WriteStartArray("batches");
            foreach (CaptureBatch batch in Batches) WriteBatch(writer, batch);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string ToDot()
    {
        StringBuilder value = new();
        value.AppendLine("digraph RenderGraph {");
        value.AppendLine("  rankdir=LR;");
        foreach (CapturePass pass in Passes)
        {
            value.Append("  p").Append(pass.Ordinal)
                .Append(" [label=\"").Append(EscapeDot(pass.Name)).Append("\\n")
                .Append(pass.Queue).Append("\",style=")
                .Append(pass.Active ? "solid" : "dashed").AppendLine("];");
        }
        foreach (CapturePass pass in Passes)
        foreach (int dependency in pass.Dependencies)
            value.Append("  p").Append(dependency).Append(" -> p").Append(pass.Ordinal).AppendLine(";");
        value.AppendLine("}");
        return value.ToString();
    }

    public static Capture FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        CaptureResource[] resources = root.GetProperty("resources").EnumerateArray().Select(ReadResource).ToArray();
        CapturePass[] passes = root.GetProperty("passes").EnumerateArray().Select(ReadPass).ToArray();
        CaptureBatch[] batches = root.GetProperty("batches").EnumerateArray().Select(ReadBatch).ToArray();
        return new Capture(
            root.GetProperty("schemaVersion").GetInt32(),
            root.GetProperty("canonicalSignature").GetString()!,
            root.GetProperty("deviceSemanticGeneration").GetUInt64(),
            root.GetProperty("compilerSemanticGeneration").GetUInt64(),
            resources,
            passes,
            batches);
    }

    internal static Capture Create(
        FrozenGraph frozen,
        CompiledGraph compiled,
        DeviceCompilationSnapshot compilation,
        IDevice device)
    {
        CaptureResource[] resources = frozen.Resources
            .Select((resource, ordinal) => CaptureResource.Create(resource, ordinal, device))
            .ToArray();
        CapturePass[] passes = CreatePasses(frozen, compiled);
        CaptureBatch[] batches = CreateBatches(compiled);
        GraphSignature signature = frozen.Canonical.Signature;
        string canonical = FormattableString.Invariant(
            $"{signature.Word0:X16}{signature.Word1:X16}{signature.Word2:X16}{signature.Word3:X16}");
        return new Capture(
            CurrentSchemaVersion,
            canonical,
            compilation.SemanticGeneration,
            CompilationCache.CompilerSemanticGeneration,
            resources,
            passes,
            batches);
    }

    private static CapturePass[] CreatePasses(FrozenGraph frozen, CompiledGraph compiled) =>
        frozen.Passes.Select((pass, ordinal) => new CapturePass(
            ordinal,
            pass.Name,
            compiled.Queues[ordinal],
            Array.BinarySearch(compiled.ActivePassOrdinals, ordinal) >= 0,
            compiled.Dependencies[ordinal].ToArray(),
            pass.Accesses.Select(CaptureAccess.Create).ToArray(),
            compiled.BeforeBarriers[ordinal].Select(CaptureBarrier.Create).ToArray(),
            compiled.AfterBarriers[ordinal].Select(CaptureBarrier.Create).ToArray(),
            new List<CaptureCommand>())).ToArray();

    private static CaptureBatch[] CreateBatches(CompiledGraph compiled) =>
        compiled.ExecutionBatches.Select((batch, ordinal) => new CaptureBatch(
            ordinal,
            batch.Queue,
            batch.Dependencies.ToArray(),
            CaptureBatchStep.Create(compiled, batch))).ToArray();

    internal void RecordCommand(int pass, in CaptureCommand command)
    {
        if ((uint)pass >= (uint)Passes.Count || Passes[pass].Commands is not List<CaptureCommand> commands)
            throw new InvalidOperationException("Capture command storage is unavailable for this pass.");
        lock (commands) commands.Add(command);
    }

    private static void WriteResource(Utf8JsonWriter writer, in CaptureResource resource)
    {
        writer.WriteStartObject();
        writer.WriteNumber("ordinal", resource.Ordinal);
        writer.WriteString("kind", resource.Kind.ToString());
        writer.WriteString("lifetime", resource.Lifetime.ToString());
        writer.WriteNumber("historyOffset", resource.HistoryOffset);
        writer.WriteBoolean("exported", resource.Exported);
        writer.WriteString("initialState", resource.InitialState.ToString());
        writer.WriteBase64String("initialData", resource.InitialData.Span);
        if (resource.Buffer is CaptureBufferDescription buffer)
        {
            writer.WriteStartObject("buffer");
            writer.WriteNumber("size", buffer.Size);
            writer.WriteString("usage", buffer.Usage.ToString());
            writer.WriteString("memoryType", buffer.MemoryType.ToString());
            if (buffer.Name is null) writer.WriteNull("name"); else writer.WriteString("name", buffer.Name);
            writer.WriteEndObject();
        }
        else
        {
            CaptureTextureDescription texture = resource.Texture!.Value;
            writer.WriteStartObject("texture");
            writer.WriteNumber("width", texture.Width);
            writer.WriteNumber("height", texture.Height);
            writer.WriteNumber("depth", texture.Depth);
            writer.WriteNumber("mipLevels", texture.MipLevels);
            writer.WriteNumber("arrayLayers", texture.ArrayLayers);
            writer.WriteNumber("sampleCount", texture.SampleCount);
            writer.WriteString("format", texture.Format.ToString());
            writer.WriteString("usage", texture.Usage.ToString());
            writer.WriteString("dimension", texture.Dimension.ToString());
            writer.WriteBoolean("cubeCompatible", texture.CubeCompatible);
            if (texture.Name is null) writer.WriteNull("name"); else writer.WriteString("name", texture.Name);
            writer.WriteStartArray("allowedViewFormats");
            foreach (Format format in texture.AllowedViewFormats) writer.WriteStringValue(format.ToString());
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WritePass(Utf8JsonWriter writer, in CapturePass pass)
    {
        writer.WriteStartObject();
        writer.WriteNumber("ordinal", pass.Ordinal);
        writer.WriteString("name", pass.Name);
        writer.WriteString("queue", pass.Queue.ToString());
        writer.WriteBoolean("active", pass.Active);
        WriteNumbers(writer, "dependencies", pass.Dependencies);
        writer.WriteStartArray("accesses");
        foreach (CaptureAccess access in pass.Accesses)
        {
            writer.WriteStartObject();
            writer.WriteNumber("resource", access.Resource);
            writer.WriteString("kind", access.Kind);
            writer.WriteString("effect", access.Effect);
            writer.WriteString("use", access.Use);
            writer.WriteString("range", access.Range);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteBarriers(writer, "beforeBarriers", pass.BeforeBarriers);
        WriteBarriers(writer, "afterBarriers", pass.AfterBarriers);
        writer.WriteStartArray("commands");
        foreach (CaptureCommand command in pass.Commands)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", command.Kind.ToString());
            writer.WriteNumber("source", command.Source);
            writer.WriteNumber("sourceOffset", command.SourceOffset);
            writer.WriteNumber("destination", command.Destination);
            writer.WriteNumber("destinationOffset", command.DestinationOffset);
            writer.WriteNumber("size", command.Size);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteBatch(Utf8JsonWriter writer, in CaptureBatch batch)
    {
        writer.WriteStartObject();
        writer.WriteNumber("ordinal", batch.Ordinal);
        writer.WriteString("queue", batch.Queue.ToString());
        WriteNumbers(writer, "dependencies", batch.Dependencies);
        writer.WriteStartArray("steps");
        foreach (CaptureBatchStep step in batch.Steps)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", step.Kind.ToString());
            writer.WriteNumber("pass", step.Pass);
            WriteBarriers(writer, "barriers", step.Barriers);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteBarriers(Utf8JsonWriter writer, string name, IEnumerable<CaptureBarrier> barriers)
    {
        writer.WriteStartArray(name);
        foreach (CaptureBarrier barrier in barriers)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", barrier.Kind.ToString());
            writer.WriteNumber("resource", barrier.Resource);
            writer.WriteString("before", barrier.Before.ToString());
            writer.WriteString("after", barrier.After.ToString());
            writer.WriteNumber("aliasingBefore", barrier.AliasingBefore);
            writer.WriteNumber("firstMip", barrier.TextureRange.FirstMip);
            writer.WriteNumber("mipCount", barrier.TextureRange.MipCount);
            writer.WriteNumber("firstLayer", barrier.TextureRange.FirstLayer);
            writer.WriteNumber("layerCount", barrier.TextureRange.LayerCount);
            writer.WriteString("aspect", barrier.TextureRange.Aspect.ToString());
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static CaptureResource ReadResource(JsonElement value)
    {
        CaptureResourceKind kind = ParseEnum<CaptureResourceKind>(value.GetProperty("kind"));
        CaptureBufferDescription? buffer = kind == CaptureResourceKind.Buffer ? ReadBuffer(value) : null;
        CaptureTextureDescription? texture = kind == CaptureResourceKind.Texture ? ReadTexture(value) : null;
        return new CaptureResource(
            value.GetProperty("ordinal").GetInt32(),
            kind,
            ParseEnum<ResourceLifetime>(value.GetProperty("lifetime")),
            value.GetProperty("historyOffset").GetInt32(),
            value.GetProperty("exported").GetBoolean(),
            ParseEnum<ResourceState>(value.GetProperty("initialState")),
            buffer,
            texture,
            value.GetProperty("initialData").GetBytesFromBase64());
    }

    private static CaptureBufferDescription ReadBuffer(JsonElement value)
    {
        JsonElement description = value.GetProperty("buffer");
        return new CaptureBufferDescription(
            description.GetProperty("size").GetUInt64(),
            ParseEnum<BufferUsage>(description.GetProperty("usage")),
            ParseEnum<MemoryType>(description.GetProperty("memoryType")),
            OptionalString(description.GetProperty("name")));
    }

    private static CaptureTextureDescription ReadTexture(JsonElement value)
    {
        JsonElement description = value.GetProperty("texture");
        return new CaptureTextureDescription(
            description.GetProperty("width").GetInt32(),
            description.GetProperty("height").GetInt32(),
            description.GetProperty("depth").GetInt32(),
            description.GetProperty("mipLevels").GetInt32(),
            description.GetProperty("arrayLayers").GetInt32(),
            description.GetProperty("sampleCount").GetInt32(),
            ParseEnum<Format>(description.GetProperty("format")),
            ParseEnum<TextureUsage>(description.GetProperty("usage")),
            ParseEnum<TextureDimension>(description.GetProperty("dimension")),
            description.GetProperty("cubeCompatible").GetBoolean(),
            OptionalString(description.GetProperty("name")),
            description.GetProperty("allowedViewFormats").EnumerateArray().Select(ParseEnum<Format>).ToArray());
    }

    private static CapturePass ReadPass(JsonElement value) => new(
        value.GetProperty("ordinal").GetInt32(),
        value.GetProperty("name").GetString()!,
        ParseEnum<QueueType>(value.GetProperty("queue")),
        value.GetProperty("active").GetBoolean(),
        ReadNumbers(value.GetProperty("dependencies")),
        value.GetProperty("accesses").EnumerateArray().Select(static access => new CaptureAccess(
            access.GetProperty("resource").GetInt32(),
            access.GetProperty("kind").GetString()!,
            access.GetProperty("effect").GetString()!,
            access.GetProperty("use").GetString()!,
            access.GetProperty("range").GetString()!)).ToArray(),
        ReadBarriers(value.GetProperty("beforeBarriers")),
        ReadBarriers(value.GetProperty("afterBarriers")),
        value.GetProperty("commands").EnumerateArray().Select(static command => new CaptureCommand(
            ParseEnum<CaptureCommandKind>(command.GetProperty("kind")),
            command.GetProperty("source").GetInt32(),
            command.GetProperty("sourceOffset").GetUInt64(),
            command.GetProperty("destination").GetInt32(),
            command.GetProperty("destinationOffset").GetUInt64(),
            command.GetProperty("size").GetUInt64())).ToList());

    private static CaptureBatch ReadBatch(JsonElement value) => new(
        value.GetProperty("ordinal").GetInt32(),
        ParseEnum<QueueType>(value.GetProperty("queue")),
        ReadNumbers(value.GetProperty("dependencies")),
        value.GetProperty("steps").EnumerateArray().Select(static step => new CaptureBatchStep(
            ParseEnum<CaptureBatchStepKind>(step.GetProperty("kind")),
            step.GetProperty("pass").GetInt32(),
            ReadBarriers(step.GetProperty("barriers")))).ToArray());

    private static CaptureBarrier[] ReadBarriers(JsonElement value) => value.EnumerateArray().Select(static barrier => new CaptureBarrier(
        ParseEnum<BarrierKind>(barrier.GetProperty("kind")),
        barrier.GetProperty("resource").GetInt32(),
        ParseEnum<ResourceState>(barrier.GetProperty("before")),
        ParseEnum<ResourceState>(barrier.GetProperty("after")),
        new TextureSubresourceRange(
            barrier.GetProperty("firstMip").GetInt32(),
            barrier.GetProperty("mipCount").GetInt32(),
            barrier.GetProperty("firstLayer").GetInt32(),
            barrier.GetProperty("layerCount").GetInt32(),
            ParseEnum<TextureAspect>(barrier.GetProperty("aspect"))),
        barrier.GetProperty("aliasingBefore").GetInt32())).ToArray();

    private static T ParseEnum<T>(JsonElement value) where T : struct, Enum
    {
        string text = value.GetString() ?? throw new JsonException("An enum value cannot be null.");
        if (!Enum.TryParse(text, ignoreCase: false, out T result))
            throw new JsonException($"'{text}' is not a valid {typeof(T).Name} value.");
        return result;
    }

    private static int[] ReadNumbers(JsonElement value) =>
        value.EnumerateArray().Select(static item => item.GetInt32()).ToArray();

    private static string? OptionalString(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static void WriteNumbers(Utf8JsonWriter writer, string name, IEnumerable<int> values)
    {
        writer.WriteStartArray(name);
        foreach (int value in values) writer.WriteNumberValue(value);
        writer.WriteEndArray();
    }

    private static string EscapeDot(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}

public readonly record struct CaptureBufferDescription(
    ulong Size,
    BufferUsage Usage,
    MemoryType MemoryType,
    string? Name)
{
    internal BufferDesc ToDescription() => new(Size, Usage, Name);
}

public readonly record struct CaptureTextureDescription(
    int Width,
    int Height,
    int Depth,
    int MipLevels,
    int ArrayLayers,
    int SampleCount,
    Format Format,
    TextureUsage Usage,
    TextureDimension Dimension,
    bool CubeCompatible,
    string? Name,
    IReadOnlyList<Format> AllowedViewFormats)
{
    internal TextureDesc ToDescription() => new(
        Width,
        Height,
        Format,
        Usage,
        Depth,
        MipLevels,
        ArrayLayers,
        SampleCount,
        Name,
        Dimension,
        CubeCompatible,
        AllowedViewFormats);
}

public enum CaptureResourceKind : byte
{
    Buffer,
    Texture,
}

public readonly record struct CaptureResource(
    int Ordinal,
    CaptureResourceKind Kind,
    ResourceLifetime Lifetime,
    int HistoryOffset,
    bool Exported,
    ResourceState InitialState,
    CaptureBufferDescription? Buffer,
    CaptureTextureDescription? Texture,
    ReadOnlyMemory<byte> InitialData)
{
    internal static CaptureResource Create(FrozenResource resource, int ordinal, IDevice device)
    {
        ResourceState initialState = ResolveInitialState(resource);
        if (resource.Kind == ResourceNodeKind.Buffer)
        {
            MemoryType memoryType = resource.IsImported
                ? resource.ImportedBuffer.Metadata.MemoryType
                : MemoryType.DeviceLocal;
            return new CaptureResource(
                ordinal,
                CaptureResourceKind.Buffer,
                resource.Lifetime,
                resource.HistoryOffset,
                resource.Exported,
                initialState,
                new CaptureBufferDescription(
                    resource.BufferDesc.Size,
                    resource.BufferDesc.Usage,
                    memoryType,
                    resource.BufferDesc.Name),
                null,
                SnapshotInitialData(resource, memoryType, device));
        }

        TextureDesc texture = resource.TextureDesc;
        return new CaptureResource(
            ordinal,
            CaptureResourceKind.Texture,
            resource.Lifetime,
            resource.HistoryOffset,
            resource.Exported,
            initialState,
            null,
            new CaptureTextureDescription(
                texture.Width,
                texture.Height,
                texture.Depth,
                texture.MipLevels,
                texture.ArrayLayers,
                texture.SampleCount,
                texture.Format,
                texture.Usage,
                texture.Dimension,
                texture.CubeCompatible,
                texture.Name,
                texture.AllowedViewFormats.ToArray()),
            ReadOnlyMemory<byte>.Empty);
    }

    private static ReadOnlyMemory<byte> SnapshotInitialData(
        in FrozenResource resource,
        MemoryType memoryType,
        IDevice device)
    {
        if (!resource.IsImported || memoryType != MemoryType.Upload || resource.BufferDesc.Size > int.MaxValue)
            return ReadOnlyMemory<byte>.Empty;
        using BufferMapping mapping = device.MapBuffer(
            resource.ImportedBuffer.Handle,
            BufferMapMode.Write,
            new BufferRange(0, resource.BufferDesc.Size));
        return mapping.Span.ToArray();
    }

    private static ResourceState ResolveInitialState(in FrozenResource resource)
    {
        if (!resource.IsImported) return ResourceState.Common;
        if (resource.Kind == ResourceNodeKind.Buffer)
            return resource.ImportedBuffer.InitialStateOverride ?? Map(resource.ImportedBuffer.InitialUse);
        return resource.ImportedTexture.InitialStateOverride ?? Map(resource.ImportedTexture.InitialUse);
    }

    private static ResourceState Map(BufferUse use) => use switch
    {
        BufferUse.CopySource => ResourceState.CopySource,
        BufferUse.CopyDestination => ResourceState.CopyDestination,
        BufferUse.ShaderRead => ResourceState.ShaderResource,
        BufferUse.ShaderWrite => ResourceState.UnorderedAccess,
        BufferUse.VertexOrConstant => ResourceState.VertexOrConstantBuffer,
        BufferUse.Index => ResourceState.IndexBuffer,
        BufferUse.Indirect => ResourceState.IndirectArgument,
        _ => throw new ArgumentOutOfRangeException(nameof(use)),
    };

    private static ResourceState Map(TextureUse use) => use switch
    {
        TextureUse.CopySource => ResourceState.CopySource,
        TextureUse.CopyDestination => ResourceState.CopyDestination,
        TextureUse.ResolveSource => ResourceState.ResolveSource,
        TextureUse.ResolveDestination => ResourceState.ResolveDestination,
        TextureUse.Sampled => ResourceState.ShaderResource,
        TextureUse.Storage => ResourceState.UnorderedAccess,
        TextureUse.ColorAttachment => ResourceState.RenderTarget,
        TextureUse.DepthRead => ResourceState.DepthRead,
        TextureUse.DepthWrite => ResourceState.DepthWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(use)),
    };
}

public readonly record struct CaptureAccess(int Resource, string Kind, string Effect, string Use, string Range)
{
    internal static CaptureAccess Create(FrozenAccess access)
    {
        string range = access.Kind == ResourceNodeKind.Buffer
            ? FormattableString.Invariant($"{access.BufferRange.Offset}+{access.BufferRange.Size}")
            : FormattableString.Invariant(
                $"m{access.TextureRange.FirstMip}+{access.TextureRange.MipCount}/l{access.TextureRange.FirstLayer}+{access.TextureRange.LayerCount}/{access.TextureRange.Aspect}");
        string use = access.Kind == ResourceNodeKind.Buffer ? access.BufferUse.ToString() : access.TextureUse.ToString();
        return new CaptureAccess(access.Resource, access.Kind.ToString(), access.Effect.ToString(), use, range);
    }
}

public readonly record struct CaptureBarrier(
    BarrierKind Kind,
    int Resource,
    ResourceState Before,
    ResourceState After,
    TextureSubresourceRange TextureRange,
    int AliasingBefore)
{
    internal static CaptureBarrier Create(BarrierTemplate barrier) => new(
        barrier.Kind,
        barrier.Resource,
        barrier.Before,
        barrier.After,
        barrier.TextureRange,
        barrier.AliasingBefore);
}

public enum CaptureCommandKind : byte
{
    CopyBuffer,
}

public readonly record struct CaptureCommand(
    CaptureCommandKind Kind,
    int Source,
    ulong SourceOffset,
    int Destination,
    ulong DestinationOffset,
    ulong Size);

public readonly record struct CapturePass(
    int Ordinal,
    string Name,
    QueueType Queue,
    bool Active,
    IReadOnlyList<int> Dependencies,
    IReadOnlyList<CaptureAccess> Accesses,
    IReadOnlyList<CaptureBarrier> BeforeBarriers,
    IReadOnlyList<CaptureBarrier> AfterBarriers,
    IReadOnlyList<CaptureCommand> Commands);

public enum CaptureBatchStepKind : byte
{
    PassEnvelope,
    Barriers,
}

public readonly record struct CaptureBatchStep(
    CaptureBatchStepKind Kind,
    int Pass,
    IReadOnlyList<CaptureBarrier> Barriers)
{
    internal static CaptureBatchStep[] Create(CompiledGraph compiled, in CompiledExecutionBatch batch)
    {
        List<CaptureBatchStep> result = [];
        foreach (int ordinal in batch.RecordUnits)
        {
            CompiledRecordUnit unit = compiled.RecordUnits[ordinal];
            if (unit.Kind is CompiledRecordUnitKind.Standalone or CompiledRecordUnitKind.RasterScope)
            {
                foreach (int pass in unit.LogicalPassOrdinals)
                    result.Add(new CaptureBatchStep(CaptureBatchStepKind.PassEnvelope, pass, []));
                continue;
            }
            CaptureBarrier[] barriers = unit.Kind == CompiledRecordUnitKind.AliasAcquire
                ? unit.AliasAcquires.Select(static value => new CaptureBarrier(
                    BarrierKind.Aliasing,
                    value.AfterResource,
                    ResourceState.Common,
                    ResourceState.Common,
                    default,
                    value.BeforeResource)).ToArray()
                : unit.InternalBarriers.Select(CaptureBarrier.Create).ToArray();
            result.Add(new CaptureBatchStep(CaptureBatchStepKind.Barriers, -1, barriers));
        }
        return result.ToArray();
    }
}

public readonly record struct CaptureBatch(
    int Ordinal,
    QueueType Queue,
    IReadOnlyList<int> Dependencies,
    IReadOnlyList<CaptureBatchStep> Steps);

public readonly record struct ReplayResult(
    string CanonicalSignature,
    IReadOnlyList<string> ActivePasses,
    int ResourceCount,
    int ExecutedBatchCount,
    IReadOnlyList<GpuCompletion> Completions,
    IReadOnlyDictionary<int, byte[]> BufferOutputs);

/// <summary>Executes a capture's portable resource, barrier, marker, batch, and completion envelope.</summary>
public static partial class ReplayExecutor
{
    public static ReplayResult Execute(Capture capture, IDevice device)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(device);
        CaptureValidator.Validate(capture, device);

        ResourceHandle[] resources = new ResourceHandle[capture.Resources.Count];
        GpuCompletion[] batchCompletions = new GpuCompletion[capture.Batches.Count];
        List<GpuCompletion> allCompletions = [];
        try
        {
            CreateResources(capture, device, resources);
            InitializeResources(capture, device, resources, allCompletions);
            ExecuteBatches(capture, device, resources, batchCompletions, allCompletions);
            WaitForBatches(device, batchCompletions);
            return CreateResult(capture, device, resources, batchCompletions);
        }
        finally
        {
            DestroyResources(device, resources, allCompletions);
        }
    }

    private static void CreateResources(Capture capture, IDevice device, ResourceHandle[] resources)
    {
        for (int ordinal = 0; ordinal < capture.Resources.Count; ordinal++)
        {
            CaptureResource resource = capture.Resources[ordinal];
            resources[ordinal] = resource.Kind == CaptureResourceKind.Buffer
                ? device.CreateBuffer(resource.Buffer!.Value.ToDescription(), resource.Buffer.Value.MemoryType).Resource
                : device.CreateTexture(resource.Texture!.Value.ToDescription()).Resource;
            WriteInitialData(resource, resources[ordinal], device);
        }
    }

    private static void WriteInitialData(in CaptureResource resource, ResourceHandle created, IDevice device)
    {
        if (resource.InitialData.IsEmpty) return;
        device.WriteBuffer(
            new BufferHandle(created.Domain, created.Slot, created.Generation),
            0,
            resource.InitialData.Span);
    }

    private static void InitializeResources(
        Capture capture,
        IDevice device,
        ResourceHandle[] resources,
        List<GpuCompletion> completions)
    {
        ResourceBarrier[] initialization = BuildInitialization(capture, resources);
        if (initialization.Length == 0) return;
        GpuCompletion completion = Submit(
            device,
            QueueType.Graphics,
            "capture-replay-initialization",
            [],
            commands => commands.Barriers(initialization));
        completions.Add(completion);
        if (!device.Wait(completion, Timeout.InfiniteTimeSpan))
            throw new TimeoutException("Capture replay initialization did not complete.");
    }

    private static void ExecuteBatches(
        Capture capture,
        IDevice device,
        ResourceHandle[] resources,
        GpuCompletion[] batchCompletions,
        List<GpuCompletion> allCompletions)
    {
        foreach (CaptureBatch batch in capture.Batches)
        {
            GpuCompletion[] waits = BuildWaits(batch, batchCompletions);
            GpuCompletion completion = Submit(
                device,
                batch.Queue,
                $"capture-replay-batch-{batch.Ordinal}",
                waits,
                commands => RecordBatch(commands, capture, batch, resources));
            batchCompletions[batch.Ordinal] = completion;
            allCompletions.Add(completion);
        }
    }

    private static GpuCompletion[] BuildWaits(CaptureBatch batch, GpuCompletion[] batchCompletions) =>
        batch.Dependencies
            .Select(dependency => batchCompletions[dependency])
            .Where(completion => completion.IsValid && completion.Queue != batch.Queue)
            .GroupBy(static completion => completion.Queue)
            .Select(static values => values.MaxBy(static completion => completion.Value))
            .ToArray();

    private static void WaitForBatches(IDevice device, IEnumerable<GpuCompletion> completions)
    {
        foreach (GpuCompletion completion in completions)
            if (completion.IsValid && !device.Wait(completion, Timeout.InfiniteTimeSpan))
                throw new TimeoutException("Capture replay did not complete.");
    }

    private static ReplayResult CreateResult(
        Capture capture,
        IDevice device,
        ResourceHandle[] resources,
        GpuCompletion[] completions) =>
        new(
            capture.CanonicalSignature,
            capture.Passes.Where(static pass => pass.Active).Select(static pass => pass.Name).ToArray(),
            capture.Resources.Count,
            capture.Batches.Count,
            completions.ToArray(),
            SnapshotReadbackBuffers(capture, device, resources));

    private static void DestroyResources(
        IDevice device,
        ResourceHandle[] resources,
        IEnumerable<GpuCompletion> completions)
    {
        foreach (GpuCompletion completion in completions)
            if (completion.IsValid) _ = device.Wait(completion, Timeout.InfiniteTimeSpan);
        for (int ordinal = resources.Length - 1; ordinal >= 0; ordinal--)
            DestroyResource(device, resources[ordinal]);
        device.CollectGarbage();
    }

    private static void DestroyResource(IDevice device, ResourceHandle resource)
    {
        if (!resource.IsValid) return;
        if (resource.Kind == ResourceKind.Buffer)
            device.DestroyBuffer(new BufferHandle(resource.Domain, resource.Slot, resource.Generation));
        else
            device.DestroyTexture(new TextureHandle(resource.Domain, resource.Slot, resource.Generation));
    }

}

internal static class CaptureValidator
{
    internal static void Validate(Capture capture, IDevice device)
    {
        ValidateHeader(capture, device);
        ValidateResources(capture);
        HashSet<int> activePasses = ValidatePasses(capture);
        HashSet<int> enveloped = ValidateBatches(capture, activePasses);
        if (!activePasses.SetEquals(enveloped))
            throw new InvalidOperationException("Capture batches do not execute every active pass exactly once.");
    }

    private static void ValidateHeader(Capture capture, IDevice device)
    {
        if (capture.SchemaVersion != Capture.CurrentSchemaVersion)
            throw new NotSupportedException($"Capture schema {capture.SchemaVersion} is not supported.");
        if (capture.DeviceSemanticGeneration != device.Compilation.SemanticGeneration)
            throw new InvalidOperationException("Capture device-compilation semantics do not match the replay device.");
        if (capture.CompilerSemanticGeneration != CompilationCache.CompilerSemanticGeneration)
            throw new InvalidOperationException("Capture compiler semantics do not match this RenderGraph build.");
        if (string.IsNullOrWhiteSpace(capture.CanonicalSignature))
            throw new InvalidOperationException("Capture canonical identity is missing.");
    }

    private static void ValidateResources(Capture capture)
    {
        for (int ordinal = 0; ordinal < capture.Resources.Count; ordinal++)
            ValidateResource(capture.Resources[ordinal], ordinal);
    }

    private static void ValidateResource(in CaptureResource resource, int ordinal)
    {
        if (resource.Ordinal != ordinal || !Enum.IsDefined(resource.Kind) || !Enum.IsDefined(resource.Lifetime) ||
            !Enum.IsDefined(resource.InitialState) || resource.HistoryOffset < 0)
            throw new InvalidOperationException("Capture resource metadata is not canonical.");
        if (resource.Kind == CaptureResourceKind.Buffer) ValidateBuffer(resource);
        else ValidateTexture(resource);
    }

    private static void ValidateBuffer(in CaptureResource resource)
    {
        if (resource.Buffer is null || resource.Texture is not null || !Enum.IsDefined(resource.Buffer.Value.MemoryType))
            throw new InvalidOperationException("Capture buffer description is invalid.");
        resource.Buffer.Value.ToDescription().Validate();
        if (!resource.InitialData.IsEmpty &&
            (resource.Buffer.Value.MemoryType != MemoryType.Upload ||
             (ulong)resource.InitialData.Length > resource.Buffer.Value.Size))
            throw new InvalidOperationException("Capture buffer initial data is incompatible with its resource contract.");
    }

    private static void ValidateTexture(in CaptureResource resource)
    {
        if (resource.Texture is null || resource.Buffer is not null || !resource.InitialData.IsEmpty)
            throw new InvalidOperationException("Capture texture description is invalid.");
        resource.Texture.Value.ToDescription().Validate();
    }

    private static HashSet<int> ValidatePasses(Capture capture)
    {
        HashSet<int> activePasses = [];
        for (int ordinal = 0; ordinal < capture.Passes.Count; ordinal++)
        {
            CapturePass pass = capture.Passes[ordinal];
            ValidatePass(capture, pass, ordinal);
            if (pass.Active) activePasses.Add(ordinal);
        }
        return activePasses;
    }

    private static void ValidatePass(Capture capture, in CapturePass pass, int ordinal)
    {
        if (pass.Ordinal != ordinal || string.IsNullOrWhiteSpace(pass.Name) || !Enum.IsDefined(pass.Queue))
            throw new InvalidOperationException("Capture pass metadata is not canonical.");
        foreach (int dependency in pass.Dependencies)
            if (dependency < 0 || dependency >= ordinal)
                throw new InvalidOperationException("Capture pass dependencies are not a forward topological order.");
        foreach (CaptureAccess access in pass.Accesses)
            if (access.Resource < 0 || access.Resource >= capture.Resources.Count)
                throw new InvalidOperationException("Capture access references an invalid resource.");
        ValidateBarriers(capture, pass.BeforeBarriers);
        ValidateBarriers(capture, pass.AfterBarriers);
        foreach (CaptureCommand command in pass.Commands) ValidateCommand(capture, pass, command);
    }

    private static HashSet<int> ValidateBatches(Capture capture, HashSet<int> activePasses)
    {
        HashSet<int> enveloped = [];
        for (int ordinal = 0; ordinal < capture.Batches.Count; ordinal++)
        {
            CaptureBatch batch = capture.Batches[ordinal];
            ValidateBatch(capture, batch, ordinal, activePasses, enveloped);
        }
        return enveloped;
    }

    private static void ValidateBatch(
        Capture capture,
        in CaptureBatch batch,
        int ordinal,
        HashSet<int> activePasses,
        HashSet<int> enveloped)
    {
        if (batch.Ordinal != ordinal || !Enum.IsDefined(batch.Queue))
            throw new InvalidOperationException("Capture batch metadata is not canonical.");
        foreach (int dependency in batch.Dependencies)
            if (dependency < 0 || dependency >= ordinal)
                throw new InvalidOperationException("Capture batch dependencies are not a forward topological order.");
        foreach (CaptureBatchStep step in batch.Steps)
            ValidateBatchStep(capture, batch, step, activePasses, enveloped);
    }

    private static void ValidateBatchStep(
        Capture capture,
        in CaptureBatch batch,
        in CaptureBatchStep step,
        HashSet<int> activePasses,
        HashSet<int> enveloped)
    {
        if (!Enum.IsDefined(step.Kind)) throw new InvalidOperationException("Capture batch step kind is invalid.");
        if (step.Kind == CaptureBatchStepKind.PassEnvelope)
        {
            bool valid = activePasses.Contains(step.Pass) &&
                capture.Passes[step.Pass].Queue == batch.Queue &&
                enveloped.Add(step.Pass) &&
                step.Barriers.Count == 0;
            if (!valid)
                throw new InvalidOperationException("Capture pass envelope is inconsistent with compiled batches.");
            return;
        }
        if (step.Pass != -1 || step.Barriers.Count == 0)
            throw new InvalidOperationException("Capture barrier step is malformed.");
        ValidateBarriers(capture, step.Barriers);
    }

    private static void ValidateBarriers(Capture capture, IEnumerable<CaptureBarrier> barriers)
    {
        foreach (CaptureBarrier barrier in barriers)
        {
            if (!Enum.IsDefined(barrier.Kind) || !Enum.IsDefined(barrier.Before) || !Enum.IsDefined(barrier.After) ||
                barrier.Resource < 0 || barrier.Resource >= capture.Resources.Count)
                throw new InvalidOperationException("Capture barrier metadata is invalid.");
            if (barrier.Kind == BarrierKind.Aliasing)
            {
                if (barrier.AliasingBefore < 0 || barrier.AliasingBefore >= capture.Resources.Count)
                    throw new InvalidOperationException("Capture aliasing barrier references an invalid resource.");
            }
            else if (barrier.AliasingBefore != -1)
            {
                throw new InvalidOperationException("A non-aliasing capture barrier has an alias source.");
            }
        }
    }

    private static void ValidateCommand(Capture capture, in CapturePass pass, in CaptureCommand command)
    {
        ValidateCommandMetadata(capture, command);
        (CaptureBufferDescription source, CaptureBufferDescription destination) =
            GetCopyDescriptions(capture, command);
        ValidateCopyRange(command, source, destination);
        ValidateCopyAccess(pass, command);
    }

    private static void ValidateCommandMetadata(Capture capture, in CaptureCommand command)
    {
        if (!Enum.IsDefined(command.Kind) || command.Kind != CaptureCommandKind.CopyBuffer || command.Size == 0 ||
            command.Source < 0 || command.Source >= capture.Resources.Count ||
            command.Destination < 0 || command.Destination >= capture.Resources.Count)
            throw new InvalidOperationException("Capture command metadata is invalid.");
    }

    private static (CaptureBufferDescription Source, CaptureBufferDescription Destination) GetCopyDescriptions(
        Capture capture,
        in CaptureCommand command)
    {
        CaptureBufferDescription? source = capture.Resources[command.Source].Buffer;
        CaptureBufferDescription? destination = capture.Resources[command.Destination].Buffer;
        if (source is null || destination is null)
            throw new InvalidOperationException("Capture copy command exceeds its resource contract.");
        return (source.Value, destination.Value);
    }

    private static void ValidateCopyRange(
        in CaptureCommand command,
        in CaptureBufferDescription source,
        in CaptureBufferDescription destination)
    {
        if (command.SourceOffset > source.Size || command.Size > source.Size - command.SourceOffset ||
            command.DestinationOffset > destination.Size || command.Size > destination.Size - command.DestinationOffset)
            throw new InvalidOperationException("Capture copy command exceeds its resource contract.");
    }

    private static void ValidateCopyAccess(in CapturePass pass, in CaptureCommand command)
    {
        if (!HasBufferAccess(
                pass,
                command.Source,
                nameof(BufferUse.CopySource),
                ResourceEffect.Read,
                command.SourceOffset,
                command.Size) ||
            !HasBufferAccess(
                pass,
                command.Destination,
                nameof(BufferUse.CopyDestination),
                ResourceEffect.Write,
                command.DestinationOffset,
                command.Size))
        {
            throw new InvalidOperationException("Capture command exceeds its pass access contract.");
        }
    }

    private static bool HasBufferAccess(
        in CapturePass pass,
        int resource,
        string use,
        ResourceEffect requiredEffect,
        ulong offset,
        ulong size)
    {
        ulong end = checked(offset + size);
        foreach (CaptureAccess access in pass.Accesses)
        {
            if (access.Resource != resource || access.Kind != nameof(ResourceNodeKind.Buffer) || access.Use != use ||
                !Enum.TryParse(access.Effect, out ResourceEffect effect) || !Covers(effect, requiredEffect) ||
                !TryParseBufferRange(access.Range, out ulong accessOffset, out ulong accessSize))
            {
                continue;
            }
            ulong accessEnd = checked(accessOffset + accessSize);
            if (offset >= accessOffset && end <= accessEnd) return true;
        }
        return false;
    }

    private static bool TryParseBufferRange(string value, out ulong offset, out ulong size)
    {
        offset = 0;
        size = 0;
        int separator = value.IndexOf('+');
        return separator > 0 &&
            ulong.TryParse(value.AsSpan(0, separator), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out offset) &&
            ulong.TryParse(value.AsSpan(separator + 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out size);
    }

    private static bool Covers(ResourceEffect actual, ResourceEffect required) => required switch
    {
        ResourceEffect.Read => actual is ResourceEffect.Read or ResourceEffect.ReadWrite,
        ResourceEffect.Write => actual is ResourceEffect.Write or ResourceEffect.ReadWrite,
        _ => false,
    };

}

public static partial class ReplayExecutor
{

    private static ResourceBarrier[] BuildInitialization(Capture capture, ResourceHandle[] resources)
    {
        List<ResourceBarrier> result = [];
        foreach (CaptureResource resource in capture.Resources)
        {
            if (resource.InitialState == ResourceState.Common) continue;
            if (resource.Buffer is CaptureBufferDescription buffer && buffer.MemoryType != MemoryType.DeviceLocal) continue;
            TextureSubresourceRange range = resource.Texture is CaptureTextureDescription texture
                ? WholeRange(texture)
                : default;
            result.Add(ResourceBarrier.Transition(
                resources[resource.Ordinal],
                ResourceState.Common,
                resource.InitialState,
                range));
        }
        return result.ToArray();
    }

    private static TextureSubresourceRange WholeRange(in CaptureTextureDescription texture)
    {
        TextureAspect aspect = texture.Format switch
        {
            Format.D24UNormS8UInt => TextureAspect.Depth | TextureAspect.Stencil,
            Format.D32Float => TextureAspect.Depth,
            _ => TextureAspect.Color,
        };
        return new TextureSubresourceRange(0, texture.MipLevels, 0, texture.ArrayLayers, aspect);
    }

    private static void RecordBatch(
        ICommandContext commands,
        Capture capture,
        in CaptureBatch batch,
        ResourceHandle[] resources)
    {
        foreach (CaptureBatchStep step in batch.Steps)
        {
            if (step.Kind == CaptureBatchStepKind.Barriers)
            {
                commands.Barriers(step.Barriers.Select(barrier => Materialize(barrier, resources)).ToArray());
                continue;
            }
            CapturePass pass = capture.Passes[step.Pass];
            if (pass.BeforeBarriers.Count != 0)
                commands.Barriers(pass.BeforeBarriers.Select(barrier => Materialize(barrier, resources)).ToArray());
            commands.PushDebugGroup(pass.Name);
            foreach (CaptureCommand command in pass.Commands)
                RecordCommand(commands, command, resources);
            commands.PopDebugGroup();
            if (pass.AfterBarriers.Count != 0)
                commands.Barriers(pass.AfterBarriers.Select(barrier => Materialize(barrier, resources)).ToArray());
        }
    }

    private static ResourceBarrier Materialize(in CaptureBarrier barrier, ResourceHandle[] resources) => barrier.Kind switch
    {
        BarrierKind.Transition => ResourceBarrier.Transition(
            resources[barrier.Resource],
            barrier.Before,
            barrier.After,
            barrier.TextureRange),
        BarrierKind.UnorderedAccess => new ResourceBarrier(
            BarrierKind.UnorderedAccess,
            resources[barrier.Resource],
            ResourceState.UnorderedAccess,
            ResourceState.UnorderedAccess,
            barrier.TextureRange),
        BarrierKind.Aliasing => ResourceBarrier.Aliasing(
            resources[barrier.AliasingBefore],
            resources[barrier.Resource]),
        _ => throw new ArgumentOutOfRangeException(nameof(barrier)),
    };

    private static void RecordCommand(
        ICommandContext commands,
        in CaptureCommand command,
        ResourceHandle[] resources)
    {
        switch (command.Kind)
        {
            case CaptureCommandKind.CopyBuffer:
                ResourceHandle source = resources[command.Source];
                ResourceHandle destination = resources[command.Destination];
                commands.CopyBuffer(
                    new BufferHandle(source.Domain, source.Slot, source.Generation),
                    command.SourceOffset,
                    new BufferHandle(destination.Domain, destination.Slot, destination.Generation),
                    command.DestinationOffset,
                    command.Size);
                break;
            default:
                throw new InvalidOperationException("Capture command kind is unsupported.");
        }
    }

    private static Dictionary<int, byte[]> SnapshotReadbackBuffers(
        Capture capture,
        IDevice device,
        ResourceHandle[] resources)
    {
        Dictionary<int, byte[]> result = [];
        foreach (CaptureResource resource in capture.Resources)
        {
            if (resource.Buffer is not CaptureBufferDescription buffer ||
                buffer.MemoryType != MemoryType.Readback ||
                buffer.Size > int.MaxValue)
            {
                continue;
            }
            byte[] data = new byte[checked((int)buffer.Size)];
            ResourceHandle handle = resources[resource.Ordinal];
            device.ReadBuffer(
                new BufferHandle(handle.Domain, handle.Slot, handle.Generation),
                0,
                data);
            result.Add(resource.Ordinal, data);
        }
        return result;
    }

    private static GpuCompletion Submit(
        IDevice device,
        QueueType queue,
        string name,
        ReadOnlySpan<GpuCompletion> waits,
        Action<ICommandContext> record)
    {
        CommandListHandle list = default;
        try
        {
            using (ICommandContext commands = device.AcquireCommandContext(queue, name))
            {
                record(commands);
                list = commands.Finish();
            }
            GpuCompletion completion = device.Submit(queue, new[] { list }, waits);
            list = default;
            return completion;
        }
        finally
        {
            if (list.IsValid) device.DiscardCommandList(list);
        }
    }
}
