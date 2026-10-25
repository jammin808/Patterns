using Patterns.Audience;
using Patterns.Assistant;
using Patterns.Devices;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The kernel: what every role of this build stands on — the desk, a standby, a caller, a
/// timer, an arcade. Built first, from the launch and the settings, before a single desk
/// service exists: the store and the show, the log, the journal, the last run's notes, the
/// snapshot bus, the runtime of the cue lists, the assistant client, the beacon, the nodes
/// registry. Not the arcade: an engine is a role's to build (the arcade node's, the desk's for a
/// rig day), never every role's. A service written against the kernel cannot reach an output, an
/// engine or the sandbox — those are the desk's — and a service that needs something of the desk
/// says so in its constructor, as a capability the desk provides (<see cref="ITwinHost"/>,
/// <see cref="IWireHost"/>, <see cref="ICueHost"/>…) or a slot the desk fills (<see cref="Air"/>,
/// <see cref="Link"/>, <see cref="Notifier"/>).
/// </summary>
public sealed class ServiceKernel : IDisposable, IBeaconHost, IMdnsHost, IAssistantHost, IAudienceHost
{
    /// <summary>What this process is: the desk, or a node that boots a fraction of it and never opens an output.</summary>
    public NodeKind Profile { get; }

    public bool IsDesk => Profile == NodeKind.Desk;

    public SettingsStore Store { get; }

    public ShowState State { get; }

    /// <summary>The show file was upgraded on load: the desk writes it back once, so the ids minted for its looks and stingers stay the same next time.</summary>
    public bool Migrated { get; }
    /// <summary>Round 79: the schema version of a settings file a newer build wrote, 0 when none — the desk runs it as read and holds its saves.</summary>
    public int NewerSchema { get; }

    /// <summary>Round 76: what the load changed that the operator should hear about (a credential-bearing control row removed) — the health line and the log carry it, the file is written back.</summary>
    public IReadOnlyList<string> MigrationNotes { get; }

    /// <summary>Round 76: journal rows an older build wrote with the admin passcode, blanked at this boot.</summary>
    public int JournalRowsScrubbed { get; }

    /// <summary>Round 89: this machine's kept page addresses — the book every entry secures an address through, and the browser resolves from.</summary>
    public WebPasses WebPasses { get; }

    /// <summary>Round 89: what this boot's scrub took out of the show (private addresses kept, or held unkept) — the desk writes the show back once when it took any.</summary>
    public WebPassScrub WebPassesKept { get; }

