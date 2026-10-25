using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using SlangShaderSharp;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

public unsafe partial class Shader
{
    private static readonly ActivitySource s_compilationActivities = new("SomeEngine.Shaders");
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
        ImmutableArray<(string EntryPoint, ImmutableArray<string> Arguments, Lazy<Shader> Shader)> specializations = default,
        Shader? entryIndexSource = null)
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
        if (entryIndexSource is null)
        {
            _slangNamedEntries = [];
            _slangStageEntries = [];
            IndexSlangEntries();
        }
        else
        {
            _slangNamedEntries = entryIndexSource._slangNamedEntries;
            _slangStageEntries = entryIndexSource._slangStageEntries;
        }
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
        foreach (IModule sourceUnit in sourceUnits) SlangShaderData.RetainSession(session, sourceUnit);
        foreach (IEntryPoint entry in entries) SlangShaderData.RetainSession(session, entry);
        bool unbound = linkedCode.GetSpecializationParamCount() != 0;
        ImmutableArray<bool> unboundEntries = entries.Select(entry =>
            unbound && entry.GetSpecializationParamCount() != 0).ToImmutableArray();
        var reflections = ImmutableArray.CreateBuilder<ShaderReflection>(targets.Length);
        int entryCount = -1;
        for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
        {
            using var layoutActivity = s_compilationActivities.StartActivity("Shader.Layout");
            layoutActivity?.SetTag("target", targetIndex);
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
        }

        int codeCount = checked(targets.Length * entryCount);
        var targetBlobs = new ISlangBlob?[targets.Length];
        var entryBlobs = new ISlangBlob?[codeCount];
        if (generateCode && !unbound)
        {
            // Complete layout queries before using Slang's experimental parallel backend.
            // Front-end work remains serialized by the caller.
            if (generateLibraryCode && entryCount != 1)
                for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
                    targetBlobs[targetIndex] = GetTargetBlob(linkedCode, targetIndex);

            Parallel.ForAsync(0, codeCount,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4) },
                (index, _) =>
                {
                    entryBlobs[index] = GetEntryBlob(linkedCode, index / entryCount, index % entryCount);
                    return ValueTask.CompletedTask;
                }).GetAwaiter().GetResult();

            // A single bound entry has the same code as the whole target. Keep
            // both public views backed by the same managed blob reference.
            if (generateLibraryCode && entryCount == 1)
                for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
                    targetBlobs[targetIndex] = entryBlobs[targetIndex] ?? GetTargetBlob(linkedCode, targetIndex);
        }
        else if (generateCode)
        {
            // A component with unbound generics is outside the parallel-backend guarantee.
            for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
                for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
                    if (!unboundEntries[entryIndex])
                        entryBlobs[targetIndex * entryCount + entryIndex] =
                            GetEntryBlob(linkedCode, targetIndex, entryIndex);
        }
        return new Shader(
            name,
            slangCode,
            sourceUnits.ToImmutableArray(),
            entries.ToImmutableArray(),
            targets.ToArray().ToImmutableArray(),
            reflections.ToImmutable(),
            targetBlobs.ToImmutableArray(),
            entryBlobs.ToImmutableArray(),
            Enumerable.Repeat<byte[]?>(null, targets.Length).ToImmutableArray(),
            Enumerable.Repeat(Array.Empty<byte>(), codeCount).ToImmutableArray(),
            entryCount,
            generateCode ? ReadSlangUsedParameters(linkedCode, reflections, entryCount, unboundEntries) :
                Enumerable.Repeat(ImmutableHashSet<SlangDescriptorLocation>.Empty, codeCount).ToImmutableArray(),
            unboundEntries);
    }

    /// <summary>
    /// Generates an ordered set of linked specializations with one bounded backend budget.
    /// The caller must serialize the shared Slang object graph through this entire operation.
    /// Slang's cross-component backend concurrency support is experimental.
    /// </summary>
    public static Shader[] CreateFromSlangBatch(
        string name,
        IGlobalSession global,
        ISession session,
        IReadOnlyList<IModule> sourceUnits,
        IReadOnlyList<(IReadOnlyList<IEntryPoint> Entries, IComponentType LinkedCode)> inputs,
        ReadOnlySpan<TargetDesc> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sourceUnits);
        ArgumentNullException.ThrowIfNull(inputs);
        if (targets.IsEmpty)
            throw new ArgumentException("Creating a shader requires at least one Slang target.", nameof(targets));

        ImmutableArray<IModule> units = sourceUnits.ToImmutableArray();
        foreach (IModule sourceUnit in units) SlangShaderData.RetainSession(session, sourceUnit);
        ImmutableArray<TargetDesc> targetSnapshot = targets.ToArray().ToImmutableArray();
        var prepared = new SlangBatchPreparation[inputs.Count];
        try
        {
            // No backend work begins until all shared frontend/layout work has finished.
            for (int index = 0; index < inputs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var input = inputs[index];
                ArgumentNullException.ThrowIfNull(input.Entries);
                ArgumentNullException.ThrowIfNull(input.LinkedCode);
                SlangShaderData.RetainSession(session, input.LinkedCode);
                ImmutableArray<IEntryPoint> entries = input.Entries.ToImmutableArray();
                foreach (IEntryPoint entry in entries) SlangShaderData.RetainSession(session, entry);
                bool unbound = input.LinkedCode.GetSpecializationParamCount() != 0;
                ImmutableArray<bool> unboundEntries = entries.Select(entry =>
                    unbound && entry.GetSpecializationParamCount() != 0).ToImmutableArray();
                var reflections = ImmutableArray.CreateBuilder<ShaderReflection>(targetSnapshot.Length);
                int entryCount = -1;
                for (int target = 0; target < targetSnapshot.Length; target++)
                {
                    using var layoutActivity = s_compilationActivities.StartActivity("Shader.Layout");
                    layoutActivity?.SetTag("target", target);
                    ShaderReflection reflection = input.LinkedCode.GetLayout(target, out ISlangBlob? diagnostics);
                    string diagnosticText = DiagnosticText(diagnostics);
                    if (reflection == ShaderReflection.Null)
                        throw new InvalidDataException($"Slang did not produce reflection for target {target}: {diagnosticText}");
                    int reflectedEntryCount = checked((int)reflection.EntryPointCount);
                    if (entryCount < 0) entryCount = reflectedEntryCount;
                    else if (entryCount != reflectedEntryCount)
                        throw new InvalidDataException("Slang targets expose different entry-point counts.");
                    if (reflectedEntryCount != entries.Length)
                        throw new InvalidDataException("Slang reflection differs from the supplied entry points.");
                    reflections.Add(reflection);
                }
                int codeCount = checked(targetSnapshot.Length * entryCount);
                prepared[index] = new(entries, input.LinkedCode, unbound, unboundEntries, reflections,
                    entryCount, new ISlangBlob?[targetSnapshot.Length], new ISlangBlob?[codeCount]);
            }

            var jobs = new List<(SlangBatchPreparation Item, int Index)>();
            foreach (SlangBatchPreparation item in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Unbound)
                {
                    // Mixed-generic work remains serial, with no parallel jobs active.
                    for (int target = 0; target < targetSnapshot.Length; target++)
                        for (int entry = 0; entry < item.EntryCount; entry++)
                            if (!item.UnboundEntries[entry])
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                item.EntryBlobs[target * item.EntryCount + entry] =
                                    GetEntryBlob(item.LinkedCode, target, entry);
                            }
                    continue;
                }
                // Preserve optional whole-target generation and its ordering before entry code.
                if (item.EntryCount != 1)
                    for (int target = 0; target < targetSnapshot.Length; target++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        item.TargetBlobs[target] = GetTargetBlob(item.LinkedCode, target);
                    }
                for (int index = 0; index < item.EntryBlobs.Length; index++) jobs.Add((item, index));
            }

            cancellationToken.ThrowIfCancellationRequested();
            // ForAsync completes only after every started synchronous native call returns,
            // including failure/cancellation. Never replace this join with WaitAsync(token).
            Parallel.ForAsync(0, jobs.Count, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 4),
                CancellationToken = cancellationToken,
            }, (index, token) =>
            {
                token.ThrowIfCancellationRequested();
                var job = jobs[index];
                job.Item.EntryBlobs[job.Index] = GetEntryBlob(job.Item.LinkedCode,
                    job.Index / job.Item.EntryCount, job.Index % job.Item.EntryCount);
                return ValueTask.CompletedTask;
            }).GetAwaiter().GetResult();

            cancellationToken.ThrowIfCancellationRequested();
            foreach (SlangBatchPreparation item in prepared)
                if (!item.Unbound && item.EntryCount == 1)
                    for (int target = 0; target < targetSnapshot.Length; target++)
                        item.TargetBlobs[target] = item.EntryBlobs[target] ?? GetTargetBlob(item.LinkedCode, target);

            var result = new Shader[prepared.Length];
            for (int index = 0; index < prepared.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SlangBatchPreparation item = prepared[index];
                // Used-parameter queries and constructor indexing run only after the full join.
                var usedParameters = ReadSlangUsedParameters(item.LinkedCode, item.Reflections,
                    item.EntryCount, item.UnboundEntries);
                result[index] = new Shader(name, new SlangShaderData(global, session, item.LinkedCode), units, item.Entries,
                    targetSnapshot, item.Reflections.ToImmutable(), item.TargetBlobs.ToImmutableArray(),
                    item.EntryBlobs.ToImmutableArray(),
                    Enumerable.Repeat<byte[]?>(null, targetSnapshot.Length).ToImmutableArray(),
                    Enumerable.Repeat(Array.Empty<byte>(), item.EntryBlobs.Length).ToImmutableArray(),
                    item.EntryCount, usedParameters, item.UnboundEntries);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            GC.KeepAlive(prepared);
            GC.KeepAlive(inputs);
            GC.KeepAlive(units);
            GC.KeepAlive(session);
            GC.KeepAlive(global);
        }
    }

    private sealed record SlangBatchPreparation(
        ImmutableArray<IEntryPoint> Entries,
        IComponentType LinkedCode,
        bool Unbound,
        ImmutableArray<bool> UnboundEntries,
        ImmutableArray<ShaderReflection>.Builder Reflections,
        int EntryCount,
        ISlangBlob?[] TargetBlobs,
        ISlangBlob?[] EntryBlobs);

    public bool HasSlangData => true;
    public int SlangTargetCount => _slangTargets.Length;
    public int SlangEntryCount => _slangEntryCount;
    public int SlangSourceUnitCount => _slangUnits.Length;
    public IModule GetSlangSourceUnit(int index) => _slangUnits[index];
    public IEntryPoint GetSlangEntryPoint(int index) => _slangEntries[index];
    public SlangShaderData SlangCode { get; }
    // The native session is borrowed; retain the main SlangShaderData owner.
    internal ISession SlangSession => SlangCode.Session;
    public TargetDesc GetSlangTarget(int targetIndex) => _slangTargets[targetIndex];
    public ShaderReflection GetSlangReflection(int targetIndex) => _slangReflections[targetIndex];

    /// <summary>Finds the unique attributed source struct and resolves its linked target layout.</summary>
    /// <remarks>
    /// The returned layout borrows this Shader's native reflection. Keep the Shader alive while
    /// consuming it. Query after linking; this method does not authorize concurrent mutation
    /// of a shared Slang session. Attribute and layout-rule policy belong to the caller.
    /// </remarks>
    public TypeLayoutReflection GetSlangAttributedTypeLayout(
        int targetIndex,
        string attributeName,
        LayoutRules layoutRules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeName);
        try
        {
            var typeNames = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int unitIndex = 0; unitIndex < _slangUnits.Length; unitIndex++)
                Visit(_slangUnits[unitIndex].GetModuleReflection());
            if (typeNames.Count == 0)
                throw new InvalidDataException(
                    $"Shader '{Name}' exposes no {attributeName} type after IR restoration.");
            if (typeNames.Count != 1)
            {
                throw new InvalidDataException(
                    $"Shader '{Name}' exposes multiple {attributeName} types.");
            }

            string typeName = typeNames[0];
            ShaderReflection reflection = GetSlangReflection(targetIndex);
            TypeReflection type = reflection.FindTypeByName(typeName)
                ?? throw new InvalidDataException(
                    $"Shader '{Name}' has no linked {attributeName} type '{typeName}'.");
            TypeLayoutReflection layout = reflection.GetTypeLayout(type, layoutRules)
                ?? throw new InvalidDataException(
                    $"Shader '{Name}' has no layout for {attributeName} type '{type.Name}'.");
            return layout;

            void Visit(DeclReflection declaration)
            {
                if (declaration == DeclReflection.Null)
                    return;
                if (declaration.Kind == DeclReflectionKind.Struct)
                {
                    TypeReflection candidate = declaration.Type;
                    if (candidate != TypeReflection.Null && HasAttribute(candidate, attributeName) &&
                        !string.IsNullOrWhiteSpace(candidate.Name) &&
                        seen.Add(candidate.Name))
                    {
                        typeNames.Add(candidate.Name);
                    }
                }
                for (int index = 0; index < declaration.ChildrenCount; index++)
                    Visit(declaration.GetChild(checked((uint)index)));
            }

            static bool HasAttribute(TypeReflection type, string name)
            {
                for (uint index = 0; index < type.AttributeCount; index++)
                {
                    AttributeReflection attribute = type.GetAttribute(index);
                    if (attribute != AttributeReflection.Null &&
                        string.Equals(attribute.Name, name, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }
        }
        finally
        {
            GC.KeepAlive(this);
        }
    }

    /// <summary>Gets the index of a direct Slang entry in this shader target.</summary>
    public int GetSlangEntryIndex(int targetIndex, EntryPointReflection entry) =>
        GetEntryIndex(targetIndex, entry);

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

    /// <summary>Requires a nonempty, single-string entry attribute with consistent repeated declarations.</summary>
    /// <remarks>
    /// Reads this Shader's linked reflection and returns a managed value. Query after linking;
    /// this method does not authorize concurrent mutation of a shared Slang session.
    /// Attribute names and the interpretation of their values belong to the caller.
    /// </remarks>
    public string RequireSlangEntryStringAttribute(
        int targetIndex,
        string entryPoint,
        string attributeName,
        string subject)
    {
        try
        {
            if (!TryGetSlangEntry(
                    targetIndex,
                    entryPoint,
                    out EntryPointReflection entry))
            {
                throw new InvalidDataException(
                    $"{subject} has no Slang entry point named '{entryPoint}'.");
            }

            FunctionReflection function = entry.Function;
            string? result = null;
            for (uint index = 0; index < function.AttributeCount; index++)
            {
                AttributeReflection attribute = function.GetAttribute(index);
                if (attribute == AttributeReflection.Null ||
                    !string.Equals(attribute.Name, attributeName, StringComparison.Ordinal))
                {
                    continue;
                }
                if (attribute.ArgumentCount != 1)
                {
                    throw new InvalidDataException(
                        $"{subject} entry '{entryPoint}' has an invalid '{attributeName}' declaration.");
                }
                string value = attribute.GetArgumentValueString(0);
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new InvalidDataException(
                        $"{subject} entry '{entryPoint}' has an empty '{attributeName}' value.");
                }
                if (result is not null && !string.Equals(result, value, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"{subject} entry '{entryPoint}' has divergent '{attributeName}' declarations.");
                }
                result = value;
            }
            return result ?? throw new InvalidDataException(
                $"{subject} entry '{entryPoint}' must declare '{attributeName}'.");
        }
        finally
        {
            GC.KeepAlive(this);
        }
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
        usedParameters ??= ReadSlangUsedParameters(_slangCode, _slangReflections, _slangEntryCount, _slangUnboundEntries);
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
            _slangUnboundEntries,
            entryIndexSource: this);
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
        using var activity = s_compilationActivities.StartActivity("Shader.TargetCode");
        activity?.SetTag("target", targetIndex);
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
        using var activity = s_compilationActivities.StartActivity("Shader.EntryCode");
        activity?.SetTag("target", targetIndex);
        activity?.SetTag("entry", entryIndex);
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
