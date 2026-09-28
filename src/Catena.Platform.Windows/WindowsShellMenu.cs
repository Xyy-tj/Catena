using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Catena.Platform.Windows;

// Reuse Explorer's registered verbs, including copy/cut, rename, recycle and properties.
[SupportedOSPlatform("windows")]
public static class WindowsShellMenu
{
    public enum Action { None, Open, Rename, Refresh }
    public static Action Show(IReadOnlyList<string> paths, nint owner, int x, int y, bool interceptOpen)
        => ShowCore(paths, owner, x, y, interceptOpen, null);
    public static IReadOnlyList<string> GetAvailableVerbs(IReadOnlyList<string> paths)
    {
        var verbs = new List<string>(); ShowCore(paths, 0, 0, 0, false, verbs); return verbs;
    }
    private static Action ShowCore(IReadOnlyList<string> paths, nint owner, int x, int y, bool interceptOpen, List<string>? verbs)
    {
        if (paths.Count == 0) return Action.None;
        var parent = Path.GetDirectoryName(paths[0]);
        if (paths.Any(p => !string.Equals(Path.GetDirectoryName(p), parent, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("右键菜单需要同一目录中的文件。");
        var pidls = new List<nint>(); IShellFolder? folder = null; IContextMenu? context = null; nint menu = 0;
        SubclassProc? callback = null;
        try
        {
            foreach (var path in paths)
            {
                Marshal.ThrowExceptionForHR(SHParseDisplayName(path, 0, out var pidl, 0, out _));
                pidls.Add(pidl);
            }
            var folderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(SHBindToParent(pidls[0], ref folderId, out folder, out _));
            var menuId = typeof(IContextMenu).GUID;
            folder.GetUIObjectOf(owner, (uint)pidls.Count, pidls.Select(ILFindLastID).ToArray(), ref menuId, 0, out context);
            menu = CreatePopupMenu();
            var resultCode = context.QueryContextMenu(menu, 0, 1, 0x7fff, 0x10);
            Marshal.ThrowExceptionForHR(resultCode);
            if (verbs is not null)
            {
                var text = Marshal.AllocHGlobal(512);
                try
                {
                    for (var i = 0; i < (resultCode & 0xffff); i++)
                    {
                        Marshal.WriteInt16(text, 0);
                        if (context.GetCommandString((nuint)i, 4, 0, text, 256) == 0 && Marshal.PtrToStringUni(text) is { Length: > 0 } availableVerb) verbs.Add(availableVerb);
                    }
                }
                finally { Marshal.FreeHGlobal(text); }
                return Action.None;
            }
            if (context is IContextMenu3 menu3)
            {
                callback = (hwnd, message, wParam, lParam, id, data) =>
                {
                    if (message is 0x117 or 0x120 or 0x2b or 0x2c)
                        try { if (menu3.HandleMenuMsg2(message, wParam, lParam, out var result) == 0) return result; } catch (COMException) { }
                    return DefSubclassProc(hwnd, message, wParam, lParam);
                };
            }
            else if (context is IContextMenu2 menu2)
            {
                callback = (hwnd, message, wParam, lParam, id, data) =>
                {
                    if (message is 0x117 or 0x2b or 0x2c)
                        try { if (menu2.HandleMenuMsg(message, wParam, lParam) == 0) return 0; } catch (COMException) { }
                    return DefSubclassProc(hwnd, message, wParam, lParam);
                };
            }
            if (callback is not null) SetWindowSubclass(owner, callback, 0xCA7E, 0);
            var selected = TrackPopupMenuEx(menu, 0x100, x, y, owner, 0);
            if (selected == 0) return Action.None;
            string? verb = null;
            {
                var buffer = Marshal.AllocHGlobal(512);
                try
                {
                    Marshal.WriteInt16(buffer, 0);
                    if (context.GetCommandString((nuint)(selected - 1), 4, 0, buffer, 256) == 0)
                        verb = Marshal.PtrToStringUni(buffer);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            if (interceptOpen && verb is "open" or "explore") return Action.Open;
            if (paths.Count == 1 && verb == "rename") return Action.Rename;
            var invoke = new InvokeInfo { Size = Marshal.SizeOf<InvokeInfo>(), Owner = owner, Verb = selected - 1, Show = 1 };
            context.InvokeCommand(ref invoke);
            return verb is "delete" or "paste" or "pastelink" ? Action.Refresh : Action.None;
        }
        finally
        {
            if (callback is not null) RemoveWindowSubclass(owner, callback, 0xCA7E);
            if (menu != 0) DestroyMenu(menu);
            if (context is not null) Marshal.ReleaseComObject(context);
            if (folder is not null) Marshal.ReleaseComObject(folder);
            foreach (var pidl in pidls) Marshal.FreeCoTaskMem(pidl);
            GC.KeepAlive(callback);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct InvokeInfo
    { public int Size; public uint Mask; public nint Owner, Verb, Parameters, Directory; public int Show; public uint HotKey; public nint Icon; }
    [ComImport, Guid("000214e6-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(nint a, nint b, nint c, nint d, nint e, nint f);
        void EnumObjects(nint a, uint b, nint c); void BindToObject(nint a, nint b, nint c, nint d);
        void BindToStorage(nint a, nint b, nint c, nint d); void CompareIDs(nint a, nint b, nint c);
        void CreateViewObject(nint a, nint b, nint c); void GetAttributesOf(uint a, nint b, nint c);
        void GetUIObjectOf(nint owner, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] nint[] children, ref Guid iid, nint reserved, [MarshalAs(UnmanagedType.Interface)] out IContextMenu menu);
    }
    [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        void InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(nuint id, uint flags, nint reserved, nint name, uint max);
    }
    [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        void InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(nuint id, uint flags, nint reserved, nint name, uint max);
        [PreserveSig] int HandleMenuMsg(uint message, nuint wParam, nint lParam);
    }
    [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(nint menu, uint index, uint first, uint last, uint flags);
        void InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(nuint id, uint flags, nint reserved, nint name, uint max);
        [PreserveSig] int HandleMenuMsg(uint message, nuint wParam, nint lParam);
        [PreserveSig] int HandleMenuMsg2(uint message, nuint wParam, nint lParam, out nint result);
    }
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, nint context, out nint pidl, uint mask, out uint attributes);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(nint pidl, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder, out nint child);
    [DllImport("shell32.dll")] private static extern nint ILFindLastID(nint pidl);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint owner, nint parameters);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}

