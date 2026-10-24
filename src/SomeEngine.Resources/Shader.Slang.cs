using System.Buffers;
using System.Collections.Immutable;
using SlangShaderSharp;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

public unsafe partial class Shader
{
    private IGlobalSession _slangGlobal => SlangCode.Global;
    private readonly ImmutableArray<IModule> _slangUnits;
    private readonly ImmutableArray<IEntryPoint> _slangEntries;
    private IComponentType _slangCode => SlangCode.Code;
    private readonly ImmutableArray<TargetDesc> _slangTargets;
    private readonly ImmutableArray<ShaderReflection> _slangReflections;
    private readonly ImmutableArray<ISlangBlob?> _slangTargetBlobs;
    private readonly ImmutableArray<ISlangBlob?> _slangEntryBlobs;
    private readonly ImmutableArray<byte[]?> _slangTargetBytes;
    private readonly ImmutableArray<byte[]> _slangEntryBytes;
    private readonly int _slangEntryCount;
    private readonly ImmutableArray<bool> _slangUnboundEntries;

    private Shader(
        string name,
        SlangShaderData slangCode,
        ImmutableArray<IModule> sourceUnits,
        ImmutableArray<IEntryPoint> entries,
        ImmutableArray<TargetDesc> targets,
        ImmutableArray<ShaderReflection> reflections,
        ImmutableArray<ISlangBlob?> targetBlobs,
        ImmutableArray<ISlangBlob?> entryBlobs,
        ImmutableArray<byte[]?> targetBytes,
        ImmutableArray<byte[]> entryBytes,
        int entryCount,
        ImmutableArray<ImmutableHashSet<SlangDescriptorLocation>> usedParameters,
        ImmutableArray<bool> unboundEntries,
        ImmutableArray<(string EntryPoint, ImmutableArray<string> Arguments, Lazy<Shader> Shader)> specializations = default)
    {
        Name = name;
        SlangCode = slangCode;
        _slangUnits = sourceUnits;
        _slangEntries = entries;
        _slangTargets = targets;
        _slangReflections = reflections;
        _slangTargetBlobs = targetBlobs;
        _slangEntryBlobs = entryBlobs;
        _slangTargetBytes = targetBytes;
        _slangEntryBytes = entryBytes;
        _slangEntryCount = entryCount;
        _slangUsedParameters = usedParameters;
        _slangUnboundEntries = unboundEntries;
        _specializations = specializations;
        IndexSlangEntries();
    }

    /// <summary>
    /// Creates an immutable shader that keeps the supplied Slang interfaces reachable.
    /// Generated code remains in its original <see cref="ISlangBlob"/> objects.
    /// </summary>
    public static Shader CreateFromSlang(
        string name,
        IGlobalSession global,
        ISession session,
        IReadOnlyList<IModule> sourceUnits,
        IReadOnlyList<IEntryPoint> entries,
        IComponentType linkedCode,
        ReadOnlySpan<TargetDesc> targets,
        bool generateCode = true,
        bool generateLibraryCode = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sourceUnits);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(linkedCode);
        if (targets.IsEmpty)
            throw new ArgumentException("Creating a shader requires at least one Slang target.", nameof(targets));

        var slangCode = new SlangShaderData(global, session, linkedCode);
        bool unbound = linkedCode.GetSpecializationParamCount() != 0;
        ImmutableArray<bool> unboundEntries = entries.Select(entry =>
            unbound && entry.GetSpecializationParamCount() != 0).ToImmutableArray();
        var reflections = ImmutableArray.CreateBuilder<ShaderReflection>(targets.Length);
        var targetBlobs = ImmutableArray.CreateBuilder<ISlangBlob?>(targets.Length);
        var entryBlobs = ImmutableArray.CreateBuilder<ISlangBlob?>();
        int entryCount = -1;
        for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            ShaderReflection reflection = linkedCode.GetLayout(
                targetIndex,
                out ISlangBlob? layoutDiagnostics);
            string layoutText = DiagnosticText(layoutDiagnostics);
            if (reflection == ShaderReflection.Null)
            {
                throw new InvalidDataException(
                    $"Slang did not produce reflection for target {targetIndex}: {layoutText}");
            }

            int reflectedEntryCount = checked((int)reflection.EntryPointCount);
            if (entryCount < 0)
                entryCount = reflectedEntryCount;
            else if (entryCount != reflectedEntryCount)
                throw new InvalidDataException("Slang targets expose different entry-point counts.");
            if (reflectedEntryCount != entries.Count)
                throw new InvalidDataException("Slang reflection differs from the supplied entry points.");

