using System.Security.Cryptography;
using System.Text.Json.Serialization.Metadata;

namespace SomeEngine.Assets.Importing;

/// <summary>Creates each requested semantic asset once using this import's selected conversions.</summary>
public sealed class ImportContext : IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly SourceMeta _sourceMeta;
    private readonly Dictionary<(Type Source, Type Asset), object> _sourceTypes = [];
    private readonly Dictionary<Guid, AssetResult> _assets = [];
    private readonly List<IAsyncDisposable> _resources = [];
    private readonly Dictionary<string, string> _inputs = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly AsyncLocal<ImportWork?> _current = new();
    private TaskCompletionSource? _closed;
    private bool _started;

    public ImportContext(CancellationToken cancellationToken = default)
        : this(Directory.GetCurrentDirectory(), "", new SourceMeta { SourceGuid = SourceGuid.New() }, cancellationToken) { }

    public ImportContext(string projectRoot, string sourcePath, SourceMeta sourceMeta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceMeta);
        ProjectRoot = Path.GetFullPath(projectRoot);
        SourcePath = sourcePath;
        _sourceMeta = sourceMeta;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public string ProjectRoot { get; }
    public string SourcePath { get; }
    public SourceGuid SourceGuid => _sourceMeta.SourceGuid;
    public IReadOnlyList<SourceImporterSettings> Selections => _sourceMeta.Importers;
    public bool UseDefaultImporters => _sourceMeta.UseDefaultImporters;

    public T GetSettings<T>(Guid importerId, T defaults, JsonTypeInfo<T> typeInfo,
        AssetImportSettings<T>? overrides = null)
        => overrides is null ? _sourceMeta.GetSettings(importerId, defaults, typeInfo) : overrides.Value;

    /// <summary>Declares a native source type's identity, creation and saving rules, not individual results.</summary>
    public void ConfigureAssets<TSource, TAsset>(Func<TSource, string> elementKey,
        Func<TSource, Guid, TAsset> create, Func<TSource, Guid, string> outputPath,
        Func<TAsset, string, CancellationToken, ValueTask<AssetEntry>> save)
        where TSource : class where TAsset : Asset
    {
        ArgumentNullException.ThrowIfNull(elementKey);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(save);
        lock (_sync)
        {
            RequireConfiguration();
            if (!_sourceTypes.TryAdd((typeof(TSource), typeof(TAsset)),
                new SourceAssets<TSource, TAsset>(elementKey, create, outputPath, save)))
                throw new InvalidOperationException("This source and semantic asset type are already configured.");
        }
    }

    public void EnableImporter<TSource, TAsset>(IConfiguredAssetImporter<TSource> importer)
        where TSource : class where TAsset : Asset
    {
        ArgumentNullException.ThrowIfNull(importer);
        lock (_sync)
        {
            RequireConfiguration();
            SourceAssets<TSource, TAsset> source = GetSourceType<TSource, TAsset>();
            if (source.Importers.Any(value => value.Id == importer.Id))
                throw new InvalidOperationException($"Importer {importer.Id} is selected twice for {typeof(TAsset)}.");
            source.Importers.Add(importer);
        }
    }

    public bool HasImporters<TSource, TAsset>() where TSource : class where TAsset : Asset
    {
        lock (_sync)
        {
            RequireOpen();
            return _sourceTypes.TryGetValue((typeof(TSource), typeof(TAsset)), out object? source)
                && ((SourceAssets<TSource, TAsset>)source).Importers.Count != 0;
        }
    }

    /// <summary>Returns the same completed object to every request for one source element and semantic asset.</summary>
    public async ValueTask<TAsset> GetAssetAsync<TSource, TAsset>(TSource source,
        CancellationToken cancellationToken = default) where TSource : class where TAsset : Asset
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        AssetResult result;
        lock (_sync)
        {
            RequireOpen();
            _cancellation.Token.ThrowIfCancellationRequested();
            _started = true;
            SourceAssets<TSource, TAsset> definition = GetSourceType<TSource, TAsset>();
            string key = definition.ElementKey(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            if (definition.Importers.Count == 0)
                throw new InvalidOperationException($"No importer is enabled for {typeof(TSource)} -> {typeof(TAsset)}.");
            if (!_sourceMeta.ElementIds.TryGetValue(key, out Guid id))
                _sourceMeta.ElementIds.Add(key, id = Guid.NewGuid());
            if (_assets.TryGetValue(id, out result!))
            {
                if (result.ElementKey != key || result.Content is not TAsset)
                    throw new InvalidOperationException("Different source elements or semantic asset types share one identity.");
                if (result.Completion.Task.IsCompletedSuccessfully) return (TAsset)result.Content;
            }
            else
            {
                string path = AssetProject.RequireProjectPath(ProjectRoot, definition.OutputPath(source, id));
                if (_assets.Values.Any(value => AssetProject.PathsEqual(value.Path, path)))
                    throw new InvalidOperationException("Two imported assets use the same output path.");
                TAsset asset = definition.Create(source, id);
                try
                {
                    if (asset.Id != id) throw new InvalidOperationException("The asset factory returned a different identity.");
                    result = CreateResult(source, asset, key, path, definition);
                    _assets.Add(id, result);
                }
                catch { asset.Dispose(); throw; }
            }
        }
        await RunAsync(result.Completion, cancellationToken).ConfigureAwait(false);
        return (TAsset)result.Content;
    }

    /// <summary>Runs the selected producer on demand; dependency order is not traversal order.</summary>
    public async ValueTask<T> GetBlockAsync<T>(Asset asset, CancellationToken cancellationToken = default)
        where T : AssetBlock, IAssetBlock<T>
    {
        ArgumentNullException.ThrowIfNull(asset);
        cancellationToken.ThrowIfCancellationRequested();
        ImportWork? producer = null;
        lock (_sync)
        {
            RequireOpen();
            if (_assets.TryGetValue(asset.Id, out AssetResult? result) && ReferenceEquals(result.Content, asset))
                producer = result.Producers.GetValueOrDefault(T.BlockTypeId);
        }
        if (producer is not null)
            await RunAsync(producer, cancellationToken).ConfigureAwait(false);
        return await asset.GetBlockAsync<T>(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completed assets generated by this context; objects remain valid until the context is closed.</summary>
    public IReadOnlyList<Asset> Assets
    {
        get
        {
            lock (_sync)
            {
                RequireOpen();
                return _assets.Values.Where(value => value.Completion.Task.IsCompletedSuccessfully)
                    .Select(value => value.Content).ToArray();
            }
        }
    }

    public void Hold(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_sync) { RequireOpen(); _resources.Add(resource); }
    }

    public void ReportInput(string fullPath, string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        if (!Path.IsPathRooted(fullPath)) throw new ArgumentException("An importer input needs an absolute path.", nameof(fullPath));
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        if (contentHash.Length != 64 || contentHash.Any(static value => !char.IsAsciiHexDigit(value)))
            throw new ArgumentException("An importer input needs a SHA-256 hexadecimal digest.", nameof(contentHash));
        string path = Path.GetFullPath(fullPath);
        string hash = contentHash.ToUpperInvariant();
        lock (_sync)
        {
            RequireOpen();
            _cancellation.Token.ThrowIfCancellationRequested();
            if (_inputs.TryGetValue(path, out string? previous) && previous != hash)
                throw new IOException($"Importers read different bytes for '{path}'.");
            _inputs[path] = hash;
        }
    }

    public void ReportInput(string fullPath)
    {
        using var file = File.OpenRead(fullPath);
        ReportInput(fullPath, Convert.ToHexString(SHA256.HashData(file)));
    }

    public async ValueTask ReportInputAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        ReportInput(fullPath, Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false)));
    }

    internal IReadOnlyList<AssetOutput> Outputs
    {
        get
        {
            lock (_sync)
            {
                RequireCompleted();
                return _assets.Values.OrderBy(value => value.ElementKey, StringComparer.Ordinal)
                    .Select(value => new AssetOutput(value.Content, value.ElementKey, value.Path, value.Save)).ToArray();
            }
        }
    }

    internal IReadOnlyDictionary<string, string> ReportedInputs()
    {
        lock (_sync) { RequireCompleted(); return new Dictionary<string, string>(_inputs, _inputs.Comparer); }
    }

    private SourceAssets<TSource, TAsset> GetSourceType<TSource, TAsset>() where TSource : class where TAsset : Asset
        => _sourceTypes.TryGetValue((typeof(TSource), typeof(TAsset)), out object? source)
            ? (SourceAssets<TSource, TAsset>)source
            : throw new InvalidOperationException($"No source rules are configured for {typeof(TSource)} -> {typeof(TAsset)}.");

    private AssetResult CreateResult<TSource, TAsset>(TSource source, TAsset asset, string key, string path,
        SourceAssets<TSource, TAsset> definition) where TSource : class where TAsset : Asset
    {
        var result = new AssetResult(asset, key, path, (value, destination, token) => definition.Save((TAsset)value, destination, token));
        foreach (IConfiguredAssetImporter<TSource> importer in definition.Importers)
        {
            var output = new ImportOutput(asset, importer.ProducedBlocks);
            var work = new ImportWork($"importer {importer.Id}", async token =>
            {
                try
                {
                    await importer.ImportAsync(source, this, output, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    output.Finish();
                    IReadOnlyList<Exception> cleanup = asset.ApplyBlocks(output.Blocks);
                    output.Transfer();
                    if (cleanup.Count != 0) throw new AggregateException("Imported block cleanup failed.", cleanup);
                }
                finally { await output.ReleaseAsync().ConfigureAwait(false); }
            });
            foreach (Guid block in importer.ProducedBlocks)
            {
                if (result.Producers.TryGetValue(block, out ImportWork? other))
                    throw new InvalidOperationException($"Block {block} has competing producers {other.Name} and importer {importer.Id}.");
                result.Producers[block] = work;
            }
            result.Conversions.Add(work);
        }
        result.Completion = new ImportWork($"asset {asset.Id}", async token =>
        {
            foreach (ImportWork work in result.Conversions)
                await RunAsync(work, token).ConfigureAwait(false);
        });
        return result;
    }

    private async ValueTask RunAsync(ImportWork work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ImportWork? parent = _current.Value;
        bool start;
        bool dependency = false;
        lock (_sync)
        {
            RequireOpen();
            if (work.Task.IsCompletedSuccessfully) return;
            if (parent is not null && !work.Task.IsCompleted)
            {
                if (FindPath(work, parent, new HashSet<ImportWork>(), out string? path))
                    throw new InvalidOperationException($"Import dependency cycle: {parent.Name} -> {path}.");
                parent.Dependencies.TryGetValue(work, out int readers);
                parent.Dependencies[work] = readers + 1;
                dependency = true;
            }
            start = !work.Started;
            work.Started = true;
        }
        if (start) _ = ExecuteAsync(work);
        try
        {
            if (parent is not null && cancellationToken == _cancellation.Token)
                await work.Task.ConfigureAwait(false);
            else
                await work.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (dependency)
                lock (_sync)
                {
                    if (parent!.Dependencies[work] == 1) parent.Dependencies.Remove(work);
                    else parent.Dependencies[work]--;
                }
        }
    }

    private async Task ExecuteAsync(ImportWork work)
    {
        ImportWork? previous = _current.Value;
        _current.Value = work;
        try
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            await work.Execute(_cancellation.Token).ConfigureAwait(false);
            _cancellation.Token.ThrowIfCancellationRequested();
            work.Result.TrySetResult();
        }
        catch (OperationCanceledException error) { work.Result.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { work.Result.TrySetException(error); }
        finally { _current.Value = previous; }
    }

    private static bool FindPath(ImportWork from, ImportWork destination, HashSet<ImportWork> visited, out string? path)
    {
        path = null;
        if (!visited.Add(from)) return false;
        if (ReferenceEquals(from, destination)) { path = from.Name; return true; }
        foreach (ImportWork next in from.Dependencies.Keys)
            if (FindPath(next, destination, visited, out path)) { path = from.Name + " -> " + path; return true; }
        return false;
    }

    private void RequireOpen() => ObjectDisposedException.ThrowIf(_closed is not null, this);
    private void RequireConfiguration()
    {
        RequireOpen();
        if (_started) throw new InvalidOperationException("Importer selections and source rules cannot change after the first asset request.");
    }
    private void RequireCompleted()
    {
        RequireOpen();
        _cancellation.Token.ThrowIfCancellationRequested();
        if (_assets.Values.Any(value => !value.Completion.Task.IsCompletedSuccessfully))
            throw new InvalidOperationException("Every requested asset must finish successfully before saving the import.");
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        AssetResult[] assets;
        IAsyncDisposable[] resources;
        lock (_sync)
        {
            if (_closed is not null) return new(_closed.Task);
            _closed = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            assets = _assets.Values.ToArray();
            resources = _resources.ToArray();
        }
        _ = CloseAsync(assets, resources, completion);
        return new(completion.Task);
    }

    private async Task CloseAsync(AssetResult[] assets, IAsyncDisposable[] resources, TaskCompletionSource completion)
    {
        try
        {
            List<Exception> errors = [];
            try { await _cancellation.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
            try
            {
                await Task.WhenAll(assets.SelectMany(value => value.Conversions.Append(value.Completion))
                    .Where(value => value.Started).Select(value => value.Task)).ConfigureAwait(false);
            }
            catch { /* Errors are also delivered to the corresponding asset or block request. */ }
            foreach (AssetResult asset in assets)
                try { await asset.Content.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            for (int index = resources.Length - 1; index >= 0; index--)
                try { await resources[index].DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { errors.Add(error); }
            _cancellation.Dispose();
            if (errors.Count != 0) throw new AggregateException("Import cleanup failed.", errors);
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    internal sealed record AssetOutput(Asset Asset, string ElementKey, string Path,
        Func<Asset, string, CancellationToken, ValueTask<AssetEntry>> Save);

    private sealed record SourceAssets<TSource, TAsset>(Func<TSource, string> ElementKey,
        Func<TSource, Guid, TAsset> Create, Func<TSource, Guid, string> OutputPath,
        Func<TAsset, string, CancellationToken, ValueTask<AssetEntry>> Save) where TSource : class where TAsset : Asset
    {
        public List<IConfiguredAssetImporter<TSource>> Importers { get; } = [];
    }

    private sealed class AssetResult(Asset content, string elementKey, string path,
        Func<Asset, string, CancellationToken, ValueTask<AssetEntry>> save)
    {
        public Asset Content { get; } = content;
        public string ElementKey { get; } = elementKey;
        public string Path { get; } = path;
        public Func<Asset, string, CancellationToken, ValueTask<AssetEntry>> Save { get; } = save;
        public Dictionary<Guid, ImportWork> Producers { get; } = [];
        public List<ImportWork> Conversions { get; } = [];
        public ImportWork Completion { get; set; } = null!;
    }

    private sealed class ImportWork(string name, Func<CancellationToken, ValueTask> execute)
    {
        public string Name { get; } = name;
        public Func<CancellationToken, ValueTask> Execute { get; } = execute;
        public TaskCompletionSource Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task => Result.Task;
        public Dictionary<ImportWork, int> Dependencies { get; } = [];
        public bool Started { get; set; }
    }
}
