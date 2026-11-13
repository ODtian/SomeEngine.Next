using System.Text;
using SlangShaderSharp;
using SomeEngine.Diagnostics;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

public sealed partial class ShaderLibrary
{
    private sealed record ShaderComposition(Shader First, string SourceName, string Source, Shader[] Libraries, Shader Shader);
    private readonly List<ShaderComposition> _compositions = [];

    /// <summary>Composes generated entries with already loaded shader libraries on the Slang thread.</summary>
    public ValueTask<Shader> ComposeAsync(Shader first, string name, string sourceName, string source,
        IReadOnlyList<Shader> libraries, CancellationToken cancellationToken = default) => new(RunAsync(() =>
    {
        foreach (ShaderComposition composition in _compositions)
            if (ReferenceEquals(composition.First, first) && composition.SourceName == sourceName &&
                composition.Source == source && composition.Libraries.SequenceEqual(libraries)) return composition.Shader;

        RestoreShaderInputs([first, .. libraries]);

        var sourceUnits = new List<IModule>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        LoadLibrary(first);
        foreach (Shader library in libraries) LoadLibrary(library);

        string inputName = Path.GetFileNameWithoutExtension(sourceName) + "_" + _compositions.Count;
        IModule? unit;
        using (EngineTrace.Begin(LoadInputTrace))
            unit = _session.LoadModuleFromSource(inputName, inputName + ".slang",
                Slang.CreateBlob(Encoding.UTF8.GetBytes(source)), out ISlangBlob? diagnostics)
                ?? throw new InvalidDataException("Slang source composition failed: " + DiagnosticText(diagnostics));
        SlangShaderData.RetainSession(_global, _session, unit);
        sourceUnits.Add(unit);
        var entries = new List<IEntryPoint>();
        for (int index = 0; index < unit.GetDefinedEntryPointCount(); index++)
        {
            SlangResult result = unit.GetDefinedEntryPoint(index, out IEntryPoint entry);
            if (result.Failed) throw new InvalidDataException("Slang entry lookup failed: " + result);
            SlangShaderData.RetainSession(_global, _session, entry);
            entries.Add(entry);
        }
        SlangResult combineResult = _session.CreateCompositeComponentType([unit, .. entries],
            out IComponentType combined, out ISlangBlob? combineDiagnostics);
        if (combineResult.Failed)
            throw new InvalidDataException("Slang composition failed: " + DiagnosticText(combineDiagnostics));
        SlangShaderData.RetainSession(_global, _session, combined);
        using var linkTrace = EngineTrace.Begin(LinkTrace);
        SlangResult linkResult = combined.Link(out IComponentType linked, out ISlangBlob? linkDiagnostics);
        if (linkResult.Failed)
            throw new InvalidDataException("Slang link failed: " + DiagnosticText(linkDiagnostics));
        SlangShaderData.RetainSession(_global, _session, linked);
        Shader shader = Shader.CreateFromSlang(name, _global, _session, sourceUnits, entries,
            linked, _settings.Targets!, generateLibraryCode: false).WithDeclarationSources([first, .. libraries]);
        _compositions.Add(new(first, sourceName, source, libraries.ToArray(), shader));
        return shader;

        void LoadLibrary(Shader library)
        {
            LoadInputs(library);
            var roots = Enumerable.Range(0, library.SlangSourceUnitCount)
                .Select(index => library.GetSlangSourceUnit(index).GetName()).ToHashSet(StringComparer.Ordinal);
            foreach (IModule input in library.SlangDeclarationSources)
            {
                string name = input.GetName();
                if (roots.Contains(name) && names.Add(name)) sourceUnits.Add(_inputs[name].Source);
            }
        }
    }, cancellationToken));
}
