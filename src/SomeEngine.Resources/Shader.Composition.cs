using System.Text;
using SlangShaderSharp;
using SomeEngine.Graphics;

namespace SomeEngine.Resources;

public partial class Shader
{
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

}
