using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>One immutable page version with a shared, demand-driven CPU copy.</summary>
internal sealed class TerrainFieldPage
{
    private readonly object _gate = new();
    private TerrainDataRequest? _readRequest;
    private JobHandle _cpuCompletion;

    internal TerrainFieldPage(float[] samples, JobHandle completion, ulong revision)
        : this(samples, completion, revision, Guid.NewGuid()) { }

    internal TerrainFieldPage(float[] samples, JobHandle completion, ulong revision, Guid id)
    {
        Samples = samples;
        SampleCount = samples.Length;
        Completion = _cpuCompletion = completion;
        Revision = revision;
        Id = id;
    }

    internal TerrainFieldPage(int sampleCount, ITerrainPageSource source, JobHandle completion, ulong revision)
    {
        SampleCount = sampleCount;
        Source = source;
        Completion = completion;
        Revision = revision;
        Id = Guid.NewGuid();
    }

    internal TerrainFieldPage(int sampleCount, TerrainFilePage filePage, ulong revision, Guid id)
    {
        SampleCount = sampleCount;
        FilePage = filePage;
        Revision = revision;
        Id = id;
    }

    internal Guid Id { get; }
    internal ulong Revision { get; }
    internal int SampleCount { get; }
    internal JobHandle Completion { get; }
    internal ITerrainPageSource? Source { get; }
    private TerrainFilePage? FilePage { get; }
    internal float[]? Samples { get; private set; }

    internal TerrainPageRead ReadCpu(Terrain terrain)
    {
        lock (_gate)
        {
            if (Samples is null)
            {
                ITerrainFileSource? file = FilePage is { } stored ? terrain.RetainFileSource(stored.SourceIndex) : null;
                Samples = new float[SampleCount];
                var completion = new TerrainCompletion(terrain.Editing.Scheduler,
                    [JobResourceAccess.Write(terrain.Editing.Scheduler, Samples)]);
                _cpuCompletion = completion.Handle;
                if (file is not null)
                    _ = ReadFileAsync(file, FilePage!.Value.ChunkKey, Samples, completion);
                else
                    _readRequest = Source!.CreateReadRequest(terrain, this, Samples, completion);
            }
            if (_readRequest is not null) terrain.RetainRequest(_readRequest);
            return new(Samples, _cpuCompletion);
        }
    }

    internal JobHandle PrepareGpuRead(Terrain terrain) => Source is null ? ReadCpu(terrain).Completion : Completion;

    private static async Task ReadFileAsync(ITerrainFileSource source, ulong key, float[] samples, TerrainCompletion completion)
    {
        try
        {
            using (source) await source.ReadPageIntoAsync(key, samples).ConfigureAwait(false);
            completion.Complete();
        }
        catch (Exception error) { completion.Fail(error); }
    }
}

internal readonly record struct TerrainPageRead(float[] Samples, JobHandle Completion);

internal interface ITerrainPageSource
{
    TerrainDataRequest CreateReadRequest(Terrain terrain, TerrainFieldPage page, float[] samples,
        TerrainCompletion completion);
}
