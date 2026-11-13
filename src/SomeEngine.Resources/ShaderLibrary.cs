using System.Collections.Concurrent;
using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Diagnostics;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

public readonly record struct ShaderKey(Guid AssetId, ulong ContentHash);

/// <summary>Holds the shader compiler, loaded inputs and completed shaders for one target.</summary>
public sealed partial class ShaderLibrary : IDisposable
{
    private sealed record ShaderInput(IModule Source, IModule Declaration, Guid DataVersion);
    private sealed record ShaderAssetInputs(Shader Shader, ShaderInputVersion[] InputVersions);

    private static readonly TraceSite CreateSessionTrace = new("shader.Slang", "CreateSession");
    private static readonly TraceSite LoadInputTrace = new("shader.Slang", "LoadModule");
    private static readonly TraceSite LinkTrace = new("shader.Slang", "Link");
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;
    private readonly object _gate = new();
    private readonly Dictionary<string, ShaderInput> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<ShaderKey, ShaderAssetInputs> _shaders = [];
    private readonly Dictionary<ShaderKey, Task<ShaderAssetInputs>> _loads = [];
    private IGlobalSession _global = null!;
    private ISession _session = null!;
    private SessionDesc _settings;
    private bool _closed;

    public ShaderLibrary(SlangCompileTarget target) : this(null, default, target) { }

    public ShaderLibrary(IGlobalSession global, SessionDesc settings)
        : this(global, settings, default) { }

    private ShaderLibrary(IGlobalSession? global, SessionDesc settings, SlangCompileTarget target)
    {
        _thread = new Thread(ProcessRequests) { IsBackground = true, Name = "Slang" };
        _thread.Start();
        try
        {
            RunAsync(() =>
            {
                _global = global ?? Shader.CreateSlangGlobalSession();
                _settings = global is null ? CreateSettings(target) : settings;
                _sourceFiles = new(_settings.FileSystem);
                _settings.FileSystem = _sourceFiles;
                using var trace = EngineTrace.Begin(CreateSessionTrace);
                SlangResult result = _global.CreateSession(_settings, out _session);
                if (result.Failed || _session is null)
                    throw new InvalidDataException($"Slang session creation failed with {result}.");
                return true;
            }).GetAwaiter().GetResult();
        }
        catch
        {
            _work.CompleteAdding();
            _thread.Join();
            _work.Dispose();
            throw;
        }
    }

    internal SessionDesc Settings => _settings;

