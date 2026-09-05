using System.Net.Http;
using System.Text;
using Avalonia.Threading;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The site's check-in with its management server: every few minutes one POST out — who it is,
/// its build, its health line and the same STATE every remote reads — and a reply that may carry
/// protocol lines to run (with the server as their origin, fenced like any remote's), an update
/// to download into the updates folder (checked against its SHA-256 before it counts as staged),
/// an apply, a restart. Outbound only, so a shop screen behind a router needs no port opened;
/// off until a URL is typed. Every failure is a line on the page, never a fault in the show.
/// </summary>
public sealed class ManagementService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly AppServices _s;
    private readonly CommandRouter _router;
    private DateTime _lastTryUtc = DateTime.MinValue;
    private int _busy;
    private volatile string _status = "No management URL — the site does not check in.";
    private long _checkIns;
    private long _commandsRun;

    public ManagementService(AppServices services)
    {
        _s = services;
        _router = new CommandRouter(services);
    }

    public string Status => _status;
    public DateTime? LastOkUtc { get; private set; }
    public long CheckIns => Interlocked.Read(ref _checkIns);
    public long CommandsRun => Interlocked.Read(ref _commandsRun);

    /// <summary>The 1 s poll: a check-in when the interval has passed (UI thread).</summary>
    public void Tick(DateTime utcNow)
    {
        var cfg = _s.State.Install;
        if (cfg.ManagementUrl.Length == 0)
        {
            _status = "No management URL — the site does not check in.";
            return;
        }
        if (CheckIn.ProblemWithUrl(cfg.ManagementUrl) is { } problem)
        {
            _status = $"Management URL: {problem}.";
            return;
        }
        if (utcNow - _lastTryUtc < TimeSpan.FromMinutes(cfg.CheckInMinutes)) return;
        _lastTryUtc = utcNow;
        _ = CheckInAsync();
    }

    /// <summary>The page's CHECK IN NOW.</summary>
    public void CheckInNow()
    {
        _lastTryUtc = DateTime.UtcNow;
        _ = CheckInAsync();
    }

    /// <summary>Tests: the check-in as a task to await.</summary>
    public Task CheckInAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                await RunCheckInAsync();
            }
            catch (Exception ex)
            {
                _status = $"Check-in failed: {ex.Message}";
                Log.Warn("Management check-in failed.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    private async Task RunCheckInAsync()
    {
        var cfg = _s.State.Install;
        var url = cfg.ManagementUrl;
        var token = cfg.ManagementToken;
        if (url.Length == 0 || CheckIn.ProblemWithUrl(url) is not null) return;

        var (site, health, state) = await Dispatcher.UIThread.InvokeAsync(() =>
            (cfg.SiteName.Length > 0 ? cfg.SiteName : Environment.MachineName, HealthMonitor.Summary(DateTime.UtcNow), _router.StateJson()));
        var payload = CheckIn.Payload(site, UpdateService.RunningVersion, Environment.MachineName, health, state, DateTime.UtcNow);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (token.Length > 0) request.Headers.TryAddWithoutValidation("X-Patterns-Token", token);
        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _status = $"Check-in answered {(int)response.StatusCode} at {DateTime.Now:HH:mm}.";
            return;
        }
        Interlocked.Increment(ref _checkIns);
        LastOkUtc = DateTime.UtcNow;
        var reply = CheckIn.Parse(body, token);
        if (reply.Problem.Length > 0)
        {
            _status = $"Checked in at {DateTime.Now:HH:mm} — {reply.Problem}.";
            return;
        }

        var notes = new List<string>();
        var origin = new ActionOrigin(OriginKind.Management, site);
        foreach (var line in reply.Commands)
        {
            var answer = await _router.ExecuteAsync(ControlProtocol.Parse(line), origin);
            Interlocked.Increment(ref _commandsRun);
            if (!answer.StartsWith("OK", StringComparison.Ordinal)) notes.Add($"{line} → {answer}");
        }
        if (reply.Update is { } update)
        {
            notes.Add(await StageAsync(update));
        }
        if (reply.ApplyUpdate)
        {
            var result = await Dispatcher.UIThread.InvokeAsync(() => _s.Updates.Apply("", origin, byPolicy: true));
            notes.Add($"apply: {result.Message}");
        }
        else if (reply.Restart)
        {
            var result = await Dispatcher.UIThread.InvokeAsync(() => _s.Actions.Execute(new ShowAction(ShowActionKind.Restart, cfg.AdminPasscode), origin));
            notes.Add($"restart: {result.Message}");
        }
        var summary = reply.Commands.Count == 0 ? "nothing to do" : $"{reply.Commands.Count} command{(reply.Commands.Count == 1 ? "" : "s")}";
        if (reply.Note.Length > 0) notes.Add(reply.Note);
        _status = $"Checked in at {DateTime.Now:HH:mm} — {summary}{(notes.Count > 0 ? "; " + string.Join("; ", notes) : "")}.";
    }

    /// <summary>Downloads an offered package into the updates folder and keeps it only when its SHA-256 is the one promised.</summary>
    private async Task<string> StageAsync(ManagementUpdate update)
    {
        var folder = _s.Updates.Folder;
        Directory.CreateDirectory(folder);
        var name = $"patterns-update-{Sanitise(update.Version)}.zip";
        var target = Path.Combine(folder, name);
        if (File.Exists(target) && CheckIn.Sha256Of(target) == update.Sha256) return $"update {update.Version} already staged";
        var tmp = target + ".part";
        try
        {
            using (var response = await Http.GetAsync(update.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = File.Create(tmp);
                await stream.CopyToAsync(file);
            }
            var hash = CheckIn.Sha256Of(tmp);
            if (hash != update.Sha256)
            {
                File.Delete(tmp);
                return $"update {update.Version} refused — its SHA-256 is not the one promised";
            }
            File.Move(tmp, target, overwrite: true);
            await Dispatcher.UIThread.InvokeAsync(_s.Updates.Scan);
            Log.Info($"Update {update.Version} staged from the management server: {target}");
            return $"update {update.Version} staged";
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { /* nothing to clean */ }
            Log.Warn($"Update {update.Version} could not be downloaded.", ex);
            return $"update {update.Version} could not be downloaded — {ex.Message}";
        }
    }

    private static string Sanitise(string version)
    {
        var sb = new StringBuilder();
        foreach (var ch in version) sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        return sb.Length == 0 ? "next" : sb.ToString();
    }

    public void Dispose()
    {
        // The shared client outlives the service on purpose: a check-in in flight finishes or times out on its own.
    }
}
