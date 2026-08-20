using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Tests;

internal sealed class ConformanceShaderProgram : IDisposable
{
    private static readonly Lazy<IGlobalSession> s_global = new(
        CreateGlobalSession,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly List<ComObject> _owned;
    private bool _disposed;

    private ConformanceShaderProgram(
        List<ComObject> owned,
        IComponentType program,
        EntryPointReflection entryPoint)
    {
        _owned = owned;
        Program = program;
        EntryPoint = entryPoint;
    }

    internal IComponentType Program { get; }
    internal EntryPointReflection EntryPoint { get; }

    internal static ConformanceShaderProgram CompileCompute()
    {
        const string source = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
            }
            """;
        var owned = new List<ComObject>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        try
        {
            IGlobalSession global = s_global.Value;
            SessionDesc description = new()
            {
                Targets =
                [
                    new TargetDesc
                    {
                        Format = SlangCompileTarget.Dxil,
                        Profile = global.FindProfile("sm_6_0"),
                    },
                ],
                DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
            };
            RequireSuccess(global.CreateSession(description, out ISession session));
            Track(session);
            ISlangBlob sourceBlob = Track(Slang.CreateBlob(source));
            string virtualPath = Path.Combine(
                AppContext.BaseDirectory,
                "strict_conformance_compute.slang");
            IModule? module = session.LoadModuleFromSource(
                "strict_conformance_compute",
                virtualPath,
                sourceBlob,
                out ISlangBlob? moduleDiagnostics);
            TrackOptional(moduleDiagnostics);
            if (module is null)
                throw new InvalidDataException(moduleDiagnostics?.AsString ?? "Slang module load failed.");
            Track(module);
            RequireSuccess(module.FindEntryPointByName("computeMain", out IEntryPoint entryPoint));
            Track(entryPoint);
            IComponentType[] components = [module, entryPoint];
            SlangResult compose = session.CreateCompositeComponentType(
                components,
                out IComponentType? composite,
                out ISlangBlob? composeDiagnostics);
            TrackOptional(composeDiagnostics);
            if (!compose.Succeeded || composite is null)
                throw new InvalidDataException(composeDiagnostics?.AsString ?? "Slang composition failed.");
            Track(composite);
            SlangResult link = composite.Link(
                out IComponentType? linked,
                out ISlangBlob? linkDiagnostics);
            TrackOptional(linkDiagnostics);
            if (!link.Succeeded || linked is null)
                throw new InvalidDataException(linkDiagnostics?.AsString ?? "Slang link failed.");
            Track(linked);
            ShaderReflection reflection = linked.GetLayout(0, out ISlangBlob? layoutDiagnostics);
            TrackOptional(layoutDiagnostics);
            if (reflection == ShaderReflection.Null || reflection.EntryPointCount != 1)
                throw new InvalidDataException(layoutDiagnostics?.AsString ?? "Slang reflection failed.");
            EntryPointReflection reflected = reflection.GetEntryPointByIndex(0);
            if (reflected == EntryPointReflection.Null || reflected.Stage != SlangStage.Compute)
                throw new InvalidDataException("Slang did not expose the compute entry point.");
            return new ConformanceShaderProgram(owned, linked, reflected);
        }
        catch
        {
            Release(owned);
            throw;
        }

        T Track<T>(T value)
            where T : class
        {
            if (value is ComObject wrapper && seen.Add(wrapper))
                owned.Add(wrapper);
            return value;
        }

        void TrackOptional(object? value)
        {
            if (value is ComObject wrapper && seen.Add(wrapper))
                owned.Add(wrapper);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Release(_owned);
        _disposed = true;
    }

    private static IGlobalSession CreateGlobalSession()
    {
        RequireSuccess(Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession session));
        return session;
    }

    private static void RequireSuccess(SlangResult result)
    {
        if (!result.Succeeded)
            throw new InvalidDataException($"Slang operation failed with {result}.");
    }

    private static void Release(List<ComObject> objects)
    {
        for (int index = objects.Count - 1; index >= 0; index--)
            objects[index].FinalRelease();
        objects.Clear();
    }
}