    private SessionDesc CreateSettings(SlangCompileTarget target) => new()
    {
        Targets = [new TargetDesc { Format = target,
            Profile = _global.FindProfile(target == SlangCompileTarget.Dxil ? "sm_6_6" : "glsl_460") }],
        DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
        CompilerOptionEntries =
        [
            new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(0, 0)),
            new(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
            new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
            new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0)),
        ],
    };

    private void ProcessRequests()
    {
        foreach (Action work in _work.GetConsumingEnumerable()) work();
    }

    private Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.SetResult(action());
            }
            catch (OperationCanceledException error) { result.SetCanceled(error.CancellationToken); }
            catch (Exception error) { result.SetException(error); }
        });
        return result.Task;
    }

    /// <summary>Starts a fresh compiler session after rebuilt input assets are ready.</summary>
    public ValueTask RefreshAsync(CancellationToken cancellationToken = default) => new(RunAsync(() =>
    {
        RefreshSession();
        return true;
    }, cancellationToken));

    private void RefreshSession()
    {
        _inputs.Clear();
        _sourceShaders.Clear();
        _compositions.Clear();
        using var trace = EngineTrace.Begin(CreateSessionTrace);
        SlangResult result = _global.CreateSession(_settings, out _session);
        if (result.Failed || _session is null)
            throw new InvalidDataException($"Slang session creation failed with {result}.");
    }

    public bool TryGetKey(Shader shader, out ShaderKey key)
    {
        lock (_gate)
        {
            foreach (var pair in _shaders)
                if (ReferenceEquals(pair.Value.Shader, shader)) { key = pair.Key; return true; }
            key = default;
            return false;
        }
    }

    public void Replace(ShaderKey previous, Shader next)
    {
        lock (_gate) _shaders[previous] = _shaders.Values.First(input => ReferenceEquals(input.Shader, next));
    }

    private bool ContainsInputs(Shader shader)
    {
        foreach (IModule declaration in shader.SlangDeclarationSources)
            if (!_inputs.TryGetValue(declaration.GetName(), out ShaderInput? input)
                || !MatchesDeclaration(input, declaration)) return false;
        return true;
    }

    private static bool MatchesDeclaration(ShaderInput input, IModule declaration) =>
        ReferenceEquals(input.Source, declaration) || ReferenceEquals(input.Declaration, declaration);

    private void RestoreAssetInputs(ShaderInputSource[] inputs, IReadOnlyList<Shader> dependencies, Shader? cached)
    {
        bool inputConflict = dependencies.SelectMany(shader => shader.SlangDeclarationSources)
            .Any(declaration => _inputs.TryGetValue(declaration.GetName(), out ShaderInput? loaded)
                && !MatchesDeclaration(loaded, declaration));
        for (int index = 0; index < inputs.Length && !inputConflict; index++)
        {
            ShaderInputSource input = inputs[index];
            if (_inputs.TryGetValue(Path.GetFileNameWithoutExtension(input.Name), out ShaderInput? loaded)
                && loaded.DataVersion != input.Version.DataVersion
                && (cached is null || !MatchesDeclaration(loaded, cached.GetSlangSourceUnit(index)))) inputConflict = true;
        }
        if (inputConflict) RefreshSession();

        foreach (Shader dependency in dependencies) LoadInputs(dependency);
        for (int index = 0; index < inputs.Length; index++)
        {
            ShaderInputSource input = inputs[index];
            string name = Path.GetFileNameWithoutExtension(input.Name);
            IModule? declaration = cached?.GetSlangSourceUnit(index);
            if (_inputs.TryGetValue(name, out ShaderInput? existing))
            {
                _inputs[name] = existing with { Declaration = declaration ?? existing.Declaration, DataVersion = input.Version.DataVersion };
                continue;
            }
            using var trace = EngineTrace.Begin(LoadInputTrace);
            IModule source = _session.LoadModuleFromIRBlob(name, input.Name, Slang.CreateBlob(input.Serialized.Span),
                out ISlangBlob? diagnostics) ?? throw new InvalidDataException("Slang input load failed: " + DiagnosticText(diagnostics));
            SlangShaderData.RetainSession(_global, _session, source);
            _inputs.Add(name, new(source, declaration ?? source, input.Version.DataVersion));
        }
    }

    private void LoadInputs(Shader shader)
    {
        foreach (IModule input in shader.SlangDeclarationSources)
        {
            string name = input.GetName();
            if (_inputs.ContainsKey(name)) continue;
            IModule loaded;
            if (ReferenceEquals(shader.SlangCode.Session, _session)) loaded = input;
            else
            {
                SlangResult serialized = input.Serialize(out ISlangBlob bytes);
                if (serialized.Failed) throw new InvalidDataException($"Slang input serialization failed: {serialized}.");
                using var trace = EngineTrace.Begin(LoadInputTrace);
                loaded = _session.LoadModuleFromIRBlob(name, input.GetFilePath(), bytes, out ISlangBlob? diagnostics)
                    ?? throw new InvalidDataException("Slang input reload failed: " + DiagnosticText(diagnostics));
                SlangShaderData.RetainSession(_global, _session, loaded);
            }
            _inputs.Add(name, new(loaded, input, Guid.Empty));
        }
    }

    private void RestoreShaderInputs(IReadOnlyList<Shader> shaders)
    {
        if (shaders.SelectMany(shader => shader.SlangDeclarationSources)
            .Any(declaration => _inputs.TryGetValue(declaration.GetName(), out ShaderInput? input)
                && !MatchesDeclaration(input, declaration))) RefreshSession();
        foreach (Shader shader in shaders) LoadInputs(shader);
    }

    public void Dispose()
    {
        Task[] loads;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            loads = _loads.Values.ToArray();
        }
        Task.WhenAll(loads).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
        _work.CompleteAdding();
        _thread.Join();
        _work.Dispose();
        lock (_gate)
        {
            _loads.Clear();
            _shaders.Clear();
        }
        _inputs.Clear();
        _sourceShaders.Clear();
        _compositions.Clear();
    }
}
