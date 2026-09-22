using System.IO;
using TheOverseer.Core.Configuration;

namespace TheOverseer.App;

public sealed record HudSaveResult(bool StateSaved, bool SettingsSaved);

/// <summary>Synchronous disk boundary, called only by the controller's single background writer.</summary>
public interface IHudPersistence
{
    HudSaveResult Save(HudControllerPaths paths, HudWindowState state, MonitorConfig config);
}

internal sealed class FileHudPersistence : IHudPersistence
{
    public HudSaveResult Save(HudControllerPaths paths, HudWindowState state, MonitorConfig config)
    {
        var stateSaved = HudStateStore.TrySave(paths.StatePath, state);
        try
        {
            MonitorConfigStore.Save(paths.ConfigPath, config);
            return new(stateSaved, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(stateSaved, false);
        }
    }
}
