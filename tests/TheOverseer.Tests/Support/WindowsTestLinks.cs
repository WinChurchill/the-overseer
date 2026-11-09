using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TheOverseer.Tests.Support;

internal static class WindowsTestLinks
{
    public static async Task CreateJunctionAsync(string link, string target)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:OVERSEER_TEST_LINK -Target $env:OVERSEER_TEST_TARGET | Out-Null");
        start.Environment["OVERSEER_TEST_LINK"] = link;
        start.Environment["OVERSEER_TEST_TARGET"] = target;
        using var process = Process.Start(start)!;
        try
        {
            // Fixture setup may hit a cold PowerShell startup. Do not leave a
            // late writer alive while the owned fixture is being cleaned up.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    public static void CreateHardLink(string link, string target) =>
        Assert.True(CreateHardLinkW(link, target, IntPtr.Zero), $"Fixture hard link failed: {Marshal.GetLastWin32Error()}");

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string target, IntPtr securityAttributes);
}
