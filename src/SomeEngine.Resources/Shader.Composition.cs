using System.Text;
using SlangShaderSharp;

namespace SomeEngine.Resources;

public partial class Shader
{
    private sealed record ComposedShader(string SourceName, string Source, SlangCompileTarget? Target, Shader[] Libraries, Shader Shader);
    private readonly List<ComposedShader> _composedShaders = [];

    /// <summary>Creates a Slang compiler that locates the application's downstream compiler libraries.</summary>
    public static IGlobalSession CreateSlangGlobalSession()
    {
        SlangResult result = Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession slang);
        if (result.Failed || slang is null)
            throw new InvalidOperationException($"Slang global session creation failed with {result}.");
        if (OperatingSystem.IsWindows())
            slang.SetDownstreamCompilerPath(SlangPassThrough.Dxc, AppContext.BaseDirectory);
        return slang;
    }

    /// <summary>Returns the Slang import name of a resident source unit.</summary>
    public string GetSlangSourceUnitName(int index) => _slangUnits[index].GetName();

    /// <summary>Compiles new entries against this shader and the supplied resident shader libraries.</summary>
    /// <param name="sourceName">Source path for diagnostics; its file name without extension names the new Slang unit.</param>
    /// <param name="source">Complete source text, including imports using the libraries' Slang source unit names.</param>
    public Shader Compose(string name, string sourceName, string source, IReadOnlyList<Shader> libraries,
        SlangCompileTarget? target = null)
    {
        lock (_composedShaders)
        {
            foreach (ComposedShader composition in _composedShaders)
                if (composition.SourceName == sourceName && composition.Source == source && composition.Target == target &&
                    composition.Libraries.SequenceEqual(libraries)) return composition.Shader;
            Shader shader = CompileComposition(name, sourceName, source, libraries, target);
            _composedShaders.Add(new(sourceName, source, target, libraries.ToArray(), shader));
            return shader;
        }
    }

    private Shader CompileComposition(string name, string sourceName, string source, IReadOnlyList<Shader> libraries,
        SlangCompileTarget? target)
    {
        TargetDesc[] targets = target is { } format ? [_slangTargets[RequireSlangTarget(format)]] : _slangTargets.ToArray();
        var description = new SessionDesc
        {
            Targets = targets,
            DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            CompilerOptionEntries =
            [
                new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(0, 0)),
                new(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
                new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
                new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0)),
            ],
        };
        SlangResult sessionResult = _slangGlobal.CreateSession(description, out ISession session);
        if (sessionResult.Failed || session is null)
            throw new InvalidDataException($"Slang session creation failed with {sessionResult}.");

        var sourceUnits = new List<IModule>();
        var unitNames = new HashSet<string>(StringComparer.Ordinal);
        LoadLibrary(this);
        foreach (Shader library in libraries) LoadLibrary(library);

        IModule? sourceUnit = session.LoadModuleFromSource(Path.GetFileNameWithoutExtension(sourceName), sourceName,
            Slang.CreateBlob(Encoding.UTF8.GetBytes(source)), out ISlangBlob? diagnostics);
        if (sourceUnit is null)
            throw new InvalidDataException($"Slang could not load source '{sourceName}': {DiagnosticText(diagnostics)}");
        sourceUnits.Add(sourceUnit);

        var entries = new List<IEntryPoint>();
        for (int index = 0; index < sourceUnit.GetDefinedEntryPointCount(); index++)
        {
            SlangResult entryResult = sourceUnit.GetDefinedEntryPoint(index, out IEntryPoint entry);
            if (entryResult.Failed || entry is null)
                throw new InvalidDataException($"Slang could not return a declared entry point from '{sourceName}'.");
            entries.Add(entry);
        }

        SlangResult combineResult = session.CreateCompositeComponentType([sourceUnit, .. entries],
            out IComponentType combinedCode, out diagnostics);
        if (combineResult.Failed || combinedCode is null)
            throw new InvalidDataException("Slang composition failed: " + DiagnosticText(diagnostics));
        SlangResult linkResult = combinedCode.Link(out IComponentType linkedCode, out diagnostics);
        if (linkResult.Failed || linkedCode is null)
            throw new InvalidDataException("Slang link failed: " + DiagnosticText(diagnostics));

        return CreateFromSlang(name, _slangGlobal, session, sourceUnits, entries, linkedCode, targets,
            generateLibraryCode: false);

        void LoadLibrary(Shader library)
        {
            foreach (IModule unit in library._slangUnits)
            {
                string unitName = unit.GetName();
                if (!unitNames.Add(unitName)) continue;
                SlangResult serializeResult = unit.Serialize(out ISlangBlob serialized);
                if (serializeResult.Failed)
                    throw new InvalidDataException($"Slang could not serialize source '{unitName}'.");
                IModule? loaded = session.LoadModuleFromIRBlob(unitName, unit.GetFilePath(), serialized, out ISlangBlob? loadDiagnostics);
                if (loaded is null)
                    throw new InvalidDataException($"Slang could not load source '{unitName}': {DiagnosticText(loadDiagnostics)}");
                sourceUnits.Add(loaded);
            }
        }
    }
}
