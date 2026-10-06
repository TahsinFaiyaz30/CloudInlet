using System.ComponentModel;

namespace CloudInlet.Core.Sync;

/// <summary>Explains an actual Windows failure without changing file attributes or permissions.</summary>
public static class FileSystemError
{
    public static string Describe(Exception error)
    {
        // A contextual outer message (for example a verified B2 copy awaiting native marking)
        // must remain intact. Only decode the Windows error at the point where it occurred.
        if (error.InnerException is not null)
        {
            var detail = Describe(error.InnerException);
            return detail == error.InnerException.Message ? error.Message : error.Message + " " + detail;
        }
        var code = error is Win32Exception native ? native.NativeErrorCode :
            ((uint)error.HResult & 0xffff0000u) == 0x80070000u ? error.HResult & 0xffff : 0;
        return code switch
        {
            5 or 395 => "Windows denied this operation. Check this file's permissions or protection settings. CloudInlet will retry; it will not change permissions or remove your local file. " + Diagnostic(error),
            32 or 33 => "Another app is using this file. Close that app or let it finish saving; CloudInlet will retry automatically. " + Diagnostic(error),
            39 or 112 => "The disk does not have enough free space. Free space on the affected drive and retry. Existing files and verified copies are retained. " + Diagnostic(error),
            2 or 3 => "This file or folder is no longer available. Check its location or reconnect the drive; CloudInlet will check again. " + Diagnostic(error),
            206 => "Windows rejected a file path that is too long. Shorten the affected folder or filename and retry. " + Diagnostic(error),
            362 => "The app that provides this online-only file is not running. Open that cloud app and make the source available before retrying. " + Diagnostic(error),
            396 => "Windows cannot turn this hard-linked file into a Files On-Demand placeholder under this backup's policy. Its local aliases remain intact. " + Diagnostic(error),
            _ => error.Message
        };
    }

    private static string Diagnostic(Exception error) => error is Win32Exception native
        ? $"Windows error {native.NativeErrorCode} (0x{native.NativeErrorCode:X8})."
        : $"Windows error 0x{error.HResult:X8}.";
}
