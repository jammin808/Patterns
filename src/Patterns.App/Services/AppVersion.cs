namespace Patterns.App.Services;

/// <summary>This build's version, as the wire, the beacon and the pages say it: the assembly's, "dev" for a build without one.</summary>
public static class AppVersion
{
    public static string Current { get; } = typeof(AppVersion).Assembly.GetName().Version?.ToString() ?? "dev";
}
