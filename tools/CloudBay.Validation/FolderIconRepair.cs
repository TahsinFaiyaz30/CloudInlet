using System.Text.Json;
using CloudBay.Application;
using CloudBay.Core.Sync;
using CloudBay.Windows;

/// <summary>Repairs only existing, owned Windows folder appearance. No provider restart or B2 access.</summary>
internal static class FolderIconRepair
{
    public static async Task<int> RunAsync()
    {
        var results = new List<object>();
        var failed = false;
        try
        {
            var settings = new ClientStorage().LoadSettings();
            PathRules.ValidateSettings(settings);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            foreach (var folder in settings.Backups)
            {
                if (!KnownFolderBackup.FolderIds.ContainsKey(folder.Name) ||
                    !PathRules.FullPath(settings.RootPath, folder.Name).Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("A backup appearance record does not match its owned sync root.");
                var owned = KnownFolderBackup.GetPath(folder.Name).Equals(folder.DestinationPath, StringComparison.OrdinalIgnoreCase);
                if (!owned) { results.Add(new { folder = folder.Name, skipped = true, reason = "Windows folder ownership changed" }); continue; }
                try
                {
                    await FolderAppearance.EnsureKnownFolderAsync(folder, timeout.Token);
                    var configured = FolderAppearance.GetIconResource(folder.DestinationPath) is not null;
                    results.Add(new { folder = folder.Name, skipped = false, iconConfigured = configured });
                    failed |= !configured;
                }
                catch (Exception error)
                {
                    failed = true;
                    results.Add(new { folder = folder.Name, skipped = false, errorType = error.GetType().Name });
                }
            }
        }
        catch (Exception error)
        {
            failed = true;
            results.Add(new { errorType = error.GetType().Name });
        }
        var report = JsonSerializer.Serialize(new { timeUtc = DateTimeOffset.UtcNow, passed = !failed, folders = results },
            new JsonSerializerOptions { WriteIndented = true });
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/validation"));
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "owned-folder-icon-repair.json"), report);
        Console.WriteLine(report);
        return failed ? 1 : 0;
    }
}
