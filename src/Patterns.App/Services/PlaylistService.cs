using Avalonia.Threading;
using Patterns.Core.Media;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Drives the media playlist: scans folders, keeps the sequencer's order current, advances
/// items on their timers / video ends / daily schedules, and publishes the current item on
/// the snapshot bus so every sink (outputs, span slices, NDI, tiles) shows the same thing.
/// </summary>
public sealed class PlaylistService : IDisposable
{
    private const int MaxFolderFiles = 1000;

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly PlaylistSequencer _sequencer = new();
    private List<string> _folderFiles = new();
    private string _folderKey = "";
    private string _orderKey = "";
    private DateTime _lastScanUtc = DateTime.MinValue;

    public PlaylistService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        try
        {
            var options = MediaLocator.FindActivePlaylist(_services.State)?.Playlist;
            if (options is null)
            {
                if (_services.Bus.PlaylistNow is not null)
                {
                    _services.Bus.PlaylistNow = null;
                    _services.PublishRuntime();
                }
                return;
            }

            var utcNow = DateTime.UtcNow;
            var localNow = DateTime.Now;

            // A section with a start time takes over daily at its minute.
            if (_sequencer.SectionDue(options, localNow) is { } dueSection && options.ActiveSection != dueSection)
            {
                options.ActiveSection = dueSection;
                Log.Info($"Playlist section '{options.Sections[dueSection].Name}' took over ({options.Sections[dueSection].StartTime}).");
            }

            var section = PlaylistSequencer.ActiveSectionOf(options);

            // Rescan folders when the active set changes or every 30 s (files may be dropped in live).
            var folderKey = $"{options.ActiveSection}|{string.Join('|', section.Folders)}";
            if (folderKey != _folderKey || (utcNow - _lastScanUtc).TotalSeconds > 30)
            {
                _folderKey = folderKey;
                _lastScanUtc = utcNow;
                _folderFiles = ScanFolders(section.Folders);
            }

            var videoAvailable = !options.IncludeVideos || _services.Video.EnsureAvailable();
            var orderKey = $"{folderKey}#{string.Join('|', section.Items.Select(i => i.Path))}#{options.Shuffle}#{options.ShuffleSeed}#{options.IncludeImages}#{options.IncludeVideos}#{videoAvailable}#{_folderFiles.Count}";
            if (orderKey != _orderKey)
            {
                _orderKey = orderKey;
                _sequencer.SetOrder(PlaylistSequencer.BuildOrder(options, section.Items, _folderFiles, videoAvailable), utcNow);
            }

            var currentItem = _sequencer.Current;
            var currentIsVideo = currentItem?.IsVideo == true;
            var video = currentIsVideo ? InputBus.For(InputKeys.Video(currentItem!.Path)) : null;
            var videoEnded = currentIsVideo && video is { IsEnded: true };
            var videoLength = currentIsVideo && video is not null ? video.DurationSeconds : 0;

            _sequencer.Tick(options, localNow, utcNow, videoEnded, videoLength);

            var current = _sequencer.Current;
            var now = current is null
                ? null
                : new PlaylistNow(current.Path, current.IsVideo, _sequencer.CurrentIndex, _sequencer.Count,
                    _sequencer.ItemStartedUtc, _sequencer.ItemDurationSeconds);

            var previous = _services.Bus.PlaylistNow;
            if (!Equals(previous?.Path, now?.Path) || previous?.StartedUtc != now?.StartedUtc)
            {
                _services.Bus.PlaylistNow = now;
                _services.PublishRuntime();
                // Runtime publishes skip side effects — mount the new item's decoder now,
                // not on the next model edit.
                _services.ReconcileInputs();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Playlist tick failed.", ex);
        }
    }

    private static List<string> ScanFolders(IEnumerable<string> folders)
    {
        var files = new List<string>();
        foreach (var folder in folders)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (PlaylistSequencer.IsMediaPath(file))
                    {
                        files.Add(file);
                        if (files.Count >= MaxFolderFiles) return files;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Playlist folder '{folder}' could not be scanned.", ex);
            }
        }
        return files;
    }

    /// <summary>Status line for the Media panel.</summary>
    public string Status
    {
        get
        {
            var now = _services.Bus.PlaylistNow;
            if (now is null) return "Playlist idle.";
            var name = Path.GetFileName(now.Path);
            var pos = now.Index >= 0 ? $"{now.Index + 1}/{now.Count}" : "scheduled";
            var held = (DateTime.UtcNow - now.StartedUtc).TotalSeconds;
            var dur = now.DurationSeconds > 0 ? $" · {Math.Max(0, now.DurationSeconds - held):0}s left" : "";
            var options = MediaLocator.FindActivePlaylist(_services.AirState)?.Playlist;
            var part = options is not null && options.Sections.Count > 1
                ? $"[{PlaylistSequencer.ActiveSectionOf(options).Name}] "
                : "";
            return $"{part}Playing {pos}: {name}{dur}";
        }
    }

    public void Dispose() => _timer.Stop();
}
