using System.Collections.Immutable;

namespace SomeEngine.Resources;

public partial class Shader
{
    private readonly ImmutableArray<(string EntryPoint, ImmutableArray<string> Arguments, Lazy<Shader> Shader)> _specializations;

    /// <summary>Whether the selected entry requires arguments or declares offline-compiled variants.</summary>
    public bool HasSpecializations(string entryPoint)
    {
        if (_slangNamedEntries.TryGetValue((0, entryPoint), out var entry) && _slangUnboundEntries[entry.Index])
            return true;
        return !_specializations.IsDefaultOrEmpty &&
            _specializations.Any(variant => variant.EntryPoint == entryPoint);
    }

    /// <summary>Restores saved entry reflection once without generating GPU code or substituting another variant.</summary>
    public bool TryGetSpecialization(string entryPoint, ReadOnlySpan<string> arguments, out Shader shader)
    {
        if (!_specializations.IsDefaultOrEmpty)
            foreach (var variant in _specializations)
                if (variant.EntryPoint == entryPoint && arguments.SequenceEqual(variant.Arguments.AsSpan()))
                {
                    shader = variant.Shader.Value;
                    return true;
                }
        shader = null!;
        return false;
    }

    public Shader RequireSpecialization(string entryPoint, ReadOnlySpan<string> arguments)
    {
        if (!HasSpecializations(entryPoint) && arguments.IsEmpty) return this;
        if (TryGetSpecialization(entryPoint, arguments, out Shader shader)) return shader;
        throw new InvalidDataException(
            $"Shader '{Name}' has no saved specialization for entry '{entryPoint}' with [{string.Join(", ", arguments.ToArray())}].");
    }

    internal Shader WithSpecializations(IReadOnlyList<(string EntryPoint, string[] Arguments, Shader Shader)> variants) =>
        WithSpecializations(variants.Select(static variant =>
            (variant.EntryPoint, variant.Arguments, new Lazy<Shader>(() => variant.Shader))).ToArray());

    internal Shader WithSpecializations(IReadOnlyList<(string EntryPoint, string[] Arguments, Lazy<Shader> Shader)> variants) =>
        new(Name, SlangCode, _slangUnits, _slangEntries,
            _slangTargets, _slangReflections, _slangTargetBlobs, _slangEntryBlobs,
            _slangTargetBytes, _slangEntryBytes, _slangEntryCount, _slangUsedParameters, _slangUnboundEntries,
            variants.Select(static variant =>
                (variant.EntryPoint, variant.Arguments.ToImmutableArray(), variant.Shader)).ToImmutableArray());
}
