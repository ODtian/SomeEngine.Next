using System.Numerics;
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

    [LibraryImport("freetype", EntryPoint = "SomeEngine_RenderGlyph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int RenderGlyph(nint library, nint face, uint glyph, uint fontSize, int spread, out FreeTypeBitmap bitmap);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FreeTypeBitmap
{
    internal int Width;
    internal int Height;
    internal int Pitch;
    internal int Left;
    internal int Top;
    internal byte* Pixels;
}

internal readonly record struct GlyphBitmap(int Width, int Height, Vector2 Offset, byte[] Pixels);
