using System.Net.Http.Headers;
using System.Text;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Break music: Patterns drives Spotify, Spotify makes the sound. One reconciling poll like
/// <see cref="AudioPlayerService"/> — it reads the live model every tick, sandbox or not, because
/// break music is not part of the picture the sandbox freezes. Exactly one request is in flight at
/// a time; every failure lands as a sentence in <see cref="Status"/> and never as an exception.
/// With break music switched off, without a Client ID, or in a second window on the same folder,
/// this service never opens a socket — which is why the headless suite runs offline.
/// </summary>
public sealed class SpotifyService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Longer than <c>CueStackService.SettleWindow</c> (12 s), short enough that a dead network cannot poison the night.</summary>
    private static readonly TimeSpan CommandFailureLife = TimeSpan.FromSeconds(15);

    /// <summary>A slider drag is one write, not a storm.</summary>
    private static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(250);

    private readonly AppServices _services;
    private readonly SpotifyCredentialStore _store;
    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _life = new();

    private SpotifyCredentials _creds = SpotifyCredentials.None;
    private SpotifyToken _token = SpotifyToken.None;
    private string? _appliedKey;                      // null = unknown; see the first-tick rule in Tick
    private string _deviceId = "";
    private int _sentLevel = -1;
    private int _consecutiveFailures;
    private int _consecutive401;
    private DateTime _levelSentUtc = DateTime.MinValue;
    private DateTime _lastReadUtc = DateTime.MinValue;
    private DateTime _blockedUntilUtc = DateTime.MinValue;
    private DateTime _commandFailureUtc = DateTime.MinValue;
    private DateTime _playedAtUtc = DateTime.MinValue;
    private bool _playedThisSession;
    private bool _skip;
    private volatile bool _busy;
    private volatile string _status = "Off.";
    private volatile string _nowPlaying = "";
    private volatile string _deviceLabel = "";
    private volatile string _account = "Not connected.";
    private volatile string _commandFailure = "";
    private volatile string? _signedOutReason;       // why the sign-in went, until CONNECT is pressed again

    public SpotifyService(AppServices services, SpotifyCredentialStore store)
    {
        _services = services;
        _store = store;
        _creds = store.Read();
        Transport = DefaultTransport;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    // ---- what the desk, the remote and the cue rows read ------------------------------

    /// <summary>A sentence the operator can act on. Never scanned for cue settling — see <see cref="CommandFailure"/>.</summary>
    public string Status => _status;

    /// <summary>"Bonobo · Kerala", or empty.</summary>
    public string NowPlaying => _nowPlaying;

    /// <summary>The Spotify device the sound is coming out of, as Spotify names it.</summary>
    public string DeviceLabel => _deviceLabel;

    public string AccountText => _account;

    /// <summary>
    /// Set only when a play / pause / skip this app issued failed. Cleared on the next success and
    /// expired after 15 s. Read by <see cref="CueStackService"/> — and nothing else about break
    /// music is, because Status legitimately reports setup facts for minutes at a time.
    /// </summary>
    public string CommandFailure => _commandFailure;

    public bool Connected => _creds.IsConnected && !_token.IsEmpty;

    public bool HasClientId => _creds.HasClientId;

    /// <summary>The operator's own Client ID from the Spotify developer dashboard. The setter writes the sidecar.</summary>
    public string ClientId
    {
        get => _creds.ClientId;
        set
        {
            var id = (value ?? "").Trim();
            if (id == _creds.ClientId) return;
            // A different app means a different sign-in: the refresh token issued for the old one
            // is worthless, and keeping it would produce a 400 nobody can read.
            _creds = new SpotifyCredentials(id, "", "", NowUtc());
            _token = SpotifyToken.None;
            _signedOutReason = null;
            _store.Write(_creds);
            _account = id.Length > 0 ? "Not connected." : "No Client ID.";
        }
    }

    public IReadOnlyList<SpotifyDevice> Devices { get; private set; } = Array.Empty<SpotifyDevice>();

    public IReadOnlyList<SpotifyPlaylistRef> Playlists { get; private set; } = Array.Empty<SpotifyPlaylistRef>();

    /// <summary>Set by the skip verb; consumed by the next poll. Never a synchronous socket from a cue.</summary>
    public bool SkipRequested { get => _skip; set => _skip = value; }

    /// <summary>The timer body, callable directly (tests drive it without waiting on the clock).</summary>
    public void Poll() => Tick();

    /// <summary>STOP ALL and PAUSE: the pause goes out on this UI turn rather than up to 400 ms later.</summary>
    public void PokeNow() => Tick();

    /// <summary>
    /// The only way this service reaches the network. Defaulted to the real HttpClient; a test
    /// replaces it with a function and never opens a socket. A Func seam, not a container — the
    /// <c>ScreenService.PlannedProvider</c> / <c>CueValidationContext.FileExists</c> idiom.
    /// </summary>
    public Func<SpotifyRequest, CancellationToken, Task<SpotifyReply>> Transport { get; set; }

    /// <summary>The clock, so token expiry, rate limits and backoff are testable without sleeping.</summary>
    public Func<DateTime> NowUtc { get; set; } = () => DateTime.UtcNow;

    // ---- the reconciler ---------------------------------------------------------------

    private void Tick()
    {
        try
        {
            Reconcile();
        }
        catch (Exception ex)
        {
            // A poll must never be able to take the app down; the operator gets a sentence.
            Log.Error("Break music poll failed.", ex);
            _status = $"Break music could not run: {ex.Message}";
        }
    }

    private void Reconcile()
    {
        // The LIVE model, every tick — never AirState, never the snapshot: break music is sound,
        // and the sandbox freezes the picture.
        var cfg = _services.State.Spotify;

        if (!cfg.Enabled)
        {
            // Intent must not survive the feature being switched off. Turning it off is not a
            // stop: nothing is sent, so whatever Spotify is doing carries on.
            if (cfg.Playing) cfg.Playing = false;
            if (cfg.PlayingId.Length > 0) cfg.PlayingId = "";
            _appliedKey = null;
            _sentLevel = -1;
            _deviceId = "";
            _skip = false;
            _nowPlaying = "";
            _deviceLabel = "";
            _commandFailure = "";
            _status = "Off.";
            return;
        }
        if (!_services.IsPrimaryInstance)
        {
            _status = "Break music is run by the first Patterns window.";
            return;
        }
        if (!_creds.HasClientId)
        {
            _status = "Add your Spotify Client ID on the Audio page.";
            return;
        }
        if (!_creds.IsConnected)
        {
            _status = _signedOutReason ?? "Not connected — press CONNECT on the Audio page.";
            return;
        }
        if (_busy) return;

        // The first tick must never issue a pause against the operator's own Spotify: an unknown
        // applied key adopts "nothing is playing" instead of comparing against a blank — before
        // anything is sent, so a failing token refresh cannot count against a pause nobody asked
        // for. A play the operator has already asked for is a deliberate instruction and still goes out.
        var want = Key(cfg);
        if (_appliedKey is null && !cfg.Playing) _appliedKey = want;

        var now = NowUtc();
        if (now < _blockedUntilUtc) return;
        if (_commandFailure.Length > 0 && now - _commandFailureUtc > CommandFailureLife) _commandFailure = "";

        if (_token.IsEmpty || _token.NeedsRefresh(now))
        {
            _ = IssueRefresh();
            return;
        }

        if (_appliedKey is null || want != _appliedKey)
        {
            _ = cfg.Playing ? IssuePlay(cfg, want) : IssuePause(want);
            return;
        }

        if (_skip)
        {
            _skip = false;
            if (cfg.Playing)
            {
                _ = IssueNext();
                return;
            }
        }

        var level = MusicLevel.DevicePercent(cfg.LevelPct, _services.Stingers.MusicGainAt(now));
        if (cfg.Playing && level != _sentLevel && now - _levelSentUtc >= LevelInterval)
        {
            _ = IssueVolume(level);
            return;
        }

        var readEvery = cfg.Playing ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(15);
        if (now - _lastReadUtc >= readEvery) _ = IssueRead();
    }

    private static string Key(SpotifyConfig cfg) => $"{cfg.Playing}|{cfg.PlayingId}|{cfg.DeviceName}";

    /// <summary>"play" / "pause" while an intent is still waiting to land, else null: a token that cannot be renewed then counts against it.</summary>
    private string? PendingVerb()
    {
        var cfg = _services.State.Spotify;
        if (_appliedKey == Key(cfg)) return null;
        return cfg.Playing ? "play" : "pause";
    }

    // ---- the requests -----------------------------------------------------------------

    private async Task IssueRefresh()
    {
        _busy = true;
        try
        {
            var reply = await Send(new SpotifyRequest("POST", SpotifyEndpoints.TokenUrl,
                SpotifyEndpoints.RefreshForm(_creds.ClientId, _creds.RefreshToken),
                ContentType: "application/x-www-form-urlencoded"));
            if (!Handle(reply, PendingVerb(), refresh: true)) return;
            if (SpotifyJson.TryReadToken(reply.Body, NowUtc(), _creds.RefreshToken, out var token))
            {
                _token = token;
                if (token.RefreshToken.Length > 0 && token.RefreshToken != _creds.RefreshToken)
                {
                    // Spotify rotates refresh tokens: the sidecar has to follow or the next launch
                    // signs out for no visible reason.
                    _creds = _creds with { RefreshToken = token.RefreshToken, SavedUtc = NowUtc() };
                    _store.Write(_creds);
                }
                _lastReadUtc = DateTime.MinValue; // read the truth back straight away
            }
            else
            {
                _status = "Spotify sign-in could not be renewed — press CONNECT on the Audio page.";
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// One ordered chain, stopping at the first non-2xx: resolve the device, wake it, set shuffle,
    /// then play. Worst case four round trips inside one tick, not four ticks.
    /// </summary>
    private async Task IssuePlay(SpotifyConfig cfg, string want)
    {
        _busy = true;
        try
        {
            if (cfg.DeviceName.Length > 0 && _deviceId.Length == 0)
            {
                var list = await Send(new SpotifyRequest("GET", SpotifyEndpoints.DevicesUrl));
                if (!Handle(list, "play")) return;
                Devices = SpotifyJson.ReadDevices(list.Body);
                var match = Devices.FirstOrDefault(d => string.Equals(d.Name, cfg.DeviceName, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    // Never a refusal: the desk machine's Spotify gets restarted between rehearsal
                    // and doors, and the room still needs music.
                    _deviceId = "";
                    _status = $"'{cfg.DeviceName}' is not on Spotify right now — playing on the active device.";
                }
                else
                {
                    _deviceId = match.Id;
                    if (!match.IsActive)
                    {
                        var wake = await Send(new SpotifyRequest("PUT", SpotifyEndpoints.TransferUrl,
                            SpotifyEndpoints.TransferBody(_deviceId)));
                        if (!Handle(wake, "play")) return;
                    }
                }
            }

            var item = SpotifyLibrary.Find(_services.State, cfg.PlayingId);
            if (item is null && cfg.PlayingId.Length == 0 && !_playedThisSession)
            {
                // Nothing has played yet and no target was named: start the library at the top
                // rather than resuming a context nobody in this room chose.
                item = cfg.Items.FirstOrDefault();
            }

            if (item is { Shuffle: true })
            {
                var shuffle = await Send(new SpotifyRequest("PUT", SpotifyEndpoints.ShuffleUrl(true, _deviceId)));
                if (!Handle(shuffle, "play")) return;
            }

            var play = await Send(new SpotifyRequest("PUT", SpotifyEndpoints.PlayUrl(_deviceId),
                SpotifyEndpoints.PlayBody(item?.Uri)));
            if (!Handle(play, "play")) return;

            _appliedKey = want;
            _playedAtUtc = NowUtc();
            if (item is not null) _playedThisSession = true;
            _sentLevel = -1;              // re-assert the level on the next tick
            _lastReadUtc = DateTime.MinValue;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task IssuePause(string want)
    {
        _busy = true;
        try
        {
            var reply = await Send(new SpotifyRequest("PUT", SpotifyEndpoints.PauseUrl(_deviceId)));
            if (!Handle(reply, "pause")) return;
            _appliedKey = want;
            _lastReadUtc = DateTime.MinValue;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task IssueNext()
    {
        _busy = true;
        try
        {
            var reply = await Send(new SpotifyRequest("POST", SpotifyEndpoints.NextUrl(_deviceId)));
            if (!Handle(reply, "skip")) return;
            _lastReadUtc = DateTime.MinValue;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task IssueVolume(int level)
    {
        _busy = true;
        try
        {
            var reply = await Send(new SpotifyRequest("PUT", SpotifyEndpoints.VolumeUrl(level, _deviceId)));
            // A level write is never an intent command: it must not be able to flip a cue row.
            if (!Handle(reply, null)) return;
            _sentLevel = level;
            _levelSentUtc = NowUtc();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task IssueRead()
    {
        _busy = true;
        try
        {
            var reply = await Send(new SpotifyRequest("GET", SpotifyEndpoints.PlayerUrl));
            // A read-back is never an intent command either.
            if (!Handle(reply, null)) return;
            _lastReadUtc = NowUtc();
            var now = SpotifyJson.ReadNowPlaying(reply.Body);
            _nowPlaying = now?.Line ?? "";
            _deviceLabel = now?.DeviceName ?? "";

            var cfg = _services.State.Spotify;
            // Adoption: someone paused on their phone, or the app was relaunched over music that
            // never stopped. Only a positive report counts — a 204 says "no state", not "paused",
            // and must not quietly drop an intent the operator just pressed. The applied key moves
            // with the adoption, or the next tick would issue a command undoing it.
            if (now is not null && now.IsPlaying != cfg.Playing && _appliedKey == Key(cfg))
            {
                cfg.Playing = now.IsPlaying;
                _appliedKey = Key(cfg);
                _sentLevel = -1;
            }
            _status = StatusLine(cfg, now);
        }
        finally
        {
            _busy = false;
        }
    }

    private string StatusLine(SpotifyConfig cfg, SpotifyNowPlaying? now)
    {
        if (now is { IsPlaying: true })
        {
            var where = now.DeviceName.Length > 0 ? $" — on {now.DeviceName}" : "";
            var line = now.Line.Length > 0 ? $" — {now.Line}" : "";
            return $"Playing{line}{where} · {now.VolumePercent}%";
        }
        if (cfg.Playing)
        {
            return NowUtc() - _playedAtUtc < TimeSpan.FromSeconds(10)
                ? "Starting…"
                : "Spotify reports nothing playing — open Spotify on the desk machine and press play once.";
        }
        if (now is not null) return "Paused.";
        return cfg.DeviceName.Length > 0 && _deviceId.Length > 0
            ? $"Ready — Spotify on {cfg.DeviceName}."
            : "Ready — Spotify will use whichever device is active.";
    }

    /// <summary>
    /// One place for every reply. Returns true for a 2xx. <paramref name="intentVerb"/> is set only
    /// for play / pause / skip — never a volume write, never a read-back — because only those may
    /// flip a settling cue row.
    /// </summary>
    private bool Handle(SpotifyReply reply, string? intentVerb, bool refresh = false)
    {
        if (SpotifyJson.Failure(reply) is not { } problem)
        {
            _consecutiveFailures = 0;
            if (!refresh) _consecutive401 = 0;   // a fresh token proves nothing until the API accepts it
            if (intentVerb is not null) _commandFailure = "";
            return true;
        }

        _status = problem;
        Log.Warn($"Break music: {problem}");
        // A rate limit is transient and self-healing; it is the one failure that must not reach
        // the cue rows (§ the failure table).
        if (intentVerb is not null && reply.StatusCode != 429)
        {
            _commandFailure = $"Break music could not {intentVerb} — {problem}";
            _commandFailureUtc = NowUtc();
        }

        switch (reply.StatusCode)
        {
            case 400 when refresh:
            case 401 when refresh:
                // The refresh token itself is dead (revoked in Spotify, or minted for another
                // app): retrying it every backoff forever would only ever say "400".
                SignOut();
                break;
            case 401:
                _token = SpotifyToken.None;                       // one refresh on the next tick
                if (++_consecutive401 >= 2) SignOut();  // a fresh token was refused too
                else _blockedUntilUtc = NowUtc() + SpotifyBackoff.Delay(++_consecutiveFailures);
                break;
            case 403:
                _blockedUntilUtc = NowUtc().AddSeconds(60);   // Premium / not allow-listed: never a retry storm
                break;
            case 429:
                _blockedUntilUtc = NowUtc().AddSeconds(Math.Max(1, reply.RetryAfterSeconds));
                break;
            default:
                if (reply.StatusCode == 404) _deviceId = "";   // re-resolve the device next attempt
                _blockedUntilUtc = NowUtc() + SpotifyBackoff.Delay(++_consecutiveFailures);
                break;
        }
        // _appliedKey is deliberately not advanced, so the same play or pause is retried on the
        // next unblocked tick: STOP ALL is a standing instruction, not a claim.
        return false;
    }

    /// <summary>The sign-in leaves the sidecar; the reason stays on the status line until CONNECT.</summary>
    private void SignOut()
    {
        _creds = _creds with { RefreshToken = "" };
        _store.Write(_creds);
        _token = SpotifyToken.None;
        _signedOutReason = "Spotify sign-in expired — press CONNECT on the Audio page.";
        _status = _signedOutReason;
    }

    /// <summary>
    /// Every request goes through here. A transport that throws (a test double, a future
    /// replacement) is the same as never reaching Spotify: reply 0, one sentence, no crash.
    /// </summary>
    private async Task<SpotifyReply> Send(SpotifyRequest request)
    {
        try
        {
            return await Transport(request with { Bearer = _token.AccessToken }, _life.Token);
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify request could not be sent.", ex);
            return new SpotifyReply(0, "");
        }
    }

    /// <summary>Every exception becomes <c>SpotifyReply(0, "")</c> — one try, one place.</summary>
    private static async Task<SpotifyReply> DefaultTransport(SpotifyRequest request, CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            if (request.Body is { } body)
            {
                message.Content = new StringContent(body, Encoding.UTF8, request.ContentType);
            }
            if (request.Bearer is { Length: > 0 } bearer)
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            using var response = await Http.SendAsync(message, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            var retry = response.Headers.TryGetValues("Retry-After", out var values)
                ? SpotifyJson.RetryAfterSeconds(values.FirstOrDefault())
                : 0;
            return new SpotifyReply((int)response.StatusCode, text, retry);
        }
        catch (Exception ex)
        {
            Log.Warn("Spotify request could not be sent.", ex);
            return new SpotifyReply(0, "");
        }
    }

    // ---- desk-only, asynchronous (never from a cue, never from the wire) ---------------

    /// <summary>
    /// PKCE sign-in: a one-shot loopback listener, the system browser, the code exchange, then who
    /// this is and what devices exist. Only ever started by CONNECT on the Audio page.
    /// </summary>
    public async Task ConnectAsync()
    {
        if (!_creds.HasClientId)
        {
            _status = "Add your Spotify Client ID on the Audio page.";
            return;
        }
        LoopbackCallback? callback = null;
        _signedOutReason = null;
        try
        {
            var verifier = SpotifyPkce.NewVerifier();
            var challenge = SpotifyPkce.Challenge(verifier);
            var state = SpotifyPkce.NewState();
            callback = LoopbackCallback.Start(out var redirectUri);
            if (callback is null)
            {
                _status = $"Could not open a local port for Spotify sign-in — {string.Join(", ", LoopbackCallback.Ports)} are all in use.";
                return;
            }

            _status = "Waiting for Spotify sign-in in your browser…";
            var url = SpotifyEndpoints.AuthorizeUrl(_creds.ClientId, redirectUri, challenge, state);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("Spotify sign-in page could not be opened.", ex);
                _status = "Spotify sign-in page could not be opened — no browser is set up on this machine.";
                return;
            }

            var query = await callback.WaitAsync(TimeSpan.FromMinutes(3), _life.Token);
            if (query.Count == 0)
            {
                // No failure word: this is "press CONNECT again", not a fault to chase.
                _status = "Spotify sign-in timed out — press CONNECT again.";
                return;
            }
            if (query.ContainsKey("error"))
            {
                _status = "Spotify sign-in was cancelled.";
                return;
            }
            if (!query.TryGetValue("state", out var back) || back != state)
            {
                _status = "Spotify sign-in could not be verified — press CONNECT again.";
                return;
            }
            if (!query.TryGetValue("code", out var code) || code.Length == 0)
            {
                _status = "Spotify sign-in returned nothing to exchange — press CONNECT again.";
                return;
            }

            var token = await Transport(new SpotifyRequest("POST", SpotifyEndpoints.TokenUrl,
                SpotifyEndpoints.TokenForm(_creds.ClientId, code, verifier, redirectUri),
                ContentType: "application/x-www-form-urlencoded"), _life.Token);
            if (!Handle(token, null)) return;
            if (!SpotifyJson.TryReadToken(token.Body, NowUtc(), "", out var read) || read.RefreshToken.Length == 0)
            {
                _status = "Spotify did not return a sign-in this app can keep — press CONNECT again.";
                return;
            }
            _token = read;

            var me = await Send(new SpotifyRequest("GET", SpotifyEndpoints.MeUrl));
            var account = "";
            var premium = false;
            if (me.Ok) (account, premium) = SpotifyJson.ReadMe(me.Body);
            _creds = new SpotifyCredentials(_creds.ClientId, read.RefreshToken, account, NowUtc());
            _store.Write(_creds);
            _account = account.Length > 0
                ? $"Connected as {account} — {(premium ? "Premium" : "not Premium")}."
                : "Connected.";
            _appliedKey = null;   // adopt whatever Spotify is doing before commanding anything

            await RefreshDevicesAsync();
            _status = premium
                ? _account
                : "Spotify Premium is required to control playback.";
        }
        catch (Exception ex)
        {
            Log.Error("Spotify sign-in failed.", ex);
            _status = $"Spotify sign-in failed: {ex.Message}";
        }
        finally
        {
            callback?.Dispose();
        }
    }

    /// <summary>Forgets this machine's sign-in. Issues no network call — nothing to tell Spotify.</summary>
    public void Disconnect()
    {
        _store.Clear();
        _creds = new SpotifyCredentials(_creds.ClientId, "", "", NowUtc());
        _store.Write(_creds);
        _token = SpotifyToken.None;
        Devices = Array.Empty<SpotifyDevice>();
        Playlists = Array.Empty<SpotifyPlaylistRef>();
        _deviceId = "";
        _appliedKey = null;
        _nowPlaying = "";
        _deviceLabel = "";
        _account = "Not connected.";
        _signedOutReason = null;
        _status = "Not connected — press CONNECT on the Audio page.";
        _services.State.Spotify.Playing = false;
    }

    /// <summary>Re-reads the sidecar. The app never needs this; a test writes a sign-in after boot.</summary>
    public void ReloadCredentials()
    {
        _creds = _store.Read();
        _token = SpotifyToken.None;
        _appliedKey = null;
        _signedOutReason = null;
    }

    public async Task RefreshDevicesAsync()
    {
        if (!Connected) return;
        var reply = await Send(new SpotifyRequest("GET", SpotifyEndpoints.DevicesUrl));
        if (!Handle(reply, null)) return;
        Devices = SpotifyJson.ReadDevices(reply.Body);
        if (Devices.Count == 0)
        {
            _status = "No Spotify device — open Spotify on the desk machine and press play once.";
        }
    }

    public async Task RefreshPlaylistsAsync()
    {
        if (!Connected) return;
        var reply = await Send(new SpotifyRequest("GET", SpotifyEndpoints.PlaylistsUrl()));
        if (!Handle(reply, null)) return;
        Playlists = SpotifyJson.ReadPlaylists(reply.Body);
    }

    /// <summary>
    /// No network on the way out: a Shutdown that makes a call can hang the close for the HTTP
    /// timeout, a watchdog kill never runs it at all, and an operator closing the laptop at the end
    /// of a show does not want the room to go abruptly silent.
    /// </summary>
    public void Dispose()
    {
        _timer.Stop();
        try
        {
            _life.Cancel();
        }
        catch (Exception ex)
        {
            Log.Warn("Break music shutdown issue.", ex);
        }
    }
}
