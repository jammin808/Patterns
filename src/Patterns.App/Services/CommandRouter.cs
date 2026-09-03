using System.Text.Json;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Turns remote commands (TCP, web remote, Companion) into show actions on the UI thread —
/// the same typed verbs the operator's own clicks use — and builds the state JSON remotes
/// display. Needs no window: everything goes through <see cref="ShowActions"/>.
/// </summary>
public sealed class CommandRouter
{
    private readonly AppServices _services;

    public CommandRouter(AppServices services)
    {
        _services = services;
    }

    /// <summary>Runs one command on the UI thread; returns the protocol response line.</summary>
    public async Task<string> ExecuteAsync(RemoteCommand cmd, ActionOrigin? origin = null)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => Execute(cmd, origin ?? new ActionOrigin(OriginKind.Tcp)));
        }
        catch (Exception ex)
        {
            Log.Error("Remote command failed.", ex);
            return ControlProtocol.Err(ex.Message);
        }
    }

    private string Execute(RemoteCommand cmd, ActionOrigin origin)
    {
        switch (cmd.Kind)
        {
            case RemoteCommandKind.Ping:
                return ControlProtocol.Ok("PONG");
            case RemoteCommandKind.Status:
                return ControlProtocol.Ok(StateJson());
            case RemoteCommandKind.Unknown:
                return ControlProtocol.Err($"unknown command '{cmd.TextArg}'");
        }

        if (ToAction(cmd) is not { } action) return ControlProtocol.Err($"unknown command '{cmd.Kind}'");
        var result = _services.Actions.Execute(action, origin);
        return result.Ok ? ControlProtocol.Ok() : ControlProtocol.Err(result.Message);
    }

    /// <summary>The wire vocabulary → the show's vocabulary. Pure; unit tested.</summary>
    public static ShowAction? ToAction(RemoteCommand cmd)
    {
        var byNumberOrName = cmd.IntArg > 0 ? cmd.IntArg.ToString() : cmd.TextArg;
        return cmd.Kind switch
        {
            RemoteCommandKind.OutputsOn => new ShowAction(ShowActionKind.OutputsOn),
            RemoteCommandKind.OutputsOff => new ShowAction(ShowActionKind.OutputsOff),
            RemoteCommandKind.BlackoutOn => new ShowAction(ShowActionKind.BlackoutOn),
            RemoteCommandKind.BlackoutOff => new ShowAction(ShowActionKind.BlackoutOff),
            RemoteCommandKind.BlackoutToggle => new ShowAction(ShowActionKind.BlackoutToggle),
            RemoteCommandKind.Identify => new ShowAction(ShowActionKind.Identify),
            RemoteCommandKind.Look => cmd.IntArg > 0
                ? new ShowAction(ShowActionKind.ApplyLookHotkey, cmd.IntArg.ToString())
                : new ShowAction(ShowActionKind.ApplyLook, cmd.TextArg),
            RemoteCommandKind.Next => new ShowAction(ShowActionKind.PresenterNext),
            RemoteCommandKind.Prev => new ShowAction(ShowActionKind.PresenterPrev),
            RemoteCommandKind.ScreenOn => new ShowAction(ShowActionKind.ScreenOn, cmd.IntArg.ToString()),
            RemoteCommandKind.ScreenOff => new ShowAction(ShowActionKind.ScreenOff, cmd.IntArg.ToString()),
            RemoteCommandKind.ScreenToggle => new ShowAction(ShowActionKind.ScreenToggle, cmd.IntArg.ToString()),
            RemoteCommandKind.GroupOn => new ShowAction(ShowActionKind.CanvasOn, cmd.TextArg),
            RemoteCommandKind.GroupOff => new ShowAction(ShowActionKind.CanvasOff, cmd.TextArg),
            RemoteCommandKind.AudioPlay => new ShowAction(ShowActionKind.AudioPlay),
            RemoteCommandKind.AudioStop => new ShowAction(ShowActionKind.AudioStop),
            RemoteCommandKind.ToneOn => new ShowAction(ShowActionKind.ToneOn),
            RemoteCommandKind.ToneOff => new ShowAction(ShowActionKind.ToneOff),
            RemoteCommandKind.Stinger => new ShowAction(ShowActionKind.StingerFire, byNumberOrName),
            RemoteCommandKind.StingerStop => new ShowAction(ShowActionKind.StingerStop),
            RemoteCommandKind.PlaylistSection => new ShowAction(ShowActionKind.PlaylistPart, byNumberOrName),
            RemoteCommandKind.StreamOn => new ShowAction(ShowActionKind.StreamStart),
            RemoteCommandKind.StreamOff => new ShowAction(ShowActionKind.StreamStop),
            _ => null,
        };
    }

    /// <summary>State summary for remotes. UI thread only.</summary>
    public string StateJson()
    {
        var s = _services.State;
        var payload = new
        {
            show = s.Name,
            blackout = s.Blackout,
            live = _services.Outputs.IsLive,
            looks = s.LooksAndCues.Looks.Select(l => new { name = l.Name, slot = l.Hotkey }).ToArray(),
            presenter = PresenterState(s),
            screens = _services.Actions.RemoteScreens(),
            audio = new
            {
                playing = s.AudioPlayer.Playing,
                track = System.IO.Path.GetFileName(s.AudioPlayer.Path),
            },
            tone = s.Tone.Enabled,
            stingers = s.Stingers.Items.Select((i, n) => new { n = n + 1, name = i.DisplayName }).ToArray(),
            stingerPlaying = s.Stingers.PlayingName,
            sections = SectionRows(s),
            playlist = _services.Playlist.Status,
            nextCue = ShowActions.NextScheduledText(s, DateTime.Now),
            stream = new { active = s.Stream.Active, status = _services.Stream.Status },
            health = HealthMonitor.Summary(DateTime.UtcNow),
            machine = MachineRow(),
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Playlist parts for remotes; empty when the playlist has a single unnamed flow.</summary>
    private static object[] SectionRows(ShowState s)
    {
        var options = MediaLocator.FindActivePlaylist(s)?.Playlist ?? s.Pattern.Media.Playlist;
        if (options.Sections.Count <= 1) return Array.Empty<object>();
        var active = Math.Clamp(options.ActiveSection, 0, options.Sections.Count - 1);
        return options.Sections
            .Select((x, i) => (object)new { n = i + 1, name = x.Name, active = i == active })
            .ToArray();
    }

    /// <summary>Machine health for remotes: rounded numbers plus how many advisor lines want attention.</summary>
    private object MachineRow()
    {
        var m = _services.Metrics.Current;
        var advice = _services.Metrics.Suggestions.Count(x => x.Severity >= HealthSeverity.Advice);
        return m is null
            ? new { cpu = -1.0, ram = -1.0, fps = 0.0, battery = false, advice }
            : new
            {
                cpu = Math.Round(m.CpuSystemPct, 0),
                ram = Math.Round(m.RamSystemPct, 0),
                fps = Math.Round(m.OutputWindows > 0 ? m.OutputFps : m.PreviewFps, 0),
                battery = m.OnBattery,
                advice,
            };
    }

    /// <summary>Builds StateJson from any thread.</summary>
    public Task<string> StateJsonAsync() => Dispatcher.UIThread.InvokeAsync(StateJson).GetTask();

    /// <summary>The clicker list as the remotes have always seen the presenter: armed, index, count, step names.</summary>
    private object PresenterState(ShowState s)
    {
        var clicker = CueStacks.Clicker(s);
        var rt = _services.Cues.For(clicker);
        return new
        {
            armed = rt.Armed,
            index = rt.CurrentIndex,
            count = clicker.Cues.Count,
            steps = clicker.Cues.Select(c => c.Name).ToArray(),
        };
    }
}
