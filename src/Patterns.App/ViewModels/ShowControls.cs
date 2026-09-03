using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.ViewModels;

/// <summary>
/// The SHOW CONTROLS drawer beside the switcher: exactly four air-targeted controls — the
/// message, the clock, the countdown and the audio track's volume — each behind an explicit
/// SEND through the action layer. So they reach the audience whether or not the sandbox is
/// open, are journaled as desk actions, and the next look recall replaces them like anything
/// else on air. The drafts here are the desk's; the "on air" texts read back the snapshot.
/// </summary>
public sealed class ShowControls : Observable
{
    private readonly AppServices _s;
    private readonly Action<string> _status;

    private bool _isOpen;
    private string _draftMessage = "";
    private string _draftMinutesText = "5";
    private double _draftVolume = 100;
    private string _messageAirText = "off";
    private string _clockAirText = "off";
    private string _countdownAirText = "off";
    private string _volumeAirText = "100%";
    private bool _messageOnAir;
    private bool _clockOnAir;
    private bool _countdownOnAir;

    public ShowControls(AppServices services, Action<string> status)
    {
        _s = services;
        _status = status;
        MessageShowCommand = new RelayCommand(() => Send(new ShowAction(ShowActionKind.MessageOn, "", _draftMessage.Trim())));
        MessageHideCommand = new RelayCommand(() => Send(new ShowAction(ShowActionKind.MessageOff)));
        ClockShowCommand = new RelayCommand(() => Send(new ShowAction(ShowActionKind.ClockOn)));
        ClockHideCommand = new RelayCommand(() => Send(new ShowAction(ShowActionKind.ClockOff)));
        CountdownStartCommand = new RelayCommand(() => Send(new ShowAction(ShowActionKind.CountdownStart, "", _draftMinutesText.Trim())));
        CountdownStopCommand = new RelayCommand(() => Send(new ShowAction(ShowActionKind.CountdownStop)));
        VolumeSendCommand = new RelayCommand(() => Send(new ShowAction(ShowActionKind.AudioVolume, "", _draftVolume.ToString("0", System.Globalization.CultureInfo.InvariantCulture))));
        _draftVolume = services.State.AudioPlayer.VolumePct;
        Refresh();
    }

    /// <summary>Open or closed; opening re-reads the air so the labels are current.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (Set(ref _isOpen, value) && value) Refresh();
        }
    }

    public string DraftMessage { get => _draftMessage; set => Set(ref _draftMessage, value ?? ""); }

    /// <summary>Minutes as typed; parsed by the action, which refuses anything but a number above zero.</summary>
    public string DraftMinutesText { get => _draftMinutesText; set => Set(ref _draftMinutesText, value ?? ""); }

    public double DraftVolume
    {
        get => _draftVolume;
        set
        {
            if (Set(ref _draftVolume, Math.Clamp(value, 0, 125))) Raise(nameof(DraftVolumeText));
        }
    }

    public string DraftVolumeText => $"{_draftVolume:0}%";

    public string MessageAirText { get => _messageAirText; private set => Set(ref _messageAirText, value); }
    public string ClockAirText { get => _clockAirText; private set => Set(ref _clockAirText, value); }
    public string CountdownAirText { get => _countdownAirText; private set => Set(ref _countdownAirText, value); }
    public string VolumeAirText { get => _volumeAirText; private set => Set(ref _volumeAirText, value); }
    public bool MessageOnAir { get => _messageOnAir; private set => Set(ref _messageOnAir, value); }
    public bool ClockOnAir { get => _clockOnAir; private set => Set(ref _clockOnAir, value); }
    public bool CountdownOnAir { get => _countdownOnAir; private set => Set(ref _countdownOnAir, value); }

    public RelayCommand MessageShowCommand { get; }
    public RelayCommand MessageHideCommand { get; }
    public RelayCommand ClockShowCommand { get; }
    public RelayCommand ClockHideCommand { get; }
    public RelayCommand CountdownStartCommand { get; }
    public RelayCommand CountdownStopCommand { get; }
    public RelayCommand VolumeSendCommand { get; }

    /// <summary>Re-read what is on air (the snapshot) and the live track volume. UI thread.</summary>
    public void Refresh()
    {
        var air = _s.Bus.Current.State;
        var message = air.Overlays.Message;
        MessageOnAir = message.Enabled;
        MessageAirText = message.Enabled ? $"on air: “{message.Text}”" : "off";
        ClockOnAir = air.Overlays.Clock.Enabled;
        ClockAirText = air.Overlays.Clock.Enabled ? "on air" : "off";
        var countdown = air.Countdown;
        CountdownOnAir = countdown.Enabled;
        CountdownAirText = !countdown.Enabled
            ? "off"
            : countdown.TargetKind == CountdownTargetKind.Duration
                ? $"running · {countdown.DurationMinutes:0.#} min"
                : "running · to a time";
        VolumeAirText = $"{_s.State.AudioPlayer.VolumePct:0}%";
    }

    private void Send(ShowAction action)
    {
        var result = _s.Actions.Execute(action, ActionOrigin.Desk);
        _status(result.Message);
        Refresh();
    }
}
