# Typography test fonts

The fonts are subsets made with fontTools 4.53.0. Their family names are changed
to `SomeEngine Typography Test Cjk` and `SomeEngine Typography Test Latin`.
The SIL Open Font License for each source is included beside the fonts.

| Fixture | Source |
| --- | --- |
| `Cjk.otf` | [Noto Sans CJK SC Regular, commit 165c01b](https://github.com/googlefonts/noto-cjk/blob/165c01b46ea533872e002e0785ff17e44f6d97d8/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf) |
| `Latin.ttf` | [Noto Sans Regular, commit c971829](https://github.com/notofonts/noto-fonts/blob/c971829a87e7920f960e7277c3dafd9bedd3c601/hinted/ttf/NotoSans/NotoSans-Regular.ttf) |

Source SHA-256 values:

- CJK: `2c76254f6fc379fddfce0a7e84fb5385bb135d3e399294f6eeb6680d0365b74b`
- Latin: `b85c38ecea8a7cfb39c24e395a4007474fa5a4fc864f6ee33309eb4948d232d5`

The subset retains ASCII U+0020–007E, U+00E9, U+0301, `中文你好世界爱`,
and U+2000B. The subset options retain all layout features and name records.
The committed fixture bytes are the reference for glyph numbers.

## HarfBuzz reference values

Generated on 2026-10-03 with the upstream HarfBuzz 14.5.1 Windows release:

```text
hb-shape <fixture> <text> --direction=ltr --no-glyph-names --output-format=json
```

The font has 1000 units per em. Values below are font units; a 20 px font
scales them by 0.02. The shaping tests read the library's public glyph and line
results and compare them to these independently generated values.

| Font | Text | Glyph numbers | UTF-16 indices | X advances | Total at 20 px |
| --- | --- | --- | --- | --- | --- |
| CJK | `Hello世界` | 41, 70, 77, 77, 80, 207, 216 | 0, 1, 2, 3, 4, 5, 6 | 728, 554, 284, 284, 606, 1000, 1000 | 89.12 |
| Latin | `office` | 80, 115, 68, 70 | 0, 1, 4, 5 | 605, 946, 480, 564 | 51.90 |
| Latin | `AV` | 34, 55 | 0, 1 | 599, 600 | 23.98 |
| CJK | `中文你好` | 208, 214, 209, 211 | 0, 1, 2, 3 | 1000, 1000, 1000, 1000 | 80.00 |
| Latin | `e` + U+0301 | 102 | 0 | 564 | 11.28 |
| CJK | U+2000B | 327 | 0 | 1000 | 20.00 |

`Hello world`, with `--script=latn --language=und`, produces glyph numbers
41, 70, 77, 77, 80, 1, 88, 80, 83, 77, 69 and advances
728, 554, 284, 284, 606, 224, 798, 606, 388, 284, 620.
At 20 px, the two word ranges `Hello ` and `world` have widths 53.60 and 53.92.

Line-height references come from the fixture font metrics: CJK ascent 1160,
descent -288, line gap 0; Latin ascent 1069, descent -293, line gap 0.
At 20 px a line containing both fonts has ascent 23.20 and descent 5.86,
giving height 29.06.

The Latin font's single-glyph advance references are 639 units for `A` and
600 units for `V` (12.78 and 12.00 px at 20 px), as recorded in its `hmtx`
table. The wrapped-line tests use these values to verify that kerning applies
within each line.
