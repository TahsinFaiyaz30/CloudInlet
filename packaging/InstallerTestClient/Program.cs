using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;

// Only scripts/test-installers.ps1 packages this fixture. It never creates a
// sync root, reads cloud credentials, or accesses client settings.
internal static class Program
{
    private static int Main(string[] arguments)
    {
        if (arguments.Length == 2 && arguments[0] == "--wait")
        {
            if (!int.TryParse(arguments[1], out var seconds) || seconds < 1 || seconds > 30) return 87;
            Thread.Sleep(seconds * 1000);
        }
        else if (arguments.Length == 2 && arguments[0] == "--serve")
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            // Both apphost names share the original channel during an upgrade.
            using (var pipe = new NamedPipeServerStream("CloudBay.Client." + sid + ".Debug", PipeDirection.In))
            {
                File.WriteAllText(arguments[1], "ready");
                pipe.WaitForConnection();
                using (var input = new StreamReader(pipe, Encoding.UTF8))
                    if (input.ReadToEnd() != "quit") return 87;
                // Give the installer a measurable graceful-exit interval.
                Thread.Sleep(1500);
            }
        }
        return 0;
    }
}
