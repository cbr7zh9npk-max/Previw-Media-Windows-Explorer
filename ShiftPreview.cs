// ShiftPreview - hold Shift while hovering a photo or video in File Explorer (or on the desktop)
// to get a floating, resizable preview.
//
// Written for the C# 5 compiler that ships with Windows (csc.exe in .NET Framework 4.x),
// so it builds on any Windows 11 PC without installing anything. Run build.bat.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ShiftPreview
{
    // ------------------------------------------------------------------ Win32
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        public const int VK_LBUTTON = 0x01, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        public const uint GA_ROOT = 2, MONITOR_DEFAULTTONEAREST = 2;
        public const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
        public const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int idx, int v);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

        // Low-level keyboard hook (for shortcut keys while the preview is open)
        public delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; public uint lPrivate; }
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG m);
        // Explorer-style "natural" name sorting (file2 before file10)
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] public static extern int StrCmpLogicalW(string a, string b);

        public static bool KeyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        public static string ClassName(IntPtr h)
        {
            var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString();
        }
        public static string WindowText(IntPtr h)
        {
            var sb = new StringBuilder(512); GetWindowText(h, sb, sb.Capacity); return sb.ToString();
        }
    }

    // ------------------------------------------------------------------ Shell COM interfaces
    // Used to ask an Explorer window for its items in the exact order it shows them.
    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IComServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
        [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
        [PreserveSig] int SetStatusTextSB(IntPtr pszStatusText);
        [PreserveSig] int EnableModelessSB(int fEnable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
        [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr phwnd);
        [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object ppshv);
        [PreserveSig] int OnViewWindowActive(IntPtr pshv);
        [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint viewMode);
        [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
        [PreserveSig] int Items(uint uFlags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int GetCount(out uint pdwNumItems);
        [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
    }

    // ------------------------------------------------------------------ Windows star rating (System.Rating)
    // The same rating File Explorer shows in its "Rating" column and in file Properties > Details.
    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint uintVal;
        [FieldOffset(8)] public int intVal;
        [FieldOffset(8)] public short shortVal;
        [FieldOffset(8)] public ushort ushortVal;
        [FieldOffset(8)] public long longVal;
        [FieldOffset(8)] public double dblVal;
        [FieldOffset(8)] public IntPtr ptr;
        [FieldOffset(16)] public IntPtr ptr2;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    static class RatingStore
    {
        const int GPS_DEFAULT = 0, GPS_READWRITE = 2;
        const ushort VT_EMPTY = 0, VT_UI4 = 19;
        static readonly uint[] StarValues = { 0, 1, 25, 50, 75, 99 };   // what Windows stores for 0..5 stars

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr pbc, int flags, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
        [DllImport("propsys.dll", CharSet = CharSet.Unicode)] static extern int PSGetPropertyKeyFromName(string name, out PROPERTYKEY key);

        /// <summary>Video length from Windows' file info (System.Media.Duration), or null.</summary>
        public static TimeSpan? ReadDuration(string path)
        {
            var key = new PROPERTYKEY();
            key.fmtid = new Guid("64440490-4C8B-11D1-8B70-080036B11A03");
            key.pid = 3;
            object[] v = ReadValues(path, new[] { key }, new[] { true });
            if (v[0] == null) return null;
            try
            {
                long ticks = Convert.ToInt64(v[0], CultureInfo.InvariantCulture);   // 100-ns units
                return ticks > 0 ? (TimeSpan?)TimeSpan.FromTicks(ticks) : null;
            }
            catch { return null; }
        }

        public static bool TryKeyFromName(string name, out PROPERTYKEY key)
        {
            try { return PSGetPropertyKeyFromName(name, out key) == 0; }
            catch { key = new PROPERTYKEY(); return false; }
        }

        /// <summary>Reads property values from a file and closes it again immediately.</summary>
        public static object[] ReadValues(string path, PROPERTYKEY[] keys, bool[] valid)
        {
            var result = new object[keys.Length];
            IPropertyStore store = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_DEFAULT, ref iid, out store) != 0 || store == null) return result;
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!valid[i]) continue;
                    PROPERTYKEY k = keys[i];
                    PROPVARIANT pv;
                    if (store.GetValue(ref k, out pv) == 0)
                    {
                        try { result[i] = ToObject(pv); } catch { }
                        PropVariantClear(ref pv);
                    }
                }
            }
            catch { }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
            return result;
        }

        static object ToObject(PROPVARIANT pv)
        {
            switch (pv.vt)
            {
                case 2: return (long)pv.shortVal;                  // VT_I2
                case 3: return (long)pv.intVal;                    // VT_I4
                case 18: return (long)pv.ushortVal;                // VT_UI2
                case 19: return (long)pv.uintVal;                  // VT_UI4
                case 20: return pv.longVal;                        // VT_I8
                case 21: return (double)unchecked((ulong)pv.longVal); // VT_UI8
                case 5: return pv.dblVal;                          // VT_R8
                case 7: return DateTime.FromOADate(pv.dblVal);     // VT_DATE
                case 11: return pv.shortVal != 0 ? 1L : 0L;        // VT_BOOL
                case 64: return DateTime.FromFileTimeUtc(pv.longVal); // VT_FILETIME
                case 31: return pv.ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(pv.ptr); // VT_LPWSTR
                case 8: return pv.ptr == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(pv.ptr); // VT_BSTR
                default: return null;
            }
        }

        static PROPERTYKEY RatingKey()
        {
            var k = new PROPERTYKEY();
            k.fmtid = new Guid("64440492-4C8B-11D1-8B70-080036B11A03");
            k.pid = 9;
            return k;
        }

        public static int StarsFromValue(uint v)
        {
            if (v == 0) return 0;
            if (v <= 12) return 1;
            if (v <= 37) return 2;
            if (v <= 62) return 3;
            if (v <= 87) return 4;
            return 5;
        }

        /// <summary>Returns 0..5 stars, or -1 if it can't be read. Call from an STA thread.</summary>
        public static int Read(string path)
        {
            IPropertyStore store = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_DEFAULT, ref iid, out store) != 0 || store == null) return -1;
                PROPERTYKEY key = RatingKey();
                PROPVARIANT pv;
                if (store.GetValue(ref key, out pv) != 0) return -1;
                int stars = pv.vt == VT_UI4 ? StarsFromValue(pv.uintVal) : 0;
                PropVariantClear(ref pv);
                return stars;
            }
            catch { return -1; }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        /// <summary>Saves 0..5 stars into the file. Returns null on success, otherwise an error message.
        /// Call from an STA thread. Retries for a moment if the file is still in use.</summary>
        public static string Write(string path, int stars)
        {
            string lastError = "unknown error";
            string ext = Path.GetExtension(path).ToLowerInvariant();
            for (int attempt = 0; attempt < 12; attempt++)
            {
                IPropertyStore store = null;
                int hr;
                try
                {
                    Guid iid = typeof(IPropertyStore).GUID;
                    hr = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_READWRITE, ref iid, out store);
                    if (hr == 0 && store != null)
                    {
                        PROPERTYKEY key = RatingKey();
                        var pv = new PROPVARIANT();
                        if (stars > 0) { pv.vt = VT_UI4; pv.uintVal = StarValues[stars]; }
                        else pv.vt = VT_EMPTY;
                        hr = store.SetValue(ref key, ref pv);
                        if (hr == 0) hr = store.Commit();
                        if (hr == 0) return null;
                    }
                }
                catch (Exception ex) { hr = Marshal.GetHRForException(ex); }
                finally { if (store != null) Marshal.ReleaseComObject(store); }

                uint u = unchecked((uint)hr);
                string code = "0x" + u.ToString("X8");
                Log.Write("Rating write attempt " + (attempt + 1) + " failed for " + path + " (hr=" + code + ")");
                bool inUse = u == 0x80070020 || u == 0x80070021 || u == 0x80030020 || u == 0x80030021; // sharing/lock violation
                if (inUse)
                {
                    lastError = "the file is in use by another program (" + code + ")";
                    // Let .NET release any shell objects that might still hold the file open.
                    GC.Collect(); GC.WaitForPendingFinalizers();
                    Thread.Sleep(300);
                    continue;
                }
                if (u == 0x80070005 || u == 0x80030005) return "access denied - is the file read-only? (" + code + ")";
                bool knownUnsupported = ext == ".mkv" || ext == ".avi" || ext == ".webm" || ext == ".ts" || ext == ".m2ts" || ext == ".mts" || ext == ".mpg" || ext == ".mpeg";
                if (knownUnsupported) return "Windows can't store ratings in " + ext + " files (.mp4, .m4v, .mov and .wmv work)";
                return "Windows refused to save it (" + code + ")";
            }
            return lastError;
        }
    }

    // ------------------------------------------------------------------ Global shortcut-key hook
    // Runs on its own thread so keyboard input never waits on the UI.
    class KeyHook
    {
        const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101;
        public const int VK_SPACE = 0x20, VK_C = 0x43, VK_V = 0x56, VK_X = 0x58, VK_Z = 0x5A, VK_0 = 0x30, VK_5 = 0x35, VK_ESCAPE = 0x1B;

        public volatile bool Armed;
        public volatile bool VideoKeys;
        readonly Dispatcher dispatcher;
        readonly Action<int> handler;
        Native.LowLevelProc proc;   // kept in a field so it isn't garbage-collected
        IntPtr hook;

        public KeyHook(Dispatcher dispatcher, Action<int> handler)
        {
            this.dispatcher = dispatcher;
            this.handler = handler;
            var t = new Thread(Run);
            t.IsBackground = true;
            t.Name = "ShiftPreview key hook";
            t.Start();
        }

        void Run()
        {
            proc = HookProc;
            hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }

        // A "Shift tap" = Shift pressed and released with no other key (or mouse click) in between.
        public const int SHIFT_TAP = -1;
        public volatile bool OtherKeyDuringShift;
        bool shiftIsDown;

        void TrackShift(IntPtr wParam, IntPtr lParam)
        {
            int m = wParam.ToInt32();
            int vk = Marshal.ReadInt32(lParam);
            bool isShift = vk == 0x10 || vk == 0xA0 || vk == 0xA1;
            if (m == WM_KEYDOWN || m == 0x104 /*WM_SYSKEYDOWN*/)
            {
                if (isShift) { if (!shiftIsDown) { shiftIsDown = true; OtherKeyDuringShift = false; } }
                else OtherKeyDuringShift = true;
            }
            else if ((m == WM_KEYUP || m == 0x105 /*WM_SYSKEYUP*/) && isShift && shiftIsDown)
            {
                shiftIsDown = false;
                if (!OtherKeyDuringShift && Armed)
                    dispatcher.BeginInvoke(new Action(delegate { handler(SHIFT_TAP); }));
            }
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0) { try { TrackShift(wParam, lParam); } catch { } }
            if (nCode >= 0 && Armed)
            {
                int m = wParam.ToInt32();
                if (m == WM_KEYDOWN || m == WM_KEYUP)
                {
                    int vk = Marshal.ReadInt32(lParam);
                    bool otherMods = Native.KeyDown(Native.VK_CONTROL) || Native.KeyDown(Native.VK_MENU) ||
                                     Native.KeyDown(Native.VK_LWIN) || Native.KeyDown(Native.VK_RWIN);
                    bool shift = Native.KeyDown(Native.VK_SHIFT);
                    bool navigationKey = (vk == VK_C || vk == VK_V) && shift;
                    bool playbackKey = VideoKeys && (vk == VK_SPACE || ((vk == VK_X || vk == VK_Z) && shift));
                    bool rateKey = vk >= VK_0 && vk <= VK_5 && shift;     // Shift+1..5 = stars, Shift+0 = clear
                    if (!otherMods && (vk == VK_ESCAPE || navigationKey || playbackKey || rateKey))
                    {
                        if (m == WM_KEYDOWN)
                        {
                            int v = vk;
                            dispatcher.BeginInvoke(new Action(delegate { handler(v); }));
                        }
                        return new IntPtr(1); // swallow so Explorer doesn't also move its selection
                    }
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }
    }

    // ------------------------------------------------------------------ Small log file (%APPDATA%\ShiftPreview\log.txt)
    static class Log
    {
        static readonly object gate = new object();
        static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShiftPreview", "log.txt"); } }

        public static void Write(string line)
        {
            try
            {
                lock (gate)
                {
                    string p = FilePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 200 * 1024) fi.Delete();
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Settings
    class Settings
    {
        public double Width = 560, Height = 315;   // popup size (device-independent pixels)
        public int Volume = 50;                     // 0..100
        public bool Muted = false;
        public bool Enabled = true;
        public bool Pinned = false;                 // true = popup always opens where you last dragged it
        public int PinX = 0, PinY = 0;
        public bool HasPinnedPos = false;
        public bool FirstRunDone = false;
        public int HoldDelayMs = 250;
        public bool ModernEngine = false;           // always use the modern (Media Foundation) player               // how long Shift must be held before the preview opens

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShiftPreview"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var ci = CultureInfo.InvariantCulture;
                foreach (var raw in File.ReadAllLines(FilePath))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim(), v = raw.Substring(eq + 1).Trim();
                    try
                    {
                        switch (k)
                        {
                            case "Width": s.Width = double.Parse(v, ci); break;
                            case "Height": s.Height = double.Parse(v, ci); break;
                            case "Volume": s.Volume = int.Parse(v, ci); break;
                            case "Muted": s.Muted = bool.Parse(v); break;
                            case "Enabled": s.Enabled = bool.Parse(v); break;
                            case "Pinned": s.Pinned = bool.Parse(v); break;
                            case "PinX": s.PinX = int.Parse(v, ci); break;
                            case "PinY": s.PinY = int.Parse(v, ci); break;
                            case "HasPinnedPos": s.HasPinnedPos = bool.Parse(v); break;
                            case "FirstRunDone": s.FirstRunDone = bool.Parse(v); break;
                            case "HoldDelayMs": s.HoldDelayMs = int.Parse(v, ci); break;
                            case "ModernEngine": s.ModernEngine = bool.Parse(v); break;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            s.Width = Math.Max(200, Math.Min(4000, s.Width));
            s.Height = Math.Max(120, Math.Min(3000, s.Height));
            s.Volume = Math.Max(0, Math.Min(100, s.Volume));
            s.HoldDelayMs = Math.Max(0, Math.Min(3000, s.HoldDelayMs));
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var ci = CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.AppendLine("Width=" + Width.ToString(ci));
                sb.AppendLine("Height=" + Height.ToString(ci));
                sb.AppendLine("Volume=" + Volume.ToString(ci));
                sb.AppendLine("Muted=" + Muted);
                sb.AppendLine("Enabled=" + Enabled);
                sb.AppendLine("Pinned=" + Pinned);
                sb.AppendLine("PinX=" + PinX.ToString(ci));
                sb.AppendLine("PinY=" + PinY.ToString(ci));
                sb.AppendLine("HasPinnedPos=" + HasPinnedPos);
                sb.AppendLine("FirstRunDone=" + FirstRunDone);
                sb.AppendLine("HoldDelayMs=" + HoldDelayMs.ToString(ci));
                sb.AppendLine("ModernEngine=" + ModernEngine);
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Finds the file under the mouse
    // Runs on its own STA thread so a slow/hung Explorer never freezes the UI.
    class Resolver
    {
        static readonly string[] VideoExts = {
            ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg",
            ".m2ts", ".mts", ".ts", ".3gp", ".3g2", ".asf"
        };
        static readonly string[] PhotoExts = { ".jpg", ".jpeg", ".jpe", ".png", ".bmp", ".gif", ".tif", ".tiff" };

        class FolderCache { public DateTime At; public List<string> Folders; }

        readonly Dispatcher dispatcher;
        readonly Action<Native.POINT, string, IntPtr> callback;
        readonly object gate = new object();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        readonly Queue<Action> jobs = new Queue<Action>();
        Native.POINT pending; bool hasPending;

        Native.POINT lastP; DateTime lastAt = DateTime.MinValue; string lastResult; IntPtr lastRoot;
        readonly Dictionary<string, FolderCache> folderCache = new Dictionary<string, FolderCache>();
        object shellApp;

        static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        const uint SVGIO_ALLVIEW = 0x2, SVGIO_FLAG_VIEWORDER = 0x80000000, SIGDN_FILESYSPATH = 0x80058000;

        public Resolver(Dispatcher dispatcher, Action<Native.POINT, string, IntPtr> callback)
        {
            this.dispatcher = dispatcher;
            this.callback = callback;
            var t = new Thread(Loop);
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Name = "ShiftPreview resolver";
            t.Start();
        }

        public static bool IsPhoto(string path)
        {
            return HasExtension(path, PhotoExts);
        }

        static bool CanPreview(string path)
        {
            return HasExtension(path, VideoExts) || IsPhoto(path);
        }

        static bool HasExtension(string path, string[] extensions)
        {
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            foreach (var e in extensions) if (string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void Request(Native.POINT p)
        {
            lock (gate) { pending = p; hasPending = true; }
            signal.Set();
        }

        /// <summary>Gets previewable files in the same folder, in Explorer's display order.</summary>
        public void RequestSiblings(IntPtr root, string path, Action<List<string>> done)
        {
            lock (gate)
            {
                jobs.Enqueue(delegate
                {
                    List<string> list = null;
                    try { list = GetSiblings(root, path); } catch { }
                    if (list == null) list = new List<string>();
                    var l = list;
                    dispatcher.BeginInvoke(new Action(delegate { done(l); }));
                });
            }
            signal.Set();
        }

        void Loop()
        {
            while (true)
            {
                signal.WaitOne();
                while (true)
                {
                    Action job = null;
                    lock (gate) { if (jobs.Count > 0) job = jobs.Dequeue(); }
                    if (job == null) break;
                    try { job(); } catch { }
                }

                Native.POINT p;
                lock (gate)
                {
                    if (!hasPending) continue;
                    p = pending; hasPending = false;
                }
                string result; IntPtr root;
                if (p.X == lastP.X && p.Y == lastP.Y && (DateTime.UtcNow - lastAt).TotalMilliseconds < 300)
                {
                    result = lastResult; root = lastRoot;
                }
                else
                {
                    root = IntPtr.Zero;
                    try { result = ResolveAt(p, out root); } catch { result = null; }
                    lastP = p; lastAt = DateTime.UtcNow; lastResult = result; lastRoot = root;
                }
                var pp = p; var rr = result; var ro = root;
                dispatcher.BeginInvoke(new Action(delegate { callback(pp, rr, ro); }));
            }
        }

        // rootOut = the Explorer window the file is in (IntPtr.Zero for the desktop).
        string ResolveAt(Native.POINT p, out IntPtr rootOut)
        {
            rootOut = IntPtr.Zero;
            IntPtr hwnd = Native.WindowFromPoint(p);
            if (hwnd == IntPtr.Zero) return null;
            IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOT);
            string cls = Native.ClassName(root);
            bool isExplorer = cls == "CabinetWClass";
            bool isDesktop = cls == "Progman" || cls == "WorkerW";
            if (!isExplorer && !isDesktop) return null;
            if (isExplorer) rootOut = root;

            string name = ItemNameAt(p);
            if (string.IsNullOrEmpty(name)) return null;

            List<string> folders = isExplorer ? ExplorerFolders(root) : DesktopFolders();
            foreach (var folder in folders)
            {
                string f = FindPreviewFile(folder, name);
                if (f != null) return f;
            }
            return null;
        }

        // Uses UI Automation to read the name of the list item under the mouse.
        static string ItemNameAt(Native.POINT p)
        {
            AutomationElement el = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
            TreeWalker walker = TreeWalker.ControlViewWalker;
            for (int i = 0; el != null && i < 6; i++)
            {
                ControlType ct = el.Current.ControlType;
                if (ct == ControlType.ListItem || ct == ControlType.DataItem) return el.Current.Name;
                if (ct == ControlType.List || ct == ControlType.Window || ct == ControlType.Tree) return null;
                el = walker.GetParent(el);
            }
            return null;
        }

        static List<string> DesktopFolders()
        {
            var list = new List<string>();
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            list.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
            return list;
        }

        // Asks the shell which folder each Explorer window/tab shows. The active tab is tried first
        // (its name matches the window title).
        List<string> ExplorerFolders(IntPtr root)
        {
            string title = Native.WindowText(root);
            string key = root.ToInt64().ToString(CultureInfo.InvariantCulture) + "|" + title;
            FolderCache fc;
            if (folderCache.TryGetValue(key, out fc) && (DateTime.UtcNow - fc.At).TotalSeconds < 3) return fc.Folders;
            if (folderCache.Count > 64) folderCache.Clear();

            var preferred = new List<string>();
            var others = new List<string>();
            foreach (object wo in ExplorerTabs(root))
            {
                try
                {
                    dynamic w = wo;
                    string path = w.Document.Folder.Self.Path;
                    string loc = w.LocationName;
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!string.IsNullOrEmpty(loc) && title.StartsWith(loc, StringComparison.OrdinalIgnoreCase)) preferred.Add(path);
                    else others.Add(path);
                }
                catch { }
            }

            preferred.AddRange(others);
            fc = new FolderCache(); fc.At = DateTime.UtcNow; fc.Folders = preferred;
            folderCache[key] = fc;
            return preferred;
        }

        // All shell window objects (one per tab) that belong to the given Explorer window.
        List<object> ExplorerTabs(IntPtr root)
        {
            var result = new List<object>();
            try
            {
                if (shellApp == null) shellApp = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                dynamic shell = shellApp;
                dynamic wins = shell.Windows();
                int count = wins.Count;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        dynamic w = wins.Item(i);
                        if (w == null) continue;
                        long h = Convert.ToInt64(w.HWND);
                        if (h != root.ToInt64()) continue;
                        object wo = w;
                        result.Add(wo);
                    }
                    catch { }
                }
            }
            catch { shellApp = null; }
            return result;
        }

        static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        static int IndexOfPath(List<string> list, string path)
        {
            for (int i = 0; i < list.Count; i++) if (SamePath(list[i], path)) return i;
            return -1;
        }

        List<string> GetSiblings(IntPtr root, string path)
        {
            string folder = Path.GetDirectoryName(path);
            Log.Write("Next/prev order requested for " + path);
            if (root != IntPtr.Zero)
            {
                // Tabs showing this file's folder first, then any other tab of the window
                // (covers libraries, search results and other virtual views).
                var tabs = ExplorerTabs(root);
                var ordered = new List<object>();
                var rest = new List<object>();
                object matchingTab = null;
                foreach (object wo in tabs)
                {
                    string wf = null;
                    try { dynamic w = wo; wf = w.Document.Folder.Self.Path; } catch { }
                    if (SamePath(wf, folder)) { ordered.Add(wo); if (matchingTab == null) matchingTab = wo; }
                    else rest.Add(wo);
                }
                ordered.AddRange(rest);

                // 1st choice: exactly what Explorer shows (sort, filters, grouping).
                foreach (object wo in ordered)
                {
                    List<string> view = ViewOrder(wo);
                    if (view != null && IndexOfPath(view, path) >= 0)
                    {
                        // Explorer may show several folders at once (search, library, "Recent")
                // - only keep previewable files from this file's own folder.
                        var sameFolder = new List<string>();
                        foreach (var v in view) if (SamePath(Path.GetDirectoryName(v), folder)) sameFolder.Add(v);
                    Log.Write("  using Explorer's on-screen order (" + sameFolder.Count + " previewable files in this folder)");
                        return sameFolder;
                    }
                }

                // 2nd choice: sort the folder ourselves by the same columns Explorer is sorted by.
                if (matchingTab != null)
                {
                    string sortColumns = null;
                    try { dynamic w = matchingTab; sortColumns = w.Document.SortColumns; } catch (Exception ex) { Log.Write("  SortColumns failed: " + ex.Message); }
                    Log.Write("  on-screen order unavailable; sorting by Explorer's columns: " + (sortColumns ?? "(unknown)"));
                    List<string> sorted = SortByShellProperties(folder, sortColumns);
                    if (sorted != null && IndexOfPath(sorted, path) >= 0) return sorted;
                }
                else Log.Write("  no Explorer tab found showing " + folder);
            }
            Log.Write("  falling back to name order");
            return SortFolder(folder, null);
        }

        // Two ways to reach the window's IFolderView; both give items in on-screen order.
        static List<string> ViewOrder(object shellWindow)
        {
            var sp = shellWindow as IComServiceProvider;
            if (sp == null) { Log.Write("  no IServiceProvider on the Explorer window"); return null; }

            IFolderView fv = null;
            // Route A: top-level browser -> active shell view -> IFolderView
            try
            {
                Guid sid = SID_STopLevelBrowser, iidBrowser = typeof(IShellBrowser).GUID;
                object o;
                int hr = sp.QueryService(ref sid, ref iidBrowser, out o);
                var browser = hr == 0 ? o as IShellBrowser : null;
                if (browser != null)
                {
                    object viewObj;
                    hr = browser.QueryActiveShellView(out viewObj);
                    if (hr == 0) fv = viewObj as IFolderView;
                }
                if (fv == null) Log.Write("  route A failed (hr=0x" + hr.ToString("X8") + ")");
            }
            catch (Exception ex) { Log.Write("  route A error: " + ex.Message); }

            // Route B: ask the window directly for its folder view
            if (fv == null)
            {
                try
                {
                    Guid sid = typeof(IFolderView).GUID, iid = typeof(IFolderView).GUID;   // SID_SFolderView == IID_IFolderView
                    object o;
                    int hr = sp.QueryService(ref sid, ref iid, out o);
                    if (hr == 0) fv = o as IFolderView;
                    if (fv == null) Log.Write("  route B failed (hr=0x" + hr.ToString("X8") + ")");
                }
                catch (Exception ex) { Log.Write("  route B error: " + ex.Message); }
            }
            if (fv == null) return null;

            try
            {
                Guid iidArray = typeof(IShellItemArray).GUID;
                object arrObj;
                int hr = fv.Items(SVGIO_ALLVIEW | SVGIO_FLAG_VIEWORDER, ref iidArray, out arrObj);
                var arr = hr == 0 ? arrObj as IShellItemArray : null;
                if (arr == null) { Log.Write("  IFolderView.Items failed (hr=0x" + hr.ToString("X8") + ")"); return null; }
                uint count;
                if (arr.GetCount(out count) != 0) return null;

                var list = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    IShellItem item;
                    if (arr.GetItemAt(i, out item) != 0 || item == null) continue;
                    IntPtr psz;
                    if (item.GetDisplayName(SIGDN_FILESYSPATH, out psz) == 0 && psz != IntPtr.Zero)
                    {
                        string p = Marshal.PtrToStringUni(psz);
                        Marshal.FreeCoTaskMem(psz);
                        if (CanPreview(p)) list.Add(p);
                    }
                    ReleaseCom(item);
                }
                ReleaseCom(arr);
                return list;
            }
            catch (Exception ex) { Log.Write("  reading view items failed: " + ex.Message); return null; }
        }

        // Sorts like Explorer using the shell's own property values, so any column works
        // (e.g. "prop:-System.Media.DateEncoded;" = Media created, newest first).
        List<string> SortByShellProperties(string folder, string sortColumns)
        {
            var keys = new List<string>();
            var desc = new List<bool>();
            if (!string.IsNullOrEmpty(sortColumns))
            {
                string sc = sortColumns.StartsWith("prop:") ? sortColumns.Substring(5) : sortColumns;
                foreach (var part in sc.Split(';'))
                {
                    string k = part.Trim();
                    if (k.Length == 0) continue;
                    bool d = k.StartsWith("-");
                    if (d || k.StartsWith("+")) k = k.Substring(1);
                    keys.Add(k); desc.Add(d);
                }
            }
            if (keys.Count == 0) return null;

            var files = new List<FileInfo>();
            try
            {
                foreach (var f in new DirectoryInfo(folder).GetFiles())
                    if (CanPreview(f.Name) && (f.Attributes & FileAttributes.Hidden) == 0) files.Add(f);
            }
            catch { return null; }

            // Each file is opened, read and closed again right away, so nothing stays locked
            // (a locked file can't get a star rating saved).
            var pkeys = new PROPERTYKEY[keys.Count];
            var valid = new bool[keys.Count];
            for (int k = 0; k < keys.Count; k++)
            {
                valid[k] = RatingStore.TryKeyFromName(keys[k], out pkeys[k]);
                if (!valid[k]) Log.Write("  unknown sort property " + keys[k]);
            }
            var values = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files) values[f.FullName] = RatingStore.ReadValues(f.FullName, pkeys, valid);

            files.Sort((a, b) =>
            {
                object[] va = values[a.FullName], vb = values[b.FullName];
                for (int k = 0; k < keys.Count; k++)
                {
                    object x = va[k], y = vb[k];
                    bool xe = IsEmptyValue(x), ye = IsEmptyValue(y);
                    if (xe && ye) continue;
                    if (xe) return 1;          // files without a value go last
                    if (ye) return -1;
                    int c = CompareValues(x, y);
                    if (c != 0) return desc[k] ? -c : c;
                }
                return Native.StrCmpLogicalW(a.Name, b.Name);
            });

            var result = new List<string>();
            foreach (var f in files) result.Add(f.FullName);
            return result;
        }

        static void ReleaseCom(object o)
        {
            try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
        }

        static bool IsEmptyValue(object v)
        {
            if (v == null || v is DBNull) return true;
            var str = v as string;
            return str != null && str.Length == 0;
        }

        static int CompareValues(object x, object y)
        {
            var sx = x as string; var sy = y as string;
            if (sx != null && sy != null) return Native.StrCmpLogicalW(sx, sy);
            try
            {
                if (x is DateTime && y is DateTime) return ((DateTime)x).CompareTo((DateTime)y);
                if (IsNumber(x) && IsNumber(y)) return Convert.ToDouble(x).CompareTo(Convert.ToDouble(y));
                var cx = x as IComparable;
                if (cx != null && x.GetType() == y.GetType()) return cx.CompareTo(y);
            }
            catch { }
            return Native.StrCmpLogicalW(Convert.ToString(x, CultureInfo.InvariantCulture), Convert.ToString(y, CultureInfo.InvariantCulture));
        }

        static bool IsNumber(object v)
        {
            return v is int || v is uint || v is long || v is ulong || v is short || v is ushort ||
                   v is byte || v is sbyte || v is double || v is float || v is decimal;
        }

        // sortColumns looks like "prop:-System.DateModified;System.ItemNameDisplay;" ('-' = descending)
        static List<string> SortFolder(string folder, string sortColumns)
        {
            var files = new List<FileInfo>();
            try
            {
                foreach (var f in new DirectoryInfo(folder).GetFiles())
                    if (CanPreview(f.Name) && (f.Attributes & FileAttributes.Hidden) == 0) files.Add(f);
            }
            catch { }

            string key = "System.ItemNameDisplay";
            bool desc = false;
            if (!string.IsNullOrEmpty(sortColumns))
            {
                string sc = sortColumns.StartsWith("prop:") ? sortColumns.Substring(5) : sortColumns;
                string first = sc.Split(';')[0].Trim();
                if (first.StartsWith("-")) { desc = true; first = first.Substring(1); }
                if (first.Length > 0) key = first;
            }

            Comparison<FileInfo> byName = (a, b) => Native.StrCmpLogicalW(a.Name, b.Name);
            Comparison<FileInfo> cmp;
            switch (key)
            {
                case "System.DateModified": cmp = (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); break;
                case "System.DateCreated": cmp = (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc); break;
                case "System.Size": cmp = (a, b) => a.Length.CompareTo(b.Length); break;
                case "System.ItemTypeText":
                case "System.ItemType": cmp = (a, b) => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase); break;
                default: cmp = byName; break;
            }
            files.Sort((a, b) =>
            {
                int c = cmp(a, b);
                if (desc) c = -c;
                if (c == 0) c = byName(a, b);
                return c;
            });

            var result = new List<string>();
            foreach (var f in files) result.Add(f.FullName);
            return result;
        }

        // Display names may hide the extension. Try the name as-is before appending supported extensions.
        static string FindPreviewFile(string folder, string name)
        {
            if (string.IsNullOrEmpty(folder) || folder.StartsWith("::")) return null;
            try
            {
                string full = Path.Combine(folder, name);
                if (CanPreview(full) && File.Exists(full)) return full;
                foreach (var ext in VideoExts)
                {
                    string c = full + ext;
                    if (File.Exists(c)) return c;
                }
                foreach (var ext in PhotoExts)
                {
                    string c = full + ext;
                    if (File.Exists(c)) return c;
                }
            }
            catch { }
            return null;
        }
    }

    // ------------------------------------------------------------------ Video players
    // Two engines: the classic WPF/Windows Media Player one (default) and the modern Media
    // Foundation "Media Engine" (what the new Windows Media Player app uses), which can jump
    // in files the classic one refuses to seek in.
    interface IVideoPlayer
    {
        FrameworkElement View { get; }
        bool IsModern { get; }
        void Open(string path, TimeSpan? startAt, bool startPaused);
        void Play();
        void Pause();
        TimeSpan Position { get; set; }
        TimeSpan? KnownDuration { get; }
        double Volume { set; }
        bool IsMuted { set; }
        void Close();
        event Action Opened;
        event Action<string> Failed;
    }

    class ClassicPlayer : IVideoPlayer
    {
        readonly MediaElement m = new MediaElement();
        TimeSpan? startAt;
        bool startPaused, closed;
        public event Action Opened;
        public event Action<string> Failed;

        public ClassicPlayer()
        {
            m.LoadedBehavior = MediaState.Manual;
            m.UnloadedBehavior = MediaState.Manual;
            m.Stretch = Stretch.Uniform;
            m.MediaOpened += delegate
            {
                if (closed) return;
                if (startAt.HasValue) { m.Position = startAt.Value; startAt = null; }
                if (startPaused) m.Pause();
                if (Opened != null) Opened();
            };
            m.MediaEnded += delegate { if (closed) return; m.Position = TimeSpan.Zero; m.Play(); };
            m.MediaFailed += delegate (object o, ExceptionRoutedEventArgs e)
            {
                if (closed) return;
                if (Failed != null) Failed(e.ErrorException != null ? e.ErrorException.Message : "unknown error");
            };
        }

        public FrameworkElement View { get { return m; } }
        public bool IsModern { get { return false; } }
        public void Open(string path, TimeSpan? at, bool paused) { startAt = at; startPaused = paused; m.Source = new Uri(path); m.Play(); }
        public void Play() { if (!closed) m.Play(); }
        public void Pause() { if (!closed) m.Pause(); }
        public TimeSpan Position { get { return m.Position; } set { if (!closed) m.Position = value; } }
        public TimeSpan? KnownDuration { get { return m.NaturalDuration.HasTimeSpan ? (TimeSpan?)m.NaturalDuration.TimeSpan : null; } }
        public double Volume { set { m.Volume = value; } }
        public bool IsMuted { set { m.IsMuted = value; } }
        public void Close()
        {
            closed = true;
            try { m.Stop(); } catch { }
            try { m.Close(); } catch { }
            m.Source = null;
        }
    }

    // ---- Media Foundation interop (Media Engine in "frame server" mode, frames copied via WIC)
    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr pValue);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint v);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong v);
        [PreserveSig] int GetDouble(ref Guid key, out double v);
        [PreserveSig] int GetGUID(ref Guid key, out Guid v);
        [PreserveSig] int GetStringLength(ref Guid key, out uint len);
        [PreserveSig] int GetString(ref Guid key, IntPtr buf, uint size, out uint len);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr s, out uint len);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr buf, uint size, out uint blobSize);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr buf, out uint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint v);
        [PreserveSig] int SetUINT64(ref Guid key, ulong v);
        [PreserveSig] int SetDouble(ref Guid key, double v);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid v);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string v);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr buf, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object unk);
    }

    [ComImport, Guid("98A1B0BB-03EB-4935-AE7C-93C1FA0E1C93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaEngine
    {
        [PreserveSig] int GetError(out IntPtr ppError);
        [PreserveSig] int SetErrorCode(int error);
        [PreserveSig] int SetSourceElements(IntPtr elements);
        [PreserveSig] int SetSource([MarshalAs(UnmanagedType.BStr)] string url);
        [PreserveSig] int GetCurrentSource(out IntPtr url);
        [PreserveSig] ushort GetNetworkState();
        [PreserveSig] int GetPreload();
        [PreserveSig] int SetPreload(int preload);
        [PreserveSig] int GetBuffered(out IntPtr ranges);
        [PreserveSig] int Load();
        [PreserveSig] int CanPlayType([MarshalAs(UnmanagedType.BStr)] string type, out int answer);
        [PreserveSig] ushort GetReadyState();
        [PreserveSig] int IsSeeking();
        [PreserveSig] double GetCurrentTime();
        [PreserveSig] int SetCurrentTime(double seconds);
        [PreserveSig] double GetStartTime();
        [PreserveSig] double GetDuration();
        [PreserveSig] int IsPaused();
        [PreserveSig] double GetDefaultPlaybackRate();
        [PreserveSig] int SetDefaultPlaybackRate(double rate);
        [PreserveSig] double GetPlaybackRate();
        [PreserveSig] int SetPlaybackRate(double rate);
        [PreserveSig] int GetPlayed(out IntPtr ranges);
        [PreserveSig] int GetSeekable(out IntPtr ranges);
        [PreserveSig] int IsEnded();
        [PreserveSig] int GetAutoPlay();
        [PreserveSig] int SetAutoPlay(int autoPlay);
        [PreserveSig] int GetLoop();
        [PreserveSig] int SetLoop(int loop);
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int GetMuted();
        [PreserveSig] int SetMuted(int muted);
        [PreserveSig] double GetVolume();
        [PreserveSig] int SetVolume(double volume);
        [PreserveSig] int HasVideo();
        [PreserveSig] int HasAudio();
        [PreserveSig] int GetNativeVideoSize(out uint cx, out uint cy);
        [PreserveSig] int GetVideoAspectRatio(out uint cx, out uint cy);
        [PreserveSig] int Shutdown();
        [PreserveSig] int TransferVideoFrame([MarshalAs(UnmanagedType.IUnknown)] object dstSurface, IntPtr srcRect, ref Native.RECT dstRect, ref MFARGB borderColor);
        [PreserveSig] int OnVideoStreamTick(out long pts);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MFARGB { public byte Blue, Green, Red, Alpha; }

    [ComImport, Guid("4D645ACE-26AA-4688-9BE1-DF3516990B93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaEngineClassFactory
    {
        [PreserveSig] int CreateInstance(uint flags, IMFAttributes attr, out IMFMediaEngine engine);
    }

    [ComImport, Guid("FEE7C112-E776-42B5-9BBF-0048524E2BD5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMFMediaEngineNotify
    {
        [PreserveSig] int EventNotify(uint ev, IntPtr param1, uint param2);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public class MfNotify : IMFMediaEngineNotify
    {
        public Action<uint, uint> Handler;
        public int EventNotify(uint ev, IntPtr param1, uint param2)
        {
            var h = Handler;
            if (h != null) { try { h(ev, param2); } catch { } }
            return 0;
        }
    }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICImagingFactory
    {
        [PreserveSig] int CreateDecoderFromFilename(IntPtr a, IntPtr b, uint c, int d, out IntPtr e);
        [PreserveSig] int CreateDecoderFromStream(IntPtr a, IntPtr b, int c, out IntPtr d);
        [PreserveSig] int CreateDecoderFromFileHandle(IntPtr a, IntPtr b, int c, out IntPtr d);
        [PreserveSig] int CreateComponentInfo(ref Guid a, out IntPtr b);
        [PreserveSig] int CreateDecoder(ref Guid a, IntPtr b, out IntPtr c);
        [PreserveSig] int CreateEncoder(ref Guid a, IntPtr b, out IntPtr c);
        [PreserveSig] int CreatePalette(out IntPtr a);
        [PreserveSig] int CreateFormatConverter(out IntPtr a);
        [PreserveSig] int CreateBitmapScaler(out IntPtr a);
        [PreserveSig] int CreateBitmapClipper(out IntPtr a);
        [PreserveSig] int CreateBitmapFlipRotator(out IntPtr a);
        [PreserveSig] int CreateStream(out IntPtr a);
        [PreserveSig] int CreateColorContext(out IntPtr a);
        [PreserveSig] int CreateColorTransformer(out IntPtr a);
        [PreserveSig] int CreateBitmap(uint width, uint height, ref Guid pixelFormat, int cacheOption, out IWICBitmap bitmap);
    }

    [ComImport, Guid("00000121-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWICBitmap
    {
        [PreserveSig] int GetSize(out uint width, out uint height);
        [PreserveSig] int GetPixelFormat(out Guid format);
        [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
        [PreserveSig] int CopyPalette(IntPtr palette);
        [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
    }

    class ModernPlayer : IVideoPlayer
    {
        static bool mfStarted;
        static readonly Guid MF_MEDIA_ENGINE_CALLBACK = new Guid("C60381B8-83A4-41F8-A3D0-DE05076849A9");
        static readonly Guid MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT = new Guid("5066893C-8CF9-42BC-8B8A-472212E52726");
        static readonly Guid CLSID_MFMediaEngineClassFactory = new Guid("B44392DA-499B-446B-A4CB-005FEAD0E6D5");
        static readonly Guid CLSID_WICImagingFactory = new Guid("CACAF262-9370-4615-A13B-9F5539DA4C0A");
        static readonly Guid GUID_WICPixelFormat32bppBGRA = new Guid("6FDDC324-4E03-4BFE-B185-3D77768DC90F");
        const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
        const uint EV_ERROR = 5, EV_LOADEDMETADATA = 10, EV_FORMATCHANGE = 1000;

        [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);

        readonly Image image = new Image();
        readonly Dispatcher dispatcher;
        IMFMediaEngine engine;
        MfNotify notify;
        IWICImagingFactory wic;
        IWICBitmap frame;
        WriteableBitmap wb;
        int fw, fh;
        TimeSpan? startAt;
        bool startPaused, closed, ready;
        double volume = 0.5;
        bool muted;
        public event Action Opened;
        public event Action<string> Failed;

        public ModernPlayer()
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            image.Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        }

        public FrameworkElement View { get { return image; } }
        public bool IsModern { get { return true; } }

        static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed (0x" + hr.ToString("X8") + ")", hr);
        }

        public void Open(string path, TimeSpan? at, bool paused)
        {
            startAt = at; startPaused = paused;
            if (!mfStarted) { Check(MFStartup(0x20070, 0), "MFStartup"); mfStarted = true; }

            IMFAttributes attr;
            Check(MFCreateAttributes(out attr, 2), "MFCreateAttributes");
            notify = new MfNotify();
            notify.Handler = delegate (uint ev, uint p2) { dispatcher.BeginInvoke(new Action(delegate { OnEngineEvent(ev, p2); })); };
            Guid k1 = MF_MEDIA_ENGINE_CALLBACK;
            Check(attr.SetUnknown(ref k1, notify), "SetUnknown(callback)");
            Guid k2 = MF_MEDIA_ENGINE_VIDEO_OUTPUT_FORMAT;
            Check(attr.SetUINT32(ref k2, DXGI_FORMAT_B8G8R8A8_UNORM), "SetUINT32(format)");

            var factory = (IMFMediaEngineClassFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MFMediaEngineClassFactory));
            IMFMediaEngine e;
            Check(factory.CreateInstance(0, attr, out e), "Create media engine");
            Marshal.ReleaseComObject(factory);
            Marshal.ReleaseComObject(attr);
            engine = e;

            engine.SetVolume(volume);
            engine.SetMuted(muted ? 1 : 0);
            engine.SetLoop(1);
            engine.SetAutoPlay(paused ? 0 : 1);
            Check(engine.SetSource(path), "SetSource");
            CompositionTarget.Rendering += OnRendering;
        }

        void OnEngineEvent(uint ev, uint param2)
        {
            if (closed || engine == null) return;
            if (ev == EV_LOADEDMETADATA || ev == EV_FORMATCHANGE)
            {
                uint w, h;
                if (engine.GetNativeVideoSize(out w, out h) == 0 && w > 0 && h > 0) SetupFrame((int)w, (int)h);
                if (ev == EV_LOADEDMETADATA)
                {
                    if (startAt.HasValue) { engine.SetCurrentTime(startAt.Value.TotalSeconds); startAt = null; }
                    ready = true;
                    if (Opened != null) Opened();
                }
            }
            else if (ev == EV_ERROR)
            {
                if (Failed != null) Failed("Windows couldn't play it (0x" + param2.ToString("X8") + ")");
            }
        }

        void SetupFrame(int w, int h)
        {
            // Keep the copy small enough to be cheap; the popup scales it anyway.
            double scale = Math.Min(1.0, Math.Min(1280.0 / w, 720.0 / h));
            int nw = Math.Max(2, (int)(w * scale)), nh = Math.Max(2, (int)(h * scale));
            if (frame != null && nw == fw && nh == fh) return;
            if (wic == null) wic = (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_WICImagingFactory));
            if (frame != null) { Marshal.ReleaseComObject(frame); frame = null; }
            Guid fmt = GUID_WICPixelFormat32bppBGRA;
            IWICBitmap bmp;
            Check(wic.CreateBitmap((uint)nw, (uint)nh, ref fmt, 2 /*CacheOnLoad*/, out bmp), "CreateBitmap");
            frame = bmp; fw = nw; fh = nh;
            wb = new WriteableBitmap(fw, fh, 96, 96, PixelFormats.Bgr32, null);
            image.Source = wb;
        }

        void OnRendering(object sender, EventArgs e)
        {
            if (closed || engine == null || frame == null) return;
            try
            {
                long pts;
                if (engine.OnVideoStreamTick(out pts) != 0) return;   // no new frame
                var dst = new Native.RECT();
                dst.Right = fw; dst.Bottom = fh;
                var border = new MFARGB();
                border.Alpha = 255;
                if (engine.TransferVideoFrame(frame, IntPtr.Zero, ref dst, ref border) != 0) return;
                wb.Lock();
                try
                {
                    frame.CopyPixels(IntPtr.Zero, (uint)wb.BackBufferStride, (uint)(wb.BackBufferStride * fh), wb.BackBuffer);
                    wb.AddDirtyRect(new Int32Rect(0, 0, fw, fh));
                }
                finally { wb.Unlock(); }
            }
            catch { }
        }

        public void Play() { if (engine != null && !closed) engine.Play(); }
        public void Pause() { if (engine != null && !closed) engine.Pause(); }

        public TimeSpan Position
        {
            get
            {
                if (engine == null || closed) return TimeSpan.Zero;
                if (!ready && startAt.HasValue) return startAt.Value;
                double t = engine.GetCurrentTime();
                return double.IsNaN(t) || t < 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(t);
            }
            set
            {
                if (engine == null || closed) return;
                if (!ready) { startAt = value; return; }
                engine.SetCurrentTime(value.TotalSeconds);
            }
        }

        public TimeSpan? KnownDuration
        {
            get
            {
                if (engine == null || closed) return null;
                double d = engine.GetDuration();
                if (double.IsNaN(d) || double.IsInfinity(d) || d <= 0) return null;
                return TimeSpan.FromSeconds(d);
            }
        }

        public double Volume { set { volume = value; if (engine != null && !closed) engine.SetVolume(value); } }
        public bool IsMuted { set { muted = value; if (engine != null && !closed) engine.SetMuted(value ? 1 : 0); } }

        public void Close()
        {
            if (closed) return;
            closed = true;
            CompositionTarget.Rendering -= OnRendering;
            if (notify != null) notify.Handler = null;
            if (engine != null)
            {
                try { engine.Shutdown(); } catch { }      // releases the file
                try { Marshal.ReleaseComObject(engine); } catch { }
                engine = null;
            }
            if (frame != null) { try { Marshal.ReleaseComObject(frame); } catch { } frame = null; }
            if (wic != null) { try { Marshal.ReleaseComObject(wic); } catch { } wic = null; }
            image.Source = null;
        }
    }

    // ------------------------------------------------------------------ The popup
    class PreviewWindow : Window
    {
        const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
        const string GlyphVolume = "\uE767", GlyphMute = "\uE74F", GlyphPin = "\uE718", GlyphPinned = "\uE840",
                     GlyphPlay = "\uE768", GlyphPause = "\uE769", GlyphStar = "\uE734", GlyphStarFill = "\uE735", GlyphClose = "\uE711";
        static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D));

        readonly Settings s;
        IVideoPlayer media;             // a fresh player for every video (classic or modern engine)
        readonly Grid previewHost = new Grid();
        readonly Image photo = new Image { Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        readonly TextBlock titleText, errorText, timeText, volText, counterText, osdText;
        readonly DispatcherTimer osdTimer;
        readonly Border topBar, bottomBar;
        readonly Grid progressTrack;
        readonly System.Windows.Shapes.Rectangle progressFill, thinFill;
        readonly Slider volSlider;
        readonly Button muteBtn, pinBtn, playBtn;
        readonly DispatcherTimer progressTimer;
        IntPtr hwnd;
        bool paused, mouseDownInside, inSizeMove;
        readonly Button[] starBtns = new Button[5];
        int currentRating = 0;          // 0..5 for the current file
        bool savingRating;
        TimeSpan? fileDuration;         // length from Windows' file info (used if the player doesn't report one)
        TimeSpan seekTarget;            // last requested jump
        bool seekRetried;               // already tried "reopen at position" for this video
        DispatcherTimer seekCheck;
        int previewSession;

        public string CurrentPath { get; private set; }
        public bool IsPhoto { get; private set; }
        /// <summary>Pinned by the user: stays open without Shift.</summary>
        public bool Sticky { get; private set; }
        public event Action PinnedChanged;
        public event Action CloseRequested;

        public PreviewWindow(Settings settings)
        {
            s = settings;
            Title = "ShiftPreview";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Background = Brushes.Black;
            Width = s.Width; Height = s.Height;
            MinWidth = 200; MinHeight = 120;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -30000; Top = -30000;

            var chrome = new WindowChrome();
            chrome.CaptionHeight = 0;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.GlassFrameThickness = new Thickness(0);
            chrome.CornerRadius = new CornerRadius(0);
            chrome.UseAeroCaptionButtons = false;
            WindowChrome.SetWindowChrome(this, chrome);

            var root = new Grid();
            var outline = new Border();
            outline.BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
            outline.BorderThickness = new Thickness(1);
            outline.Child = root;
            Content = outline;

            root.Children.Add(previewHost);
            previewHost.Children.Add(photo);

            // Click on the video = pause/play
            var clickLayer = new Border();
            clickLayer.Background = Brushes.Transparent;
            clickLayer.MouseLeftButtonUp += delegate { TogglePause(); };
            root.Children.Add(clickLayer);

            errorText = new TextBlock();
            errorText.Foreground = Brushes.White;
            errorText.TextWrapping = TextWrapping.Wrap;
            errorText.TextAlignment = TextAlignment.Center;
            errorText.HorizontalAlignment = HorizontalAlignment.Center;
            errorText.VerticalAlignment = VerticalAlignment.Center;
            errorText.Margin = new Thickness(16);
            errorText.Visibility = Visibility.Collapsed;
            errorText.IsHitTestVisible = false;
            root.Children.Add(errorText);

            // Short on-screen message (used when skipping with Shift+Z / Shift+X)
            osdText = new TextBlock();
            osdText.Foreground = Brushes.White;
            osdText.Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0));
            osdText.FontSize = 18;
            osdText.TextAlignment = TextAlignment.Center;
            osdText.TextWrapping = TextWrapping.Wrap;
            osdText.Margin = new Thickness(20);
            osdText.Padding = new Thickness(12, 6, 12, 6);
            osdText.HorizontalAlignment = HorizontalAlignment.Center;
            osdText.VerticalAlignment = VerticalAlignment.Center;
            osdText.IsHitTestVisible = false;
            osdText.Visibility = Visibility.Collapsed;
            root.Children.Add(osdText);
            osdTimer = new DispatcherTimer();
            osdTimer.Interval = TimeSpan.FromMilliseconds(900);
            osdTimer.Tick += delegate { osdTimer.Stop(); osdText.Visibility = Visibility.Collapsed; };

            // Top bar: file name (drag to move) + pin button
            topBar = new Border();
            topBar.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
            topBar.VerticalAlignment = VerticalAlignment.Top;
            topBar.Height = 32;
            topBar.Cursor = Cursors.SizeAll;
            var topDock = new DockPanel();
            var closeBtn = MakeIconButton(GlyphClose, "Close preview (Esc)");
            closeBtn.Click += delegate { if (CloseRequested != null) CloseRequested(); };
            DockPanel.SetDock(closeBtn, Dock.Right);
            topDock.Children.Add(closeBtn);
            pinBtn = MakeIconButton(GlyphPin, "Pin: keep the preview open without holding Shift\n(tap Shift, press Esc or click X to close)");
            pinBtn.Click += delegate { SetSticky(!Sticky, true); };
            DockPanel.SetDock(pinBtn, Dock.Right);
            topDock.Children.Add(pinBtn);
            counterText = new TextBlock();
            counterText.Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));
            counterText.VerticalAlignment = VerticalAlignment.Center;
            counterText.Margin = new Thickness(6, 0, 4, 0);
            counterText.ToolTip = "Shift+C / Shift+V: previous / next photo or video in Explorer's order";
            DockPanel.SetDock(counterText, Dock.Right);
            topDock.Children.Add(counterText);
            var starPanel = new StackPanel();
            starPanel.Orientation = Orientation.Horizontal;
            starPanel.Margin = new Thickness(0, 0, 4, 0);
            for (int i = 0; i < 5; i++)
            {
                int stars = i + 1;
                var b = MakeIconButton(GlyphStar, stars + (stars == 1 ? " star" : " stars") + "  (Shift+" + stars + ")\nClick the current rating again to clear it (Shift+0)");
                b.Width = 22;
                b.FontSize = 13;
                b.Click += delegate { SetRating(stars == currentRating ? 0 : stars); };
                b.MouseEnter += delegate { ShowStars(stars); };
                starBtns[i] = b;
                starPanel.Children.Add(b);
            }
            starPanel.MouseLeave += delegate { ShowStars(currentRating); };
            DockPanel.SetDock(starPanel, Dock.Right);
            topDock.Children.Add(starPanel);
            titleText = new TextBlock();
            titleText.Foreground = Brushes.White;
            titleText.VerticalAlignment = VerticalAlignment.Center;
            titleText.Margin = new Thickness(10, 0, 6, 0);
            titleText.TextTrimming = TextTrimming.CharacterEllipsis;
            topDock.Children.Add(titleText);
            topBar.Child = topDock;
            topBar.MouseLeftButtonDown += TopBar_MouseLeftButtonDown;
            root.Children.Add(topBar);

            // Bottom bar: progress (click to seek), play/pause, mute, volume slider, time
            bottomBar = new Border();
            bottomBar.Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
            bottomBar.VerticalAlignment = VerticalAlignment.Bottom;
            var bottomStack = new StackPanel();

            progressTrack = new Grid();
            progressTrack.Height = 6;
            progressTrack.Margin = new Thickness(10, 8, 10, 2);
            progressTrack.Background = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
            progressTrack.Cursor = Cursors.Hand;
            progressFill = new System.Windows.Shapes.Rectangle();
            progressFill.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xA3, 0xFF));
            progressFill.HorizontalAlignment = HorizontalAlignment.Left;
            progressFill.Width = 0;
            progressTrack.Children.Add(progressFill);
            progressTrack.MouseLeftButtonDown += delegate (object o, MouseButtonEventArgs e)
            {
                if (progressTrack.ActualWidth > 0) Seek(e.GetPosition(progressTrack).X / progressTrack.ActualWidth);
                e.Handled = true;
            };
            bottomStack.Children.Add(progressTrack);

            var controls = new DockPanel();
            controls.Margin = new Thickness(4, 0, 8, 4);
            controls.LastChildFill = false;

            playBtn = MakeIconButton(GlyphPause, "Play / pause");
            playBtn.Click += delegate { TogglePause(); };
            controls.Children.Add(playBtn);

            muteBtn = MakeIconButton(s.Muted ? GlyphMute : GlyphVolume, "Mute");
            muteBtn.Click += delegate { SetMuted(!s.Muted); };
            controls.Children.Add(muteBtn);

            volSlider = new Slider();
            volSlider.Minimum = 0; volSlider.Maximum = 100;
            volSlider.Value = s.Volume;
            volSlider.Width = 110;
            volSlider.VerticalAlignment = VerticalAlignment.Center;
            volSlider.IsMoveToPointEnabled = true;
            volSlider.SmallChange = 5; volSlider.LargeChange = 10;
            volSlider.ValueChanged += delegate { ApplyVolume((int)Math.Round(volSlider.Value)); };
            controls.Children.Add(volSlider);

            volText = new TextBlock();
            volText.Foreground = Brushes.White;
            volText.VerticalAlignment = VerticalAlignment.Center;
            volText.Margin = new Thickness(6, 0, 0, 0);
            volText.Width = 40;
            controls.Children.Add(volText);

            timeText = new TextBlock();
            timeText.Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            timeText.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(timeText, Dock.Right);
            controls.Children.Add(timeText);

            bottomStack.Children.Add(controls);
            bottomBar.Child = bottomStack;
            root.Children.Add(bottomBar);

            // Thin progress line shown when the mouse isn't over the popup
            thinFill = new System.Windows.Shapes.Rectangle();
            thinFill.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xA3, 0xFF));
            thinFill.Height = 3;
            thinFill.HorizontalAlignment = HorizontalAlignment.Left;
            thinFill.VerticalAlignment = VerticalAlignment.Bottom;
            thinFill.IsHitTestVisible = false;
            root.Children.Add(thinFill);

            UpdateVolumeUi();
            SetBars(false);

            MouseEnter += delegate { SetBars(true); };
            MouseLeave += delegate { SetBars(false); };
            MouseWheel += delegate (object o, MouseWheelEventArgs e)
            {
                if (media == null) return;
                volSlider.Value = Math.Max(0, Math.Min(100, volSlider.Value + (e.Delta > 0 ? 5 : -5)));
                e.Handled = true;
            };
            PreviewMouseLeftButtonDown += delegate { mouseDownInside = true; };
            SizeChanged += delegate
            {
                if (IsVisible) { s.Width = Width; s.Height = Height; }
                UpdateProgress();
            };

            progressTimer = new DispatcherTimer();
            progressTimer.Interval = TimeSpan.FromMilliseconds(200);
            progressTimer.Tick += delegate { UpdateProgress(); };

            SourceInitialized += OnSourceInitialized;
            new WindowInteropHelper(this).EnsureHandle();
        }

        void ClosePlayer()
        {
            IVideoPlayer old = media;
            if (old == null) return;
            media = null;
            try { old.Close(); } catch { }
            previewHost.Children.Remove(old.View);
        }

        // Every video gets a brand-new player; the old one is shut down completely.
        void ReplacePlayer(bool modern)
        {
            ClosePlayer();
            IVideoPlayer p = modern ? (IVideoPlayer)new ModernPlayer() : new ClassicPlayer();
            p.Volume = s.Volume / 100.0;
            p.IsMuted = s.Muted;
            p.Opened += delegate
            {
                if (p != media) return;
                errorText.Visibility = Visibility.Collapsed;
                UpdateProgress();
            };
            p.Failed += delegate (string msg)
            {
                if (p != media) return;
                Log.Write("Playback failed (" + (p.IsModern ? "modern" : "classic") + " player): " + msg + " - " + CurrentPath);
                errorText.Text = "Can't play this file.\n" + msg + (p.IsModern ? "" :
                    "\n\nThe format may need a codec, or the optional Windows feature\n\"Windows Media Player Legacy\" may be turned off.");
                errorText.Visibility = Visibility.Visible;
            };
            media = p;
            previewHost.Children.Add(p.View);
        }

        // Opens a video in a new player. If the modern engine can't start, falls back to the classic one.
        void OpenVideo(string path, TimeSpan? startAt, bool startPaused, bool modern)
        {
            ReplacePlayer(modern);
            try { media.Open(path, startAt, startPaused); }
            catch (Exception ex)
            {
                if (!modern) throw;
                Log.Write("Modern player couldn't start: " + ex.Message);
                ShowOsd("Modern player unavailable:\n" + ex.Message, true);
                ReplacePlayer(false);
                media.Open(path, startAt, startPaused);
            }
            paused = startPaused;
            playBtn.Content = paused ? GlyphPlay : GlyphPause;
        }

        static Button MakeIconButton(string glyph, string tip)
        {
            var t = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            t.VisualTree = bd;

            var b = new Button();
            b.Template = t;
            b.Content = glyph;
            b.FontFamily = new FontFamily(IconFont);
            b.FontSize = 15;
            b.Foreground = Brushes.White;
            b.Background = Brushes.Transparent;
            b.Width = 32; b.Height = 28;
            b.Cursor = Cursors.Arrow;
            b.Focusable = false;
            b.ToolTip = tip;
            var hover = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            b.MouseEnter += delegate { b.Background = hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            return b;
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
            var src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(WndProc);
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            const int WM_MOUSEACTIVATE = 0x0021, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232, MA_NOACTIVATE = 3;
            if (msg == WM_MOUSEACTIVATE) { handled = true; return new IntPtr(MA_NOACTIVATE); }
            if (msg == WM_ENTERSIZEMOVE) inSizeMove = true;
            if (msg == WM_EXITSIZEMOVE) { inSizeMove = false; s.Save(); }
            return IntPtr.Zero;
        }

        void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch { return; }
            // Dragging the popup somewhere means "keep it here".
            Native.RECT r;
            if (Native.GetWindowRect(hwnd, out r))
            {
                s.PinX = r.Left; s.PinY = r.Top; s.HasPinnedPos = true;
            }
            SetPinned(true);
        }

        public void SetPinned(bool pinned)
        {
            if (pinned && !s.HasPinnedPos)
            {
                Native.RECT r;
                if (IsVisible && Native.GetWindowRect(hwnd, out r)) { s.PinX = r.Left; s.PinY = r.Top; s.HasPinnedPos = true; }
            }
            s.Pinned = pinned;
            s.Save();
            if (PinnedChanged != null) PinnedChanged();
        }

        public void SetSticky(bool sticky, bool announce)
        {
            Sticky = sticky;
            pinBtn.Content = sticky ? GlyphPinned : GlyphPin;
            pinBtn.Foreground = sticky ? Gold : Brushes.White;
            if (announce) ShowOsd(sticky ? "Pinned. Tap Shift, press Esc or click X to close." : "Unpinned", true);
        }

        public void SetSize(double w, double h)
        {
            s.Width = w; s.Height = h;
            Width = w; Height = h;
            s.Save();
        }

        /// <summary>True while the user is dragging/resizing or holding the mouse button down inside the popup.</summary>
        public bool KeepOpen
        {
            get
            {
                if (mouseDownInside && !Native.KeyDown(Native.VK_LBUTTON)) mouseDownInside = false;
                return inSizeMove || mouseDownInside;
            }
        }

        /// <summary>Distance in pixels from a screen point to the popup (0 = inside, -1 = not visible).</summary>
        public double DistanceTo(Native.POINT p)
        {
            if (!IsVisible || hwnd == IntPtr.Zero) return -1;
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return -1;
            double dx = p.X < r.Left ? r.Left - p.X : (p.X >= r.Right ? p.X - r.Right + 1 : 0);
            double dy = p.Y < r.Top ? r.Top - p.Y : (p.Y >= r.Bottom ? p.Y - r.Bottom + 1 : 0);
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public bool ContainsScreenPoint(Native.POINT p)
        {
            if (!IsVisible || hwnd == IntPtr.Zero) return false;
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return false;
            return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
        }

        /// <summary>Shows "3 / 12" in the title bar (n = 0 hides it).</summary>
        public void SetCounter(int index, int n)
        {
            counterText.Text = n > 0 ? index + " / " + n : "";
        }

        public void ShowFile(string path, Native.POINT cursor, bool reposition)
        {
            if (IsVisible && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase)) return;

            CurrentPath = path;
            IsPhoto = Resolver.IsPhoto(path);
            previewSession++;
            titleText.Text = Path.GetFileName(path);
            errorText.Visibility = Visibility.Collapsed;
            osdTimer.Stop(); osdText.Visibility = Visibility.Collapsed;
            progressFill.Width = 0; thinFill.Width = 0; timeText.Text = "";
            currentRating = 0; ShowStars(0);
            LoadRating(path);
            fileDuration = null; seekRetried = false;
            if (seekCheck != null) seekCheck.Stop();
            progressTimer.Stop();
            photo.Source = null;
            photo.Visibility = IsPhoto ? Visibility.Visible : Visibility.Collapsed;
            SetBars(IsMouseOver);

            if (reposition || !IsVisible) PositionNear(cursor);
            if (!IsVisible) Show();

            if (IsPhoto)
            {
                ClosePlayer();
                LoadPhoto(path, previewSession);
                return;
            }

            LoadDuration(path);
            try
            {
                OpenVideo(path, null, false, s.ModernEngine);
            }
            catch (Exception ex)
            {
                errorText.Text = "Can't play this file.\n" + ex.Message;
                errorText.Visibility = Visibility.Visible;
            }
            progressTimer.Start();
        }

        void LoadPhoto(string path, int session)
        {
            RunSta(delegate
            {
                BitmapSource bitmap = null;
                try { bitmap = DecodePhoto(path); }
                catch (Exception ex) { Log.Write("Photo preview failed: " + ex.Message + " - " + path); }

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (session != previewSession) return;
                    photo.Source = bitmap;
                    if (bitmap == null)
                    {
                        errorText.Text = "Can't open this photo.";
                        errorText.Visibility = Visibility.Visible;
                    }
                }));
            });
        }

        static BitmapSource DecodePhoto(string path)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                BitmapSource bitmap = frame;
                var metadata = frame.Metadata as BitmapMetadata;
                if (metadata != null)
                {
                    string query = metadata.Format == "jpg" ? "/app1/ifd/{ushort=274}" : "/ifd/{ushort=274}";
                    var orientation = metadata.GetQuery(query) as ushort?;
                    Transform transform = null;
                    switch (orientation)
                    {
                        case 2: transform = new ScaleTransform(-1, 1); break;
                        case 3: transform = new RotateTransform(180); break;
                        case 4: transform = new ScaleTransform(1, -1); break;
                        case 5: transform = new MatrixTransform(0, 1, 1, 0, 0, 0); break;
                        case 6: transform = new RotateTransform(90); break;
                        case 7: transform = new MatrixTransform(0, -1, -1, 0, 0, 0); break;
                        case 8: transform = new RotateTransform(270); break;
                    }
                    if (transform != null) bitmap = new TransformedBitmap(frame, transform);
                }
                bitmap.Freeze();
                return bitmap;
            }
        }

        public void HidePreview()
        {
            if (!IsVisible && CurrentPath == null) return;
            progressTimer.Stop();
            if (seekCheck != null) seekCheck.Stop();
            previewSession++;
            ClosePlayer();
            photo.Source = null;
            CurrentPath = null;
            counterText.Text = "";
            SetSticky(false, false);
            osdTimer.Stop(); osdText.Visibility = Visibility.Collapsed;
            mouseDownInside = false;
            SetBars(false);
            Hide();
            s.Save();
        }

        void PositionNear(Native.POINT cursor)
        {
            Native.POINT anchor = cursor;
            bool usePinned = s.Pinned && s.HasPinnedPos;
            if (usePinned) { anchor.X = s.PinX + 20; anchor.Y = s.PinY + 20; }

            IntPtr mon = Native.MonitorFromPoint(anchor, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.GetMonitorInfo(mon, ref mi);
            Native.RECT wa = mi.rcWork;

            double scale = 1.0;
            try
            {
                uint dx, dy;
                if (Native.GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) scale = dx / 96.0;
            }
            catch { }
            int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);

            int x, y;
            if (usePinned) { x = s.PinX; y = s.PinY; }
            else
            {
                const int gap = 18;
                x = cursor.X + gap; y = cursor.Y + gap;
                if (x + w > wa.Right) x = cursor.X - gap - w;
                if (y + h > wa.Bottom) y = cursor.Y - gap - h;
            }
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void SetBars(bool show)
        {
            topBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            bottomBar.Visibility = show && !IsPhoto ? Visibility.Visible : Visibility.Collapsed;
            thinFill.Visibility = !show && !IsPhoto ? Visibility.Visible : Visibility.Collapsed;
        }

        public void TogglePause()
        {
            if (media == null) return;
            if (paused) { media.Play(); paused = false; playBtn.Content = GlyphPause; }
            else { media.Pause(); paused = true; playBtn.Content = GlyphPlay; }
        }

        void ShowStars(int n)
        {
            for (int i = 0; i < 5; i++)
            {
                starBtns[i].Content = i < n ? GlyphStarFill : GlyphStar;
                starBtns[i].Foreground = i < n ? Gold : Brushes.White;
            }
        }

        static string StarText(int n)
        {
            return n == 0 ? "Rating cleared" : new string('\u2605', n) + new string('\u2606', 5 - n);
        }

        static void RunSta(ThreadStart work)
        {
            var t = new Thread(work);
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void LoadRating(string path)
        {
            RunSta(delegate
            {
                int stars = RatingStore.Read(path);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (stars >= 0 && !savingRating && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        currentRating = stars;
                        ShowStars(stars);
                    }
                }));
            });
        }

        /// <summary>Saves 0..5 stars to the current file (0 clears the rating).</summary>
        public void SetRating(int stars)
        {
            if (CurrentPath == null || savingRating) return;
            string path = CurrentPath;
            TimeSpan? pos = Duration.HasValue ? (TimeSpan?)media.Position : null;
            bool wasPaused = paused;
            bool wasModern = media != null && media.IsModern;

            savingRating = true;
            ShowStars(stars);
            ShowOsd("Saving rating...", false);

            // Release the video file while Windows writes the rating.
            ClosePlayer();

            RunSta(delegate
            {
                if (!Resolver.IsPhoto(path)) Thread.Sleep(300);
                string error = RatingStore.Write(path, stars);
                Log.Write(error == null ? "Rated " + stars + " stars: " + path : "Rating failed: " + error);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    savingRating = false;
                    bool stillHere = IsVisible && string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase);
                    if (stillHere && !IsPhoto && media == null)
                    {
                        try { OpenVideo(path, pos, wasPaused, wasModern); } catch { }
                    }
                    if (error == null)
                    {
                        if (stillHere) { currentRating = stars; ShowStars(stars); ShowOsd(StarText(stars), true); }
                    }
                    else if (stillHere)
                    {
                        ShowStars(currentRating);
                        ShowOsd("Couldn't save rating:\n" + error, true);
                    }
                }));
            });
        }

        void ShowOsd(string text, bool autoHide)
        {
            osdText.Text = text;
            osdText.Visibility = Visibility.Visible;
            osdTimer.Stop();
            if (autoHide)
            {
                osdTimer.Interval = TimeSpan.FromMilliseconds(text.Length > 40 ? 5000 : text.Length > 20 ? 2500 : 900);
                osdTimer.Start();
            }
        }

        /// <summary>Jump forward (positive) or back (negative) by the given number of seconds.</summary>
        /// <summary>Video length: from the player if it knows it, otherwise from Windows' file info.</summary>
        TimeSpan? Duration
        {
            get
            {
                if (media == null) return null;
                TimeSpan? d = media.KnownDuration;
                if (d.HasValue && d.Value.TotalSeconds > 0) return d;
                return fileDuration;
            }
        }

        void LoadDuration(string path)
        {
            RunSta(delegate
            {
                TimeSpan? d = RatingStore.ReadDuration(path);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (string.Equals(path, CurrentPath, StringComparison.OrdinalIgnoreCase)) { fileDuration = d; UpdateProgress(); }
                }));
            });
        }

        public void SeekBy(double seconds)
        {
            if (media == null) return;
            TimeSpan? dur = Duration;
            double target = media.Position.TotalSeconds + seconds;
            if (dur.HasValue) target = Math.Min(dur.Value.TotalSeconds - 0.5, target);
            target = Math.Max(0, target);
            SeekTo(TimeSpan.FromSeconds(target));
            ShowOsd((seconds >= 0 ? "+" : "-") + Math.Abs(seconds) + " s   " +
                    Fmt(TimeSpan.FromSeconds(target)) + (dur.HasValue ? " / " + Fmt(dur.Value) : ""), true);
        }

        void Seek(double frac)
        {
            TimeSpan? dur = Duration;
            if (CurrentPath == null || !dur.HasValue) return;
            frac = Math.Max(0, Math.Min(1, frac));
            SeekTo(TimeSpan.FromTicks((long)(dur.Value.Ticks * frac)));
        }

        // Jumps to a position and checks that it actually happened. Some mp4 files ignore a jump
        // while playing; for those the video is reopened directly at the wanted spot.
        void SeekTo(TimeSpan target)
        {
            seekTarget = target;
            media.Position = target;
            UpdateProgress();
            if (seekCheck == null)
            {
                seekCheck = new DispatcherTimer();
                seekCheck.Interval = TimeSpan.FromMilliseconds(700);
                seekCheck.Tick += delegate { seekCheck.Stop(); VerifySeek(); };
            }
            seekCheck.Stop();
            seekCheck.Interval = TimeSpan.FromMilliseconds(700);
            seekCheck.Start();
        }

        void VerifySeek()
        {
            if (media == null || savingRating) return;
            double off = Math.Abs((media.Position - seekTarget).TotalSeconds);
            if (off <= 3) return;                       // the jump worked (playback moved on a little)

            Log.Write("Seek ignored by " + (media.IsModern ? "modern" : "classic") + " player (wanted " + Fmt(seekTarget) +
                      ", at " + Fmt(media.Position) + ", length known: " + media.KnownDuration.HasValue + "): " + CurrentPath);
            if (media.IsModern || seekRetried)
            {
                ShowOsd("This video won't let the player jump.", true);
                return;
            }
            // The classic player refused: switch this video to the modern engine at the wanted spot.
            seekRetried = true;
            ShowOsd("Switching to the modern player...", true);
            try { OpenVideo(CurrentPath, seekTarget, paused, true); }
            catch (Exception ex) { Log.Write("Switch to modern player failed: " + ex.Message); }
            seekCheck.Stop();
            seekCheck.Interval = TimeSpan.FromMilliseconds(1500);  // give the reopen time before re-checking
            seekCheck.Start();
        }

        void SetMuted(bool muted)
        {
            s.Muted = muted;
            if (media != null) media.IsMuted = muted;
            UpdateVolumeUi();
            s.Save();
        }

        void ApplyVolume(int v)
        {
            s.Volume = v;
            if (media != null) media.Volume = v / 100.0;
            if (v > 0 && s.Muted)
            {
                s.Muted = false;
                if (media != null) media.IsMuted = false;
            }
            UpdateVolumeUi();
        }

        void UpdateVolumeUi()
        {
            muteBtn.Content = (s.Muted || s.Volume == 0) ? GlyphMute : GlyphVolume;
            volText.Text = s.Muted ? "muted" : s.Volume + "%";
        }

        void UpdateProgress()
        {
            TimeSpan? d = Duration;
            if (!d.HasValue || d.Value.TotalMilliseconds <= 0)
            {
                progressFill.Width = 0; thinFill.Width = 0;
                timeText.Text = media != null ? Fmt(media.Position) : "";
                return;
            }
            TimeSpan dur = d.Value, pos = media.Position;
            double frac = Math.Max(0, Math.Min(1, pos.TotalMilliseconds / dur.TotalMilliseconds));
            progressFill.Width = progressTrack.ActualWidth * frac;
            double inner = ActualWidth - 2; // inside the 1px outline
            thinFill.Width = Math.Max(0, inner * frac);
            timeText.Text = Fmt(pos) + " / " + Fmt(dur);
        }

        static string Fmt(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }
    }

    // ------------------------------------------------------------------ Glue: Shift polling, tray icon
    class Controller
    {
        readonly Settings s;
        readonly PreviewWindow popup;
        readonly Resolver resolver;
        readonly KeyHook keyHook;
        readonly DispatcherTimer timer;
        WinForms.NotifyIcon tray;
        WinForms.ToolStripMenuItem pinnedItem;
        DateTime shiftSince = DateTime.MinValue, lastValid = DateTime.MinValue, lastOverPopup = DateTime.MinValue;

        // "Heading for the player" detection: while the mouse is moving toward the popup, files it
        // crosses on the way are ignored, so the preview doesn't switch before you get there.
        double prevPopupDist = -1;
        DateTime lastTowardPopup = DateTime.MinValue;
        const int TowardGraceMs = 300;   // the mouse must stop (or move away) this long before switching

        // Arrow-key navigation state
        IntPtr currentRoot;            // Explorer window the current file came from (Zero = desktop)
        string lastHoveredPath;        // the file the mouse is actually on
        string navAnchor;              // set after keyboard navigation: keep showing the chosen file while
                                       // the mouse stays on this (originally hovered) file
        List<string> siblings;         // cached folder order
        string siblingsKey;
        bool siblingsLoading;
        int pendingSteps, siblingsSession;

        public const string Version = "2.9 (photo previews)";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string AppName = "ShiftPreview";

        public Controller()
        {
            s = Settings.Load();
            popup = new PreviewWindow(s);
            resolver = new Resolver(Dispatcher.CurrentDispatcher, OnResolved);
            keyHook = new KeyHook(Dispatcher.CurrentDispatcher, OnKey);
            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(50);
            timer.Tick += delegate { Tick(); };
        }

        public void Start()
        {
            CreateTray();
            popup.CloseRequested += delegate { ClosePreview(); };
            popup.PinnedChanged += delegate { if (pinnedItem.Checked != s.Pinned) pinnedItem.Checked = s.Pinned; };
            timer.Start();
            if (!s.FirstRunDone)
            {
                tray.ShowBalloonTip(5000, "ShiftPreview is running",
                    "Hover a photo or video in File Explorer and hold Shift to preview it. Shift+C / Shift+V switch files. Right-click this icon for options.",
                    WinForms.ToolTipIcon.Info);
                s.FirstRunDone = true;
                s.Save();
            }
        }

        bool ShiftActive()
        {
            if (!s.Enabled || !Native.KeyDown(Native.VK_SHIFT)) return false;
            // Ignore Ctrl+Shift / Alt+Shift / Win+Shift shortcuts
            return !(Native.KeyDown(Native.VK_CONTROL) || Native.KeyDown(Native.VK_MENU) ||
                     Native.KeyDown(Native.VK_LWIN) || Native.KeyDown(Native.VK_RWIN));
        }

        // Closed on purpose (X button / Esc): restart the Shift hold delay so it doesn't
        // instantly pop open again on whatever is under the mouse.
        void ClosePreview()
        {
            HidePopup();
            shiftSince = DateTime.UtcNow;
        }

        void HidePopup()
        {
            popup.HidePreview();
            keyHook.Armed = false;
            navAnchor = null;
            ResetSiblings();
        }

        void Tick()
        {
            DateTime now = DateTime.UtcNow;
            Native.POINT p;
            Native.GetCursorPos(out p);
            bool active = ShiftActive();
            keyHook.Armed = popup.IsVisible;

            double dist = popup.DistanceTo(p);
            if (dist >= 0 && prevPopupDist >= 0 && dist < prevPopupDist - 1.5) lastTowardPopup = now;
            prevPopupDist = dist;

            if (!active) shiftSince = DateTime.MinValue;
            else if (shiftSince == DateTime.MinValue) shiftSince = now;

            // Shift+click (e.g. selecting files in Explorer) doesn't count as a "Shift tap".
            if (Native.KeyDown(Native.VK_SHIFT) && (Native.KeyDown(Native.VK_LBUTTON) || Native.KeyDown(0x02 /*VK_RBUTTON*/)))
                keyHook.OtherKeyDuringShift = true;

            // A Shift tap / Esc / X closes a pinned preview. Holding Shift over another file switches to it.
            if (popup.IsVisible && popup.Sticky)
            {
                if (active && !keyHook.OtherKeyDuringShift && !popup.ContainsScreenPoint(p) &&
                    (now - shiftSince).TotalMilliseconds >= Math.Max(s.HoldDelayMs, 400))
                    resolver.Request(p);
                return;
            }

            // Mouse on the popup (or dragging/resizing it): leave it alone so you can use its controls.
            if (popup.IsVisible && (popup.KeepOpen || popup.ContainsScreenPoint(p)))
            {
                lastOverPopup = now;
                return;
            }

            if (!active)
            {
                if (popup.IsVisible && (now - lastOverPopup).TotalMilliseconds > 250) HidePopup();
                return;
            }

            // A preview is open: it stays locked to its file while Shift is held. Hovering other
            // files does nothing - use Shift+C / Shift+V, or close it (Esc / X / let go of Shift).
            if (popup.IsVisible) return;

            if ((now - shiftSince).TotalMilliseconds < s.HoldDelayMs) return;
            resolver.Request(p);
        }

        void OnResolved(Native.POINT p, string path, IntPtr root)
        {
            if (!ShiftActive()) return;
            Native.POINT cur;
            Native.GetCursorPos(out cur);
            if (popup.IsVisible)
            {
                // Normal preview: locked, never switch by hovering.
                // Pinned preview: Shift held (alone) over another file switches to it and ends the pin.
                if (!popup.Sticky || path == null || keyHook.OtherKeyDuringShift) return;
                if (string.Equals(path, popup.CurrentPath, StringComparison.OrdinalIgnoreCase)) return;
                if (popup.ContainsScreenPoint(cur)) return;
                popup.SetSticky(false, false);
                lastValid = DateTime.UtcNow;
                navAnchor = null;
                lastHoveredPath = path;
                currentRoot = root;
                popup.ShowFile(path, p, true);
                keyHook.VideoKeys = !popup.IsPhoto;
                ResetSiblings(); popup.SetCounter(0, 0); FetchSiblings();
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (path != null)
            {
                lastValid = now;
                bool isOther = popup.IsVisible && !string.Equals(path, popup.CurrentPath, StringComparison.OrdinalIgnoreCase);
                if (isOther && (now - lastTowardPopup).TotalMilliseconds < TowardGraceMs) return; // on the way to the player
                // Still on the file we navigated away from: keep showing the file the user picked.
                if (navAnchor != null && popup.IsVisible && string.Equals(path, navAnchor, StringComparison.OrdinalIgnoreCase)) return;
                navAnchor = null;
                lastHoveredPath = path;
                bool changed = !popup.IsVisible || !string.Equals(path, popup.CurrentPath, StringComparison.OrdinalIgnoreCase);
                currentRoot = root;
                popup.ShowFile(path, p, true);
                keyHook.VideoKeys = !popup.IsPhoto;
                keyHook.Armed = true;
                if (changed) { ResetSiblings(); popup.SetCounter(0, 0); FetchSiblings(); }
            }
            else if (popup.IsVisible && (now - lastValid).TotalMilliseconds > 200 &&
                     (now - lastTowardPopup).TotalMilliseconds >= TowardGraceMs)
            {
                HidePopup();
            }
        }

        // Preview shortcuts
        void OnKey(int vk)
        {
            if (!popup.IsVisible || popup.CurrentPath == null) return;
            if (vk == KeyHook.SHIFT_TAP) { if (popup.Sticky) ClosePreview(); return; }
            if (vk == KeyHook.VK_ESCAPE) { ClosePreview(); return; }
            if (vk == KeyHook.VK_SPACE) { popup.TogglePause(); return; }
            if (vk == KeyHook.VK_X) { popup.SeekBy(5); return; }
            if (vk == KeyHook.VK_Z) { popup.SeekBy(-5); return; }
            if (vk >= KeyHook.VK_0 && vk <= KeyHook.VK_5) { popup.SetRating(vk - KeyHook.VK_0); return; }
            if (vk == KeyHook.VK_V) { Step(1); return; }
            if (vk == KeyHook.VK_C) { Step(-1); return; }
        }

        string SiblingsKeyFor(string path)
        {
            return currentRoot.ToInt64().ToString(CultureInfo.InvariantCulture) + "|" + Path.GetDirectoryName(path);
        }

        // The order list belongs to one preview: it's read when the preview opens and thrown
        // away when it closes, so a changed sort/filter in Explorer is always picked up.
        bool SiblingsFresh(string path)
        {
            return siblings != null && siblingsKey == SiblingsKeyFor(path);
        }

        void ResetSiblings()
        {
            siblings = null; siblingsKey = null; pendingSteps = 0;
            siblingsSession++;               // ignore any answer still on its way
            siblingsLoading = false;
        }

        void FetchSiblings()
        {
            string cur = popup.CurrentPath;
            if (cur == null || siblingsLoading) return;
            siblingsLoading = true;
            string key = SiblingsKeyFor(cur);
            int session = siblingsSession;
            resolver.RequestSiblings(currentRoot, cur, delegate (List<string> list)
            {
                if (session != siblingsSession) return;
                siblingsLoading = false;
                siblings = list; siblingsKey = key;
                int steps = pendingSteps; pendingSteps = 0;
                if (!popup.IsVisible) return;
                if (steps != 0) ApplySteps(steps); else UpdateCounter();
            });
        }

        void Step(int dir)
        {
            string cur = popup.CurrentPath;
            if (SiblingsFresh(cur)) { ApplySteps(dir); return; }
            pendingSteps += dir;   // presses made while the list is loading are applied when it arrives
            FetchSiblings();
        }

        void ApplySteps(int steps)
        {
            string cur = popup.CurrentPath;
            if (cur == null || siblings == null || siblings.Count == 0) return;
            int idx = IndexOf(siblings, cur);
            if (idx < 0) return;
            int n = siblings.Count;
            int next = ((idx + steps) % n + n) % n;   // wraps around at the ends
            if (next == idx) return;

            if (navAnchor == null) navAnchor = lastHoveredPath;
            popup.ShowFile(siblings[next], default(Native.POINT), false);
            keyHook.VideoKeys = !popup.IsPhoto;
            popup.SetCounter(next + 1, n);
        }

        void UpdateCounter()
        {
            string cur = popup.CurrentPath;
            int idx = (cur != null && SiblingsFresh(cur)) ? IndexOf(siblings, cur) : -1;
            if (idx >= 0) popup.SetCounter(idx + 1, siblings.Count);
            else popup.SetCounter(0, 0);
        }

        static int IndexOf(List<string> list, string path)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        void CreateTray()
        {
            var menu = new WinForms.ContextMenuStrip();

            var header = new WinForms.ToolStripMenuItem("ShiftPreview v" + Version);
            header.Enabled = false;
            menu.Items.Add(header);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            var enabled = new WinForms.ToolStripMenuItem("Enabled");
            enabled.CheckOnClick = true;
            enabled.Checked = s.Enabled;
            enabled.CheckedChanged += delegate { s.Enabled = enabled.Checked; if (!s.Enabled) HidePopup(); s.Save(); };
            menu.Items.Add(enabled);

            pinnedItem = new WinForms.ToolStripMenuItem("Keep popup in a fixed spot (drag it to choose)");
            pinnedItem.CheckOnClick = true;
            pinnedItem.Checked = s.Pinned;
            pinnedItem.CheckedChanged += delegate { if (pinnedItem.Checked != s.Pinned) popup.SetPinned(pinnedItem.Checked); };
            menu.Items.Add(pinnedItem);

            var size = new WinForms.ToolStripMenuItem("Popup size");
            AddSize(size, "Small (400 x 225)", 400, 225);
            AddSize(size, "Medium (560 x 315)", 560, 315);
            AddSize(size, "Large (800 x 450)", 800, 450);
            AddSize(size, "Extra large (1120 x 630)", 1120, 630);
            size.DropDownItems.Add(new WinForms.ToolStripSeparator());
            var tip = new WinForms.ToolStripMenuItem("Tip: drag the popup's edges to resize freely");
            tip.Enabled = false;
            size.DropDownItems.Add(tip);
            menu.Items.Add(size);

            var delay = new WinForms.ToolStripMenuItem("Shift hold delay");
            AddDelay(delay, "Instant", 0);
            AddDelay(delay, "Short (0.25 s)", 250);
            AddDelay(delay, "Medium (0.5 s)", 500);
            AddDelay(delay, "Long (1 s)", 1000);
            menu.Items.Add(delay);

            var modern = new WinForms.ToolStripMenuItem("Always use the modern video player");
            modern.ToolTipText = "Off: the classic player is used, and a video switches to the modern one automatically if it can't jump.";
            modern.CheckOnClick = true;
            modern.Checked = s.ModernEngine;
            modern.CheckedChanged += delegate { s.ModernEngine = modern.Checked; s.Save(); };
            menu.Items.Add(modern);

            var startup = new WinForms.ToolStripMenuItem("Start with Windows");
            startup.CheckOnClick = true;
            startup.Checked = IsStartupEnabled();
            startup.CheckedChanged += delegate { SetStartup(startup.Checked); };
            menu.Items.Add(startup);

            menu.Items.Add(new WinForms.ToolStripSeparator());
            var openLog = new WinForms.ToolStripMenuItem("Open log file");
            openLog.Click += delegate
            {
                string lp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShiftPreview", "log.txt");
                if (!File.Exists(lp)) Log.Write("Log opened");
                try { System.Diagnostics.Process.Start("notepad.exe", "\"" + lp + "\""); } catch { }
            };
            menu.Items.Add(openLog);

            var exit = new WinForms.ToolStripMenuItem("Exit");
            exit.Click += delegate { Exit(); };
            menu.Items.Add(exit);

            tray = new WinForms.NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "ShiftPreview v" + Version;
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
        }

        void AddSize(WinForms.ToolStripMenuItem parent, string label, double w, double h)
        {
            var item = new WinForms.ToolStripMenuItem(label);
            item.Click += delegate { popup.SetSize(w, h); };
            parent.DropDownItems.Add(item);
        }

        void AddDelay(WinForms.ToolStripMenuItem parent, string label, int ms)
        {
            var item = new WinForms.ToolStripMenuItem(label);
            item.Checked = s.HoldDelayMs == ms;
            item.Click += delegate
            {
                s.HoldDelayMs = ms; s.Save();
                foreach (WinForms.ToolStripItem other in parent.DropDownItems)
                {
                    var mi = other as WinForms.ToolStripMenuItem;
                    if (mi != null) mi.Checked = mi == item;
                }
            };
            parent.DropDownItems.Add(item);
        }

        static Drawing.Icon MakeIcon()
        {
            using (var bmp = new Drawing.Bitmap(32, 32))
            {
                using (var g = Drawing.Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Drawing.Color.Transparent);
                    using (var bg = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 37, 99, 235)))
                        g.FillEllipse(bg, 1, 1, 30, 30);
                    using (var fg = new Drawing.SolidBrush(Drawing.Color.White))
                        g.FillPolygon(fg, new[] { new Drawing.Point(12, 8), new Drawing.Point(12, 24), new Drawing.Point(24, 16) });
                }
                IntPtr hIcon = bmp.GetHicon();
                var icon = (Drawing.Icon)Drawing.Icon.FromHandle(hIcon).Clone();
                Native.DestroyIcon(hIcon);
                return icon;
            }
        }

        static string ExePath { get { return System.Reflection.Assembly.GetExecutingAssembly().Location; } }

        static bool IsStartupEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(AppName) != null;
            }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(AppName, "\"" + ExePath + "\"");
                    else k.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't change the startup setting:\n" + ex.Message, "ShiftPreview");
            }
        }

        void Exit()
        {
            timer.Stop();
            HidePopup();
            s.Save();
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }
    }

    // ------------------------------------------------------------------ Entry point
    static class Program
    {
        static Mutex singleInstance;

        [STAThread]
        static void Main()
        {
            bool created;
            singleInstance = new Mutex(true, "ShiftPreview_SingleInstance_7f3a", out created);
            if (!created)
            {
                WinForms.MessageBox.Show("ShiftPreview is already running (look for the blue play icon in the system tray).", "ShiftPreview");
                return;
            }

            // Per-monitor DPI awareness so mouse, window and screen coordinates all agree on every monitor.
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);

            RunApp();
            GC.KeepAlive(singleInstance);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void RunApp()
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += delegate (object o, DispatcherUnhandledExceptionEventArgs e)
            {
                e.Handled = true; // keep running if something odd happens with a single file
            };
            var controller = new Controller();
            controller.Start();
            app.Run();
        }
    }
}
