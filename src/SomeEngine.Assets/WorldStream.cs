using System.Diagnostics;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.Job;

namespace SomeEngine.Assets;

/// <summary>Loads complete entity groups into a borrowed World as content demand changes.</summary>
public sealed partial class WorldStream : IDisposable, IAsyncDisposable
{
    private readonly World _world;
    private readonly AssetLoader _loader;
    private readonly WorldStreamOptions _options;
    private readonly SerializationReadOptions _readOptions;
    private readonly Scheduler _scheduler;
    private readonly CancellationTokenSource _stop = new();
    private Task<WorldContent[]> _contents;
    private readonly Dictionary<int, Task<ReadOnlyMemory<byte>>> _reads = [];
    private readonly List<int> _candidates = [];
    private readonly List<int> _finished = [];
    private readonly List<Entity> _unload = [];
    private int[] _byMinX = [];
    private List<int>[] _hits = [];
    private ContentDemand[] _nextDemands = [];
    private bool _hasContents;
    private Task? _close;

    public WorldStream(World world, AssetLoader loader, AssetId<WorldAsset> source,
        WorldStreamOptions options, SerializationReadOptions readOptions = default)
    {
        _world = world;
        _loader = loader;
        _options = options;
        _readOptions = readOptions;
        _scheduler = world.Execution?.Scheduler ?? JobSystem.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConcurrentReads);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxReadyBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(options.CommitBudget.Ticks);
        _contents = ReadContentsAsync(source);
    }

    public Task ContentsReady => _contents;
    public ReadOnlyMemory<WorldContent> Contents => _contents.IsCompletedSuccessfully ? _contents.Result : default;
    public ContentRequests Requests { get; } = new();
    public ContentDemands Demands { get; } = new();
    public ContentReadResults ReadResults { get; } = new();
    public LoadedEntityGroups LoadedGroups { get; } = new();

    public void Request(int contentIndex, int priority = 0) => Requests.Values[contentIndex] = priority;
    public void Release(int contentIndex) => Requests.Values.Remove(contentIndex);

    /// <summary>Completes the byte reads started so far; Update collects their results and commits groups.</summary>
    public Task CompleteReadsAsync() => Task.WhenAll(_reads.Values.Cast<Task>());

    /// <summary>Completes this call's source work, then advances I/O and whole-group commits.</summary>
    public void Update(ReadOnlyMemory<WorldStreamSource> sources = default, JobHandle dependency = default)
    {
        ObjectDisposedException.ThrowIf(_close is not null, this);
        if (!_contents.IsCompleted)
        {
            dependency.Complete();
            return;
        }
        WorldContent[] contents = _contents.GetAwaiter().GetResult();
        if (!_hasContents) SetContents(contents);
        UpdateDemands(contents, sources, dependency);
        UnloadUnrequired();
        CollectReads();
        StartReads(contents);
        CommitReads();
    }

    private async Task<WorldContent[]> ReadContentsAsync(AssetId<WorldAsset> source)
    {
        using WorldAsset asset = await WorldAsset.OpenAsync(_loader, source, _stop.Token).ConfigureAwait(false);
        WorldBlock block = await asset.GetBlockAsync<WorldBlock>(_stop.Token).ConfigureAwait(false);
        return ImmutableCollectionsMarshal.AsArray(block.Contents) ?? [];
    }

    private void SetContents(WorldContent[] contents)
    {
        Demands.Data = new ContentDemand[contents.Length];
        Demands.Changes = new int[contents.Length];
        _nextDemands = new ContentDemand[contents.Length];
        _byMinX = Enumerable.Range(0, contents.Length).ToArray();
        Array.Sort(_byMinX, (first, second) => contents[first].Bounds.MinX.CompareTo(contents[second].Bounds.MinX));
        _hasContents = true;
    }

    private async Task<ReadOnlyMemory<byte>> ReadContentAsync(AssetId<Asset> source)
    {
        using EntityAsset asset = await EntityAsset.OpenAsync(_loader, new(source.Value), _stop.Token).ConfigureAwait(false);
        EntityBlock block = await asset.GetBlockAsync<EntityBlock>(_stop.Token).ConfigureAwait(false);
        return await block.ReadBytesAsync(EntityBlock.ContentKey, _stop.Token).ConfigureAwait(false);
    }

    private void CollectReads()
    {
        _finished.Clear();
        foreach (var (index, read) in _reads)
        {
            if (!read.IsCompleted) continue;
            _finished.Add(index);
            ReadOnlyMemory<byte> bytes = read.GetAwaiter().GetResult();
            if (Demands.Data[index].Required) ReadResults.Values.Add(new(index, bytes));
        }
        foreach (int index in _finished) _reads.Remove(index);
        for (int index = ReadResults.Values.Count - 1; index >= 0; index--)
            if (!Demands.Data[ReadResults.Values[index].ContentIndex].Required) ReadResults.Values.RemoveAt(index);
    }

    private void StartReads(WorldContent[] contents)
    {
        long readyBytes = 0;
        foreach (ContentBytes result in ReadResults.Values) readyBytes += result.Bytes.Length;
        if (readyBytes >= _options.MaxReadyBytes) return;
        _candidates.Clear();
        for (int index = 0; index < contents.Length; index++)
            if (Demands.Data[index].Required && !LoadedGroups.Values.ContainsKey(index) && !_reads.ContainsKey(index)
                && !HasReadResult(index))
                _candidates.Add(index);
        _candidates.Sort(ComparePriority);
        foreach (int index in _candidates)
        {
            if (_reads.Count == _options.MaxConcurrentReads) break;
            _reads.Add(index, ReadContentAsync(contents[index].Asset));
        }
    }

    private bool HasReadResult(int contentIndex)
    {
        foreach (ContentBytes result in ReadResults.Values)
            if (result.ContentIndex == contentIndex) return true;
        return false;
    }

    private int ComparePriority(int first, int second)
    {
        int order = Demands.Data[second].Priority.CompareTo(Demands.Data[first].Priority);
        return order != 0 ? order : first.CompareTo(second);
    }

    private void CommitReads()
    {
        ReadResults.Values.Sort((first, second) => ComparePriority(first.ContentIndex, second.ContentIndex));
        if (_options.CommitBudget == TimeSpan.Zero) return;
        long start = Stopwatch.GetTimestamp();
        while (ReadResults.Values.Count != 0)
        {
            ContentBytes result = ReadResults.Values[0];
            Entity[] members = WorldSerializer.ReadEntities(result.Bytes, _world, _readOptions);
            LoadedGroups.Values.Add(result.ContentIndex, members);
            ReadResults.Values.RemoveAt(0);
            if (Stopwatch.GetElapsedTime(start) >= _options.CommitBudget) break;
        }
    }

    private void UnloadUnrequired()
    {
        _finished.Clear();
        foreach (var (index, members) in LoadedGroups.Values)
            if (!Demands.Data[index].Required)
            {
                DestroyMembers(members);
                _finished.Add(index);
            }
        foreach (int index in _finished) LoadedGroups.Values.Remove(index);
    }

    private void DestroyMembers(Entity[] members)
    {
        _unload.Clear();
        foreach (Entity member in members)
            if (_world.IsAlive(member) && !_world.Has<Destroyed>(member)) _unload.Add(member);
        if (_unload.Count != 0) _world.Destroy(CollectionsMarshal.AsSpan(_unload));
        _unload.Clear();
    }

    public void UnloadAll()
    {
        Requests.Values.Clear();
        Demands.ChangeCount = 0;
        for (int index = 0; index < Demands.Data.Length; index++)
            if (Demands.Data[index] != default)
            {
                Demands.Changes[Demands.ChangeCount++] = index;
                Demands.Data[index] = default;
            }
        foreach (Entity[] members in LoadedGroups.Values.Values) DestroyMembers(members);
        LoadedGroups.Values.Clear();
        ReadResults.Values.Clear();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync() => new(_close ??= CloseAsync());

    private async Task CloseAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_reads.Values.Cast<Task>().Append(_contents)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            UnloadAll();
            _reads.Clear();
            _hits = [];
            _byMinX = [];
            _nextDemands = [];
            _contents = Task.FromResult(Array.Empty<WorldContent>());
            Demands.Data = [];
            Demands.Changes = [];
            _stop.Dispose();
        }
    }
}
