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

/// <summary>
/// The start after a crash restart: what the watchdog's note does to the run (the health line,
/// the log, the safe run's software decoding), what the operator's choice does to it, and the
/// Machine page's block for it.
/// </summary>
public class CrashHardeningAppTests
{
    private static readonly DateTime T0 = new(2026, 9, 6, 22, 30, 0, DateTimeKind.Utc);

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void ANativeFaultMarkerMakesASafeRunThatTheMachinePageCanOverride()
    {
        var note = new CrashNote(ExitCodes.AccessViolation, ExitCodes.Describe(ExitCodes.AccessViolation), true, false, T0, 4174, "", 1);
        var b = TestApp.Boot("patterns-crash-", dir => CrashMarker.Write(dir, note));
        try
        {
            var (services, vm, _) = b;
            Assert.Equal(note, services.LastCrash);
            Assert.True(services.SafeRun);
            Assert.False(services.HardwareDecoding);                                   // the decoder is the first suspect
            Assert.False(services.Video.HardwareDecoding());                            // and the pool asks the services, not a constant
            Assert.False(File.Exists(Path.Combine(b.Dir, CrashMarker.FileName)));      // one run only

            var health = HealthMonitor.Summary(DateTime.UtcNow);
            Assert.Contains("ended in an access violation", health);
            Assert.Contains("Video decoding is in software for this run", health);
            Assert.Contains("ended in an access violation", File.ReadAllText(Path.Combine(b.Dir, "patterns.log")));

            Assert.True(vm.LastCrashVisible);
            Assert.Contains("access violation", vm.LastCrashText);
            Assert.StartsWith("Auto, in a safe run:", vm.VideoDecodingText);
            Assert.Contains("next clip", vm.VideoDecodingText);

            // Hardware regardless: the operator's choice wins over the run's fate, and the line says so at once.
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            services.State.Admin.VideoDecoding = VideoDecodingKind.Hardware;
            Assert.True(services.HardwareDecoding);
            Assert.True(services.Video.HardwareDecoding());
            Assert.Contains(nameof(MainViewModel.VideoDecodingText), raised);
            Assert.StartsWith("Hardware:", vm.VideoDecodingText);

            services.State.Admin.VideoDecoding = VideoDecodingKind.Software;
            Assert.False(services.HardwareDecoding);
            Assert.StartsWith("Software:", vm.VideoDecodingText);
        }
        finally
        {
            b.Dispose();
            HealthMonitor.Reset();
        }
    }

    [AvaloniaFact]
    public void AManagedCrashIsNotedButKeepsTheCard()
    {
        var note = new CrashNote(ExitCodes.ClrException, ExitCodes.Describe(ExitCodes.ClrException), false, false, T0, 600, "", 0);
        var b = TestApp.Boot("patterns-crash-", dir => CrashMarker.Write(dir, note));
        try
        {
            Assert.Equal(note, b.Services.LastCrash);
            Assert.False(b.Services.SafeRun);
            Assert.True(b.Services.HardwareDecoding);
            Assert.Contains("unhandled .NET exception", HealthMonitor.Summary(DateTime.UtcNow));
            Assert.DoesNotContain("software", HealthMonitor.Summary(DateTime.UtcNow));
            Assert.True(b.Vm.LastCrashVisible);
            Assert.StartsWith("Auto:", b.Vm.VideoDecodingText);
        }
        finally
        {
            b.Dispose();
            HealthMonitor.Reset();
        }
    }

    [AvaloniaFact]
    public void ACleanStartHasNoCrashNoteAndTheMachinePageShowsTheDecodingBlock()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            Assert.Null(services.LastCrash);
            Assert.False(services.SafeRun);
            Assert.True(services.HardwareDecoding);
            Assert.False(vm.LastCrashVisible);
            Assert.Equal("", vm.LastCrashText);
            Assert.StartsWith("Auto: clips decode on the graphics card.", vm.VideoDecodingText);
            Assert.DoesNotContain("ended in", HealthMonitor.Summary(DateTime.UtcNow));

            window.Width = 1420;
            window.Height = 900;
            vm.SelectPage(Shell.IndexOf("Machine"));
            Settle(window);
            var page = window.GetVisualDescendants().OfType<AdminSection>().First();
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "VIDEO DECODING");
            Assert.Contains(page.GetVisualDescendants().OfType<ComboBox>(), c => ReferenceEquals(c.ItemsSource, Lists.VideoDecodings));
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.VideoDecodingText);
            Assert.DoesNotContain(page.GetVisualDescendants().OfType<TextBlock>(), t => t.IsVisible && t.Text == vm.LastCrashText && t.Text.Length > 0);
            Assert.Equal(3, Lists.VideoDecodings.Length);
        }
        finally
        {
            b.Dispose();
        }
    }
}
