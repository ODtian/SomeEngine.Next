namespace SomeEngine.Resources;

public sealed partial class Terrain : IDisposable
{
    private readonly List<ITerrainFileSource> _fileSources = [];
    private bool _sourcesClosed;

    internal int AddFileSource(ITerrainFileSource source)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_sourcesClosed, this);
            int index = _fileSources.Count;
            _fileSources.Add(source.Retain());
            return index;
        }
    }

    internal ITerrainFileSource RetainFileSource(int index)
    {
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(_sourcesClosed, this);
            return _fileSources[index].Retain();
        }
    }

    /// <summary>Closes this Terrain's file access. Existing reads, samples and retained branches keep their data.</summary>
    public void Dispose()
    {
        lock (Gate)
        {
            if (_sourcesClosed) return;
            _sourcesClosed = true;
            foreach (ITerrainFileSource source in _fileSources) source.Dispose();
            _fileSources.Clear();
        }
    }
}

internal interface ITerrainFileSource : IDisposable
{
    ITerrainFileSource Retain();
    ValueTask ReadPageIntoAsync(ulong key, Memory<float> samples);
}

internal readonly record struct TerrainFilePage(int SourceIndex, ulong ChunkKey);
