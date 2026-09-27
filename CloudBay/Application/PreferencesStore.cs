using System.Text.Json;

namespace CloudBay.Application;

public sealed class CloudBayPreferences
{
    public string RootPath { get; set; } = string.Empty;
    public bool IncludeDownloads { get; set; }
    public string Theme { get; set; } = "System";
    public FilterSettings Filters { get; set; } = new();
}

/// <summary>Persists per-user choices without touching Windows shell configuration.</summary>
public sealed class PreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public PreferencesStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudBay", "settings.json");
    }

    public async Task<CloudBayPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_filePath)) return new CloudBayPreferences();
            await using var stream = File.Open(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var preferences = await JsonSerializer.DeserializeAsync<CloudBayPreferences>(stream, JsonOptions, cancellationToken);
            if (preferences is null) throw new InvalidDataException("CloudBay settings are empty.");
            preferences.Filters ??= new FilterSettings();
            preferences.Filters.EnabledNames ??= [];
            preferences.Filters.CustomPatterns ??= [];
            return preferences;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CloudBayPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_filePath) ?? throw new InvalidOperationException("Invalid settings path.");
            Directory.CreateDirectory(directory);
            var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
                if (File.Exists(_filePath))
                {
                    File.Replace(temporary, _filePath, _filePath + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporary, _filePath);
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
