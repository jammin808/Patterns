using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>One request as it leaves: the fence with the brief, the turns so far (the new question last), and whether the reply is pinned to the schema on the wire or asked for in plain JSON with the schema in the prompt.</summary>
public sealed record AssistantRequest(string System, IReadOnlyList<AssistantTurnText> Turns, bool Plain = false);

/// <summary>One turn of the conversation: the operator's words with the files they attached, or the JSON the model answered.</summary>
public sealed record AssistantTurnText(bool Mine, string Text, IReadOnlyList<AssistantAttachment>? Attachments = null)
{
    public IReadOnlyList<AssistantAttachment> Attachments { get; init; } = Attachments ?? Array.Empty<AssistantAttachment>();
}

/// <summary>What an ask came back with: the reply (null when nothing usable came), the desk's words, whether anything was sent.</summary>
public sealed record AssistantAnswer(AssistantReply? Reply, string Status, bool Sent);

/// <summary>
/// The assistant's wire: the key from its store beside the settings, the conversation kept in
/// memory for the session, one request per ask to the model through the official SDK with the
/// reply pinned to the schema, the network off the UI thread, every failure in words. Three
/// fences: <see cref="AssistantScope.Gate"/> stops the obvious probes before anything is sent,
/// the system prompt tells the model what it is and is not, and the reply's own in_scope flag
/// says whether the model declined. The model proposes; APPLY is the desk's, through Core.
/// </summary>
public sealed class AssistantService
{
    public const string Model = "claude-opus-5";
    public const int MaxTokens = 16000;

    /// <summary>Turns kept and sent again: a long session keeps its last dozen exchanges.</summary>
    public const int KeptTurns = 24;

    /// <summary>How many of the latest exchanges send their attachments again in full; older turns say what was attached in words instead of sending the bytes every ask.</summary>
    public const int ExchangesWithAttachments = 2;

    private readonly AppServices _services;
    private readonly List<AssistantTurnText> _turns = new();
    private bool _plain;   // the service refused the reply's schema once this session: every ask since carries it in words instead

    public AssistantService(AppServices services, AssistantKeyStore keys)
    {
        _services = services;
        Keys = keys;
    }

    public AssistantKeyStore Keys { get; }

    /// <summary>Tests only: answers a request with the JSON reply instead of the network.</summary>
    public Func<AssistantRequest, Task<string>>? Transport { get; set; }

    /// <summary>How many requests went out this session (the tests count them).</summary>
    public int Sent { get; private set; }

    /// <summary>The last request as it left — the tests read the fence and the brief off it.</summary>
    public AssistantRequest? LastRequest { get; private set; }

    public IReadOnlyList<AssistantTurnText> Turns => _turns;

    /// <summary>Whether the asks go out in plain JSON (the schema in the prompt, the reply read on this side) because the service refused the schema on the wire.</summary>
    public bool PlainJson => _plain;

    public bool HasKey => Keys.Read().HasKey;

    public string KeyMasked => Keys.Read().Masked;

    public bool Busy { get; private set; }

    /// <summary>What the editors and the PGM pane's preview edit right now — the desk sets it before each ask ("Program", or a screen's own picture).</summary>
    public string EditingTarget { get; set; } = "Program";

