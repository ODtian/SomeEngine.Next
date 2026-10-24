using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

public abstract partial class Asset
{
    private readonly Dictionary<Guid, BlockEntry> _blocks = [];
    private ImmutableArray<AssetBlockDescription> _blockDescriptions = [];
    private CancellationTokenSource? _blockReadCancellation;

    protected void ReadBlockContent(AssetFile file)
    {
        var header = new AssetHeader();
        file.ReadRootInto(header);
        if (header.Id != Id || header.AssetType != AssetType)
            throw new InvalidDataException("The file contains a different asset identity or semantic type.");
        Name = header.Name;
        BlockDescriptions = header.BlockDescriptions;
    }

    protected async ValueTask<AssetEntry> SaveBlockContentAsync(string path, string pathSuffix, CancellationToken cancellationToken)
    {
        var header = new AssetHeader { Id = Id, Name = Name, AssetType = AssetType };
        var writer = BinaryDocumentWriter.Create(header);
        var retained = new List<AssetBlockWriter>();
        try
        {
            ValidateContent();
            await WriteBlocksAsync(writer, retained, cancellationToken).ConfigureAwait(false);
            header.BlockDescriptions = _blockDescriptions;
            return await SaveFileAsync(writer, path, pathSuffix,
                Describe(AssetHeader.SchemaFingerprint, header.BlockDescriptions.SelectMany(static block => block.References)),
                cancellationToken).ConfigureAwait(false);
        }
        finally { foreach (AssetBlockWriter block in retained) block.Dispose(); }
    }

    protected ImmutableArray<AssetBlockDescription> BlockDescriptions
    {
        get { lock (SyncRoot) { RequireIdle(); return _blockDescriptions; } }
        set
        {
            if (value.IsDefault) throw new InvalidDataException("The block directory is missing.");
            var entries = new Dictionary<Guid, BlockEntry>();
            var keys = new HashSet<ulong>();
            Guid previous = Guid.Empty;
            bool first = true;
            foreach (AssetBlockDescription description in value)
            {
                if (description.BlockTypeId == Guid.Empty || description.EncodingTypeId == Guid.Empty || description.SchemaHash == 0 || description.Epoch == 0
                    || description.MetadataKey == 0 || description.ChunkKeys.IsDefault || description.References.IsDefault)
                    throw new InvalidDataException("The block directory contains invalid data.");
                if (!first && CompareBlockIds(previous, description.BlockTypeId) >= 0)
                    throw new InvalidDataException("The block directory is not ordered by block identity or contains a duplicate identity.");
                first = false;
                previous = description.BlockTypeId;
                if (!keys.Add(description.MetadataKey) || description.ChunkKeys.Any(key => !keys.Add(AssetBlockWriter.StorageKey(description.BlockTypeId, key))))
                    throw new InvalidDataException("Several blocks claim the same document chunk.");
                entries.Add(description.BlockTypeId, new(description));
            }
            lock (SyncRoot)
            {
                RequireIdle();
                if (_blocks.Values.Any(static entry => entry.Read is not null))
                    throw new InvalidOperationException("Cannot replace the block directory while reads are active.");
                ReleaseBlocks();
                foreach (var entry in entries) _blocks.Add(entry.Key, entry.Value);
                _blockDescriptions = value;
            }
        }
    }

    public bool ContainsBlock<T>() where T : AssetBlock, IAssetBlock<T>
    {
        lock (SyncRoot) { RequireIdle(); return _blocks.ContainsKey(T.BlockTypeId); }
    }

    protected AssetBlock? GetBlock(Guid blockTypeId)
    {
        lock (SyncRoot) { RequireIdle(); return _blocks[blockTypeId].Content; }
    }

    protected AssetBlockSource OpenBlockSource(Guid blockTypeId)
    {
        lock (SyncRoot) { RequireIdle(); return new(_file!, _blocks[blockTypeId].Description!); }
    }

    public T GetBlock<T>() where T : AssetBlock, IAssetBlock<T>
    {
        lock (SyncRoot)
        {
            RequireIdle();
            BlockEntry? entry = _blocks.GetValueOrDefault(T.BlockTypeId);
            if (entry is null) throw new KeyNotFoundException($"Block {T.BlockTypeId} is absent.");
            return entry.Content is T value ? value : throw new InvalidOperationException("Acquire this block asynchronously before synchronous access.");
        }
    }

