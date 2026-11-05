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

    /// <summary>
    /// Finite production-path check: invented Codex and bridge producer files through
    /// the real readers, composite and controller, asserting the applied HUD state.
    /// </summary>
    public bool HudIntegrationCheck { get; private init; }

    /// <summary>
    /// Finite normal-composition check: the real HudComposition object graph, real
    /// startup discovery and provider selection, invented producers, asserting the
    /// applied HUD state for an empty first run and for valid mixed sources.
    /// </summary>
    public bool HudCompositionCheck { get; private init; }

    /// <summary>Hidden normal-composition stress check with invented producers and resource evidence.</summary>
    public bool HudEnduranceCheck { get; private init; }

    /// <summary>Finite Plan 3 voice smoke (silent unless --audible is given).</summary>
    public bool VoiceSmoke { get; private init; }

    public bool IncludeWindowsSpeech { get; private init; }

    /// <summary>Play through the real sound device during the voice smoke.</summary>
    public bool Audible { get; private init; }

    /// <summary>Silent check of an installed voice pack's shipped assets.</summary>
    public bool VoicePackCheck { get; private init; }

    public bool StartInTray { get; private init; }

    public bool NoTray { get; private init; }

    public bool RedactLabels { get; private init; }

    public TimeSpan Duration { get; private init; } = TimeSpan.FromSeconds(DefaultSmokeSeconds);

    public string? CodexHome { get; private init; }

    public string? DataDirectory { get; private init; }

    public string? OutputDirectory { get; private init; }

    /// <summary>
    /// Diagnostics/test placement: start the HUD window on this monitor device instead
    /// of the saved or primary one. It exists so automated checks can run on a display
    /// the user is not working on; it changes no setting and persists nothing.
    /// </summary>
    public string? PlacementMonitor { get; private init; }
    public bool AllowPrimaryPlacement { get; private init; }

    public bool IsFixtureInvocation =>
        HudFixture || HudSmoke || HudStartupCheck || HudIntegrationCheck || HudCompositionCheck || HudEnduranceCheck || VoiceSmoke || VoicePackCheck;

    /// <summary>
    /// Copy with different Codex home and data directory. Diagnostics checks invent both
    /// so they never read or write the user's real sessions or settings; production
    /// never calls this.
    /// </summary>
    internal StartupArguments WithPaths(string? codexHome, string? dataDirectory) => new()
    {
        HudFixture = HudFixture,
        HudSmoke = HudSmoke,
        HudStartupCheck = HudStartupCheck,
        HudIntegrationCheck = HudIntegrationCheck,
        HudCompositionCheck = HudCompositionCheck,
        HudEnduranceCheck = HudEnduranceCheck,
        VoiceSmoke = VoiceSmoke,
        IncludeWindowsSpeech = IncludeWindowsSpeech,
        Audible = Audible,
        VoicePackCheck = VoicePackCheck,
        StartInTray = StartInTray,
        NoTray = NoTray,
        RedactLabels = RedactLabels,
        Duration = Duration,
        CodexHome = codexHome,
        DataDirectory = dataDirectory,
        OutputDirectory = OutputDirectory,
        PlacementMonitor = PlacementMonitor,
        AllowPrimaryPlacement = AllowPrimaryPlacement,
    };

    public static bool TryParse(IReadOnlyList<string> args, out StartupArguments result, out string error)
    {
        result = new StartupArguments();
        error = string.Empty;
        var fixture = false;
        var smoke = false;
        var startupCheck = false;
        var integrationCheck = false;
        var compositionCheck = false;
        var enduranceCheck = false;
        var allowPrimaryPlacement = false;
        var voiceSmoke = false;
        var includeWindowsSpeech = false;
        var voicePackCheck = false;
        var audible = false;
        var startInTray = false;
        var noTray = false;
        var redact = false;
        var seconds = DefaultSmokeSeconds;
        string? codexHome = null;
        string? dataDirectory = null;
        string? outputDirectory = null;
        string? placementMonitor = null;

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
                case "--hud-integration-check":
                    integrationCheck = true;
                    break;
                case "--hud-composition-check":
                    compositionCheck = true;
                    break;
                case "--hud-endurance-check":
                    enduranceCheck = true;
                    break;
                case "--allow-primary-placement":
                    allowPrimaryPlacement = true;
                    break;
                case "--voice-smoke":
                    voiceSmoke = true;
                    break;
                case "--include-windows-speech":
                    includeWindowsSpeech = true;
                    break;
                case "--voice-pack-check":
                    voicePackCheck = true;
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
                case "--placement-monitor":
                    if (index + 1 >= args.Count)
                    {
                        error = "missing value for --placement-monitor";
                        return false;
                    }

                    placementMonitor = args[++index];
                    break;
                default:
                    error = "unknown argument: " + args[index];
                    return false;
            }
        }

        if (includeWindowsSpeech && !voiceSmoke)
        {
            error = "--include-windows-speech requires --voice-smoke";
            return false;
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
            HudIntegrationCheck = integrationCheck,
            HudCompositionCheck = compositionCheck,
            HudEnduranceCheck = enduranceCheck,
            VoiceSmoke = voiceSmoke,
            IncludeWindowsSpeech = includeWindowsSpeech,
            VoicePackCheck = voicePackCheck,
            Audible = audible,
            StartInTray = startInTray,
            NoTray = noTray,
            RedactLabels = redact,
            Duration = TimeSpan.FromSeconds(seconds),
            CodexHome = codexHome,
            DataDirectory = dataDirectory,
            OutputDirectory = outputDirectory,
            PlacementMonitor = placementMonitor,
            AllowPrimaryPlacement = allowPrimaryPlacement,
        };
        return true;
    }

    public const string Usage = @"usage: TheOverseer.App [--start-in-tray] [--data-directory PATH] [--codex-home PATH]
       TheOverseer.App --monitor-debug [--duration SECONDS] [--codex-home PATH] [--data-directory PATH] [--redact-labels]
       TheOverseer.App --hud-smoke [--duration SECONDS] [--data-directory PATH] [--no-tray]
       TheOverseer.App --hud-fixture --output-directory DIR [--data-directory PATH]
       TheOverseer.App --hud-startup-check   (reversible live check of OVERSEER's own Run entry)
       TheOverseer.App --hud-integration-check [--output-directory DIR]   (invented producers through the real monitor and HUD)
       TheOverseer.App --hud-composition-check [--output-directory DIR]   (the real startup composition and discovery, invented producers)
       TheOverseer.App --hud-endurance-check --start-in-tray --no-tray --duration 3600 --output-directory DIR
       TheOverseer.App --voice-smoke [--audible] [--include-windows-speech] [--data-directory PATH]
       TheOverseer.App --voice-pack-check [--data-directory PATH]

       --placement-monitor DEVICE   diagnostics/tests: start the HUD on this monitor
                                    (for example \\.\DISPLAY2) instead of the saved one
       --allow-primary-placement    composition check only: explicitly allow the primary display";
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
