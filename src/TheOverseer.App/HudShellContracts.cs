using System.Runtime.InteropServices;
using TheOverseer.App.Desktop;
using TheOverseer.Core.Models;

namespace TheOverseer.App;

public interface IStartupRegistration
{
    StartupRegistrationResult Read();

    StartupRegistrationResult Set(bool enabled);
}

public interface ITaskNavigator
{
    TaskNavigationResult Navigate(AgentTask task, string profileId);
}

public interface IClipboardWriter
{
    bool TrySetText(string text);
}

public sealed class WpfClipboardWriter : IClipboardWriter
{
    public bool TrySetText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}

internal sealed class StartupRegistrationAdapter : IStartupRegistration
{
    private readonly StartupRegistration _registration;

    public StartupRegistrationAdapter(StartupRegistration registration) => _registration = registration;

    public StartupRegistrationResult Read() => _registration.Read();

    public StartupRegistrationResult Set(bool enabled) => _registration.Set(enabled);
}

internal sealed class TaskNavigatorAdapter : ITaskNavigator
{
    private readonly CodexTaskNavigator _navigator;

    public TaskNavigatorAdapter(CodexTaskNavigator navigator) => _navigator = navigator;

    public TaskNavigationResult Navigate(AgentTask task, string profileId) => _navigator.Navigate(task, profileId);
}

