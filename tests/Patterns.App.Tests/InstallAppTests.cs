using System.IO.Compression;
using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A permanent install on a live desk: the programme's look landing by the clock with the
/// schedule as its origin, an advert at its minute and the programme back after it, an
/// announcement's words up and down, the verbs and a cue by hand, idle black outside hours and
/// the programme lifting it, the schedule switched off, STATE's block, the page; the passcode gate
/// on RESTART and UPDATE APPLY, a staged package read and handed to the watchdog; the check-in
/// with a management server whose reply runs a command with the server as its origin.
/// </summary>
public class InstallAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static LookConfig MakeLook(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.NewLookName = name;
        vm.SaveLookCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return LookService.Find(vm.State, name) ?? throw new InvalidOperationException($"look {name} was not saved");
    }

    [AvaloniaFact]
    public void TheClockRunsTheSiteAndAnnouncementsAndAdvertsComeByHandToo()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;
            vm.State.Transition.Enabled = false;
            var daytime = MakeLook(vm, "Daytime", PatternKind.Grid);
            var offer = MakeLook(vm, "Offer", PatternKind.ColorBars);
            var monday = new DateTime(2026, 9, 7);
            DateTime At(int hour, int minute, int second = 0) => monday.AddHours(hour).AddMinutes(minute).AddSeconds(second);

            var cfg = vm.State.Install;
            cfg.Slots.Add(new ScheduleSlotConfig { Name = "Daytime", Kind = SlotKind.Programme, Start = "09:00", End = "17:00", Look = "Daytime" });
            cfg.Slots.Add(new ScheduleSlotConfig { Name = "Lunch offer", Kind = SlotKind.Advert, Start = "12:30", End = "12:31", DurationSeconds = 30, Look = "Offer" });
            cfg.Slots.Add(new ScheduleSlotConfig { Name = "Closing", Kind = SlotKind.Announcement, Start = "16:45", End = "16:46", DurationSeconds = 20, Text = "Closing in 15 minutes" });
            var install = services.Install;

            // Off: the clock does nothing; the page says so.
            install.Tick(At(10, 0));
            Assert.Equal("", services.AirLookId);
            Assert.StartsWith("Schedule off", install.Status);

            // On: the programme's look lands, journaled from the schedule; once.
            cfg.Enabled = true;
            install.Tick(At(10, 0));
            Assert.Equal(daytime.Id, services.AirLookId);
            Assert.Contains(services.Journal.Tail(3), e => e.Kind == "ApplyLook" && e.Origin == "schedule" && e.Target == "Daytime");
            Assert.Contains("programme 'Daytime' until 17:00", install.Status);
            Assert.Contains("next: advert Lunch offer at 12:30", install.Status);
            var journalRows = services.Journal.Tail(50).Count;
            install.Tick(At(10, 1));
            Assert.Equal(journalRows, services.Journal.Tail(50).Count);

            // The advert at its minute, the programme back after its seconds.
            install.Tick(At(12, 30));
            Assert.Equal(offer.Id, services.AirLookId);
            Assert.Contains("advert 'Lunch offer' until 12:30:30", install.Status);
            install.Tick(At(12, 30, 31));
            Assert.Equal(daytime.Id, services.AirLookId);
            Assert.Contains("ended", cfg.Slots[1].Status.ToLowerInvariant().Contains("ran its time") ? "ended" : cfg.Slots[1].Status);

            // The announcement: its words on the message overlay for its seconds, the picture untouched.
            install.Tick(At(16, 45));
            Assert.True(services.AirState.Overlays.Message.Enabled);
            Assert.Equal("Closing in 15 minutes", services.AirState.Overlays.Message.Text);
            Assert.Equal(daytime.Id, services.AirLookId);
            install.Tick(At(16, 45, 21));
            Assert.False(services.AirState.Overlays.Message.Enabled);

            // Outside every programme: black; the next morning the programme lifts it.
            install.Tick(At(17, 0));
            Assert.True(services.AirState.Blackout);
            Assert.Contains("idle — black", install.Status);
            install.Tick(At(9, 0).AddDays(1));
            Assert.False(services.AirState.Blackout);
            Assert.Equal(daytime.Id, services.AirLookId);

            // By hand, from the wire: a named announcement, free words, an advert, and OFF for each — the kinds kept apart.
            install.Clock = () => At(11, 0).AddDays(1);
            Assert.Equal("OK", Send(router, "ANNOUNCE Closing"));
            Assert.Equal("Closing in 15 minutes", services.AirState.Overlays.Message.Text);
            Assert.StartsWith("ERR", Send(router, "ADVERT OFF"));                              // an announcement is on, not an advert
            Assert.Equal("OK", Send(router, "ANNOUNCE OFF"));
            Assert.False(services.AirState.Overlays.Message.Enabled);
            Assert.Equal("OK", Send(router, "ANNOUNCE The car with the lights on"));
            Assert.Equal("The car with the lights on", services.AirState.Overlays.Message.Text);
            Assert.True(install.Runtime.Override!.IsAdHoc);
            Assert.Equal("OK", Send(router, "ANNOUNCE OFF"));
            Assert.Equal("OK", Send(router, "ADVERT Lunch offer"));
            Assert.Equal(offer.Id, services.AirLookId);
            Assert.Equal("OK", Send(router, "ADVERT OFF"));
            Assert.Equal(daytime.Id, services.AirLookId);
            Assert.StartsWith("ERR", Send(router, "ADVERT Nobody"));
            Assert.StartsWith("ERR", Send(router, "ADVERT Closing"));                          // an announcement, not an advert
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == "AdvertPlay" && e.Origin.StartsWith("tcp"));

            // A cue's Announcement on, through the executor.
            var cue = new CueActionConfig { Kind = CueActionKind.Announce, Target = "", Value = "Please take your seats" };
            Assert.True(services.Actions.Execute(ShowActions.ToShowAction(cue), new ActionOrigin(OriginKind.Cue, "01.010")).Ok);
            Assert.Equal("Please take your seats", services.AirState.Overlays.Message.Text);
            Assert.True(services.Actions.Execute(ShowActionKind.AnnounceOff, ActionOrigin.Desk).Ok);

            // The schedule off from the wire: the picture stays, the next firing never comes, by hand still works.
            Assert.Equal("OK", Send(router, "SCHEDULE OFF"));
            Assert.False(cfg.Enabled);
            install.Clock = null;
            install.Tick(At(12, 30).AddDays(1));
            Assert.Equal(daytime.Id, services.AirLookId);
            Assert.Equal("OK", Send(router, "SCHEDULE ON"));
            Assert.True(cfg.Enabled);

            // STATE carries the block.
            var state = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("install");
            Assert.True(state.GetProperty("on").GetBoolean());
            Assert.Equal(3, state.GetProperty("slots").GetArrayLength());
            Assert.Equal("advert", state.GetProperty("slots")[1].GetProperty("kind").GetString());
            Assert.Equal(0, state.GetProperty("problems").GetInt32());
            Assert.False(state.GetProperty("update").GetProperty("ok").GetBoolean());

            // The page: the blocks, a row card, the day's timeline, the add buttons.
            vm.SelectPage(Shell.IndexOf("Install"));
            vm.PollNow();
            Settle(window);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "PROGRAMMES, ADVERTS AND ANNOUNCEMENTS");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "+ ANNOUNCEMENT");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), x => x.Text == "Closing in 15 minutes");
            Assert.Equal(3, vm.InstallTimeline.Count);
            Assert.Equal("09:00–17:00", vm.InstallTimeline[0].TimeText);
            Assert.Equal("", vm.InstallProblems);
            vm.AddAdvertCommand.Execute(null);
            Assert.Equal(4, cfg.Slots.Count);
            Assert.Equal(SlotKind.Advert, cfg.Slots[3].Kind);
            vm.RemoveSlotCommand.Execute(cfg.Slots[3]);
            Assert.Equal(3, cfg.Slots.Count);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePasscodeGuardsRestartAndTheStagedUpdateGoesToTheWatchdog()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            var exits = new List<int>();
            services.ExitRequest = code =>
            {
                exits.Add(code);
                return true;
            };

            // No passcode: the gate refuses everything and says why.
            Assert.StartsWith("ERR", Send(router, "RESTART anything"));
            Assert.Contains("no admin passcode", Send(router, "RESTART anything"));
            Assert.StartsWith("ERR", Send(router, "UPDATE APPLY anything"));
            vm.State.Install.AdminPasscode = "open-sesame";
            Assert.Contains("wrong passcode", Send(router, "RESTART nope"));

            // The right passcode, no watchdog: refused with the reason; nothing staged: refused too.
            services.Updates.SupervisedOverride = () => false;
            Assert.Contains("needs the watchdog", Send(router, "RESTART open-sesame"));
            Assert.Empty(exits);
            services.Updates.SupervisedOverride = () => true;
            Assert.Contains("Nothing to apply", Send(router, "UPDATE APPLY open-sesame"));

            // A package staged: read, shown, then handed to the watchdog with the app leaving through the update exit code.
            var updates = services.Updates.Folder;
            Directory.CreateDirectory(updates);
            var package = Path.Combine(updates, "patterns-update-9.9.9.zip");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("Patterns.exe").Open())) w.Write("exe");
                using (var m = new StreamWriter(zip.CreateEntry(UpdatePackage.ManifestName).Open())) m.Write("{ \"version\": \"9.9.9\" }");
            }
            services.Updates.Scan();
            Assert.True(services.Updates.Staged!.Ok);
            Assert.Contains("Staged: 9.9.9", services.Updates.Status);
            var answer = Send(router, "UPDATE APPLY open-sesame");
            Assert.StartsWith("OK", answer);
            Assert.Equal(new[] { SupervisorPolicy.UpdateRequestExitCode }, exits);
            var request = UpdateApply.ReadRequest(updates);
            Assert.Equal(package, request!.Package);
            Assert.Equal("9.9.9", request.Version);
            Assert.Contains(services.Journal.Tail(5), e => e.Kind == "UpdateApply" && e.Outcome == "Requested");

            // A restart with the passcode leaves through the restart code.
            Assert.StartsWith("OK", Send(router, "RESTART open-sesame"));
            Assert.Equal(SupervisorPolicy.RestartRequestExitCode, exits[^1]);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheSiteChecksInAndTheServersReplyRunsWithTheServerAsItsOrigin()
    {
        var b = TestApp.Boot();
        HttpListener? listener = null;
        try
        {
            var (services, vm, _) = b;
            var port = FreePort();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/checkin/");
            listener.Start();
            string received = "";
            string tokenHeader = "";
            var serve = Task.Run(async () =>
            {
                var ctx = await listener.GetContextAsync();
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                received = await reader.ReadToEndAsync();
                tokenHeader = ctx.Request.Headers["X-Patterns-Token"] ?? "";
                var reply = Encoding.UTF8.GetBytes("{\"token\":\"s3cret\",\"commands\":[\"BLACKOUT ON\",\"ANNOUNCE Sale on now\"],\"note\":\"seen\"}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = reply.Length;
                await ctx.Response.OutputStream.WriteAsync(reply);
                ctx.Response.Close();
            });

            vm.IsSandboxActive = false;
            vm.State.Install.SiteName = "Lobby";
            vm.State.Install.ManagementUrl = $"http://127.0.0.1:{port}/checkin/";
            vm.State.Install.ManagementToken = "s3cret";
            var done = services.Management.CheckInAsync();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!done.IsCompleted && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            Assert.True(done.IsCompleted, "the check-in did not finish");
            Assert.True(serve.Wait(2000));
            Assert.Contains("\"site\":\"Lobby\"", received);
            Assert.Contains("\"state\":{", received);
            Assert.Equal("s3cret", tokenHeader);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Blackout);
            Assert.Equal("Sale on now", services.AirState.Overlays.Message.Text);
            Assert.Contains(services.Journal.Tail(5), e => e.Kind == "BlackoutOn" && e.Origin == "management Lobby");
            Assert.Contains("2 commands", services.Management.Status);
            Assert.Contains("seen", services.Management.Status);
            Assert.Equal(1, services.Management.CheckIns);

            // A URL across the internet must be https.
            vm.State.Install.ManagementUrl = "http://signage.example.com/checkin";
            services.Management.Tick(DateTime.UtcNow);
            Assert.Contains("https", services.Management.Status);
        }
        finally
        {
            try { listener?.Stop(); } catch { /* already down */ }
            b.Dispose();
        }
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
