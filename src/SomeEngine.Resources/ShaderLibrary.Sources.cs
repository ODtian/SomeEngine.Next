using SlangShaderSharp;
using SomeEngine.Assets.Schema;
using SomeEngine.Diagnostics;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

public sealed partial class ShaderLibrary
{
    private ShaderSourceFiles _sourceFiles = null!;
    private readonly Dictionary<(string Name, string Source, string? Entry), Shader> _sourceShaders = [];

    /// <summary>Compiles generated source on the library's Slang thread and shares equal requests.</summary>
    public ValueTask<Shader> CreateAsync(string name, string sourceName, string source,
        string? entryPoint = null, CancellationToken cancellationToken = default) => new(RunAsync(() =>
    {
        var key = (sourceName, source, entryPoint);
        if (_sourceShaders.TryGetValue(key, out Shader? existing)) return existing;
        string path = Path.GetFileNameWithoutExtension(sourceName) + "_" + _sourceShaders.Count + ".slang";
        string directory = _settings.SearchPaths?.FirstOrDefault() ?? Directory.GetCurrentDirectory();
        _sourceFiles.AddSource(Path.Combine(directory, path), source);
        using var block = new ShaderSourceBlock { SourceNames = [path] };
        Shader shader = Compile(block, [path], [null], name, default, generateCode: true,
            saveSources: false, cancellationToken, entryPoint).Shader;
        _sourceShaders.Add(key, shader);
        return shader;
    }, cancellationToken));

    internal async ValueTask<ShaderCompilation> CompileAsync(ShaderSourceBlock source, string name,
        ReadOnlyMemory<SpecializationArg> specializations, bool generateCode, bool saveSources,
        CancellationToken cancellationToken, string? entryPoint = null,
        ShaderInputSource[]? assetInputs = null, IReadOnlyList<Shader>? dependencies = null, Shader? inputShader = null)
    {
        using ShaderSourceBlock copy = source.Duplicate();
        string[] paths = copy.SourceNames.Select(NormalizeSourceName).ToArray();
        var savedSources = new ReadOnlyMemory<byte>?[paths.Length];
        if (!saveSources)
            for (int index = 0; index < paths.Length; index++)
            {
                ulong key = ShaderInputBlock.SerializedSourceKey(index);
                if (copy.ContentKeys.Contains(key))
                    savedSources[index] = await copy.ReadBytesAsync(key, cancellationToken).ConfigureAwait(false);
            }
        return await RunAsync(() =>
        {
            if (assetInputs is not null) RestoreAssetInputs(assetInputs, dependencies ?? [], inputShader);
            else if (inputShader is not null) RestoreShaderInputs([inputShader, .. (dependencies ?? [])]);
            else if (dependencies is not null)
            {
                if (paths.Any(path => _inputs.ContainsKey(Path.GetFileNameWithoutExtension(path)))) RefreshSession();
                RestoreShaderInputs(dependencies);
            }
            return Compile(copy, paths, savedSources, name, specializations,
                generateCode, saveSources, cancellationToken, entryPoint);
        }, cancellationToken).ConfigureAwait(false);
    }

