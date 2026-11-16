using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A link's rise is published whole. The LINK verdict that makes the Stagewatch link live at level 1 gives the key's standing in the
/// same step under the core's gate, so no publication of the rise — no saying of the record (the journal's row, the log line,
/// <see cref="StagewatchLinkService.Published"/>), no reading the desk's readers make when the link tells them it moved, no STATE
/// written then, no reading at any moment between — shows the record live at level 1 beside a key whose word is still unset: a state
/// that never was. Every publication is read, not the last: the link's own, each in the order said; the desk's readers', on each
/// change the link tells them of, and STATE's <c>bus.stagewatch</c> written on each; and a reader of the test's own on a thread of its
/// own, reading the readers' one read (<see cref="StagewatchLinkService.SightWithLink"/>) for as long as the rise takes and after it.
/// That reader samples: a tear shorter than its period can pass it unseen, so it holds the readers' one read only as often as it
/// samples; the link's sayings and the readings on each change are taken every time.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    /// <summary>
    /// The first rise: the key proved with LINK at level 1. Every publication that shows the record verified carries the verdict's
    /// word, the first of them included; one journal row and one log line for the rise, and one line for the key taken.
    /// </summary>
    [AvaloniaFact]
    public void EveryPublicationOfALinksRiseShowsTheKeysWordWithTheRecordItMadeLive()
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
        Dispatcher.UIThread.RunJobs();
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        using var rise = new RiseWatch(services, link);
        try
        {
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            DialAsAnotherMachine(link, config, port, key);

            PumpUntil(() => link.Record is { State: "live", PeerId: StagewatchId, Level: 1, Proof: "mutual" }, 20000, link);
            PumpUntil(() => rise.Said.Any(p => Verified(p.Record)) && rise.Told.Any(p => Verified(p.Record)) && !rise.State.IsEmpty, 5000, link);
            PumpFor(300);                                                                         // the publications after the rise too
            rise.Stop();

            rise.AssertWhole(StagewatchId);
            Assert.Equal("key", link.Key.Word);
            Assert.Equal("key", rise.Said.First(p => Verified(p.Record)).Key.Word);               // the first saying of the rise carries the word

            // One journal row for the rise and one line for the key taken: the standing moved in the verdict's step, said once after it.
            Assert.Single(services.Journal.Tail(500), e => e.Kind == "StagewatchLink" && e.Outcome == "live");
            var log = LinkLog(services, b.Dir);
            Assert.Equal(1, log.Split(" | ").Count(l => l.Contains("takes this desk's key — verified key", StringComparison.Ordinal)));
            Assert.Equal(1, log.Split(" | ").Count(l => l.Contains("Stagewatch link to main " + StagewatchId, StringComparison.Ordinal) && l.Contains("→ live:", StringComparison.Ordinal)));
        }
        finally
        {
            rise.Stop();
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// The twin for the retarget's second rise: the address read keyless in its public view, then the node's hello heard
    /// there and the link dialled again as the node, the key proved with LINK. The address's record is live at level 0 with no key
    /// word, as it is; the node's is whole at every publication of its rise, the first of them included.
    /// </summary>
    [AvaloniaFact]
    public void EveryPublicationOfARetargetsSecondRiseShowsTheKeysWordWithTheNodesRecord()
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
        Dispatcher.UIThread.RunJobs();
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        using var rise = new RiseWatch(services, link);
        try
        {
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            config.KeyFor = StagewatchKeyFor.Node(StagewatchId);
            DialAsAnotherMachine(link, config, port, key);

            PumpUntil(() => link.Record is { State: "live" } r && r.PeerName == "127.0.0.1" && r.Show == "live", 20000, link);
            Assert.Contains(rise.Said, p => p.Record is { State: "live", Level: 0 } && p.Key.Word.Length == 0);   // the address, keyless: as it is

            PumpUntil(() =>
            {
                HearStagewatch(services, port, cert.Fingerprint);
                return link.Record is { State: "live", PeerId: StagewatchId, Level: 1, Proof: "mutual" };
            }, 20000, link);
            Assert.Equal("key", link.Key.Word);
            Assert.Equal(2, Dials(peer));
            PumpUntil(() => rise.Said.Any(p => Verified(p.Record)) && rise.Told.Any(p => Verified(p.Record)) && !rise.State.IsEmpty, 5000, link);
            PumpFor(300);
            rise.Stop();

            rise.AssertWhole(StagewatchId);
            Assert.Equal("key", rise.Said.First(p => Verified(p.Record)).Key.Word);
            // No rise said twice: the address's rise has its row, and the node's, a second connecting → live, is the change log's repeat
            // of that pair, a row at most — never one for the record and another for the key.
            Assert.InRange(services.Journal.Tail(500).Count(e => e.Kind == "StagewatchLink" && e.Outcome == "live"), 1, 2);
        }
        finally
        {
            rise.Stop();
            config.KeyFor = "";
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>A record LINK verified: level 1, both proofs, the link up.</summary>
    private static bool Verified(LinkRecord r) => r is { Level: 1, Proof: "mutual", State: "live" or "stale" };

    /// <summary>
    /// Every publication of one link while a test runs: what the link says (its <see cref="StagewatchLinkService.Published"/>, in order),
    /// what the desk's readers read each time the link tells them it moved (the kernel's change, on the UI thread), STATE's
    /// <c>bus.stagewatch</c> written at each such change, and what a reader of the test's own reads of
    /// <see cref="StagewatchLinkService.SightWithLink"/> on its own thread, every millisecond or so. A fault in a reading or a reader
    /// that does not stop is kept and failed by <see cref="AssertWhole"/>, never thrown on the link's or the reader's thread.
    /// </summary>
    private sealed class RiseWatch : IDisposable
    {
        private readonly AppServices _services;
        private readonly StagewatchLinkService _link;
        private readonly Action<LinkRecord, StagewatchKey> _said;
        private readonly Action _told;
        private readonly Thread _reader;
        private volatile bool _stop;
        private volatile Exception? _fault;
        private int _stopped;
        private bool _joined = true;

        public ConcurrentQueue<(LinkRecord Record, StagewatchKey Key)> Said { get; } = new();
        public ConcurrentQueue<(LinkRecord Record, StagewatchKey Key)> Told { get; } = new();
        public ConcurrentQueue<(LinkRecord Record, StagewatchKey Key)> Read { get; } = new();
        public ConcurrentQueue<Stated> State { get; } = new();
        public long Reads;

        /// <summary>STATE's <c>bus.stagewatch</c> as one change wrote it: its link record's level, proof, state and peer id, and its key word.</summary>
        public sealed record Stated(int Level, string Proof, string LinkState, string? PeerId, string Key)
        {
            public bool Verified => Level == 1 && Proof == "mutual" && LinkState is "live" or "stale";
        }

        public RiseWatch(AppServices services, StagewatchLinkService link)
        {
            _services = services;
            _link = link;
            _said = (r, k) => Said.Enqueue((r, k));
            _told = () =>
            {
                try
                {
                    Take(Told);
                    TakeState();
                }
                catch (Exception e)
                {
                    _fault ??= e;
                }
            };
            link.Published += _said;
            services.Kernel.StagewatchChanged += _told;
            _reader = new Thread(() =>
            {
                try
                {
                    while (!_stop)
                    {
                        Take(Read);
                        Interlocked.Increment(ref Reads);
                        Thread.Sleep(1);
                    }
                }
                catch (Exception e)
                {
                    _fault ??= e;
                }
            }) { IsBackground = true, Name = "rise reader" };
            _reader.Start();
        }

        /// <summary>One reading of the readers' one read: kept when it shows the link verified (the rise and what follows it).</summary>
        private void Take(ConcurrentQueue<(LinkRecord, StagewatchKey)> into)
        {
            var s = _link.SightWithLink;
            if (s.Link is { } r && Verified(r)) into.Enqueue((r, s.Key));
        }

        /// <summary>STATE written now, on the UI thread as the wire writes it: its Stagewatch row's link record and key, kept when verified.</summary>
        private void TakeState()
        {
            using var doc = JsonDocument.Parse(new CommandRouter(_services).StateJson());
            var row = doc.RootElement.GetProperty("bus").GetProperty("stagewatch");
            var rec = row.GetProperty("link");
            var id = rec.GetProperty("peer").GetProperty("id");
            var stated = new Stated(rec.GetProperty("level").GetInt32(), rec.GetProperty("proof").GetString() ?? "",
                rec.GetProperty("state").GetString() ?? "", id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                row.GetProperty("key").GetString() ?? "");
            if (stated.Verified) State.Enqueue(stated);
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
            _stop = true;
            _link.Published -= _said;
            _services.Kernel.StagewatchChanged -= _told;
            _joined = _reader.Join(5000);
        }

        /// <summary>
        /// No publication shows the record verified beside an unset key word; each kind of publication saw the rise of the link to
        /// <paramref name="id"/>; no reading faulted and the reader stopped.
        /// </summary>
        public void AssertWhole(string id)
        {
            if (_fault is { } fault) Assert.Fail("a reading of the rise faulted: " + fault);
            Assert.True(_joined, "the test's reader did not stop");
            Assert.True(Reads > 0, "the test's reader read nothing");
            foreach (var (where, seen) in new[] { ("said", Said.ToArray()), ("told", Told.ToArray()), ("read", Read.ToArray()) })
            {
                var torn = seen.Where(p => Verified(p.Record) && p.Key.Word.Length == 0).ToList();
                if (torn.Count > 0)
                    Assert.Fail(string.Create(CultureInfo.InvariantCulture,
                        $"{torn.Count} of {seen.Length} {where} publications show the link verified with no key word: {torn[0].Record.ToJson()}"));
            }
            var tornState = State.Where(s => s.Verified && s.Key.Length == 0).ToList();
            if (tornState.Count > 0)
                Assert.Fail(string.Create(CultureInfo.InvariantCulture,
                    $"{tornState.Count} of {State.Count} STATE rows show the link verified with no key word: {tornState[0]}"));
            Assert.Contains(Said, p => Verified(p.Record) && p.Record.PeerId == id);
            Assert.Contains(Told, p => Verified(p.Record) && p.Record.PeerId == id);
            Assert.Contains(State, s => s.Verified && s.PeerId == id);
            Assert.Contains(Read, p => Verified(p.Record) && p.Record.PeerId == id);
        }

        public void Dispose() => Stop();
    }
}
