using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Particles;
using Patterns.Core.Media;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Patterns.Core.LowerThirds;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- the Interactive area: devices over serial and IP -----------------------

    private string _serialPortsText = "";

    /// <summary>"COM3, COM7" — the serial ports this machine has, refreshed every few seconds while the page is open.</summary>
    public string SerialPortsText { get => _serialPortsText; private set => Set(ref _serialPortsText, value); }

    private string _interactiveStatus = "";

    /// <summary>"Interactive on · 2 devices, 1 open" — the page's status line.</summary>
    public string InteractiveStatus { get => _interactiveStatus; private set => Set(ref _interactiveStatus, value); }

    private void AddDevice(DeviceLink link)
    {
        var n = State.Interactive.Devices.Count + 1;
        var device = new DeviceConfig
        {
            Name = link == DeviceLink.Serial ? (n == 1 ? "Arduino" : $"Arduino {n}") : (n == 1 ? "Pi" : $"Device {n}"),
            Link = link,
        };
        device.Triggers.Add(new DeviceTriggerConfig { Match = "BTN1", Command = "CUE GO" });
        device.Triggers.Add(new DeviceTriggerConfig { Match = "BTN2", Command = "NEXT" });
        BulkEdit(() => State.Interactive.Devices.Add(device));
        StatusMessage = link == DeviceLink.Serial
            ? $"{device.Name} added — type its port (COM3, or /dev/ttyUSB0), then switch the Interactive area on."
            : $"{device.Name} added — type its address (192.168.1.50, or host:7000), then switch the Interactive area on.";
    }

    private void RemoveDevice(DeviceConfig? device)
    {
        if (device is null) return;
        BulkEdit(() => State.Interactive.Devices.Remove(device));
        StatusMessage = $"{device.Name} removed.";
    }

    private void TestDevice(DeviceConfig? device)
    {
        if (device is null) return;
        Report(_services.Devices.Send(device.Name, device.TestText));
    }

    private void AddTrigger(DeviceConfig? device)
    {
        if (device is null) return;
        var n = device.Triggers.Count + 1;
        BulkEdit(() => device.Triggers.Add(new DeviceTriggerConfig { Match = $"BTN{n}", Command = n == 1 ? "CUE GO" : "" }));
    }

    private void RemoveTrigger(DeviceTriggerConfig? trigger)
    {
        if (trigger is null) return;
        var owner = State.Interactive.Devices.FirstOrDefault(d => d.Triggers.Contains(trigger));
        if (owner is null) return;
        BulkEdit(() => owner.Triggers.Remove(trigger));
    }

    /// <summary>The 1 s poll: links reconciled and their status words fresh; the serial port list every few seconds.</summary>
    private void PollDevices()
    {
        _services.Devices.Poll();
        var config = State.Interactive;
        var open = _services.Devices.OpenCount;
        InteractiveStatus = !config.Enabled
            ? config.Devices.Count == 0 ? "Interactive area off — add a device below." : $"Interactive area off — {config.Devices.Count} device{(config.Devices.Count == 1 ? "" : "s")} waiting."
            : $"Interactive on · {config.Devices.Count} device{(config.Devices.Count == 1 ? "" : "s")}, {open} open.";
        if (_statusTicks % 5 == 0 && SelectedPageIndex == Shell.IndexOf("Interactive")) SerialPortsText = "Serial ports on this machine: " + DeviceService.SerialPortsText();
    }

    // ---- the Install page: a permanent install's clock, remote administration, updates ------------

    private string _installStatus = "";
    private string _installLastEvent = "";
    private string _installProblems = "";
    private string _adminUrlText = "";
    private string _updateStatus = "";
    private string _updateLastNote = "";
    private string _managementStatus = "";
    private string _supportBundleText = "";
    private string _installSignature = "";
    private List<string> _installLookChoices = new();
    private List<string> _installSoundChoices = new();

    /// <summary>"Schedule on · programme 'Daytime' until 17:00 · next: advert Lunch offer at 12:30." — the page's line.</summary>
    public string InstallStatus { get => _installStatus; private set => Set(ref _installStatus, value); }

    /// <summary>The last thing the clock did, with its time.</summary>
    public string InstallLastEvent { get => _installLastEvent; private set => Set(ref _installLastEvent, value); }

    /// <summary>Every row that cannot do what it says, one line each; "" when all is well.</summary>
    public string InstallProblems { get => _installProblems; private set => Set(ref _installProblems, value); }

    /// <summary>Where the ADMIN page is, or why there is none.</summary>
    public string AdminUrlText { get => _adminUrlText; private set => Set(ref _adminUrlText, value); }

    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }

    public string UpdateLastNote { get => _updateLastNote; private set => Set(ref _updateLastNote, value); }

    public string ManagementStatus { get => _managementStatus; private set => Set(ref _managementStatus, value); }

    public string SupportBundleText { get => _supportBundleText; private set => Set(ref _supportBundleText, value); }

    /// <summary>The day's rows: programme windows and firings in time order, with NOW and done.</summary>
    public ObservableCollection<InstallRow> InstallTimeline { get; } = new();

    /// <summary>The show's look names, for the rows' pickers.</summary>
    public List<string> InstallLookChoices { get => _installLookChoices; private set => Set(ref _installLookChoices, value); }

    /// <summary>The VOGs of the Audio page's library, by name, for an announcement's sound.</summary>
    public List<string> InstallSoundChoices { get => _installSoundChoices; private set => Set(ref _installSoundChoices, value); }

    private void AddSlot(SlotKind kind)
    {
        var count = State.Install.Slots.Count(s => s.Kind == kind) + 1;
        var slot = new ScheduleSlotConfig
        {
            Kind = kind,
            Name = kind switch
            {
                SlotKind.Programme => count == 1 ? "Daytime" : $"Programme {count}",
                SlotKind.Advert => count == 1 ? "Offer" : $"Advert {count}",
                _ => count == 1 ? "Closing time" : $"Announcement {count}",
            },
            Start = kind == SlotKind.Programme ? "09:00" : "12:00",
            End = kind == SlotKind.Programme ? "17:00" : "18:00",
            EveryMinutes = kind == SlotKind.Programme ? 0 : 60,
            DurationSeconds = kind == SlotKind.Announcement ? 20 : 30,
            Text = kind == SlotKind.Announcement ? "The store closes in 15 minutes" : "",
            Look = kind == SlotKind.Announcement ? "" : State.LooksAndCues.Looks.FirstOrDefault()?.Name ?? "",
        };
        BulkEdit(() => State.Install.Slots.Add(slot));
        StatusMessage = kind switch
        {
            SlotKind.Programme => $"{slot.Name} added — pick its look, its days and its hours; switch the schedule on when the rota is right.",
            SlotKind.Advert => $"{slot.Name} added — pick its look, when it fires and for how long; name screens to keep the others as they are.",
            _ => $"{slot.Name} added — its words, a VOG, when it fires; ANNOUNCE {slot.Name} fires it by hand.",
        };
        RefreshInstallTimeline(force: true);
    }

    private void RemoveSlot(ScheduleSlotConfig? slot)
    {
        if (slot is null) return;
        BulkEdit(() => State.Install.Slots.Remove(slot));
        StatusMessage = $"{slot.Name} removed.";
        RefreshInstallTimeline(force: true);
    }

    private void PlaySlot(ScheduleSlotConfig? slot)
    {
        if (slot is null) return;
        Report(slot.Kind == SlotKind.Advert
            ? _services.Actions.Execute(new ShowAction(ShowActionKind.AdvertPlay, slot.Name), ActionOrigin.Desk)
            : _services.Actions.Execute(new ShowAction(ShowActionKind.Announce, slot.Name), ActionOrigin.Desk));
    }

    private void EndInstallOverride()
    {
        var on = _services.Install.Runtime.Override;
        if (on is null)
        {
            StatusMessage = "Nothing is on over the programme.";
            return;
        }
        Report(_services.Actions.Execute(on.Kind == SlotKind.Advert ? ShowActionKind.AdvertOff : ShowActionKind.AnnounceOff, ActionOrigin.Desk));
    }

    private void BuildSupportBundle()
    {
        try
        {
            var dir = _services.Store.BaseDirectory;
            var path = System.IO.Path.Combine(dir, SupportBundle.FileNameFor(DateTime.Now));
            var info = string.Join(Environment.NewLine,
                $"Patterns support bundle — {DateTime.Now:yyyy-MM-dd HH:mm}",
                $"Site: {(State.Install.SiteName.Length > 0 ? State.Install.SiteName : "(unnamed)")} · machine {Environment.MachineName}",
                $"Build: {UpdateService.RunningVersion} · .NET {Environment.Version} · {Environment.OSVersion}",
                $"Show: {State.Name} · folder {dir}",
                $"Health: {HealthMonitor.Summary(DateTime.UtcNow)}",
                $"Install: {_services.Install.Status}",
                $"Update: {_services.Updates.Status}",
                $"Management: {_services.Management.Status}");
            var entries = SupportBundle.Build(dir, path, info);
            SupportBundleText = $"Written: {path} ({entries.Count} entries — {string.Join(", ", entries)}).";
            StatusMessage = $"Support bundle written beside the settings: {System.IO.Path.GetFileName(path)}.";
            Log.Info($"Support bundle written: {path}");
        }
        catch (Exception ex)
        {
            SupportBundleText = $"Could not write the bundle: {ex.Message}";
            Log.Warn("Support bundle failed.", ex);
        }
    }

    private void ApplyUpdate()
    {
        // The desk's own button needs no passcode: whoever sits at the machine owns it.
        Report(_services.Updates.Apply("", ActionOrigin.Desk, byPolicy: true));
    }

    /// <summary>The 1 s poll: the clock ticks, the folders and the check-in follow, the page's words refresh.</summary>
    private void PollInstall()
    {
        _services.Install.Tick();
        if (_statusTicks % 5 == 0) _services.Updates.Scan();
        _services.Updates.TickWindow(DateTime.Now);
        _services.Management.Tick(DateTime.UtcNow);
        InstallStatus = _services.Install.Status;
        InstallLastEvent = _services.Install.LastEvent;
        UpdateStatus = _services.Updates.Status;
        UpdateLastNote = _services.Updates.LastNote;
        ManagementStatus = _services.Management.Status;
        var passcode = State.Install.AdminPasscode;
        AdminUrlText = passcode.Length == 0
            ? "No passcode: the web remote has no ADMIN page and RESTART / UPDATE APPLY on the wire are refused."
            : State.Control.Enabled
                ? $"ADMIN page: {(_services.Control.RemoteUrls().Skip(1).FirstOrDefault() ?? _services.Control.RemoteUrls()[0])}admin"
                : "ADMIN page: switch remote control on (Remote page) to reach it.";
        RefreshInstallTimeline(force: false);
    }

    /// <summary>The day's rows and the pickers, rebuilt when the rows or the lists change (a cheap signature) and every few seconds for NOW / done.</summary>
    private void RefreshInstallTimeline(bool force)
    {
        var cfg = State.Install;
        var sb = new System.Text.StringBuilder();
        foreach (var s in cfg.Slots) sb.Append(s.Id).Append(s.Name).Append(s.Kind).Append(s.Enabled).Append(s.Days).Append(s.From).Append(s.Until).Append(s.Start).Append(s.End).Append(s.EveryMinutes).Append(s.DurationSeconds).Append(s.Look).Append(s.Text).Append(s.Sound).Append(s.Screens).Append('|');
        sb.Append(cfg.IdleLook).Append('|');
        foreach (var l in State.LooksAndCues.Looks) sb.Append(l.Name).Append(',');
        sb.Append('|');
        foreach (var i in State.Stingers.Items) sb.Append(i.DisplayName).Append(i.Kind).Append(',');
        var signature = sb.ToString();
        var changed = signature != _installSignature;
        if (!force && !changed && _statusTicks % 10 != 0) return;
        _installSignature = signature;
        var now = DateTime.Now;
        InstallTimeline.Clear();
        foreach (var row in Schedule.Timeline(cfg, DateOnly.FromDateTime(now)))
        {
            InstallTimeline.Add(new InstallRow(row.TimeText, row.KindText, row.Name, row.Detail, row.StateAt(now)));
        }
        Raise(nameof(InstallTimeline));
        var problems = Schedule.Problems(cfg, State);
        InstallProblems = problems.Count == 0 ? "" : string.Join(Environment.NewLine, problems.Select(p => "⚠ " + p));
        if (changed || force)
        {
            InstallLookChoices = State.LooksAndCues.Looks.Select(l => l.Name).ToList();
            InstallSoundChoices = State.Stingers.Items.Where(i => i.Kind == StingerKind.Vog).Select(i => i.DisplayName).ToList();
        }
    }

    private void ResetBlend()
    {
        if (_selectedPlacement is not { } p) return;
        p.BlendAuto = false;
        p.BlendLeftPx = p.BlendTopPx = p.BlendRightPx = p.BlendBottomPx = 0;
        p.BlendCurve = BlendCurve.SCurve;
        p.BlendGamma = 1.0;
        ReconcilePlacements();
        RaiseSelection();
    }

    // ---- admin ---------------------------------------------------------------

    private const double SparkW = 300;
    private const double SparkH = 56;

    private string _adminCpuText = "—";
    private string _adminMemText = "—";
    private string _adminGpuText = "—";
    private string _adminRenderText = "—";
    private string _adminExtrasText = "—";
    private string _gpuActiveText = "";
    private string _graphicsApplyStatus = "";
    private string _machineOverview = "";
    private Avalonia.Points _adminCpuSpark = new();
    private Avalonia.Points _adminRamSpark = new();
    private Avalonia.Points _adminFpsSpark = new();
    private string _suggestionsKey = "";
    private string? _cpuNameCache;
    private int _statusTicks;

    public string AdminCpuText { get => _adminCpuText; private set => Set(ref _adminCpuText, value); }
    public string AdminMemText { get => _adminMemText; private set => Set(ref _adminMemText, value); }
    public string AdminGpuText { get => _adminGpuText; private set => Set(ref _adminGpuText, value); }
    public string AdminRenderText { get => _adminRenderText; private set => Set(ref _adminRenderText, value); }
    public string AdminExtrasText { get => _adminExtrasText; private set => Set(ref _adminExtrasText, value); }
    public string GpuActiveText { get => _gpuActiveText; private set => Set(ref _gpuActiveText, value); }
    public string GraphicsApplyStatus { get => _graphicsApplyStatus; private set => Set(ref _graphicsApplyStatus, value); }
    public string MachineOverview { get => _machineOverview; private set => Set(ref _machineOverview, value); }
    public Avalonia.Points AdminCpuSpark { get => _adminCpuSpark; private set => Set(ref _adminCpuSpark, value); }
    public Avalonia.Points AdminRamSpark { get => _adminRamSpark; private set => Set(ref _adminRamSpark, value); }
    public Avalonia.Points AdminFpsSpark { get => _adminFpsSpark; private set => Set(ref _adminFpsSpark, value); }

    // ---- the dashboard: HEALTH AT A GLANCE ---------------------------------------------

    private CheckFacts? _dashboardFacts;
    private string _dashboardHeadline = "Reading the machine…";
    private string _dashboardDetail = "the first numbers arrive within a second.";
    private string _dashboardUptime = "";
    private Avalonia.Media.IBrush _dashboardDot = LightBrushes.For(CheckLight.Grey);
    private Avalonia.Points _adminCpuDaySpark = new();
    private Avalonia.Points _adminRamDaySpark = new();
    private Avalonia.Points _adminFpsDaySpark = new();
    private string _adminDayText = "the day's lines appear after the first minute";

    /// <summary>The twelve tiles — outputs, render, CPU, memory, GPU, NDI, stream, audio, remote, watchdog, power, disk — updated in place.</summary>
    public ObservableCollection<DashboardTileView> DashboardTiles { get; } = new();

    /// <summary>"All clear" / "Ready, with cautions — NDI" / "Attention needed — CPU, POWER".</summary>
    public string DashboardHeadline { get => _dashboardHeadline; private set => Set(ref _dashboardHeadline, value); }
    public string DashboardDetail { get => _dashboardDetail; private set => Set(ref _dashboardDetail, value); }
    public string DashboardUptime { get => _dashboardUptime; private set => Set(ref _dashboardUptime, value); }
    public Avalonia.Media.IBrush DashboardDot { get => _dashboardDot; private set => Set(ref _dashboardDot, value); }

    /// <summary>The day so far: the 30-second aggregates as lines beside the last three minutes.</summary>
    public Avalonia.Points AdminCpuDaySpark { get => _adminCpuDaySpark; private set => Set(ref _adminCpuDaySpark, value); }
    public Avalonia.Points AdminRamDaySpark { get => _adminRamDaySpark; private set => Set(ref _adminRamDaySpark, value); }
    public Avalonia.Points AdminFpsDaySpark { get => _adminFpsDaySpark; private set => Set(ref _adminFpsDaySpark, value); }
    public string AdminDayText { get => _adminDayText; private set => Set(ref _adminDayText, value); }

    public ObservableCollection<SuggestionRow> AdminSuggestions { get; } = new();
    public ObservableCollection<GpuRow> GpuRows { get; } = new();
    public ObservableCollection<string> GpuAdapterNames { get; } = new();

    public bool GpuSpecificVisible => State.Admin.Graphics.Preference == GpuPreferenceKind.Specific;

    /// <summary>The Copy support info payload (also used by tests to sanity-check content).</summary>
    public string BuildSupportInfo() => _services.Metrics.SupportInfo();

    // ---- the super-check ----------------------------------------------------------------------

    private RelayCommand? _runSuperCheck;
    private string _superCheckHeadline = "";
    private string _superCheckLevelText = "";
    private string _superCheckSavedText = "";
    private string _superCheckText = "";
    private Avalonia.Media.IBrush _superCheckDot = Avalonia.Media.Brushes.Gray;

    public RelayCommand RunSuperCheckCommand => _runSuperCheck ??= new RelayCommand(RunSuperCheck);

    public ObservableCollection<CheckRowView> SuperCheckRows { get; } = new();

    public string SuperCheckHeadline { get => _superCheckHeadline; private set => Set(ref _superCheckHeadline, value); }
    public string SuperCheckLevelText { get => _superCheckLevelText; private set => Set(ref _superCheckLevelText, value); }
    public string SuperCheckSavedText { get => _superCheckSavedText; private set => Set(ref _superCheckSavedText, value); }

    /// <summary>The report as plain text (the Copy button's payload).</summary>
    public string SuperCheckText { get => _superCheckText; private set => Set(ref _superCheckText, value); }

    public Avalonia.Media.IBrush SuperCheckDot { get => _superCheckDot; private set => Set(ref _superCheckDot, value); }

    public bool HasSuperCheck => SuperCheckRows.Count > 0;

    /// <summary>One press: every fact, every light, the level — on the page and in a file beside the exe.</summary>
    public void RunSuperCheck()
    {
        var report = _services.Metrics.RunSuperCheck();
        SuperCheckRows.Clear();
        foreach (var row in report.Rows)
        {
            SuperCheckRows.Add(new CheckRowView(row.Section, row.Item, row.Value, row.Note, LightBrush(row.Light)));
        }
        SuperCheckHeadline = report.Headline;
        SuperCheckDot = LightBrush(report.Overall);
        SuperCheckLevelText = report.Level.Reasons.Count > 0
            ? $"Level: {report.Level.Name} (score {report.Level.Score}) — {string.Join("; ", report.Level.Reasons)}"
            : $"Level: {report.Level.Name} (score {report.Level.Score})";
        SuperCheckText = SuperCheck.ToText(report);
        var path = _services.Metrics.LastReportPath;
        SuperCheckSavedText = path.Length > 0 ? $"Saved: {path}" : "The report could not be written beside the exe — copy it instead.";
        Raise(nameof(HasSuperCheck));
        StatusMessage = $"Super-check: {report.Headline}";
    }

    private static Avalonia.Media.IBrush LightBrush(CheckLight light) => light switch
    {
        CheckLight.Green => Avalonia.Media.Brush.Parse("#2EE68A"),
        CheckLight.Amber => Avalonia.Media.Brush.Parse("#FFC24D"),
        CheckLight.Red => Avalonia.Media.Brush.Parse("#E0342E"),
        _ => Avalonia.Media.Brush.Parse("#4A505E"),
    };

    /// <summary>The Admin pages (the machine, help) take the room: the screens reduce to a strip while one is selected.</summary>
    public bool PageWantsRoom => !_isRunLayout && Shell.Pages[_page].Group == ShellGroup.Admin;

    private void OnGraphicsChoiceChanged()
    {
        GpuService.RecordAppliedPath(State);
        var registry = GpuService.ApplyWindowsPreference(State.Admin.Graphics);
        GraphicsApplyStatus = (registry.Length > 0 ? registry + " " : "") +
                              "Takes effect at the next start — use Restart app below.";
        RebuildGpuRows();
        Raise(nameof(GpuSpecificVisible));
    }

    private void RebuildGpuRows()
    {
        GpuRows.Clear();
        GpuAdapterNames.Clear();
        var adapters = GpuService.Adapters;
        var best = GpuSelector.ChooseBest(adapters);
        for (var i = 0; i < adapters.Count; i++)
        {
            var gpu = adapters[i];
            var badges = new List<string>();
            if (gpu.DedicatedVideoMemoryMB > 0) badges.Add($"{gpu.DedicatedVideoMemoryMB / 1024.0:0.#} GB");
            badges.Add(gpu.VendorName);
            if (i == best) badges.Add("best");
            if (gpu.IsSoftware) badges.Add("software fallback");
            if (string.Equals(gpu.Name, GpuService.ActiveAdapterName, StringComparison.OrdinalIgnoreCase) ||
                (GpuService.ActiveAdapterName.Length == 0 && i == GpuService.RequestedIndex))
            {
                badges.Add("selected");
            }
            GpuRows.Add(new GpuRow(gpu.Name, string.Join(" · ", badges)));
            if (!gpu.IsSoftware) GpuAdapterNames.Add(gpu.Name);
        }
        if (adapters.Count == 0)
        {
            GpuRows.Add(new GpuRow("No adapters detected", "GPU detection runs on Windows."));
        }
        if (GpuAdapterNames.Count == 0) GpuAdapterNames.Add("");
        GpuActiveText = GpuService.ActiveAdapterName.Length > 0
            ? $"Rendering on: {GpuService.ActiveAdapterName}"
            : GpuService.RequestedName.Length > 0
                ? $"Will render on: {GpuService.RequestedName}"
                : "Adapter choice: Windows default";
    }

    private void RestartApp()
    {
        if (!LaunchOptions.IsChild)
        {
            StatusMessage = "Restart in place needs the watchdog (see Stability below) — with it off, close and reopen Patterns instead.";
            return;
        }
        var code = _services.PrepareRestart();
        StatusMessage = "Restarting — the show comes straight back…";
        Log.Info("Restart requested from the Machine page.");
        (Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IControlledApplicationLifetime)?.Shutdown(code);
    }

    private void OpenAppFolder()
    {
        var dir = _services.Store.BaseDirectory;
        StatusMessage = $"App folder: {dir}";
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Could not open the app folder.", ex);
        }
    }

    private void PollAdmin()
    {
        var metrics = _services.Metrics;
        RefreshDashboard(metrics.Current);
        if (metrics.Current is not { } s) return;

        AdminCpuText = $"this app {Pct(s.CpuAppPct)} · whole computer {Pct(s.CpuSystemPct)}";
        AdminMemText = $"this app {Mb(s.RamAppMB)} · computer {Pct(s.RamSystemPct)}" +
                       (s.RamTotalMB > 0 ? $" of {s.RamTotalMB / 1024.0:0.0} GB" : "");
        var vram = s.VramTotalMB > 0 ? $"video memory {Mb(s.VramUsedMB)} of {Mb(s.VramTotalMB)}" : "video memory n/a";
        AdminGpuText = $"busy {Pct(s.GpuBusyPct)} · {vram}";
        AdminRenderText = s.OutputWindows > 0
            ? $"outputs {s.OutputFps:0} fps × {s.OutputWindows} window{(s.OutputWindows == 1 ? "" : "s")} · " +
              $"preview {s.PreviewFps:0} fps · worst frame {s.WorstFrameMs:0.0} ms" +
              (s.SlowFrames > 0 ? $" · {s.SlowFrames} slow" : "")
            : $"preview {s.PreviewFps:0} fps — outputs closed";
        AdminExtrasText = $"threads {s.Threads} · handles {s.Handles}" +
                          (s.GcPausePct >= 0 ? $" · GC pause {s.GcPausePct:0.0}%" : "") +
                          (s.DiskFreeGB >= 0 ? $" · disk free {s.DiskFreeGB:0.0} GB" : "") +
                          $" · {(s.OnBattery ? $"ON BATTERY{(s.BatteryPct >= 0 ? $" {s.BatteryPct}%" : "")}" : "mains power")}";

        AdminCpuSpark = Spark(metrics.History.Tail(180, x => x.CpuSystemPct), 100);
        AdminRamSpark = Spark(metrics.History.Tail(180, x => x.RamAppMB), null);
        AdminFpsSpark = Spark(metrics.History.Tail(180, x => x.OutputWindows > 0 ? x.OutputFps : x.PreviewFps), 66);

        var key = string.Join("|", metrics.Suggestions.Select(x => x.Id + (int)x.Severity));
        if (key != _suggestionsKey)
        {
            _suggestionsKey = key;
            AdminSuggestions.Clear();
            foreach (var advice in metrics.Suggestions)
            {
                AdminSuggestions.Add(new SuggestionRow(advice.Title, advice.Detail, SeverityBrush(advice)));
            }
        }

        if (MachineOverview.Length == 0 || _statusTicks % 30 == 0)
        {
            MachineOverview = BuildMachineOverview(s);
        }

        static string Pct(double v) => v < 0 ? "n/a" : $"{v:0}%";
        static string Mb(double v) => v < 0 ? "n/a" : v >= 1024 ? $"{v / 1024.0:0.0} GB" : $"{v:0} MB";
    }

    /// <summary>
    /// HEALTH AT A GLANCE: the facts every five seconds (a probe or two), the live sample every
    /// second, the tiles updated in place, the verdict over them and the advice, and the day's lines.
    /// </summary>
    private void RefreshDashboard(MetricSample? now)
    {
        if (_dashboardFacts is null || _statusTicks % 5 == 0) _dashboardFacts = _services.Metrics.GatherFacts();
        var facts = _dashboardFacts;
        var tiles = HealthDashboard.Tiles(facts, now);
        if (DashboardTiles.Count != tiles.Count)
        {
            DashboardTiles.Clear();
            foreach (var tile in tiles) DashboardTiles.Add(new DashboardTileView(tile));
        }
        else
        {
            for (var i = 0; i < tiles.Count; i++) DashboardTiles[i].Update(tiles[i]);
        }
        var verdict = HealthDashboard.Verdict(tiles, _services.Metrics.Suggestions);
        DashboardHeadline = verdict.Headline;
        DashboardDetail = verdict.Detail;
        DashboardDot = LightBrushes.For(verdict.Light);
        DashboardUptime = HealthDashboard.Uptime(facts.UptimeSeconds);

        var day = _services.Metrics.History.LongTerm;
        if (day.Count >= 2)
        {
            AdminCpuDaySpark = Spark(SparklinePath.Downsample(day.Select(x => Math.Max(0, x.CpuSystemPct)).ToList(), 180), 100);
            AdminRamDaySpark = Spark(SparklinePath.Downsample(day.Select(x => Math.Max(0, x.RamAppMB)).ToList(), 180), null);
            AdminFpsDaySpark = Spark(SparklinePath.Downsample(day.Select(x => x.OutputWindows > 0 ? x.OutputFps : x.PreviewFps).ToList(), 180), 66);
            var minutes = day.Count * MetricsHistory.AggregateEvery / 60;
            AdminDayText = minutes >= 60 ? $"the day so far: {minutes / 60} h {minutes % 60:00} min of 30-second averages" : $"the day so far: {minutes} min of 30-second averages";
        }
    }

    private static Avalonia.Media.IBrush SeverityBrush(HealthSuggestion s) => s switch
    {
        { Severity: HealthSeverity.Warning } => Avalonia.Media.Brush.Parse("#FF5C7A"),
        { Severity: HealthSeverity.Advice } => Avalonia.Media.Brush.Parse("#FFC24D"),
        { Id: "all-clear" } => Avalonia.Media.Brush.Parse("#2EE68A"),
        _ => Avalonia.Media.Brush.Parse("#9AA7B8"),
    };

    private static Avalonia.Points Spark(IReadOnlyList<double> values, double? fixedMax)
    {
        var points = new Avalonia.Points();
        foreach (var (x, y) in SparklinePath.Points(values, SparkW, SparkH, fixedMax))
        {
            points.Add(new Avalonia.Point(x, y));
        }
        return points;
    }

    private string BuildMachineOverview(MetricSample s)
    {
        try
        {
            _cpuNameCache ??= WinRegistry.ReadCpuName();
            var parts = new List<string>
            {
                $"{Environment.MachineName} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
                $"CPU: {(_cpuNameCache.Length > 0 ? _cpuNameCache : "unknown")} · {Environment.ProcessorCount} threads",
            };
            if (s.RamTotalMB > 0) parts.Add($"RAM: {s.RamTotalMB / 1024.0:0.0} GB");
            var screens = _services.Screens.All;
            if (screens.Count > 0)
            {
                parts.Add("Screens: " + string.Join(", ",
                    screens.Select(sc => $"{sc.Bounds.Width}×{sc.Bounds.Height}{(sc.IsPrimary ? "★" : "")}")));
            }
            parts.Add($"App: Patterns · .NET {Environment.Version} · folder {_services.Store.BaseDirectory}");
            return string.Join(Environment.NewLine, parts);
        }
        catch (Exception ex)
        {
            Log.Warn("Machine overview failed.", ex);
            return "";
        }
    }
}
