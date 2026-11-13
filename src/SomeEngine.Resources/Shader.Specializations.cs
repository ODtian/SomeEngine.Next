using System.Collections.Immutable;
using SlangShaderSharp;

namespace SomeEngine.Resources;

public partial class Shader
{
    private readonly ImmutableArray<(string EntryPoint, ImmutableArray<string> Arguments, Shader Shader)> _specializations;

    /// <summary>Whether the selected entry requires arguments or declares offline-compiled variants.</summary>
    public bool HasSpecializations(string entryPoint)
    {
        if (_slangNamedEntries.TryGetValue((0, entryPoint), out var entry) && _slangUnboundEntries[entry.Index])
            return true;
        return !_specializations.IsDefaultOrEmpty &&
            _specializations.Any(variant => variant.EntryPoint == entryPoint);
    }

    /// <summary>Returns an already loaded entry specialization.</summary>
    public bool TryGetSpecialization(string entryPoint, ReadOnlySpan<string> arguments, out Shader shader)
    {
        if (!_specializations.IsDefaultOrEmpty)
            foreach (var variant in _specializations)
                if (variant.EntryPoint == entryPoint && arguments.SequenceEqual(variant.Arguments.AsSpan()))
                {
                    shader = variant.Shader;
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
        new(Name, SlangCode, _slangUnits, _slangEntries,
            _slangTargets, _slangReflections, _slangTargetBlobs, _slangEntryBlobs,
            _slangTargetBytes, _slangEntryBytes, _slangEntryCount, _slangUsedParameters, _slangUnboundEntries,
            variants.Select(static variant =>
                (variant.EntryPoint, variant.Arguments.ToImmutableArray(), variant.Shader)).ToImmutableArray(),
            _slangDeclarationSources);

    internal Shader WithDeclarationSources(IReadOnlyList<Shader> dependencies)
    {
        if (dependencies.Count == 0) return this;
        var sources = dependencies.SelectMany(static dependency => dependency._slangDeclarationSources)
            .Concat(_slangDeclarationSources).DistinctBy(static source => source.GetName(), StringComparer.Ordinal)
            .ToImmutableArray();
        return WithDeclarationSources(sources);
    }

    private Shader WithDeclarationSources(ImmutableArray<IModule> sources)
    {
        var variants = _specializations.IsDefault ? default : _specializations.Select(variant =>
            (variant.EntryPoint, variant.Arguments, variant.Shader.WithDeclarationSources(sources))).ToImmutableArray();
        return new(Name, SlangCode, _slangUnits, _slangEntries,
            _slangTargets, _slangReflections, _slangTargetBlobs, _slangEntryBlobs,
            _slangTargetBytes, _slangEntryBytes, _slangEntryCount, _slangUsedParameters, _slangUnboundEntries,
            variants, sources);
    }
}
