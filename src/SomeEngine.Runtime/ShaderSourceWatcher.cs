using System.Collections.Concurrent;
using System.Text.Json;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Runtime;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ShaderSourceDirectoryAttribute(string directory) : Attribute
{
    public string Directory { get; } = directory;
}

/// <summary>Builds changed shader assets in the background and publishes them at a frame boundary.</summary>
public sealed class ShaderSourceWatcher : IDisposable
{
    private readonly ShaderLibrary _shaders;
    private readonly SlangCompileTarget _target;
    private readonly string[] _roots;
    private readonly string _contentDirectory;
    private readonly FileSystemWatcher[] _watchers;
    private readonly ConcurrentDictionary<string, byte> _changes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<ShaderReplacement> _replacements = new();
    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly object _gate = new();
    private Task _build = Task.CompletedTask;
    private bool _disposed;
    private bool _building;

    private sealed record Definition(string Root, string Path, Guid Id, SourceBuildRecord Build);
    private sealed record ShaderReplacement(ShaderKey Key, Shader Previous, Shader Next);

    public ShaderSourceWatcher(string contentDirectory, string sourceDirectory, ShaderLibrary shaders,
        SlangCompileTarget target)
    {
        _contentDirectory = Path.GetFullPath(contentDirectory);
        _shaders = shaders;
        _target = target;
        var roots = new List<string> { Path.GetFullPath(sourceDirectory) };
        if (!StringComparer.OrdinalIgnoreCase.Equals(_contentDirectory, roots[0])) roots.Add(_contentDirectory);
        foreach (string root in roots.ToArray())
        {
            string file = Path.Combine(root, "Library", "AssetManifest", EngineShaderContent.ContentDirectoriesFileName);
            if (!File.Exists(file)) continue;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(file));
            foreach (JsonElement value in document.RootElement.EnumerateArray())
                roots.Add(Path.GetFullPath(value.GetString()!, root));
        }
        _roots = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _watchers = _roots.Select(root =>
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Renamed += Changed;
            watcher.EnableRaisingEvents = true;
            return watcher;
        }).ToArray();
    }

    private void Changed(object sender, FileSystemEventArgs change)
    {
        string extension = Path.GetExtension(change.FullPath);
        if (extension is not (".slang" or ".shader")) return;
        _changes[Path.GetFullPath(change.FullPath)] = 0;
        lock (_gate)
            if (!_disposed && !_building)
            {
                _building = true;
                _build = Task.Run(BuildChangesAsync);
            }
    }

    private async Task BuildChangesAsync()
    {
        while (true)
        {
            await Task.Delay(100).ConfigureAwait(false);
            string[] changes;
            lock (_gate)
            {
                if (_disposed || _changes.IsEmpty) { _building = false; return; }
                changes = _changes.Keys.ToArray();
                foreach (string path in changes) _changes.TryRemove(path, out _);
            }
            try { await RebuildAsync(changes).ConfigureAwait(false); }
            catch (Exception error) { Console.Error.WriteLine(error.Message); _failures.Enqueue(error); }
        }
    }

    private async Task RebuildAsync(string[] changes)
    {
        Definition[] definitions = ReadDefinitions();
        var affected = definitions.Where(definition => changes.Any(path => SamePath(path, definition.Path)) ||
            definition.Build.Inputs.Any(input => changes.Any(path =>
                SamePath(path, Path.GetFullPath(input.RelativePath, definition.Root)))))
            .Select(definition => definition.Id).ToHashSet();
        int count;
        do
        {
            count = affected.Count;
            foreach (Definition definition in definitions)
                if (definition.Build.AssetInputs.Any(affected.Contains)) affected.Add(definition.Id);
        } while (affected.Count != count);
        Definition[] rebuilding = definitions.Where(definition => affected.Contains(definition.Id)).ToArray();
        if (rebuilding.Length == 0) return;
        var previous = new Dictionary<Guid, (ShaderKey Key, Shader Shader)>();
        await using (AssetLoader files = OpenFiles())
            foreach (Definition definition in rebuilding)
            {
                Shader shader = await _shaders.GetAsync(files, new AssetId<ShaderAsset>(definition.Id)).ConfigureAwait(false);
                if (_shaders.TryGetKey(shader, out ShaderKey key)) previous.Add(definition.Id, (key, shader));
            }
        string[] includes = _roots.Select(root => Path.Combine(root, "assets", "Shaders")).ToArray();
        foreach (string root in _roots.Reverse())
        {
            string[] sources = rebuilding.Where(definition => SamePath(root, definition.Root)).Select(definition => definition.Path).ToArray();
            if (sources.Length == 0) continue;
            var project = new AssetProject(root, [], shaderIncludeDirectories: includes, shaderTargets: [_target]);
            await project.BuildAllAsync(sources, Math.Max(1, Environment.ProcessorCount / 2)).ConfigureAwait(false);
        }
        await _shaders.RefreshAsync().ConfigureAwait(false);
        await using (AssetLoader files = OpenFiles())
            foreach (Definition definition in rebuilding)
            {
                Shader next = await _shaders.GetAsync(files, new AssetId<ShaderAsset>(definition.Id)).ConfigureAwait(false);
                var old = previous[definition.Id];
                if (!ReferenceEquals(old.Shader, next)) _replacements.Enqueue(new(old.Key, old.Shader, next));
            }
    }

    private AssetLoader OpenFiles() => new(_roots.Select(root => new LooseAssetStorage(root,
        AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest")))));

    private Definition[] ReadDefinitions()
    {
        var definitions = new Dictionary<Guid, Definition>();
        foreach (string root in _roots)
        {
            AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest"));
            foreach (AssetEntry asset in manifest.List(ShaderAsset.FileType))
            {
                if (asset.DefinitionPath.Length == 0) continue;
                string path = Path.GetFullPath(asset.DefinitionPath, root);
                if (!File.Exists(path) || !File.Exists(SourceMeta.GetBuildPath(path))) continue;
                definitions.TryAdd(asset.AssetGuid, new(root, path, asset.AssetGuid, SourceMeta.LoadBuild(path)));
            }
        }
        return definitions.Values.ToArray();
    }

    public void Apply(Action<Shader, Shader> replace, Action<Exception>? buildFailed = null)
    {
        while (_failures.TryDequeue(out Exception? error)) buildFailed?.Invoke(error);
        while (_replacements.TryDequeue(out ShaderReplacement? change))
        {
            _shaders.Replace(change.Key, change.Next);
            replace(change.Previous, change.Next);
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        foreach (FileSystemWatcher watcher in _watchers) watcher.Dispose();
        _build.GetAwaiter().GetResult();
    }

    private static bool SamePath(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Equals(first, second);
}
