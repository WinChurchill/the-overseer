using System.Windows;
using TheOverseer.Core.Configuration;

namespace TheOverseer.UI;

/// <summary>
/// Loads and applies the one HUD palette. Swapping the dictionary changes
/// brushes only; the single HudWindow layout tree is never rebuilt.
/// </summary>
public static class HudThemeManager
{
    public const string LightSource = "pack://application:,,,/TheOverseer.UI;component/Themes/WebsiteLight.xaml";
    public const string DarkSource = "pack://application:,,,/TheOverseer.UI;component/Themes/WebsiteDark.xaml";

    public static string SourceFor(HudTheme theme) => theme == HudTheme.Light ? LightSource : DarkSource;

    public static ResourceDictionary Load(HudTheme theme) => new()
    {
        Source = new Uri(SourceFor(theme), UriKind.Absolute),
    };

    /// <summary>
    /// Replaces the HUD palette inside the application resource chain. Returns
    /// the dictionary that was inserted so callers can remove it on shutdown.
    /// </summary>
    public static ResourceDictionary Apply(Application application, HudTheme theme)
    {
        ArgumentNullException.ThrowIfNull(application);
        var dictionaries = application.Resources.MergedDictionaries;
        ResourceDictionary? existing = null;
        foreach (var dictionary in dictionaries)
        {
            if (IsHudPalette(dictionary))
            {
                existing = dictionary;
                break;
            }
        }

        var palette = Load(theme);
        if (existing is not null)
        {
            var index = dictionaries.IndexOf(existing);
            dictionaries[index] = palette;
        }
        else
        {
            dictionaries.Insert(0, palette);
        }

        return palette;
    }

    private static bool IsHudPalette(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.ToString() ?? string.Empty;
        return source.Contains("Themes/WebsiteLight.xaml", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("Themes/WebsiteDark.xaml", StringComparison.OrdinalIgnoreCase);
    }
}
