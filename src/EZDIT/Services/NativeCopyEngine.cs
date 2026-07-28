using System.ComponentModel;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(
    DllImportSearchPath.AssemblyDirectory |
    DllImportSearchPath.SafeDirectories)]

namespace EZDIT.Services;

internal static class NativeCopyEngine
{
    private const string LibraryName = "EZDIT.NativeCopy.dll";
    private const uint ApiVersion = 1;
    private const uint EnableDirectIo = 0x00000001;
    private const uint ErrorOperationAborted = 995;

    private static readonly Lazy<bool> Availability =
        new(CheckAvailability, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static bool IsAvailable => Availability.Value;

    internal static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        Action<CancellationToken> waitWhilePaused,
        Action<int> reportBytesWritten,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        nint operation = NativeCopyCreate();
        if (operation == nint.Zero)
        {
            throw new InvalidOperationException(
                "无法创建 FastCopy 原生复制操作。");
        }

        Exception? callbackError = null;
        NativeProgressCallback progressCallback = (bytesWritten, _) =>
        {
            try
            {
                if (bytesWritten > 0)
                {
                    reportBytesWritten(checked((int)bytesWritten));
                }

                // The native callback runs on a C++ worker thread.
                // Blocking is intentional here — when the user pauses,
                // the native pipeline must stop producing data.
                // The waitWhilePaused delegate is guaranteed to be a
                // trivial poll loop that never requires the UI thread.
                waitWhilePaused(cancellationToken);
                return cancellationToken.IsCancellationRequested ? 1 : 0;
            }
            catch (OperationCanceledException)
            {
                return 1;
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(
                    ref callbackError, ex, null);
                return 1;
            }
        };

        CancellationTokenRegistration registration =
            cancellationToken.Register(
                () => NativeCopyCancel(operation));
        uint error;
        try
        {
            error = await Task.Run(() => NativeCopyFile(
                operation,
                sourcePath,
                destinationPath,
                EnableDirectIo,
                progressCallback,
                nint.Zero));
        }
        finally
        {
            registration.Dispose();
            NativeCopyDestroy(operation);
        }

        if (callbackError is not null)
        {
            throw callbackError;
        }
        if (cancellationToken.IsCancellationRequested ||
            error == ErrorOperationAborted)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (error != 0)
        {
            throw new Win32Exception(
                unchecked((int)error),
                $"FastCopy 原生引擎复制失败：{sourcePath}");
        }
    }

    private static bool CheckAvailability()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            DllImportSearchPath searchPath =
                DllImportSearchPath.AssemblyDirectory |
                DllImportSearchPath.SafeDirectories;
            if (!NativeLibrary.TryLoad(
                    LibraryName,
                    typeof(NativeCopyEngine).Assembly,
                    searchPath,
                    out nint library))
            {
                return false;
            }

            NativeLibrary.Free(library);
            return NativeCopyGetApiVersion() == ApiVersion;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or
                  EntryPointNotFoundException or
                  BadImageFormatException)
        {
            return false;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NativeProgressCallback(
        ulong bytesWritten,
        nint context);

    [DllImport(
        LibraryName,
        EntryPoint = "EZDIT_NativeCopyGetApiVersion",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern uint NativeCopyGetApiVersion();

    [DllImport(
        LibraryName,
        EntryPoint = "EZDIT_NativeCopyCreate",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern nint NativeCopyCreate();

    [DllImport(
        LibraryName,
        EntryPoint = "EZDIT_NativeCopyCancel",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern void NativeCopyCancel(nint operation);

    [DllImport(
        LibraryName,
        EntryPoint = "EZDIT_NativeCopyDestroy",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall)]
    private static extern void NativeCopyDestroy(nint operation);

    [DllImport(
        LibraryName,
        EntryPoint = "EZDIT_NativeCopyFileW",
        ExactSpelling = true,
        CallingConvention = CallingConvention.StdCall,
        CharSet = CharSet.Unicode)]
    private static extern uint NativeCopyFile(
        nint operation,
        string sourcePath,
        string destinationPath,
        uint flags,
        NativeProgressCallback progressCallback,
        nint progressContext);
}