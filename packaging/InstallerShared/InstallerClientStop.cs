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

namespace CloudInlet.Packaging
{
    public static class InstallerClientStop
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

        public static void Stop(string flavor, string installationDirectory, bool removeNotifications = false)
        {
            StopCore(flavor, installationDirectory);
            if (!removeNotifications) return;
            var image = ApplicationImage(installationDirectory);
            if (!File.Exists(image)) return;
            if ((File.GetAttributes(image) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Notification cleanup cannot launch a linked application file.");
            using (var process = Process.Start(new ProcessStartInfo(image, "--unregister-notifications")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(image)
            }))
            {
                if (process == null || !process.WaitForExit(30000))
                    throw new IOException("Windows notification cleanup has not finished. Wait and try uninstalling again.");
                if (process.ExitCode != 0)
                    throw new IOException("Windows notification cleanup could not finish. Restart CloudInlet, then try uninstalling again.");
            }
        }

        private static void StopCore(string flavor, string installationDirectory)
        {
            if (flavor != "Debug" && flavor != "Release") throw new ArgumentException("Unsupported build flavor.");
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("The current Windows user could not be identified.");
            // Keep the existing activation channel so an upgrade can gracefully
            // stop CloudBay as well as CloudInlet and its compatibility apphost.
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
                    var image = process.MainModule?.FileName;
                    if (!IsApplicationImage(image, installationDirectory))
                        throw new IOException("The running CloudInlet instance belongs to a different installation. Quit it from the tray menu before installing.");
                    using (var deadline = new CancellationTokenSource(5000))
                    {
                        var command = Encoding.UTF8.GetBytes("quit");
                        pipe.WriteAsync(command, 0, command.Length, deadline.Token).GetAwaiter().GetResult();
                        pipe.FlushAsync(deadline.Token).GetAwaiter().GetResult();
                    }
                    // Close the pipe before waiting so a legacy server sees EOF.
                    pipe.Dispose();
                    if (!process.WaitForExit(90000))
                        throw new IOException("CloudInlet is still safely finishing a transfer. Wait until syncing is idle, then try again.");
                }
            }
        }

        private static void AssertNoUnresponsiveClient(string directory)
        {
            foreach (var name in new[] { "CloudInlet", "CloudBay" })
            foreach (var candidate in Process.GetProcessesByName(name))
            {
                using (candidate)
                {
                    try
                    {
                        if (IsApplicationImage(candidate.MainModule?.FileName, directory))
                            throw new IOException("CloudInlet is starting or not responding. Quit it from the tray menu before installing.");
                    }
                    catch (InvalidOperationException) { /* The process already exited. */ }
                    catch (Win32Exception) { /* Another user's process is outside this per-user install. */ }
                }
            }
        }

        private static string ApplicationImage(string directory)
        {
            var current = Path.Combine(Path.GetFullPath(directory), "CloudInlet.exe");
            return File.Exists(current) ? current : Path.Combine(Path.GetFullPath(directory), "CloudBay.exe");
        }

        private static bool IsApplicationImage(string? image, string directory)
        {
            if (image == null) return false;
            var root = Path.GetFullPath(directory);
            return string.Equals(image, Path.Combine(root, "CloudInlet.exe"), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(image, Path.Combine(root, "CloudBay.exe"), StringComparison.OrdinalIgnoreCase);
        }
    }
}