    private ShaderCompilation Compile(ShaderSourceBlock copy, string[] paths,
        ReadOnlyMemory<byte>?[] savedSources, string name, ReadOnlyMemory<SpecializationArg> specializations,
        bool generateCode, bool saveSources, CancellationToken cancellationToken, string? entryPoint)
    {
        var sourceUnits = new List<IModule>(paths.Length);
        var entries = new List<IEntryPoint>();
        var serializedSources = new List<ReadOnlyMemory<byte>>(saveSources ? paths.Length : 0);
        foreach (int index in Enumerable.Range(0, paths.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string inputName = Path.GetFileNameWithoutExtension(paths[index]);
            IModule? sourceUnit;
            if (!_inputs.TryGetValue(inputName, out ShaderInput? input))
            {
                using var loadTrace = EngineTrace.Begin(LoadInputTrace);
                ISlangBlob? diagnostics;
                if (savedSources[index] is { } serialized)
                    sourceUnit = _session.LoadModuleFromIRBlob(inputName, paths[index],
                        Slang.CreateBlob(serialized.Span), out diagnostics);
                else
                {
                    string directory = _settings.SearchPaths?.FirstOrDefault() ?? Directory.GetCurrentDirectory();
                    string path = Path.GetFullPath(Path.Combine(directory, paths[index]));
                    if (_sourceFiles.LoadFile(path, out ISlangBlob source).Failed)
                        throw new FileNotFoundException("Shader source file could not be read.", path);
                    sourceUnit = _session.LoadModuleFromSource(inputName, path, source, out diagnostics);
                }
                if (sourceUnit is null)
                    throw new InvalidDataException($"Slang could not load source '{paths[index]}': {DiagnosticText(diagnostics)}");
                SlangShaderData.RetainSession(_global, _session, sourceUnit);
                _inputs.Add(inputName, new(sourceUnit, sourceUnit, Guid.Empty));
            }
            else sourceUnit = input.Source;
            sourceUnits.Add(sourceUnit);
            if (saveSources)
            {
                SlangResult serialized = sourceUnit.Serialize(out ISlangBlob data);
                if (serialized.Failed || data.GetBufferSize() == 0)
                    throw new InvalidDataException($"Slang could not serialize source '{paths[index]}'.");
                serializedSources.Add(data.Buffer.ToArray());
            }
        }
        if (copy.SourceOnly)
            return new(Shader.CreateFromSlang(name, _global, _session, sourceUnits, [], sourceUnits[0],
                _settings.Targets!, generateCode: false), serializedSources, _sourceFiles.Finish());
        foreach (IModule sourceUnit in sourceUnits)
        {
            for (int entryIndex = 0; entryIndex < sourceUnit.GetDefinedEntryPointCount(); entryIndex++)
            {
                SlangResult entryResult = sourceUnit.GetDefinedEntryPoint(entryIndex, out IEntryPoint entry);
                if (entryResult.Failed || entry is null)
                    throw new InvalidDataException("Slang could not return a declared shader entry point.");
                SlangShaderData.RetainSession(_global, _session, entry);
                if (entryPoint is null || entry.GetFunctionReflection().Name == entryPoint)
                    entries.Add(entry);
            }
        }

        if (entries.Count == 0)
            throw new InvalidDataException("The shader source block exposes no entry points.");

        var components = new List<IComponentType>(sourceUnits);
        foreach (IEntryPoint entry in entries)
        {
            IComponentType component = entry;
            if (entryPoint is not null && !specializations.IsEmpty)
                component = Specialize(_session, entry, specializations);
            components.Add(component);
        }
        SlangResult combineResult = _session.CreateCompositeComponentType(
            components.ToArray(),
            out IComponentType combinedCode,
            out ISlangBlob? combineDiagnostics);
        string combineText = DiagnosticText(combineDiagnostics);
        if (combineResult.Failed || combinedCode is null)
            throw new InvalidDataException("Slang composition failed: " + combineText);
        SlangShaderData.RetainSession(_global, _session, combinedCode);

        IComponentType codeToLink = entryPoint is null && !specializations.IsEmpty
            ? Specialize(_session, combinedCode, specializations)
            : combinedCode;

        using var linkTrace = EngineTrace.Begin(LinkTrace);
        SlangResult linkResult = codeToLink.Link(
            out IComponentType linkedCode,
            out ISlangBlob? linkDiagnostics);
        string linkText = DiagnosticText(linkDiagnostics);
        if (linkResult.Failed || linkedCode is null)
            throw new InvalidDataException("Slang link failed: " + linkText);
        SlangShaderData.RetainSession(_global, _session, linkedCode);

        cancellationToken.ThrowIfCancellationRequested();
        Shader shader = Shader.CreateFromSlang(
            name,
            _global,
            _session,
            sourceUnits,
            entries,
            linkedCode,
            _settings.Targets!,
            generateCode,
            generateLibraryCode: entryPoint is null);
        return new(shader, serializedSources, _sourceFiles.Finish())
        {
            Specializations = generateCode && entryPoint is null ? copy.ReadSpecializations(shader) : [],
        };
    }

    private IComponentType Specialize(ISession session, IComponentType code, ReadOnlyMemory<SpecializationArg> arguments)
    {
        SlangResult result = code.Specialize(arguments.ToArray(), out IComponentType specialized,
            out ISlangBlob? diagnostics);
        if (result.Failed || specialized is null)
            throw new InvalidDataException("Slang specialization failed: " + DiagnosticText(diagnostics));
        SlangShaderData.RetainSession(_global, session, specialized);
        return specialized;
    }

    private static string NormalizeSourceName(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName) || Path.IsPathRooted(sourceName))
            throw new InvalidDataException("Shader source names must be relative.");
        string normalized = sourceName.Replace('\\', '/');
        string directory = Path.GetFullPath(".");
        return Path.GetRelativePath(directory, Path.GetFullPath(Path.Combine(directory, normalized))).Replace('\\', '/');
    }

    private static string DiagnosticText(ISlangBlob? diagnostics)
        => diagnostics is null || diagnostics.GetBufferSize() == 0
            ? "no diagnostics"
            : diagnostics.AsString;
}