    /// <summary>
    /// The secret values every exit masks in free text: the show's own (<see cref="Secrets.ValuesOf"/>) and, round 89,
    /// the real addresses this machine keeps — longest first, so a value inside another is masked whole.
    /// </summary>
    public IReadOnlyList<string> SecretValues()
        => Secrets.ValuesOf(State).Concat(WebPasses.SecretValues).Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length).ThenBy(v => v, StringComparer.Ordinal).ToList();

    public SnapshotBus Bus { get; }

    public ShowLog Journal { get; }

    /// <summary>The commissioned rig on disk and the comparison against it (round 65.9) — the desk's, a node's, the bundle's.</summary>
    public KnownGoodRig KnownGood { get; }

    public StartupBudget Startup { get; } = new();

    public AdminGate Gate { get; } = new();

    /// <summary>A supervisor that stood down last time left a note, "" otherwise.</summary>
    public string StandDownNote { get; }

    /// <summary>The supervisor's note of how the last run ended (a crash or a hang), consumed at this start; null after a clean run.</summary>
    public CrashNote? LastCrash { get; }

    /// <summary>The run right after a native fault: clips decode in software unless the Machine page says Hardware.</summary>
    public bool SafeRun { get; }

    public AssistantKeyStore AssistantKeys { get; }

    public AssistantService Assistant { get; }

    public BeaconService Beacon { get; }

    /// <summary>Round 87: this install's id on the show network — the same across restarts, kept beside the settings.</summary>
    public string ShowBusId { get; }

    string IBeaconHost.TlsFingerprint => State.Control.Enabled ? Tls.Fingerprint : "";

    /// <summary>Round 87: the link to a Stagewatch (Show Bus 1.1) — its timer, its context, its clock.</summary>
    public StagewatchLinkService Stagewatch { get; }

    /// <summary>Round 88 (Show Bus 1.2 §10): this install's certificate for the web remote's TLS, made at the first start.</summary>
    public TlsCertificateService Tls { get; }

    /// <summary>Round 87: a Stagewatch frame or context landed, or the link came or went (UI thread): the stage pages read again.</summary>
    public event Action? StagewatchChanged;

    IBeaconIdentity IMdnsHost.BeaconIdentity => Beacon;

    ShowFacts IAssistantHost.Facts() => Facts();

    /// <summary>The room's moderation: the desk asks its own assistant; a node asks the first desk the beacon heard, over its wire.</summary>
    async Task<string?> IAudienceHost.ModerateAsync(string text)
    {
        if (IsDesk)
        {
            var answer = await Assistant.AskAsync(PlayService.ModerationQuestion(text));
            return answer.Sent ? answer.Reply?.Reply : null;
        }
        var desk = await UiThread.InvokeAsync(() => Nodes.Desks() is { Count: > 0 } desks ? desks[0] : null);
        if (desk is null) return null;
        var line = await NodesService.AskNodeAsync(desk, "ASSISTANT MODERATE " + text.Replace('\n', ' '));
        if (!line.StartsWith("OK ", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line[3..]);
            return doc.RootElement.TryGetProperty("sent", out var sent) && sent.GetBoolean() && doc.RootElement.TryGetProperty("reply", out var r) ? r.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    string IMdnsHost.AppVersion => AppVersion.Current;

    /// <summary>This process announced on the network by name (mDNS), and the Companions heard announcing themselves.</summary>
    public MdnsService Mdns { get; }

    public NodesService Nodes { get; }

    /// <summary>Where every list is right now — never in the show, reset when one loads. Every role that shows a stack reads it: the desk's Run surface, a caller's, a timer's segment.</summary>
    public CueRuntime Cues { get; } = new();

    /// <summary>The rig's one clock as this process reads it: the machine's, moved by the offset a follower's link measured to the desk's; the desk's own offset is zero.</summary>
    public RoomClock Clock { get; } = new();

    /// <summary>What is on air, for the beacon packet and the nodes page — nothing until the desk fills it.</summary>
    public IAirReport Air { get; set; } = NothingOnAir.Instance;

    /// <summary>The link this process offers — none until a twin service fills it.</summary>
    public ILinkReport Link { get; set; } = NoLink.Instance;

    /// <summary>The desk's state at the moment of an ask, for the assistant's brief — a desk with nothing on air until the desk fills it.</summary>
    public Func<ShowFacts> Facts { get; set; } = () => new ShowFacts();

    /// <summary>Where a line for the operator goes: the desk's status strip once there is one, the log until then.</summary>
    public Action<string> Notifier { get; set; } = Log.Info;

    /// <summary>A line for the operator — the status strip on a desk, the log on a bare kernel.</summary>
    public void Notify(string message) => Notifier(message);

    /// <summary>The kernel, in the order the desk always built these: the store and the log first, the show, the journal, the notes, the bus, then its services.</summary>
    public static ServiceKernel Build(NodeKind profile, SettingsStore? store = null, ShowState? preloaded = null)
        => new(profile, store ?? new SettingsStore(), preloaded);

    private ServiceKernel(NodeKind profile, SettingsStore store, ShowState? preloaded)
    {
        Profile = profile;
        Store = store;
        UiThread.Capture();                     // this is the UI thread: every worker's line to it goes through the dispatcher taken here
        Log.Init(Store.BaseDirectory);

        // The start-up budget: from Main when this process went through it (the runtime before
        // Main, the settings read and the graphics choices come in as Main marked them, and
        // Avalonia's own start ends here), else from here.
        Startup.Begin(StartupBudget.ProcessStartedAt);
        if (StartupBudget.ProcessStartedAt != 0) Startup.Mark(StartupBudget.Avalonia);

        var firstRun = preloaded is null && !File.Exists(Store.SettingsPath) && !File.Exists(Store.SettingsPath + ".bak");
        State = preloaded ?? Store.Load();
        // Round 87: a desk's first run is paired from the start (fail closed) — the token is on the Remote page, TRUST.
        // A settings file that exists keeps its choice, open included; a node's pages stay as they were.
        if (PairingToken.ForFirstRun(firstRun, profile == NodeKind.Desk) is { Length: > 0 } token)
        {
            State.Control.Token = token;
            Log.Info("First run: this desk is paired from the start — the pairing token is on the Remote page, TRUST.");
        }
        Startup.Mark(StartupBudget.Settings);   // already marked by Main when it read them: kept as Main's
        State.Blackout = false;
        State.Tone.Enabled = false;             // a tone must never auto-start with the app
        Migrated = Store.LastLoadMigrated;
        MigrationNotes = Store.LastMigrationNotes;
        NewerSchema = Store.NewerSchema;                                     // round 79: a file from a newer build runs as read, unsaved

        Journal = new ShowLog(Store.BaseDirectory);
        JournalRowsScrubbed = Journal.ScrubSecrets();   // round 76: a passcode an older build journaled leaves the disk at the first boot that knows better
        // Round 89: this machine's kept page addresses, then the show's scrub before anything reads it — a private
        // address a file still holds raw is kept here and the show holds kept://… instead; one journal row says so.
        WebPasses = new WebPasses(Store.BaseDirectory) { Shows = () => new[] { State } };
        WebPassesKept = WebPasses.MigrateShow(State, Journal);
        KnownGood = new KnownGoodRig(Store.BaseDirectory, Journal);
        StandDownNote = WatchdogMarker.ReadAndClear(Store.BaseDirectory);
        LastCrash = CrashMarker.ReadAndClear(Store.BaseDirectory);
        SafeRun = LastCrash?.NativeFault ?? false;
        Bus = new SnapshotBus(State);

        AssistantKeys = new AssistantKeyStore(Store.BaseDirectory);
        Assistant = new AssistantService(this, AssistantKeys);
        ShowBusId = ShowBusIdentity.LoadOrCreate(Store.BaseDirectory);   // round 87: before the beacon, whose hello says it
        Beacon = new BeaconService(this);
        Tls = new TlsCertificateService(Store.BaseDirectory, Beacon.MachineName);
        // Round 87: the link to a Stagewatch — off until the Nodes page turns it on; the nodes' poll reconciles it each second.
        Stagewatch = new StagewatchLinkService(() => State.Control.Stagewatch, now => Beacon.ShowBus.Snapshot(now), () => StagewatchChanged?.Invoke())
        {
            SelfId = ShowBusId,                                                 // round 88: the id this desk posts its context and its proposals under
            SelfName = Beacon.MachineName,
        };
        Mdns = new MdnsService(this);
        Nodes = new NodesService(this);
    }

    public void Dispose()
    {
        Stagewatch.Dispose();
        Tls.Dispose();
        Mdns.Dispose();
        Beacon.Dispose();
    }
}
