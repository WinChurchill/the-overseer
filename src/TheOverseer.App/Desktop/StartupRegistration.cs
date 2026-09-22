using System.IO;
using Microsoft.Win32;

namespace TheOverseer.App.Desktop;

/// <summary>
/// Thin, injectable registry boundary. Tests use a fake store; the production
/// store touches only OVERSEER's own value under HKCU Run.
/// </summary>
public interface IStartupRegistry
{
    string? Read(string valueName);

    void Write(string valueName, string value);

    void Delete(string valueName);
}

public sealed class WindowsStartupRegistry : IStartupRegistry
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName) as string;
    }

    public void Write(string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void Delete(string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

public enum StartupRegistrationState
{
    Enabled,
    Disabled,
    StalePath,
    Unavailable,
}

public sealed record StartupRegistrationResult(StartupRegistrationState State, string ReasonCode);

/// <summary>
/// Owns exactly one current-user startup entry. The command line always uses a
/// quoted real executable path plus the documented hidden-start flag; bare
/// dotnet.exe is never registered.
/// </summary>
public sealed class StartupRegistration
{
    public const string ValueName = "OVERSEER";
    public const string StartHiddenFlag = "--start-in-tray";

    private readonly IStartupRegistry _registry;
    private readonly string _executablePath;

    public StartupRegistration(IStartupRegistry registry, string executablePath)
    {
        _registry = registry;
        _executablePath = executablePath ?? string.Empty;
    }

    /// <summary>Builds the exact command line; exposed for tests and diagnostics.</summary>
    public static string BuildCommand(string executablePath, string startHiddenFlag = StartHiddenFlag)
    {
        var trimmed = (executablePath ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var quoted = trimmed.StartsWith('"') && trimmed.EndsWith('"') ? trimmed : "\"" + trimmed + "\"";
        return quoted + " " + startHiddenFlag;
    }

    /// <summary>True when the executable path is usable for registration.</summary>
    public static bool IsUsableExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim().Trim('"');
        if (!trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(trimmed);
        return !fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    }

    public StartupRegistrationResult Read()
    {
        if (!IsUsableExecutablePath(_executablePath))
        {
            return new StartupRegistrationResult(StartupRegistrationState.Unavailable, "executable-unusable");
        }

        try
        {
            var existing = _registry.Read(ValueName);
            if (string.IsNullOrWhiteSpace(existing))
            {
                return new StartupRegistrationResult(StartupRegistrationState.Disabled, "not-registered");
            }

            var expected = BuildCommand(_executablePath);
            if (string.Equals(existing.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            {
                return new StartupRegistrationResult(StartupRegistrationState.Enabled, "registered");
            }

            return new StartupRegistrationResult(StartupRegistrationState.StalePath, "stale-path");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new StartupRegistrationResult(StartupRegistrationState.Unavailable, "registry-denied");
        }
    }

    /// <summary>Enables or disables the owned entry. Never throws.</summary>
    public StartupRegistrationResult Set(bool enabled)
    {
        if (!IsUsableExecutablePath(_executablePath))
        {
            return new StartupRegistrationResult(StartupRegistrationState.Unavailable, "executable-unusable");
        }

        try
        {
            if (enabled)
            {
                _registry.Write(ValueName, BuildCommand(_executablePath));
                return Read();
            }

            _registry.Delete(ValueName);
            return new StartupRegistrationResult(StartupRegistrationState.Disabled, "removed");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new StartupRegistrationResult(StartupRegistrationState.Unavailable, "registry-denied");
        }
    }
}