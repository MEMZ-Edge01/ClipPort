#pragma once

#include <Windows.h>

#define CLIPPORT_NATIVE_COPY_API_VERSION 1u
#define CLIPPORT_NATIVE_COPY_FLAG_DIRECT_IO 0x00000001u

typedef int(__stdcall* ClipPortNativeCopyProgressCallback)(
    ULONGLONG bytesWritten,
    void* context);

extern "C"
{
    __declspec(dllexport) DWORD __stdcall ClipPort_NativeCopyGetApiVersion();
    __declspec(dllexport) void* __stdcall ClipPort_NativeCopyCreate();
    __declspec(dllexport) void __stdcall ClipPort_NativeCopyCancel(void* operation);
    __declspec(dllexport) void __stdcall ClipPort_NativeCopyDestroy(void* operation);
    __declspec(dllexport) DWORD __stdcall ClipPort_NativeCopyFileW(
        void* operation,
        const wchar_t* sourcePath,
        const wchar_t* destinationPath,
        DWORD flags,
        ClipPortNativeCopyProgressCallback progressCallback,
        void* progressContext);
}