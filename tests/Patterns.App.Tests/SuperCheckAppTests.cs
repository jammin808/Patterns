using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The super-check from the button: the facts gathered off a headless rig, the rows on the page, the file beside the exe.</summary>
public class SuperCheckAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void OnePressGathersTheFactsLightsTheRowsAndWritesTheReport()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.State.Ndi.Senders.Add(new NdiSenderConfig { Name = "Feed", Enabled = true, Width = 1280, Height = 720 });
            for (var i = 0; i < 70; i++)
            {
                b.Services.Metrics.Ingest(new MetricSample
                {
                    Utc = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc).AddSeconds(i),
                    CpuAppPct = 9,
                    CpuSystemPct = 22,
                    RamAppMB = 640,
                    RamSystemPct = 48,
                    RamTotalMB = 16384,
                    OutputFps = 60,
                    OutputWindows = 1,
                    PreviewFps = 60,
                    DiskFreeGB = 90,
                    Threads = 40,
                    Handles = 900,
                });
            }

            Assert.False(vm.HasSuperCheck);
            vm.RunSuperCheckCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.HasSuperCheck);
            Assert.True(vm.SuperCheckRows.Count > 10);
            Assert.Contains(vm.SuperCheckRows, r => r.Section == "MACHINE" && r.Item == "Computer" && r.Value == Environment.MachineName);
            Assert.Contains(vm.SuperCheckRows, r => r.Item == "Memory" && r.Value.Contains("16.0 GB"));
            Assert.Contains(vm.SuperCheckRows, r => r.Section == "NDI" && r.Item == "Sends");
            Assert.Contains(vm.SuperCheckRows, r => r.Section == "LEVEL");
            Assert.Contains(vm.SuperCheckRows, r => r.Item == "Watchdog");
            Assert.NotEqual("", vm.SuperCheckHeadline);
            Assert.StartsWith("Level:", vm.SuperCheckLevelText);
            Assert.Contains("PATTERNS SUPER-CHECK", vm.SuperCheckText);
            Assert.Contains("Super-check:", vm.StatusMessage);

            var report = b.Services.Metrics.LastReport!;
            Assert.Equal(report.Headline, vm.SuperCheckHeadline);
            var path = b.Services.Metrics.LastReportPath;
            Assert.True(File.Exists(path), $"report at {path}");
            Assert.Equal(Path.Combine(b.Dir, SuperCheck.FileName), path);
            Assert.Contains(report.Headline, File.ReadAllText(path));
            Assert.StartsWith("Saved:", vm.SuperCheckSavedText);

            // The Machine page renders the report; the Admin pages take the room and the others give it back.
            var window = b.Window;
            window.Width = 1420;
            window.Height = 900;
            vm.SelectPage(Shell.IndexOf("Machine"));
            Settle(window);
            Assert.True(vm.PageWantsRoom);
            Assert.True(window.IsWideApplied);
            var page = window.GetVisualDescendants().OfType<AdminSection>().First();
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == report.Headline);
            Assert.Contains(page.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "RUN SUPER-CHECK");
            vm.SelectPage(Shell.IndexOf("Pattern"));
            Settle(window);
            Assert.False(vm.PageWantsRoom);
            Assert.False(window.IsWideApplied);
        }
        finally
        {
            b.Dispose();
        }
    }
}
