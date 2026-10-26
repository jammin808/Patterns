using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Patterns.App.ViewModels;
using Patterns.Assistant;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 90: the bench host (<c>tools/Patterns.Bench</c>) — the real desk headless off Windows, started as Stagewatch's
/// interop lane starts it: its own process from this build's output, its token in a file, its control on standard input.
/// The facts are read where the lane reads them: the ready line on stdout, the wire paired by proof, STATE. And the
/// approval desk's road the bench proposes on (<see cref="AssistantPage.ProposeToApprovalDesk"/>), in the desk itself.
/// </summary>
public class BenchHostTests
{
    private static readonly TimeSpan ReadyWithin = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(20);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static string SecretFile(string dir, string name, string text)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return path;
    }

    /// <summary>The bench as a child process: its stdout as lines, its stderr kept, its stdin a line at a time.</summary>
    private sealed class Bench : IDisposable
    {
        private readonly Process _process;
        private readonly BlockingCollection<string?> _lines = new();
        private readonly ConcurrentQueue<string> _stderr = new();

        public List<string> Seen { get; } = new();

        /// <summary>The bench this build made, where the build names it (the test project's AssemblyMetadata).</summary>
        private static string BenchDll()
            => typeof(BenchHostTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "PatternsBench").Value!;

        /// <summary>The dotnet host this test run is on (the CLI names it to its children), else the one on the path.</summary>
        private static string DotnetHost()
            => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";

        public Bench(params string[] args)
        {
            var dll = BenchDll();
            Assert.True(File.Exists(dll), $"the bench was not built where this build says it is: {dll}");
            var start = new ProcessStartInfo(DotnetHost())
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add(dll);
            foreach (var a in args) start.ArgumentList.Add(a);
            _process = new Process { StartInfo = start };
            _process.OutputDataReceived += (_, e) => _lines.Add(e.Data);
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) _stderr.Enqueue(e.Data);
            };
            Assert.True(_process.Start());
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public IEnumerable<string> Stderr => _stderr;

        /// <summary>The next stdout line within the deadline, parsed.</summary>
        public JsonElement Next(TimeSpan within)
        {
            Assert.True(_lines.TryTake(out var line, within), $"the bench said nothing within {within.TotalSeconds:0} s");
            Assert.NotNull(line);
            Seen.Add(line!);
            using var doc = JsonDocument.Parse(line!);
            return doc.RootElement.Clone();
        }

        public void Send(string line)
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }

        /// <summary>The bench's process id.</summary>
        public int Id => _process.Id;

        /// <summary>The end of the bench's input.</summary>
        public void EndInput() => _process.StandardInput.Close();

        public int ExitCode(TimeSpan within)
        {
            Assert.True(_process.WaitForExit(within), $"the bench did not end within {within.TotalSeconds:0} s");
            _process.WaitForExit();                                                                // the output's last lines drained
            while (_lines.TryTake(out var line)) if (line is not null) Seen.Add(line);
            return _process.ExitCode;
        }

        public void Dispose()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
            _process.WaitForExit();                                                                // its output read to the end: no line arrives after the queue is gone
            _process.Dispose();
            _lines.Dispose();
        }
    }

    /// <summary>A wire connection to the bench: STATE pushes skipped wherever they come (before pairing too, or never), the newest NONCE kept.</summary>
    private sealed class Wire : IDisposable
    {
        private readonly TcpClient _client = new();
        private readonly StreamReader _reader;
        private readonly NetworkStream _stream;

        public string Nonce { get; private set; } = "";

        public Wire(int port)
        {
            _client.Connect(IPAddress.Loopback, port);
            _stream = _client.GetStream();
            _stream.ReadTimeout = 20_000;
            _reader = new StreamReader(_stream, Encoding.UTF8);
            while (true)
            {
                var line = _reader.ReadLine();
                Assert.NotNull(line);
                if (line!.StartsWith("NONCE ", StringComparison.Ordinal))
                {
                    Nonce = line["NONCE ".Length..];
                    return;
                }
                Assert.StartsWith("STATE {", line);                                                   // the one line a desk may say before its NONCE
            }
        }

        public string Say(string line)
        {
            _stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
            while (true)
            {
                var reply = _reader.ReadLine();
                Assert.NotNull(reply);
                if (reply!.StartsWith("STATE ", StringComparison.Ordinal)) continue;
                if (reply.StartsWith("NONCE ", StringComparison.Ordinal))
                {
                    Nonce = reply["NONCE ".Length..];
                    continue;
                }
                return reply;
            }
        }

        /// <summary>STATE's JSON, asked for (STATUS) rather than waited for.</summary>
        public JsonElement State()
        {
            var reply = Say("STATUS");
            Assert.StartsWith("OK {", reply);
            using var doc = JsonDocument.Parse(reply["OK ".Length..]);
            return doc.RootElement.Clone();
        }

        public void Dispose() => _client.Dispose();
    }

    /// <summary>
    /// The files of a bench's home that hold a secret by design — the lane's named exemptions (ecosystem-lane decision 11):
    /// the settings, their .bak and their timed backups, the recovery record and its .bak. Every other file is swept.
    /// </summary>
    private static bool HoldsSecretsByDesign(string home, string file)
    {
        var relative = Path.GetRelativePath(home, file).Replace('\\', '/');
        return relative is "patterns.settings.json" or "patterns.settings.json.bak" or "patterns.recovery.json" or "patterns.recovery.json.bak"
               || (relative.StartsWith("backups/patterns.settings.", StringComparison.Ordinal) && relative.EndsWith(".json", StringComparison.Ordinal));
    }

    /// <summary>No file of the home outside the exemptions holds any of <paramref name="secrets"/>; the log is among those swept.</summary>
    private static void AssertNoSecretInHome(string home, params string[] secrets)
    {
        var swept = new List<string>();
        foreach (var file in Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories))
        {
            if (HoldsSecretsByDesign(home, file)) continue;
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            var relative = Path.GetRelativePath(home, file);
            foreach (var secret in secrets) Assert.False(text.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{relative} holds a secret");
            swept.Add(relative);
        }
        Assert.Contains("patterns.log", swept);
    }

    /// <summary>The show the bench wrote, as the desk saved it at its close.</summary>
    private static Patterns.Core.Model.ShowState Saved(string home) => new SettingsStore(home).Load();

    private static string[] LookNames(JsonElement state)
        => state.GetProperty("looks").EnumerateArray().Select(l => l.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public void TheBenchBootsTheRealDeskHeadlessAndAnswersEachControlLineWithWhatHappened()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-bench-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(dir, "home");
        Directory.CreateDirectory(dir);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));                      // the lane's tokens: 32 upper-case hex
        var tokenFile = SecretFile(dir, "token", token + "\n");
        var http = FreePort();
        var wirePort = FreePort();
        while (wirePort == http) wirePort = FreePort();
        try
        {
            using var bench = new Bench("--home", home, "--token-file", tokenFile, "--http", http.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--wire", wirePort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--bind", "127.0.0.1",          // this machine's own doors only: a test opens no desk to the network
                "--clips", "synthetic", "--control", "stdin", "--name", "Bench test");

            // The ready line: stdout's first, within the lane's deadline — the doors the listeners hold, the show network id kept in the home, no secret.
            var ready = bench.Next(ReadyWithin);
            Assert.Equal("ready", ready.GetProperty("patterns").GetString());
            Assert.Equal(http, ready.GetProperty("http").GetInt32());
            Assert.Equal(wirePort, ready.GetProperty("wire").GetInt32());
            var node = ready.GetProperty("node").GetString()!;
            Assert.Matches("^[0-9a-f]{8}$", node);
            Assert.Equal(node, File.ReadAllText(Path.Combine(home, ShowBusIdentity.FileName)).Trim());
            Assert.NotEqual("", ready.GetProperty("build").GetString());
            Assert.Equal("synthetic", ready.GetProperty("clips").GetString());
            Assert.Equal(JsonValueKind.Null, ready.GetProperty("stagewatch").ValueKind);                 // no link asked for
            Assert.False(ready.GetProperty("key").GetBoolean());
            Assert.False(ready.GetProperty("trustLoopback").GetBoolean());
            Assert.Equal("127.0.0.1", ready.GetProperty("bind").GetString());

            // The wire: this machine is not trusted, so a mutating verb waits for the proof; the proof pairs; STATE has its bus row.
            using var wire = new Wire(wirePort);
            Assert.Equal("OK", wire.Say("HELLO bench-test"));
            Assert.StartsWith("ERR", wire.Say("BLACKOUT ON"));
            Assert.Equal("OK paired", wire.Say("AUTH-PROOF " + WireProof.Proof(token, wire.Nonce, "bench-test")));
            var before = wire.State();
            Assert.Equal(JsonValueKind.Object, before.GetProperty("bus").ValueKind);
            Assert.False(before.GetProperty("editSafe").GetBoolean());
            Assert.False(before.GetProperty("blackout").GetBoolean());

            // A proposal with no approval desk linked is refused, and nothing here changed: the looks, the preview, the air.
            bench.Send("propose look x");
            var proposed = bench.Next(AnswerWithin);
            Assert.Equal("propose", proposed.GetProperty("control").GetString());
            Assert.False(proposed.GetProperty("ok").GetBoolean());
            Assert.Equal("no Stagewatch approval desk linked", proposed.GetProperty("error").GetString());
            var afterPropose = wire.State();
            Assert.Equal(LookNames(before), LookNames(afterPropose));
            Assert.DoesNotContain("x", LookNames(afterPropose));
            Assert.False(afterPropose.GetProperty("editSafe").GetBoolean());
            Assert.Equal(before.GetProperty("airLook").GetString(), afterPropose.GetProperty("airLook").GetString());
            Assert.Equal(before.GetProperty("pattern").GetString(), afterPropose.GetProperty("pattern").GetString());

            // A synthetic clip on the air: the answer names its input, and STATE's video row reads its length.
            bench.Send("clip a 30");
            var clip = bench.Next(AnswerWithin);
            Assert.Equal("clip", clip.GetProperty("control").GetString());
            Assert.True(clip.GetProperty("ok").GetBoolean(), clip.ToString());
            Assert.Equal(30_000, clip.GetProperty("lengthMs").GetInt64());
            var video = wire.State().GetProperty("video");
            Assert.Equal(30_000, video.GetProperty("lengthMs").GetInt64());
            Assert.Equal(clip.GetProperty("key").GetString(), video.GetProperty("key").GetString());
            Assert.Equal("program", video.GetProperty("role").GetString());

            // A word the bench does not know is refused, never guessed.
            bench.Send("fish");
            var fish = bench.Next(AnswerWithin);
            Assert.Equal("fish", fish.GetProperty("control").GetString());
            Assert.False(fish.GetProperty("ok").GetBoolean());
            Assert.Equal("unknown control (clip, propose look, exit)", fish.GetProperty("error").GetString());

            // exit: answered, then the desk closes and the process ends with 0.
            bench.Send("exit");
            var exit = bench.Next(AnswerWithin);
            Assert.Equal("exit", exit.GetProperty("control").GetString());
            Assert.True(exit.GetProperty("ok").GetBoolean());
            Assert.Equal(0, bench.ExitCode(TimeSpan.FromSeconds(60)));

            // No secret on stdout or stderr: the token rode in no line.
            Assert.DoesNotContain(bench.Seen.Concat(bench.Stderr), l => l.Contains(token, StringComparison.OrdinalIgnoreCase));

            // No door the ready line does not name: no caller node may link, so the twin's port never opened and no twin key was made.
            var saved = Saved(home);
            Assert.False(saved.Twin.AcceptCallers);
            Assert.Equal("", saved.Twin.Key);

            // Nor in the home: the log and every file but the exemptions are free of the token. The bench made the home this user's alone.
            AssertNoSecretInHome(home, token);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(home));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* a log still closing */ }
        }
    }

    [Fact]
    public void TheBenchKeepsEverySecretOffEveryOutputAndStopsAtTheEndOfItsInput()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-bench-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(dir, "home");
        Directory.CreateDirectory(dir);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var key = "swk-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var pasted = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));                    // a secret typed into the wrong window
        var tokenFile = SecretFile(dir, "token", token + "\n");
        var keyFile = SecretFile(dir, "key", key + "\n");
        var ports = new HashSet<int>();
        while (ports.Count < 3) ports.Add(FreePort());
        var (http, wirePort, dead) = (ports.ElementAt(0), ports.ElementAt(1), ports.ElementAt(2));
        string Port(int p) => p.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var bench = new Bench("--home", home, "--token-file", tokenFile, "--http", Port(http), "--wire", Port(wirePort), "--bind", "127.0.0.1",
                "--stagewatch", "127.0.0.1:" + Port(dead), "--stagewatch-key-file", keyFile, "--control", "stdin");    // a Stagewatch that never answers

            // The ready line says a key is presented, never which; no clips were asked for.
            var ready = bench.Next(ReadyWithin);
            Assert.Equal("ready", ready.GetProperty("patterns").GetString());
            Assert.True(ready.GetProperty("key").GetBoolean());
            Assert.Equal("127.0.0.1:" + Port(dead), ready.GetProperty("stagewatch").GetString());
            Assert.Equal("none", ready.GetProperty("clips").GetString());

            // Four lines, then the end of the input: each is answered in its order before the bench stops.
            bench.Send(pasted);
            bench.Send(new string('x', 10_000));
            bench.Send("clip a 3");
            bench.Send("propose look y");
            bench.EndInput();

            // A long unknown word is refused as "?", never repeated.
            var unknown = bench.Next(AnswerWithin);
            Assert.Equal("?", unknown.GetProperty("control").GetString());
            Assert.False(unknown.GetProperty("ok").GetBoolean());
            Assert.Equal("unknown control (clip, propose look, exit)", unknown.GetProperty("error").GetString());

            // A line past the longest is refused whole, and the next line is read as its own.
            var tooLong = bench.Next(AnswerWithin);
            Assert.False(tooLong.GetProperty("ok").GetBoolean());
            Assert.Equal("a control line is at most 4096 characters", tooLong.GetProperty("error").GetString());

            var clip = bench.Next(AnswerWithin);
            Assert.Equal("clip", clip.GetProperty("control").GetString());
            Assert.False(clip.GetProperty("ok").GetBoolean());
            Assert.Equal("this bench opens no clips: start it with --clips synthetic", clip.GetProperty("error").GetString());

            // The link is dialled and nobody answers: no approval desk is live, so the proposal is refused, not applied here.
            var proposed = bench.Next(AnswerWithin);
            Assert.Equal("propose", proposed.GetProperty("control").GetString());
            Assert.False(proposed.GetProperty("ok").GetBoolean());
            Assert.Equal("no Stagewatch approval desk linked", proposed.GetProperty("error").GetString());

            // The end of the input stops the bench as exit does: the desk closes and the process ends with 0, nothing said after.
            Assert.Equal(0, bench.ExitCode(TimeSpan.FromSeconds(60)));
            Assert.Equal(5, bench.Seen.Count);

            // The token, the key and the pasted line rode in no line and sit in no file but the exemptions.
            string[] secrets = { token, key, pasted };
            Assert.DoesNotContain(bench.Seen.Concat(bench.Stderr), l => secrets.Any(x => l.Contains(x, StringComparison.OrdinalIgnoreCase)));
            Assert.Equal(key, Saved(home).Control.Stagewatch.ApiKey);                                  // the key reached the link, in its settings
            AssertNoSecretInHome(home, secrets);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* a log still closing */ }
        }
    }

    [Fact]
    public void TheBenchStopsWithZeroWhenItsTimeIsUpOrItIsToldToTerminate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tokenFile = SecretFile(dir, "token", Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + "\n");
        string[] Doors()
        {
            var ports = new HashSet<int>();
            while (ports.Count < 2) ports.Add(FreePort());
            return new[] { "--http", ports.ElementAt(0).ToString(System.Globalization.CultureInfo.InvariantCulture), "--wire",
                ports.ElementAt(1).ToString(System.Globalization.CultureInfo.InvariantCulture), "--bind", "127.0.0.1" };
        }
        try
        {
            // --exit-after: ready, then the desk closes by itself and the process ends with 0, nothing said after the ready line.
            using (var timed = new Bench(new[] { "--home", Path.Combine(dir, "a"), "--token-file", tokenFile, "--exit-after", "1" }.Concat(Doors()).ToArray()))
            {
                Assert.Equal("ready", timed.Next(ReadyWithin).GetProperty("patterns").GetString());
                Assert.Equal(0, timed.ExitCode(TimeSpan.FromSeconds(60)));
                Assert.Single(timed.Seen);
            }

            // SIGTERM, as the lane stops an app: the desk shuts down as at a close, never mid-step, and the process ends with 0.
            if (!OperatingSystem.IsWindows())
            {
                using var signalled = new Bench(new[] { "--home", Path.Combine(dir, "b"), "--token-file", tokenFile }.Concat(Doors()).ToArray());
                Assert.Equal("ready", signalled.Next(ReadyWithin).GetProperty("patterns").GetString());
                using (var kill = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-TERM", signalled.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false })!)
                {
                    Assert.True(kill.WaitForExit(10_000));
                    Assert.Equal(0, kill.ExitCode);
                }
                Assert.Equal(0, signalled.ExitCode(TimeSpan.FromSeconds(60)));
                Assert.Single(signalled.Seen);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* a log still closing */ }
        }
    }

    [Fact]
    public void TheBenchRefusesWhatItDoesNotKnowWithoutRepeatingItAndRunsNoOpenDesk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var tokenFile = SecretFile(dir, "token", Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + "\n");

            // A token typed on the command line is an option the bench does not know: refused by its place, its words never repeated.
            using (var typed = new Bench("--home", Path.Combine(dir, "a"), "--token-file", tokenFile, "--token", "K7QM-3XWD-P9RA"))
            {
                var refused = typed.Next(ReadyWithin);
                Assert.Equal("refused", refused.GetProperty("patterns").GetString());
                Assert.Contains("argument 5", refused.GetProperty("error").GetString());
                Assert.Equal(2, typed.ExitCode(ReadyWithin));
                Assert.DoesNotContain(typed.Seen.Concat(typed.Stderr), l => l.Contains("K7QM", StringComparison.Ordinal));
            }

            // An empty token file would be a desk open to the network: refused before anything is written.
            var empty = SecretFile(dir, "empty", "\n");
            using (var open = new Bench("--home", Path.Combine(dir, "b"), "--token-file", empty))
            {
                var refused = open.Next(ReadyWithin);
                Assert.Equal("refused", refused.GetProperty("patterns").GetString());
                Assert.Contains("holds no token", refused.GetProperty("error").GetString());
                Assert.Equal(2, open.ExitCode(ReadyWithin));
                Assert.False(File.Exists(Path.Combine(dir, "b", "patterns.settings.json")));
            }

            // A value out of its range is refused with what the option takes.
            using (var port = new Bench("--home", Path.Combine(dir, "c"), "--token-file", tokenFile, "--wire", "80"))
            {
                var refused = port.Next(ReadyWithin);
                Assert.Equal("refused", refused.GetProperty("patterns").GetString());
                Assert.Equal("--wire takes a port from 1024 to 65535", refused.GetProperty("error").GetString());
                Assert.Equal(2, port.ExitCode(ReadyWithin));
            }

            // A desk's own folder is never the bench's home: refused, its show as it was and no marker left in it.
            var desk = Path.Combine(dir, "d");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(desk);
            else Directory.CreateDirectory(desk, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(desk, "patterns.settings.json"), "{\"name\":\"tonight\"}");
            using (var taken = new Bench("--home", desk, "--token-file", tokenFile))
            {
                var refused = taken.Next(ReadyWithin);
                Assert.Equal("refused", refused.GetProperty("patterns").GetString());
                Assert.Equal("the home folder holds files the bench did not write: --home is the bench's own folder, never a desk's", refused.GetProperty("error").GetString());
                Assert.Equal(2, taken.ExitCode(ReadyWithin));
                Assert.Equal("{\"name\":\"tonight\"}", File.ReadAllText(Path.Combine(desk, "patterns.settings.json")));
                Assert.False(File.Exists(Path.Combine(desk, "patterns.bench")));
            }

            // On Unix a home another user can enter would hand them the token: refused before anything is written.
            if (!OperatingSystem.IsWindows())
            {
                var shared = Path.Combine(dir, "e");
                Directory.CreateDirectory(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
                                             | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                using var entered = new Bench("--home", shared, "--token-file", tokenFile);
                var refused = entered.Next(ReadyWithin);
                Assert.Equal("refused", refused.GetProperty("patterns").GetString());
                Assert.StartsWith("the home folder can be entered by other users", refused.GetProperty("error").GetString());
                Assert.Equal(2, entered.ExitCode(ReadyWithin));
                Assert.Empty(Directory.EnumerateFileSystemEntries(shared));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* nothing held */ }
        }
    }

    private static AssistantProposal Look(string title) => new() { Kind = "look", Title = title, Looks = new[] { new LookPart(title, 0, null, null) } };

    [AvaloniaFact]
    public void TheApprovalDesksRoadRefusesWithNoDeskAndChangesNothingWhileApplyStillLandsInThePreview()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var looks = services.State.LooksAndCues.Looks.Select(l => l.Name).ToList();
            var air = JsonUtil.Serialize(services.AirState.Pattern);
            var airLook = services.AirLookId;
            Assert.False(services.Kernel.Stagewatch.ApprovalsLive);

            // The bench's road: one read of the link, no desk, refused — the chip unspent, the looks, the preview and the air as they were.
            var chip = new AssistantChip(vm.Assistant, Look("Bench look"));
            var (localId, words) = vm.Assistant.ProposeToApprovalDesk(chip);
            Assert.Null(localId);
            Assert.Equal("no Stagewatch approval desk linked", words);
            Assert.False(chip.IsApplied);
            Assert.True(chip.CanApply);
            Assert.Equal(looks, services.State.LooksAndCues.Looks.Select(l => l.Name).ToList());
            Assert.False(vm.IsSandboxActive);
            Assert.Equal(air, JsonUtil.Serialize(services.AirState.Pattern));
            Assert.Equal(airLook, services.AirLookId);
            Assert.Empty(services.Kernel.Stagewatch.Approvals.All);

            // A proposal with nothing to apply is refused by its own words.
            var steps = new AssistantChip(vm.Assistant, new AssistantProposal { Kind = "steps", Title = "Read the room", Steps = new[] { "Look up." } });
            Assert.Equal((null, "this proposal has nothing to apply"), vm.Assistant.ProposeToApprovalDesk(steps));

            // The assistant's own APPLY keeps its fallback: with no desk it lands here, in the preview (EDIT SAFE opened), the air untouched.
            vm.Assistant.ApplyProposal(chip);
            Assert.True(chip.IsApplied);
            Assert.Contains("Bench look", services.State.LooksAndCues.Looks.Select(l => l.Name));
            Assert.True(vm.IsSandboxActive);
            Assert.Equal(air, JsonUtil.Serialize(services.AirState.Pattern));
            Assert.Equal(airLook, services.AirLookId);

            // A chip spent here cannot go to a desk afterwards.
            Assert.Equal((null, "this proposal was already applied or sent"), vm.Assistant.ProposeToApprovalDesk(chip));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheBenchsDeskLoadsNoLibVlcWhenTheProcessForbidsIt()
    {
        // Set before the desk is built, as the bench sets it: the view model's first validation asks for the decoder.
        var note = Patterns.Rendering.Media.VideoService.AvailabilityNote;
        Patterns.Rendering.Media.VideoService.AvailabilityNote = "";
        Services.VideoEngine.LibVlcAllowed = false;
        TestApp.Booted? b = null;
        try
        {
            b = TestApp.Boot();
            Assert.False(b.Services.Video.EnsureAvailable());
            Assert.Null(b.Services.Video.SharedVlc);
            Assert.False(b.Services.ValidationContext.VideoDecoderAvailable);
            Assert.Equal(Services.VideoEngine.LibVlcOffNote, Patterns.Rendering.Media.VideoService.AvailabilityNote);   // the process's reason, never an install hint for a folder never tried
        }
        finally
        {
            Services.VideoEngine.LibVlcAllowed = true;
            Patterns.Rendering.Media.VideoService.AvailabilityNote = note;
            b?.Dispose();
        }
    }
}
