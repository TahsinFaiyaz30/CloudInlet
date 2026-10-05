using System;
using System.IO;
using CloudBay.Packaging;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--update") return UpdateWorker.Run(args[1]);
            if (args.Length != 4 || args[0] != "--flavor" || args[2] != "--install-directory") return 87;
            var directory = Path.GetFullPath(args[3]);
            if (!Path.IsPathRooted(args[3]) || directory.Length < 8 || directory.IndexOf('"') >= 0) return 87;
            InstallerClientStop.Stop(args[1], directory);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}
