# Typography native libraries

`SomeEngine.Typography` builds FreeType 2.14.3 and HarfBuzz 14.5.1 from the
upstream release archives through CMake `FetchContent`. The archive SHA-256
values are fixed in `CMakeLists.txt`. The first build needs network access,
CMake 3.24 or later, and a native C/C++ toolchain.

On Windows, CMake uses the installed Visual Studio toolchain and selects x64
or ARM64 from the .NET RID. On Linux and macOS it uses the configured native
toolchain. Cross-compilation uses a matching CMake toolchain supplied by the
build environment.

Native libraries are placed in `bin/<rid>` and copied beside the consuming
assembly, including NativeAOT publish output. CMake source downloads and
intermediate files are kept under `obj/<rid>`. Both directories follow the
repository's build-output ignore rules.

The source archives contain the upstream license files:

- [HarfBuzz license](https://github.com/harfbuzz/harfbuzz/blob/14.5.1/COPYING)
- [FreeType license](https://github.com/freetype/freetype/blob/VER-2-14-3/docs/FTL.TXT)

This software is based in part on the work of the FreeType Team.

FreeType loads the asset bytes. HarfBuzz reads OpenType data at the font's
units-per-em scale; pixel scaling happens while the public shape is produced.
The font is immutable after loading and can be shared by independent shape
buffers.
