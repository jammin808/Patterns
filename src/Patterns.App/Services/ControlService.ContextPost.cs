using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Show Bus 1.3 §6, Posting, on this desk's wire: <c>CONTEXT POST &lt;json&gt;</c> from a link LINK verified with a key of scope
/// <c>post</c> or above, for the node its key speaks for. The gate (<see cref="ContextPost.Check"/>) stands ahead of the router and of
/// the pairing door; the line is parsed and kept on the connection's reader task; the table is published by one reference write and
/// read by the composition every reader takes (<see cref="ShowBusFeed.Posted"/>). A posted context reaches nothing on the air, no take
/// and no setting. One journal row on an id's first keep, one log line per change of what the table holds (a first keep, a context no
/// longer kept), one summary of the refusals a minute; never the context's text, a key, a kid's secret or an address.
/// </summary>
public sealed partial class ControlService
{
    private readonly PostedContexts _posted = new();
    private readonly ContextPostRefusals _postRefusals = new();

    /// <summary>The posted table's clock: monotonic milliseconds. Settable for the tests, which step it past context.kept.ms.</summary>
    public Func<long> PostedNowMs { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// The contexts other apps posted and this desk keeps now; a context past context.kept.ms is dropped by this read (said once in
    /// the log) and a refusal summary whose minute has ended is said here. Any thread.
    /// </summary>
    public IReadOnlyList<PostedContext> Posted
    {
        get
        {
            var now = PostedNowMs();
            var (live, dropped) = _posted.Take(now);
            foreach (var p in dropped) Log.Info("Wire: " + ContextPost.DroppedWords(p) + ".");
            if (_postRefusals.Due(now) is { } summary) Log.Info("Wire: " + summary + ".");
            if (dropped.Count > 0) UiThread.Post(ArmPush);
            return live;
        }
    }

    /// <summary>The bytes the kept contexts were posted as (bounded by context.bytes × peers.max).</summary>
    public long PostedBytes => _posted.BytesHeld(PostedNowMs());

    /// <summary>The refusals counted in the minute now open, by reason (STATE, the Super Check).</summary>
    public IReadOnlyDictionary<string, int> PostRefusals => _postRefusals.Open();

    /// <summary>
    /// One <c>CONTEXT POST</c> line, answered by the port on the connection's reader task: refused in the gate's words — a node of this
    /// desk's (not the desk) as it refuses any word it does not know — or kept, the latest for its id. No refusal is counted against a
    /// key or a connection; each is summed into the minute's summary by its reason.
    /// </summary>
    private void ContextPostLine(WirePeer peer, string rid, string json)
    {
        void Reply(string reply) => peer.Say(WireReceipt.Tag(reply, rid));
        var now = PostedNowMs();
        if (!_kernel.IsDesk)
        {
            Reply(ControlProtocol.Err($"unknown command '{ContextPost.Verb}'"));
            return;
        }
        var token = _kernel.State.Control.Token;
        var link = peer.Link;
        var scopeNow = link is { Verified: "key" or "link" } && KeyOf(token, link) is not null ? ScopeNow(token, link) : null;
        var decision = ContextPost.Check(Hears(token, peer), link, scopeNow, json, scope => LinkAcceptor.ScopeWords(scope, _kernel.Beacon.MachineName));
        if (decision.Context is not { } context || link is null)
        {
            Refused(decision.Reason, decision.Words);
            return;
        }
        var (outcome, kept, moved, dropped) = _posted.Keep(context.Node.Kind, context.Node.Id, context, decision.Bytes, link.Lid, now);
        // A context this write found past context.kept.ms is let go here, and said here: the reader that drops one is the one that says so.
        foreach (var p in dropped) Log.Info("Wire: " + ContextPost.DroppedWords(p) + ".");
        if (outcome == PostOutcome.Full)
        {
            if (moved) UiThread.Post(ArmPush);
            Refused(ContextPost.Reasons.Full, ContextPost.FullWords);
            return;
        }
        Reply(ControlProtocol.Ok(ContextPost.KeptWords));
        if (outcome == PostOutcome.First)
        {
            // An id the table did not hold — never, or no longer after context.kept.ms — is a first keep: one row, one log line.
            var words = ContextPost.FirstKeepWords(kept!);
            Log.Info("Wire: " + words + ".");
            _kernel.Journal.Record("Remote", "ContextPost", kept!.T + ":" + kept.Id, "Info", words);
        }
        // STATE and the deck hear it only when what the table says moved: a context posted again unchanged costs the desk's thread nothing.
        if (moved) UiThread.Post(ArmPush);

        void Refused(string reason, string words)
        {
            Reply(ControlProtocol.Err(words));
            if (_postRefusals.Refused(reason, now) is { } summary) Log.Info("Wire: " + summary + ".");
        }
    }
}
