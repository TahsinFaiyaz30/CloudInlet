using System.Security.Cryptography;
using System.Text;

namespace CloudBay.Core.Transfers;

internal static class TransferConflictNames
{
    public static string RenamePath(string path, string operationId)
    {
        path = path.Replace('\\', '/');
        var slash = path.LastIndexOf('/');
        var name = path[(slash + 1)..];
        var suffix = operationId.Length == 64 && operationId.All(Uri.IsHexDigit)
            ? operationId[..12].ToLowerInvariant()
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId)))[..12].ToLowerInvariant();
        return (slash < 0 ? "" : path[..(slash + 1)]) + Sync.PathRules.ConflictFileName(name, " (CloudBay " + suffix + ")");
    }
}
