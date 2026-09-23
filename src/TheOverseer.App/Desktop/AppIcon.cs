using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TheOverseer.App.Desktop;

/// <summary>The user's supplied artwork, shared by the window and tray.</summary>
internal static class AppIcon
{
    private const string ResourceName = "TheOverseer.App.Assets.overseer.ico";
    private static readonly Lazy<ImageSource> WindowSource = new(() =>
    {
        using var stream = Open();
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.MaxBy(frame => frame.PixelWidth)!;
        frame.Freeze();
        return frame;
    });

    public static ImageSource WindowImage => WindowSource.Value;

    public static Icon CreateTrayIcon()
    {
        using var stream = Open();
        using var icon = new Icon(stream, 32, 32);
        return (Icon)icon.Clone();
    }

    private static Stream Open() => typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException("OVERSEER icon resource is missing.");
}
