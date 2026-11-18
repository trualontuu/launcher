using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launcher.Shell;

/// <summary>COM оболочки Windows: список приложений как в «Пуске» (shell:AppsFolder) и иконки через IShellItemImageFactory.</summary>
public static class ShellApi
{
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem2
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IntPtr psi, uint hint, out int order);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreWithCreateObject(int flags, IntPtr punk, ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreForKeys(IntPtr keys, uint count, int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(ref PROPERTYKEY key, ref Guid riid, out IntPtr ppv);
        void Update(IntPtr pbc);
        void GetProperty(ref PROPERTYKEY key, IntPtr pv);
        void GetCLSID(ref PROPERTYKEY key, out Guid clsid);
        void GetFileTime(ref PROPERTYKEY key, out long ft);
        void GetInt32(ref PROPERTYKEY key, out int i);
        [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr psz);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out object item, out uint fetched);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

    static Guid IID_IShellItem2 = new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
    static Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    static Guid IID_ImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    static Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    static PROPERTYKEY PKEY_Link_TargetParsingPath = new() { fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), pid = 2 };

    const uint SIGDN_NORMALDISPLAY = 0, SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    static string? Take(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUni(p);
        CoTaskMemFree(p);
        return s;
    }

    /// <summary>Все приложения из «Пуска»: (название, ID для shell:AppsFolder\ID, путь к exe/ярлыку или null). Вызывать в STA-потоке.</summary>
    public static List<(string name, string id, string? path)> EnumApps()
    {
        var list = new List<(string, string, string?)>();
        if (SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref IID_IShellItem2, out object fo) != 0) return list;
        var folder = (IShellItem2)fo;
        try
        {
            folder.BindToHandler(IntPtr.Zero, ref BHID_EnumItems, ref IID_IEnumShellItems, out object eo);
            var en = (IEnumShellItems)eo;
            while (en.Next(1, out object io, out uint got) == 0 && got == 1)
            {
                var it = (IShellItem2)io;
                try
                {
                    it.GetDisplayName(SIGDN_NORMALDISPLAY, out var pn);
                    it.GetDisplayName(SIGDN_PARENTRELATIVEPARSING, out var pi);
                    string? name = Take(pn), id = Take(pi);
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id)) continue;
                    string? path = null;
                    if (it.GetString(ref PKEY_Link_TargetParsingPath, out var pp) == 0) path = Take(pp);
                    path ??= ResolveId(id);
                    if (path != null && !File.Exists(path) && !Directory.Exists(path)) path = null;
                    list.Add((name, id, path));
                }
                catch { }
                finally { Marshal.ReleaseComObject(it); }
            }
            Marshal.ReleaseComObject(en);
        }
        catch { }
        finally { Marshal.ReleaseComObject(folder); }
        return list;
    }

    /// <summary>«{6D809377-…}\Microsoft VS Code\Code.exe» → «C:\Program Files\Microsoft VS Code\Code.exe».</summary>
    static string? ResolveId(string id)
    {
        if (id.Length > 3 && id[1] == ':' && id[2] == '\\') return id;
        if (id.Length > 40 && id[0] == '{' && id[37] == '}' && Guid.TryParse(id.AsSpan(0, 38), out var g))
        {
            if (SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out var p) == 0)
            {
                var root = Take(p);
                if (root != null) return System.IO.Path.Combine(root, id[39..]);
            }
        }
        return null;
    }

    // ---------- иконки ----------
    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    /// <summary>Иконка (или миниатюра, если thumbnail) элемента оболочки. Вызывать в STA-потоке.</summary>
    public static ImageSource? GetImage(string parsingName, int px, bool thumbnail)
    {
        if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref IID_ImageFactory, out object o) != 0) return null;
        var f = (IShellItemImageFactory)o;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            int flags = thumbnail ? SIIGBF_BIGGERSIZEOK : SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY;
            if (f.GetImage(new SIZE { cx = px, cy = px }, flags, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            return FromHBitmap(hbm);
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            Marshal.ReleaseComObject(f);
        }
    }

    /// <summary>HBITMAP → BitmapSource с прозрачностью (стандартный CreateBitmapSourceFromHBitmap теряет альфа-канал).</summary>
    static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0) return null;
        var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var px = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }

        bool anyAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;

        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        src.Freeze();
        return src;
    }
}
