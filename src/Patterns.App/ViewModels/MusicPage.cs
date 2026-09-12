using System.Collections.ObjectModel;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// Break music (Spotify) on the desk: the account and its devices, the list of entries, the
/// browse and search that fill it, the transport, and the Music picker every look carries. A
/// page of its own, read by the Audio page's block, the Show panel's strip and the Looks page;
/// the desk is asked only for its status line. The rules — what is a link, what may be removed,
/// what a look may choose — are the show's (<see cref="SpotifyLibrary"/>), not the page's.
/// </summary>
public sealed class MusicPage : Observable
{
    private readonly MainViewModel _desk;
    private readonly AppServices _services;
    private string _status = "Off.";
    private string _accountText = "Not connected.";
    private string _nowPlaying = "";
    private string _linkDraft = "";
    private string _searchDraft = "";
    private string _browseStatus = "";
    private SpotifyDeviceChoice? _selectedDevice;
    private SpotifyPlaylistRef? _selectedPlaylist;
    private SpotifyTrackRef? _selectedTrack;
    private SpotifySearchHit? _selectedSearchHit;
    private IReadOnlyList<SpotifyDevice>? _devicesSeen;
    private IReadOnlyList<SpotifyPlaylistRef>? _playlistsSeen;
    private IReadOnlyList<SpotifyTrackRef>? _tracksSeen;
    private IReadOnlyList<SpotifySearchHit>? _searchSeen;

