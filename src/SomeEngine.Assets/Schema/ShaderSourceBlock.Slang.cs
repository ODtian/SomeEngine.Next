using System.Globalization;
using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;
using SomeEngine.Resources;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Schema;

public sealed partial class ShaderSourceBlock
{
    public static ulong SerializedSourceKey(int sourceIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceIndex);
        return BinaryFieldKey.FromName(
            "SomeEngine.Assets.Schema.Shader.Source.Slang." +
            sourceIndex.ToString(CultureInfo.InvariantCulture));
    }

    public void SetSerializedSource(int sourceIndex, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            throw new ArgumentException("Serialized Slang source cannot be empty.", nameof(bytes));
        lock (Access.SyncRoot)
        {
            RequireIdle();
            if ((uint)sourceIndex >= (uint)SourceFiles.Count)
                throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        }

        using var storage = new PageBuffer(Pages);
        using (var output = new PageBufferStream(storage))
            output.Write(bytes);
        using PageBuffer content = storage.Slice(0, bytes.Length);
        lock (Access.SyncRoot)
        {
            RequireIdle();
            if ((uint)sourceIndex >= (uint)SourceFiles.Count)
                throw new InvalidOperationException("The shader source list changed while its Slang data was encoded.");
            Chunks.Set(SerializedSourceKey(sourceIndex), content);
        }
    }

    /// <summary>
    /// Acquires the selected source files, compiles them with Slang, and returns a resident shader.
    /// The returned shader no longer depends on this block or the source files.
    /// </summary>
    public async ValueTask<Shader> CreateAsync(
        IGlobalSession slang,
        SessionDesc sessionDescription,
        string definitionPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slang);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionPath);
        if (sessionDescription.Targets is not { Length: > 0 } targets)
            throw new ArgumentException("Creating a shader requires at least one Slang target.", nameof(sessionDescription));

        using ShaderSourceBlock copy = Duplicate();
        if (copy.SourceFiles.Count == 0)
            throw new InvalidDataException("The shader source block contains no source files.");

        string fullDefinitionPath = Path.GetFullPath(definitionPath);
        string definitionDirectory = Path.GetDirectoryName(fullDefinitionPath)!;
        string[] paths = copy.SourceFiles.Select(path => ResolveSourcePath(definitionDirectory, path)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("The shader source block contains duplicate source files.");

        sessionDescription.SearchPaths = paths
            .Select(static path => Path.GetDirectoryName(path)!)
            .Concat(sessionDescription.SearchPaths ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        SlangResult sessionResult = slang.CreateSession(sessionDescription, out ISession session);
        if (sessionResult.Failed || session is null)
            throw new InvalidDataException($"Slang session creation failed with {sessionResult}.");

        var sourceUnits = new List<IModule>(paths.Length);
        var entries = new List<IEntryPoint>();
        IComponentType? combinedCode = null;
        IComponentType? specializedCode = null;
        IComponentType? linkedCode = null;
        bool shaderOwnsSlang = false;
        try
        {
            for (int sourceIndex = 0; sourceIndex < paths.Length; sourceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong serializedKey = SerializedSourceKey(sourceIndex);
                bool hasSerializedSource = copy.ContentKeys.Contains(serializedKey);
                ISlangBlob source;
                if (hasSerializedSource)
                {
                    using PageBuffer bytes = await copy.AcquireBufferAsync(serializedKey, cancellationToken)
                        .ConfigureAwait(false);
                    using PageRange<byte> range = bytes.Retain();
                    source = Slang.CreateBlob(range.ToArray());
                }
                else
                {
                    byte[] bytes = await File.ReadAllBytesAsync(paths[sourceIndex], cancellationToken)
                        .ConfigureAwait(false);
                    source = Slang.CreateBlob(bytes);
                }
                try
                {
                    IModule? sourceUnit = hasSerializedSource
                        ? session.LoadModuleFromIRBlob(
                            Path.GetFileNameWithoutExtension(paths[sourceIndex]),
                            paths[sourceIndex],
                            source,
                            out ISlangBlob? diagnostics)
                        : session.LoadModuleFromSource(
                            Path.GetFileNameWithoutExtension(paths[sourceIndex]),
                            paths[sourceIndex],
                            source,
                            out diagnostics);
                    string diagnosticText = DiagnosticText(diagnostics);
                    Release(diagnostics);
                    if (sourceUnit is null)
                    {
                        string inputKind = hasSerializedSource ? "serialized source" : "source file";
                        throw new InvalidDataException(
                            $"Slang could not load {inputKind} '{paths[sourceIndex]}': {diagnosticText}");
                    }
                    sourceUnits.Add(sourceUnit);
                }
                finally
                {
                    Release(source);
                }
            }

            if (copy.SelectedEntries.Count == 0)
            {
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
            }
            else
            {
                var selected = new HashSet<ShaderEntrySelection>();
                foreach (ShaderEntrySelection selection in copy.SelectedEntries)
                {
                    if (selection.SourceIndex >= sourceUnits.Count ||
                        string.IsNullOrWhiteSpace(selection.Name) ||
                        !selected.Add(selection))
                    {
                        throw new InvalidDataException("The shader source block contains an invalid entry selection.");
                    }
                    SlangResult entryResult = sourceUnits[checked((int)selection.SourceIndex)]
                        .FindEntryPointByName(selection.Name, out IEntryPoint entry);
                    if (entryResult.Failed || entry is null)
                        throw new InvalidDataException($"Slang could not find shader entry '{selection.Name}'.");
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
                out combinedCode,
                out ISlangBlob? combineDiagnostics);
            string combineText = DiagnosticText(combineDiagnostics);
            Release(combineDiagnostics);
            if (combineResult.Failed || combinedCode is null)
                throw new InvalidDataException("Slang composition failed: " + combineText);

            IComponentType codeToLink = combinedCode;
            if (copy.Specializations.Count > 0)
            {
                SpecializationArg[] arguments = copy.Specializations
                    .Select(SpecializationArg.FromExpression)
                    .ToArray();
                SlangResult specializationResult = combinedCode.Specialize(
                    arguments,
                    out specializedCode,
                    out ISlangBlob? specializationDiagnostics);
                string specializationText = DiagnosticText(specializationDiagnostics);
                Release(specializationDiagnostics);
                if (specializationResult.Failed || specializedCode is null)
                    throw new InvalidDataException("Slang specialization failed: " + specializationText);
                codeToLink = specializedCode;
            }

            SlangResult linkResult = codeToLink.Link(out linkedCode, out ISlangBlob? linkDiagnostics);
            string linkText = DiagnosticText(linkDiagnostics);
            Release(linkDiagnostics);
            if (linkResult.Failed || linkedCode is null)
                throw new InvalidDataException("Slang link failed: " + linkText);

            cancellationToken.ThrowIfCancellationRequested();
            Release(specializedCode);
            specializedCode = null;
            Release(combinedCode);
            combinedCode = null;

            shaderOwnsSlang = true;
            return Shader.CreateFromSlang(
                Path.GetFileNameWithoutExtension(fullDefinitionPath),
                session,
                sourceUnits,
                entries,
                linkedCode,
                targets);
        }
        finally
        {
            Release(specializedCode);
            Release(combinedCode);
            if (!shaderOwnsSlang)
            {
                Release(linkedCode);
                ReleaseReverse(entries);
                ReleaseReverse(sourceUnits);
                Release(session);
            }
        }
    }

    private static string ResolveSourcePath(string definitionDirectory, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || Path.IsPathRooted(sourcePath))
            throw new InvalidDataException("Shader source paths must be relative to the definition file.");
        return Path.GetFullPath(Path.Combine(definitionDirectory, sourcePath));
    }

    private static string DiagnosticText(ISlangBlob? diagnostics)
        => diagnostics is null || diagnostics.GetBufferSize() == 0
            ? "no diagnostics"
            : diagnostics.AsString;

    private static void ReleaseReverse<T>(IReadOnlyList<T> values)
        where T : class
    {
        for (int index = values.Count - 1; index >= 0; index--)
            Release(values[index]);
    }

    private static void Release(object? value)
    {
        if (value is ComObject com)
            com.FinalRelease();
    }
}
