using System.Windows.Media.Imaging;
using TheOverseer.App;
using TheOverseer.Tests.Support;

namespace TheOverseer.Tests;

public sealed class AppIconTests
{
    [Fact]
    public void SuppliedArtworkIsEmbeddedAtWindowAndTraySizes()
    {
        StaTestHost.Invoke(() =>
        {
            using var stream = typeof(HudApplicationController).Assembly.GetManifestResourceStream("TheOverseer.App.Assets.overseer.ico");
            Assert.NotNull(stream);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, decoder.Frames.Select(frame => frame.PixelWidth));
            stream.Position = 0;
            using var trayIcon = new System.Drawing.Icon(stream, 32, 32);
            Assert.Equal(32, trayIcon.Width);
            Assert.Equal(32, trayIcon.Height);
        });
    }
}
