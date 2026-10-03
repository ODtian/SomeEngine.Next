using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Typography;

internal static unsafe partial class HarfBuzz
{
    internal const int LeftToRight = 4;
    internal const int ReadOnlyData = 1;

    [LibraryImport("harfbuzz", EntryPoint = "hb_blob_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CreateBlob(byte* data, uint length, int mode, nint userData, nint destroy);

    [LibraryImport("harfbuzz", EntryPoint = "hb_blob_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyBlob(nint blob);

    [LibraryImport("harfbuzz", EntryPoint = "hb_face_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CreateFace(nint blob, uint index);

    [LibraryImport("harfbuzz", EntryPoint = "hb_face_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyFace(nint face);

    [LibraryImport("harfbuzz", EntryPoint = "hb_face_get_upem")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetUnitsPerEm(nint face);

    [LibraryImport("harfbuzz", EntryPoint = "hb_face_get_glyph_count")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetGlyphCount(nint face);

    [LibraryImport("harfbuzz", EntryPoint = "hb_font_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CreateFont(nint face);

    [LibraryImport("harfbuzz", EntryPoint = "hb_ot_font_set_funcs")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetOpenTypeFunctions(nint font);

    [LibraryImport("harfbuzz", EntryPoint = "hb_font_make_immutable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void MakeFontImmutable(nint font);

    [LibraryImport("harfbuzz", EntryPoint = "hb_font_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyFont(nint font);

    [LibraryImport("harfbuzz", EntryPoint = "hb_font_get_h_extents")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetFontExtents(FontData font, out FontExtents extents);

    [LibraryImport("harfbuzz", EntryPoint = "hb_font_get_nominal_glyph")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetGlyph(FontData font, uint character, out uint glyph);

    [LibraryImport("harfbuzz", EntryPoint = "hb_buffer_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint CreateBuffer();

    [LibraryImport("harfbuzz", EntryPoint = "hb_buffer_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyBuffer(nint buffer);

    [LibraryImport("harfbuzz", EntryPoint = "hb_buffer_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void ResetBuffer(GlyphBuffer buffer);

    [LibraryImport("harfbuzz", EntryPoint = "hb_buffer_add_utf16")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void AddText(GlyphBuffer buffer, char* text, int length, uint offset, int count);

    [LibraryImport("harfbuzz", EntryPoint = "hb_buffer_set_direction")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void SetDirection(GlyphBuffer buffer, int direction);

    [LibraryImport("harfbuzz", EntryPoint = "hb_buffer_guess_segment_properties")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void GuessSegmentProperties(GlyphBuffer buffer);

    [LibraryImport("harfbuzz", EntryPoint = "hb_shape")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Shape(FontData font, GlyphBuffer buffer, nint features, uint featureCount);

    [LibraryImport("harfbuzz", EntryPoint = "hb_buffer_allocation_successful")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int IsBufferAllocated(GlyphBuffer buffer);

    [LibraryImport("harfbuzz", EntryPoint = "hb_buffer_get_glyph_infos")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GlyphInfo* GetGlyphInfo(GlyphBuffer buffer, out uint count);

    [LibraryImport("harfbuzz", EntryPoint = "hb_buffer_get_glyph_positions")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial GlyphPosition* GetGlyphPositions(GlyphBuffer buffer, out uint count);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FontExtents
{
    internal int Ascent;
    internal int Descent;
    internal int LineGap;
    private fixed int reserved[9];
}

[StructLayout(LayoutKind.Sequential)]
internal struct GlyphInfo
{
    internal uint Index;
    private uint mask;
    internal uint Cluster;
    private uint data1;
    private uint data2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GlyphPosition
{
    internal int AdvanceX;
    internal int AdvanceY;
    internal int OffsetX;
    internal int OffsetY;
    private uint data;
}
