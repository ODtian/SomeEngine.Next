using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace SomeEngine.RenderGraph.Diagnostics;

public sealed class RenderGraphSnapshot
{
    public const int CurrentVersion = 2;

    [JsonConstructor]
    public RenderGraphSnapshot(
        int version,
        ulong structureVersion,
        ImmutableArray<Pass> passes,
        ImmutableArray<Buffer> buffers,
        ImmutableArray<Texture> textures,
        ImmutableArray<Access> accesses,
        ImmutableArray<Dependency> dependencies,
        ImmutableArray<Barrier> barriers,
        RenderGraphStatistics statistics)
    {
        Version = version;
        StructureVersion = structureVersion;
        Passes = passes.IsDefault ? [] : passes;
        Buffers = buffers.IsDefault ? [] : buffers;
        Textures = textures.IsDefault ? [] : textures;
        Accesses = accesses.IsDefault ? [] : accesses;
        Dependencies = dependencies.IsDefault ? [] : dependencies;
        Barriers = barriers.IsDefault ? [] : barriers;
        Statistics = statistics;
    }

    public int Version { get; }
    public ulong StructureVersion { get; }
    public ImmutableArray<Pass> Passes { get; }
    public ImmutableArray<Buffer> Buffers { get; }
    public ImmutableArray<Texture> Textures { get; }
    public ImmutableArray<Access> Accesses { get; }
    public ImmutableArray<Dependency> Dependencies { get; }
    public ImmutableArray<Barrier> Barriers { get; }
    public RenderGraphStatistics Statistics { get; }

    public static RenderGraphSnapshot Capture(in RenderGraphDiagnosticsView diagnostics)
    {
        var passOrdinals = new Dictionary<GraphPassId, int>(diagnostics.Passes.Length);
        var passes = ImmutableArray.CreateBuilder<Pass>(diagnostics.Passes.Length);
        for (int ordinal = 0; ordinal < diagnostics.Passes.Length; ordinal++)
        {
            RenderGraphPassDiagnostic source = diagnostics.Passes[ordinal];
            passOrdinals.Add(source.Id, ordinal);
            GraphIdentity identity = source.Id.Value;
            QueueInfo? queue = source.Queue is null
                ? null
                : new QueueInfo(source.Queue.Type, source.Queue.Index, source.Queue.NodeIndex);
            passes.Add(new Pass(
                ordinal,
                identity.Slot,
                identity.Generation,
                source.Label,
                source.Kind,
                source.Enabled,
                source.Live,
                source.DeclarationOrdinal,
                source.ScheduledOrdinal,
                queue));
        }

        var buffers = ImmutableArray.CreateBuilder<Buffer>(diagnostics.Buffers.Length);
        for (int ordinal = 0; ordinal < diagnostics.Buffers.Length; ordinal++)
        {
            RenderGraphBufferDiagnostic source = diagnostics.Buffers[ordinal];
            GraphIdentity identity = source.Id.Value;
            buffers.Add(new Buffer(
                ordinal,
                identity.Slot,
                identity.Generation,
                source.Label,
                source.Ownership,
                source.Lifetime,
                source.Size,
                source.PlacementOffset,
                source.PlacementSize,
                source.FirstScheduledUse,
                source.LastScheduledUse));
        }

        var textures = ImmutableArray.CreateBuilder<Texture>(diagnostics.Textures.Length);
        for (int ordinal = 0; ordinal < diagnostics.Textures.Length; ordinal++)
        {
            RenderGraphTextureDiagnostic source = diagnostics.Textures[ordinal];
            GraphIdentity identity = source.Id.Value;
            textures.Add(new Texture(
                ordinal,
                identity.Slot,
                identity.Generation,
                source.Label,
                source.Ownership,
                source.Lifetime,
                source.Width,
                source.Height,
                source.Format,
                source.PlacementOffset,
                source.PlacementSize,
                source.FirstScheduledUse,
                source.LastScheduledUse));
        }

        var accesses = ImmutableArray.CreateBuilder<Access>(diagnostics.Accesses.Length);
        foreach (ref readonly RenderGraphAccessDiagnostic source in diagnostics.Accesses)
        {
            accesses.Add(new Access(
                ResolvePass(source.Pass),
                source.TargetKind,
                source.TargetOrdinal,
                source.Mode,
                source.Coverage,
                source.Sync,
                source.Access,
                source.BufferRange,
                source.TextureRange,
                source.TextureLayout,
                source.ResultContents));
        }

        var dependencies = ImmutableArray.CreateBuilder<Dependency>(diagnostics.Dependencies.Length);
        foreach (ref readonly RenderGraphDependencyDiagnostic source in diagnostics.Dependencies)
        {
            dependencies.Add(new Dependency(
                ResolvePass(source.Predecessor),
                ResolvePass(source.Consumer),
                source.Kind));
        }

        var barriers = ImmutableArray.CreateBuilder<Barrier>(diagnostics.Barriers.Length);
        foreach (ref readonly RenderGraphBarrierDiagnostic source in diagnostics.Barriers)
        {
            barriers.Add(new Barrier(
                ResolvePass(source.Pass),
                source.Kind,
                source.Phase));
        }

        return new RenderGraphSnapshot(
            CurrentVersion,
            diagnostics.StructureVersion,
            passes.MoveToImmutable(),
            buffers.MoveToImmutable(),
            textures.MoveToImmutable(),
            accesses.MoveToImmutable(),
            dependencies.MoveToImmutable(),
            barriers.MoveToImmutable(),
            diagnostics.Statistics);

        int ResolvePass(GraphPassId id)
        {
            if (!passOrdinals.TryGetValue(id, out int ordinal))
                throw new InvalidOperationException("Diagnostics reference an unknown Pass.");
            return ordinal;
        }
    }

    public readonly record struct QueueInfo(QueueType Type, uint Index, uint NodeIndex);

    public readonly record struct Pass(
        int Ordinal,
        int Slot,
        uint Generation,
        string Label,
        GraphPassKind Kind,
        bool Enabled,
        bool Live,
        int DeclarationOrdinal,
        int ScheduledOrdinal,
        QueueInfo? Queue);

    public readonly record struct Buffer(
        int Ordinal,
        int Slot,
        uint Generation,
        string? Label,
        RenderGraphResourceOwnership Ownership,
        RenderGraphResourceLifetime Lifetime,
        ulong Size,
        ulong PlacementOffset,
        ulong PlacementSize,
        int FirstScheduledUse,
        int LastScheduledUse);

    public readonly record struct Texture(
        int Ordinal,
        int Slot,
        uint Generation,
        string? Label,
        RenderGraphResourceOwnership Ownership,
        RenderGraphResourceLifetime Lifetime,
        uint Width,
        uint Height,
        Format Format,
        ulong PlacementOffset,
        ulong PlacementSize,
        int FirstScheduledUse,
        int LastScheduledUse);

    public readonly record struct Access(
        int Pass,
        GraphAccessTargetKind TargetKind,
        int TargetOrdinal,
        GraphAccessMode Mode,
        WriteCoverage Coverage,
        PipelineSync Sync,
        ResourceAccess ResourceAccess,
        BufferRange BufferRange,
        TextureSubresourceRange TextureRange,
        TextureLayout TextureLayout,
        ResourceContentState? ResultContents);

    public readonly record struct Dependency(
        int Predecessor,
        int Consumer,
        RenderGraphDependencyKind Kind);

    public readonly record struct Barrier(
        int Pass,
        RenderGraphBarrierKind Kind,
        BarrierPhase Phase);
}
