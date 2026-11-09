using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TheOverseer.App.Monitoring;

internal sealed class UnsafeSpoolPathException() : IOException("Bridge spool path is linked or not a regular file.");

/// <summary>Checks the bridge's configured root and owned paths before consuming files.</summary>
internal static class BridgeSpoolFiles
{
    public readonly record struct FileStamp(uint Volume, ulong FileIndex, long Length, long LastWrite);

    public static void CheckDirectories(string directory, int levels)
    {
        CheckDirectory(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), levels);
    }

    private static void CheckDirectory(string directory, int levels)
    {
        if (levels > 1 && Path.GetDirectoryName(directory) is { } parent)
            CheckDirectory(parent, levels - 1);
        try
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                throw new UnsafeSpoolPathException();
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    public static void CheckFile(string file)
    {
        if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new UnsafeSpoolPathException();
    }

    public static FileStream OpenRead(string file, bool publishingReceipt = false) =>
        OpenRead(file, out _, publishingReceipt);

    public static FileStream OpenRead(string file, out FileStamp stamp, bool publishingReceipt = false)
    {
        CheckFile(file);
        var stream = OpenShared(file);
        try
        {
            if (GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            {
                // The Claude producer briefly links a receipt to its staging
                // name. Confirm that the other name is the expected sibling.
                if (info.NumberOfLinks == 1 ||
                    (publishingReceipt && info.NumberOfLinks == 2 &&
                     (MatchesStaging(file + ".tmp", info) ||
                      (GetFileInformationByHandle(stream.SafeFileHandle, out info) && info.NumberOfLinks == 1))))
                {
                    stamp = new FileStamp(info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow,
                        ((long)info.SizeHigh << 32) | info.SizeLow,
                        ((long)info.LastWrite.dwHighDateTime << 32) | (uint)info.LastWrite.dwLowDateTime);
                    return stream;
                }
            }
            throw new UnsafeSpoolPathException();
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static FileStream OpenShared(string file) =>
        new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static bool MatchesStaging(string file, FileInformation receipt)
    {
        try
        {
            CheckFile(file);
            using var staging = OpenShared(file);
            return GetFileInformationByHandle(staging.SafeFileHandle, out var info) &&
                info.Volume == receipt.Volume && info.IndexHigh == receipt.IndexHigh && info.IndexLow == receipt.IndexLow;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccess;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWrite;
        public uint Volume;
        public uint SizeHigh;
        public uint SizeLow;
        public uint NumberOfLinks;
        public uint IndexHigh;
        public uint IndexLow;
    }
}