    /// <summary>
    /// The desk's states for the brief, read from the services at the moment of the ask: EDIT
    /// SAFE and the two states, the outputs, the canvases and what every screen shows, the cue
    /// on standby, the inputs mounted (by nickname and kind, never a path), the library's names,
    /// the sound, the lower third on air, the sends and the stream. Never throws: a service that
    /// cannot answer leaves its line at the default, and the ask goes out with the rest.
    /// </summary>
    public ShowFacts Gather()
    {
        var s = _services;
        var state = s.State;
        var air = s.AirState;
        var canvases = new List<CanvasFact>();
        var shows = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var geo = Rig.Geometry(state, s.Screens.All);
            var infos = new Dictionary<string, ScreenInfo>(StringComparer.Ordinal);
            foreach (var info in s.Screens.All) infos[info.Id] = info;
            string LabelOf(string id)
            {
                var placement = state.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
                return placement is null ? id : Rig.LabelFor(placement, infos.GetValueOrDefault(id));
            }
            foreach (var target in geo.Targets)
            {
                if (!ContentTargets.IsCanvasKey(target)) continue;
                var members = geo.MembersOf(target);
                var size = geo.SizeOf(target);
                var name = state.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == target)?.Name ?? "";
                canvases.Add(new CanvasFact(geo.LetterOf(target), name, size.Width, size.Height, members.ToList(), members.Select(LabelOf).ToList()));
            }
            foreach (var p in state.Output.Placements)
            {
                string words;
                if (p.MirrorOf.Length > 0) words = "a repeater of " + (ContentTargets.IsCanvasKey(p.MirrorOf) ? "canvas " + geo.LetterOf(p.MirrorOf) : LabelOf(p.MirrorOf));
                else if (!p.Enabled) words = "nothing (off)";
                else
                {
                    var target = geo.TargetOf(p.ScreenId);
                    var own = ContentTargets.UsesOwnPattern(air, target) ? air.Independent.FirstOrDefault(a => a.ScreenId == target)?.Pattern : null;
                    var picture = own is null ? "the program" : "its own picture: " + ShowBrief.PatternWords(own);
                    words = ContentTargets.IsCanvasKey(target) ? $"canvas {geo.LetterOf(target)} with {picture}" : picture;
                }
                shows[p.ScreenId] = words;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the rig.", ex);
        }

        var inputs = new List<string>();
        try
        {
            foreach (var key in InputBus.Keys)
            {
                var kind = key.StartsWith("cap:", StringComparison.Ordinal) ? "a capture input"
                    : key.StartsWith("ndi:", StringComparison.Ordinal) ? "an NDI feed"
                    : key.StartsWith("web:", StringComparison.Ordinal) ? "a web page"
                    : key.StartsWith("deck:", StringComparison.Ordinal) ? "a deck"
                    : key.StartsWith("vid:", StringComparison.Ordinal) ? "a clip"
                    : "a source";
                var label = state.InputLabel(key, "");
                inputs.Add(label.Length > 0 ? $"{label} ({kind[2..]})" : kind);   // the nickname and the kind, never the key's path or address
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the inputs.", ex);
        }

        var standby = s.CueStack.StandbyCue;
        var last = s.CueStack.LastCue;
        var audioNow = "";
        try
        {
            audioNow = s.AudioPlayer.NowPath.Length > 0 ? $"playing '{s.AudioPlayer.CurrentName}'" : state.AudioPlayer.Items.Count + state.AudioPlayer.Folders.Count > 0 ? "stopped" : "";
        }
        catch (Exception ex)
        {
            Log.Warn("The assistant's brief could not read the audio player.", ex);
        }
        var lowerThird = air.LowerThirds.IsShowing ? air.LowerThirds.Active?.Name ?? "" : "";
        return new ShowFacts
        {
            EditSafeOpen = s.Sandbox.Active,
            Air = s.Sandbox.Active ? air : null,
            AirLabel = s.AirLabel == "—" ? "" : s.AirLabel,   // the strip's dash is "no look named", not a name
            PreviewLook = s.PreviewLookId.Length > 0 ? LookService.Find(state, s.PreviewLookId)?.Name ?? "" : "",
            EditingTarget = EditingTarget,
            OutputsLive = s.Outputs.IsLive,
            OutputWindows = s.Outputs.Windows.Count,
            Canvases = canvases,
            ScreenShows = shows,
            StackArmed = s.CueStack.Armed,
            StandbyCue = standby is null ? "" : $"{standby.Number} {standby.Name}".Trim(),
            LastCue = last is null ? "" : $"{last.Number} {last.Name}".Trim(),
            InputsMounted = inputs,
            MediaFiles = state.MediaLibrary.Count,
            MediaNames = state.MediaLibrary.Where(m => m.Name.Trim().Length > 0).Select(m => m.Name.Trim()).ToList(),
            AudioNow = audioNow,
            VogOnAir = s.Stingers.VogOnAir,
            StingOnAir = s.Stingers.StingOnAir,
            LowerThirdOnAir = lowerThird,
            NdiSendsRunning = s.Ndi.ActiveCount,
            StreamStatus = s.Stream.Status,
        };
    }

