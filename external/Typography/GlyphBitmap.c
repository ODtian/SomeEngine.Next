#include <ft2build.h>
#include FT_FREETYPE_H
#include <freetype/ftmodapi.h>

typedef struct SomeEngine_GlyphBitmap_
{
    int width;
    int height;
    int pitch;
    int left;
    int top;
    const unsigned char* pixels;
} SomeEngine_GlyphBitmap;

/* The face retains the bitmap until the next glyph is loaded. */
FT_EXPORT(FT_Error)
SomeEngine_RenderGlyph(FT_Library library, FT_Face face, FT_UInt glyph,
                       FT_UInt font_size, FT_Int spread,
                       SomeEngine_GlyphBitmap* bitmap)
{
    FT_Error error;
    FT_Int flip = 0;

    error = FT_Property_Set(library, "bsdf", "spread", &spread);
    if (error) return error;
    error = FT_Property_Set(library, "bsdf", "flip_sign", &flip);
    if (error) return error;
    error = FT_Property_Set(library, "bsdf", "flip_y", &flip);
    if (error) return error;
    error = FT_Set_Pixel_Sizes(face, 0, font_size);
    if (error) return error;
    error = FT_Load_Glyph(face, glyph, FT_LOAD_NO_HINTING | FT_LOAD_NO_BITMAP);
    if (error) return error;
    error = FT_Render_Glyph(face->glyph, FT_RENDER_MODE_NORMAL);
    if (error) return error;
    error = FT_Render_Glyph(face->glyph, FT_RENDER_MODE_SDF);
    if (error) return error;

    bitmap->width = (int)face->glyph->bitmap.width;
    bitmap->height = (int)face->glyph->bitmap.rows;
    bitmap->pitch = face->glyph->bitmap.pitch;
    bitmap->left = face->glyph->bitmap_left;
    bitmap->top = face->glyph->bitmap_top;
    bitmap->pixels = face->glyph->bitmap.buffer;
    return 0;
}
