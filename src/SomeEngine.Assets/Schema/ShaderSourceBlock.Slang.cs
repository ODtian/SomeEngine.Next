using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Resources;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

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
        CancellationToken cancellationToken = default)
    {
        using ShaderSourceBlock copy = Duplicate();
        var sourceFiles = new ShaderSourceFiles(sessionDescription.FileSystem);
        ShaderCompilation compiled = await copy.CreateCoreAsync(slang, sessionDescription, name, specializations,
            generateCode: true, saveSources: false, cancellationToken, sourceFiles: sourceFiles).ConfigureAwait(false);
        if (!specializations.IsEmpty || compiled.Specializations.Count == 0) return compiled.Shader;

        var variants = new List<(string EntryPoint, string[] Arguments, Shader Shader)>(compiled.Specializations.Count);
        foreach (ShaderSpecialization specialization in compiled.Specializations)
        {
            Shader variant = (await CompileUsingLoadedSourcesAsync(slang, compiled.Shader, sourceFiles,
                specialization.EntryPoint, specialization.ToSlangArguments(), cancellationToken)
                .ConfigureAwait(false)).Shader;
            variants.Add((specialization.EntryPoint, specialization.Arguments.ToArray(), variant));
        }
        return compiled.Shader.WithSpecializations(variants);
    }

    /// <summary>Compiles the current source files and returns their serialized Slang data and read-byte hashes.</summary>
    public ValueTask<ShaderCompilation> CompileAsync(IGlobalSession slang, SessionDesc sessionDescription,
        string name, CancellationToken cancellationToken = default)
        => CompileAsync(slang, sessionDescription, name, specializations: default, cancellationToken);

    public ValueTask<ShaderCompilation> CompileAsync(IGlobalSession slang, SessionDesc sessionDescription,
        string name, ReadOnlyMemory<SpecializationArg> specializations, CancellationToken cancellationToken = default)
        => CreateCoreAsync(slang, sessionDescription, name, specializations, generateCode: true,
            saveSources: true, cancellationToken);

    public ValueTask<ShaderCompilation> CompileAsync(IGlobalSession slang, SessionDesc sessionDescription,
        string name, string entryPoint, ReadOnlyMemory<SpecializationArg> specializations,
        CancellationToken cancellationToken = default)
        => CreateCoreAsync(slang, sessionDescription, name, specializations, generateCode: true,
            saveSources: true, cancellationToken, entryPoint);

    internal ValueTask<ShaderCompilation> CompileAsync(IGlobalSession slang, SessionDesc sessionDescription,
        string name, ShaderSourceFiles sourceFiles, CancellationToken cancellationToken)
        => CreateCoreAsync(slang, sessionDescription, name, default, generateCode: true,
            saveSources: true, cancellationToken, sourceFiles: sourceFiles);

    // A specialization shares its base compilation's session, loaded sources and input tracking.
    internal static async ValueTask<ShaderCompilation> CompileUsingLoadedSourcesAsync(
        IGlobalSession slang, Shader shader, ShaderSourceFiles sourceFiles, string entryPoint,
        ReadOnlyMemory<SpecializationArg> specializations, CancellationToken cancellationToken)
    {
        SemaphoreSlim frontEnd = s_slangFrontEnds.GetValue(slang, static _ => new SemaphoreSlim(1, 1));
        await frontEnd.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IModule[] sourceUnits = Enumerable.Range(0, shader.SlangSourceUnitCount)
                .Select(shader.GetSlangSourceUnit).ToArray();
            IEntryPoint[] entries = Enumerable.Range(0, shader.SlangEntryCount)
                .Select(shader.GetSlangEntryPoint)
                .Where(entry => entry.GetFunctionReflection().Name == entryPoint).ToArray();
            TargetDesc[] targets = Enumerable.Range(0, shader.SlangTargetCount)
                .Select(shader.GetSlangTarget).ToArray();
            Shader variant = CompileShader(slang, shader.SlangSession, shader.Name,
                sourceUnits, entries, targets, specializations, generateCode: true, cancellationToken, entryPoint);
            return new(variant, [], sourceFiles.Finish());
        }
        finally
        {
            frontEnd.Release();
        }
    }

    internal static async ValueTask<(Shader[] Shaders, ShaderSourceFile[] Files)> CompileUsingLoadedSourcesBatchAsync(
        IGlobalSession slang, Shader shader, ShaderSourceFiles sourceFiles,
        IReadOnlyList<(string EntryPoint, ReadOnlyMemory<SpecializationArg> Arguments)> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ([], []);
        }
        SemaphoreSlim frontEnd = s_slangFrontEnds.GetValue(slang, static _ => new SemaphoreSlim(1, 1));
        var roots = new List<IComponentType>();
        await frontEnd.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IModule[] sourceUnits = Enumerable.Range(0, shader.SlangSourceUnitCount)
                .Select(shader.GetSlangSourceUnit).ToArray();
            IEntryPoint[] allEntries = Enumerable.Range(0, shader.SlangEntryCount)
                .Select(shader.GetSlangEntryPoint).ToArray();
            TargetDesc[] targets = Enumerable.Range(0, shader.SlangTargetCount)
                .Select(shader.GetSlangTarget).ToArray();
            var inputs = new List<(IReadOnlyList<IEntryPoint> Entries, IComponentType LinkedCode)>(requests.Count);
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IEntryPoint[] entries = allEntries
                    .Where(entry => entry.GetFunctionReflection().Name == request.EntryPoint).ToArray();
                IComponentType linked = LinkShaderForBatch(shader.SlangSession, sourceUnits, entries,
                    request.Arguments, cancellationToken, request.EntryPoint, roots);
                inputs.Add((entries, linked));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Shader[] variants = Shader.CreateFromSlangBatch(shader.Name, slang, shader.SlangSession,
                sourceUnits, inputs, targets, cancellationToken);
            sourceFiles.ThrowIfInputChanged();
            cancellationToken.ThrowIfCancellationRequested();
            return (variants, sourceFiles.Finish());
        }
        catch
        {
            // The batch factory has drained all native calls before any failure reaches here.
            if (!cancellationToken.IsCancellationRequested) sourceFiles.ThrowIfInputChanged();
            throw;
        }
        finally
        {
            GC.KeepAlive(roots);
            GC.KeepAlive(shader);
            GC.KeepAlive(sourceFiles);
            frontEnd.Release();
        }
    }

    // SaveCode must not issue shared-graph queries after the frontend gate is released.
    // Capture the original per-entry predicate exactly, without relying on linked-code flags.
    internal static async ValueTask<bool[]> ReadEntrySpecializationFlagsAsync(
        IGlobalSession slang, Shader shader, CancellationToken cancellationToken)
    {
        SemaphoreSlim frontEnd = s_slangFrontEnds.GetValue(slang, static _ => new SemaphoreSlim(1, 1));
        await frontEnd.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool[] flags = new bool[shader.SlangEntryCount];
            for (int entry = 0; entry < flags.Length; entry++)
                flags[entry] = shader.GetSlangEntryPoint(entry).GetSpecializationParamCount() != 0;
            return flags;
        }
        finally
        {
            GC.KeepAlive(shader);
            frontEnd.Release();
        }
    }

    private static IComponentType LinkShaderForBatch(
        ISession session, IReadOnlyList<IModule> sourceUnits, IReadOnlyList<IEntryPoint> entries,
        ReadOnlyMemory<SpecializationArg> specializations, CancellationToken cancellationToken,
        string entryPoint, List<IComponentType> roots)
    {
        if (entries.Count == 0)
            throw new InvalidDataException("The shader source block exposes no entry points.");
        var components = new List<IComponentType>(sourceUnits);
        foreach (IEntryPoint entry in entries)
        {
            IComponentType component = specializations.IsEmpty ? entry : Specialize(session, entry, specializations);
            components.Add(component);
            roots.Add(component);
        }
        SlangResult combineResult = session.CreateCompositeComponentType(components.ToArray(),
            out IComponentType combinedCode, out ISlangBlob? combineDiagnostics);
        if (combineResult.Failed || combinedCode is null)
            throw new InvalidDataException("Slang composition failed: " + DiagnosticText(combineDiagnostics));
        SlangShaderData.RetainSession(session, combinedCode);
        roots.Add(combinedCode);
        // This batch contains named-entry specializations; the entry was specialized above.
        IComponentType codeToLink = combinedCode;
        roots.Add(codeToLink);
        SlangResult linkResult = codeToLink.Link(out IComponentType linkedCode, out ISlangBlob? linkDiagnostics);
        if (linkResult.Failed || linkedCode is null)
            throw new InvalidDataException("Slang link failed: " + DiagnosticText(linkDiagnostics));
        SlangShaderData.RetainSession(session, linkedCode);
        roots.Add(linkedCode);
        cancellationToken.ThrowIfCancellationRequested();
        return linkedCode;
    }

    internal async ValueTask<Shader> CreateUsingSavedCodeAsync(
        IGlobalSession slang,
        SessionDesc sessionDescription,
        string name,
        ReadOnlyMemory<SpecializationArg> specializations = default,
        CancellationToken cancellationToken = default,
        string? entryPoint = null) =>
        (await CreateCoreAsync(
            slang,
            sessionDescription,
            name,
            specializations,
            generateCode: false,
            saveSources: false,
            cancellationToken, entryPoint).ConfigureAwait(false)).Shader;

    internal async ValueTask<(string[] Names, byte[][] Sources)> ReadSavedSourcesAsync(CancellationToken cancellationToken)
    {
        string[] names = SourceNames.ToArray();
        var sources = new byte[names.Length][];
        for (int index = 0; index < sources.Length; index++)
        {
            ulong key = SerializedSourceKey(index);
            if (!ContentKeys.Contains(key))
                throw new InvalidDataException("Saved shader specializations require their serialized Slang source.");
            // File-backed arrays survive source disposal. Keep copying caller-supplied chunks.
            bool local = Chunks.ContainsKey(key);
            ReadOnlyMemory<byte> bytes = await ReadBytesAsync(key, cancellationToken).ConfigureAwait(false);
            sources[index] = !local &&
                System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> data) &&
                data.Offset == 0 && data.Count == data.Array!.Length
                    ? data.Array! : bytes.ToArray();
            if (sources[index].Length == 0)
                throw new InvalidDataException("Saved shader specializations have empty serialized Slang source.");
        }
        return (names, sources);
    }

    internal static Shader RestoreSavedEntry(IGlobalSession slang, SessionDesc description, string name,
        string entryPoint, string[] arguments, string[] sourceNames, byte[][] sources)
    {
        using var source = new ShaderSourceBlock();
        foreach (string path in sourceNames) source.SourceNames.Add(path);
        // ReadSavedSourcesAsync already owns and validates these immutable bytes.
        for (int index = 0; index < sources.Length; index++)
            source.SetBytes(SerializedSourceKey(index), sources[index]);
        return source.CreateUsingSavedCodeAsync(slang, description, name,
            arguments.Select(SpecializationArg.FromExpression).ToArray(), CancellationToken.None, entryPoint)
            .AsTask().GetAwaiter().GetResult();
    }

    internal async ValueTask<Shader> CreateSelectedAsync(IReadOnlyList<ShaderInputSource> inputs,
        IGlobalSession slang, SessionDesc description, string name, ReadOnlyMemory<SpecializationArg> arguments,
        bool generateCode, CancellationToken cancellationToken)
    {
        using ShaderSourceBlock source = FromInputs(inputs);
        return (await source.CreateCoreAsync(slang, description, name, arguments, generateCode,
            saveSources: false, cancellationToken).ConfigureAwait(false)).Shader;
    }

    internal async ValueTask<ShaderCompilation> CompileSelectedAsync(IReadOnlyList<ShaderInputSource> inputs,
        IGlobalSession slang, SessionDesc description, string name, CancellationToken cancellationToken,
        ShaderSourceFiles? sourceFiles = null)
    {
        using ShaderSourceBlock source = FromInputs(inputs);
        return await source.CreateCoreAsync(slang, description, name, default, generateCode: true,
            saveSources: false, cancellationToken, sourceFiles: sourceFiles).ConfigureAwait(false);
    }

    internal ValueTask<ShaderCompilation> CompileSelectedEntryAsync(IGlobalSession slang, SessionDesc description,
        string name, string entryPoint, ReadOnlyMemory<SpecializationArg> arguments, CancellationToken cancellationToken) =>
        CreateCoreAsync(slang, description, name, arguments, generateCode: true, saveSources: false,
            cancellationToken, entryPoint);

    internal ShaderSourceBlock FromInputs(IReadOnlyList<ShaderInputSource> inputs)
    {
        var source = new ShaderSourceBlock { SourceNames = inputs.Select(input => input.Name).ToList(),
            Specializations = Specializations.ToList() };
        for (int index = 0; index < inputs.Count; index++) source.SetSerializedSource(index, inputs[index].Serialized.Span);
        return source;
    }

    private async ValueTask<ShaderCompilation> CreateCoreAsync(
        IGlobalSession slang,
        SessionDesc sessionDescription,
        string name,
        ReadOnlyMemory<SpecializationArg> specializations,
        bool generateCode,
        bool saveSources,
        CancellationToken cancellationToken,
        string? entryPoint = null,
        ShaderSourceFiles? sourceFiles = null)
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
            if (copy.SourceNames.Count == 0)
                throw new InvalidDataException("The shader source block contains no source files.");

            string[] paths = copy.SourceNames.Select(NormalizeSourceName).ToArray();
            if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
                throw new InvalidDataException("The shader source block contains duplicate source files.");

            sourceFiles ??= new ShaderSourceFiles(sessionDescription.FileSystem);
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
                if (!saveSources && copy.ContentKeys.Contains(serializedKey))
                {
                    ReadOnlyMemory<byte> serialized = await copy.ReadBytesAsync(serializedKey, cancellationToken).ConfigureAwait(false);
                    ISlangBlob source = Slang.CreateBlob(serialized.Span);
                    sourceUnit = session.LoadModuleFromIRBlob(Path.GetFileNameWithoutExtension(paths[sourceIndex]),
                        paths[sourceIndex], source, out diagnostics);
                    if (saveSources) serializedSources.Add(serialized);
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
                if (sourceUnit is null)
                {
                    throw new InvalidDataException(
                        $"Slang could not load source '{paths[sourceIndex]}': {DiagnosticText(diagnostics)}");
                }
                SlangShaderData.RetainSession(session, sourceUnit);
                sourceUnits.Add(sourceUnit);
            }

            foreach (IModule sourceUnit in sourceUnits)
            {
                for (int entryIndex = 0; entryIndex < sourceUnit.GetDefinedEntryPointCount(); entryIndex++)
                {
                    SlangResult entryResult = sourceUnit.GetDefinedEntryPoint(entryIndex, out IEntryPoint entry);
                    if (entryResult.Failed || entry is null)
                        throw new InvalidDataException("Slang could not return a declared shader entry point.");
                    SlangShaderData.RetainSession(session, entry);
                    if (entryPoint is null || entry.GetFunctionReflection().Name == entryPoint)
                        entries.Add(entry);
                }
            }

            Shader shader = CompileShader(slang, session, name, sourceUnits, entries, targets,
                specializations, generateCode, cancellationToken, entryPoint);
            return new(shader, serializedSources, sourceFiles.Finish())
            {
                Specializations = generateCode && entryPoint is null ? copy.ReadSpecializations(shader) : [],
            };
        }
        finally
        {
            frontEnd.Release();
        }
    }

    private static Shader CompileShader(
        IGlobalSession slang, ISession session, string name,
        IReadOnlyList<IModule> sourceUnits, IReadOnlyList<IEntryPoint> entries, TargetDesc[] targets,
        ReadOnlyMemory<SpecializationArg> specializations, bool generateCode,
        CancellationToken cancellationToken, string? entryPoint)
    {
        if (entries.Count == 0)
            throw new InvalidDataException("The shader source block exposes no entry points.");

        var components = new List<IComponentType>(sourceUnits);
        foreach (IEntryPoint entry in entries)
        {
            IComponentType component = entry;
            if (entryPoint is not null && !specializations.IsEmpty)
                component = Specialize(session, entry, specializations);
            components.Add(component);
        }
        SlangResult combineResult = session.CreateCompositeComponentType(
            components.ToArray(),
            out IComponentType combinedCode,
            out ISlangBlob? combineDiagnostics);
        if (combineResult.Failed || combinedCode is null)
            throw new InvalidDataException("Slang composition failed: " + DiagnosticText(combineDiagnostics));
        SlangShaderData.RetainSession(session, combinedCode);

        IComponentType codeToLink = entryPoint is null && !specializations.IsEmpty
            ? Specialize(session, combinedCode, specializations)
            : combinedCode;

        SlangResult linkResult = codeToLink.Link(
            out IComponentType linkedCode,
            out ISlangBlob? linkDiagnostics);
        if (linkResult.Failed || linkedCode is null)
            throw new InvalidDataException("Slang link failed: " + DiagnosticText(linkDiagnostics));
        SlangShaderData.RetainSession(session, linkedCode);

        cancellationToken.ThrowIfCancellationRequested();
        return Shader.CreateFromSlang(
            name,
            slang,
            session,
            sourceUnits,
            entries,
            linkedCode,
            targets,
            generateCode);
    }

    private static IComponentType Specialize(ISession session, IComponentType code, ReadOnlyMemory<SpecializationArg> arguments)
    {
        SlangResult result = code.Specialize(arguments.ToArray(), out IComponentType specialized,
            out ISlangBlob? diagnostics);
        if (result.Failed || specialized is null)
            throw new InvalidDataException("Slang specialization failed: " + DiagnosticText(diagnostics));
        SlangShaderData.RetainSession(session, specialized);
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
