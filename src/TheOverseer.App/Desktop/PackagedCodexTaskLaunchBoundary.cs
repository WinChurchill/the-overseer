using System.Runtime.InteropServices;

namespace TheOverseer.App.Desktop;

/// <summary>
/// Windows Store protocol activation, used only when the ordinary shell has no
/// association. The app identity comes from the installed OpenAI.Codex manifest;
/// it has no version/path dependence and changes no Windows registration.
/// </summary>
internal sealed class PackagedCodexTaskLaunchBoundary : ITaskLaunchBoundary
{
    internal const string AppUserModelId = "OpenAI.Codex_2p2nqsd0c76g0!App";
    private static readonly Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid ShellItemArrayId = new("b63ea76d-1f85-456f-a19c-48159efa858b");

    public bool TryLaunch(Uri uri, out string? errorCode)
    {
        // Defence at the OS boundary: only the exact validated task route belongs
        // here, even if a future caller bypasses CodexTaskNavigator.
        if (!uri.IsAbsoluteUri || uri.Scheme != "codex" || uri.Host != "threads" ||
            uri.UserInfo.Length != 0 || uri.Port != -1 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/" + uri.AbsolutePath.TrimStart('/') ||
            !CodexTaskNavigator.IsSafeConversationId(uri.AbsolutePath.TrimStart('/')))
        {
            errorCode = "invalid-uri";
            return false;
        }

        IntPtr item = IntPtr.Zero;
        IntPtr array = IntPtr.Zero;
        object? activation = null;
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(uri.AbsoluteUri,
                IntPtr.Zero, in ShellItemId, out item));
            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromShellItem(item,
                in ShellItemArrayId, out array));
            var type = Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c"), throwOnError: true)!;
            activation = Activator.CreateInstance(type)!;
            var result = ((IApplicationActivationManager)activation).ActivateForProtocol(AppUserModelId, array, out _);
            Marshal.ThrowExceptionForHR(result);
            errorCode = null;
            return true;
        }
        catch (Exception exception) when (exception is COMException or FileNotFoundException)
        {
            // No package/association is distinct from other activation failures.
            errorCode = exception.HResult is unchecked((int)0x80070483) or unchecked((int)0x80073CF1)
                ? "no-handler" : "packaged-activation-failed";
            return false;
        }
        finally
        {
            if (activation is not null && Marshal.IsComObject(activation)) Marshal.ReleaseComObject(activation);
            if (array != IntPtr.Zero) Marshal.Release(array);
            if (item != IntPtr.Zero) Marshal.Release(item);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, in Guid interfaceId, out IntPtr item);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHCreateShellItemArrayFromShellItem(IntPtr item, in Guid interfaceId, out IntPtr array);

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);

        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr items,
            [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appId, IntPtr items, out uint processId);
    }
}
