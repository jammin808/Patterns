using System.Text;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>One request as it leaves: the fence with the brief, the turns so far (the new question last), and whether the reply is pinned to the schema on the wire or asked for in plain JSON with the schema in the prompt.</summary>
public sealed record AssistantRequest(string System, IReadOnlyList<AssistantTurnText> Turns, bool Plain = false);

/// <summary>One turn of the conversation: the operator's words, or the JSON the model answered.</summary>
public sealed record AssistantTurnText(bool Mine, string Text);

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

    /// <summary>One ask: the gate, the key, the brief, the wire, the parse — never a throw to the desk.</summary>
    public async Task<AssistantAnswer> AskAsync(string? text)
    {
        var question = (text ?? "").Trim();
        if (question.Length == 0) return new AssistantAnswer(null, "Type a question, or what you want built.", false);
        if (AssistantScope.Gate(question) is { } refusal) return new AssistantAnswer(AssistantReply.Refusal(refusal), "Not sent — that is outside what the assistant does.", false);
        var key = Keys.Read();
        if (!key.HasKey) return new AssistantAnswer(null, "No key saved — paste an Anthropic API key in the KEY block first. Nothing leaves this machine without one.", false);
        if (Busy) return new AssistantAnswer(null, "Still waiting on the last answer.", false);

        Busy = true;
        Sent++;
        var brief = ShowBrief.Summarise(_services.State);
        var turns = new List<AssistantTurnText>(_turns) { new(true, question) };
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
            _turns.Add(new AssistantTurnText(true, question));
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

    /// <summary>The wire, or the tests' transport in its place; the network off the UI thread.</summary>
    private Task<string> Send(string apiKey, AssistantRequest request)
        => Transport is { } transport ? transport(request) : Task.Run(() => SendAsync(apiKey, request));

    /// <summary>The request on the wire: the official SDK, the reply pinned to the schema (or asked for in plain JSON when the service refused the schema), a decline read as one.</summary>
    private static async Task<string> SendAsync(string apiKey, AssistantRequest request)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = MaxTokens,
            System = new List<TextBlockParam> { new() { Text = request.System } },
            Messages = request.Turns.Select(t => new MessageParam { Role = t.Mine ? Role.User : Role.Assistant, Content = t.Text }).ToList(),
            OutputConfig = request.Plain ? null : new OutputConfig { Format = new JsonOutputFormat { Schema = AssistantScope.SchemaElements() } },
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
