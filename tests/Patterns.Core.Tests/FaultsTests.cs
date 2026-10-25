using System.Net.Sockets;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 83 (L31): the line between a socket's end and a fault, the words a reply may carry, and the throttle
/// that writes the first fault with its stack and then one a minute with the count.
/// </summary>
public class FaultsTests
{
    [Fact]
    public void ASocketsEndIsRoutineAndAnythingElseIsAFault()
    {
        Assert.True(Faults.IsIoEnd(new IOException("read failed")));
        Assert.True(Faults.IsIoEnd(new EndOfStreamException()));
        Assert.True(Faults.IsIoEnd(new SocketException(10054)));
        Assert.True(Faults.IsIoEnd(new ObjectDisposedException("stream")));
        Assert.True(Faults.IsIoEnd(new OperationCanceledException()));
        Assert.True(Faults.IsIoEnd(new TaskCanceledException()));
        Assert.True(Faults.IsIoEnd(new AggregateException(new IOException(), new SocketException(10053))));

        Assert.False(Faults.IsIoEnd(new InvalidOperationException("a bug")));
        Assert.False(Faults.IsIoEnd(new NullReferenceException()));
        Assert.False(Faults.IsIoEnd(new OverflowException()));
        Assert.False(Faults.IsIoEnd(new FormatException()));
        Assert.False(Faults.IsIoEnd(new AggregateException(new IOException(), new InvalidOperationException())));
        Assert.False(Faults.IsIoEnd(new AggregateException()));
    }

    [Fact]
    public void ABriefIsTheTypeAndTheFirstLineCappedNeverTheStack()
    {
        Assert.Equal("InvalidOperationException: the first line", Faults.Brief(new InvalidOperationException("the first line\r\nthe second\nthe third")));
        Assert.Equal("FormatException", Faults.Brief(new FormatException("   ")));
        var brief = Faults.Brief(new InvalidOperationException(new string('x', 500)), 40);
        Assert.Equal("InvalidOperationException: " + new string('x', 40) + "…", brief);
        Assert.DoesNotContain("   at ", Faults.Brief(new InvalidOperationException("no stack")));
    }

    [Fact]
    public void TheThrottleWritesTheFirstFaultThenOneAMinuteAndCountsThemAll()
    {
        var t = new FaultThrottle(TimeSpan.FromMinutes(1));
        var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(t.Note(now));
        Assert.False(t.Note(now.AddSeconds(1)));
        Assert.False(t.Note(now.AddSeconds(59)));
        Assert.Equal(3, t.Count);
        Assert.True(t.Note(now.AddSeconds(60)));
        Assert.False(t.Note(now.AddSeconds(61)));
        Assert.Equal(5, t.Count);
    }

    /// <summary>
    /// A key is said once; at most the bound are said; the first new key past the bound answers Full once — the line that
    /// says no more are named — and every key after it, new or said, is quiet. The set never grows past the bound.
    /// </summary>
    [Fact]
    public void SaidOnceSaysEachKeyOnceUpToTheBoundThenSaysOnceThatItNamesNoMore()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaidOnce(0));
        var said = new SaidOnce(3);
        Assert.Equal(3, said.Most);
        Assert.Equal(SaidOnceAnswer.Say, said.Note("a"));
        Assert.Equal(SaidOnceAnswer.Quiet, said.Note("a"));
        Assert.Equal(SaidOnceAnswer.Say, said.Note("b"));
        Assert.Equal(SaidOnceAnswer.Say, said.Note("c"));
        Assert.Equal(SaidOnceAnswer.Full, said.Note("d"));
        Assert.Equal(SaidOnceAnswer.Quiet, said.Note("e"));
        Assert.Equal(SaidOnceAnswer.Quiet, said.Note("d"));
        Assert.Equal(SaidOnceAnswer.Quiet, said.Note("a"));

        var flood = new SaidOnce(64);
        var answers = Enumerable.Range(0, 1000).Select(i => flood.Note(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"10.0.{i / 256}.{i % 256}"))).ToList();
        Assert.Equal(64, answers.Count(a => a == SaidOnceAnswer.Say));
        Assert.Equal(1, answers.Count(a => a == SaidOnceAnswer.Full));
        Assert.Equal(SaidOnceAnswer.Full, answers[64]);
    }
}
