using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Schema;

public sealed partial class ShaderSourceBlock
{
    internal async ValueTask<Shader> CreateAsync(ShaderLibrary library, string name,
        ReadOnlyMemory<SpecializationArg> specializations, CancellationToken cancellationToken,
        ShaderInputSource[]? assetInputs = null, IReadOnlyList<Shader>? dependencies = null)
    {
        using ShaderSourceBlock copy = Duplicate();
        ShaderCompilation compiled = await library.CompileAsync(copy, name, specializations,
            generateCode: true, saveSources: false, cancellationToken,
            assetInputs: assetInputs, dependencies: dependencies).ConfigureAwait(false);
        if (!specializations.IsEmpty || compiled.Specializations.Count == 0) return compiled.Shader;
        var variants = new List<(string EntryPoint, string[] Arguments, Shader Shader)>(compiled.Specializations.Count);
        foreach (ShaderSpecialization specialization in compiled.Specializations)
        {
            Shader variant = (await library.CompileAsync(copy, name, specialization.ToSlangArguments(),
                generateCode: true, saveSources: false, cancellationToken, specialization.EntryPoint,
                assetInputs, dependencies, compiled.Shader).ConfigureAwait(false)).Shader;
            variants.Add((specialization.EntryPoint, specialization.Arguments.ToArray(), variant));
        }
        return compiled.Shader.WithSpecializations(variants);
    }

    public ValueTask<ShaderCompilation> CompileAsync(IGlobalSession slang, SessionDesc settings,
        string name, CancellationToken cancellationToken = default) =>
        CompileAsync(slang, settings, name, default(ReadOnlyMemory<SpecializationArg>), cancellationToken);

    public async ValueTask<ShaderCompilation> CompileAsync(IGlobalSession slang, SessionDesc settings,
        string name, ReadOnlyMemory<SpecializationArg> specializations, CancellationToken cancellationToken = default)
    {
        using var library = new ShaderLibrary(slang, settings);
        return await library.CompileAsync(this, name, specializations, generateCode: true,
            saveSources: true, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ShaderCompilation> CompileAsync(IGlobalSession slang, SessionDesc settings,
        string name, string entryPoint, ReadOnlyMemory<SpecializationArg> specializations,
        CancellationToken cancellationToken = default)
    {
        using var library = new ShaderLibrary(slang, settings);
        return await library.CompileAsync(this, name, specializations, generateCode: true,
            saveSources: true, cancellationToken, entryPoint).ConfigureAwait(false);
    }

    internal async ValueTask<Shader> CreateUsingSavedCodeAsync(ShaderLibrary library, string name,
        ReadOnlyMemory<SpecializationArg> specializations, CancellationToken cancellationToken, string? entryPoint,
        ShaderInputSource[]? assetInputs = null, IReadOnlyList<Shader>? dependencies = null, Shader? inputShader = null) =>
        (await library.CompileAsync(this, name, specializations, generateCode: false,
            saveSources: false, cancellationToken, entryPoint, assetInputs, dependencies, inputShader).ConfigureAwait(false)).Shader;

    internal ShaderSourceBlock FromInputs(IReadOnlyList<ShaderInputSource> inputs)
    {
        var source = new ShaderSourceBlock { SourceNames = inputs.Select(input => input.Name).ToList(),
            Specializations = Specializations.ToList(), SourceOnly = SourceOnly, Dependencies = Dependencies.ToList() };
        for (int index = 0; index < inputs.Count; index++) source.SetSerializedSource(index, inputs[index].Serialized.Span);
        return source;
    }
}
