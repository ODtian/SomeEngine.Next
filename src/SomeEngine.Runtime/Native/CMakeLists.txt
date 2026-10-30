cmake_minimum_required(VERSION 3.20)
project(Cimgui LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 11)

# ImGui.NET.SourceBuild 1.91.6 contains the sources used by ImGui.NET 1.91.6.1:
# cimgui d9982af9299fa7ccce1f0c62a46d73728891843f, imgui 947aa9c9722bd6ff740dd757da609ff41f4d3ba3.
add_library(cimgui SHARED
    ${CIMGUI_SOURCE_DIRECTORY}/cimgui.cpp
    ${CIMGUI_SOURCE_DIRECTORY}/imgui/imgui.cpp
    ${CIMGUI_SOURCE_DIRECTORY}/imgui/imgui_draw.cpp
    ${CIMGUI_SOURCE_DIRECTORY}/imgui/imgui_demo.cpp
    ${CIMGUI_SOURCE_DIRECTORY}/imgui/imgui_widgets.cpp
    ${CIMGUI_SOURCE_DIRECTORY}/imgui/imgui_tables.cpp)
target_include_directories(cimgui PRIVATE
    ${CIMGUI_SOURCE_DIRECTORY}
    ${CIMGUI_SOURCE_DIRECTORY}/imgui)
target_compile_definitions(cimgui PRIVATE
    IMGUI_DISABLE_OBSOLETE_FUNCTIONS=1
    "IMGUI_IMPL_API=extern \"C\"")
# The upstream cimconfig.h keeps assertions enabled in Release builds.
target_compile_options(cimgui PRIVATE -UNDEBUG)
