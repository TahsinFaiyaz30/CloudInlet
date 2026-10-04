using System.Collections.Concurrent;
using System.ComponentModel;
using CloudBay.Core;
using Microsoft.UI.Xaml;

namespace CloudBay.ViewModels;

public interface IActivityActionRow
{
    ActivityRowActions Actions { get; }
}

/// <summary>Metadata-only row actions. Local probes run off the UI thread and never open file content.</summary>
public sealed class ActivityRowActions : INotifyPropertyChanged
{
    private static readonly SemaphoreSlim ProbeSlots = new(2, 2);
    private static readonly ConcurrentDictionary<string, FolderProbe> FolderProbes = new(StringComparer.OrdinalIgnoreCase);
    private sealed record FolderProbe(DateTimeOffset Started, Task<string?> Result);
    public event PropertyChangedEventHandler? PropertyChanged;
    public ActivityActionTarget? Target { get; private set; }
    public string? OpenFolderPath { get; private set; }
    public Visibility OpenFolderVisibility => OpenFolderPath is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CloudVisibility => Target?.CanViewCloud == true ? Visibility.Visible : Visibility.Collapsed;
    public string OpenFolderToolTip => OpenFolderPath is null ? "Open folder" : "Open folder · " + OpenFolderPath;
    public string CloudToolTip => "View retained file versions in Backblaze B2";

    public void SetTarget(ActivityActionTarget? target, bool presentation = false)
    {
        if (Equals(Target, target)) return;
        Target = target;
        OpenFolderPath = presentation ? target?.ContainingFolder : null;
        PropertyChanged?.Invoke(this, new(null));
    }

    public async Task RefreshLocalAvailabilityAsync(bool presentation)
    {
        var target = Target;
        if (target is null || presentation) return;
        var key = target.Location.RootPath + "|" + target.ContainingFolder;
        var now = DateTimeOffset.UtcNow;
        // Virtualized rows and the two windows share probes. A slow external
        // volume cannot block the UI or start hundreds of parallel IO requests.
        if (FolderProbes.Count > 1024)
            foreach (var item in FolderProbes.Where(item => now - item.Value.Started > TimeSpan.FromSeconds(5)).ToArray())
                FolderProbes.TryRemove(item.Key, out _);
        var probe = FolderProbes.AddOrUpdate(key,
            _ => new(now, ProbeAsync(target)),
            (_, prior) => !prior.Result.IsCompleted || now - prior.Started < TimeSpan.FromSeconds(5)
                ? prior : new(now, ProbeAsync(target)));
        var available = await probe.Result;
        if (!Equals(Target, target) || available == OpenFolderPath) return;
        OpenFolderPath = available;
        PropertyChanged?.Invoke(this, new(null));
    }

    private static async Task<string?> ProbeAsync(ActivityActionTarget target)
    {
        await ProbeSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => ActivityLocationResolver.FindExistingFolder(target, Directory.Exists)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
        finally { ProbeSlots.Release(); }
    }
}
