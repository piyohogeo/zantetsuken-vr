cmake_minimum_required(VERSION 3.14)

# Phase 0.11 NVENC SDK build contract.
#
# This project compiles one translation unit against the official NVIDIA Video
# Codec SDK header to prove that the header, its version, and the local
# toolchain agree. It links nothing, produces no DLL or executable, and
# contains no encoder code. The SDK is an external prerequisite: its root is
# given on the command line and nothing about it is stored in this repository.

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
