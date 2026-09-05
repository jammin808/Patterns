using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.LowerThirds;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The redesigned phone remote: one page with its tabs, its controls and the commands they send, served live and shaped as the phone needs it.</summary>
public class RemotePageTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static T Pump<T>(Task<T> task, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        return task.GetAwaiter().GetResult();
    }

    [AvaloniaFact]
    public void ThePageHasItsTabsControlsAndCommands()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.LowerThirds.Designs.Add(LowerThirdPresets.Create("Clean"));
            vm.State.LowerThirds.Entries.Add(new LowerThirdEntry { Name = "Jane Doe", Role = "CEO" });
            Dispatcher.UIThread.RunJobs();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{vm.State.Control.HttpPort}/") };

            var page = Pump(http.GetStringAsync("/"));
            Assert.Contains("<title>Patterns Remote</title>", page);
            Assert.Contains("PATTERNS", page);

            // The menu, one section per tab, the tab remembered.
            foreach (var tab in new[] { "show", "cues", "looks", "screens", "audio", "lower", "setup" })
            {
                Assert.Contains($"data-tab=\"{tab}\"", page);
                Assert.Contains($"id=\"tab-{tab}\"", page);
            }
            Assert.Equal(7, Regex.Matches(page, "<section ").Count);
            Assert.Equal(7, Regex.Matches(page, "</section>").Count);
            Assert.Contains("localStorage.setItem('patterns.tab'", page);
            Assert.Contains("width=device-width", page);
            Assert.DoesNotContain("<script src=", page); // nothing to fetch from anywhere else

            // Every control is the TCP line it always was, and the cue verbs carry the client header.
            foreach (var line in new[] { "'PREV'", "'NEXT'", "'OUTPUTS ON'", "'OUTPUTS OFF'", "'IDENTIFY'", "'BLACKOUT TOGGLE'", "'DUCK TOGGLE'", "'STOPALL'",
                                         "'CUE GO '", "'CUE STANDBY PREV'", "'CUE STANDBY NEXT'", "'CUE HOLD '", "'CUE ARM '", "'LOOK '", "'SCREEN '", "'LOCK '", "'SECTION '",
                                         "'AUDIO PLAY'", "'AUDIO STOP'", "'MUSIC PLAY'", "'MUSIC PAUSE'", "'MUSIC NEXT'", "'STINGER '", "'STINGER STOP'", "'TONE ON'", "'TONE OFF'",
                                         "'LT '", "'LT OFF'", "'PERSON '" })
            {
                Assert.Contains(line, page);
            }
            Assert.Contains("X-Patterns-Client", page);
            Assert.Contains("/api/cmd", page);
            Assert.Contains("/api/state?since=", page);
            Assert.Contains("href=\"/run\"", page);
            Assert.Contains("href=\"/multiview\"", page);
            Assert.Contains("PRESS AGAIN", page); // STOP ALL asks twice

            // The state it renders carries what the tabs read.
            var state = Pump(http.GetStringAsync("/api/state"));
            foreach (var key in new[] { "\"airLabel\"", "\"cuestack\"", "\"looks\"", "\"screens\"", "\"stingers\"", "\"lowerThirds\"", "\"people\"", "\"lowerThirdPerson\"", "\"music\"", "\"health\"", "\"machine\"", "\"beacon\"", "\"stream\"" })
            {
                Assert.Contains(key, state);
            }
            Assert.Contains("\"name\":\"Jane Doe\",\"role\":\"CEO\"", state);

            // The caller's page and the multiview still stand beside it.
            Assert.Contains("CUE GO", Pump(http.GetStringAsync("/run")));
            Assert.Contains("/mv.jpg", Pump(http.GetStringAsync("/multiview")));
        }
        finally
        {
            b.Dispose();
        }
    }
}
