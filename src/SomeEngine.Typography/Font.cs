using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Typography;

/// <summary>A font loaded from asset bytes. Dispose it after its shaped text and atlas data.</summary>
public sealed class Font : IDisposable
{
    internal FontData Data { get; }
    internal FontExtents Extents { get; }
    public uint UnitsPerEm { get; }

    private Font(FontData data)
    {
        Data = data;
        UnitsPerEm = HarfBuzz.GetUnitsPerEm(data.Face);
        HarfBuzz.GetFontExtents(data, out FontExtents extents);
        Extents = extents;
    }

    public static Font Load(ReadOnlyMemory<byte> data) => new(FontData.Load(data.Span));

    public void Dispose() => Data.Dispose();

    internal bool Supports(ReadOnlySpan<char> text)
    {
        DevelopmentCheck.Require(!Data.IsClosed, "The font must be alive while shaping.");
        foreach (Rune character in text.EnumerateRunes())
            if (HarfBuzz.GetGlyph(Data, (uint)character.Value, out _) == 0)
                return false;
        return true;
    }
}

internal sealed unsafe class FontData : SafeHandle
{
    private nint library;
    private nint freeTypeFace;
    private byte* bytes;
    private int byteLength;
    private readonly object glyphBitmapLock = new();
    internal nint Face { get; private set; }

    private FontData() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    internal static FontData Load(ReadOnlySpan<byte> source)
    {
        var data = new FontData();
        try
        {
            data.byteLength = source.Length;
            data.bytes = (byte*)NativeMemory.Alloc((nuint)source.Length);
            if (data.bytes == null && source.Length != 0)
                throw new OutOfMemoryException();
            source.CopyTo(new Span<byte>(data.bytes, source.Length));
            int error = FreeType.CreateLibrary(out data.library);
            if (error != 0)
                throw new InvalidOperationException($"FreeType initialization failed: {error}.");
            error = FreeType.CreateFace(data.library, data.bytes, new CLong(source.Length), new CLong(0), out data.freeTypeFace);
            if (error != 0)
                throw new InvalidDataException($"FreeType font loading failed: {error}.");

            nint blob = HarfBuzz.CreateBlob(data.bytes, (uint)source.Length, HarfBuzz.ReadOnlyData, 0, 0);
            try
            {
                data.Face = HarfBuzz.CreateFace(blob, 0);
                data.SetHandle(HarfBuzz.CreateFont(data.Face));
            }
            finally
            {
                HarfBuzz.DestroyBlob(blob);
            }
            HarfBuzz.SetOpenTypeFunctions(data.handle);
            HarfBuzz.MakeFontImmutable(data.handle);
            return data;
        }
        catch
        {
            data.ReleaseHandle();
            data.SetHandleAsInvalid();
            throw;
        }
    }

    internal GlyphBitmap RenderGlyph(uint glyph, int fontSize, int spread)
    {
        bool retained = false;
        DangerousAddRef(ref retained);
        try
        {
            lock (glyphBitmapLock)
            {
                return RenderGlyph(library, freeTypeFace, glyph, fontSize, spread);
            }
        }
        finally
        {
            if (retained) DangerousRelease();
        }
    }

    private static GlyphBitmap RenderGlyph(nint library, nint face, uint glyph, int fontSize, int spread)
    {
        int error = FreeType.RenderGlyph(library, face, glyph, (uint)fontSize, spread, out FreeTypeBitmap bitmap);
        if (error != 0)
            throw new InvalidOperationException($"FreeType glyph rasterization failed: {error}.");
        byte[] pixels = new byte[checked(bitmap.Width * bitmap.Height)];
        for (int row = 0; row < bitmap.Height; row++)
        {
            byte* source = bitmap.Pixels + row * bitmap.Pitch;
            new ReadOnlySpan<byte>(source, bitmap.Width).CopyTo(pixels.AsSpan(row * bitmap.Width, bitmap.Width));
        }
        return new GlyphBitmap(bitmap.Width, bitmap.Height, new Vector2(bitmap.Left, -bitmap.Top), pixels);
    }

    internal GlyphRasterizer CreateGlyphRasterizer() => new(this);

    internal sealed class GlyphRasterizer : IDisposable
    {
        private readonly FontData data;
        private bool retained;
        private nint library;
        private nint face;

        internal GlyphRasterizer(FontData data)
        {
            this.data = data;
            data.DangerousAddRef(ref retained);
            try
            {
                int error = FreeType.CreateLibrary(out library);
                if (error != 0)
                    throw new InvalidOperationException($"FreeType initialization failed: {error}.");
                error = FreeType.CreateFace(library, data.bytes, new CLong(data.byteLength), new CLong(0), out face);
                if (error != 0)
                    throw new InvalidDataException($"FreeType font loading failed: {error}.");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal GlyphBitmap RenderGlyph(uint glyph, int fontSize, int spread) =>
            FontData.RenderGlyph(library, face, glyph, fontSize, spread);

        public void Dispose()
        {
            if (face != 0) FreeType.DestroyFace(face);
            if (library != 0) FreeType.DestroyLibrary(library);
            if (retained) data.DangerousRelease();
        }
    }

    internal byte[] GetAssetHash()
    {
        DevelopmentCheck.Require(!IsClosed, "The font must be alive while saving or loading atlas data.");
        bool retained = false;
        DangerousAddRef(ref retained);
        try
        {
            return SHA256.HashData(new ReadOnlySpan<byte>(bytes, byteLength));
        }
        finally
        {
            if (retained) DangerousRelease();
        }
    }

    protected override bool ReleaseHandle()
    {
        if (handle != 0) HarfBuzz.DestroyFont(handle);
        if (Face != 0) HarfBuzz.DestroyFace(Face);
        if (freeTypeFace != 0) FreeType.DestroyFace(freeTypeFace);
        if (library != 0) FreeType.DestroyLibrary(library);
        NativeMemory.Free(bytes);
        return true;
    }
}
