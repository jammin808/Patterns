using System.Text.Json;
using Avalonia.Threading;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Executes remote commands (TCP, web remote, Companion) on the UI thread against the same
/// code paths the operator's own clicks use, and builds the state JSON remotes display.
/// </summary>
public sealed class CommandRouter
{
    private readonly AppServices _services;

    public CommandRouter(AppServices services)
    {
        _services = services;
    }

    private MainViewModel? Vm => _services.MainWindow?.DataContext as MainViewModel;

    /// <summary>Runs one command on the UI thread; returns the protocol response line.</summary>
    public async Task<string> ExecuteAsync(RemoteCommand cmd)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => Execute(cmd));
        }
        catch (Exception ex)
        {
            Log.Error("Remote command failed.", ex);
            return ControlProtocol.Err(ex.Message);
        }
    }

    private string Execute(RemoteCommand cmd)
    {
        var state = _services.State;
        switch (cmd.Kind)
        {
            case RemoteCommandKind.Ping:
                return ControlProtocol.Ok("PONG");
            case RemoteCommandKind.Go:
                Vm?.GoCommand.Execute(null);
                return ControlProtocol.Ok();
            case RemoteCommandKind.Stop:
                Vm?.StopCommand.Execute(null);
                return ControlProtocol.Ok();
            case RemoteCommandKind.BlackoutOn:
                state.Blackout = true;
                return ControlProtocol.Ok();
            case RemoteCommandKind.BlackoutOff:
                state.Blackout = false;
                return ControlProtocol.Ok();
            case RemoteCommandKind.BlackoutToggle:
                state.Blackout = !state.Blackout;
                return ControlProtocol.Ok();
            case RemoteCommandKind.Identify:
                _services.Identify();
                return ControlProtocol.Ok();

            case RemoteCommandKind.Look:
            {
                if (Vm is not { } vm) return ControlProtocol.Err("UI not ready");
                if (cmd.IntArg > 0)
                {
                    return vm.ApplyLookHotkey(cmd.IntArg) ? ControlProtocol.Ok() : ControlProtocol.Err($"no look on F{cmd.IntArg}");
                }
                var look = state.LooksAndCues.Looks.FirstOrDefault(
                    l => string.Equals(l.Name, cmd.TextArg, StringComparison.OrdinalIgnoreCase));
                if (look is null) return ControlProtocol.Err($"no look named '{cmd.TextArg}'");
                vm.ApplyLook(look);
                return ControlProtocol.Ok();
            }

            case RemoteCommandKind.Next:
                return Vm?.PresenterAdvance(+1) == true ? ControlProtocol.Ok() : ControlProtocol.Err("no presenter step");
            case RemoteCommandKind.Prev:
                return Vm?.PresenterAdvance(-1) == true ? ControlProtocol.Ok() : ControlProtocol.Err("no presenter step");

            case RemoteCommandKind.ScreenOn:
            case RemoteCommandKind.ScreenOff:
            case RemoteCommandKind.ScreenToggle:
            {
                if (Vm is not { } vm) return ControlProtocol.Err("UI not ready");
                bool? target = cmd.Kind switch
                {
                    RemoteCommandKind.ScreenOn => true,
                    RemoteCommandKind.ScreenOff => false,
                    _ => null,
                };
                return vm.SetScreenEnabled(cmd.IntArg, target)
                    ? ControlProtocol.Ok()
                    : ControlProtocol.Err($"no screen {cmd.IntArg}");
            }

            case RemoteCommandKind.GroupOn:
            case RemoteCommandKind.GroupOff:
            {
                if (Vm is not { } vm) return ControlProtocol.Err("UI not ready");
                return vm.SetGroupEnabled(cmd.TextArg, cmd.Kind == RemoteCommandKind.GroupOn)
                    ? ControlProtocol.Ok()
                    : ControlProtocol.Err($"no canvas {cmd.TextArg}");
            }

            case RemoteCommandKind.AudioPlay:
                state.AudioPlayer.Playing = true;
                return ControlProtocol.Ok();
            case RemoteCommandKind.AudioStop:
                state.AudioPlayer.Playing = false;
                return ControlProtocol.Ok();
            case RemoteCommandKind.ToneOn:
                state.Tone.Enabled = true;
                return ControlProtocol.Ok();
            case RemoteCommandKind.ToneOff:
                state.Tone.Enabled = false;
                return ControlProtocol.Ok();

            case RemoteCommandKind.Stinger:
            {
                var items = state.Stingers.Items;
                var item = cmd.IntArg > 0
                    ? (cmd.IntArg <= items.Count ? items[cmd.IntArg - 1] : null)
                    : items.FirstOrDefault(i => string.Equals(i.DisplayName, cmd.TextArg, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    return ControlProtocol.Err(cmd.IntArg > 0 ? $"no stinger {cmd.IntArg}" : $"no stinger named '{cmd.TextArg}'");
                }
                return _services.Stingers.Fire(item) ? ControlProtocol.Ok() : ControlProtocol.Err(_services.Stingers.Status);
            }
            case RemoteCommandKind.StingerStop:
                _services.Stingers.Stop();
                return ControlProtocol.Ok();

            case RemoteCommandKind.Status:
                return ControlProtocol.Ok(StateJson());

            default:
                return ControlProtocol.Err($"unknown command '{cmd.TextArg}'");
        }
    }

    /// <summary>State summary for remotes. UI thread only.</summary>
    public string StateJson()
    {
        var s = _services.State;
        var vm = Vm;
        var screens = vm?.RemoteScreens() ?? Array.Empty<object>();
        var payload = new
        {
            blackout = s.Blackout,
            live = _services.Outputs.IsLive,
            looks = s.LooksAndCues.Looks.Select(l => new { name = l.Name, slot = l.Hotkey }).ToArray(),
            presenter = new
            {
                armed = s.Presenter.Armed,
                index = s.Presenter.CurrentIndex,
                count = s.Presenter.Steps.Count,
                steps = s.Presenter.Steps.Select(p => p.Label.Length > 0 ? p.Label : p.LookName).ToArray(),
            },
            screens,
            audio = new
            {
                playing = s.AudioPlayer.Playing,
                track = System.IO.Path.GetFileName(s.AudioPlayer.Path),
            },
            tone = s.Tone.Enabled,
            stingers = s.Stingers.Items.Select((i, n) => new { n = n + 1, name = i.DisplayName }).ToArray(),
            stingerPlaying = s.Stingers.PlayingName,
            playlist = _services.Playlist.Status,
            nextCue = vm?.NextCueText ?? "",
            health = HealthMonitor.Summary(DateTime.UtcNow),
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Builds StateJson from any thread.</summary>
    public Task<string> StateJsonAsync() => Dispatcher.UIThread.InvokeAsync(StateJson).GetTask();
}
