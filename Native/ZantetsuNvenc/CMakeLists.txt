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

set(ZANTETSU_FXC "" CACHE FILEPATH
    "Full path to the Windows SDK fxc.exe that compiles the fixed RGBA to NV12 shaders")

if(ZANTETSU_FXC STREQUAL "")
    message(FATAL_ERROR
        "ZANTETSU_FXC is required. Pass the full path to the fxc.exe of your own "
        "Windows SDK installation, for example -DZANTETSU_FXC=<path to fxc.exe>. "
        "No shader compiler is searched for, read from the registry, guessed, or "
        "shipped by this repository, and none is used at run time.")
endif()

if(IS_DIRECTORY "${ZANTETSU_FXC}" OR NOT EXISTS "${ZANTETSU_FXC}")
    message(FATAL_ERROR
        "ZANTETSU_FXC does not name an existing file: '${ZANTETSU_FXC}'. It must "
        "be the full path to an fxc.exe.")
endif()

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

# The fixed RGBA to NV12 shaders, compiled once at build time into three byte
# code headers. There is exactly one custom command for the three of them and
# one target that owns it, so every consumer depends on the same rule rather
# than defining its own copy of it. Nothing here is compiled at run time: the
# DLL never links or loads d3dcompiler.
#
# The headers and every intermediate go to the build directory, which is
# outside the repository; no byte code, .cso, or compiler binary is ever
# written into the source tree or committed.
set(ZANTETSU_SHADER_SOURCE "${CMAKE_CURRENT_SOURCE_DIR}/NvencRgbaToNv12V1.hlsl")
set(ZANTETSU_SHADER_HEADER_DIR "${CMAKE_CURRENT_BINARY_DIR}/generated")

set(ZANTETSU_SHADER_VS_HEADER "${ZANTETSU_SHADER_HEADER_DIR}/NvencRgbaToNv12V1_Vs.h")
set(ZANTETSU_SHADER_LUMA_HEADER "${ZANTETSU_SHADER_HEADER_DIR}/NvencRgbaToNv12V1_LumaPs.h")
set(ZANTETSU_SHADER_CHROMA_HEADER "${ZANTETSU_SHADER_HEADER_DIR}/NvencRgbaToNv12V1_ChromaPs.h")

# Common to all three: the entry point is named explicitly, optimisation is on,
# every warning is an error, and debug and reflection data are stripped rather
# than carried into the build output.
add_custom_command(
    OUTPUT
        "${ZANTETSU_SHADER_VS_HEADER}"
        "${ZANTETSU_SHADER_LUMA_HEADER}"
        "${ZANTETSU_SHADER_CHROMA_HEADER}"
    COMMAND "${CMAKE_COMMAND}" -E make_directory "${ZANTETSU_SHADER_HEADER_DIR}"
    COMMAND "${ZANTETSU_FXC}" /nologo /T vs_5_0 /E FullscreenTriangleVs
            /O3 /WX /Qstrip_debug /Qstrip_reflect
            /Vn kZantetsuNvencRgbaToNv12VertexShaderV1
            /Fh "${ZANTETSU_SHADER_VS_HEADER}" "${ZANTETSU_SHADER_SOURCE}"
    COMMAND "${ZANTETSU_FXC}" /nologo /T ps_5_0 /E LumaPs
            /O3 /WX /Qstrip_debug /Qstrip_reflect
            /Vn kZantetsuNvencRgbaToNv12LumaPixelShaderV1
            /Fh "${ZANTETSU_SHADER_LUMA_HEADER}" "${ZANTETSU_SHADER_SOURCE}"
    COMMAND "${ZANTETSU_FXC}" /nologo /T ps_5_0 /E ChromaPs
            /O3 /WX /Qstrip_debug /Qstrip_reflect
            /Vn kZantetsuNvencRgbaToNv12ChromaPixelShaderV1
            /Fh "${ZANTETSU_SHADER_CHROMA_HEADER}" "${ZANTETSU_SHADER_SOURCE}"
    DEPENDS "${ZANTETSU_SHADER_SOURCE}"
    COMMENT "Compiling the fixed RGBA to NV12 shaders with fxc"
    VERBATIM)

