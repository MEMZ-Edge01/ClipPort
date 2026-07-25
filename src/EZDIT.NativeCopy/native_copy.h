#pragma once

#include <Windows.h>

#define EZDIT_NATIVE_COPY_API_VERSION 1u
#define EZDIT_NATIVE_COPY_FLAG_DIRECT_IO 0x00000001u

typedef int(__stdcall* EzditNativeCopyProgressCallback)(
    ULONGLONG bytesWritten,
    void* context);

extern "C"
{
    __declspec(dllexport) DWORD __stdcall EZDIT_NativeCopyGetApiVersion();
    __declspec(dllexport) void* __stdcall EZDIT_NativeCopyCreate();
    __declspec(dllexport) void __stdcall EZDIT_NativeCopyCancel(void* operation);
    __declspec(dllexport) void __stdcall EZDIT_NativeCopyDestroy(void* operation);
    __declspec(dllexport) DWORD __stdcall EZDIT_NativeCopyFileW(
        void* operation,
        const wchar_t* sourcePath,
        const wchar_t* destinationPath,
        DWORD flags,
        EzditNativeCopyProgressCallback progressCallback,
        void* progressContext);
}