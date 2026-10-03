using System.Numerics;
using System.Text;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Typography;

public sealed partial class GlyphAtlas
{
    private const int FileSignature = 0x46445347; // GSDF
    private const int FileVersion = 1;

    /// <summary>Prebakes the font's shaped glyph numbers through the same entry point as dynamic queries.</summary>
    public void Bake(Font font, ReadOnlySpan<uint> glyphIndices)
    {
        foreach (uint glyph in glyphIndices) Get(font, glyph);
    }

    /// <summary>Writes baked pixels, glyph positions, and font asset hashes. The caller keeps the stream open.</summary>
    public void Save(Stream destination)
    {
        DevelopmentCheck.Require(destination is not null && destination.CanWrite, "Saving an atlas needs a writable stream.");
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(FileSignature);
        writer.Write(FileVersion);
        writer.Write(PageWidth);
        writer.Write(PageHeight);
        writer.Write(FontSize);
        writer.Write(DistanceSpread);
        writer.Write(pages.Count);
        foreach (AtlasPage page in pages) page.WriteTo(writer);

        var fonts = glyphs.GroupBy(entry => entry.Key.Font).ToArray();
        writer.Write(fonts.Length);
        foreach (var fontGlyphs in fonts)
        {
            writer.Write(fontGlyphs.Key.Data.GetAssetHash());
            writer.Write(fontGlyphs.Count());
            foreach (var entry in fontGlyphs)
            {
                writer.Write(entry.Key.Glyph);
                GlyphLocation location = entry.Value;
                writer.Write(location.Region.PageIndex);
                writer.Write(location.Region.X);
                writer.Write(location.Region.Y);
                writer.Write(location.Region.Width);
                writer.Write(location.Region.Height);
                writer.Write(location.Offset.X);
                writer.Write(location.Offset.Y);
            }
        }
    }

    /// <summary>Loads baked glyphs and binds them to fonts with matching asset bytes. The stream stays open.</summary>
    public static GlyphAtlas Load(Stream source, ReadOnlySpan<Font> fonts)
    {
        DevelopmentCheck.Require(source is not null && source.CanRead, "Loading an atlas needs a readable stream.");
        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != FileSignature || reader.ReadInt32() != FileVersion)
            throw new InvalidDataException("The glyph atlas file needs a supported GSDF header and version.");

        int pageWidth = reader.ReadInt32();
        int pageHeight = reader.ReadInt32();
        int fontSize = reader.ReadInt32();
        int distanceSpread = reader.ReadInt32();
        if (pageWidth <= 2 || pageHeight <= 2 || fontSize <= 0 || distanceSpread is < 2 or > 32)
            throw new InvalidDataException("Atlas file configuration requires pages larger than two pixels, a positive font size, and a distance spread of 2–32 pixels.");
        var atlas = new GlyphAtlas(pageWidth, pageHeight, fontSize, distanceSpread);
        int pageCount = ReadCount(reader);
        for (int index = 0; index < pageCount; index++)
        {
            AtlasPage page = AtlasPage.ReadFrom(reader);
            atlas.pages.Add(page);
            atlas.dirtyRegions.Add(new AtlasDirtyRegion(index, 0, 0, page.Width, page.Height));
        }

        var fontsByHash = new Dictionary<string, List<Font>>();
        foreach (Font font in fonts)
        {
            DevelopmentCheck.Require(font is not null, "Each atlas font entry needs a font.");
            string hash = Convert.ToHexString(font.Data.GetAssetHash());
            if (!fontsByHash.TryGetValue(hash, out List<Font>? fontsWithHash))
                fontsByHash.Add(hash, fontsWithHash = []);
            fontsWithHash.Add(font);
        }

        byte[] fontHash = new byte[32];
        int fontCount = ReadCount(reader);
        for (int fontIndex = 0; fontIndex < fontCount; fontIndex++)
        {
            reader.BaseStream.ReadExactly(fontHash);
            string hash = Convert.ToHexString(fontHash);
            DevelopmentCheck.Require(fontsByHash.ContainsKey(hash), "Loading needs a font for each baked font asset.");
            List<Font> fontsWithHash = fontsByHash[hash];
            int glyphCount = ReadCount(reader);
            for (int index = 0; index < glyphCount; index++)
            {
                uint glyph = reader.ReadUInt32();
                GlyphLocation location = atlas.ReadLocation(reader);
                foreach (Font font in fontsWithHash) atlas.glyphs.TryAdd((font, glyph), location);
            }
        }
        return atlas;
    }

    private GlyphLocation ReadLocation(BinaryReader reader)
    {
        var region = new AtlasDirtyRegion(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
            reader.ReadInt32(), reader.ReadInt32());
        var offset = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        if (region.PageIndex == -1)
            return new GlyphLocation(region, Vector2.Zero, Vector2.Zero, offset);
        AtlasPage page = pages[region.PageIndex];
        return new GlyphLocation(region,
            new Vector2((float)region.X / page.Width, (float)region.Y / page.Height),
            new Vector2((float)(region.X + region.Width) / page.Width, (float)(region.Y + region.Height) / page.Height), offset);
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0) throw new InvalidDataException("Atlas data counts must be zero or greater.");
        return count;
    }
}
