using System.Globalization;

namespace Patterns.Core.Tests;

/// <summary>
/// The bytes of a Stagewatch's speaker message and its acknowledgement, copied from the contract Stagewatch pins for a show
/// app that sends its desk's message through it: its <c>tests/e2e/message.e2e.cjs</c>, read at Stagewatch <c>98f6463</c> (the
/// file's own commit <c>b256a9f</c>, SHA-256 <c>f134dd8d33c44ee53f6029ec76720971c4c3c59001c88dff00745e358b7f19bb</c>), whose
/// header calls them "the bytes Patterns' fake Stagewatch is to copy". The Core tests read the receipts through this desk's
/// reader; the desk's tests answer its link with them (this file is linked into that suite). What the file says and no case of
/// it can show is kept beside the bytes: a receipt's <c>effect</c> is free words a peer neither shows nor asserts; an
/// <c>/ack</c> message answered applied with no <c>refs.messageId</c> has no acknowledgement to follow; a message signal
/// completes on its first answer, so <c>messageAck</c> goes <c>sent</c> → <c>done</c> and is never <c>seen</c>; and the three
/// frame fields go back to 0, "", "" 30 s after the answer or the cancel, the message maybe still on stage.
/// </summary>
internal static class StagewatchMessageBytes
{
    /// <summary>What Stagewatch says of an ACK that would change nothing: a 409 receipt while the signal is listed, a 403 at the door once it has left the frame.</summary>
    public const string Gone = "That cue has already been answered or cleared.";

    /// <summary>What Stagewatch's door says to a show app's forwarded ACK while crew links are required (403).</summary>
    public const string CrewLinks = "This screen can't confirm cues for speaker. Open its crew link (Showcaller page → Crew links).";

    /// <summary>The message route as the contract forms it: one percent-encoded segment, then urgency, flash and ack.</summary>
    public static string MessageRoute(string text, string flash = "slow", bool ack = true)
        => "/api/message/" + Uri.EscapeDataString(text) + "/info/" + flash + (ack ? "/ack" : "");

    /// <summary>The ACK a show app forwards with its desk's name.</summary>
    public static string AckRoute(long id, string desk) => "/api/ack/" + id.ToString(CultureInfo.InvariantCulture) + "/seen/" + Uri.EscapeDataString(desk);

    public const string ClearRoute = "/api/clearmessage";

    /// <summary>200: applied, the far end's effect, no error, the signal in refs.</summary>
    public static string Applied(string id, long messageId, string effect = "Message: Wrap up")
        => "{\"type\":\"receipt\",\"id\":\"" + id + "\",\"ok\":true,\"stage\":\"applied\",\"effect\":\"" + effect + "\",\"error\":null,\"refs\":{\"messageId\":" + messageId.ToString(CultureInfo.InvariantCulture) + "}}";

    /// <summary>200: applied with no signal named — a message without ack, a clear, an ACK taken, or a Stagewatch with no message signals.</summary>
    public static string AppliedNoRefs(string id, string effect = "")
        => "{\"type\":\"receipt\",\"id\":\"" + id + "\",\"ok\":true,\"stage\":\"applied\",\"effect\":\"" + effect + "\",\"error\":null,\"refs\":{}}";

    /// <summary>200: a slow show's receipt — only accepted, no refs.</summary>
    public static string Accepted(string id)
        => "{\"type\":\"receipt\",\"id\":\"" + id + "\",\"ok\":true,\"stage\":\"applied\",\"effect\":\"\",\"error\":null,\"refs\":{}}".Replace("\"stage\":\"applied\"", "\"stage\":\"accepted\"", StringComparison.Ordinal);

    /// <summary>409: the show refused it — a receipt with <c>ok:false</c> and the words.</summary>
    public static string Refused(string id, string error = Gone)
        => "{\"type\":\"receipt\",\"id\":\"" + id + "\",\"ok\":false,\"stage\":\"applied\",\"effect\":\"\",\"error\":\"" + error + "\",\"refs\":{}}";

    /// <summary>401 or 403 at the door: no receipt, only the words.</summary>
    public static string Door(string error = Gone) => "{\"ok\":false,\"error\":\"" + error + "\"}";

    /// <summary>Who answered, as Stagewatch names a show app: the name cut (and trimmed) to fit 40 characters with the device's address, which is never cut.</summary>
    public static string AnsweredBy(string name, string address)
    {
        var suffix = " (" + address + ")";
        var given = name.Trim();
        var room = 40 - suffix.Length;
        if (given.Length == 0 || room <= 0) return address;
        var cut = given[..Math.Min(given.Length, room)].TrimEnd();
        return cut.Length == 0 ? address : cut + suffix;
    }

    /// <summary>
    /// A state frame with the speaker's message as Stagewatch's frames carry it: <c>message</c>, <c>urgency</c>, <c>flash</c>,
    /// and the signal's three fields at the end (0, "", "" with no signal, and again 30 s after its answer or its cancel).
    /// </summary>
    public static string Frame(long seq, string message, string flash, long messageId, string messageAck, string messageAckBy)
        => "{\"type\":\"state\",\"v\":1,\"seq\":" + seq.ToString(CultureInfo.InvariantCulture)
           + ",\"mode\":\"countdown\",\"state\":\"running\",\"seconds\":600,\"total\":900,\"text\":\"10:00\",\"phase\":\"normal\",\"title\":\"Keynote — Ana\""
           + ",\"message\":" + System.Text.Json.JsonSerializer.Serialize(message) + ",\"urgency\":\"info\",\"flash\":\"" + flash + "\""
           + ",\"messageId\":" + messageId.ToString(CultureInfo.InvariantCulture)
           + ",\"messageAck\":\"" + messageAck + "\",\"messageAckBy\":" + System.Text.Json.JsonSerializer.Serialize(messageAckBy) + "}";
}
