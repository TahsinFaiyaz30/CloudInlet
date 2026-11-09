using CloudInlet.Core;
using CloudInlet.Core.Migration;
using CloudInlet.Core.Sync;

namespace CloudInlet.Application;

/// <summary>Optional discovery must not replace the controller's existing settings-recovery UI.
/// Call only after protected routing is resolved and no migration consent is pending.</summary>
internal static class ClientMigrationInventory
{
    internal sealed record Result(AppSettings? Settings, MigrationPlan? Plan, string? Error);

    internal static Result TryCreate(string dataDirectory, Func<AppSettings, MigrationPlan> discover)
    {
        try
        {
            var settings = new ClientStorage(dataDirectory).LoadSettings();
            PathRules.ValidateSettings(settings);
            return new(settings, discover(settings), null);
        }
        catch (Exception error)
        {
            // Discovery is read-only. No saved consent, migration receipt or routing
            // failure may enter this fallback; those must still stop normal startup.
            return new(null, null, "Migration review is unavailable: " + error.Message +
                " No locations were changed. You can review migration again from Settings > Updates.");
        }
    }
}