    public async ValueTask<T> GetBlockAsync<T>(CancellationToken cancellationToken = default)
        where T : AssetBlock, IAssetBlock<T>
        => await TryGetBlockAsync<T>(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Block {T.BlockTypeId} is absent.");

    /// <summary>Acquires a block when present; an absent block returns null.</summary>
    public ValueTask<T?> TryGetBlockAsync<T>(CancellationToken cancellationToken = default)
        where T : AssetBlock, IAssetBlock<T>
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<AssetBlock> read;
        AssetBlockSource? source = null;
        TaskCompletionSource<AssetBlock>? completion = null;
        CancellationToken readCancellation = default;
        BlockEntry entry;
        lock (SyncRoot)
        {
            RequireIdle();
            if (!_blocks.TryGetValue(T.BlockTypeId, out entry!)) return ValueTask.FromResult<T?>(null);
            if (entry.Content is T value) return ValueTask.FromResult<T?>(value);
            if (entry.Content is not null) throw new InvalidDataException("Different block types have the same persistent identity.");
            if (entry.Read is null)
            {
                source = new(_file ?? throw new InvalidOperationException("The block has no document source."), entry.Description!);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                entry.Read = completion.Task;
                readCancellation = (_blockReadCancellation ??= new()).Token;
            }
            read = entry.Read;
        }
        if (completion is not null) _ = ReadBlockAsync<T>(entry, source!, completion, readCancellation);
        return AwaitBlock<T>(read, cancellationToken);
    }

    private static async ValueTask<T?> AwaitBlock<T>(Task<AssetBlock> read, CancellationToken cancellationToken) where T : AssetBlock
        => (T)await read.WaitAsync(cancellationToken).ConfigureAwait(false);

    private async Task ReadBlockAsync<T>(BlockEntry entry, AssetBlockSource source, TaskCompletionSource<AssetBlock> completion,
        CancellationToken cancellationToken) where T : AssetBlock, IAssetBlock<T>
    {
        T? content = null;
        try
        {
            using (source)
            {
                content = await T.ReadAsync(source, cancellationToken).ConfigureAwait(false);
            }
            if (content.BlockId != T.BlockTypeId) throw new InvalidDataException("A block reader returned a different persistent type.");
            AssetBlock replacement = content.Duplicate(SyncRoot);
            try { content.Dispose(); }
            catch { replacement.Dispose(); throw; }
            content = (T)replacement;
            lock (SyncRoot)
            {
                RequireIdle();
                entry.Content = content;
                entry.Persistent = T.Persistent;
                entry.Read = null;
                completion.SetResult(content);
                content = null;
            }
        }
        catch (Exception error)
        {
            try { content?.Dispose(); }
            catch (Exception cleanup) { error = new AggregateException("Block acquisition and cleanup failed.", error, cleanup); }
            finally { lock (SyncRoot) entry.Read = null; }
            if (error is OperationCanceledException cancelled) completion.TrySetCanceled(cancelled.CancellationToken);
            else completion.TrySetException(error);
        }
    }