    public MusicPage(MainViewModel desk, AppServices services)
    {
        _desk = desk;
        _services = services;
        // The desk's buttons go through the same verbs a cue and the remote use.
        ConnectCommand = new RelayCommand(() => _ = ConnectAsync());
        DisconnectCommand = new RelayCommand(() =>
        {
            _services.Spotify.Disconnect();
            RefreshDevices();
            RefreshPlaylists();
        });
        RefreshDevicesCommand = new RelayCommand(() => _ = RefreshDevicesAsync());
        RefreshPlaylistsCommand = new RelayCommand(() => _ = RefreshPlaylistsAsync());
        AddLinkCommand = new RelayCommand(() =>
        {
            if (!SpotifyLibrary.TryAddLink(State, LinkDraft, out _, out var problem))
            {
                _desk.StatusMessage = problem;
                return;
            }
            LinkDraft = "";
        });
        AddPlaylistCommand = new RelayCommand(() =>
        {
            if (SelectedPlaylist is not { } list)
            {
                _desk.StatusMessage = "Choose one of your playlists first — press Refresh my playlists after CONNECT.";
                return;
            }
            SpotifyLibrary.AddEntry(State, list.Uri, list.Name, out _);
        });
        RemoveCommand = new RelayCommand<SpotifyItemConfig>(item =>
        {
            if (item is null) return;
            if (!SpotifyLibrary.TryRemove(State, item, out var problem)) _desk.StatusMessage = problem;
        });
        PlayCommand = new RelayCommand<SpotifyItemConfig>(item =>
        {
            if (item is null) return;
            _services.Actions.Execute(ShowActionKind.SpotifyPlay, ActionOrigin.Desk, item.Id);
        });
        ResumeCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.SpotifyPlay, ActionOrigin.Desk));
        PauseCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.SpotifyPause, ActionOrigin.Desk));
        SkipCommand = new RelayCommand(() => _services.Actions.Execute(ShowActionKind.SpotifyNext, ActionOrigin.Desk));
        BrowsePlaylistCommand = new RelayCommand(() =>
        {
            if (SelectedPlaylist is not { } list)
            {
                _desk.StatusMessage = "Choose one of your playlists first — press Refresh my playlists after CONNECT.";
                return;
            }
            _ = BrowseAsync(list.Uri);
        });
        BrowseLinkCommand = new RelayCommand(() =>
        {
            if (!SpotifyUri.TryParse(LinkDraft, out var r))
            {
                _desk.StatusMessage = "Paste a Spotify playlist, album or artist link to browse its songs.";
                return;
            }
            _ = BrowseAsync(r.Uri);
        });
        AddTrackCommand = new RelayCommand(() =>
        {
            if (SelectedTrack is not { } track)
            {
                _desk.StatusMessage = "Pick a song in the list first.";
                return;
            }
            AddEntry(track.Uri, track.Line);
        });
        SearchCommand = new RelayCommand(() => _ = SearchAsync());
        AddSearchHitCommand = new RelayCommand(() =>
        {
            if (SelectedSearchHit is not { } hit)
            {
                _desk.StatusMessage = "Pick a result first.";
                return;
            }
            AddEntry(hit.Uri, hit.EntryName);
        });
        RefreshDevices();
        RefreshLookChoices();
    }

    private ShowState State => _services.State;

    // ---- the account and what is playing (the poll keeps these) ----------------------------

    public string Status { get => _status; private set => Set(ref _status, value); }
    public string AccountText { get => _accountText; private set => Set(ref _accountText, value); }
    public string NowPlaying { get => _nowPlaying; private set => Set(ref _nowPlaying, value); }

    /// <summary>The operator's own Client ID; the setter writes the sidecar beside the settings, never the show.</summary>
    public string ClientId
    {
        get => _services.Spotify.ClientId;
        set
        {
            if (_services.Spotify.ClientId == (value ?? "").Trim()) return;
            _services.Spotify.ClientId = value ?? "";
            Raise(nameof(ClientId));
        }
    }

    /// <summary>The three redirect URIs to register on the Spotify app — one per loopback port CONNECT may use.</summary>
    public string RedirectUris => string.Join("\n", LoopbackCallback.Ports.Select(SpotifyEndpoints.RedirectUri));

    public ObservableCollection<SpotifyDeviceChoice> Devices { get; } = new();

    public SpotifyDeviceChoice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value is null) return; // a rebuilt picker clears itself first; the show's choice stands
            if (!Set(ref _selectedDevice, value)) return;
            State.Spotify.DeviceName = value.Name;
        }
    }

    public ObservableCollection<SpotifyPlaylistRef> Playlists { get; } = new();
    public SpotifyPlaylistRef? SelectedPlaylist { get => _selectedPlaylist; set => Set(ref _selectedPlaylist, value); }

    /// <summary>A pasted link: ADD makes an entry of it, BROWSE SONGS lists what it holds.</summary>
    public string LinkDraft { get => _linkDraft; set => Set(ref _linkDraft, value ?? ""); }

    // ---- browse & search (desk only; a free account can do this much) ------------------------

    public ObservableCollection<SpotifyTrackRef> Tracks { get; } = new();
    public SpotifyTrackRef? SelectedTrack { get => _selectedTrack; set => Set(ref _selectedTrack, value); }
    public ObservableCollection<SpotifySearchHit> SearchHits { get; } = new();
    public SpotifySearchHit? SelectedSearchHit { get => _selectedSearchHit; set => Set(ref _selectedSearchHit, value); }
    public string SearchDraft { get => _searchDraft; set => Set(ref _searchDraft, value ?? ""); }
    public string BrowseStatus { get => _browseStatus; private set => Set(ref _browseStatus, value); }

    /// <summary>The Music picker on every look: leave it, pause it, or one of the break-music entries.</summary>
    public ObservableCollection<LookMusicChoice> LookChoices { get; } = new();

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand RefreshPlaylistsCommand { get; }
    public RelayCommand AddLinkCommand { get; }
    public RelayCommand AddPlaylistCommand { get; }
    public RelayCommand<SpotifyItemConfig> RemoveCommand { get; }
    public RelayCommand<SpotifyItemConfig> PlayCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand BrowsePlaylistCommand { get; }
    public RelayCommand BrowseLinkCommand { get; }
    public RelayCommand AddTrackCommand { get; }
    public RelayCommand SearchCommand { get; }
    public RelayCommand AddSearchHitCommand { get; }

    /// <summary>The desk's second: the service's lines and lists into the page, in place, only where they moved.</summary>
    public void Poll()
    {
        var spotify = _services.Spotify;
        Status = spotify.Status;
        AccountText = spotify.AccountText;
        NowPlaying = spotify.NowPlaying;
        if (!ReferenceEquals(_devicesSeen, spotify.Devices)) RefreshDevices();       // CONNECT filled them in
        if (!ReferenceEquals(_playlistsSeen, spotify.Playlists)) RefreshPlaylists();
        if (!ReferenceEquals(_tracksSeen, spotify.Tracks) || !ReferenceEquals(_searchSeen, spotify.SearchHits) || BrowseStatus != spotify.BrowseStatus)
        {
            RefreshBrowse();
        }
        RefreshLookChoices(); // a renamed or added entry, a loaded show
    }

    /// <summary>A show read from a file: its device choice and its entries into the pickers.</summary>
    public void OnShowLoaded()
    {
        RefreshDevices();
        RefreshLookChoices();
    }

    /// <summary>
    /// Kept in step with the break-music list by adding and relabelling in place. A choice is
    /// removed only when no look names it — a bound picker that loses its selected item writes
    /// the loss back into the look — so an entry a look still names stays offered, marked, until
    /// the look is pointed elsewhere.
    /// </summary>
    public void RefreshLookChoices()
    {
        var wanted = SpotifyLibrary.LookChoices(State);
        for (var i = LookChoices.Count - 1; i >= 0; i--)
        {
            if (wanted.All(w => w.Id != LookChoices[i].Id)) LookChoices.RemoveAt(i);
        }
        foreach (var (id, label) in wanted)
        {
            var existing = LookChoices.FirstOrDefault(c => c.Id == id);
            if (existing is null) LookChoices.Add(new LookMusicChoice(id, label));
            else if (existing.Label != label) existing.Label = label;
        }
    }

    /// <summary>
    /// The "Play on" picker: whichever device is active, then Spotify's devices, then the show's
    /// choice when it is not on Spotify right now (so a loaded show never loses its device).
    /// Rebuilt in place only when the entries really moved — clearing a bound picker drops its
    /// selection, and the selection setter ignores that null.
    /// </summary>
    public void RefreshDevices()
    {
        var chosen = State.Spotify.DeviceName;
        var devices = _services.Spotify.Devices;
        _devicesSeen = devices;
        var wanted = new List<SpotifyDeviceChoice> { new("", "Whichever device is active") };
        foreach (var d in devices)
        {
            wanted.Add(new SpotifyDeviceChoice(d.Name, d.IsActive ? $"{d.Name} (active)" : d.Name));
        }
        if (chosen.Length > 0 && !wanted.Any(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase)))
        {
            wanted.Add(new SpotifyDeviceChoice(chosen, $"{chosen} (not on Spotify right now)"));
        }
        if (Devices.Count != wanted.Count || !Devices.SequenceEqual(wanted))
        {
            Devices.Clear();
            foreach (var c in wanted) Devices.Add(c);
        }
        _selectedDevice = Devices.FirstOrDefault(c => string.Equals(c.Name, chosen, StringComparison.OrdinalIgnoreCase)) ?? Devices[0];
        Raise(nameof(SelectedDevice));
    }

    public void RefreshPlaylists()
    {
        var lists = _services.Spotify.Playlists;
        _playlistsSeen = lists;
        if (Playlists.Count == lists.Count && Playlists.SequenceEqual(lists)) return;
        Playlists.Clear();
        foreach (var l in lists) Playlists.Add(l);
        SelectedPlaylist = Playlists.FirstOrDefault();
    }

    private void RefreshBrowse()
    {
        var tracks = _services.Spotify.Tracks;
        if (!ReferenceEquals(_tracksSeen, tracks))
        {
            _tracksSeen = tracks;
            Tracks.Clear();
            foreach (var t in tracks) Tracks.Add(t);
            SelectedTrack = null;
        }
        var hits = _services.Spotify.SearchHits;
        if (!ReferenceEquals(_searchSeen, hits))
        {
            _searchSeen = hits;
            SearchHits.Clear();
            foreach (var h in hits) SearchHits.Add(h);
            SelectedSearchHit = null;
        }
        BrowseStatus = _services.Spotify.BrowseStatus;
    }

    private async Task BrowseAsync(string uri)
    {
        try
        {
            await _services.Spotify.LoadTracksAsync(uri);
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify browse issue.", ex);
        }
        RefreshBrowse();
    }

    private async Task SearchAsync()
    {
        try
        {
            await _services.Spotify.SearchAsync(SearchDraft);
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify search issue.", ex);
        }
        RefreshBrowse();
    }

    /// <summary>A browsed song or a search hit becomes a one-press entry; the same link twice stays one entry.</summary>
    private void AddEntry(string uri, string name)
    {
        var entry = SpotifyLibrary.AddEntry(State, uri, name, out var added);
        if (entry is null) return;
        _desk.StatusMessage = added ? $"Added '{name}' to break music." : $"'{entry.DisplayName}' is already in break music.";
        if (added) RefreshLookChoices();
    }

    private async Task ConnectAsync()
    {
        try
        {
            await _services.Spotify.ConnectAsync();
            RefreshDevices();
            await _services.Spotify.RefreshPlaylistsAsync();
            RefreshPlaylists();
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify connect issue.", ex);
        }
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            await _services.Spotify.RefreshDevicesAsync();
            RefreshDevices();
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify device refresh issue.", ex);
        }
    }

    private async Task RefreshPlaylistsAsync()
    {
        try
        {
            await _services.Spotify.RefreshPlaylistsAsync();
            RefreshPlaylists();
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify playlist refresh issue.", ex);
        }
    }
}
