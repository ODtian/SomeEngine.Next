cmake_minimum_required(VERSION 3.24)
project(SomeEngineTypography LANGUAGES C CXX)

include(FetchContent)
set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
set(FT_DISABLE_ZLIB ON CACHE BOOL "" FORCE)
set(FT_DISABLE_BZIP2 ON CACHE BOOL "" FORCE)
set(FT_DISABLE_PNG ON CACHE BOOL "" FORCE)
set(FT_DISABLE_HARFBUZZ ON CACHE BOOL "" FORCE)
set(FT_DISABLE_BROTLI ON CACHE BOOL "" FORCE)
FetchContent_Declare(freetype
    URL https://codeload.github.com/freetype/freetype/tar.gz/refs/tags/VER-2-14-3
    URL_HASH SHA256=dc49de6b01a266eef4876a4dd34d9842c475d3e28ff2eff63bd2fb760ab56261
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(freetype)
target_sources(freetype PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/GlyphBitmap.c")

set(HB_BUILD_SUBSET OFF CACHE BOOL "" FORCE)
set(HB_BUILD_UTILS OFF CACHE BOOL "" FORCE)
set(HB_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(HB_BUILD_RASTER OFF CACHE BOOL "" FORCE)
set(HB_BUILD_VECTOR OFF CACHE BOOL "" FORCE)
set(HB_BUILD_GPU OFF CACHE BOOL "" FORCE)
set(HB_HAVE_FREETYPE OFF CACHE BOOL "" FORCE)
set(HB_HAVE_GLIB OFF CACHE BOOL "" FORCE)
set(HB_HAVE_GOBJECT OFF CACHE BOOL "" FORCE)
set(HB_HAVE_ICU OFF CACHE BOOL "" FORCE)
set(HB_HAVE_CAIRO OFF CACHE BOOL "" FORCE)
FetchContent_Declare(harfbuzz
    URL https://codeload.github.com/harfbuzz/harfbuzz/tar.gz/refs/tags/14.5.1
    URL_HASH SHA256=a058ff4c32dad1e163cd62dce4bca81035f7fbc060bc52fc97f6079c4446ec44
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(harfbuzz)

foreach(library freetype harfbuzz)
    if(MSVC)
        target_compile_options(${library} PRIVATE /utf-8 /MP4)
    endif()
    set_target_properties(${library} PROPERTIES
        VERSION "" SOVERSION "" DEBUG_POSTFIX ""
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/bin/${TYPOGRAPHY_RID}/$<0:>"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/bin/${TYPOGRAPHY_RID}/$<0:>")
endforeach()

configure_file(${freetype_SOURCE_DIR}/docs/FTL.TXT
    ${CMAKE_CURRENT_SOURCE_DIR}/bin/${TYPOGRAPHY_RID}/FreeType-LICENSE.txt COPYONLY)
configure_file(${harfbuzz_SOURCE_DIR}/COPYING
    ${CMAKE_CURRENT_SOURCE_DIR}/bin/${TYPOGRAPHY_RID}/HarfBuzz-LICENSE.txt COPYONLY)
