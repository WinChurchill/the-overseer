using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using TheOverseer.Core.Models;
using TheOverseer.Core.Monitoring;

namespace TheOverseer.App.Monitoring;

/// <summary>
/// Read-only Windows UI Automation client. It never invokes a control, activates
/// a window, reads an edit value or walks a conversation feed. COM calls are
/// confined to the adapter's single bounded worker, independent of the HUD thread.
/// </summary>
public sealed class ClaudeProjectsDesktopReader : IDisposable
{
    private object? _client;
    private IAutomation? _automation;
    private IWalker? _walker;
    private IWalker? _titleWalker;
    private readonly Dictionary<IntPtr, IElement> _roots = new();
    private readonly StructureSink _sink = new();

    public ClaudeProjectRead Read(SourceBudget budget, DateTimeOffset now)
    {
        try
        {
            if (_automation is null)
            {
                var type = Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"), throwOnError: true)!;
                _client = Activator.CreateInstance(type)!;
                try
                {
                    var automation = (IAutomation)_client;
                    // Filter presentation-only wrappers so the existing depth/
                    // byte budgets reach the document. The persistent event
                    // subscription keeps loaded background accessibility alive.
                    automation.GetControlViewWalker(out var walker);
                    _walker = walker;
                    automation.GetRawViewWalker(out var titleWalker);
                    _titleWalker = titleWalker;
                    _automation = automation;
                }
                catch { Release(_walker); Release(_titleWalker); Release(_client); _walker = null; _titleWalker = null; _client = null; throw; }
            }
            using var identity = WindowsIdentity.GetCurrent();
            var scope = identity.User?.Value ?? "windows-local";
            var parser = new ClaudeOverviewParser(scope, now, budget.BytesPerPass, budget.MaximumSessions);
            return parser.Read(Roots());
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(Array.Empty<ClaudeProjectObservation>(), "projects-accessibility-unavailable");
        }
    }

