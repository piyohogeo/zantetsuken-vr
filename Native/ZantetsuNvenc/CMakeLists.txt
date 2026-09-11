cmake_minimum_required(VERSION 3.14)

# Phase 0.11 NVENC native build.
#
# Two things are built here. The SDK version contract compiles one translation
# unit against the official NVIDIA Video Codec SDK header to prove that the
# header, its version, and the local toolchain agree. The plugin builds
# ZantetsuNvenc.dll, which binds to Unity's plugin lifecycle and holds the
# D3D11 device Unity is currently using. Neither links an NVENC library or
# names an NVENC entry point.
#
# The SDK and the Unity installation are external prerequisites: both roots are
# given on the command line, and nothing from either is stored in this
# repository.

set(ZANTETSU_NVENC_SDK_ROOT "" CACHE PATH
    "Root of the externally provided NVIDIA Video Codec SDK 13.0")

if(ZANTETSU_NVENC_SDK_ROOT STREQUAL "")
    message(FATAL_ERROR
        "ZANTETSU_NVENC_SDK_ROOT is required. Pass the root of your own NVIDIA "
        "Video Codec SDK 13.0 installation, for example "
        "-DZANTETSU_NVENC_SDK_ROOT=<sdk root>. This repository ships no SDK.")
endif()

set(ZANTETSU_NVENC_SDK_INTERFACE_DIR "${ZANTETSU_NVENC_SDK_ROOT}/Interface")
set(ZANTETSU_NVENC_SDK_HEADER "${ZANTETSU_NVENC_SDK_INTERFACE_DIR}/nvEncodeAPI.h")

if(NOT EXISTS "${ZANTETSU_NVENC_SDK_HEADER}")
    message(FATAL_ERROR
        "nvEncodeAPI.h was not found at '${ZANTETSU_NVENC_SDK_HEADER}'. "
        "ZANTETSU_NVENC_SDK_ROOT must point at the root of an extracted NVIDIA "
        "Video Codec SDK 13.0, which contains Interface/nvEncodeAPI.h.")
endif()

set(ZANTETSU_UNITY_EDITOR_ROOT "" CACHE PATH
    "Root of the externally provided Unity Editor installation")

if(ZANTETSU_UNITY_EDITOR_ROOT STREQUAL "")
    message(FATAL_ERROR
        "ZANTETSU_UNITY_EDITOR_ROOT is required. Pass the root of your own Unity "
        "installation, for example -DZANTETSU_UNITY_EDITOR_ROOT=<unity root>. "
        "This repository ships no Unity headers.")
endif()

set(ZANTETSU_UNITY_PLUGIN_API_DIR
    "${ZANTETSU_UNITY_EDITOR_ROOT}/Editor/Data/PluginAPI")

foreach(header IN ITEMS
        "IUnityInterface.h"
        "IUnityGraphics.h"
        "IUnityGraphicsD3D11.h")
    if(NOT EXISTS "${ZANTETSU_UNITY_PLUGIN_API_DIR}/${header}")
        message(FATAL_ERROR
            "${header} was not found at '${ZANTETSU_UNITY_PLUGIN_API_DIR}/${header}'. "
            "ZANTETSU_UNITY_EDITOR_ROOT must point at a Unity installation root, "
            "which contains Editor/Data/PluginAPI.")
    endif()
endforeach()

project(ZantetsuNvenc CXX)

if(NOT WIN32)
    message(FATAL_ERROR
        "This target builds on Windows only; the Phase 0.11 capture boundary is "
        "Windows / NVIDIA / D3D11.")
endif()

if(NOT MSVC)
    message(FATAL_ERROR
        "This target builds with MSVC only; '${CMAKE_CXX_COMPILER_ID}' is not "
        "accepted.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR
        "This target builds for x64 only. Configure with -A x64.")
endif()

# One object library: the version contract, compiled and nothing else.
add_library(ZantetsuNvencSdkVersionContract OBJECT
    NvencSdkVersionContract.cpp)

set_target_properties(ZantetsuNvencSdkVersionContract PROPERTIES
    CXX_STANDARD 14
    CXX_STANDARD_REQUIRED ON)

# The SDK include directory belongs to this target alone, and is not exported.
target_include_directories(ZantetsuNvencSdkVersionContract
    PRIVATE "${ZANTETSU_NVENC_SDK_INTERFACE_DIR}")

# The Unity plugin: lifecycle binding and the current D3D11 device, and nothing
# from the NVENC SDK. It links only what D3D11 itself needs - no NVIDIA import
# library, no delay-loaded or run-time-loaded nvEncodeAPI64.dll - and is neither
# copied nor installed anywhere: the build directory is where it stays.
add_library(ZantetsuNvenc SHARED
    D3D11DeviceBinding.cpp
    D3D11DeviceBinding.h
    UnityPluginLifecycle.cpp
    ZantetsuNvencGraphicsObservationV1.h)

set_target_properties(ZantetsuNvenc PROPERTIES
    OUTPUT_NAME "ZantetsuNvenc"
    CXX_STANDARD 14
    CXX_STANDARD_REQUIRED ON)

target_compile_definitions(ZantetsuNvenc
    PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)

# Unity's headers are UTF-8; say so, rather than letting the compiler guess
# from the local code page.
target_compile_options(ZantetsuNvenc PRIVATE /utf-8)

# The Unity plugin headers belong to this target alone, and are not exported.
target_include_directories(ZantetsuNvenc
    PRIVATE "${ZANTETSU_UNITY_PLUGIN_API_DIR}")

target_link_libraries(ZantetsuNvenc PRIVATE d3d11)

# The ownership boundary's own contract test: a small executable that runs
# against real WARP devices. It is built and run from the build directory and
# is never installed, copied, or shipped.
add_executable(ZantetsuNvencD3D11DeviceBindingContractTest
    D3D11DeviceBinding.cpp
    D3D11DeviceBindingContractTest.cpp)

set_target_properties(ZantetsuNvencD3D11DeviceBindingContractTest PROPERTIES
    CXX_STANDARD 14
    CXX_STANDARD_REQUIRED ON)

target_compile_definitions(ZantetsuNvencD3D11DeviceBindingContractTest
    PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)

target_compile_options(ZantetsuNvencD3D11DeviceBindingContractTest PRIVATE /utf-8)

target_link_libraries(ZantetsuNvencD3D11DeviceBindingContractTest PRIVATE d3d11)
