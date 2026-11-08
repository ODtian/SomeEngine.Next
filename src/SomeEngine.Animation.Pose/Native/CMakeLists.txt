cmake_minimum_required(VERSION 3.20)
project(SomeEngineAcl LANGUAGES CXX)

add_library(SomeEngineAcl SHARED Acl.cpp)
target_compile_features(SomeEngineAcl PRIVATE cxx_std_17)
target_include_directories(SomeEngineAcl PRIVATE
    ../../../external/acl/includes
    ../../../external/rtm/includes)
target_compile_definitions(SomeEngineAcl PRIVATE NOMINMAX)
set_property(TARGET SomeEngineAcl PROPERTY MSVC_RUNTIME_LIBRARY MultiThreaded)
if(MSVC)
    target_compile_options(SomeEngineAcl PRIVATE /O2 /fp:precise)
else()
    target_compile_options(SomeEngineAcl PRIVATE -O2 -ffp-contract=off)
endif()
set_target_properties(SomeEngineAcl PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/Release"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/Release"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/Release"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/Release")
