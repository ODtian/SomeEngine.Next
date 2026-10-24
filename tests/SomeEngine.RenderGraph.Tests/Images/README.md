# Glyph coverage reference

`Latin-A-48.pgm` contains a 64 × 64 grayscale coverage image of glyph `A` from
`tests/SomeEngine.Typography.Tests/Fonts/Latin.ttf`. The font's license and source
identity are recorded beside that fixture. Its baseline is at (16, 48), with
48 pixels per em and a top-left image origin.

The reference was generated with fontTools 4.53.0 and Pillow 10.3.0 from the
TrueType outline, independently of the engine's SDF atlas and shader. Each
quadratic segment is divided into 64 segments. Contours are filled into a
1024 × 1024 grayscale image, then reduced with a 16 × 16 box filter per pixel.
The outer contour uses coverage 255 and the inner contour uses coverage zero.

The GPU test compares all pixels, allowing a mean absolute coverage error of
3/255 and a maximum error of 80/255. The mean bound checks the full silhouette;
the per-pixel bound accommodates the SDF approximation at sharp corners.
