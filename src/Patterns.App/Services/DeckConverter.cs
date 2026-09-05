using System.Diagnostics;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Runs LibreOffice Impress headless to turn a PowerPoint, Keynote or Impress deck into a PDF —
/// once per version of the file — into a cache under Patterns' folder (decks/). A conversion
/// never blocks the desk: the engine mounts a pending deck meanwhile and the PDF takes its place
/// when it lands. One conversion runs at a time; LibreOffice is heavy and a show machine is busy.
/// </summary>
public sealed class DeckConverter
{
    /// <summary>What a conversion came to: the cached PDF, or why there is none.</summary>
    public readonly record struct Result(bool Ok, string PdfPath, string Message);

    /// <summary>The process: LibreOffice's path and arguments in, whether it finished and its last word out. Tests stand in for it.</summary>
    public delegate Task<(bool Ok, string Message)> Runner(string exe, IReadOnlyList<string> args, CancellationToken ct);

    private readonly string _cacheDir;
    private readonly string _profileDir;
    private readonly string _workDir;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, Task<Result>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private string? _probe;
    private DateTime _probedUtc = DateTime.MinValue;
    private int _conversions;

    public DeckConverter(string baseDirectory)
    {
        _cacheDir = Path.Combine(baseDirectory, "decks");
        _profileDir = Path.Combine(_cacheDir, "lo-profile");
        _workDir = Path.Combine(_cacheDir, "work");
    }

    /// <summary>Tests stand in for the process: the exe and the arguments in, whether the PDF was written out.</summary>
    public Runner? RunnerOverride { get; set; }

    /// <summary>Tests stand in for the search; the real one asks the settings, the app folder, the install folders and the PATH.</summary>
    public Func<string?>? Locator { get; set; }

    /// <summary>The operator's own path to LibreOffice (Admin → LibreOffice), read whenever the search runs.</summary>
    public Func<string>? ConfiguredPath { get; set; }

    /// <summary>How long a deck may take; a 300-page deck with pictures converts in well under a minute on a show machine.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(180);

    /// <summary>Where the converted PDFs live.</summary>
    public string CacheDirectory => _cacheDir;

    /// <summary>How many conversions have run in this session (tests count them).</summary>
    public int Conversions => _conversions;

    /// <summary>LibreOffice's executable, or null; the search repeats at most every 20 s, so a fresh install shows up without a restart.</summary>
    public string? LibreOffice
    {
        get
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _probedUtc < TimeSpan.FromSeconds(20)) return _probe;
                _probedUtc = DateTime.UtcNow;
            }
            var found = Locator is { } custom ? custom() : Find();
            lock (_gate) _probe = found;
            return found;
        }
    }

    /// <summary>What the desk says about the converter: found where, or what to do.</summary>
    public string Describe() => DeckConversion.Describe(LibreOffice);

    /// <summary>Searches again on the next ask — after the operator changes the path setting.</summary>
    public void ForgetProbe()
    {
        lock (_gate) _probedUtc = DateTime.MinValue;
    }

    private string? Find()
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var candidates = DeckConversion.Candidates(
            ConfiguredPath?.Invoke(),
            appDir,
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows());
        return DeckConversion.FindLibreOffice(File.Exists, candidates);
    }

    /// <summary>The cached PDF's path for a source as it is now (its size and last write), or null when the source is missing.</summary>
    public string? CachePathFor(string source)
    {
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists) return null;
            return Path.Combine(_cacheDir, DeckConversion.CacheName(info.FullName, info.Length, info.LastWriteTimeUtc));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The cached PDF when the source is already converted as it is now; null otherwise.</summary>
    public string? Cached(string source)
    {
        var path = CachePathFor(source);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Drops the cached PDF for a source so the next mount converts it again (RELOAD on the desk).</summary>
    public void Forget(string source)
    {
        try
        {
            var path = CachePathFor(source);
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Deck cache entry could not be dropped for {source}.", ex);
        }
    }

    /// <summary>Converts a source, or joins the conversion already running for it.</summary>
    public Task<Result> ConvertAsync(string source)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(source, out var running) && !running.IsCompleted) return running;
            var task = Task.Run(() => ConvertCoreAsync(source));
            _inFlight[source] = task;
            return task;
        }
    }

    private async Task<Result> ConvertCoreAsync(string source)
    {
        var name = Path.GetFileName(source);
        var kind = DeckConversion.KindOf(source);
        FileInfo info;
        try
        {
            info = new FileInfo(source);
        }
        catch (Exception ex)
        {
            return new Result(false, "", $"Deck not found: {name} ({ex.Message})");
        }
        if (!info.Exists) return new Result(false, "", $"Deck not found: {name}");
        var cachePath = Path.Combine(_cacheDir, DeckConversion.CacheName(info.FullName, info.Length, info.LastWriteTimeUtc));
        if (File.Exists(cachePath)) return new Result(true, cachePath, "");
        var exe = LibreOffice;
        if (exe is null) return new Result(false, "", DeckConversion.NotFoundNote);

        await _oneAtATime.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(cachePath)) return new Result(true, cachePath, ""); // converted while this one waited
            Directory.CreateDirectory(_cacheDir);
            Directory.CreateDirectory(_profileDir);
            var outDir = Path.Combine(_workDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);
            try
            {
                Interlocked.Increment(ref _conversions);
                var args = DeckConversion.Arguments(info.FullName, outDir, _profileDir);
                using var cts = new CancellationTokenSource(Timeout);
                var started = Stopwatch.StartNew();
                var (ok, message) = await (RunnerOverride ?? RunProcessAsync)(exe, args, cts.Token).ConfigureAwait(false);
                var produced = Path.Combine(outDir, DeckConversion.ProducedName(info.FullName));
                if (!File.Exists(produced))
                {
                    var why = message.Length > 0 ? message : ok ? "LibreOffice wrote no PDF." : "LibreOffice did not finish.";
                    Log.Warn($"Deck conversion failed for {name}: {why}");
                    return new Result(false, "", $"{name} could not be converted from {kind} — {why}");
                }
                File.Move(produced, cachePath, overwrite: true);
                Log.Info($"Deck converted: {name} → {Path.GetFileName(cachePath)} in {started.Elapsed.TotalSeconds:0.0} s.");
                return new Result(true, cachePath, "");
            }
            finally
            {
                try
                {
                    Directory.Delete(outDir, recursive: true);
                }
                catch
                {
                    // a work folder left behind is swept by the next conversion's parent create; never a failure
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Deck conversion crashed for {name}.", ex);
            return new Result(false, "", $"{name} could not be converted from {kind} — {ex.Message}");
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>The real process: started hidden, its output read to the end, stopped (with its children) past the timeout.</summary>
    private static async Task<(bool Ok, string Message)> RunProcessAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) return (false, "LibreOffice did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }
            return (false, "LibreOffice took too long and was stopped.");
        }
        var text = await stderr.ConfigureAwait(false) + "\n" + await stdout.ConfigureAwait(false);
        // javaldx's warning is noise (no Java is needed for a conversion); the last error line says what went wrong.
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.Contains("javaldx", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var error = lines.LastOrDefault(l => l.StartsWith("Error", StringComparison.OrdinalIgnoreCase));
        var ok = process.ExitCode == 0;
        var message = error ?? (ok ? "" : lines.LastOrDefault() ?? $"exit code {process.ExitCode}");
        return (ok, message);
    }
}
