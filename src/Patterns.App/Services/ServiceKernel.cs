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

        State = preloaded ?? Store.Load();
        Startup.Mark(StartupBudget.Settings);   // already marked by Main when it read them: kept as Main's
        State.Blackout = false;
        State.Tone.Enabled = false;             // a tone must never auto-start with the app
        Migrated = Store.LastLoadMigrated;

        Journal = new ShowLog(Store.BaseDirectory);
        KnownGood = new KnownGoodRig(Store.BaseDirectory, Journal);
        StandDownNote = WatchdogMarker.ReadAndClear(Store.BaseDirectory);
        LastCrash = CrashMarker.ReadAndClear(Store.BaseDirectory);
        SafeRun = LastCrash?.NativeFault ?? false;
        Bus = new SnapshotBus(State);

        AssistantKeys = new AssistantKeyStore(Store.BaseDirectory);
        Assistant = new AssistantService(this, AssistantKeys);
        Beacon = new BeaconService(this);
        Mdns = new MdnsService(this);
        Nodes = new NodesService(this);
    }

    public void Dispose()
    {
        Mdns.Dispose();
        Beacon.Dispose();
    }
}
