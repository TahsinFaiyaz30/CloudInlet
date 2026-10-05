using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CloudBay.Packaging
{
    public static class InstallerClientStop
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

        public static void Stop(string flavor, string installationDirectory)
        {
            if (flavor != "Debug" && flavor != "Release") throw new ArgumentException("Unsupported build flavor.");
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("The current Windows user could not be identified.");
            var pipeName = "CloudBay.Client." + sid + (flavor == "Debug" ? ".Debug" : "");
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous))
            {
                try { pipe.Connect(3000); }
                catch (TimeoutException)
                {
                    // An existing process that has not created its pipe yet is not
                    // safe to overwrite. Check exact installation images only.
                    AssertNoUnresponsiveClient(installationDirectory);
                    return;
                }
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId)) throw new Win32Exception(Marshal.GetLastWin32Error());
                using (var process = Process.GetProcessById(checked((int)processId)))
                {
                    if (!string.Equals(process.ProcessName, "CloudBay", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The CloudBay activation pipe belongs to another application.");
                    var image = process.MainModule?.FileName;
                    var root = Path.GetFullPath(installationDirectory).TrimEnd('\\') + "\\";
                    if (image == null || !image.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The running CloudBay instance belongs to a different installation. Quit it from the tray menu before installing.");
                    using (var deadline = new CancellationTokenSource(5000))
                    {
                        var command = Encoding.UTF8.GetBytes("quit");
                        pipe.WriteAsync(command, 0, command.Length, deadline.Token).GetAwaiter().GetResult();
                        pipe.FlushAsync(deadline.Token).GetAwaiter().GetResult();
                    }
                    // Close the pipe before waiting so a legacy server sees EOF.
                    pipe.Dispose();
                    if (!process.WaitForExit(90000))
                        throw new IOException("CloudBay is still safely finishing a transfer. Wait until syncing is idle, then try again.");
                }
            }
        }

        private static void AssertNoUnresponsiveClient(string directory)
        {
            var root = Path.GetFullPath(directory).TrimEnd('\\') + "\\";
            foreach (var candidate in Process.GetProcessesByName("CloudBay"))
            {
                using (candidate)
                {
                    try
                    {
                        if (candidate.MainModule?.FileName is string image && image.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("CloudBay is starting or not responding. Quit it from the tray menu before installing.");
                    }
                    catch (InvalidOperationException) { /* The process already exited. */ }
                    catch (Win32Exception) { /* Another user's process is outside this per-user install. */ }
                }
            }
        }
    }
}
