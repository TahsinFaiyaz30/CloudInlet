using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace CloudInlet.Packaging
{
    internal static class Program
    {
        // Published update workers verify and reopen CloudBay.exe. Launch the
        // branded executable so WinUI/MRT, Windows and the process name all use
        // CloudInlet. Never run renamed apphosts against an old CloudBay PRI.
        [STAThread]
        private static int Main(string[] arguments)
        {
            try
            {
                var directory = AppDomain.CurrentDomain.BaseDirectory;
                var executable = Path.Combine(directory, "CloudInlet.exe");
                if (!File.Exists(executable)) return 2;
                var expected = typeof(Program).Assembly.GetName().Version.ToString(3);
                if (FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Split('+')[0] != expected) return 3;
                var start = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(Quote)))
                { UseShellExecute = false, WorkingDirectory = directory, CreateNoWindow = true };
                using (var child = Process.Start(start)) return child == null ? 4 : 0;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.ComponentModel.Win32Exception)
            { return 5; }
        }

        // Windows CommandLineToArgvW quoting: preserve spaces, quotes and trailing
        // backslashes in file/notification arguments without a command interpreter.
        internal static string Quote(string argument)
        {
            var output = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') output.Append('\\', slashes * 2 + 1);
                else output.Append('\\', slashes);
                slashes = 0;
                output.Append(character);
            }
            output.Append('\\', slashes * 2).Append('"');
            return output.ToString();
        }
    }
}
