using System.Collections.Immutable;
using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;

namespace SomeEngine.Resources;

public partial class Shader
{
    private ISession? _slangSession;
    private ImmutableArray<IModule> _slangUnits = [];
    private ImmutableArray<IEntryPoint> _slangEntries = [];
    private IComponentType? _slangCode;
    private ImmutableArray<TargetDesc> _slangTargets = [];
    private ImmutableArray<ShaderReflection> _slangReflections = [];
    private ImmutableArray<ISlangBlob> _slangEntryCode = [];
    private int _slangEntryCount;

    /// <summary>
    /// Creates a resident shader by taking ownership of the supplied Slang interfaces.
    /// Slang reflection and generated code stay valid until this shader is updated or disposed.
    /// </summary>
    public static Shader CreateFromSlang(
        string name,
        ISession session,
        IReadOnlyList<IModule> sourceUnits,
        IReadOnlyList<IEntryPoint> entries,
        IComponentType linkedCode,
        ReadOnlySpan<TargetDesc> targets)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(sourceUnits);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(linkedCode);
        if (targets.IsEmpty)
            throw new ArgumentException("Creating a shader requires at least one Slang target.", nameof(targets));

        var reflections = ImmutableArray.CreateBuilder<ShaderReflection>(targets.Length);
        var code = ImmutableArray.CreateBuilder<ISlangBlob>();
        try
        {
            int entryCount = -1;
            for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
            {
                ShaderReflection reflection = linkedCode.GetLayout(targetIndex, out ISlangBlob? layoutDiagnostics);
                try
                {
                    if (reflection == ShaderReflection.Null)
                    {
                        throw new InvalidDataException(
                            $"Slang did not produce reflection for target {targetIndex}: " +
                            DiagnosticText(layoutDiagnostics));
                    }
                }
                finally
                {
                    Release(layoutDiagnostics);
                }

                int reflectedEntryCount = checked((int)reflection.EntryPointCount);
                if (entryCount < 0)
                    entryCount = reflectedEntryCount;
                else if (entryCount != reflectedEntryCount)
                    throw new InvalidDataException("Slang targets expose different entry-point counts.");
                if (reflectedEntryCount != entries.Count)
                    throw new InvalidDataException("Slang reflection differs from the supplied entry points.");

                reflections.Add(reflection);
                for (int entryIndex = 0; entryIndex < reflectedEntryCount; entryIndex++)
                {
                    SlangResult result = linkedCode.GetEntryPointCode(
                        entryIndex,
                        targetIndex,
                        out ISlangBlob entryCode,
                        out ISlangBlob? codeDiagnostics);
                    try
                    {
                        if (result.Failed || entryCode is null || entryCode.GetBufferSize() == 0)
                        {
                            Release(entryCode);
                            throw new InvalidDataException(
                                $"Slang did not produce code for target {targetIndex}, entry {entryIndex}: " +
                                DiagnosticText(codeDiagnostics));
                        }
                    }
                    finally
                    {
                        Release(codeDiagnostics);
                    }
                    code.Add(entryCode);
                }
            }

            return new Shader
            {
                Name = name,
                _slangSession = session,
                _slangUnits = sourceUnits.ToImmutableArray(),
                _slangEntries = entries.ToImmutableArray(),
                _slangCode = linkedCode,
                _slangTargets = targets.ToArray().ToImmutableArray(),
                _slangReflections = reflections.ToImmutable(),
                _slangEntryCode = code.ToImmutable(),
                _slangEntryCount = entryCount,
            };
        }
        catch
        {
            Release(code);
            Release(linkedCode);
            ReleaseReverse(entries);
            ReleaseReverse(sourceUnits);
            Release(session);
            throw;
        }
    }

    public bool HasSlangData
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _slangCode is not null;
        }
    }

    public int SlangTargetCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _slangTargets.Length;
        }
    }

    public int SlangEntryCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _slangEntryCount;
        }
    }

    public IComponentType SlangCode
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _slangCode ?? throw new InvalidOperationException("The shader has no direct Slang data.");
        }
    }

    public TargetDesc GetSlangTarget(int targetIndex)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return _slangTargets[targetIndex];
    }

    public ShaderReflection GetSlangReflection(int targetIndex)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return _slangReflections[targetIndex];
    }

    public ISlangBlob GetSlangEntryCode(int targetIndex, int entryIndex)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        if (targetIndex >= _slangTargets.Length)
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        if (entryIndex >= _slangEntryCount)
            throw new ArgumentOutOfRangeException(nameof(entryIndex));
        return _slangEntryCode[checked(targetIndex * _slangEntryCount + entryIndex)];
    }

    public bool TryGetSlangTarget(SlangCompileTarget format, out int targetIndex)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
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

    public bool TryGetSlangEntry(
        int targetIndex,
        string name,
        SlangStage stage,
        out EntryPointReflection entry,
        out ISlangBlob code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ShaderReflection reflection = GetSlangReflection(targetIndex);
        for (int entryIndex = 0; entryIndex < _slangEntryCount; entryIndex++)
        {
            EntryPointReflection candidate = reflection.GetEntryPointByIndex(checked((nuint)entryIndex));
            if (candidate.Stage == stage && string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                entry = candidate;
                code = GetSlangEntryCode(targetIndex, entryIndex);
                return true;
            }
        }
        entry = EntryPointReflection.Null;
        code = null!;
        return false;
    }

    private void ReplaceSlangData(Shader replacement)
    {
        ISession? oldSession = _slangSession;
        ImmutableArray<IModule> oldUnits = _slangUnits;
        ImmutableArray<IEntryPoint> oldEntries = _slangEntries;
        IComponentType? oldCode = _slangCode;
        ImmutableArray<ISlangBlob> oldEntryCode = _slangEntryCode;

        _slangSession = replacement._slangSession;
        _slangUnits = replacement._slangUnits;
        _slangEntries = replacement._slangEntries;
        _slangCode = replacement._slangCode;
        _slangTargets = replacement._slangTargets;
        _slangReflections = replacement._slangReflections;
        _slangEntryCode = replacement._slangEntryCode;
        _slangEntryCount = replacement._slangEntryCount;

        replacement.ClearSlangData();
        Release(oldEntryCode);
        Release(oldCode);
        ReleaseReverse(oldEntries);
        ReleaseReverse(oldUnits);
        Release(oldSession);
    }

    private void DisposeSlangData()
    {
        ImmutableArray<ISlangBlob> entryCode = _slangEntryCode;
        IComponentType? slangCode = _slangCode;
        ImmutableArray<IEntryPoint> entries = _slangEntries;
        ImmutableArray<IModule> units = _slangUnits;
        ISession? session = _slangSession;
        ClearSlangData();
        Release(entryCode);
        Release(slangCode);
        ReleaseReverse(entries);
        ReleaseReverse(units);
        Release(session);
    }

    private void ClearSlangData()
    {
        _slangSession = null;
        _slangUnits = [];
        _slangEntries = [];
        _slangCode = null;
        _slangTargets = [];
        _slangReflections = [];
        _slangEntryCode = [];
        _slangEntryCount = 0;
    }

    private static string DiagnosticText(ISlangBlob? diagnostics)
        => diagnostics is null || diagnostics.GetBufferSize() == 0
            ? "no diagnostics"
            : diagnostics.AsString;

    private static void Release<T>(IEnumerable<T> values)
        where T : class
    {
        foreach (T value in values)
            Release(value);
    }

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
