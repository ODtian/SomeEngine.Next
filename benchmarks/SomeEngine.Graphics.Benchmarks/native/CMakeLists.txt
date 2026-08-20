cmake_minimum_required(VERSION 3.24)
project(SomeEngineGraphicsNativeBenchmarks LANGUAGES CXX)

if(NOT WIN32 OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "The native RHI evidence runner requires 64-bit Windows.")
endif()

set(AGILITY_SDK_DIR "$ENV{USERPROFILE}/.nuget/packages/microsoft.direct3d.d3d12/1.619.5/build/native"
    CACHE PATH "Microsoft.Direct3D.D3D12 1.619.5 native package root")
if(NOT EXISTS "${AGILITY_SDK_DIR}/include/d3d12.h" OR
   NOT EXISTS "${AGILITY_SDK_DIR}/bin/x64/D3D12Core.dll")
  message(FATAL_ERROR "Microsoft.Direct3D.D3D12 1.619.5 is missing. Restore SomeEngine.slnx or set AGILITY_SDK_DIR.")
endif()

add_executable(SomeEngine.Graphics.NativeBenchmarks
  main.cpp
  benchmark_protocol.cpp
  native_d3d12.cpp)
set(SOMEENGINE_REPRESENTATIVE_MODE "Full" CACHE STRING
    "Representative workload mode: Full, Uniform, PerDrawBindings, BindingsOnly, Fixed, State, or Lifecycle")
set_property(CACHE SOMEENGINE_REPRESENTATIVE_MODE PROPERTY STRINGS
  Full Uniform PerDrawBindings BindingsOnly Fixed State Lifecycle)
target_compile_features(SomeEngine.Graphics.NativeBenchmarks PRIVATE cxx_std_20)
target_compile_definitions(SomeEngine.Graphics.NativeBenchmarks PRIVATE
  UNICODE
  _UNICODE
  WIN32_LEAN_AND_MEAN
  NOMINMAX)
if(SOMEENGINE_REPRESENTATIVE_MODE STREQUAL "Uniform")
  target_compile_definitions(SomeEngine.Graphics.NativeBenchmarks PRIVATE
    REPRESENTATIVE_UNIFORM_MATERIAL)
elseif(SOMEENGINE_REPRESENTATIVE_MODE STREQUAL "Fixed")
  target_compile_definitions(SomeEngine.Graphics.NativeBenchmarks PRIVATE
    REPRESENTATIVE_FIXED_ONLY)
elseif(SOMEENGINE_REPRESENTATIVE_MODE STREQUAL "State")
  target_compile_definitions(SomeEngine.Graphics.NativeBenchmarks PRIVATE
    REPRESENTATIVE_STATE_ONLY)
elseif(SOMEENGINE_REPRESENTATIVE_MODE STREQUAL "Lifecycle")
  target_compile_definitions(SomeEngine.Graphics.NativeBenchmarks PRIVATE
    REPRESENTATIVE_LIFECYCLE_ONLY)
elseif(SOMEENGINE_REPRESENTATIVE_MODE STREQUAL "BindingsOnly")
  target_compile_definitions(SomeEngine.Graphics.NativeBenchmarks PRIVATE
    REPRESENTATIVE_BINDINGS_ONLY)
elseif(SOMEENGINE_REPRESENTATIVE_MODE STREQUAL "PerDrawBindings")
  target_compile_definitions(SomeEngine.Graphics.NativeBenchmarks PRIVATE
    REPRESENTATIVE_PER_DRAW_BINDINGS)
elseif(NOT SOMEENGINE_REPRESENTATIVE_MODE STREQUAL "Full")
  message(FATAL_ERROR
    "SOMEENGINE_REPRESENTATIVE_MODE must be Full, Uniform, PerDrawBindings, BindingsOnly, Fixed, State, or Lifecycle")
endif()
target_compile_options(SomeEngine.Graphics.NativeBenchmarks PRIVATE
  /W4
  /WX
  /EHsc
  /permissive-
  /Zc:__cplusplus)
target_include_directories(SomeEngine.Graphics.NativeBenchmarks BEFORE PRIVATE
  "${AGILITY_SDK_DIR}/include")
target_link_libraries(SomeEngine.Graphics.NativeBenchmarks PRIVATE
  d3d12
  dxgi
  dxguid
  advapi32
  bcrypt)

add_custom_command(TARGET SomeEngine.Graphics.NativeBenchmarks POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E make_directory "$<TARGET_FILE_DIR:SomeEngine.Graphics.NativeBenchmarks>/D3D12"
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${AGILITY_SDK_DIR}/bin/x64/D3D12Core.dll"
    "$<TARGET_FILE_DIR:SomeEngine.Graphics.NativeBenchmarks>/D3D12/D3D12Core.dll"
  COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${AGILITY_SDK_DIR}/bin/x64/d3d12SDKLayers.dll"
    "$<TARGET_FILE_DIR:SomeEngine.Graphics.NativeBenchmarks>/D3D12/d3d12SDKLayers.dll")
