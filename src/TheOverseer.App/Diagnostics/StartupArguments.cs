using System.IO;
using System.Globalization;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Argument parsing for normal HUD mode and the HUD diagnostics fixtures. The
/// console-only monitor debug harness keeps its own reviewed parser.
/// </summary>
public sealed class StartupArguments
{
    public const int DefaultSmokeSeconds = 25;
    public const int MaximumDurationSeconds = 3600;

    public bool HudFixture { get; private init; }

    public bool HudSmoke { get; private init; }

    /// <summary>Explicit, reversible live check of the owned startup entry.</summary>
    public bool HudStartupCheck { get; private init; }

    /// <summary>Finite Plan 3 voice smoke (silent unless --audible is given).</summary>
    public bool VoiceSmoke { get; private init; }

    /// <summary>Play through the real sound device during the voice smoke.</summary>
    public bool Audible { get; private init; }

    public bool StartInTray { get; private init; }

    public bool NoTray { get; private init; }

    public bool RedactLabels { get; private init; }

    public TimeSpan Duration { get; private init; } = TimeSpan.FromSeconds(DefaultSmokeSeconds);

    public string? CodexHome { get; private init; }

    public string? DataDirectory { get; private init; }

    public string? OutputDirectory { get; private init; }

    public bool IsFixtureInvocation => HudFixture || HudSmoke || HudStartupCheck || VoiceSmoke;

    public static bool TryParse(IReadOnlyList<string> args, out StartupArguments result, out string error)
    {
        result = new StartupArguments();
        error = string.Empty;
        var fixture = false;
        var smoke = false;
        var startupCheck = false;
        var voiceSmoke = false;
        var audible = false;
        var startInTray = false;
        var noTray = false;
        var redact = false;
        var seconds = DefaultSmokeSeconds;
        string? codexHome = null;
        string? dataDirectory = null;
        string? outputDirectory = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--hud-fixture":
                    fixture = true;
                    break;
                case "--hud-smoke":
                    smoke = true;
                    break;
                case "--hud-startup-check":
                    startupCheck = true;
                    break;
                case "--voice-smoke":
                    voiceSmoke = true;
                    break;
                case "--audible":
                    audible = true;
                    break;
                case "--start-in-tray":
                    startInTray = true;
                    break;
                case "--no-tray":
                    noTray = true;
                    break;
                case "--redact-labels":
                    redact = true;
                    break;
                case "--duration":
                    if (index + 1 >= args.Count ||
                        !int.TryParse(args[++index], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                    {
                        error = "invalid value for --duration";
                        return false;
                    }

                    break;
                case "--codex-home":
                    if (index + 1 >= args.Count)
                    {
                        error = "missing value for --codex-home";
                        return false;
                    }

                    codexHome = args[++index];
                    break;
                case "--data-directory":
                    if (index + 1 >= args.Count)
                    {
                        error = "missing value for --data-directory";
                        return false;
                    }

                    dataDirectory = args[++index];
                    break;
                case "--output-directory":
                    if (index + 1 >= args.Count)
                    {
                        error = "missing value for --output-directory";
                        return false;
                    }

                    outputDirectory = args[++index];
                    break;
                default:
                    error = "unknown argument: " + args[index];
                    return false;
            }
        }

        if (seconds < 1 || seconds > MaximumDurationSeconds)
        {
            error = "duration must be between 1 and " + MaximumDurationSeconds.ToString(CultureInfo.InvariantCulture) + " seconds";
            return false;
        }

        result = new StartupArguments
        {
            HudFixture = fixture,
            HudSmoke = smoke,
            HudStartupCheck = startupCheck,
            VoiceSmoke = voiceSmoke,
            Audible = audible,
            StartInTray = startInTray,
            NoTray = noTray,
            RedactLabels = redact,
            Duration = TimeSpan.FromSeconds(seconds),
            CodexHome = codexHome,
            DataDirectory = dataDirectory,
            OutputDirectory = outputDirectory,
        };
        return true;
    }

    public const string Usage = @"usage: TheOverseer.App [--start-in-tray] [--data-directory PATH] [--codex-home PATH]
       TheOverseer.App --monitor-debug [--duration SECONDS] [--codex-home PATH] [--data-directory PATH] [--redact-labels]
       TheOverseer.App --hud-smoke [--duration SECONDS] [--data-directory PATH] [--no-tray]
       TheOverseer.App --hud-fixture --output-directory DIR [--data-directory PATH]
       TheOverseer.App --hud-startup-check   (reversible live check of OVERSEER's own Run entry)
       TheOverseer.App --voice-smoke [--audible] [--data-directory PATH]";
}

public static class DataDirectoryResolver
{
    /// <summary>Explicit override wins, then %LOCALAPPDATA%\OVERSEER.</summary>
    public static string Resolve(string? explicitDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            return Path.GetFullPath(explicitDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OVERSEER");
    }
}