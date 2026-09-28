using System.Runtime.InteropServices;

namespace Catena.Platform.Windows;

public static class WindowsFileIcons
{
    // Type icons only: never ask Shell to open the selected file or invoke a thumbnail handler.
    public static byte[]? ReadTypeIcon(string extension, bool directory)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var initialized = CoInitializeEx(IntPtr.Zero, 0) >= 0;
        IntPtr icon = IntPtr.Zero, dc = IntPtr.Zero, bitmap = IntPtr.Zero, original = IntPtr.Zero;
        try
        {
            if (SHGetFileInfoW(directory ? "folder" : "file" + extension, directory ? 16u : 128u,
                out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x10) == IntPtr.Zero) return null;
            icon = info.Icon;
            dc = CreateCompatibleDC(IntPtr.Zero); if (dc == IntPtr.Zero) return null;
            var dib = new BitmapInfo { Size = 40, Width = 32, Height = -32, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(dc, ref dib, 0, out var pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) return null;
            original = SelectObject(dc, bitmap);
            var data = new byte[32 * 32 * 4]; Marshal.Copy(data, 0, pixels, data.Length);
            if (!DrawIconEx(dc, 0, 0, icon, 32, 32, 0, IntPtr.Zero, 3)) return null;
            Marshal.Copy(pixels, data, 0, data.Length); return data;
        }
        finally
        {
            if (original != IntPtr.Zero) SelectObject(dc, original);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            if (initialized) CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon; public int Index; public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important, Colors;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfoW(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