    /// <summary>SAVE KEY: the key to the store beside the settings; blank forgets it.</summary>
    public void SaveKey(string? key)
    {
        var k = AssistantKey.Normalise(key);
        if (k.Length == 0)
        {
            Keys.Clear();
            return;
        }
        Keys.Write(new AssistantKey(k, DateTime.UtcNow));
    }

    /// <summary>FORGET: the key leaves this machine; the conversation stays until cleared.</summary>
    public void ForgetKey() => Keys.Clear();

    public void Clear() => _turns.Clear();

    /// <summary>One ask: the gate, the key, the brief, the wire, the parse — never a throw to the desk. The files attached ride in the turn.</summary>
    public async Task<AssistantAnswer> AskAsync(string? text, IReadOnlyList<AssistantAttachment>? attachments = null)
    {
        var question = (text ?? "").Trim();
        var files = attachments ?? Array.Empty<AssistantAttachment>();
        if (question.Length == 0 && files.Count > 0) question = "Read what I have attached and work out a plan for the show from it.";
        if (question.Length == 0) return new AssistantAnswer(null, "Type a question, or what you want built.", false);
        if (AssistantScope.Gate(question) is { } refusal) return new AssistantAnswer(AssistantReply.Refusal(refusal), "Not sent — that is outside what the assistant does.", false);
        var key = Keys.Read();
        if (!key.HasKey) return new AssistantAnswer(null, "No key saved — paste an Anthropic API key in the KEY block first. Nothing leaves this machine without one.", false);
        if (Busy) return new AssistantAnswer(null, "Still waiting on the last answer.", false);
        if (files.Count > AssistantAttachments.MaxAttachments) return new AssistantAnswer(null, $"At most {AssistantAttachments.MaxAttachments} files ride with one ask — remove some.", false);
        if (files.Sum(f => (long)f.Size) > AssistantAttachments.MaxTotalBytes) return new AssistantAnswer(null, "The files attached add up to more than one ask can carry — remove the largest.", false);

        Busy = true;
        Sent++;
        var brief = ShowBrief.Summarise(_services.State, Gather());
        var turns = History();
        turns.Add(new AssistantTurnText(true, question, files));
        var request = new AssistantRequest(AssistantScope.SystemPrompt(brief, _plain), turns, _plain);
        LastRequest = request;
        var fellBack = false;
        try
        {
            string json;
            try
            {
                json = await Send(key.ApiKey, request);
            }
            catch (Exception ex) when (!request.Plain && AssistantScope.IsSchemaRefusal(ex.Message))
            {
                // The service would not compile the reply's schema into its grammar: the same ask again
                // with the schema in the prompt and the reply read leniently here — and every ask after
                // it, this session, goes that way from the start.
                Log.Warn("The assistant's reply schema was refused by the service — asking again in plain JSON.", ex);
                _plain = true;
                fellBack = true;
                request = new AssistantRequest(AssistantScope.SystemPrompt(brief, plain: true), turns, Plain: true);
                LastRequest = request;
                Sent++;
                json = await Send(key.ApiKey, request);
            }
            var reply = AssistantParser.Parse(json);
            if (reply is null)
            {
                Log.Warn("Assistant reply could not be read: " + (json is { Length: > 0 } ? json[..Math.Min(json.Length, 300)] : "(empty)"));
                return new AssistantAnswer(null, "The assistant's reply could not be read — ask again.", true);
            }
            _turns.Add(new AssistantTurnText(true, question, files));
            _turns.Add(new AssistantTurnText(false, json));
            while (_turns.Count > KeptTurns) _turns.RemoveRange(0, 2);
            var status = !reply.InScope ? "Declined — the assistant only talks about Patterns and the show."
                : reply.Proposals.Count > 0 ? $"{reply.Proposals.Count} proposal{(reply.Proposals.Count == 1 ? "" : "s")} — APPLY the ones you want; nothing changes until you do."
                : reply.Questions.Count > 0 ? "The assistant has questions before it proposes."
                : "Answered.";
            if (fellBack) status += " (The service declined the reply's schema; the answer came as plain JSON and was read all the same.)";
            return new AssistantAnswer(reply, status, true);
        }
        catch (AnthropicUnauthorizedException)
        {
            return new AssistantAnswer(null, "The key was refused — check it in the KEY block (401).", true);
        }
        catch (AnthropicRateLimitException)
        {
            return new AssistantAnswer(null, "Rate limited — wait a moment and ask again (429).", true);
        }
        catch (Anthropic5xxException)
        {
            return new AssistantAnswer(null, "The service is having trouble — try again in a minute.", true);
        }
        catch (AnthropicIOException ex)
        {
            return new AssistantAnswer(null, $"No connection — the assistant needs the internet ({ex.Message}).", true);
        }
        catch (AnthropicApiException ex)
        {
            Log.Warn("Assistant request failed.", ex);
            return new AssistantAnswer(null, $"The assistant failed: {ex.Message}", true);
        }
        catch (Exception ex)
        {
            Log.Warn("Assistant request failed.", ex);
            return new AssistantAnswer(null, $"The assistant failed: {ex.Message}", true);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// The conversation so far as the next request carries it: the latest exchanges with their
    /// attachments in full, older turns with a line saying what was attached instead of the bytes.
    /// </summary>
    private List<AssistantTurnText> History()
    {
        var list = new List<AssistantTurnText>(_turns.Count);
        var keepFrom = Math.Max(0, _turns.Count - ExchangesWithAttachments * 2);
        for (var i = 0; i < _turns.Count; i++)
        {
            var t = _turns[i];
            if (i >= keepFrom || t.Attachments.Count == 0) list.Add(t);
            else list.Add(new AssistantTurnText(t.Mine, "[The operator attached earlier: " + string.Join("; ", t.Attachments.Select(a => a.Label)) + "]\n" + t.Text));
        }
        return list;
    }

    /// <summary>The wire, or the tests' transport in its place; the network off the UI thread.</summary>
    private Task<string> Send(string apiKey, AssistantRequest request)
        => Transport is { } transport ? transport(request) : Task.Run(() => SendAsync(apiKey, request));

    /// <summary>
    /// A turn as the wire takes it: words alone, or the attached files as their own blocks —
    /// a picture as an image, a PDF as a document, words as a text document — each after a
    /// line saying what it is, and the question last.
    /// </summary>
    public static MessageParam ToMessage(AssistantTurnText t)
    {
        var role = t.Mine ? Role.User : Role.Assistant;
        if (t.Attachments.Count == 0) return new MessageParam { Role = role, Content = t.Text };
        var blocks = new List<ContentBlockParam>();
        foreach (var a in t.Attachments)
        {
            blocks.Add(new TextBlockParam { Text = AssistantAttachments.Heading(a) });
            switch (a.Kind)
            {
                case AssistantAttachmentKind.Image when a.Bytes is not null:
                    blocks.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = Convert.ToBase64String(a.Bytes), MediaType = a.MediaType } });
                    break;
                case AssistantAttachmentKind.Pdf when a.Bytes is not null:
                    blocks.Add(new DocumentBlockParam { Source = new Base64PdfSource(Convert.ToBase64String(a.Bytes)), Title = a.Name });
                    break;
                default:
                    blocks.Add(new DocumentBlockParam { Source = new PlainTextSource(a.Text), Title = a.Name });
                    break;
            }
        }
        blocks.Add(new TextBlockParam { Text = t.Text });
        return new MessageParam { Role = role, Content = blocks };
    }

    /// <summary>The request on the wire: the official SDK, the reply pinned to the schema (or asked for in plain JSON when the service refused the schema), a decline read as one.</summary>
    private static async Task<string> SendAsync(string apiKey, AssistantRequest request)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = MaxTokens,
            System = new List<TextBlockParam> { new() { Text = request.System } },
            Messages = request.Turns.Select(ToMessage).ToList(),
            OutputConfig = request.Plain ? null : new Anthropic.Models.Messages.OutputConfig { Format = new JsonOutputFormat { Schema = AssistantScope.SchemaElements() } },
        };
        var response = await client.Messages.Create(parameters);
        if (response.StopReason == "refusal")
        {
            return AssistantReply.RefusalJson(response.StopDetails?.Explanation ?? "");
        }
        var sb = new StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text)) sb.Append(text.Text);
        }
        return sb.ToString();
    }
}