    public IReadOnlyList<Exception> SetBlock<T>(T block) where T : AssetBlock, IAssetBlock<T>
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.BlockId != T.BlockTypeId) throw new InvalidDataException("The block identity differs from its declared type.");
        Guid id = T.BlockTypeId;
        AssetBlock replacement = block.Duplicate(SyncRoot);
        AssetBlock? retired = null;
        try
        {
            lock (SyncRoot)
            {
                RequireIdle();
                BlockEntry? current = _blocks.GetValueOrDefault(id);
                if (current?.Read is not null) throw new InvalidOperationException("Cannot replace a block while its acquisition is active.");
                if (current?.Content is not null && current.Content.GetType() != replacement.GetType())
                    throw new InvalidDataException("Different block types have the same persistent identity.");
                retired = current?.Content;
                _blocks[id] = new(replacement, T.Persistent);
                replacement = null!;
            }
        }
        finally { replacement?.Dispose(); }
        return retired is null ? Array.Empty<Exception>() : ReleaseBlockGroup([retired]);
    }

    public IReadOnlyList<Exception> RemoveBlock<T>() where T : AssetBlock, IAssetBlock<T>
    {
        Guid id = T.BlockTypeId;
        AssetBlock? retired = null;
        lock (SyncRoot)
        {
            RequireIdle();
            BlockEntry? current = _blocks.GetValueOrDefault(id);
            if (current is null) return Array.Empty<Exception>();
            if (current.Read is not null) throw new InvalidOperationException("Cannot remove a block while its acquisition is active.");
            retired = current.Content;
            _blocks.Remove(id);
        }
        return retired is null ? Array.Empty<Exception>() : ReleaseBlockGroup([retired]);
    }

    internal IReadOnlyList<Exception> ApplyBlocks(IReadOnlyCollection<(AssetBlock Block, bool Persistent)> blocks)
    {
        lock (SyncRoot) RequireIdle();
        var replacements = new List<(Guid Id, BlockEntry Entry, AssetBlock Source)>();
        var used = new HashSet<Guid>();
        bool applied = false;
        try
        {
            foreach ((AssetBlock block, bool persistent) in blocks)
            {
                if (block.BlockId == Guid.Empty) throw new InvalidOperationException("The result contains an empty block identity.");
                if (!used.Add(block.BlockId)) throw new InvalidOperationException("The result contains a repeated block identity.");
                replacements.Add((block.BlockId, new BlockEntry(block.Duplicate(SyncRoot), persistent), block));
            }

            var retired = new List<AssetBlock>();
            lock (SyncRoot)
            {
                RequireIdle();
                foreach ((Guid id, BlockEntry replacement, _) in replacements)
                {
                    BlockEntry? current = _blocks.GetValueOrDefault(id);
                    if (current?.Read is not null) throw new InvalidOperationException("Cannot replace a block while its acquisition is active.");
                    if (current?.Content is not null && current.Content.GetType() != replacement.Content!.GetType())
                        throw new InvalidDataException("Different block types have the same persistent identity.");
                    if (current?.Content is { } old) retired.Add(old);
                }
                foreach ((Guid id, BlockEntry replacement, _) in replacements) _blocks[id] = replacement;
                applied = true;
            }
            retired.AddRange(replacements.Select(static value => value.Source));
            return ReleaseBlockGroup(retired);
        }
        catch (Exception failure)
        {
            if (!applied)
            {
                IReadOnlyList<Exception> cleanup = ReleaseBlockGroup(replacements.Select(static value => value.Entry.Content!));
                if (cleanup.Count != 0) throw new AggregateException("Block replacement and cleanup failed.", new[] { failure }.Concat(cleanup));
            }
            throw;
        }
    }

    private static IReadOnlyList<Exception> ReleaseBlockGroup(IEnumerable<AssetBlock> blocks)
    {
        List<Exception>? errors = null;
        foreach (AssetBlock block in blocks)
        {
            try { block.Dispose(); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        return errors ?? (IReadOnlyList<Exception>)Array.Empty<Exception>();
    }

    protected IEnumerable<Guid> BlockReferences()
    {
        lock (SyncRoot)
        {
            RequireIdle();
            return _blocks.Values.Where(static entry => entry.Persistent)
                .SelectMany(static entry => entry.Content?.References() ?? entry.Description!.References)
                .Where(static id => id != Guid.Empty).Distinct().ToArray();
        }
    }

    private void CopyBlocksFrom(Asset source)
    {
        _blockDescriptions = source._blockDescriptions;
        try
        {
            foreach (var entry in source._blocks)
                _blocks.Add(entry.Key, entry.Value.Content is { } content
                    ? new(content.Duplicate(SyncRoot), entry.Value.Persistent) : new(entry.Value.Description!));
        }
        catch { ReleaseBlocks(); throw; }
    }

    private async ValueTask WriteBlocksAsync(BinaryDocumentWriter writer, List<AssetBlockWriter> retained,
        CancellationToken cancellationToken)
    {
        var entries = _blocks.Values.Where(static entry => entry.Persistent).ToList();
        entries.Sort(static (left, right) => CompareBlockIds(left.Id, right.Id));

        var descriptions = ImmutableArray.CreateBuilder<AssetBlockDescription>();
        using AssetFile? file = RetainFile();
        foreach (BlockEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Content is { } block)
            {
                if (block is not IAssetBlockWriter writable) continue;
                var output = new AssetBlockWriter(writer);
                retained.Add(output);
                block.ValidateForWrite();
                await writable.WriteAsync(output, cancellationToken).ConfigureAwait(false);
                descriptions.Add(output.Complete(block));
            }
            else
            {
                AssetBlockDescription description = entry.Description!;
                if (file is null) throw new InvalidDataException("An unloaded block has no file source.");
                writer.AddContract(new(description.EncodingTypeId, description.SchemaHash, description.EncodingCompatibility, description.Epoch));
                await file.AddChunkAsync(writer, description.MetadataKey, cancellationToken).ConfigureAwait(false);
                foreach (ulong key in description.ChunkKeys)
                    await file.AddChunkAsync(writer, AssetBlockWriter.StorageKey(description.BlockTypeId, key), cancellationToken).ConfigureAwait(false);
                descriptions.Add(description);
            }
        }
        _blockDescriptions = descriptions.ToImmutable();
    }

    private Task[] ActiveBlockReads() => _blocks.Values.Where(static entry => entry.Read is not null)
        .Select(static entry => (Task)entry!.Read!).ToArray();

    private void ReleaseBlocks()
    {
        try
        {
            IReadOnlyList<Exception> errors = ReleaseBlockGroup(_blocks.Values.Where(static entry => entry.Content is not null)
                .Select(static entry => entry!.Content!));
            if (errors.Count != 0) throw new AggregateException("Asset block cleanup failed.", errors);
        }
        finally
        {
            _blocks.Clear();
        }
    }

    private static int CompareBlockIds(Guid left, Guid right) => left.CompareTo(right);

    private sealed class BlockEntry
    {
        internal AssetBlockDescription? Description { get; }
        internal bool Persistent { get; set; }
        internal AssetBlock? Content;
        internal Task<AssetBlock>? Read;
        internal Guid Id => Content?.BlockId ?? Description!.BlockTypeId;
        internal BlockEntry(AssetBlockDescription description) { Description = description; Persistent = true; }
        internal BlockEntry(AssetBlock content, bool persistent) { Content = content; Persistent = persistent; }
    }
}
