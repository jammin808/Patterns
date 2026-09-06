using Avalonia.Threading;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The desk's last line. An exception on the UI thread — a page that fails to lay out as it comes
/// in, a button whose handler throws, a timer's tick — used to end the process (the runtime's exit
/// code 0xE0434352) and the watchdog brought the desk back a few seconds later with the show
/// interrupted. Now it is caught at the dispatcher, logged with its stack, counted on the health
/// line with the exception's own words, and contained: the outputs keep rendering, the desk stays
/// up, and the operator reads what happened on the status line and the Machine page. Buttons and
/// keys go through <see cref="Guard"/> as well, since input handlers run outside the dispatcher's
/// jobs. What no handler can contain (the runtime out of memory, a native fault) still ends the
/// run — and then <see cref="NoteFatal"/> leaves the crash note with the exception's words for
/// the next start's health line, beside the exit code the supervisor sees.
/// </summary>
public static class UiFaults
{
    private static readonly object Gate = new();
    private static Dispatcher? _installedOn;
    private static long _contained;

    /// <summary>How many faults this run contained.</summary>
    public static long Contained => Interlocked.Read(ref _contained);

    /// <summary>The last contained fault in one line (type, message, the app's frames); "" for none.</summary>
    public static string LastWords { get; private set; } = "";

    /// <summary>Told about every contained fault (the desk puts it on its status line). Set by the view model that owns the desk.</summary>
    public static Action<string>? Listener { get; set; }

    /// <summary>Hooks the UI dispatcher once (per dispatcher: the headless tests make a fresh one for every test); a second call does nothing.</summary>
    public static void Install()
    {
        try
        {
            var dispatcher = Dispatcher.UIThread;
            lock (Gate)
            {
                if (ReferenceEquals(_installedOn, dispatcher)) return;
                _installedOn = dispatcher;
            }
            dispatcher.UnhandledExceptionFilter += (_, e) =>
            {
                if (IsFatal(e.Exception)) e.RequestCatch = false;
            };
            dispatcher.UnhandledException += (_, e) =>
            {
                if (IsFatal(e.Exception)) return;
                Contain(e.Exception, "a dispatcher job");
                e.Handled = true;
            };
        }
        catch (Exception ex)
        {
            Log.Warn("UI fault containment could not be installed.", ex);
        }
    }

    /// <summary>Runs a handler; a fault in it is contained and false comes back. The place is for the log ("a command", "the page switch", "a key").</summary>
    public static bool Guard(Action body, string where)
    {
        try
        {
            body();
            return true;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            Contain(ex, where);
            return false;
        }
    }

    /// <summary>Logs the fault with its stack, counts it on the health line, and tells the desk.</summary>
    public static void Contain(Exception ex, string where)
    {
        var words = FaultWords.Describe(ex);
        Interlocked.Increment(ref _contained);
        LastWords = words;
        // Log.Error is the health counter: the line reads "1 fault caught, show kept running (last hh:mm — UI fault contained …)".
        Log.Error($"UI fault contained ({where}) — {words}", ex);
        try
        {
            Listener?.Invoke(words);
        }
        catch
        {
            // The listener is the status line; a fault there must not become a second fault here.
        }
    }

    /// <summary>What no handler should swallow: the process is not sound after these.</summary>
    public static bool IsFatal(Exception ex)
    {
        var inner = FaultWords.Unwrap(ex);
        return inner is OutOfMemoryException or StackOverflowException or AccessViolationException
            or InvalidProgramException or BadImageFormatException or System.Runtime.InteropServices.SEHException;
    }

    /// <summary>
    /// On the way down: the crash note with the exception's words, so the next start's health line
    /// reads what threw and where. The supervisor writes its own note from the exit code after the
    /// process has gone and keeps these words when it finds them.
    /// </summary>
    public static void NoteFatal(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var directory = AppServices.Instance?.Store.BaseDirectory ?? new SettingsStore().BaseDirectory;
            var ran = (DateTime.UtcNow - HealthMonitor.StartedUtc).TotalSeconds;
            CrashMarker.Write(directory, new CrashNote(ExitCodes.ClrException, ExitCodes.Describe(ExitCodes.ClrException), false, false,
                DateTime.UtcNow, ran, "", 0, FaultWords.Describe(ex)));
        }
        catch
        {
            // Best-effort, like the log.
        }
    }

    /// <summary>Tests only: the counters back to a fresh run.</summary>
    public static void Reset()
    {
        Interlocked.Exchange(ref _contained, 0);
        LastWords = "";
    }
}
