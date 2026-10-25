using System.Security.Cryptography;
using System.Text.Json.Serialization.Metadata;
using SomeEngine.Job;

namespace SomeEngine.Assets.Importing;

/// <summary>Creates each requested semantic asset once using this import's selected conversions.</summary>
public sealed class ImportContext : IDisposable, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly SourceMeta _sourceMeta;
    private readonly Dictionary<Guid, object?> _settings = [];
    private readonly Dictionary<(Type Input, Type Asset), object> _importers = [];
    private readonly Dictionary<(object Input, Type Asset), Guid> _assetIds = [];
    private readonly Dictionary<Guid, AssetResult> _assets = [];
    private readonly List<IAsyncDisposable> _resources = [];
    private readonly Dictionary<string, string> _inputs = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly AsyncLocal<ImportWork?> _current = new();
    private TaskCompletionSource? _closed;
    private bool _started;

    public ImportContext(CancellationToken cancellationToken = default, Scheduler? scheduler = null)
        : this(Directory.GetCurrentDirectory(), "", new SourceMeta { SourceGuid = SourceGuid.New() }, cancellationToken, scheduler) { }

    public ImportContext(string projectRoot, string sourcePath, SourceMeta sourceMeta,
        CancellationToken cancellationToken = default, Scheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(sourceMeta);
        ProjectRoot = Path.GetFullPath(projectRoot);
        SourcePath = sourcePath;
        _sourceMeta = sourceMeta;
        Scheduler = scheduler ?? JobSystem.Current;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public string ProjectRoot { get; }
    /// <summary>The executor captured when this import begins, also used after asynchronous source reads.</summary>
    public Scheduler Scheduler { get; }
    /// <summary>The background decode category shared by imports and asset reads on this executor.</summary>
    public JobCategory DecodingCategory => AssetDecodeJobs.GetCategory(Scheduler);
    public string SourcePath { get; }
    public SourceGuid SourceGuid => _sourceMeta.SourceGuid;
    public IReadOnlyList<SourceImporterSettings> Selections => _sourceMeta.Importers;
    public bool UseDefaultImporters => _sourceMeta.UseDefaultImporters;

    public T GetSettings<T>(Guid importerId, T defaults, JsonTypeInfo<T> typeInfo,
        AssetImportSettings<T>? overrides = null)
    {
        lock (_sync)
            return overrides is not null ? overrides.Value : _settings.TryGetValue(importerId, out object? settings)
                ? (T)settings! : _sourceMeta.GetSettings(importerId, defaults, typeInfo);
    }

    /// <summary>Sets this import's effective settings before the first asset request.</summary>
    public void ConfigureSettings<T>(Guid importerId, T settings)
    {
        lock (_sync)
        {
            RequireConfiguration();
            _settings[importerId] = settings;
        }
    }

    /// <summary>Returns this source file's saved identity for an imported object.</summary>
    public Guid GetAssetId(string elementKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elementKey);
        lock (_sync)
        {
            RequireOpen();
            if (!_sourceMeta.ElementIds.TryGetValue(elementKey, out Guid id))
                _sourceMeta.ElementIds.Add(elementKey, id = Guid.NewGuid());
            return id;
        }
    }

    /// <summary>Associates actual input and asset objects after selecting their block importers.</summary>
    public TAsset AddAsset<TSource, TAsset>(TSource source, TAsset asset, string elementKey, string outputPath)
        where TSource : class where TAsset : Asset
    {
        lock (_sync)
        {
            RequireOpen();
            if (_assets.TryGetValue(asset.Id, out AssetResult? existing))
            {
                try
                {
                    if (existing.ElementKey != elementKey || existing.Content is not TAsset)
                        throw new InvalidOperationException("Different input objects or asset types share one identity.");
                    _assetIds[(source, typeof(TAsset))] = asset.Id;
                    return (TAsset)existing.Content;
                }
                finally { if (!ReferenceEquals(existing.Content, asset)) asset.Dispose(); }
            }
            try
            {
                string path = AssetProject.RequireProjectPath(ProjectRoot, outputPath);
                if (_assets.Values.Any(value => AssetProject.PathsEqual(value.Path, path)))
                    throw new InvalidOperationException("Two imported assets use the same output path.");
                AssetResult result = CreateResult(source, asset, elementKey, path);
                _assets.Add(asset.Id, result);
                _assetIds.Add((source, typeof(TAsset)), asset.Id);
                return asset;
            }
            catch { asset.Dispose(); throw; }
        }
    }

    public void EnableImporter<TSource, TAsset>(IAssetImporter<TSource> importer)
        where TSource : class where TAsset : Asset
    {
        lock (_sync)
        {
            RequireConfiguration();
            if (!_importers.TryGetValue((typeof(TSource), typeof(TAsset)), out object? values))
                _importers.Add((typeof(TSource), typeof(TAsset)), values = new List<IAssetImporter<TSource>>());
            var importers = (List<IAssetImporter<TSource>>)values;
            if (importers.Any(value => value.Id == importer.Id))
                throw new InvalidOperationException($"Importer {importer.Id} is selected twice for {typeof(TAsset)}.");
            importers.Add(importer);
        }
    }

    public bool HasImporters<TSource, TAsset>() where TSource : class where TAsset : Asset
    {
        lock (_sync)
        {
            RequireOpen();
            return _importers.TryGetValue((typeof(TSource), typeof(TAsset)), out object? values)
                && ((List<IAssetImporter<TSource>>)values).Count != 0;
        }
    }

    /// <summary>Completes and returns the asset associated with this input object.</summary>
    public async ValueTask<TAsset> GetAssetAsync<TSource, TAsset>(TSource source,
        CancellationToken cancellationToken = default) where TSource : class where TAsset : Asset
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssetResult result;
        lock (_sync)
        {
            RequireOpen();
            _cancellation.Token.ThrowIfCancellationRequested();
            _started = true;
            if (!_assetIds.TryGetValue((source, typeof(TAsset)), out Guid id))
                throw new InvalidOperationException($"No {typeof(TAsset)} is associated with this input object.");
            result = _assets[id];
            if (result.Conversions.Count == 0)
                throw new InvalidOperationException($"No importer is enabled for {typeof(TSource)} -> {typeof(TAsset)}.");
        }
        await RunAsync(result.Completion, cancellationToken).ConfigureAwait(false);
        return (TAsset)result.Content;
    }

    /// <summary>Runs the selected producer on demand; dependency order is not traversal order.</summary>
    public async ValueTask<T> GetBlockAsync<T>(Asset asset, CancellationToken cancellationToken = default)
        where T : AssetBlock, IAssetBlock<T>
        => await TryGetBlockAsync<T>(asset, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Block {T.BlockTypeId} is absent.");

    /// <summary>Runs a selected producer on demand and returns null when it supplies no block.</summary>
    public async ValueTask<T?> TryGetBlockAsync<T>(Asset asset, CancellationToken cancellationToken = default)
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
        return await asset.TryGetBlockAsync<T>(cancellationToken).ConfigureAwait(false);
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
                return _assets.Values.Where(value => value.Completion.Started)
                    .OrderBy(value => value.ElementKey, StringComparer.Ordinal)
                    .Cast<AssetOutput>().ToArray();
            }
        }
    }

    internal IReadOnlyDictionary<string, string> ReportedInputs()
    {
        lock (_sync) { RequireCompleted(); return new Dictionary<string, string>(_inputs, _inputs.Comparer); }
    }

    private AssetResult CreateResult<TSource, TAsset>(TSource source, TAsset asset, string key, string path)
        where TSource : class where TAsset : Asset
    {
        var result = new AssetResult(asset, key, path);
        if (_importers.TryGetValue((typeof(TSource), typeof(TAsset)), out object? values))
            foreach (IAssetImporter<TSource> importer in (List<IAssetImporter<TSource>>)values)
            {
                var work = new ImporterWork<TSource>(source, importer, this, asset);
                foreach (Guid block in importer.ProducedBlocks)
                {
                    if (result.Producers.TryGetValue(block, out ImportWork? other))
                        throw new InvalidOperationException($"Block {block} has competing producers {other.Name} and importer {importer.Id}.");
                    result.Producers[block] = work;
                }
                result.Conversions.Add(work);
            }
        result.Completion = new AssetWork(result, this);
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
            await work.ExecuteAsync(_cancellation.Token).ConfigureAwait(false);
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
        if (_started) throw new InvalidOperationException("Importer selections cannot change after the first asset request.");
    }
    private void RequireCompleted()
    {
        RequireOpen();
        _cancellation.Token.ThrowIfCancellationRequested();
        if (_assets.Values.Any(value => value.Completion.Started && !value.Completion.Task.IsCompletedSuccessfully))
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

    internal class AssetOutput(Asset asset, string elementKey, string path)
    {
        public Asset Asset { get; } = asset;
        public string ElementKey { get; } = elementKey;
        public string Path { get; } = path;
    }

    private sealed class AssetResult(Asset content, string elementKey, string path) : AssetOutput(content, elementKey, path)
    {
        public Asset Content => Asset;
        public Dictionary<Guid, ImportWork> Producers { get; } = [];
        public List<ImportWork> Conversions { get; } = [];
        public ImportWork Completion { get; set; } = null!;
    }

    private abstract class ImportWork(string name)
    {
        public string Name { get; } = name;
        public TaskCompletionSource Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Task => Result.Task;
        public Dictionary<ImportWork, int> Dependencies { get; } = [];
        public bool Started { get; set; }
        public abstract ValueTask ExecuteAsync(CancellationToken cancellationToken);
    }

    private sealed class ImporterWork<TSource>(TSource source, IAssetImporter<TSource> importer,
        ImportContext context, Asset asset) : ImportWork($"importer {importer.Id}") where TSource : class
    {
        public override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
        {
            var output = new ImportOutput(asset, importer.ProducedBlocks);
            try
            {
                await importer.ImportAsync(source, context, output, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                output.Finish();
                IReadOnlyList<Exception> cleanup = asset.ApplyBlocks(output.Blocks);
                output.Transfer();
                if (cleanup.Count != 0) throw new AggregateException("Imported block cleanup failed.", cleanup);
            }
            finally { await output.ReleaseAsync().ConfigureAwait(false); }
        }
    }

    private sealed class AssetWork(AssetResult asset, ImportContext context) : ImportWork($"asset {asset.Content.Id}")
    {
        public override async ValueTask ExecuteAsync(CancellationToken cancellationToken)
        {
            foreach (ImportWork work in asset.Conversions)
                await context.RunAsync(work, cancellationToken).ConfigureAwait(false);
        }
    }
}
