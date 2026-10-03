using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Typography;

internal static unsafe partial class FreeType
{
    [LibraryImport("freetype", EntryPoint = "FT_Init_FreeType")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CreateLibrary(out nint library);

    [LibraryImport("freetype", EntryPoint = "FT_New_Memory_Face")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CreateFace(nint library, byte* data, CLong length, CLong index, out nint face);

    [LibraryImport("freetype", EntryPoint = "FT_Done_Face")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DestroyFace(nint face);

    [LibraryImport("freetype", EntryPoint = "FT_Done_FreeType")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int DestroyLibrary(nint library);
}
