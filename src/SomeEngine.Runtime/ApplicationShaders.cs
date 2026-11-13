using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Runtime;

internal static class ApplicationShaders
{
    private static readonly object Gate = new();
    private static readonly Dictionary<SlangCompileTarget, ShaderLibrary> Libraries = [];

    static ApplicationShaders() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Close();

    internal static ShaderLibrary Get(SlangCompileTarget target)
    {
        lock (Gate)
        {
            if (!Libraries.TryGetValue(target, out ShaderLibrary? library))
                Libraries.Add(target, library = new ShaderLibrary(target));
            return library;
        }
    }

    private static void Close()
    {
        lock (Gate)
        {
            foreach (ShaderLibrary library in Libraries.Values) library.Dispose();
            Libraries.Clear();
        }
    }
}
