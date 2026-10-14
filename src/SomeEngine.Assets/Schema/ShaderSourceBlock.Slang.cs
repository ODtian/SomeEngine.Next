using System.Runtime.CompilerServices;
using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

public sealed partial class ShaderSourceBlock
{
    private static readonly ConditionalWeakTable<IGlobalSession, SemaphoreSlim> s_slangFrontEnds = new();

    /// <summary>
    /// Acquires the selected source files, compiles them with Slang, and returns a resident shader.
    /// The returned shader no longer depends on this block or the source files.
    /// </summary>
    public async ValueTask<Shader> CreateAsync(
        IGlobalSession slang,
        SessionDesc sessionDescription,
        string name,
        ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default) =>
        (await CreateCoreAsync(
            slang,
            sessionDescription,
            name,
            specializations,
            generateCode: true,
            saveSources: false,
            cancellationToken).ConfigureAwait(false)).Shader;

    /// <summary>Compiles the current source files and returns their serialized Slang data and read-byte hashes.</summary>
    public ValueTask<ShaderCompilation> CompileAsync(IGlobalSession slang, SessionDesc sessionDescription,
        string name, CancellationToken cancellationToken = default)
        => CreateCoreAsync(slang, sessionDescription, name, default, generateCode: true,
            saveSources: true, cancellationToken);

    internal async ValueTask<Shader> CreateUsingSavedCodeAsync(
        IGlobalSession slang,
        SessionDesc sessionDescription,
        string name,
        ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default) =>
        (await CreateCoreAsync(
            slang,
            sessionDescription,
            name,
            specializations,
            generateCode: false,
            saveSources: false,
            cancellationToken).ConfigureAwait(false)).Shader;

    internal async ValueTask<Shader> CreateSelectedAsync(
        IReadOnlyList<ShaderInputSource> inputs,
        IGlobalSession slang,
        SessionDesc sessionDescription,
        string name,
        ReadOnlyMemory<SpecializationArg> specializations,
        bool generateCode,
        CancellationToken cancellationToken) =>
        (await CreateCoreAsync(slang, sessionDescription, name, specializations,
            generateCode, saveSources: false, cancellationToken, inputs).ConfigureAwait(false)).Shader;

