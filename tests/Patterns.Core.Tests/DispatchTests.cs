using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The desk's thread as an edge sees it: a post, a check, a timer — with no UI in sight. Without
/// a provider the inline runner answers, which is what a test of an edge wants: a post runs at
/// once, a timer fires when the test says so and never otherwise.
/// </summary>
public class DispatchTests
{
    [Fact]
    public void WithoutAProviderAPostRunsAtOnceAndATimerWaitsToBeFired()
    {
        var was = Dispatch.Provider;
        try
        {
            Dispatch.Provider = null!;                                                       // null means the inline runner, never nothing
            Assert.Same(InlineDispatch.Instance, Dispatch.Provider);
            Assert.True(Dispatch.CheckAccess());
            var ran = 0;
            Dispatch.Post(() => ran++);
            Assert.Equal(1, ran);

            var ticks = 0;
            using var timer = Dispatch.Timer(TimeSpan.FromMilliseconds(200));
            timer.Tick += () => ticks++;
            var manual = Assert.IsType<ManualTimer>(timer);
            Assert.False(manual.Fire());                                                     // not started: nothing fires
            timer.Start();
            Assert.True(timer.IsEnabled);
            Assert.True(manual.Fire());
            Assert.Equal(1, ticks);
            timer.Stop();
            Assert.False(manual.Fire());
            Assert.Equal(1, ticks);
            Assert.Equal(TimeSpan.FromMilliseconds(200), timer.Interval);
        }
        finally
        {
            Dispatch.Provider = was;
        }
    }

    [Fact]
    public void TheInlineRunnerFiresEveryRunningTimerAndForgetsTheCollectedOnes()
    {
        var runner = new InlineDispatch();
        var a = runner.Timer(TimeSpan.FromSeconds(1));
        var b = runner.Timer(TimeSpan.FromSeconds(1));
        var fired = new List<string>();
        a.Tick += () => fired.Add("a");
        b.Tick += () => fired.Add("b");
        a.Start();
        Assert.Equal(1, runner.FireAll());                                                   // b never started
        Assert.Equal(new[] { "a" }, fired);
        b.Start();
        Assert.Equal(2, runner.FireAll());
        a.Dispose();                                                                          // disposed is stopped
        Assert.Equal(1, runner.FireAll());
        Assert.Equal(new[] { "a", "a", "b", "b" }, fired);
    }
}
