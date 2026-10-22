using SomeEngine.Job;

namespace SomeEngine.Resources;

/// <summary>One immutable page version with a shared, demand-driven CPU copy.</summary>
internal sealed class TerrainFieldPage
{
    private readonly object _gate = new();
    private TerrainDataRequest? _readRequest;
    private JobHandle _cpuCompletion;

    internal TerrainFieldPage(float[] samples, JobHandle completion)
    {
        Samples = samples;
        SampleCount = samples.Length;
        Completion = _cpuCompletion = completion;
    }

    internal TerrainFieldPage(int sampleCount, ITerrainPageSource source, JobHandle completion)
    {
        SampleCount = sampleCount;
        Source = source;
        Completion = completion;
    }

    internal int SampleCount { get; }
    internal JobHandle Completion { get; }
    internal ITerrainPageSource? Source { get; }
    internal float[]? Samples { get; private set; }

    internal TerrainPageRead ReadCpu(Terrain terrain)
    {
        lock (_gate)
        {
            if (Samples is null)
            {
                Samples = new float[SampleCount];
                var completion = new TerrainCompletion(terrain.Editing.Scheduler,
                    [JobResourceAccess.Write(terrain.Editing.Scheduler, Samples)]);
                _cpuCompletion = completion.Handle;
                _readRequest = Source!.CreateReadRequest(terrain, this, Samples, completion);
            }
            if (_readRequest is not null) terrain.RetainRequest(_readRequest);
            return new(Samples, _cpuCompletion);
        }
    }
}

internal readonly record struct TerrainPageRead(float[] Samples, JobHandle Completion);

internal interface ITerrainPageSource
{
    TerrainDataRequest CreateReadRequest(Terrain terrain, TerrainFieldPage page, float[] samples,
        TerrainCompletion completion);
}