    private IEnumerable<IClaudeOverviewNode> Roots()
    {
        var processes = Process.GetProcessesByName("Claude");
        var ids = new HashSet<int>();
        try
        {
            foreach (var process in processes.Take(64))
            {
                string? executable;
                try { executable = process.MainModule?.FileName; }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { continue; }
                if (executable is null || !string.Equals(Path.GetFileName(executable), "Claude.exe", StringComparison.OrdinalIgnoreCase)) continue;
                ids.Add(process.Id);
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }

        // MainWindowHandle returns only one window per process. Enumerating
        // application-owned top-level views also includes occluded/popout windows.
        // No other application's title, UI tree or contents are queried.
        var handles = new List<IntPtr>(4);
        var checkedWindows = 0;
        EnumWindows((handle, _) =>
        {
            if (++checkedWindows > 4096) return false;
            GetWindowThreadProcessId(handle, out var pid);
            if (!ids.Contains((int)pid)) return true;
            var name = new StringBuilder(128);
            if (GetClassName(handle, name, name.Capacity) > 0 && name.ToString() == "Chrome_WidgetWin_1") handles.Add(handle);
            return handles.Count < 4;
        }, IntPtr.Zero);
        foreach (var handle in _roots.Keys.Except(handles).ToArray())
        {
            try { _automation!.RemoveStructureChangedEventHandler(_roots[handle], _sink); }
            catch (COMException) { }
            Release(_roots[handle]); _roots.Remove(handle);
        }
        foreach (var handle in handles)
        {
            _automation!.ElementFromHandle(handle, out var element);
            var retained = false;
            if (!_roots.ContainsKey(handle))
            {
                // Keeping a UIA subscription alive lets Chromium maintain its
                // accessibility tree while the user works in another window.
                try { _automation.AddStructureChangedEventHandler(element, 7, IntPtr.Zero, _sink); }
                catch (COMException) { Release(element); throw; }
                _roots.Add(handle, element); retained = true;
            }
            yield return new Node(element, _walker!, _titleWalker!, ownsElement: !retained);
        }
    }

    public void Dispose()
    {
        try { _automation?.RemoveAllEventHandlers(); } catch (COMException) { }
        foreach (var root in _roots.Values) Release(root);
        _roots.Clear(); Release(_walker); Release(_titleWalker); Release(_client);
        _walker = null; _titleWalker = null; _automation = null; _client = null;
    }

    [ComVisible(true), Guid("e81d1b4e-11c5-42f8-9754-e7036c79f054"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IStructureSink
    {
        void HandleStructureChangedEvent(IntPtr sender, int changeType, IntPtr runtimeId);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class StructureSink : IStructureSink
    {
        // Events keep accessibility available; the bounded polling reader owns
        // observation. Never read an event sender or message payload here.
        public void HandleStructureChangedEvent(IntPtr sender, int changeType, IntPtr runtimeId) { }
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    internal static bool IsPrimaryWorkerLink(string classes) => classes.Length <= 512 &&
        classes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("df-link-card", StringComparer.Ordinal);

    internal static bool IsTitleText(int control, string role, bool primaryLinkChild) =>
        primaryLinkChild && control == 50020 && role == "description";

    private sealed class Node(IElement element, IWalker walker, IWalker titleWalker,
        bool ownsElement = true, bool primaryLinkChild = false) : IClaudeOverviewNode
    {
        private object? Property(int id)
        {
            try { element.GetProperty(id, out var value); return value; }
            catch (COMException) { return null; }
        }
        public string Name => Property(30005) as string ?? "";
        public string Address => Property(30045) as string is { Length: > 0 } value ? value : Property(30093) as string ?? "";
        public ClaudeOverviewNodeKind Kind
        {
            get
            {
                var role = Property(30101) as string ?? "";
                if (role == "feed") return ClaudeOverviewNodeKind.Feed;
                if (role == "heading") return ClaudeOverviewNodeKind.Heading;
                if (role == "region") return ClaudeOverviewNodeKind.Pane;
                var control = Property(30003) is int id ? id : 0;
                if (IsTitleText(control, role, primaryLinkChild)) return ClaudeOverviewNodeKind.WorkerTitle;
                return control switch
                {
                    50030 => ClaudeOverviewNodeKind.Document,
                    50033 => ClaudeOverviewNodeKind.Pane,
                    50026 => ClaudeOverviewNodeKind.Group,
                    50005 => ClaudeOverviewNodeKind.Link,
                    50006 => ClaudeOverviewNodeKind.Image,
                    50004 => ClaudeOverviewNodeKind.Edit,
                    _ => ClaudeOverviewNodeKind.Other,
                };
            }
        }

        public IEnumerable<IClaudeOverviewNode> Children
        {
            get
            {
                // ControlView hides static title text. Use RawView only for the
                // direct children of a verified primary worker link; other card
                // links and conversation trees retain the filtered walker.
                var primaryLink = Kind == ClaudeOverviewNodeKind.Link && IsPrimaryWorkerLink(Property(30012) as string ?? "");
                var childWalker = primaryLink ? titleWalker : walker;
                IElement? child;
                try { childWalker.GetFirstChild(element, out child); }
                catch (COMException) { yield break; }
                try
                {
                    while (child is not null)
                    {
                        IElement? next;
                        try { childWalker.GetNextSibling(child, out next); }
                        catch (COMException) { yield break; }
                        var current = child;
                        child = next;
                        yield return new Node(current, walker, titleWalker, primaryLinkChild: primaryLink);
                    }
                }
                finally { Release(child); }
            }
        }

        public void Dispose() { if (ownsElement) Release(element); }
    }

    private static void Release(object? item)
    {
        if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
    }

    // Read-only prefixes from the Windows SDK UIAutomationClient.h interfaces.
    // Reserved slots preserve the native vtable; no input methods are exposed.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAutomation
    {
        void ReservedCompareElements(); void ReservedCompareIds(); void ReservedRoot();
        void ElementFromHandle(IntPtr handle, out IElement element);
        void ReservedPoint(); void ReservedFocused(); void ReservedRootCache();
        void ReservedHandleCache(); void ReservedPointCache(); void ReservedFocusedCache(); void ReservedCreateWalker();
        void GetControlViewWalker(out IWalker walker);
        void ReservedContentWalker();
        void GetRawViewWalker(out IWalker walker);
        void ReservedRawCondition(); void ReservedControlCondition(); void ReservedContentCondition();
        void ReservedCacheRequest(); void ReservedTrue(); void ReservedFalse();
        void ReservedProperty(); void ReservedPropertyEx(); void ReservedAnd(); void ReservedAndArray(); void ReservedAndNative();
        void ReservedOr(); void ReservedOrArray(); void ReservedOrNative(); void ReservedNot();
        void ReservedAddAutomation(); void ReservedRemoveAutomation();
        void ReservedAddPropertyNative(); void ReservedAddProperty(); void ReservedRemoveProperty();
        void AddStructureChangedEventHandler(IElement element, int scope, IntPtr cacheRequest, IStructureSink handler);
        void RemoveStructureChangedEventHandler(IElement element, IStructureSink handler);
        void ReservedAddFocus(); void ReservedRemoveFocus(); void RemoveAllEventHandlers();
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IElement
    {
        void ReservedInput(); void ReservedRuntimeId(); void ReservedFindFirst(); void ReservedFindAll();
        void ReservedFirstCache(); void ReservedAllCache(); void ReservedUpdatedCache();
        void GetProperty(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWalker
    {
        void ReservedParent();
        void GetFirstChild(IElement element, out IElement? child);
        void ReservedLastChild();
        void GetNextSibling(IElement element, out IElement? sibling);
    }
}
