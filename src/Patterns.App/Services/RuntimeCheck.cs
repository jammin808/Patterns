using System.Runtime.InteropServices;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// <c>Patterns.exe --verify-runtime</c> (round 64): the built exe proves its own runtime without
/// opening a window — Skia draws and reads a pixel back, libVLC loads and a LibVLC instance is
/// made from the folder beside the exe (or the machine's), the audio endpoints enumerate on
/// Windows, and the modules report what this process loaded. The report goes to the console and,
/// with <c>--report &lt;path&gt;</c>, to a file; the exit code is 0 when everything the check
/// requires is there — libVLC only with <c>--require-vlc</c>, which the full bundle's CI run
/// passes — and 1 otherwise. A wrong or missing native runtime fails the build that made it,
/// instead of a show.
/// </summary>
public static class RuntimeCheck
{
    public static int Run(string[] args)
    {
        var requireVlc = Array.IndexOf(args, "--require-vlc") >= 0;
        var at = Array.IndexOf(args, "--report");
        var report = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        var lines = new List<string>();
        var ok = true;

        var version = typeof(RuntimeCheck).Assembly.GetName().Version?.ToString() ?? "dev";
        lines.Add($"Patterns runtime check · {version} · .NET {Environment.Version} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}");
        lines.Add($"Exe: {Environment.ProcessPath ?? "(unknown)"}");

        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(new SKColor(200, 30, 30));
            using var image = surface.Snapshot();
            using var pixels = image.PeekPixels();
            var pixel = pixels.GetPixelColor(3, 3);
            if (pixel.Red != 200 || pixel.Green != 30) { ok = false; lines.Add($"Skia: FAILED — drew red, read {pixel}"); }
            else lines.Add("Skia: ok — drew and read a pixel back");
        }
        catch (Exception ex)
        {
            ok = false;
            lines.Add($"Skia: FAILED — {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var vlc = VlcRuntime.Create(out var failure);
            if (vlc is null)
            {
                lines.Add($"libVLC: not loaded — {failure}");
                if (requireVlc) ok = false;
            }
            else
            {
                lines.Add($"libVLC: ok — {vlc.Version}");
                vlc.Dispose();
            }
        }
        catch (Exception ex)
        {
            lines.Add($"libVLC: FAILED — {ex.GetType().Name}: {ex.Message}");
            if (requireVlc) ok = false;
        }

        if (OperatingSystem.IsWindows())
        {
            try
            {
                lines.Add(AudioEndpoints());
            }
            catch (Exception ex)
            {
                ok = false;
                lines.Add($"Audio endpoints: FAILED — {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            lines.Add("Audio endpoints: not on this platform (Windows only)");
        }

        lines.Add(Modules.Words());
        lines.Add(ok ? "RUNTIME OK" : "RUNTIME FAILED");

        var text = string.Join(Environment.NewLine, lines);
#pragma warning disable RS0030 // --verify-runtime is a console verdict by design: it prints
        Console.WriteLine(text);
        if (report is not null)
        {
            try { File.WriteAllText(report, text + Environment.NewLine); }
            catch (Exception ex) { Console.WriteLine($"(the report could not be written to {report}: {ex.Message})"); }
        }
#pragma warning restore RS0030
        return ok ? 0 : 1;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string AudioEndpoints()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var render = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).Count;
        var capture = enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active).Count;
        return $"Audio endpoints: ok — {render} render, {capture} capture";
    }
}