    private async ValueTask<ShaderCompilation> CreateCoreAsync(
        IGlobalSession slang,
        SessionDesc sessionDescription,
        string name,
        ReadOnlyMemory<SpecializationArg> specializations,
        bool generateCode,
        bool saveSources,
        CancellationToken cancellationToken,
        IReadOnlyList<ShaderInputSource>? inputs = null)
    {
        ArgumentNullException.ThrowIfNull(slang);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (sessionDescription.Targets is not { Length: > 0 } targets)
            throw new ArgumentException("Creating a shader requires at least one Slang target.", nameof(sessionDescription));

#if !DEBUG
        // Saved GPU code already carries the build settings. This session restores
        // reflection and entry resource usage with Slang's required lowering passes.
        if (!generateCode)
            sessionDescription.CompilerOptionEntries =
            [
                .. sessionDescription.CompilerOptionEntries ?? [],
                new(CompilerOptionName.DisableNonEssentialValidations, CompilerOptionValue.FromInt(1, 0)),
                new(CompilerOptionName.MinimumSlangOptimization, CompilerOptionValue.FromInt(1, 0)),
                new(CompilerOptionName.Optimization, CompilerOptionValue.FromInt((int)SlangOptimizationLevel.None, 0)),
            ];
#endif

        using ShaderSourceBlock copy = Duplicate();
        SemaphoreSlim frontEnd = s_slangFrontEnds.GetValue(
            slang,
            static _ => new SemaphoreSlim(1, 1));
        await frontEnd.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (inputs is null && copy.SourceNames.Count == 0)
                throw new InvalidDataException("The shader source block contains no source files.");

            string[] paths = inputs is null
                ? copy.SourceNames.Select(NormalizeSourceName).ToArray()
                : inputs.Select(static input => input.Name).ToArray();
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
                throw new InvalidDataException("The shader source block contains duplicate source files.");

            var sourceFiles = new ShaderSourceFiles(sessionDescription.FileSystem);
            sessionDescription.FileSystem = sourceFiles;
            SlangResult sessionResult = slang.CreateSession(sessionDescription, out ISession session);
            if (sessionResult.Failed || session is null)
                throw new InvalidDataException($"Slang session creation failed with {sessionResult}.");

            var sourceUnits = new List<IModule>(paths.Length);
            var entries = new List<IEntryPoint>();
            var serializedSources = new List<ReadOnlyMemory<byte>>(saveSources ? paths.Length : 0);
            for (int sourceIndex = 0; sourceIndex < paths.Length; sourceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong serializedKey = SerializedSourceKey(sourceIndex);
                IModule? sourceUnit;
                ISlangBlob? diagnostics;
                if (!saveSources && (inputs is not null || copy.ContentKeys.Contains(serializedKey)))
                {
                    ReadOnlyMemory<byte> serialized = inputs is not null ? inputs[sourceIndex].Serialized
                        : await copy.ReadBytesAsync(serializedKey, cancellationToken).ConfigureAwait(false);
                    ISlangBlob source = Slang.CreateBlob(serialized.Span);
                    sourceUnit = session.LoadModuleFromIRBlob(Path.GetFileNameWithoutExtension(paths[sourceIndex]),
                        paths[sourceIndex], source, out diagnostics);
                }
                else
                {
                    string directory = sessionDescription.SearchPaths?.FirstOrDefault() ?? Directory.GetCurrentDirectory();
                    string path = Path.GetFullPath(Path.Combine(directory, paths[sourceIndex]));
                    if (sourceFiles.LoadFile(path, out ISlangBlob source).Failed)
                        throw new FileNotFoundException("Shader source file could not be read.", path);
                    sourceUnit = session.LoadModuleFromSource(Path.GetFileNameWithoutExtension(path), path,
                        source, out diagnostics);
                    if (saveSources && sourceUnit is not null)
                    {
                        SlangResult serialized = sourceUnit.Serialize(out ISlangBlob data);
                        if (serialized.Failed || data.GetBufferSize() == 0)
                            throw new InvalidDataException($"Slang could not serialize source '{path}'.");
                        serializedSources.Add(data.Buffer.ToArray());
                    }
                }
                string diagnosticText = DiagnosticText(diagnostics);
                if (sourceUnit is null)
                {
                    throw new InvalidDataException(
                        $"Slang could not load source '{paths[sourceIndex]}': {diagnosticText}");
                }
                sourceUnits.Add(sourceUnit);
            }

            foreach (IModule sourceUnit in sourceUnits)
            {
                for (int entryIndex = 0; entryIndex < sourceUnit.GetDefinedEntryPointCount(); entryIndex++)
                {
                    SlangResult entryResult = sourceUnit.GetDefinedEntryPoint(entryIndex, out IEntryPoint entry);
                    if (entryResult.Failed || entry is null)
                        throw new InvalidDataException("Slang could not return a declared shader entry point.");
                    entries.Add(entry);
                }
            }

            if (entries.Count == 0)
                throw new InvalidDataException("The shader source block exposes no entry points.");

            IComponentType[] components = sourceUnits
                .Cast<IComponentType>()
                .Concat(entries)
                .ToArray();
            SlangResult combineResult = session.CreateCompositeComponentType(
                components,
                out IComponentType combinedCode,
                out ISlangBlob? combineDiagnostics);
            string combineText = DiagnosticText(combineDiagnostics);
            if (combineResult.Failed || combinedCode is null)
                throw new InvalidDataException("Slang composition failed: " + combineText);

            IComponentType codeToLink = combinedCode;
            if (!specializations.IsEmpty)
            {
                SpecializationArg[] arguments = specializations.ToArray();
                SlangResult specializationResult = combinedCode.Specialize(
                    arguments,
                    out IComponentType specializedCode,
                    out ISlangBlob? specializationDiagnostics);
                string specializationText = DiagnosticText(specializationDiagnostics);
                if (specializationResult.Failed || specializedCode is null)
                    throw new InvalidDataException("Slang specialization failed: " + specializationText);
                codeToLink = specializedCode;
            }

            SlangResult linkResult = codeToLink.Link(
                out IComponentType linkedCode,
                out ISlangBlob? linkDiagnostics);
            string linkText = DiagnosticText(linkDiagnostics);
            if (linkResult.Failed || linkedCode is null)
                throw new InvalidDataException("Slang link failed: " + linkText);

            cancellationToken.ThrowIfCancellationRequested();
            Shader shader = Shader.CreateFromSlang(
                name,
                slang,
                session,
                sourceUnits,
                entries,
                linkedCode,
                targets,
                generateCode);
            return new(shader, serializedSources, sourceFiles.Finish());
        }
        finally
        {
            frontEnd.Release();
        }
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
