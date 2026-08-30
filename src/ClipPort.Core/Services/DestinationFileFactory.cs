using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClipPort.Services;

/// <summary>
/// Opens new partial files without following symbolic links on Linux.
/// Partial paths are always unique, so an existing entry is treated as unsafe.
/// </summary>
internal static class DestinationFileFactory
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const int OWriteOnly = 0x0001;
    private const int OCreate = 0x0040;
    private const int OExclusive = 0x0080;
    private const int OCloseOnExec = 0x80000;
    private const int ONoFollow = 0x20000;
    private const uint OwnerReadWrite = 0x180;

    public static FileStream CreateNew(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        int descriptor = Open(
            path,
            OWriteOnly | OCreate | OExclusive | OCloseOnExec | ONoFollow,
            OwnerReadWrite);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Could not safely create destination file '{path}'.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
        try
        {
            // A descriptor returned by libc open() is synchronous on Unix. Marking
            // it asynchronous makes FileStream reject the handle before the first
            // write; async writes on this stream are safely dispatched by .NET.
            return new FileStream(handle, FileAccess.Write, BufferSize, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mode);
}