            reflections.Add(reflection);
            targetBlobs.Add(generateCode && generateLibraryCode && !unbound
                ? GetTargetBlob(linkedCode, targetIndex)
                : null);

            for (int entryIndex = 0; entryIndex < reflectedEntryCount; entryIndex++)
            {
                entryBlobs.Add(generateCode && !unboundEntries[entryIndex]
                    ? GetEntryBlob(linkedCode, targetIndex, entryIndex)
                    : null);
            }
        }

        int codeCount = checked(targets.Length * entryCount);
        return new Shader(
            name,
            slangCode,
            sourceUnits.ToImmutableArray(),
            entries.ToImmutableArray(),
            targets.ToArray().ToImmutableArray(),
            reflections.ToImmutable(),
            targetBlobs.ToImmutable(),
            entryBlobs.ToImmutable(),
            Enumerable.Repeat<byte[]?>(null, targets.Length).ToImmutableArray(),
            Enumerable.Repeat(Array.Empty<byte>(), codeCount).ToImmutableArray(),
            entryCount,
            generateCode ? ReadSlangUsedParameters(linkedCode, targets.Length, entryCount, unboundEntries) :
                Enumerable.Repeat(ImmutableHashSet<SlangDescriptorLocation>.Empty, codeCount).ToImmutableArray(),
            unboundEntries);
    }

    public bool HasSlangData => true;
    public int SlangTargetCount => _slangTargets.Length;
    public int SlangEntryCount => _slangEntryCount;
    public int SlangSourceUnitCount => _slangUnits.Length;
    public IModule GetSlangSourceUnit(int index) => _slangUnits[index];
    public IEntryPoint GetSlangEntryPoint(int index) => _slangEntries[index];
    public SlangShaderData SlangCode { get; }
    public TargetDesc GetSlangTarget(int targetIndex) => _slangTargets[targetIndex];
    public ShaderReflection GetSlangReflection(int targetIndex) => _slangReflections[targetIndex];

    public ISlangBlob? GetSlangEntryBlob(int targetIndex, int entryIndex) =>
        _slangEntryBlobs[GetCodeIndex(targetIndex, entryIndex)];

    public ReadOnlySequence<byte> GetSlangEntryBytes(int targetIndex, int entryIndex)
    {
        byte[] bytes = _slangEntryBytes[GetCodeIndex(targetIndex, entryIndex)];
        return new ReadOnlySequence<byte>(bytes);
    }

    public ISlangBlob? GetSlangTargetBlob(int targetIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        return _slangTargetBlobs[targetIndex];
    }

    public ReadOnlySequence<byte> GetSlangTargetBytes(int targetIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        byte[]? bytes = _slangTargetBytes[targetIndex];
        return bytes is null
            ? ReadOnlySequence<byte>.Empty
            : new ReadOnlySequence<byte>(bytes);
    }

    public bool TryGetSlangTarget(SlangCompileTarget format, out int targetIndex)
    {
        for (int index = 0; index < _slangTargets.Length; index++)
        {
            if (_slangTargets[index].Format == format)
            {
                targetIndex = index;
                return true;
            }
        }
        targetIndex = -1;
        return false;
    }

    public int RequireSlangTarget(SlangCompileTarget format)
    {
        if (TryGetSlangTarget(format, out int targetIndex))
            return targetIndex;
        throw new InvalidDataException(
            $"Shader '{Name}' has no resident Slang target for {format}.");
    }

    public EntryPointReflection RequireSlangEntry(
        int targetIndex,
        string name,
        SlangStage stage)
    {
        if (TryGetSlangEntry(targetIndex, name, stage, out EntryPointReflection entry))
            return entry;
        throw new InvalidDataException(
            $"Shader '{Name}' has no {stage} Slang entry point named '{name}'.");
    }

    public ReadOnlySequence<byte> GetSlangEntryBytes(
        int targetIndex,
        EntryPointReflection entry) =>
        GetSlangEntryBytes(targetIndex, GetEntryIndex(targetIndex, entry));

    public ISlangBlob? GetSlangEntryBlob(
        int targetIndex,
        EntryPointReflection entry) =>
        GetSlangEntryBlob(targetIndex, GetEntryIndex(targetIndex, entry));

    public bool TryGetSlangEntry(
        int targetIndex,
        string name,
        out EntryPointReflection entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_slangNamedEntries.TryGetValue((targetIndex, name), out var found))
        {
            entry = found.Entry;
            return true;
        }
        entry = EntryPointReflection.Null;
        return false;
    }

    public bool TryGetSlangEntry(
        int targetIndex,
        string name,
        SlangStage stage,
        out EntryPointReflection entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_slangStageEntries.TryGetValue((targetIndex, name, stage), out entry)) return true;
        entry = EntryPointReflection.Null;
        return false;
    }

    internal Shader WithManagedCode(
        IReadOnlyList<byte[]?> targetCode,
        IReadOnlyList<byte[]> entryCode,
        IReadOnlyList<ImmutableHashSet<SlangDescriptorLocation>>? usedParameters = null)
    {
        ArgumentNullException.ThrowIfNull(targetCode);
        ArgumentNullException.ThrowIfNull(entryCode);
        usedParameters ??= ReadSlangUsedParameters(_slangCode, _slangTargets.Length, _slangEntryCount, _slangUnboundEntries);
        if (targetCode.Count != _slangTargets.Length ||
            entryCode.Count != checked(_slangTargets.Length * _slangEntryCount) || usedParameters.Count != entryCode.Count)
        {
            throw new ArgumentException("Saved shader code does not match Slang reflection.");
        }
        for (int index = 0; index < entryCode.Count; index++)
            if (entryCode[index].Length == 0 && !_slangUnboundEntries[index % _slangEntryCount])
                throw new ArgumentException("Saved shader entry code cannot be empty.", nameof(entryCode));

        return new Shader(
            Name,
            SlangCode,
            _slangUnits,
            _slangEntries,
            _slangTargets,
            _slangReflections,
            Enumerable.Repeat<ISlangBlob?>(null, targetCode.Count).ToImmutableArray(),
            Enumerable.Repeat<ISlangBlob?>(null, entryCode.Count).ToImmutableArray(),
            targetCode.ToImmutableArray(),
            entryCode.ToImmutableArray(),
            _slangEntryCount,
            usedParameters.ToImmutableArray(),
            _slangUnboundEntries);
    }

    private int GetCodeIndex(int targetIndex, int entryIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        if (targetIndex >= _slangTargets.Length)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        if (entryIndex >= _slangEntryCount)
            throw new ArgumentOutOfRangeException(nameof(entryIndex));
        if (_slangUnboundEntries[entryIndex])
            throw new InvalidOperationException($"Shader entry '{GetSlangReflection(targetIndex).GetEntryPointByIndex((nuint)entryIndex).Name}' requires specialization.");
        return checked(targetIndex * _slangEntryCount + entryIndex);
    }

    private int GetEntryIndex(int targetIndex, EntryPointReflection entry)
    {
        if (entry == EntryPointReflection.Null)
            throw new ArgumentException("The Slang entry point is null.", nameof(entry));
        ShaderReflection reflection = GetSlangReflection(targetIndex);
        for (int entryIndex = 0; entryIndex < _slangEntryCount; entryIndex++)
        {
            if (reflection.GetEntryPointByIndex(checked((nuint)entryIndex)) == entry)
                return entryIndex;
        }
        throw new ArgumentException(
            "The Slang entry point does not belong to this shader target.",
            nameof(entry));
    }

    private static ISlangBlob? GetTargetBlob(IComponentType linkedCode, int targetIndex)
    {
        SlangResult result = linkedCode.GetTargetCode(
            targetIndex,
            out ISlangBlob blob,
            out ISlangBlob? diagnostics);
        if (result.Failed || blob is null || blob.GetBufferSize() == 0)
            return null;
        return blob;
    }

    private static ISlangBlob GetEntryBlob(
        IComponentType linkedCode,
        int targetIndex,
        int entryIndex)
    {
        SlangResult result = linkedCode.GetEntryPointCode(
            entryIndex,
            targetIndex,
            out ISlangBlob blob,
            out ISlangBlob? diagnostics);
        string diagnosticText = DiagnosticText(diagnostics);
        if (result.Failed || blob is null || blob.GetBufferSize() == 0)
        {
            throw new InvalidDataException(
                $"Slang did not produce code for target {targetIndex}, entry {entryIndex}: " +
                diagnosticText);
        }
        return blob;
    }

    private static string DiagnosticText(ISlangBlob? diagnostics) =>
        diagnostics is null || diagnostics.GetBufferSize() == 0
            ? "no diagnostics"
            : diagnostics.AsString;
}
