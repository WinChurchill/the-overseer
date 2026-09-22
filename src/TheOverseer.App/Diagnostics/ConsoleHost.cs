using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TheOverseer.App.Diagnostics;

/// <summary>
/// Console support for a WPF WinExe debug run. Standard handles are used when
/// they already exist (redirected output), otherwise the parent console is
/// attached, and only as a last resort a new console is allocated.
/// </summary>
internal static class ConsoleHost
{
    private const int StdOutputHandle = -11;
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static bool Ensure()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == InvalidHandleValue)
            {
                if (!AttachConsole(AttachParentProcess))
                {
                    _ = AllocConsole();
                }
            }

            var stdout = Console.OpenStandardOutput();
            var writer = new StreamWriter(stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
            Console.SetOut(writer);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}
