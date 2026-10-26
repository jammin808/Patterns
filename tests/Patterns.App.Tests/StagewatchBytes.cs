using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Patterns.App.Tests;

/// <summary>
/// Round 91: what a Stagewatch 2.18 says on its /ws before any frame, byte for byte as it writes it — its
/// <c>NodeLinks.Verdict</c>, <c>Refusal</c> and <c>ViewRefusal</c> (Stagewatch <c>src/Timer.Core/NodeLinks.cs</c>): one
/// <see cref="Utf8JsonWriter"/> with the default encoder, so a dash or an apostrophe goes out escaped (<c>—</c>,
/// <c>'</c>), the fields in its order, <c>words</c> only with verified none, <c>retry</c> only when above zero.
/// </summary>
internal static class StagewatchBytes
{
    /// <summary><c>{"type":"auth","ok":true,"scope",…,"node":{"t","id","verified"}[,"words"]}</c>.</summary>
    public static string Verdict(string scope, string kind, string id, string verified, string? words = null) => Json(w =>
    {
        w.WriteString("type", "auth");
        w.WriteBoolean("ok", true);
        w.WriteString("scope", scope);
        w.WriteStartObject("node");
        w.WriteString("t", kind);
        w.WriteString("id", id);
        w.WriteString("verified", verified);
        w.WriteEndObject();
        if (verified == "none" && words is not null) w.WriteString("words", words);
    });

    /// <summary><c>{"type":"auth","ok":false,"error",…[,"retry":s]}</c>: the socket stays, a viewer of the public frame.</summary>
    public static string Refusal(string error, int retrySeconds) => Json(w =>
    {
        w.WriteString("type", "auth");
        w.WriteBoolean("ok", false);
        w.WriteString("error", error);
        if (retrySeconds > 0) w.WriteNumber("retry", retrySeconds);
    });

    /// <summary><c>{"type":"auth","ok":false,"view":true,"error",…[,"retry":s,"refused":…]}</c>: then the socket closes.</summary>
    public static string ViewRefusal(string viewError, string? refused = null, int retrySeconds = 0) => Json(w =>
    {
        w.WriteString("type", "auth");
        w.WriteBoolean("ok", false);
        w.WriteBoolean("view", true);
        w.WriteString("error", viewError);
        if (refused is null) return;
        if (retrySeconds > 0) w.WriteNumber("retry", retrySeconds);
        w.WriteString("refused", refused);
    });

    /// <summary>Stagewatch's words for a node link with no key (<c>NodeLinks.NoKey</c>).</summary>
    public const string NoKey = "no key — the id is only claimed";

    /// <summary>A key bound to another node, in Stagewatch's words (<c>NodeBindings</c>' refusal).</summary>
    public static string SpeaksFor(string boundId, string id) => $"this key speaks for patterns {boundId} — make a key for patterns {id}";

    /// <summary>
    /// What a Stagewatch 2.18 makes of a dial, as its admission does: a node link (<c>node=</c> in the query) hears a verdict
    /// on its key — taken, or the words of a claim with none; a socket that names no node hears nothing new. Null: no
    /// first message, the first thing it hears is a frame.
    /// </summary>
    public static string? FirstFor(string query, string authorization)
    {
        var node = Query(query, "node");
        if (node is null) return null;
        var kind = Query(query, "t") ?? "";
        return authorization.StartsWith("Bearer swk_", StringComparison.Ordinal)
            ? Verdict("node", kind, node, "key")
            : Verdict("none", kind, node, "none", NoKey);
    }

    /// <summary>One value of a query string, or null.</summary>
    public static string? Query(string query, string name)
    {
        var q = query.StartsWith('?') ? query[1..] : query.Contains('?') ? query[(query.IndexOf('?') + 1)..] : query;
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == name) return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    private static string Json(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(160);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