# The .hlsl is deliberately not listed as a source of this target: a Visual
# Studio generator would then add it as an FXCompile item and compile it a
# second time with its own defaults, looking for a "main" entry point that does
# not exist here. The three explicit invocations above are the only ones.
add_custom_target(ZantetsuNvencShaderHeaders
    DEPENDS
        "${ZANTETSU_SHADER_VS_HEADER}"
        "${ZANTETSU_SHADER_LUMA_HEADER}"
        "${ZANTETSU_SHADER_CHROMA_HEADER}")

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
    NvencDriverApi.cpp
    NvencDriverApi.h
    NvencEncoderSession.cpp
    NvencEncoderSession.h
    UnityPluginLifecycle.cpp
    ZantetsuNvencGraphicsObservationV1.h
    ZantetsuNvencSessionV1.h)

set_target_properties(ZantetsuNvenc PROPERTIES
    OUTPUT_NAME "ZantetsuNvenc"
    CXX_STANDARD 14
    CXX_STANDARD_REQUIRED ON)

target_compile_definitions(ZantetsuNvenc
    PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)

# Unity's headers are UTF-8; say so, rather than letting the compiler guess
# from the local code page.
target_compile_options(ZantetsuNvenc PRIVATE /utf-8)

# The Unity and SDK headers belong to this target alone, and are not exported.
# The SDK header is compiled against; its import library is not linked, and the
# driver module is loaded at run time by name, so nvEncodeAPI64.dll never
# appears in this DLL's import table.
target_include_directories(ZantetsuNvenc
    PRIVATE
        "${ZANTETSU_UNITY_PLUGIN_API_DIR}"
        "${ZANTETSU_NVENC_SDK_INTERFACE_DIR}"
        "${ZANTETSU_SHADER_HEADER_DIR}")

# The byte code headers exist before this target starts compiling.
add_dependencies(ZantetsuNvenc ZantetsuNvencShaderHeaders)

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

# The driver API loading boundary's contract test. Building it needs only the
# SDK header; running it needs an NVIDIA driver, so the build script runs it
# only when asked.
add_executable(ZantetsuNvencDriverApiContractTest
    NvencDriverApi.cpp
    NvencDriverApiContractTest.cpp)

set_target_properties(ZantetsuNvencDriverApiContractTest PROPERTIES
    CXX_STANDARD 14
    CXX_STANDARD_REQUIRED ON)

target_compile_definitions(ZantetsuNvencDriverApiContractTest
    PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)

target_compile_options(ZantetsuNvencDriverApiContractTest PRIVATE /utf-8)

target_include_directories(ZantetsuNvencDriverApiContractTest
    PRIVATE "${ZANTETSU_NVENC_SDK_INTERFACE_DIR}")

# The retained session's capability observation, against a real device and the
# real driver. Like the driver test, it is run only when asked.
add_executable(ZantetsuNvencSessionCapabilityContractTest
    D3D11DeviceBinding.cpp
    NvencDriverApi.cpp
    NvencEncoderSession.cpp
    NvencSessionCapabilityContractTest.cpp)

set_target_properties(ZantetsuNvencSessionCapabilityContractTest PROPERTIES
    CXX_STANDARD 14
    CXX_STANDARD_REQUIRED ON)

target_compile_definitions(ZantetsuNvencSessionCapabilityContractTest
    PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)

target_compile_options(ZantetsuNvencSessionCapabilityContractTest PRIVATE /utf-8)

target_include_directories(ZantetsuNvencSessionCapabilityContractTest
    PRIVATE
        "${ZANTETSU_NVENC_SDK_INTERFACE_DIR}"
        "${ZANTETSU_SHADER_HEADER_DIR}")

# The same rule, depended on rather than duplicated.
add_dependencies(ZantetsuNvencSessionCapabilityContractTest ZantetsuNvencShaderHeaders)

target_link_libraries(ZantetsuNvencSessionCapabilityContractTest PRIVATE d3d11)
