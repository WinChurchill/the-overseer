using System.IO;
using System.Diagnostics;
using TheOverseer.Core.Models;

namespace TheOverseer.App.Desktop;

public enum TaskNavigationOutcome
{
    Requested,
    UnsupportedSurface,
    UnsupportedIdentity,
    UnsupportedProfile,
    InvalidIdentifier,
    NoHandler,
    LaunchFailed,
}

public sealed record TaskNavigationResult(TaskNavigationOutcome Outcome, string UserMessage, string? Uri);

/// <summary>Injectable launch boundary; unit tests never start a real process.</summary>
public interface ITaskLaunchBoundary
{
    /// <summary>Returns null when no handler is registered for the scheme.</summary>
    bool TryLaunch(Uri uri, out string? errorCode);
}

public sealed class ShellTaskLaunchBoundary : ITaskLaunchBoundary
{
    public bool TryLaunch(Uri uri, out string? errorCode)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
            process?.Dispose();
            errorCode = null;
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            errorCode = "no-handler";
            return false;
        }
        catch (InvalidOperationException)
        {
            errorCode = "invalid-uri";
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            errorCode = "platform";
            return false;
        }
        catch (IOException)
        {
            errorCode = "io";
            return false;
        }
    }
}

/// <summary>
/// Builds and launches the validated Desktop route for one task. The route is
/// derived only from the conversation id; labels, paths and raw records never
/// participate, and no shell or task-control command is ever executed.
/// </summary>
public sealed class CodexTaskNavigator
{
    public const string UriPrefix = "codex://threads/";
    private const string DefaultProfileId = "codex";

    private readonly ITaskLaunchBoundary _boundary;

    public CodexTaskNavigator(ITaskLaunchBoundary boundary)
    {
        _boundary = boundary ?? throw new ArgumentNullException(nameof(boundary));
    }

    /// <summary>Validates and builds the route, or explains why it is unsupported.</summary>
    public static TaskNavigationResult BuildUri(AgentTask task, string profileId)
    {
        if (task.Surface != ClientSurface.Desktop)
        {
            return new TaskNavigationResult(
                TaskNavigationOutcome.UnsupportedSurface,
                "Open this task in Codex manually — this client has no supported route.",
                null);
        }

        if (task.IsProvisional)
        {
            return new TaskNavigationResult(
                TaskNavigationOutcome.UnsupportedIdentity,
                "Open this task in Codex manually — its identity is provisional.",
                null);
        }

        if (!string.Equals(profileId, DefaultProfileId, StringComparison.Ordinal))
        {
            return new TaskNavigationResult(
                TaskNavigationOutcome.UnsupportedProfile,
                "Open this task in Codex manually — this profile has no verified route.",
                null);
        }

        var id = task.Key.ConversationId;
        if (!IsSafeConversationId(id))
        {
            return new TaskNavigationResult(
                TaskNavigationOutcome.InvalidIdentifier,
                "Open this task in Codex manually — the identifier is not routable.",
                null);
        }

        var uri = UriPrefix + Uri.EscapeDataString(id);
        return new TaskNavigationResult(TaskNavigationOutcome.Requested, "Opening task in Codex…", uri);
    }

    /// <summary>Conversation ids are UUID-shaped; anything else is rejected.</summary>
    public static bool IsSafeConversationId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length is < 8 or > 64)
        {
            return false;
        }

        foreach (var character in id)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-')
            {
                return false;
            }
        }

        return true;
    }

    public TaskNavigationResult Navigate(AgentTask task, string profileId)
    {
        var built = BuildUri(task, profileId);
        if (built.Uri is null)
        {
            return built;
        }

        if (!_boundary.TryLaunch(new Uri(built.Uri), out var errorCode))
        {
            var outcome = string.Equals(errorCode, "no-handler", StringComparison.Ordinal)
                ? TaskNavigationOutcome.NoHandler
                : TaskNavigationOutcome.LaunchFailed;
            var message = outcome == TaskNavigationOutcome.NoHandler
                ? "Codex does not handle task links on this machine — open the task manually."
                : "Could not open the task in Codex — open it manually.";
            return new TaskNavigationResult(outcome, message, built.Uri);
        }

        // A successful shell request is not proof that the exact thread focused.
        return built with { UserMessage = "Open requested in Codex." };
    }
}