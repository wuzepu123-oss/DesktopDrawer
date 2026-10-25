// Desktop Drawer - 屏幕分辨率自动适配：4:3 / 16:9 变化时网格不丢项。
// 用 Windows 自带的 C# 5 编译器编译，所以不能用较新的 C# 语法。
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DrawerStage2
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        // 一次 Win32 窗口定位代替每帧分别设置 WPF Window.Left / Window.Width。
        // 保持窗口的 Z 顺序与非激活状态，避免滑动时抢走键盘焦点。
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
            int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(
            IntPtr hwnd, int attribute, out int value, int valueSize);

        // 只屏蔽真正覆盖整块主屏幕的前台程序。
        // 不能按“光标所在像素是否属于桌面窗口”判断：Windows 的透明辅助窗口、
        // 任务栏等 Shell 表面可能覆盖该像素，造成普通窗口下也完全无法唤出的回归。
        private static bool IsDesktopRootClass(string name)
        {
            return name == "Progman" || name == "WorkerW";
        }

        private static bool IsForegroundFullScreenWindow(IntPtr self, int screenWidth, int screenHeight)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == self ||
                !IsWindowVisible(foreground) || IsIconic(foreground)) return false;
            uint ownPid, foregroundPid;
            GetWindowThreadProcessId(self, out ownPid);
            GetWindowThreadProcessId(foreground, out foregroundPid);
            if (ownPid == foregroundPid) return false; // 包括本程序的菜单、提示窗口

            StringBuilder cls = new StringBuilder(96);
            if (GetClassName(foreground, cls, cls.Capacity) != 0)
            {
                string name = cls.ToString();
                if (IsDesktopRootClass(name) || name == "Shell_TrayWnd" ||
                    name == "Shell_SecondaryTrayWnd") return false;
            }

            RECT rect;
            if (!GetWindowRect(foreground, out rect)) return false;
            // Windows 最大化窗口可能含 1~2px 的无形边框；容差避免误漏判。
            const int tolerancePx = 2;
            return rect.Left <= tolerancePx && rect.Top <= tolerancePx &&
                   rect.Right >= screenWidth - tolerancePx &&
                   rect.Bottom >= screenHeight - tolerancePx;
        }

        // 保留原调用入口：普通窗口无论位置如何，只要没有铺满整个屏幕，
        // 便允许从左边缘弹出。全屏游戏 / 浏览器 F11 / 最大化窗口禁止唤出。
        public static bool IsDesktopExposedAtLeftEdge(IntPtr self, int y, int screenWidth, int screenHeight)
        {
            if (self == IntPtr.Zero || screenWidth <= 0 || screenHeight <= 0 ||
                y < 0 || y >= screenHeight) return false;
            return !IsForegroundFullScreenWindow(self, screenWidth, screenHeight);
        }

        // Explorer desktop list view: read-only positions/text; never change Explorer's icon layout.
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string cls, string title);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SendMessageTimeout(
            IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr VirtualAllocEx(
            IntPtr process, IntPtr address, UIntPtr size, uint allocationType, uint protect);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool VirtualFreeEx(
            IntPtr process, IntPtr address, UIntPtr size, uint freeType);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteProcessMemory(
            IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadProcessMemory(
            IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

        // LVM_GETITEMTEXTW only uses LVITEM through cchTextMax; native pointer-size layout is vital.
        [StructLayout(LayoutKind.Sequential)]
        public struct LVITEMTEXT
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
        }

        // Recycle Bin is a Shell namespace item, not a file on disk.
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl,
            uint attributes, out uint attributesOut);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
        public static extern IntPtr SHGetFileInfoPidl(IntPtr pidl, uint attributes,
            out SHFILEINFO info, uint size, uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        // 仅查询图标；不打开、不修改文件。
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SHGetFileInfo(string path, uint attributes, out SHFILEINFO info, uint size, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr icon);
        // Windows Shell 原生额外大图标图像列表（48px / 256px），避免把 32px 图标拉大导致模糊。
        // IImageList: 前 8 个 COM 方法按 Windows SDK 的 vtable 顺序声明。
        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IImageList
        {
            [PreserveSig] int Add(IntPtr image, IntPtr mask, out int index);
            [PreserveSig] int ReplaceIcon(int index, IntPtr icon, out int newIndex);
            [PreserveSig] int SetOverlayImage(int index, int overlay);
            [PreserveSig] int Replace(int index, IntPtr image, IntPtr mask);
            [PreserveSig] int AddMasked(IntPtr image, int mask, out int index);
            [PreserveSig] int Draw(IntPtr parameters);
            [PreserveSig] int Remove(int index);
            [PreserveSig] int GetIcon(int index, int flags, out IntPtr icon);
        }

        // Ordinal 727: SHGetImageList (支持 Windows 7+；失败则回退普通 Shell 图标)。
        [DllImport("shell32.dll", EntryPoint = "#727")]
        public static extern int SHGetImageList(int listType, ref Guid iid, out IImageList imageList);

        // 打开 Windows 的实时 Acrylic 磨砂（真正透出壁纸，不是截图）
        public static void EnableAcrylic(IntPtr hwnd, byte alpha, byte red, byte green, byte blue)
        {
            uint color = ((uint)alpha << 24) | ((uint)blue << 16) | ((uint)green << 8) | red;
            AccentPolicy policy = new AccentPolicy();
            policy.AccentState = 4;      // ACCENT_ENABLE_ACRYLICBLURBEHIND
            policy.AccentFlags = 2;
            policy.GradientColor = unchecked((int)color);
            policy.AnimationId = 0;
            int size = Marshal.SizeOf(policy);
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, pointer, false);
                WindowCompositionAttributeData data = new WindowCompositionAttributeData();
                data.Attribute = 19;     // WCA_ACCENT_POLICY
                data.Data = pointer;
                data.SizeOfData = size;
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        public static bool IsTransparencyEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key == null || Convert.ToInt32(key.GetValue("EnableTransparency", 1)) != 0;
            }
            catch { return true; }
        }

        // 兼容早期调用名称，两者统一使用同一套“是否铺满主屏幕”的判断。
        public static bool IsForegroundFullscreen(IntPtr self, int screenWidth, int screenHeight)
        {
            return IsForegroundFullScreenWindow(self, screenWidth, screenHeight);
        }
    }

    // Control only the Explorer desktop's icon list, never the underlying files.
    // The Explorer shell command is preferred because it uses the same setting
    // as Desktop -> View -> Show desktop icons. A guarded fallback handles
    // Windows builds on which Explorer ignores this undocumented command.
    internal static class DesktopIconVisibility
    {
        private const uint WmCommand = 0x0111;
        private const int ToggleDesktopIconsCommand = 0x7402;
        private const int GwlStyle = -16;
        private const int WsVisible = 0x10000000;
        private const int SwHide = 0;
        private const int SwShow = 5;
        private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        private static bool FindShellView(out IntPtr shellView, out IntPtr listView)
        {
            shellView = IntPtr.Zero;
            listView = IntPtr.Zero;
            IntPtr progman = Native.FindWindow("Progman", "Program Manager");
            if (progman != IntPtr.Zero)
                shellView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView == IntPtr.Zero)
            {
                // In newer Windows versions the desktop view lives under WorkerW.
                IntPtr found = IntPtr.Zero;
                Native.EnumWindows(delegate(IntPtr top, IntPtr unused)
                {
                    found = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                    return found == IntPtr.Zero;
                }, IntPtr.Zero);
                shellView = found;
            }
            if (shellView == IntPtr.Zero) return false;
            listView = Native.FindWindowEx(shellView, IntPtr.Zero, "SysListView32", "FolderView");
            if (listView == IntPtr.Zero)
                listView = Native.FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
            return listView != IntPtr.Zero;
        }

        // Query the actual icon list style, not an app-specific saved preference.
        // If the user toggles icons on the desktop, our menu reflects that too.
        public static bool TryGetVisible(out bool visible)
        {
            visible = true;
            IntPtr shellView, listView;
            if (!FindShellView(out shellView, out listView)) return false;
            visible = (Native.GetWindowLong(listView, GwlStyle) & WsVisible) != 0;
            return true;
        }

        public static bool TrySetVisible(bool desiredVisible, out string error)
        {
            error = null;
            IntPtr shellView, listView;
            if (!FindShellView(out shellView, out listView))
            {
                error = "没有找到 Windows 桌面图标窗口。请确认 Explorer 正在运行。";
                return false;
            }
            bool before = (Native.GetWindowLong(listView, GwlStyle) & WsVisible) != 0;
            if (before == desiredVisible) return true;

            // This command is used by Explorer's own "Show desktop icons" toggle.
            // SendMessageTimeout prevents UI freezes if Explorer is unresponsive.
            IntPtr result;
            Native.SendMessageTimeout(shellView, WmCommand,
                new IntPtr(ToggleDesktopIconsCommand), IntPtr.Zero,
                0x0002 /* SMTO_ABORTIFHUNG */, 500, out result);
            if (((Native.GetWindowLong(listView, GwlStyle) & WsVisible) != 0) == desiredVisible)
                return true;

            // Fallback: only show/hide the icon layer; wallpaper, desktop and
            // this drawer stay usable. Write Explorer's preference so that a
            // later shell restart also has the requested state when supported.
            Native.ShowWindow(listView, desiredVisible ? SwShow : SwHide);
            if (((Native.GetWindowLong(listView, GwlStyle) & WsVisible) != 0) != desiredVisible)
            {
                error = "Windows 没有接受图标显示状态的更改。";
                return false;
            }
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ExplorerAdvanced))
                {
                    if (key != null)
                        key.SetValue("HideIcons", desiredVisible ? 0 : 1, RegistryValueKind.DWord);
                    else error = "图标已切换，但无法保存到系统设置；重启 Explorer 后可能恢复。";
                }
            }
            catch (Exception)
            {
                error = "图标已切换，但无法保存到系统设置；重启 Explorer 后可能恢复。";
            }
            return true;
        }
    }

    // 当前用户自启动：只写 HKCU Run，不需要管理员权限，不创建服务、计划任务或安装器。
    // 另外保存 on/off 偏好，避免用户手动关闭后下一次启动又被自动注册。
    internal static class StartupRegistration
    {
        private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "DesktopDrawerStage2";
        private static readonly string PreferencePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DrawerStage2", "autostart.pref");

        private static string Command
        {
            get { return "\"" + Process.GetCurrentProcess().MainModule.FileName + "\""; }
        }

        public static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunPath, false))
                return key != null && String.Equals(
                    key.GetValue(RunName, "") as string, Command, StringComparison.OrdinalIgnoreCase);
        }

        private static void UpdateRegistry(bool enable)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunPath))
            {
                if (key == null) throw new InvalidOperationException("无法打开当前用户开机启动项。");
                if (enable) key.SetValue(RunName, Command, RegistryValueKind.String);
                else key.DeleteValue(RunName, false);
            }
        }

        public static void SetEnabled(bool enable)
        {
            UpdateRegistry(enable);
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath));
            File.WriteAllText(PreferencePath, enable ? "on" : "off", Encoding.ASCII);
        }

        public static void EnsureDefaultOn()
        {
            // 首次运行默认开启；用户关闭之后，后续运行只维持关闭状态。
            bool enabled = true;
            if (File.Exists(PreferencePath))
                enabled = !String.Equals(File.ReadAllText(PreferencePath, Encoding.ASCII).Trim(),
                    "off", StringComparison.OrdinalIgnoreCase);
            UpdateRegistry(enabled); // 换了工程文件夹后也能更新到当前 Drawer.exe 的路径。
            if (!File.Exists(PreferencePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath));
                File.WriteAllText(PreferencePath, "on", Encoding.ASCII);
            }
        }
    }

    // Desktop Explorer's SysListView32 owns true desktop icon coordinates.
    // We only read its visible display names and grid positions from a temporary
    // remote buffer, and abandon coordinate preservation if any matching is uncertain.
    internal static class DesktopLayoutReader
    {
        private const uint LVM_GETITEMCOUNT = 0x1004;
        private const uint LVM_GETITEMPOSITION = 0x1010;
        private const uint LVM_GETITEMSPACING = 0x1033;
        private const uint LVM_GETITEMTEXTW = 0x1073;
        private const int MaxItemsToScan = 500;

        private sealed class DisplayPosition
        {
            public string Name;
            public int X;
            public int Y;
        }

        private static IntPtr FindDesktopListView()
        {
            IntPtr desktopView = IntPtr.Zero;
            IntPtr progman = Native.FindWindow("Progman", "Program Manager");
            if (progman != IntPtr.Zero)
                desktopView = Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (desktopView == IntPtr.Zero)
                Native.EnumWindows(delegate(IntPtr top, IntPtr unused)
                {
                    desktopView = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                    return desktopView == IntPtr.Zero;
                }, IntPtr.Zero);
            return desktopView == IntPtr.Zero ? IntPtr.Zero :
                Native.FindWindowEx(desktopView, IntPtr.Zero, "SysListView32", "FolderView");
        }

        private static bool Message(IntPtr window, uint code, IntPtr wp, IntPtr lp, out IntPtr value)
        {
            return Native.SendMessageTimeout(window, code, wp, lp, 0x0002, 100, out value) != IntPtr.Zero;
        }

        private static byte[] BytesFromStruct(object value)
        {
            int size = Marshal.SizeOf(value);
            IntPtr memory = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(value, memory, false);
                byte[] result = new byte[size];
                Marshal.Copy(memory, result, 0, size);
                return result;
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private static bool ReadRemote(IntPtr process, IntPtr remote, byte[] buffer)
        {
            IntPtr read;
            return Native.ReadProcessMemory(process, remote, buffer, buffer.Length, out read)
                && read.ToInt64() == buffer.Length;
        }

        private static string ShellDisplayName(string path)
        {
            if (DrawerWindow.IsRecycleBin(path)) return DrawerWindow.RecycleDisplayName();
            Native.SHFILEINFO info = new Native.SHFILEINFO();
            try
            {
                IntPtr ok = Native.SHGetFileInfo(path, 0, out info,
                    (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), 0x00000200);
                return ok == IntPtr.Zero ? "" : (info.szDisplayName ?? "");
            }
            catch { return ""; }
        }

        private static bool IsOnDesktop(string path)
        {
            if (DrawerWindow.IsRecycleBin(path)) return true;
            try
            {
                string parent = Path.GetDirectoryName(Path.GetFullPath(path));
                string personal = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                return String.Equals(parent, personal, StringComparison.OrdinalIgnoreCase)
                    || String.Equals(parent, common, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool TryFindPath(Dictionary<string, DisplayPosition> labels,
            HashSet<string> ambiguous, string path, out DisplayPosition position)
        {
            List<string> possible = new List<string>();
            possible.Add(ShellDisplayName(path));
            if (!DrawerWindow.IsRecycleBin(path))
            {
                possible.Add(Path.GetFileNameWithoutExtension(path));
                possible.Add(Path.GetFileName(path));
            }
            foreach (string name in possible)
            {
                if (!String.IsNullOrEmpty(name) && !ambiguous.Contains(name)
                    && labels.TryGetValue(name, out position)) return true;
            }
            position = null;
            return false;
        }

        // Returns a complete map or fails; never pretend the FileDrop list is spatially ordered.
        public static bool TryRead(string[] paths, out Dictionary<string, Point> locations,
            out int spacingX, out int spacingY)
        {
            locations = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
            spacingX = 76;
            spacingY = 78;
            if (paths.Length < 2) return false;
            foreach (string path in paths) if (!IsOnDesktop(path)) return false;
            IntPtr desktop = FindDesktopListView();
            if (desktop == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(desktop, out pid);
            if (pid == 0) return false;
            IntPtr process = Native.OpenProcess(0x0038, false, pid); // VM_READ|VM_WRITE|VM_OPERATION
            if (process == IntPtr.Zero) return false;
            IntPtr memory = IntPtr.Zero;
            try
            {
                memory = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
                if (memory == IntPtr.Zero) return false;
                IntPtr result;
                if (!Message(desktop, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, out result)) return false;
                int count = (int)result.ToInt64();
                if (count < 1 || count > MaxItemsToScan) return false;
                if (Message(desktop, LVM_GETITEMSPACING, IntPtr.Zero, IntPtr.Zero, out result))
                {
                    long packed = result.ToInt64();
                    int sx = (int)(packed & 0xffff);
                    int sy = (int)((packed >> 16) & 0xffff);
                    if (sx >= 25 && sx <= 500 && sy >= 25 && sy <= 500)
                    { spacingX = sx; spacingY = sy; }
                }
                Dictionary<string, DisplayPosition> labels = new Dictionary<string, DisplayPosition>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    Native.LVITEMTEXT lv = new Native.LVITEMTEXT();
                    lv.iItem = i;
                    lv.iSubItem = 0;
                    lv.pszText = new IntPtr(memory.ToInt64() + 256);
                    lv.cchTextMax = 512;
                    byte[] structure = BytesFromStruct(lv);
                    IntPtr written;
                    if (!Native.WriteProcessMemory(process, memory, structure, structure.Length, out written)
                        || written.ToInt64() != structure.Length) return false;
                    if (!Message(desktop, LVM_GETITEMTEXTW, new IntPtr(i), memory, out result)) return false;
                    byte[] titleBuffer = new byte[1024];
                    if (!ReadRemote(process, lv.pszText, titleBuffer)) return false;
                    string name = Encoding.Unicode.GetString(titleBuffer).Split('\0')[0];
                    if (String.IsNullOrEmpty(name)) continue;
                    if (!Message(desktop, LVM_GETITEMPOSITION, new IntPtr(i), memory, out result)
                        || result == IntPtr.Zero) return false;
                    byte[] coords = new byte[8];
                    if (!ReadRemote(process, memory, coords)) return false;
                    DisplayPosition position = new DisplayPosition();
                    position.Name = name;
                    position.X = BitConverter.ToInt32(coords, 0);
                    position.Y = BitConverter.ToInt32(coords, 4);
                    if (labels.ContainsKey(name)) ambiguous.Add(name);
                    else labels[name] = position;
                }
                foreach (string path in paths)
                {
                    DisplayPosition position;
                    if (!TryFindPath(labels, ambiguous, path, out position)) return false;
                    locations[path] = new Point(position.X, position.Y);
                }
                return locations.Count == paths.Length;
            }
            catch (Exception) { return false; }
            finally
            {
                if (memory != IntPtr.Zero) Native.VirtualFreeEx(process, memory, UIntPtr.Zero, 0x8000);
                Native.CloseHandle(process);
            }
        }
    }

    // 每个快捷方式独立记录网格坐标。空格子不对应条目，因此可以长期保留空位。

    // Real Explorer shell menu for an existing file/folder.  Uses the Shell-provided
    // commands (including registered archive extensions), not a hand-coded list.
    // COM objects and menu HWND hooks live only for the duration of the popup.
    internal static class ShellFileMenu
    {
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint MF_SEPARATOR = 0x0800;
        private const uint MF_STRING = 0x0000;
        private const int RemoveId = 0x7000;
        private const int RemoveSelectedId = 0x7001;
        private const int RenameId = 0x7002;
        private const int CMF_EXPLORE = 0x00000004;
        private const int CMF_CANRENAME = 0x00000010;
        private const int WM_INITMENUPOPUP = 0x0117;
        private const int WM_DRAWITEM = 0x002B;
        private const int WM_MEASUREITEM = 0x002C;
        private const int WM_MENUCHAR = 0x0120;

        [DllImport("shell32.dll", PreserveSig = true)]
        private static extern int SHBindToParent(IntPtr pidl, ref Guid iid,
            [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder,
            out IntPtr childPidl);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string caption);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y,
            IntPtr owner, IntPtr reserved);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bindCtx,
                [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten,
                out IntPtr pidl, ref uint attributes);
            [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumeration);
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bindCtx, ref Guid iid, out IntPtr obj);
            [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bindCtx, ref Guid iid, out IntPtr obj);
            [PreserveSig] int CompareIDs(IntPtr param, IntPtr a, IntPtr b);
            [PreserveSig] int CreateViewObject(IntPtr hwnd, ref Guid iid, out IntPtr obj);
            [PreserveSig] int GetAttributesOf(uint count, IntPtr pidls, ref uint attrs);
            [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] pidls,
                ref Guid iid, IntPtr reserved,
                [MarshalAs(UnmanagedType.Interface)] out IShellContextMenu result);
            [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
            [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl,
                [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr newPidl);
        }

        [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr menu, uint index,
                uint commandFirst, uint commandLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CommandInfo command);
            [PreserveSig] int GetCommandString(UIntPtr command, uint flags,
                IntPtr reserved, IntPtr name, uint size);
        }

        [ComImport, Guid("000214F4-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellContextMenu2 : IShellContextMenu
        {
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr w, IntPtr l);
        }
        [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellContextMenu3 : IShellContextMenu2
        {
            [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr w, IntPtr l, out IntPtr result);
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct CommandInfo
        {
            public uint size;
            public uint mask;
            public IntPtr hwnd;
            public IntPtr verb;
            public IntPtr parameters;
            public IntPtr directory;
            public int show;
            public uint hotkey;
            public IntPtr icon;
        }

        public static bool Show(IntPtr hwnd, string path, int selectedCount,
            Action removeOne, Action removeSelected, Action rename,
            Action beforeNativeOperation)
        {
            if (hwnd == IntPtr.Zero || (!File.Exists(path) && !Directory.Exists(path))) return false;
            IntPtr pidl = IntPtr.Zero;
            IntPtr menu = IntPtr.Zero;
            IShellFolder folder = null;
            IShellContextMenu context = null;
            HwndSource source = null;
            HwndSourceHook hook = null;
            try
            {
                uint attrs;
                if (Native.SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs) != 0
                    || pidl == IntPtr.Zero) return false;
                Guid folderId = new Guid("000214E6-0000-0000-C000-000000000046");
                IntPtr child;
                if (SHBindToParent(pidl, ref folderId, out folder, out child) < 0 || folder == null)
                    return false;
                Guid contextId = new Guid("000214E4-0000-0000-C000-000000000046");
                if (folder.GetUIObjectOf(hwnd, 1, new IntPtr[] { child }, ref contextId,
                    IntPtr.Zero, out context) < 0 || context == null) return false;
                menu = CreatePopupMenu();
                if (menu == IntPtr.Zero) return false;
                int hr = context.QueryContextMenu(menu, 0, 1, 0x5FFF,
                    CMF_EXPLORE | CMF_CANRENAME);
                if (hr < 0) return false;
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                AppendMenu(menu, MF_STRING, new UIntPtr(RenameId), "重命名实际文件...");
                if (selectedCount > 1)
                    AppendMenu(menu, MF_STRING, new UIntPtr(RemoveSelectedId),
                        "仅从抽屉移除选中的 " + selectedCount + " 项");
                AppendMenu(menu, MF_STRING, new UIntPtr(RemoveId), "仅从抽屉移除（不删除文件）");

                IShellContextMenu2 menu2 = context as IShellContextMenu2;
                IShellContextMenu3 menu3 = context as IShellContextMenu3;
                source = HwndSource.FromHwnd(hwnd);
                if (source != null && menu2 != null)
                {
                    hook = delegate(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
                    {
                        if (msg == WM_INITMENUPOPUP || msg == WM_DRAWITEM ||
                            msg == WM_MEASUREITEM || msg == WM_MENUCHAR)
                        {
                            try
                            {
                                // C# 5 的 definite-assignment 检查：
                                // 某些分支不会通过 out 参数给 result 赋值。
                                IntPtr result = IntPtr.Zero;
                                int resultCode;
                                if (menu3 != null)
                                    resultCode = menu3.HandleMenuMsg2((uint)msg, w, l, out result);
                                else
                                    resultCode = menu2.HandleMenuMsg((uint)msg, w, l);
                                if (resultCode >= 0) { handled = true; return result; }
                            }
                            catch (COMException) { }
                        }
                        return IntPtr.Zero;
                    };
                    source.AddHook(hook);
                }
                Native.POINT position;
                if (!Native.GetCursorPos(out position)) return false;
                SetForegroundWindow(hwnd);
                int command = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                    position.X, position.Y, hwnd, IntPtr.Zero);
                // Necessary on older Windows builds for proper dismissal of native menus.
                PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
                if (command == RemoveId) { removeOne(); return true; }
                if (command == RemoveSelectedId) { removeSelected(); return true; }
                if (command == RenameId) { rename(); return true; }
                if (command >= 1 && command <= 0x5FFF)
                {
                    // Filesystem operations invoked via the native Shell menu may take
                    // place asynchronously (including external archive extractors).
                    beforeNativeOperation();
                    CommandInfo info = new CommandInfo();
                    info.size = (uint)Marshal.SizeOf(typeof(CommandInfo));
                    info.hwnd = hwnd;
                    info.verb = new IntPtr(command - 1); // Shell relative command ordinal
                    info.show = 1;
                    int invokeHr = context.InvokeCommand(ref info);
                    if (invokeHr < 0) Marshal.ThrowExceptionForHR(invokeHr);
                }
                return true;
            }
            catch (Exception ex)
            {
                // Native menus are supplied by Explorer and installed shell extensions.
                // A failed provider must never cause an unhandled exception in the drawer.
                System.Diagnostics.Debug.WriteLine("Shell menu error: " + ex);
                return false;
            }
            finally
            {
                if (source != null && hook != null) source.RemoveHook(hook);
                if (menu != IntPtr.Zero) DestroyMenu(menu);
                if (context != null) Marshal.ReleaseComObject(context);
                if (folder != null) Marshal.ReleaseComObject(folder);
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    internal sealed class ShortcutEntry
    {
        public string Path;
        public int Column;
        public int Row;

        public ShortcutEntry(string path, int column, int row)
        {
            Path = path;
            Column = column;
            Row = row;
        }
    }

    internal sealed class DrawerWindow : Window
    {
        // ====== 可以调的参数 ======
        private const double RadiusDip = 28;     // 右侧圆角大小
        private const double CellW = 76;         // 与用户桌面截图接近：图标列中心间距约 76px
        private const double CellH = 78;         // 100% 行间距。实际行距 = 78 * 百分比 / 100，始终对齐隐形网格。
        private const int MinRowSpacingPercent = 70;
        private const int MaxRowSpacingPercent = 160;
        private const int MinColumnSpacingPercent = 70;
        private const int MaxColumnSpacingPercent = 160;
        private const int MinIconSizePercent = 70;
        private const int MaxIconSizePercent = 140;
        private const double Pad = 6;           // 缩小边距，不额外增加可见的图标方框
        private const double IconDip = 32;      // Windows 桌面中等图标约 32 DIP，不再放大到 44
        private const int MinColumns = 2;        // 最少两列
        private const int TriggerPx = 2;         // 鼠标贴着屏幕最左边多少像素内算“碰到”
        private const int HideDelayMs = 350;     // 鼠标离开后多久收回
        private const byte TintAlpha = 0x66;     // 磨砂的染色浓度：越大越暗、越看不清壁纸（0x00-0xFF）
        private const byte TintR = 16, TintG = 20, TintB = 28;   // 磨砂的染色颜色
        // ==========================

        private readonly DispatcherTimer timer = new DispatcherTimer();
        private readonly Canvas tiles = new Canvas();
        private readonly ScrollViewer tileViewport = new ScrollViewer();
        // 间距存到独立文件，旧的 layout-v2.txt 始终保存原有网格坐标/路径格式。
        private readonly string spacingSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DrawerStage2", "row-spacing-v1.txt");
        private int rowSpacingPercent = 100;
        private int columnSpacingPercent = 100;
        private int iconSizePercent = 100;
        private readonly string viewSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DrawerStage2", "view-settings-v2.txt");
        private bool liveSettingsOpen;
        // 预览时记住已经绘制的尺寸，避免横向滑块反复触发所有图标的垂直测量。
        private double previewCellWidth = Double.NaN;
        private double previewCellHeight = Double.NaN;
        private double previewIconSize = Double.NaN;
        private double previewLabelSize = Double.NaN;
        private double previewLabelHeight = Double.NaN;
        private readonly TextBlock hint = new TextBlock();
        // Dynamic display size in WPF DIPs. Values are refreshed at runtime after mode/DPI changes.
        private double displayWidthDip = SystemParameters.PrimaryScreenWidth;
        private double displayHeightDip = SystemParameters.PrimaryScreenHeight;
        private int displayWidthPx;
        private int displayHeightPx;
        // Inspect DPI less frequently while resolution is unchanged to keep animation ticks light.
        private DateTime nextDpiProbe = DateTime.MinValue;
        // 选中状态只影响界面；保存格式仍沿用 layout-v2.txt。
        private readonly HashSet<string> selectedShortcuts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Border> shortcutCells = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Windows.Shapes.Rectangle selectionRectangle = new System.Windows.Shapes.Rectangle();
        private readonly HashSet<string> selectionBeforeMarquee = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool marqueePending;
        private bool marqueeActive;
        private bool marqueeAdditive;
        private Point marqueeStart;
        private readonly List<ShortcutEntry> shortcuts = new List<ShortcutEntry>();
        // Only watch the desktop root; never automatically import nested contents.
        private readonly List<FileSystemWatcher> desktopWatchers = new List<FileSystemWatcher>();
        private readonly HashSet<string> newDesktopPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly DispatcherTimer newDesktopTimer = new DispatcherTimer();
        private DateTime importDesktopChangesUntil = DateTime.MinValue;
        private string importNearPath;
        private bool nativeMenuInUse;

        private readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DrawerStage2", "layout-v2.txt");
        // 旧版记录的是连续排列的快捷方式。只读取旧文件进行首次迁移，不覆盖旧数据。
        private readonly string legacySettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DrawerStage2", "shortcuts.txt");
        public const string RecycleBinKey = "shell:RecycleBinFolder";
        // 内置设置项与真实文件使用同一套 (Column, Row) 网格及布局持久化。
        // 不创建桌面快捷方式，不占用额外的固定控件区域。
        private const string DrawerSettingsKey = "drawer:settings";
        private static bool IsDrawerSettings(string path)
        {
            return String.Equals(path, DrawerSettingsKey, StringComparison.OrdinalIgnoreCase);
        }

        private void EnsureDrawerSettingsEntry()
        {
            if (ContainsShortcut(DrawerSettingsKey)) return;
            int c = 0, r = 0;
            // 已有用户布局绝不能覆盖：升级后放在最近的空格中。
            while (!TryFindFreeCell(0, 0, out c, out r) && columns < MaxColumns)
                columns++;
            if (c < 0) { ShowStatus("抽屉已满，可通过右键打开设置并调整列数。"); return; }
            shortcuts.Add(new ShortcutEntry(DrawerSettingsKey, c, r));
            SaveSettings();
        }

        public static bool IsRecycleBin(string path)
        {
            return String.Equals(path, RecycleBinKey, StringComparison.OrdinalIgnoreCase);
        }
        public static string RecycleDisplayName()
        {
            Native.SHFILEINFO info = new Native.SHFILEINFO();
            IntPtr pidl = IntPtr.Zero;
            uint attributes;
            try
            {
                if (Native.SHParseDisplayName("::{645FF040-5081-101B-9F08-00AA002F954E}",
                    IntPtr.Zero, out pidl, 0, out attributes) == 0 && pidl != IntPtr.Zero)
                {
                    IntPtr ok = Native.SHGetFileInfoPidl(pidl, 0, out info,
                        (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), 0x00000008 | 0x00000200);
                    if (ok != IntPtr.Zero && !String.IsNullOrEmpty(info.szDisplayName)) return info.szDisplayName;
                }
            }
            catch { }
            finally { if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl); }
            return "回收站";
        }

        private const string SettingsHeader = "DesktopDrawer.Stage2/Grid2";
        private const string LegacySettingsHeader = "DesktopDrawer.Stage2/1";
        private const string InternalDragFormat = "DesktopDrawer.Stage2.InternalShortcut";
        private readonly Border dropTarget = new Border();
        private string pressedShortcut;
        private string internalDraggingPath;
        private readonly List<string> internalDraggingPaths = new List<string>();
        private Point pressedPoint;
        private bool suppressLaunch;
        private readonly bool transparencyOn;
        private IntPtr hwnd = IntPtr.Zero;
        private double scale = 1.0;
        // 记录上一次真实像素尺寸，避免重复重建非抗锯齿 GDI 裁剪区。
        private int lastRegionWidth = -1;
        private int lastRegionHeight = -1;
        private int columns = MinColumns;
        private bool expanded;
        private bool menuOpen;
        private DateTime lastInside = DateTime.MinValue;
        private DateTime holdUntil = DateTime.MinValue;
        private double targetLeft;
        private double targetWidth;
        private bool rendering;
        // 临界阻尼弹簧（无回弹）：运动从零速度起步，先加速再逐渐减速，
        // 中途改变方向时沿用当前速度，避免重新播放缓动导致跳变或突然冲刺。
        // 数值越大响应越快；建议在 18~26 范围内微调。单位为 1/秒。
        private const double SlideResponse = 18.5;
        private const double ResizeResponse = 17.0;
        private const double SettleDistancePx = 0.45;
        private const double SettleSpeedPxPerSecond = 5.0;
        private long animationLastTicks;
        private double animationLeftPx;
        private double animationWidthPx;
        private double animationLeftVelocityPx;
        private double animationWidthVelocityPx;
        private int animationToLeftPx;
        private int animationToWidthPx;
        private int animationYpx;
        private int animationHeightPx;
        private int lastAppliedLeftPx;
        private int lastAppliedWidthPx;
        private bool errorLogged;
        private DateTime statusUntil = DateTime.MinValue;

        public DrawerWindow()
        {
            transparencyOn = Native.IsTransparencyEnabled();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;

            Top = 0;
            Height = displayHeightDip;
            Width = TargetWidth;
            Left = CollapsedLeft;
            targetLeft = Left;
            targetWidth = Width;

            Content = BuildContent();
            // 在顶层窗口监听 OLE 拖入（包括 Explorer 同时选中的多个文件）。
            // handledEventsToo 防止 Canvas/子项的事件处理干扰桌面外部拖入。
            AllowDrop = true;
            AddHandler(DragDrop.PreviewDragEnterEvent, new DragEventHandler(OnExternalDragOver), true);
            AddHandler(DragDrop.PreviewDragOverEvent, new DragEventHandler(OnExternalDragOver), true);
            AddHandler(DragDrop.PreviewDropEvent, new DragEventHandler(OnExternalDrop), true);
            LoadViewSettings();
            LoadSettings();
            EnsureDrawerSettingsEntry();
            Width = TargetWidth;
            Left = CollapsedLeft;
            targetLeft = Left;
            targetWidth = Width;
            BuildTiles();

            SourceInitialized += OnSourceInitialized;
            SizeChanged += delegate(object sender, SizeChangedEventArgs e) { UpdateRegion(e.NewSize); };
            Loaded += delegate
            {
                // 登录后静默启动、保持隐藏。只有鼠标触碰可见桌面的左边缘才弹出。
                // 绝不因为开机启动而覆盖已打开的游戏或全屏程序。
                StartDesktopMonitoring();
                try { StartupRegistration.EnsureDefaultOn(); }
                catch (Exception ex) { ShowStatus("设置开机自启动失败：" + ex.Message); }
            };
            Closed += delegate { timer.Stop(); StopRendering(); StopDesktopMonitoring(); };

            // 轮询只负责判定触发，动画本身由屏幕同步的 Rendering 事件执行。
            newDesktopTimer.Interval = TimeSpan.FromMilliseconds(950);
            newDesktopTimer.Tick += OnNewDesktopTimer;
            timer.Interval = TimeSpan.FromMilliseconds(25);
            timer.Tick += OnTick;
            timer.Start();
        }

        // 窗口左边多出一个圆角半径藏在屏幕外，这样左侧贴边是直的，只有右侧是圆角
        private double ExpandedLeft { get { return -RadiusDip; } }
        private double CollapsedLeft { get { return -TargetWidth - 12; } }

        // No scrolling: compact only the *rendered* grid for a temporary low-resolution
        // mode. In settings mode, preview reflows overflow into further columns instead.
        private double ViewportHeight { get { return Math.Max(1, displayHeightDip - 2 * Pad); } }
        private double WantedCellH { get { return CellH * rowSpacingPercent / 100.0; } }
        private double DisplayCellW { get { return CellW * columnSpacingPercent / 100.0; } }
        private int NaturalRowsAt(int percent)
        {
            return Math.Max(1, (int)Math.Floor((ViewportHeight - 2) / (CellH * percent / 100.0)));
        }
        private double DisplayCellH
        {
            get { return Math.Max(1, Math.Min(WantedCellH, (ViewportHeight - 2) / Math.Max(1, Rows))); }
        }
        private double ViewportWidth
        {
            get
            {
                double wanted = columns * DisplayCellW;
                double available = Math.Max(1, displayWidthDip - RadiusDip - 2 * Pad);
                return Math.Min(wanted, available);
            }
        }

        private double ContentWidth { get { return Pad * 2 + ViewportWidth; } }
        private double TargetWidth { get { return RadiusDip + ContentWidth; } }
        // 设置模式把磨砂窗口扩展到整个主屏幕，再固定 HWND 宽度做图标实时预览。
        // Window.Left = -RadiusDip，因此额外加上左侧屏幕外的圆角半径。
        // 这里的尺寸不依赖滑块/列数；打开/退出时才用原有自然缓动改变 HWND 宽度。
        private double ActiveExpandedWidth
        {
            get { return liveSettingsOpen ? displayWidthDip + RadiusDip : TargetWidth; }
        }

        // Don't discard columns from the saved layout when entering a narrower display mode.
        private int MaxColumns
        {
            get
            {
                int natural = Math.Max(MinColumns,
                    (int)Math.Floor((displayWidthDip - RadiusDip - 2 * Pad) / DisplayCellW));
                int occupied = MinColumns;
                foreach (ShortcutEntry entry in shortcuts)
                    if (entry.Column >= 0 && entry.Column < 64)
                        occupied = Math.Max(occupied, entry.Column + 1);
                return Math.Min(64, Math.Max(natural, Math.Max(columns, occupied)));
            }
        }

        private double DisplayIconSize
        {
            get
            {
                double requested = IconDip * iconSizePercent / 100.0;
                return Math.Max(12, Math.Min(requested, Math.Min(DisplayCellW - 8, DisplayCellH - 22)));
            }
        }
        private double DisplayLabelSize { get { return DisplayCellH < 62 ? 9.5 : 11; } }
        private double DisplayLabelHeight
        {
            get { return Math.Max(14, Math.Min(34, DisplayCellH - DisplayIconSize - 9)); }
        }

        private UIElement BuildContent()
        {
            Brush glassBrush;
            if (transparencyOn)
                glassBrush = new LinearGradientBrush(Color.FromArgb(0x22, 255, 255, 255), Color.FromArgb(0x08, 255, 255, 255), 90);
            else
                glassBrush = new SolidColorBrush(Color.FromArgb(0xD8, TintR, TintG, TintB));

            Border glass = new Border();
            // 唯一可见的边缘交给 WPF 的抗锯齿圆角；不再叠加半透明描边。
            // 旧版 1px 白线和 Win32 硬裁剪半径不完全重合，容易在拐角形成断线。
            glass.CornerRadius = new CornerRadius(RadiusDip);
            glass.BorderThickness = new Thickness(0);
            glass.SnapsToDevicePixels = true;
            glass.UseLayoutRounding = true;
            glass.Background = glassBrush;

            Grid inner = new Grid();
            inner.Margin = new Thickness(RadiusDip + Pad, Pad, Pad, Pad);

            tiles.HorizontalAlignment = HorizontalAlignment.Left;
            tiles.VerticalAlignment = VerticalAlignment.Top;
            // Canvas 需要透明的命中测试背景；否则无法从空白格开始拉框。
            tiles.Background = Brushes.Transparent;
            tiles.AllowDrop = true; // 与玻璃外壳一样，可接收 Explorer 的多文件拖入。
            tiles.Focusable = true;
            tiles.PreviewMouseLeftButtonDown += OnTilesPreviewMouseLeftButtonDown;
            tiles.PreviewMouseMove += OnTilesPreviewMouseMove;
            tiles.PreviewMouseLeftButtonUp += OnTilesPreviewMouseLeftButtonUp;
            tiles.LostMouseCapture += OnTilesLostMouseCapture;
            // Never show a scrollbar; compress the render horizontally only when
            // the current screen cannot show all existing columns at their full width.
            tileViewport.HorizontalAlignment = HorizontalAlignment.Left;
            tileViewport.VerticalAlignment = VerticalAlignment.Top;
            tileViewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            tileViewport.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            tileViewport.Background = Brushes.Transparent;
            tileViewport.Focusable = false;
            tileViewport.Content = tiles;
            inner.Children.Add(tileViewport);
            PreviewKeyDown += OnDrawerKeyDown;

            // 不再用提示文字占据图标网格：需要时才短暂显示状态消息。
            hint.Text = "";
            hint.FontFamily = new FontFamily("Microsoft YaHei UI");
            hint.FontSize = 11;
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 255, 255, 255));
            hint.VerticalAlignment = VerticalAlignment.Bottom;
            hint.IsHitTestVisible = false;
            hint.Visibility = Visibility.Collapsed;
            inner.Children.Add(hint);

            glass.Child = inner;
            glass.AllowDrop = true;
            glass.PreviewDragLeave += delegate
            {
                HideDropTarget();
                holdUntil = DateTime.UtcNow.AddMilliseconds(900);
            };
            glass.ContextMenu = BuildMenu();
            return glass;
        }

        private ContextMenu BuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            MenuItem removeSelectedItem = new MenuItem();
            MenuItem autoStart = new MenuItem();
            MenuItem desktopIcons = new MenuItem();
            MenuItem preferences = new MenuItem();
            preferences.Header = "抽屉设置...";
            preferences.Click += delegate { ShowLiveSettingsDialog(); };
            Separator selectionSeparator = new Separator();
            removeSelectedItem.Click += delegate { RemoveSelectedShortcuts(); };
            menu.Opened += delegate
            {
                menuOpen = true;
                bool iconsVisible;
                if (DesktopIconVisibility.TryGetVisible(out iconsVisible))
                {
                    desktopIcons.Header = iconsVisible ? "隐藏桌面图标" : "显示桌面图标";
                    desktopIcons.IsEnabled = true;
                }
                else
                {
                    desktopIcons.Header = "显示 / 隐藏桌面图标（Explorer 不可用）";
                    desktopIcons.IsEnabled = false;
                }
                try { autoStart.IsChecked = StartupRegistration.IsEnabled(); }
                catch { autoStart.IsChecked = false; }
                removeSelectedItem.Header = "从抽屉移除选中的 " + selectedShortcuts.Count + " 项（不删除文件）";
                removeSelectedItem.Visibility = selectedShortcuts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                selectionSeparator.Visibility = removeSelectedItem.Visibility;
            };
            menu.Closed += delegate { menuOpen = false; lastInside = DateTime.UtcNow; };

            if (!transparencyOn)
            {
                MenuItem warning = new MenuItem();
                warning.Header = "Windows“透明效果”已关闭，磨砂不可用";
                warning.IsEnabled = false;
                menu.Items.Add(warning);
                menu.Items.Add(new Separator());
            }

            MenuItem batchImport = new MenuItem();
            batchImport.Header = "批量添加桌面项目...（拖入失效时备用）";
            batchImport.Click += delegate { ImportMultipleFiles(); };
            MenuItem addRecycleBin = new MenuItem();
            addRecycleBin.Header = "添加回收站图标（桌面拖入失败时使用）";
            addRecycleBin.Click += delegate { AddItems(new string[] { RecycleBinKey }, 0, 0); };
            autoStart.Header = "开机自动启动";
            autoStart.IsCheckable = true;
            autoStart.Click += delegate
            {
                bool enable = autoStart.IsChecked;
                try
                {
                    StartupRegistration.SetEnabled(enable);
                    ShowStatus(enable ? "已开启开机自启动。" : "已关闭开机自启动。");
                }
                catch (Exception ex)
                {
                    autoStart.IsChecked = !enable;
                    MessageBox.Show("修改开机自启动失败：" + ex.Message, "Desktop Drawer",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            desktopIcons.Click += delegate
            {
                bool visible;
                if (!DesktopIconVisibility.TryGetVisible(out visible))
                {
                    ShowStatus("无法读取桌面图标状态，请检查 Windows Explorer。 ");
                    return;
                }
                string error;
                if (!DesktopIconVisibility.TrySetVisible(!visible, out error))
                {
                    MessageBox.Show(error ?? "无法切换桌面图标。", "Desktop Drawer",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                ShowStatus(error ?? (visible
                    ? "已隐藏 Windows 桌面图标；再次右键可恢复显示。"
                    : "已显示 Windows 桌面图标。"));
            };
            MenuItem exit = new MenuItem();
            exit.Header = "退出";
            exit.Click += delegate { Application.Current.Shutdown(); };

            menu.Items.Add(removeSelectedItem);
            menu.Items.Add(selectionSeparator);
            menu.Items.Add(batchImport);
            menu.Items.Add(addRecycleBin);
            menu.Items.Add(new Separator());
            menu.Items.Add(preferences);
            menu.Items.Add(new Separator());
            menu.Items.Add(desktopIcons);
            menu.Items.Add(exit);
            return menu;
        }

        private int Rows
        {
            get
            {
                // 不再人为限制最多 6 行。1047px 的屏幕允许约 13 行，短屏幕自动减少。
                int rows = NaturalRowsAt(rowSpacingPercent);
                // Preserve stored logical rows even if the physical display is now shorter.
                // 13th-row icons must not disappear when a 1080p display switches to 4:3.
                foreach (ShortcutEntry entry in shortcuts)
                    if (entry.Row >= 0 && entry.Row < 128)
                        rows = Math.Max(rows, entry.Row + 1);
                return Math.Min(128, rows);
            }
        }

        private static Brush UiColor(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        private static TextBlock UiLabel(string value, double fontSize, Brush foreground,
            FontWeight weight)
        {
            TextBlock label = new TextBlock();
            label.Text = value;
            label.FontSize = fontSize;
            label.FontWeight = weight;
            label.Foreground = foreground;
            return label;
        }

        private static Border UiCard()
        {
            Border card = new Border();
            card.Background = Brushes.White;
            card.BorderBrush = UiColor("#E2EAF2");
            card.BorderThickness = new Thickness(1);
            card.CornerRadius = new CornerRadius(12);
            card.Padding = new Thickness(14, 13, 14, 13);
            card.Margin = new Thickness(0, 0, 0, 10);
            return card;
        }

        private static Button UiButton(string content, bool emphasized)
        {
            Button button = new Button();
            button.Content = content;
            button.FontSize = 11;
            button.FontWeight = FontWeights.SemiBold;
            button.Padding = new Thickness(13, 8, 13, 8);
            button.Background = emphasized ? UiColor("#2778B8") : UiColor("#EEF3F8");
            button.Foreground = emphasized ? Brushes.White : UiColor("#2B4860");
            button.BorderThickness = new Thickness(0);
            button.Cursor = Cursors.Hand;
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border));
            surface.Name = "ButtonSurface";
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            surface.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background") { RelativeSource =
                    new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            FrameworkElementFactory text = new FrameworkElementFactory(typeof(ContentPresenter));
            text.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            text.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            text.SetBinding(FrameworkElement.MarginProperty,
                new System.Windows.Data.Binding("Padding") { RelativeSource =
                    new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
            surface.AppendChild(text);
            template.VisualTree = surface;
            Trigger hover = new Trigger();
            hover.Property = Button.IsMouseOverProperty;
            hover.Value = true;
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                emphasized ? UiColor("#1B69A6") : UiColor("#DDEAF5"), "ButtonSurface"));
            template.Triggers.Add(hover);
            button.Template = template;
            return button;
        }

        private static Border UiSliderCard(string title, string description,
            int minimum, int maximum, int current,
            out Slider slider, out TextBlock valueText)
        {
            Border card = UiCard();
            StackPanel content = new StackPanel();
            Grid heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition valueColumn = new ColumnDefinition();
            valueColumn.Width = GridLength.Auto;
            heading.ColumnDefinitions.Add(valueColumn);
            StackPanel titles = new StackPanel();
            titles.Children.Add(UiLabel(title, 13, UiColor("#283F54"), FontWeights.SemiBold));
            TextBlock tip = UiLabel(description, 10, UiColor("#8292A3"), FontWeights.Normal);
            tip.Margin = new Thickness(0, 3, 0, 0);
            titles.Children.Add(tip);
            heading.Children.Add(titles);
            valueText = UiLabel(current + "%", 15, UiColor("#2475B7"), FontWeights.SemiBold);
            valueText.VerticalAlignment = VerticalAlignment.Center;
            valueText.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(valueText, 1);
            heading.Children.Add(valueText);
            content.Children.Add(heading);
            slider = new Slider();
            slider.Minimum = minimum;
            slider.Maximum = maximum;
            slider.TickFrequency = 1;
            slider.IsSnapToTickEnabled = true;
            slider.Value = current;
            slider.Margin = new Thickness(0, 13, 0, 0);
            slider.AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft;
            slider.AutoToolTipPrecision = 0;
            content.Children.Add(slider);
            card.Child = content;
            return card;
        }

        // Settings preview is a temporary in-memory layout transaction: nothing is
        // written to disk until Save is clicked. Cancel/X restores all positions.
        private void ShowLiveSettingsDialog()
        {
            if (liveSettingsOpen) return;
            liveSettingsOpen = true;
            int originalColumns = columns;
            int originalRow = rowSpacingPercent;
            int originalColumn = columnSpacingPercent;
            int originalIcon = iconSizePercent;
            Dictionary<ShortcutEntry, Point> originalSlots = SnapshotSlots();
            bool committed = false;

            Window dialog = new Window();
            dialog.Title = "Desktop Drawer · 设置";
            dialog.Width = 474;
            dialog.Height = Math.Min(660, Math.Max(440, displayHeightDip - 24));
            dialog.Owner = this;
            dialog.ShowInTaskbar = false;
            dialog.ResizeMode = ResizeMode.NoResize;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            // 设置窗口放在抽屉右侧，避免挡住实时预览；小分辨率下自动收敛到屏幕内。
            dialog.Left = Math.Max(0, Math.Min(displayWidthDip - dialog.Width - 8,
                TargetWidth - RadiusDip + 18));
            dialog.Top = Math.Max(0, Math.Min(displayHeightDip - dialog.Height - 8, 46));
            dialog.Topmost = true;
            dialog.FontFamily = new FontFamily("Microsoft YaHei UI");
            dialog.Background = UiColor("#F3F6FA");

            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(20, 18, 20, 20);
            ScrollViewer settingsScroll = new ScrollViewer();
            settingsScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            settingsScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            settingsScroll.Content = panel;
            // 滑块区可在低分辨率下滚动；底部保存/取消始终保持可见。
            Grid dialogRoot = new Grid();
            RowDefinition scrollRow = new RowDefinition();
            scrollRow.Height = new GridLength(1, GridUnitType.Star);
            dialogRoot.RowDefinitions.Add(scrollRow);
            RowDefinition footerRow = new RowDefinition();
            footerRow.Height = GridLength.Auto;
            dialogRoot.RowDefinitions.Add(footerRow);
            Grid.SetRow(settingsScroll, 0);
            dialogRoot.Children.Add(settingsScroll);
            dialog.Content = dialogRoot;

            // 顶部信息区：独立设置窗口使用卡片，但抽屉内部仍只有桌面式图标。
            Border hero = new Border();
            hero.CornerRadius = new CornerRadius(15);
            hero.Background = new LinearGradientBrush(
                Color.FromRgb(19, 54, 88), Color.FromRgb(43, 112, 168), 0);
            hero.Padding = new Thickness(19, 17, 19, 17);
            hero.Margin = new Thickness(0, 0, 0, 18);
            StackPanel heroContent = new StackPanel();
            heroContent.Children.Add(UiLabel("DESKTOP DRAWER", 10, UiColor("#BED6EB"), FontWeights.SemiBold));
            TextBlock heroTitle = UiLabel("抽屉设置", 23, Brushes.White, FontWeights.SemiBold);
            heroTitle.Margin = new Thickness(0, 5, 0, 3);
            heroContent.Children.Add(heroTitle);
            heroContent.Children.Add(UiLabel("全屏磨砂预览 · 横向、纵向与图标大小均实时调整", 11,
                UiColor("#DAE8F4"), FontWeights.Normal));
            hero.Child = heroContent;
            panel.Children.Add(hero);

            TextBlock sectionLabel = UiLabel("布局与外观", 14, UiColor("#233D55"), FontWeights.SemiBold);
            sectionLabel.Margin = new Thickness(1, 0, 0, 10);
            panel.Children.Add(sectionLabel);

            TextBlock horizontalText;
            Slider horizontalSlider;
            Border horizontalCard = UiSliderCard("横向间距", "拖动时预览比例，松开后调整真实抽屉，避免磨砂重绘卡顿",
                MinColumnSpacingPercent, MaxColumnSpacingPercent, columnSpacingPercent,
                out horizontalSlider, out horizontalText);
            // 独立的轻量二维预览：只移动 10 个矩形，不调整 Acrylic 顶层 HWND。
            // 这不需要读图标、重建菜单或操作抽屉窗口尺寸。
            StackPanel horizontalContent = (StackPanel)horizontalCard.Child;
            TextBlock horizontalGuideText = UiLabel("横向格距预览 · 100% 为默认", 10,
                UiColor("#657F97"), FontWeights.Normal);
            horizontalGuideText.Margin = new Thickness(0, 11, 0, 4);
            horizontalContent.Children.Add(horizontalGuideText);
            Canvas horizontalGuide = new Canvas();
            horizontalGuide.Width = 346;
            horizontalGuide.Height = 43;
            horizontalGuide.HorizontalAlignment = HorizontalAlignment.Left;
            horizontalGuide.IsHitTestVisible = false;
            List<System.Windows.Shapes.Rectangle> guideIcons =
                new List<System.Windows.Shapes.Rectangle>();
            for (int i = 0; i < 10; i++)
            {
                System.Windows.Shapes.Rectangle marker = new System.Windows.Shapes.Rectangle();
                marker.Width = 12;
                marker.Height = 12;
                marker.RadiusX = 3;
                marker.RadiusY = 3;
                marker.Fill = UiColor(i % 2 == 0 ? "#4C9DD3" : "#70B5D9");
                Canvas.SetTop(marker, 3 + (i / 5) * 22);
                horizontalGuide.Children.Add(marker);
                guideIcons.Add(marker);
            }
            horizontalContent.Children.Add(horizontalGuide);
            panel.Children.Add(horizontalCard);

            TextBlock verticalText;
            Slider verticalSlider;
            panel.Children.Add(UiSliderCard("纵向间距", "调整图标上下排列密度",
                MinRowSpacingPercent, MaxRowSpacingPercent, rowSpacingPercent,
                out verticalSlider, out verticalText));

            TextBlock iconText;
            Slider iconSlider;
            panel.Children.Add(UiSliderCard("图标大小", "图标自动适配网格，不改变原始文件",
                MinIconSizePercent, MaxIconSizePercent, iconSizePercent,
                out iconSlider, out iconText));

            Grid colsLine = new Grid();
            colsLine.ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinition buttonsCol = new ColumnDefinition();
            buttonsCol.Width = GridLength.Auto;
            colsLine.ColumnDefinitions.Add(buttonsCol);
            StackPanel colHeading = new StackPanel();
            TextBlock colsText = UiLabel("抽屉列数：" + columns, 13,
                UiColor("#283F54"), FontWeights.SemiBold);
            colHeading.Children.Add(colsText);
            TextBlock colDesc = UiLabel("空间不足时可自动增加列数", 10,
                UiColor("#75879A"), FontWeights.Normal);
            colDesc.Margin = new Thickness(0, 3, 0, 0);
            colHeading.Children.Add(colDesc);
            Grid.SetColumn(colHeading, 0);
            colsLine.Children.Add(colHeading);
            StackPanel colButtons = new StackPanel();
            colButtons.Orientation = Orientation.Horizontal;
            Button minus = UiButton("－", false);
            minus.Width = 42;
            minus.Margin = new Thickness(0, 0, 7, 0);
            Button plus = UiButton("＋", false);
            plus.Width = 42;
            colButtons.Children.Add(minus);
            colButtons.Children.Add(plus);
            Grid.SetColumn(colButtons, 1);
            colsLine.Children.Add(colButtons);
            Border columnsCard = UiCard();
            columnsCard.Child = colsLine;
            panel.Children.Add(columnsCard);

            Border statusCard = UiCard();
            statusCard.Background = UiColor("#EAF3FC");
            statusCard.BorderThickness = new Thickness(0);
            TextBlock status = UiLabel("", 11, UiColor("#315B81"), FontWeights.Normal);
            status.TextWrapping = TextWrapping.Wrap;
            status.MinHeight = 35;
            statusCard.Child = status;
            panel.Children.Add(statusCard);

            TextBlock behaviorHeading = UiLabel("启动与桌面", 14,
                UiColor("#233D55"), FontWeights.SemiBold);
            behaviorHeading.Margin = new Thickness(1, 6, 0, 10);
            panel.Children.Add(behaviorHeading);
            Border behaviorCard = UiCard();
            StackPanel behavior = new StackPanel();
            CheckBox startupCheckbox = new CheckBox();
            startupCheckbox.Content = "登录 Windows 时自动启动抽屉";
            startupCheckbox.FontSize = 12;
            startupCheckbox.Foreground = UiColor("#283F54");
            try { startupCheckbox.IsChecked = StartupRegistration.IsEnabled(); }
            catch { startupCheckbox.IsEnabled = false; }
            behavior.Children.Add(startupCheckbox);
            CheckBox desktopCheckbox = new CheckBox();
            desktopCheckbox.Content = "显示 Windows 桌面图标";
            desktopCheckbox.FontSize = 12;
            desktopCheckbox.Foreground = UiColor("#283F54");
            desktopCheckbox.Margin = new Thickness(0, 14, 0, 0);
            bool desktopVisible;
            if (DesktopIconVisibility.TryGetVisible(out desktopVisible))
                desktopCheckbox.IsChecked = desktopVisible;
            else desktopCheckbox.IsEnabled = false;
            behavior.Children.Add(desktopCheckbox);
            TextBlock behaviorNote = UiLabel("这些选项在点击保存设置后生效。", 10,
                UiColor("#75879A"), FontWeights.Normal);
            behaviorNote.Margin = new Thickness(0, 12, 0, 0);
            behavior.Children.Add(behaviorNote);
            behaviorCard.Child = behavior;
            panel.Children.Add(behaviorCard);

            TextBlock shortcutHeading = UiLabel("快捷操作 · 将取消未保存的预览", 14,
                UiColor("#233D55"), FontWeights.SemiBold);
            shortcutHeading.Margin = new Thickness(1, 6, 0, 10);
            panel.Children.Add(shortcutHeading);
            Border shortcutCard = UiCard();
            StackPanel shortcutsRow = new StackPanel();
            shortcutsRow.Orientation = Orientation.Horizontal;
            Button import = UiButton("添加文件...", false);
            import.Margin = new Thickness(0, 0, 8, 0);
            Button recycle = UiButton("添加回收站", false);
            shortcutsRow.Children.Add(import);
            shortcutsRow.Children.Add(recycle);
            shortcutCard.Child = shortcutsRow;
            panel.Children.Add(shortcutCard);

            StackPanel actions = new StackPanel();
            actions.Orientation = Orientation.Horizontal;
            actions.HorizontalAlignment = HorizontalAlignment.Right;
            actions.Margin = new Thickness(0, 9, 0, 0);
            Button reset = UiButton("恢复 100%", false);
            reset.Margin = new Thickness(0, 0, 8, 0);
            Button cancel = UiButton("取消", false);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(0, 0, 8, 0);
            Button save = UiButton("保存设置", true);
            save.IsDefault = true;
            actions.Children.Add(reset);
            actions.Children.Add(cancel);
            actions.Children.Add(save);
            Border footer = new Border();
            footer.Background = UiColor("#F3F6FA");
            footer.BorderBrush = UiColor("#E1E9F1");
            footer.BorderThickness = new Thickness(0, 1, 0, 0);
            footer.Padding = new Thickness(20, 10, 20, 12);
            footer.Child = actions;
            Grid.SetRow(footer, 1);
            dialogRoot.Children.Add(footer);

            // 快捷操作直接使用现有实现；只有保存视图选项才提交布局变更。
            // 布局预览期间不能修改 shortcuts 列表，否则取消无法正确还原。
            // 点击快捷操作时先关闭设置并还原未保存的预览，再执行真实操作。
            bool importAfterClose = false;
            bool recycleAfterClose = false;
            import.Click += delegate { importAfterClose = true; dialog.DialogResult = false; };
            recycle.Click += delegate { recycleAfterClose = true; dialog.DialogResult = false; };

            // 每帧只消费最新一次滑块数值，丢弃已经过时的中间输入。
            // 真正的预览只修改已创建控件的位置/尺寸，而不重建整个网格。
            // Render 优先级有利于与 WPF 合成帧衔接，且不会阻塞每一个鼠标消息。
            int requestedColumns = columns;
            // 同一批图标的原始坐标在设置窗口生命周期内不变。
            // 缓存每种(行数,列数)的重排结果，避免横向百分比变化时重复搜索空格。
            Dictionary<long, Dictionary<ShortcutEntry, Point>> previewPlanCache =
                new Dictionary<long, Dictionary<ShortcutEntry, Point>>();
            // 设置期间主窗口只在进入/退出时使用自然缓动缩放。
            // 不再调用 HWND Width/SetWindowPos：全屏磨砂层宽度恒定，滑块只更新图标。
            bool previewQueued = false;
            EventHandler previewRendering = null;
            Action updateHorizontalGuide = delegate
            {
                int pct = (int)Math.Round(horizontalSlider.Value);
                double miniStep = 39.0 * pct / 100.0;
                for (int i = 0; i < guideIcons.Count; i++)
                    Canvas.SetLeft(guideIcons[i], 4 + (i % 5) * miniStep);
                horizontalGuideText.Text = "图标左右间距：" + pct + "% · 抽屉实时预览";
            };
            updateHorizontalGuide();
            bool dirty = true;
            bool lastValid = true;
            Action applyPreview = delegate
            {
                dirty = false;
                int h = (int)Math.Round(horizontalSlider.Value);
                int v = (int)Math.Round(verticalSlider.Value);
                int z = (int)Math.Round(iconSlider.Value);
                horizontalText.Text = h + "%";
                verticalText.Text = v + "%";
                iconText.Text = z + "%";
                int arrangedColumns, moved;
                Dictionary<ShortcutEntry, Point> plan;
                string problem;
                if (!TryPlanLiveSettings(originalSlots, requestedColumns, v, h, z,
                    previewPlanCache, out arrangedColumns, out moved, out plan, out problem))
                {
                    lastValid = false;
                    save.IsEnabled = false;
                    status.Foreground = Brushes.Firebrick;
                    status.Text = problem + "\n上一次有效预览保留；请减少间距或图标大小。";
                    colsText.Text = "抽屉列数：" + columns;
                    return;
                }
                lastValid = true;
                save.IsEnabled = true;
                status.Foreground = Brushes.DarkSlateGray;
                rowSpacingPercent = v;
                columnSpacingPercent = h;
                iconSizePercent = z;
                columns = arrangedColumns;
                foreach (ShortcutEntry entry in shortcuts)
                {
                    Point target = plan[entry];
                    int newColumn = (int)target.X;
                    int newRow = (int)target.Y;
                    if (entry.Column != newColumn) entry.Column = newColumn;
                    if (entry.Row != newRow) entry.Row = newRow;
                }
                string columnLabel = "抽屉列数：" + columns;
                if (colsText.Text != columnLabel) colsText.Text = columnLabel;
                string previewStatus = "实时预览：每列约 " + NaturalRowsAt(v) + " 行、共 " + columns
                    + " 列。" + (arrangedColumns > requestedColumns
                    ? "空间不足时已自动增加 " + (arrangedColumns - requestedColumns) + " 列。" : "")
                    + (moved > 0 ? "调整了 " + moved + " 个图标的网格位置。" : "原图标位置保持不变。");
                if (status.Text != previewStatus) status.Text = previewStatus;
                UpdateLivePreviewVisuals();
                // 只动当前图标网格，不设置 Window.Width、Left 或磨砂区域。
                // 全屏窗口的几何尺寸在滑块改变时保持不变。
            };
            // 随当前显示器/WPF 渲染时钟合帧：不再用 16ms DispatcherTimer
            // 人为锁定 60Hz。60/120/144/240Hz 均按实际 Rendering 回调运行，
            // 但能达到多少 FPS 取决于图标数量、WPF 与 GPU，不作固定帧率保证。
            previewRendering = delegate(object sender, EventArgs e)
            {
                if (!previewQueued) return;
                previewQueued = false;
                CompositionTarget.Rendering -= previewRendering;
                if (dirty) applyPreview();
            };
            Action requestPreview = delegate
            {
                dirty = true;
                if (previewQueued) return;
                previewQueued = true;
                CompositionTarget.Rendering += previewRendering;
            };
            horizontalSlider.ValueChanged += delegate
            {
                horizontalText.Text = (int)Math.Round(horizontalSlider.Value) + "%";
                updateHorizontalGuide();
                requestPreview();
            };
            verticalSlider.ValueChanged += delegate
            {
                verticalText.Text = (int)Math.Round(verticalSlider.Value) + "%";
                requestPreview();
            };
            iconSlider.ValueChanged += delegate
            {
                iconText.Text = (int)Math.Round(iconSlider.Value) + "%";
                requestPreview();
            };
            minus.Click += delegate { requestedColumns = Math.Max(MinColumns, columns - 1); requestPreview(); };
            plus.Click += delegate { requestedColumns = Math.Min(64, columns + 1); requestPreview(); };
            reset.Click += delegate
            {
                requestedColumns = originalColumns;
                horizontalSlider.Value = 100;
                verticalSlider.Value = 100;
                iconSlider.Value = 100;
                updateHorizontalGuide();
                requestPreview();
            };
            cancel.Click += delegate { dialog.DialogResult = false; };
            save.Click += delegate
            {
                // Save consumes the most recent slider input even if the next frame has
                // not rendered yet. Do not leave a queued preview after committing.
                if (previewQueued)
                {
                    previewQueued = false;
                    CompositionTarget.Rendering -= previewRendering;
                }
                if (dirty || columnSpacingPercent != (int)Math.Round(horizontalSlider.Value))
                    applyPreview();
                if (!lastValid) return;
                if (!SaveSettings())
                {
                    status.Foreground = Brushes.Firebrick;
                    status.Text = "保存图标位置失败，设置未提交；请检查磁盘权限。";
                    return;
                }
                if (!SaveViewSettings())
                {
                    RestoreSettings(originalSlots, originalColumns, originalRow,
                        originalColumn, originalIcon);
                    SaveSettings(); // undo any layout file already written
                    status.Foreground = Brushes.Firebrick;
                    status.Text = "保存外观参数失败；已尝试恢复原排列，请检查磁盘权限。";
                    requestPreview();
                    return;
                }
                try
                {
                    if (startupCheckbox.IsEnabled)
                        StartupRegistration.SetEnabled(startupCheckbox.IsChecked == true);
                    if (desktopCheckbox.IsEnabled)
                    {
                        bool visibleNow;
                        if (DesktopIconVisibility.TryGetVisible(out visibleNow) &&
                            visibleNow != (desktopCheckbox.IsChecked == true))
                        {
                            string toggleError;
                            if (!DesktopIconVisibility.TrySetVisible(desktopCheckbox.IsChecked == true,
                                out toggleError))
                                ShowStatus(toggleError ?? "切换桌面图标状态失败。");
                        }
                    }
                }
                catch (Exception ex)
                {
                    ShowStatus("已保存布局，但应用系统选项失败：" + ex.Message);
                }
                committed = true;
                dialog.DialogResult = true;
            };

            menuOpen = true;
            SetExpanded(true);
            holdUntil = DateTime.UtcNow.AddMinutes(3);
            // Prevent a background desktop-file notification from editing the
            // shortcut list while it is being previewed as a transaction.
            StopDesktopMonitoring();
            newDesktopPaths.Clear();
            try
            {
                applyPreview();
                dialog.ShowDialog();
            }
            finally
            {
                if (previewQueued)
                {
                    previewQueued = false;
                    CompositionTarget.Rendering -= previewRendering;
                }
                if (!committed)
                {
                    RestoreSettings(originalSlots, originalColumns, originalRow,
                        originalColumn, originalIcon);
                }
                // 重建仅在窗口关闭时发生一次：使透明空格网格与最终布局同步。
                // 滑动滑块期间始终走轻量级 UpdateLivePreviewVisuals()。
                BuildTiles();
                // 设置事务结束：恢复普通目标宽度，复用相同临界阻尼弹簧收回。
                // 不瞬间改 HWND 的 Width；保存和取消均具有对称的过渡动画。
                liveSettingsOpen = false;
                SetExpanded(true);
                // Windows 圆角裁剪随后随尺寸动画由 SizeChanged 正常更新。
                menuOpen = false;
                StartDesktopMonitoring();
                lastInside = DateTime.UtcNow;
                holdUntil = DateTime.UtcNow.AddMilliseconds(1500);
            }
            if (importAfterClose) ImportMultipleFiles();
            else if (recycleAfterClose) AddItems(new string[] { RecycleBinKey }, 0, 0);
        }

        private Dictionary<ShortcutEntry, Point> SnapshotSlots()
        {
            Dictionary<ShortcutEntry, Point> original = new Dictionary<ShortcutEntry, Point>();
            foreach (ShortcutEntry entry in shortcuts)
                original[entry] = new Point(entry.Column, entry.Row);
            return original;
        }

        private void RestoreSettings(Dictionary<ShortcutEntry, Point> original,
            int originalColumns, int originalRow, int originalColumn, int originalIcon)
        {
            columns = originalColumns;
            rowSpacingPercent = originalRow;
            columnSpacingPercent = originalColumn;
            iconSizePercent = originalIcon;
            foreach (ShortcutEntry entry in shortcuts)
            {
                Point value;
                if (original.TryGetValue(entry, out value))
                {
                    entry.Column = (int)value.X;
                    entry.Row = (int)value.Y;
                }
            }
        }

        private void LoadViewSettings()
        {
            try
            {
                if (File.Exists(viewSettingsPath))
                {
                    string[] lines = File.ReadAllLines(viewSettingsPath, Encoding.UTF8);
                    int r, c, z;
                    if (lines.Length >= 4 && lines[0] == "DesktopDrawer.ViewSettings/2"
                        && Int32.TryParse(lines[1], out r) && r >= MinRowSpacingPercent && r <= MaxRowSpacingPercent
                        && Int32.TryParse(lines[2], out c) && c >= MinColumnSpacingPercent && c <= MaxColumnSpacingPercent
                        && Int32.TryParse(lines[3], out z) && z >= MinIconSizePercent && z <= MaxIconSizePercent)
                    {
                        rowSpacingPercent = r;
                        columnSpacingPercent = c;
                        iconSizePercent = z;
                    }
                }
                else if (File.Exists(spacingSettingsPath))
                {
                    // Migration from Stage2_RowSpacing: preserve existing vertical %.
                    int parsed;
                    if (Int32.TryParse(File.ReadAllText(spacingSettingsPath, Encoding.UTF8).Trim(), out parsed)
                        && parsed >= MinRowSpacingPercent && parsed <= MaxRowSpacingPercent)
                        rowSpacingPercent = parsed;
                }
            }
            catch (Exception ex) { LogError(ex); }
        }

        private bool SaveViewSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(viewSettingsPath));
                string temp = viewSettingsPath + ".tmp";
                string content = "DesktopDrawer.ViewSettings/2\n" + rowSpacingPercent + "\n"
                    + columnSpacingPercent + "\n" + iconSizePercent + "\n";
                File.WriteAllText(temp, content, new UTF8Encoding(false));
                if (File.Exists(viewSettingsPath))
                    File.Replace(temp, viewSettingsPath, viewSettingsPath + ".bak", true);
                else File.Move(temp, viewSettingsPath);
                return true;
            }
            catch (Exception ex)
            {
                ShowStatus("保存布局外观失败：" + ex.Message);
                LogError(ex);
                return false;
            }
        }

        private bool TryPlanLiveSettings(Dictionary<ShortcutEntry, Point> original,
            int requestedColumns, int rowPct, int colPct, int iconPct,
            Dictionary<long, Dictionary<ShortcutEntry, Point>> planCache,
            out int plannedColumns, out int moved,
            out Dictionary<ShortcutEntry, Point> planned, out string problem)
        {
            planned = null;
            moved = 0;
            plannedColumns = columns;
            problem = "";
            double wantedW = CellW * colPct / 100.0;
            double wantedH = CellH * rowPct / 100.0;
            double iconWanted = IconDip * iconPct / 100.0;
            if (wantedW < iconWanted + 8 || wantedH < iconWanted + 21)
            {
                problem = "图标与格距冲突：请增大横向/纵向间距，或减小图标大小。";
                return false;
            }
            int rows = NaturalRowsAt(rowPct);
            int fitColumns = Math.Min(64, Math.Max(MinColumns,
                (int)Math.Floor((displayWidthDip - RadiusDip - 2 * Pad) / wantedW)));
            // A previously selected 4:3 resolution can be narrower than the saved
            // grid; preserve those columns as a compatibility fallback. A new
            // spacing change, however, cannot introduce NEW columns beyond screen width.
            int originalFit = Math.Min(64, Math.Max(MinColumns,
                (int)Math.Floor((displayWidthDip - RadiusDip - 2 * Pad)
                    / (CellW * columnSpacingPercent / 100.0))));
            int originalOccupied = HighestOriginalColumn(original);
            int fallback = originalOccupied > originalFit ? originalOccupied : MinColumns;
            int maxTry = Math.Min(64, Math.Max(fitColumns, fallback));
            int first = Math.Max(MinColumns, Math.Min(maxTry, requestedColumns));
            for (int candidate = first; candidate <= maxTry; candidate++)
            {
                Dictionary<ShortcutEntry, Point> trial;
                long cacheKey = ((long)rows << 32) | (uint)candidate;
                if (!planCache.TryGetValue(cacheKey, out trial))
                {
                    if (!PlanPreviewGrid(original, rows, candidate, out trial)) continue;
                    planCache[cacheKey] = trial;
                }
                planned = trial;
                plannedColumns = candidate;
                foreach (ShortcutEntry entry in shortcuts)
                {
                    Point old = original[entry];
                    Point dest = trial[entry];
                    if ((int)old.X != (int)dest.X || (int)old.Y != (int)dest.Y) moved++;
                }
                return true;
            }
            problem = "当前屏幕没有足够的格子容纳 " + shortcuts.Count + " 项；最多可用 "
                + maxTry + " 列，每列约 " + rows + " 行。";
            return false;
        }

        private static int HighestOriginalColumn(Dictionary<ShortcutEntry, Point> original)
        {
            int high = MinColumns;
            foreach (Point p in original.Values)
                high = Math.Max(high, Math.Min(64, (int)p.X + 1));
            return high;
        }

        // Operate on a snapshot, not the previous preview (avoid cumulative drift).
        private bool PlanPreviewGrid(Dictionary<ShortcutEntry, Point> source,
            int rows, int totalColumns, out Dictionary<ShortcutEntry, Point> planned)
        {
            planned = new Dictionary<ShortcutEntry, Point>();
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            List<ShortcutEntry> overflow = new List<ShortcutEntry>();
            foreach (ShortcutEntry entry in shortcuts)
            {
                Point old = source[entry];
                int c = (int)old.X, r = (int)old.Y;
                if (c >= 0 && c < totalColumns && r >= 0 && r < rows
                    && used.Add(c + ":" + r))
                    planned.Add(entry, new Point(c, r));
                else overflow.Add(entry);
            }
            overflow.Sort(delegate(ShortcutEntry a, ShortcutEntry b)
            {
                Point aa = source[a], bb = source[b];
                int result = ((int)aa.X).CompareTo((int)bb.X);
                return result != 0 ? result : ((int)aa.Y).CompareTo((int)bb.Y);
            });
            foreach (ShortcutEntry entry in overflow)
            {
                Point old = source[entry];
                int preferredC = Math.Max(0, (int)old.X) + Math.Max(0, (int)old.Y) / rows;
                int preferredR = Math.Max(0, (int)old.Y) % rows;
                if (preferredC <= (int)old.X) preferredC = (int)old.X + 1;
                int chosenC = -1, chosenR = -1;
                for (int pass = 0; pass < 2 && chosenC < 0; pass++)
                {
                    int first = pass == 0 ? Math.Max(0, preferredC) : 0;
                    int last = pass == 0 ? totalColumns : Math.Min(totalColumns, Math.Max(0, preferredC));
                    for (int c = first; c < last && chosenC < 0; c++)
                    {
                        for (int offset = 0; offset < rows; offset++)
                        {
                            int r = (c == preferredC ? preferredR + offset : offset) % rows;
                            if (!used.Contains(c + ":" + r))
                            { chosenC = c; chosenR = r; break; }
                        }
                    }
                }
                if (chosenC < 0) return false;
                used.Add(chosenC + ":" + chosenR);
                planned.Add(entry, new Point(chosenC, chosenR));
            }
            return planned.Count == shortcuts.Count;
        }

        // Desktop 里的快捷方式、EXE、文件夹和普通文件都是路径引用，不移动原件。
        // 回收站作为 Shell 虚拟项目单独识别；不操作其内部文件。
        private static bool IsSupportedItem(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            if (IsDrawerSettings(path) || IsRecycleBin(path)) return true;
            try { return File.Exists(path) || Directory.Exists(path); }
            catch (Exception) { return false; }
        }

        // 仅保存文件或文件夹的路径引用；绝不移动/修改 Windows 桌面原件。
        // 升级使用独立的 layout-v2.txt，旧版 shortcuts.txt 保持原样，便于回退。
        private void LoadSettings()
        {
            bool migrated = false;
            try
            {
                string loadPath = File.Exists(settingsPath) ? settingsPath : legacySettingsPath;
                if (!File.Exists(loadPath)) return;
                string[] lines = File.ReadAllLines(loadPath, Encoding.UTF8);
                bool legacy = String.Equals(loadPath, legacySettingsPath, StringComparison.OrdinalIgnoreCase);
                if (lines.Length < 2 || lines[0] != (legacy ? LegacySettingsHeader : SettingsHeader))
                {
                    ShowStatus("配置格式不匹配，已保留原文件，没有覆盖。");
                    return;
                }
                int savedColumns;
                if (Int32.TryParse(lines[1], out savedColumns))
                    columns = Math.Max(MinColumns, Math.Min(64, savedColumns));

                for (int i = 2; i < lines.Length; i++)
                {
                    try
                    {
                        string path;
                        int c, r;
                        if (legacy)
                        {
                            path = Encoding.UTF8.GetString(Convert.FromBase64String(lines[i]));
                            int ordinal = shortcuts.Count;
                            c = ordinal / Rows;
                            r = ordinal % Rows;
                        }
                        else
                        {
                            string[] fields = lines[i].Split(new char[] { '\t' }, 3);
                            if (fields.Length != 3 || !Int32.TryParse(fields[0], out c)
                                || !Int32.TryParse(fields[1], out r)) continue;
                            path = Encoding.UTF8.GetString(Convert.FromBase64String(fields[2]));
                        }
                        if (String.IsNullOrWhiteSpace(path) || ContainsShortcut(path)) continue;
                        // 保留已失效的旧路径记录，用户可以自行从抽屉移除。
                        shortcuts.Add(new ShortcutEntry(path, c, r));
                    }
                    catch (FormatException) { /* 不让单条异常配置影响其他图标。 */ }
                }
                NormalizeLoadedSlots();
                migrated = legacy;
            }
            catch (Exception ex)
            {
                ShowStatus("读取配置失败，原配置未改动：" + ex.Message);
                return;
            }
            if (migrated) SaveSettings();
        }

        // 按照列、行固定坐标加载。小屏幕、重叠或损坏位置只调整必要的项目，不压缩空位。
        private void NormalizeLoadedSlots()
        {
            int highest = columns;
            foreach (ShortcutEntry entry in shortcuts)
                if (entry.Column >= 0 && entry.Column < MaxColumns && entry.Row >= 0 && entry.Row < Rows)
                    highest = Math.Max(highest, entry.Column + 1);
            columns = Math.Min(MaxColumns, highest);

            List<ShortcutEntry> unsettled = new List<ShortcutEntry>();
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            foreach (ShortcutEntry entry in shortcuts)
            {
                string key = entry.Column + ":" + entry.Row;
                if (entry.Column < 0 || entry.Column >= columns || entry.Row < 0 || entry.Row >= Rows
                    || !used.Add(key))
                    unsettled.Add(entry);
            }
            foreach (ShortcutEntry entry in unsettled)
            {
                int c, r;
                while (!TryFindFreeCell(0, 0, out c, out r) && columns < MaxColumns) columns++;
                if (TryFindFreeCell(0, 0, out c, out r))
                {
                    entry.Column = c;
                    entry.Row = r;
                }
                // 容量不足时仍保留原配置中的条目，绝不静默删除。
            }
            if (unsettled.Count > 0)
                ShowStatus("检测到屏幕行数变化，已尽量保留图标和空位。请检查排列。");
        }

        private bool ContainsShortcut(string path)
        {
            foreach (ShortcutEntry item in shortcuts)
                if (String.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private ShortcutEntry EntryAt(int column, int row)
        {
            foreach (ShortcutEntry entry in shortcuts)
                if (entry.Column == column && entry.Row == row) return entry;
            return null;
        }

        // 在现有可见格子里寻找离指定位置最近的空格子。
        private bool TryFindFreeCell(int preferredColumn, int preferredRow, out int column, out int row)
        {
            column = -1;
            row = -1;
            int bestDistance = Int32.MaxValue;
            for (int c = 0; c < columns; c++)
                for (int r = 0; r < Rows; r++)
                {
                    if (EntryAt(c, r) != null) continue;
                    int dc = c - preferredColumn;
                    int dr = r - preferredRow;
                    int distance = dc * dc + dr * dr;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        column = c;
                        row = r;
                    }
                }
            return column >= 0;
        }

        private bool SaveSettings()
        {
            try
            {
                string folder = Path.GetDirectoryName(settingsPath);
                Directory.CreateDirectory(folder);
                List<string> lines = new List<string>();
                lines.Add(SettingsHeader);
                lines.Add(columns.ToString());
                foreach (ShortcutEntry entry in shortcuts)
                    lines.Add(entry.Column + "\t" + entry.Row + "\t" +
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(entry.Path)));
                string temp = settingsPath + ".tmp";
                File.WriteAllLines(temp, lines.ToArray(), new UTF8Encoding(false));
                if (File.Exists(settingsPath))
                    File.Replace(temp, settingsPath, settingsPath + ".bak", true);
                else
                    File.Move(temp, settingsPath);
                return true;
            }
            catch (Exception ex)
            {
                ShowStatus("保存布局失败：" + ex.Message);
                LogError(ex);
                return false;
            }
        }

        private void ShowStatus(string message)
        {
            hint.Text = message;
            hint.Visibility = Visibility.Visible;
            statusUntil = DateTime.UtcNow.AddSeconds(4);
        }

        // 使用高分辨率系统 Shell 图像列表；拿不到时再回退默认大图标。
        private static BitmapSource TryGetHighResolutionIcon(string path, double dpiScale)
        {
            Native.SHFILEINFO info = new Native.SHFILEINFO();
            Native.IImageList imageList = null;
            IntPtr icon = IntPtr.Zero;
            try
            {
                // SHGFI_SYSICONINDEX | SHGFI_OVERLAYINDEX 获取图标索引及快捷方式覆盖标记。
                IntPtr result = Native.SHGetFileInfo(path, 0, out info,
                    (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), 0x00004000 | 0x00000040);
                if (result == IntPtr.Zero) return null;
                int index = info.iIcon & 0x00FFFFFF;
                int overlay = (int)(((uint)info.iIcon >> 24) & 0xFF);
                int flags = 0x00000001; // ILD_TRANSPARENT
                if (overlay > 0 && overlay <= 15) flags |= overlay << 8;
                Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                // 高缩放屏幕优先 Jumbo 256px；否则优先 ExtraLarge 48px。
                int firstList = dpiScale > 1.5 ? 4 : 2;
                int hr = Native.SHGetImageList(firstList, ref iid, out imageList);
                if (hr < 0 || imageList == null)
                {
                    // Jumbo 不支持时，仍然优先使用 48px，而不是直接退回 32px。
                    if (imageList != null) { Marshal.ReleaseComObject(imageList); imageList = null; }
                    if (firstList != 4) return null;
                    hr = Native.SHGetImageList(2, ref iid, out imageList);
                    if (hr < 0 || imageList == null) return null;
                }
                if (imageList.GetIcon(index, flags, out icon) < 0 || icon == IntPtr.Zero) return null;
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(icon,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            catch (COMException) { return null; }
            catch (EntryPointNotFoundException) { return null; }
            catch (DllNotFoundException) { return null; }
            catch (ExternalException) { return null; }
            catch (InvalidCastException) { return null; }
            finally
            {
                if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
                if (imageList != null) Marshal.ReleaseComObject(imageList);
            }
        }

        private BitmapSource GetRecycleBinIcon()
        {
            IntPtr pidl = IntPtr.Zero;
            Native.SHFILEINFO info = new Native.SHFILEINFO();
            try
            {
                uint attributes;
                if (Native.SHParseDisplayName("::{645FF040-5081-101B-9F08-00AA002F954E}",
                    IntPtr.Zero, out pidl, 0, out attributes) != 0 || pidl == IntPtr.Zero) return null;
                uint flags = 0x00000008 | 0x00000100; // SHGFI_PIDL | SHGFI_ICON (large)
                if (Native.SHGetFileInfoPidl(pidl, 0, out info,
                    (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), flags) == IntPtr.Zero
                    || info.hIcon == IntPtr.Zero) return null;
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(info.hIcon,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            catch { return null; }
            finally
            {
                if (info.hIcon != IntPtr.Zero) Native.DestroyIcon(info.hIcon);
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
        }

        private static ImageSource CreateSettingsIcon()
        {
            DrawingGroup group = new DrawingGroup();
            LinearGradientBrush background = new LinearGradientBrush(
                Color.FromRgb(50, 135, 206), Color.FromRgb(18, 66, 110), 65);
            group.Children.Add(new GeometryDrawing(background, null,
                new RectangleGeometry(new Rect(1, 1, 62, 62), 14, 14)));
            Brush white = new SolidColorBrush(Color.FromRgb(246, 250, 255));
            // 一个透明底、高清矢量的齿轮；和普通桌面图标使用相同尺寸。
            for (int index = 0; index < 8; index++)
            {
                RectangleGeometry tooth = new RectangleGeometry(new Rect(28, 12, 8, 15), 2, 2);
                tooth.Transform = new RotateTransform(index * 45, 32, 32);
                group.Children.Add(new GeometryDrawing(white, null, tooth));
            }
            group.Children.Add(new GeometryDrawing(white, null,
                new EllipseGeometry(new Point(32, 32), 17, 17)));
            group.Children.Add(new GeometryDrawing(
                new SolidColorBrush(Color.FromRgb(28, 94, 150)), null,
                new EllipseGeometry(new Point(32, 32), 8.5, 8.5)));
            group.Freeze();
            DrawingImage image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        private ImageSource GetShortcutIcon(string path)
        {
            if (IsDrawerSettings(path))
            {
                ImageSource settingIcon;
                if (!icons.TryGetValue(path, out settingIcon))
                {
                    settingIcon = CreateSettingsIcon();
                    icons[path] = settingIcon;
                }
                return settingIcon;
            }
            if (IsRecycleBin(path))
            {
                ImageSource special;
                if (icons.TryGetValue(path, out special)) return special;
                BitmapSource image = GetRecycleBinIcon();
                if (image != null) icons[path] = image;
                return image;
            }
            ImageSource existing;
            if (icons.TryGetValue(path, out existing)) return existing;
            if (!IsSupportedItem(path)) return null;
            BitmapSource hiRes = TryGetHighResolutionIcon(path, scale);
            if (hiRes != null)
            {
                icons[path] = hiRes;
                return hiRes;
            }
            Native.SHFILEINFO info = new Native.SHFILEINFO();
            try
            {
                IntPtr result = Native.SHGetFileInfo(path, 0, out info,
                    (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), 0x00000100 /* SHGFI_ICON | LARGEICON */);
                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(info.hIcon,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                icons[path] = bitmap;
                return bitmap;
            }
            catch (Exception ex)
            {
                LogError(ex);
                return null;
            }
            finally
            {
                if (info.hIcon != IntPtr.Zero) Native.DestroyIcon(info.hIcon);
            }
        }

        private void BuildTiles()
        {
            tiles.Children.Clear();
            shortcutCells.Clear();
            double cellHeight = DisplayCellH;
            double iconSize = DisplayIconSize;
            tiles.Width = columns * DisplayCellW;
            tiles.Height = Rows * cellHeight;
            tileViewport.Width = ViewportWidth;
            tileViewport.Height = ViewportHeight;
            // Scale the entire visible grid, but preserve its internal (column,row)
            // coordinates for dragging, marquee selection and saved layout.
            double sx = Math.Min(1.0, ViewportWidth / Math.Max(1.0, columns * DisplayCellW));
            // RenderTransform 只改变合成坐标，不会像 LayoutTransform 那样使整个
            // ScrollViewer / Canvas 重新 Measure + Arrange，尤其适合横向连续预览。
            tiles.RenderTransform = new ScaleTransform(sx, 1.0);
            for (int c = 0; c < columns; c++)
            {
                for (int r = 0; r < Rows; r++)
                {
                    ShortcutEntry item = EntryAt(c, r);
                    Border cell = new Border();
                    cell.Width = DisplayCellW;
                    cell.Height = cellHeight;
                    cell.BorderThickness = new Thickness(0);
                    cell.Background = Brushes.Transparent;  // 平时所有网格都是完全透明的
                    Canvas.SetLeft(cell, c * DisplayCellW);
                    Canvas.SetTop(cell, r * cellHeight);
                    if (item != null)
                    {
                        string path = item.Path;
                        bool exists = IsSupportedItem(path);
                        cell.Cursor = exists ? Cursors.Hand : Cursors.Arrow;
                        cell.ToolTip = IsDrawerSettings(path) ? "抽屉设置 · 单击打开，可拖动排列" :
                            path + (exists ? "" : "\n（原快捷方式已不存在）");
                        StackPanel content = new StackPanel();
                        content.HorizontalAlignment = HorizontalAlignment.Center;
                        content.VerticalAlignment = VerticalAlignment.Top;
                        content.Margin = new Thickness(0, cellHeight < 62 ? 2 : 5, 0, 0);
                        ImageSource icon = GetShortcutIcon(path);
                        if (icon != null)
                        {
                            Image image = new Image();
                            image.Width = iconSize;
                            image.Height = iconSize;
                            image.Stretch = Stretch.Uniform;
                            image.SnapsToDevicePixels = true;
                            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                            image.Source = icon;
                            image.IsHitTestVisible = false;
                            content.Children.Add(image);
                        }
                        else
                        {
                            TextBlock fallback = new TextBlock();
                            fallback.Text = exists ? "↗" : "!";
                            fallback.FontSize = 26;
                            fallback.TextAlignment = TextAlignment.Center;
                            fallback.Foreground = Brushes.White;
                            fallback.IsHitTestVisible = false;
                            content.Children.Add(fallback);
                        }
                        TextBlock label = new TextBlock();
                        label.Text = IsDrawerSettings(path) ? "抽屉设置" :
                            (IsRecycleBin(path) ? RecycleDisplayName() : Path.GetFileNameWithoutExtension(path));
                        label.FontFamily = new FontFamily("Microsoft YaHei UI");
                        label.FontSize = DisplayLabelSize;
                        label.Foreground = exists ? Brushes.White : Brushes.LightGray;
                        label.TextAlignment = TextAlignment.Center;
                        label.TextWrapping = TextWrapping.Wrap;
                        label.TextTrimming = TextTrimming.CharacterEllipsis;
                        label.Width = DisplayCellW - 4;
                        label.Margin = new Thickness(0, 2, 0, 0);
                        label.MaxHeight = DisplayLabelHeight;
                        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
                        label.IsHitTestVisible = false;
                        content.Children.Add(label);
                        cell.Child = content;
                        shortcutCells[path] = cell;
                        string chosenPath = path;
                        // 左键按下后轻微移动才开始拖动；不再把每一次松开都识别为单击启动。
                        cell.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
                        {
                            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                            {
                                // Ctrl+单击可增减单个选中项，不启动应用。
                                if (!selectedShortcuts.Add(chosenPath)) selectedShortcuts.Remove(chosenPath);
                                pressedShortcut = null;
                                suppressLaunch = true;
                                UpdateSelectionVisuals();
                                e.Handled = true;
                                return;
                            }
                            if (e.ClickCount >= 2 && selectedShortcuts.Contains(chosenPath))
                            {
                                pressedShortcut = null;
                                suppressLaunch = true;
                                e.Handled = true;
                                LaunchShortcut(chosenPath);
                                return;
                            }
                            // 点击已选中图标时保留整个选区，使其可以作为整体拖动。
                            // 点击未选中的图标则改为单选；单独图标仍可拖动/启动。
                            bool hadSelection = selectedShortcuts.Count > 0;
                            if (hadSelection && !selectedShortcuts.Contains(chosenPath))
                            {
                                selectedShortcuts.Clear();
                                selectedShortcuts.Add(chosenPath);
                                UpdateSelectionVisuals();
                            }
                            pressedShortcut = chosenPath;
                            pressedPoint = e.GetPosition(tiles);
                            suppressLaunch = hadSelection;
                        };
                        cell.MouseMove += delegate(object sender, MouseEventArgs e)
                        {
                            if (e.LeftButton != MouseButtonState.Pressed || internalDraggingPath != null
                                || !String.Equals(pressedShortcut, chosenPath, StringComparison.OrdinalIgnoreCase)) return;
                            Point current = e.GetPosition(tiles);
                            if (Math.Abs(current.X - pressedPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                                && Math.Abs(current.Y - pressedPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                            pressedShortcut = null;
                            suppressLaunch = true;
                            internalDraggingPath = chosenPath;
                            internalDraggingPaths.Clear();
                            if (selectedShortcuts.Contains(chosenPath) && selectedShortcuts.Count > 1)
                            {
                                foreach (string selected in selectedShortcuts) internalDraggingPaths.Add(selected);
                            }
                            else internalDraggingPaths.Add(chosenPath);
                            holdUntil = DateTime.UtcNow.AddSeconds(5);
                            DataObject payload = new DataObject();
                            payload.SetData(InternalDragFormat, chosenPath);
                            // Windows/WeChat/QQ/Explorer recognize CF_HDROP (FileDrop),
                            // not the private in-drawer position-transfer format.
                            List<string> filesToShare = new List<string>();
                            foreach (string selectedPath in internalDraggingPaths)
                                if (!IsRecycleBin(selectedPath) && !IsDrawerSettings(selectedPath)
                                    && IsSupportedItem(selectedPath))
                                    filesToShare.Add(selectedPath);
                            if (filesToShare.Count > 0)
                                payload.SetData(DataFormats.FileDrop, filesToShare.ToArray());
                            // Move is internal-only; external apps must not be offered a
                            // filesystem Move operation (which could remove source files).
                            try { DragDrop.DoDragDrop(cell, payload,
                                DragDropEffects.Copy | DragDropEffects.Link); }
                            catch (Exception ex)
                            {
                                ShowStatus("拖动失败：" + ex.Message);
                                LogError(ex);
                            }
                            finally
                            {
                                internalDraggingPath = null;
                                internalDraggingPaths.Clear();
                                HideDropTarget();
                                holdUntil = DateTime.UtcNow.AddMilliseconds(900);
                            }
                        };
                        cell.MouseLeftButtonUp += delegate(object sender, MouseButtonEventArgs e)
                        {
                            bool canLaunch = !suppressLaunch &&
                                String.Equals(pressedShortcut, chosenPath, StringComparison.OrdinalIgnoreCase);
                            pressedShortcut = null;
                            if (canLaunch)
                            {
                                e.Handled = true;
                                LaunchShortcut(chosenPath);
                            }
                        };
                        cell.PreviewMouseRightButtonDown += delegate
                        {
                            // 右键选中的图标保留多选；右键其他图标取消旧的多选。
                            // 没有选中状态时，不额外制造单选，以免改变单击启动体验。
                            if (selectedShortcuts.Count > 0 && !selectedShortcuts.Contains(chosenPath))
                                ClearSelection();
                        };
                        ContextMenu itemMenu = new ContextMenu();
                        MenuItem bulkRemove = new MenuItem();
                        Separator bulkSeparator = new Separator();
                        bulkRemove.Click += delegate { RemoveSelectedShortcuts(); };
                        itemMenu.Opened += delegate
                        {
                            menuOpen = true;
                            bulkRemove.Header = "从抽屉移除选中的 " + selectedShortcuts.Count + " 项";
                            bulkRemove.Visibility = selectedShortcuts.Count > 1 && selectedShortcuts.Contains(chosenPath)
                                ? Visibility.Visible : Visibility.Collapsed;
                            bulkSeparator.Visibility = bulkRemove.Visibility;
                        };
                        itemMenu.Closed += delegate { menuOpen = false; lastInside = DateTime.UtcNow; };
                        MenuItem openLocation = new MenuItem();
                        openLocation.Header = "在资源管理器中显示实际文件";
                        openLocation.Click += delegate { OpenFileLocation(chosenPath); };
                        MenuItem renameActual = new MenuItem();
                        renameActual.Header = "重命名实际文件...";
                        renameActual.Click += delegate { RenameRealFile(chosenPath); };
                        MenuItem remove = new MenuItem();
                        remove.Header = "仅从抽屉移除（不删除原文件）";
                        remove.Click += delegate { RemoveShortcut(chosenPath); };
                        if (IsDrawerSettings(chosenPath))
                        {
                            MenuItem openDrawerSettings = new MenuItem();
                            openDrawerSettings.Header = "打开抽屉设置";
                            openDrawerSettings.Click += delegate { ShowLiveSettingsDialog(); };
                            itemMenu.Items.Add(openDrawerSettings);
                        }
                        else
                        {
                            itemMenu.Items.Add(bulkRemove);
                            itemMenu.Items.Add(bulkSeparator);
                        }
                        if (!IsRecycleBin(chosenPath) && !IsDrawerSettings(chosenPath))
                        {
                            itemMenu.Items.Add(openLocation);
                            itemMenu.Items.Add(renameActual);
                            itemMenu.Items.Add(new Separator());
                        }
                        if (!IsDrawerSettings(chosenPath)) itemMenu.Items.Add(remove);
                        cell.ContextMenu = itemMenu;
                        cell.ContextMenuOpening += delegate(object sender, ContextMenuEventArgs e)
                        {
                            if (!IsRecycleBin(chosenPath) && !IsDrawerSettings(chosenPath)
                                && IsSupportedItem(chosenPath) && !nativeMenuInUse)
                            {
                                e.Handled = true;
                                bool nativeShown = false;
                                nativeMenuInUse = true;
                                menuOpen = true;
                                try
                                {
                                    int count = selectedShortcuts.Contains(chosenPath) ? selectedShortcuts.Count : 1;
                                    nativeShown = ShellFileMenu.Show(hwnd, chosenPath, count,
                                        delegate { RemoveShortcut(chosenPath); },
                                        delegate { RemoveSelectedShortcuts(); },
                                        delegate { RenameRealFile(chosenPath); },
                                        delegate { BeginDesktopOperationWatch(chosenPath); });
                                }
                                finally
                                {
                                    nativeMenuInUse = false;
                                    menuOpen = false;
                                    lastInside = DateTime.UtcNow;
                                    holdUntil = DateTime.UtcNow.AddMilliseconds(1200);
                                }
                                if (!nativeShown)
                                {
                                    ShowStatus("无法调用该文件的系统右键菜单，已显示抽屉菜单。");
                                    itemMenu.PlacementTarget = cell;
                                    itemMenu.IsOpen = true;
                                }
                            }
                        };
                    }
                    else
                    {
                        // 空位允许接收拖放，但是不显示占位方框。
                        cell.IsHitTestVisible = false;
                    }
                    tiles.Children.Add(cell);
                }
            }
            // 只在拖动悬停时短暂显示细线定位框；其余时间完全没有格子背景。
            dropTarget.Width = DisplayCellW - 2;
            dropTarget.Height = cellHeight - 2;
            dropTarget.BorderThickness = new Thickness(1);
            dropTarget.BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, 255, 255, 255));
            dropTarget.Background = Brushes.Transparent;
            dropTarget.CornerRadius = new CornerRadius(6);
            dropTarget.IsHitTestVisible = false;
            dropTarget.Visibility = Visibility.Collapsed;
            tiles.Children.Add(dropTarget);

            // 选框位于图标之上但不参与鼠标命中测试；结束框选后立即隐藏。
            selectionRectangle.IsHitTestVisible = false;
            selectionRectangle.Visibility = Visibility.Collapsed;
            selectionRectangle.Fill = new SolidColorBrush(Color.FromArgb(0x2A, 80, 150, 255));
            selectionRectangle.Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 140, 192, 255));
            selectionRectangle.StrokeThickness = 1;
            selectionRectangle.StrokeDashArray = new DoubleCollection(new double[] { 4, 3 });
            tiles.Children.Add(selectionRectangle);
            UpdateSelectionVisuals();
            previewCellWidth = DisplayCellW;
            previewCellHeight = DisplayCellH;
            previewIconSize = DisplayIconSize;
            previewLabelSize = DisplayLabelSize;
            previewLabelHeight = DisplayLabelHeight;
        }

        // 轻量级实时预览：不要调用 BuildTiles()。以前每移动 1% 就会删除并重新
        // 创建所有 Border / Image / TextBlock / ContextMenu / 鼠标事件，造成明显卡顿。
        // 这里复用现成的图标控件，仅更新网格坐标与可见尺寸。空格是透明、不可点击
        // 的旧占位元素；Canvas 自身承接框选，所以预览时无需重新创建空格控件。
        private void UpdateLivePreviewVisuals()
        {
            double cellWidth = DisplayCellW;
            double cellHeight = DisplayCellH;
            double iconSize = DisplayIconSize;
            double labelSize = DisplayLabelSize;
            double labelHeight = DisplayLabelHeight;
            double viewportWidth = ViewportWidth;
            double viewportHeight = ViewportHeight;
            bool horizontalChanged = Math.Abs(cellWidth - previewCellWidth) > 0.001;
            bool verticalChanged = Math.Abs(cellHeight - previewCellHeight) > 0.001;
            bool iconChanged = Math.Abs(iconSize - previewIconSize) > 0.001;
            bool labelChanged = Math.Abs(labelSize - previewLabelSize) > 0.001 ||
                Math.Abs(labelHeight - previewLabelHeight) > 0.001;

            double gridWidth = columns * cellWidth;
            double gridHeight = Rows * cellHeight;
            if (Math.Abs(tiles.Width - gridWidth) > 0.001) tiles.Width = gridWidth;
            if (Math.Abs(tiles.Height - gridHeight) > 0.001) tiles.Height = gridHeight;
            if (Math.Abs(tileViewport.Width - viewportWidth) > 0.001)
                tileViewport.Width = viewportWidth;
            if (Math.Abs(tileViewport.Height - viewportHeight) > 0.001)
                tileViewport.Height = viewportHeight;

            double sx = Math.Min(1.0, viewportWidth / Math.Max(1.0, gridWidth));
            ScaleTransform transform = tiles.RenderTransform as ScaleTransform;
            if (transform == null)
            {
                transform = new ScaleTransform(1.0, 1.0);
                tiles.RenderTransform = transform;
            }
            if (Math.Abs(transform.ScaleX - sx) > 0.0001) transform.ScaleX = sx;

            foreach (ShortcutEntry item in shortcuts)
            {
                Border cell;
                if (!shortcutCells.TryGetValue(item.Path, out cell))
                {
                    // 只有出现未知图标时才重建；列数增加无需生成更多空格 Border。
                    BuildTiles();
                    return;
                }
                double x = item.Column * cellWidth;
                double y = item.Row * cellHeight;
                if (Math.Abs(Canvas.GetLeft(cell) - x) > 0.001) Canvas.SetLeft(cell, x);
                if (Math.Abs(Canvas.GetTop(cell) - y) > 0.001) Canvas.SetTop(cell, y);
                if (horizontalChanged) cell.Width = cellWidth;
                if (verticalChanged) cell.Height = cellHeight;

                StackPanel content = cell.Child as StackPanel;
                if (content == null) continue;
                if (verticalChanged)
                    content.Margin = new Thickness(0, cellHeight < 62 ? 2 : 5, 0, 0);
                if (iconChanged)
                {
                    foreach (UIElement child in content.Children)
                    {
                        Image icon = child as Image;
                        if (icon != null)
                        {
                            icon.Width = iconSize;
                            icon.Height = iconSize;
                        }
                    }
                }
                if (content.Children.Count > 0 &&
                    (horizontalChanged || verticalChanged || iconChanged || labelChanged))
                {
                    TextBlock label = content.Children[content.Children.Count - 1] as TextBlock;
                    if (label != null)
                    {
                        if (horizontalChanged) label.Width = cellWidth - 4;
                        if (labelChanged || verticalChanged || iconChanged)
                        {
                            label.FontSize = labelSize;
                            label.MaxHeight = labelHeight;
                        }
                    }
                }
            }
            if (horizontalChanged) dropTarget.Width = cellWidth - 2;
            if (verticalChanged) dropTarget.Height = cellHeight - 2;
            HideDropTarget();
            previewCellWidth = cellWidth;
            previewCellHeight = cellHeight;
            previewIconSize = iconSize;
            previewLabelSize = labelSize;
            previewLabelHeight = labelHeight;
        }

        // 仅在真正选中时显示轻微高亮；平时保持用户要求的完全透明格子。
        private void UpdateSelectionVisuals()
        {
            foreach (KeyValuePair<string, Border> pair in shortcutCells)
            {
                bool selected = selectedShortcuts.Contains(pair.Key);
                pair.Value.Background = selected
                    ? (Brush)new SolidColorBrush(Color.FromArgb(0x30, 70, 145, 245))
                    : Brushes.Transparent;
                pair.Value.BorderBrush = selected
                    ? (Brush)new SolidColorBrush(Color.FromArgb(0xA0, 150, 205, 255))
                    : Brushes.Transparent;
                pair.Value.BorderThickness = selected ? new Thickness(1) : new Thickness(0);
            }
        }

        private void ClearSelection()
        {
            if (selectedShortcuts.Count == 0) return;
            selectedShortcuts.Clear();
            UpdateSelectionVisuals();
        }

        // 在任意空格（或图标格的边缘空隙）按下左键，启动桌面式框选。
        private void OnTilesPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (marqueePending || internalDraggingPath != null) return;
            Point p = e.GetPosition(tiles);
            if (p.X < 0 || p.Y < 0 || p.X >= tiles.Width || p.Y >= tiles.Height) return;
            int c = (int)Math.Floor(p.X / DisplayCellW);
            int r = (int)Math.Floor(p.Y / DisplayCellH);
            ShortcutEntry existing = EntryAt(c, r);
            double localX = p.X - c * DisplayCellW;
            // 图标和文字的主体区域交给旧版点击/拖动逻辑；格子侧边空隙可拉框。
            bool onIcon = existing != null && localX >= 7 && localX < DisplayCellW - 7;
            if (onIcon) return;

            marqueePending = true;
            marqueeActive = false;
            marqueeStart = p;
            marqueeAdditive = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            selectionBeforeMarquee.Clear();
            if (marqueeAdditive)
                foreach (string path in selectedShortcuts) selectionBeforeMarquee.Add(path);
            else ClearSelection();
            pressedShortcut = null;
            suppressLaunch = true;
            holdUntil = DateTime.UtcNow.AddSeconds(8);
            tiles.CaptureMouse();
            e.Handled = true;
        }

        private void OnTilesPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!marqueePending) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndMarquee();
                return;
            }
            Point p = e.GetPosition(tiles);
            p.X = Math.Max(0, Math.Min(tiles.Width, p.X));
            p.Y = Math.Max(0, Math.Min(tiles.Height, p.Y));
            if (!marqueeActive && Math.Abs(p.X - marqueeStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - marqueeStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                e.Handled = true;
                return;
            }
            marqueeActive = true;
            Rect area = new Rect(new Point(Math.Min(p.X, marqueeStart.X), Math.Min(p.Y, marqueeStart.Y)),
                new Point(Math.Max(p.X, marqueeStart.X), Math.Max(p.Y, marqueeStart.Y)));
            Canvas.SetLeft(selectionRectangle, area.Left);
            Canvas.SetTop(selectionRectangle, area.Top);
            selectionRectangle.Width = area.Width;
            selectionRectangle.Height = area.Height;
            selectionRectangle.Visibility = Visibility.Visible;
            selectedShortcuts.Clear();
            foreach (string path in selectionBeforeMarquee) selectedShortcuts.Add(path);
            foreach (ShortcutEntry item in shortcuts)
            {
                if (item.Column < 0 || item.Row < 0 || item.Column >= columns || item.Row >= Rows) continue;
                Rect cellArea = new Rect(item.Column * DisplayCellW + 7, item.Row * DisplayCellH + 4,
                    DisplayCellW - 14, DisplayCellH - 8);
                if (area.IntersectsWith(cellArea)) selectedShortcuts.Add(item.Path);
            }
            UpdateSelectionVisuals();
            holdUntil = DateTime.UtcNow.AddSeconds(3);
            e.Handled = true;
        }

        private void OnTilesPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!marqueePending) return;
            EndMarquee();
            e.Handled = true;
        }

        private void OnTilesLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (marqueePending) EndMarquee();
        }

        private void EndMarquee()
        {
            marqueePending = false;
            marqueeActive = false;
            selectionRectangle.Visibility = Visibility.Collapsed;
            if (tiles.IsMouseCaptured) tiles.ReleaseMouseCapture();
            holdUntil = DateTime.UtcNow.AddSeconds(2);
        }

        private void OnDrawerKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && selectedShortcuts.Count > 0)
            {
                ClearSelection();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && selectedShortcuts.Count > 0)
            {
                RemoveSelectedShortcuts();
                e.Handled = true;
            }
        }

        // 一次从抽屉配置中移除多个引用，不碰 .lnk/.url 原文件；保留剩余项目坐标和空位。
        private void RemoveSelectedShortcuts()
        {
            // 内置设置图标可以参与框选和整组移动，但绝不能误删。
            selectedShortcuts.Remove(DrawerSettingsKey);
            int count = selectedShortcuts.Count;
            if (count == 0) { ShowStatus("抽屉设置图标不能删除；可以自由移动位置。"); return; }
            MessageBoxResult answer = MessageBox.Show(this,
                "确定从抽屉移除选中的 " + count + " 个快捷方式吗？\n\n只移除抽屉里的图标，不会删除桌面快捷方式或程序。",
                "批量移除快捷方式", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            int removed = shortcuts.RemoveAll(delegate(ShortcutEntry entry)
            {
                return selectedShortcuts.Contains(entry.Path);
            });
            foreach (string path in selectedShortcuts) icons.Remove(path);
            selectedShortcuts.Clear();
            BuildTiles();
            if (removed > 0 && SaveSettings())
                ShowStatus("已从抽屉移除 " + removed + " 项；桌面文件和其他图标位置不变。");
        }

        private bool TryGetDropCell(DragEventArgs e, out int column, out int row)
        {
            Point p = e.GetPosition(tiles);
            // 批量拖入时光标可能落在抽屉最外侧几像素的内边距上，按最近格处理。
            if (p.X < -Pad || p.Y < -Pad || p.X > tiles.Width + Pad || p.Y > tiles.Height + Pad)
            {
                column = row = -1;
                return false;
            }
            column = Math.Max(0, Math.Min(columns - 1,
                (int)Math.Floor(Math.Max(0, p.X) / DisplayCellW)));
            row = Math.Max(0, Math.Min(Rows - 1,
                (int)Math.Floor(Math.Max(0, p.Y) / DisplayCellH)));
            return true;
        }

        private void ShowDropTarget(int column, int row)
        {
            Canvas.SetLeft(dropTarget, column * DisplayCellW + 1);
            Canvas.SetTop(dropTarget, row * DisplayCellH + 1);
            dropTarget.Visibility = Visibility.Visible;
        }

        private void HideDropTarget()
        {
            dropTarget.Visibility = Visibility.Collapsed;
        }

        private void MoveShortcut(string path, int column, int row)
        {
            ShortcutEntry source = null;
            foreach (ShortcutEntry entry in shortcuts)
                if (String.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
                { source = entry; break; }
            if (source == null || (source.Column == column && source.Row == row)) return;

            // 空位 = 直接占据，原位置留下空白；已有图标 = 与之交换，绝不堆叠。
            ShortcutEntry other = EntryAt(column, row);
            if (other != null)
            {
                other.Column = source.Column;
                other.Row = source.Row;
            }
            source.Column = column;
            source.Row = row;
            BuildTiles();
            SaveSettings();
        }

        // Move every selected item by the same grid offset: preserves holes and their shape.
        // A collision with any unselected item or an out-of-range destination cancels ALL moves.
        private void MoveShortcutGroup(string anchorPath, int column, int row)
        {
            List<ShortcutEntry> moving = new List<ShortcutEntry>();
            HashSet<string> members = new HashSet<string>(internalDraggingPaths, StringComparer.OrdinalIgnoreCase);
            ShortcutEntry anchor = null;
            foreach (ShortcutEntry item in shortcuts)
            {
                if (!members.Contains(item.Path)) continue;
                moving.Add(item);
                if (String.Equals(item.Path, anchorPath, StringComparison.OrdinalIgnoreCase)) anchor = item;
            }
            if (anchor == null || moving.Count == 0) return;
            int dc = column - anchor.Column;
            int dr = row - anchor.Row;
            if (dc == 0 && dr == 0) return;
            foreach (ShortcutEntry item in moving)
            {
                int targetC = item.Column + dc;
                int targetR = item.Row + dr;
                if (targetC < 0 || targetC >= columns || targetR < 0 || targetR >= Rows)
                {
                    ShowStatus("整组选区会超出网格。可增加列数，或换个落点。");
                    return;
                }
                ShortcutEntry occupying = EntryAt(targetC, targetR);
                if (occupying != null && !members.Contains(occupying.Path))
                {
                    ShowStatus("目标格已有未选中的图标，整组未移动；请拖到空白区域。");
                    return;
                }
            }
            foreach (ShortcutEntry item in moving)
            {
                item.Column += dc;
                item.Row += dr;
            }
            BuildTiles();
            SaveSettings();
            ShowStatus("已整体移动 " + moving.Count + " 项，空位和相对排列保持不变。");
        }


        // Start temporary synchronization only when the user invokes a native Shell
        // operation from within this drawer. Unrelated new Desktop files are not
        // continuously auto-imported during normal use.
        private void BeginDesktopOperationWatch(string originalPath)
        {
            importDesktopChangesUntil = DateTime.UtcNow.AddSeconds(90);
            importNearPath = originalPath;
            newDesktopPaths.Clear();
        }

        private void StartDesktopMonitoring()
        {
            StopDesktopMonitoring();
            string[] desktops = new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
            };
            foreach (string desktop in desktops)
            {
                if (String.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) continue;
                bool seen = false;
                foreach (FileSystemWatcher watcher in desktopWatchers)
                    if (String.Equals(watcher.Path, desktop, StringComparison.OrdinalIgnoreCase)) seen = true;
                if (seen) continue;
                try
                {
                    FileSystemWatcher watcher = new FileSystemWatcher(desktop);
                    watcher.IncludeSubdirectories = false;
                    watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName;
                    watcher.Created += OnDesktopCreated;
                    watcher.Renamed += OnDesktopRenamed;
                    watcher.EnableRaisingEvents = true;
                    desktopWatchers.Add(watcher);
                }
                catch (Exception ex) { ShowStatus("无法监视桌面新文件：" + ex.Message); }
            }
        }

        private void StopDesktopMonitoring()
        {
            newDesktopTimer.Stop();
            foreach (FileSystemWatcher watcher in desktopWatchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            desktopWatchers.Clear();
        }

        private void OnDesktopCreated(object sender, FileSystemEventArgs args)
        {
            QueueNewDesktopItem(args.FullPath);
        }

        private void OnDesktopRenamed(object sender, RenamedEventArgs args)
        {
            string from = args.OldFullPath;
            string to = args.FullPath;
            // FileSystemWatcher callbacks arrive on a background thread. All WPF and
            // layout modifications must be marshalled back to the Dispatcher.
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    bool changed = false;
                    string prefix = from.TrimEnd('\\') + "\\";
                    foreach (ShortcutEntry entry in shortcuts)
                    {
                        if (String.Equals(entry.Path, from, StringComparison.OrdinalIgnoreCase) ||
                            entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            string revised = to + entry.Path.Substring(from.Length);
                            selectedShortcuts.Remove(entry.Path);
                            icons.Remove(entry.Path);
                            entry.Path = revised;
                            changed = true;
                        }
                    }
                    if (changed) { SaveSettings(); BuildTiles(); }
                }));
            }
            catch (InvalidOperationException) { }
            QueueNewDesktopItem(to);
        }

        private void QueueNewDesktopItem(string path)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (DateTime.UtcNow > importDesktopChangesUntil) return;
                    if (!String.IsNullOrEmpty(path) && !ContainsShortcut(path))
                    {
                        newDesktopPaths.Add(path);
                        newDesktopTimer.Stop();
                        newDesktopTimer.Start();
                    }
                }));
            }
            catch (InvalidOperationException) { }
        }

        private static bool GoodNewDesktopItem(string path)
        {
            try
            {
                if (!File.Exists(path) && !Directory.Exists(path)) return false;
                string filename = Path.GetFileName(path);
                if (filename.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                    filename.StartsWith("~$", StringComparison.Ordinal) ||
                    filename.StartsWith(".", StringComparison.Ordinal) ||
                    filename.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    filename.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return false;
                FileAttributes attrs = File.GetAttributes(path);
                return (attrs & (FileAttributes.Hidden | FileAttributes.System)) == 0;
            }
            catch { return false; }
        }

        private void OnNewDesktopTimer(object sender, EventArgs e)
        {
            newDesktopTimer.Stop();
            if (DateTime.UtcNow > importDesktopChangesUntil) { newDesktopPaths.Clear(); return; }
            List<string> candidates = new List<string>();
            foreach (string path in newDesktopPaths)
                if (GoodNewDesktopItem(path) && !ContainsShortcut(path)) candidates.Add(path);
            newDesktopPaths.Clear();
            // Stable alphabetical ordering for multiple extracted top-level files.
            candidates.Sort(StringComparer.CurrentCultureIgnoreCase);
            if (candidates.Count == 0) return;
            int col = 0, row = 0;
            foreach (ShortcutEntry item in shortcuts)
            {
                if (!String.Equals(item.Path, importNearPath, StringComparison.OrdinalIgnoreCase)) continue;
                col = item.Column;
                row = item.Row;
                break;
            }
            int count = AddItems(candidates.ToArray(), col, row);
            if (count > 0) ShowStatus("桌面出现了 " + count + " 个新项目，已自动加入抽屉。");
        }

        private void OpenFileLocation(string path)
        {
            if (IsRecycleBin(path)) return;
            try
            {
                ProcessStartInfo start = new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"");
                start.UseShellExecute = true;
                Process.Start(start);
            }
            catch (Exception ex) { ShowStatus("打开文件位置失败：" + ex.Message); }
        }

        private void RenameRealFile(string oldPath)
        {
            if (IsRecycleBin(oldPath) || !IsSupportedItem(oldPath)) return;
            Window dialog = new Window();
            dialog.Title = "重命名真实文件（桌面文件也会更名）";
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.Owner = this;
            dialog.Width = 390;
            dialog.Height = 165;
            dialog.ResizeMode = ResizeMode.NoResize;
            dialog.ShowInTaskbar = false;
            StackPanel panel = new StackPanel();
            panel.Margin = new Thickness(15);
            TextBlock label = new TextBlock();
            label.Text = "新名称（不要输入路径）：";
            panel.Children.Add(label);
            TextBox textbox = new TextBox();
            textbox.Margin = new Thickness(0, 8, 0, 12);
            textbox.Text = Path.GetFileName(oldPath);
            panel.Children.Add(textbox);
            StackPanel actions = new StackPanel();
            actions.Orientation = Orientation.Horizontal;
            actions.HorizontalAlignment = HorizontalAlignment.Right;
            Button confirm = new Button();
            confirm.Content = "确定";
            confirm.MinWidth = 75;
            confirm.Margin = new Thickness(0, 0, 8, 0);
            Button cancel = new Button();
            cancel.Content = "取消";
            cancel.MinWidth = 75;
            actions.Children.Add(confirm);
            actions.Children.Add(cancel);
            panel.Children.Add(actions);
            dialog.Content = panel;
            string destination = null;
            confirm.Click += delegate
            {
                string name = textbox.Text.Trim();
                if (String.IsNullOrWhiteSpace(name) || name == "." || name == ".." ||
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    name.EndsWith(".", StringComparison.Ordinal) ||
                    name.EndsWith(" ", StringComparison.Ordinal))
                {
                    MessageBox.Show(dialog, "请输入合法的文件名。", "Desktop Drawer");
                    return;
                }
                destination = Path.Combine(Path.GetDirectoryName(oldPath), name);
                dialog.DialogResult = true;
            };
            cancel.Click += delegate { dialog.DialogResult = false; };
            dialog.Loaded += delegate { textbox.SelectAll(); textbox.Focus(); };
            if (dialog.ShowDialog() != true || destination == null ||
                String.Equals(oldPath, destination, StringComparison.Ordinal)) return;
            try
            {
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    ShowStatus("目标名称已经存在，没有覆盖任何文件。");
                    return;
                }
                bool isFolder = Directory.Exists(oldPath);
                if (isFolder) Directory.Move(oldPath, destination);
                else File.Move(oldPath, destination);
                string prefix = oldPath.TrimEnd('\\') + "\\";
                foreach (ShortcutEntry entry in shortcuts)
                {
                    if (String.Equals(entry.Path, oldPath, StringComparison.OrdinalIgnoreCase)
                        || entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedShortcuts.Remove(entry.Path);
                        icons.Remove(entry.Path);
                        entry.Path = destination + entry.Path.Substring(oldPath.Length);
                    }
                }
                SaveSettings();
                BuildTiles();
                ShowStatus("实际文件已重命名，抽屉中的路径已同步。");
            }
            catch (Exception ex)
            {
                MessageBox.Show("重命名失败：" + ex.Message, "Desktop Drawer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LaunchShortcut(string path)
        {
            if (IsDrawerSettings(path))
            {
                ShowLiveSettingsDialog();
                return;
            }
            if (!IsSupportedItem(path))
            {
                ShowStatus("原项目已不存在，可右键将其从抽屉移除。" );
                return;
            }
            try
            {
                ProcessStartInfo start = IsRecycleBin(path)
                    ? new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
                    : new ProcessStartInfo(path);
                start.UseShellExecute = true;
                Process.Start(start);
                SetExpanded(false);
            }
            catch (Exception ex)
            {
                ShowStatus("启动失败：" + ex.Message);
            }
        }

        private void RemoveShortcut(string path)
        {
            if (IsDrawerSettings(path))
            {
                ShowStatus("抽屉设置图标不可移除，可拖到任何空格。");
                return;
            }
            for (int i = 0; i < shortcuts.Count; i++)
            {
                if (!String.Equals(shortcuts[i].Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                shortcuts.RemoveAt(i);
                selectedShortcuts.Remove(path);
                icons.Remove(path);
                BuildTiles();
                if (SaveSettings())
                    ShowStatus("已从抽屉移除，桌面上的原快捷方式未删除。");
                return;
            }
        }

        // 从 Explorer 的 CF_HDROP/FileDrop 中一次取出所有选中路径，不再只解析一个图标。
        // Shell IDList Array (CIDA) can carry virtual desktop objects without a FileDrop path.
        // Recognize the Recycle Bin's documented CLSID in a child PIDL; never treat the
        // entire desktop or an unknown virtual item as a real file.
        private static bool HasRecycleBinShellItem(IDataObject data)
        {
            try
            {
                if (data == null || !data.GetDataPresent("Shell IDList Array", true)) return false;
                object payload = data.GetData("Shell IDList Array", true);
                Stream stream = payload as Stream;
                if (stream == null) return false;
                long previousPosition = stream.CanSeek ? stream.Position : 0;
                byte[] bytes;
                try
                {
                    if (stream.CanSeek) stream.Position = 0;
                    using (MemoryStream copied = new MemoryStream())
                    {
                        byte[] buffer = new byte[4096];
                        int length;
                        while ((length = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            copied.Write(buffer, 0, length);
                            if (copied.Length > 1048576) return false;
                        }
                        bytes = copied.ToArray();
                    }
                }
                finally { if (stream.CanSeek) stream.Position = previousPosition; }
                if (bytes.Length < 12) return false;
                int items = BitConverter.ToInt32(bytes, 0);
                if (items < 1 || items > 1024 || (long)(items + 2) * 4 > bytes.Length) return false;
                byte[] guid = new Guid("645FF040-5081-101B-9F08-00AA002F954E").ToByteArray();
                for (int item = 1; item <= items; item++)
                {
                    int offset = BitConverter.ToInt32(bytes, 4 + item * 4);
                    if (offset < 0 || offset + 20 > bytes.Length) continue;
                    // ITEMID SHITEMID (14 00 1F 78 + CLSID bytes), as in the Shell desktop PIDL.
                    for (int i = offset; i + guid.Length <= Math.Min(bytes.Length, offset + 128); i++)
                    {
                        bool matching = true;
                        for (int n = 0; n < guid.Length; n++)
                            if (bytes[i + n] != guid[n]) { matching = false; break; }
                        if (matching) return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        private string[] GetDroppedItems(IDataObject data)
        {
            if (data == null) return new string[0];
            string[] paths = null;
            try
            {
                if (data.GetDataPresent(DataFormats.FileDrop, true))
                    paths = data.GetData(DataFormats.FileDrop, true) as string[];
            }
            catch (COMException) { }
            catch (ExternalException) { }
            if (paths == null) paths = new string[0];
            List<string> valid = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in paths)
            {
                if (!IsSupportedItem(raw)) continue;
                string normalized;
                try { normalized = Path.GetFullPath(raw); }
                catch (Exception) { continue; }
                if (seen.Add(normalized)) valid.Add(normalized);
            }
            if (HasRecycleBinShellItem(data) && seen.Add(RecycleBinKey)) valid.Add(RecycleBinKey);
            return valid.ToArray();
        }

        // 对 Windows Shell 只返回 Copy/Link，绝不声称 Move，防止资源管理器删除原文件。
        private static DragDropEffects SafeExternalEffect(DragDropEffects allowed)
        {
            if ((allowed & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
            if ((allowed & DragDropEffects.Link) != 0) return DragDropEffects.Link;
            return DragDropEffects.None;
        }

        // Read desktop positions to preserve the selection's shape and its internal gaps.
        // Entire group is placed atomically at the nearest fitting origin; no existing
        // drawer icon is moved or overwritten. Fail gracefully when Explorer is inaccessible.
        private bool TryAddInDesktopPattern(string[] paths, int dropColumn, int dropRow, out int added)
        {
            added = 0;
            if (paths.Length < 2) return false;
            Dictionary<string, Point> positions;
            int spacingX, spacingY;
            if (!DesktopLayoutReader.TryRead(paths, out positions, out spacingX, out spacingY)) return false;
            List<string> newPaths = new List<string>();
            foreach (string path in paths) if (!ContainsShortcut(path) && IsSupportedItem(path)) newPaths.Add(path);
            if (newPaths.Count == 0) return true;
            double minX = Double.MaxValue, minY = Double.MaxValue;
            foreach (string path in paths)
            {
                Point pt = positions[path];
                minX = Math.Min(minX, pt.X);
                minY = Math.Min(minY, pt.Y);
            }
            Dictionary<string, Point> relative = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> offsets = new HashSet<string>();
            int maxDC = 0, maxDR = 0;
            foreach (string path in newPaths)
            {
                Point pt = positions[path];
                int dc = (int)Math.Round((pt.X - minX) / spacingX, MidpointRounding.AwayFromZero);
                int dr = (int)Math.Round((pt.Y - minY) / spacingY, MidpointRounding.AwayFromZero);
                if (!offsets.Add(dc + ":" + dr)) return false;
                relative[path] = new Point(dc, dr);
                maxDC = Math.Max(maxDC, dc);
                maxDR = Math.Max(maxDR, dr);
            }
            if (maxDR >= Rows || maxDC >= MaxColumns) return false;
            // Add columns only if needed; never shrink away the user's existing blank cells.
            for (int available = Math.Max(columns, maxDC + 1); available <= MaxColumns; available++)
            {
                int bestC = -1, bestR = -1, bestDistance = Int32.MaxValue;
                for (int c = 0; c + maxDC < available; c++)
                    for (int r = 0; r + maxDR < Rows; r++)
                    {
                        bool free = true;
                        foreach (string path in newPaths)
                        {
                            Point delta = relative[path];
                            if (EntryAt(c + (int)delta.X, r + (int)delta.Y) != null)
                            { free = false; break; }
                        }
                        if (!free) continue;
                        int distance = (c - dropColumn) * (c - dropColumn)
                            + (r - dropRow) * (r - dropRow);
                        if (distance < bestDistance)
                        { bestDistance = distance; bestC = c; bestR = r; }
                    }
                if (bestC < 0) continue;
                columns = available;
                foreach (string path in newPaths)
                {
                    Point delta = relative[path];
                    shortcuts.Add(new ShortcutEntry(path, bestC + (int)delta.X, bestR + (int)delta.Y));
                    added++;
                }
                return true;
            }
            return false;
        }

        // 统一处理单个/批量导入，落点附近找空格，不挤占已有图标和留白。
        private int AddItems(string[] paths, int preferredColumn, int preferredRow)
        {
            int added = 0, duplicates = 0, full = 0;
            int spatialAdded;
            if (TryAddInDesktopPattern(paths, preferredColumn, preferredRow, out spatialAdded))
            {
                if (spatialAdded > 0)
                {
                    BuildTiles();
                    if (SaveSettings()) ShowStatus("已按桌面相对位置添加 " + spatialAdded + " 项（保留中间空格）。");
                    SetExpanded(true);
                }
                else ShowStatus("这些图标已经在抽屉里了，没有重复导入。");
                return spatialAdded;
            }
            foreach (string path in paths)
            {
                if (!IsSupportedItem(path)) continue;
                if (ContainsShortcut(path)) { duplicates++; continue; }
                int c, r;
                while (!TryFindFreeCell(preferredColumn, preferredRow, out c, out r) && columns < MaxColumns)
                    columns++;
                if (!TryFindFreeCell(preferredColumn, preferredRow, out c, out r)) { full++; continue; }
                shortcuts.Add(new ShortcutEntry(path, c, r));
                added++;
            }
            if (added > 0)
            {
                BuildTiles();
                bool saved = SaveSettings();
                SetExpanded(true);
                if (saved)
                {
                    if (full > 0) ShowStatus("已添加 " + added + " 项，另 " + full + " 项因空间不足未添加。");
                    else ShowStatus(paths.Length > 1
                        ? "已添加 " + added + " 项；未能读取桌面图标坐标，已就近排列，可框选整组调整。"
                        : "已添加 1 项，可以拖动到任意空格。");
                }
            }
            else if (duplicates > 0) ShowStatus("所选项目已在抽屉中，没有重复导入。");
            else if (full > 0) ShowStatus("格子已满，请右键增加一列。");
            return added;
        }

        // Win32 资源管理器拖放异常时的备用入口；可一次选择多份文件/快捷方式。
        private void ImportMultipleFiles()
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Title = "批量添加桌面项目到抽屉（仅创建引用）";
            dialog.Multiselect = true;
            dialog.Filter = "所有文件 (*.*)|*.*";
            dialog.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            bool? result = dialog.ShowDialog(this);
            if (result == true) AddItems(dialog.FileNames, 0, 0);
        }

        private void OnExternalDragOver(object sender, DragEventArgs e)
        {
            // 框选是普通鼠标事件；Shell 拖放是 OLE DragEvent，两者不共享 CaptureMouse。
            // 若上次框选意外遗留了 Capture，先释放，再交给原生拖放事件处理。
            if (marqueePending) EndMarquee();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            int c, r;
            if (!IsDesktopExposedForCursor()) { HideDropTarget(); return; }
            if (!TryGetDropCell(e, out c, out r)) { HideDropTarget(); return; }
            int highlightC = c, highlightR = r;
            if (internalDraggingPath != null && e.Data != null && e.Data.GetDataPresent(InternalDragFormat))
            {
                string dragged = e.Data.GetData(InternalDragFormat) as string;
                if (String.Equals(dragged, internalDraggingPath, StringComparison.OrdinalIgnoreCase)
                    && (e.AllowedEffects & DragDropEffects.Copy) != 0)
                    e.Effects = DragDropEffects.Copy;
            }
            else
            {
                string[] paths = GetDroppedItems(e.Data);
                if (paths.Length != 0)
                {
                    // 所有拖入的桌面项目一起接受：占用位置向最近空格扩散。
                    if (!TryFindFreeCell(c, r, out highlightC, out highlightR))
                    { highlightC = c; highlightR = r; }
                    e.Effects = SafeExternalEffect(e.AllowedEffects);
                    if (e.Effects == DragDropEffects.None && statusUntil < DateTime.UtcNow)
                        ShowStatus("此拖动仅允许“移动”。请按住 Ctrl 再拖进抽屉（复制/引用，不移动原文件）。");
                }
            }
            if (e.Effects == DragDropEffects.None) { HideDropTarget(); return; }
            ShowDropTarget(highlightC, highlightR);
            holdUntil = DateTime.UtcNow.AddMilliseconds(1400);
            lastInside = DateTime.UtcNow;
            if (!expanded) SetExpanded(true);
        }

        private void OnExternalDrop(object sender, DragEventArgs e)
        {
            if (marqueePending) EndMarquee();
            HideDropTarget();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            holdUntil = DateTime.UtcNow.AddMilliseconds(1800);
            int c, r;
            if (!IsDesktopExposedForCursor()) return;
            if (!TryGetDropCell(e, out c, out r)) return;

            if (internalDraggingPath != null && e.Data != null && e.Data.GetDataPresent(InternalDragFormat))
            {
                string dragged = e.Data.GetData(InternalDragFormat) as string;
                if (String.Equals(dragged, internalDraggingPath, StringComparison.OrdinalIgnoreCase)
                    && (e.AllowedEffects & DragDropEffects.Copy) != 0)
                {
                    if (internalDraggingPaths.Count > 1) MoveShortcutGroup(dragged, c, r);
                    else MoveShortcut(dragged, c, r);
                    e.Effects = DragDropEffects.Copy;
                }
                return;
            }
            // 不能对仅允许 Move 的 Shell 拖放返回成功，否则可能移动/删除桌面源文件。
            DragDropEffects safeEffect = SafeExternalEffect(e.AllowedEffects);
            if (safeEffect == DragDropEffects.None)
            {
                ShowStatus("拖动只允许移动。请按 Ctrl 重试，抽屉只保存引用、不移动源文件。");
                return;
            }
            string[] paths = GetDroppedItems(e.Data);
            if (paths.Length == 0)
            {
                ShowStatus("未收到文件路径或可识别的回收站图标。可在空白处右键添加回收站。");
                return;
            }
            int added = AddItems(paths, c, r);
            if (added > 0) e.Effects = safeEffect;
        }

        private void ChangeColumns(int delta)
        {
            // 已占用最右一列时不允许缩掉这一列，否则图标会意外消失。
            int minimumForItems = MinColumns;
            foreach (ShortcutEntry entry in shortcuts)
                minimumForItems = Math.Max(minimumForItems, entry.Column + 1);
            int value = Math.Max(minimumForItems, Math.Min(MaxColumns, columns + delta));
            if (value == columns) return;
            columns = value;
            BuildTiles();
            SaveSettings();
            SetExpanded(expanded);
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source != null && source.CompositionTarget != null)
                scale = source.CompositionTarget.TransformToDevice.M11;
            RefreshDisplayMetrics(true);

            // 不在 Alt+Tab 里出现
            int exStyle = Native.GetWindowLong(hwnd, -20);
            Native.SetWindowLong(hwnd, -20, exStyle | 0x00000080);

            if (transparencyOn) Native.EnableAcrylic(hwnd, TintAlpha, TintR, TintG, TintB);
            // 已获得正确 DPI，刷新按实际像素密度提取的 Shell 图标。
            icons.Clear();
            BuildTiles();
            UpdateRegion(new Size(Width, Height));
        }

        // Acrylic 是 HWND 级合成效果，仍需要用窗口 Region 剪掉四角。
        // 没有 Acrylic 时只用 WPF 透明圆角，避开 GDI 整数像素裁剪产生的台阶。
        private void UpdateRegion(Size size)
        {
            if (hwnd == IntPtr.Zero || !transparencyOn) return;
            int w = (int)Math.Round(size.Width * scale);
            int h = (int)Math.Round(size.Height * scale);
            if (w < 4 || h < 4) return;
            if (w == lastRegionWidth && h == lastRegionHeight) return;
            int d = (int)Math.Round(RadiusDip * 2 * scale);
            // GDI Region 右/下是排他边界，不要再额外 +1px，
            // 否则会比 WPF 的窗口边界伸出一圈，留下断续细线。
            IntPtr region = Native.CreateRoundRectRgn(0, 0, w, h, d, d);
            if (region == IntPtr.Zero) return;
            // 设置窗口预览阶段无需强制同步重画整个 Acrylic HWND；让 WPF/DWM
            // 合并同一显示帧的绘制。窗口正常使用时仍按原方式立即重画。
            if (Native.SetWindowRgn(hwnd, region, !liveSettingsOpen) == 0)
            {
                Native.DeleteObject(region);
                return;
            }
            // SetWindowRgn 成功后窗口系统接管 region，不能在此删除它。
            lastRegionWidth = w;
            lastRegionHeight = h;
        }

        private bool IsDesktopExposedForCursor()
        {
            Native.POINT p;
            if (!Native.GetCursorPos(out p)) return false;
            return Native.IsDesktopExposedAtLeftEdge(hwnd, p.Y,
                Native.GetSystemMetrics(0), Native.GetSystemMetrics(1));
        }

        // Windows can switch between 16:9 and 4:3 without restarting this process.
        // Using live GetSystemMetrics avoids stale SystemParameters.PrimaryScreenHeight.
        private void RefreshDisplayMetrics(bool force)
        {
            if (hwnd == IntPtr.Zero || internalDraggingPath != null || marqueePending || liveSettingsOpen) return;
            int newW = Native.GetSystemMetrics(0);
            int newH = Native.GetSystemMetrics(1);
            if (newW <= 0 || newH <= 0) return;
            bool screenSizeUnchanged = displayWidthPx == newW && displayHeightPx == newH;
            if (!force && screenSizeUnchanged && DateTime.UtcNow < nextDpiProbe) return;
            nextDpiProbe = DateTime.UtcNow.AddSeconds(1);
            double newScale = scale;
            HwndSource source = HwndSource.FromHwnd(hwnd);
            if (source != null && source.CompositionTarget != null)
            {
                double candidate = source.CompositionTarget.TransformToDevice.M11;
                if (candidate > 0.1 && candidate < 10) newScale = candidate;
            }
            if (!force && displayWidthPx == newW && displayHeightPx == newH &&
                Math.Abs(scale - newScale) < 0.001) return;

            bool dpiChanged = Math.Abs(scale - newScale) >= 0.001;
            scale = newScale;
            displayWidthPx = newW;
            displayHeightPx = newH;
            displayWidthDip = newW / scale;
            displayHeightDip = newH / scale;
            if (dpiChanged) icons.Clear();

            // Snap closed once, then rebuild to the NEW physical height and row spacing.
            // Never NormalizeLoadedSlots / SaveSettings here: a temporary game resolution
            // must not overwrite the original high-resolution arrangement.
            StopRendering();
            expanded = false;
            lastRegionWidth = -1;
            lastRegionHeight = -1;
            Height = displayHeightDip;
            BuildTiles();
            targetWidth = TargetWidth;
            targetLeft = CollapsedLeft;
            Width = targetWidth;
            Left = targetLeft;
            UpdateRegion(new Size(Width, Height));
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                RefreshDisplayMetrics(false);
                if (hint.Visibility == Visibility.Visible && DateTime.UtcNow > statusUntil)
                {
                    hint.Visibility = Visibility.Collapsed;
                    hint.Text = "";
                }
                Native.POINT p;
                if (!Native.GetCursorPos(out p)) return;
                int screenW = Native.GetSystemMetrics(0);
                int screenH = Native.GetSystemMetrics(1);
                bool withinY = p.Y >= 0 && p.Y < screenH;
                bool atEdge = withinY && p.X >= 0 && p.X <= TriggerPx;
                double visibleRight = (expanded ? ActiveExpandedWidth - RadiusDip : TargetWidth - RadiusDip) * scale;
                bool inside = withinY && p.X >= 0 && p.X <= visibleRight;

                // 只排除真正全屏覆盖主屏幕的前台软件；普通窗口不会阻止触发。
                // 浏览器最大化、F11 视频、无边框或独占全屏游戏则被阻止。
                if (!expanded && !atEdge) return;
                bool desktopExposed = Native.IsDesktopExposedAtLeftEdge(hwnd, p.Y, screenW, screenH);
                if (!desktopExposed)
                {
                    // 切换到真正全屏窗口时立即收回并让出置顶层级。
                    // 普通窗口即使处于前台，也不得触发这个分支。
                    if (expanded) SetExpanded(false);
                    if (Topmost) Topmost = false;
                    return;
                }
                if (!Topmost) Topmost = true;

                if (!expanded)
                {
                    // 仍允许从可见桌面按住左键拖动多个快捷方式到屏幕边缘。
                    if (atEdge) SetExpanded(true);
                }
                else
                {
                    DateTime now = DateTime.UtcNow;
                    if (liveSettingsOpen) lastInside = now;
                    if (inside || atEdge) lastInside = now;
                    else if (internalDraggingPath == null && !marqueePending && !menuOpen && now > holdUntil
                        && (now - lastInside).TotalMilliseconds > HideDelayMs)
                        SetExpanded(false);
                }
            }
            catch (Exception ex) { LogError(ex); }
        }

        private void SetExpanded(bool value)
        {
            expanded = value;
            lastInside = DateTime.UtcNow;
            targetWidth = value ? ActiveExpandedWidth : TargetWidth;
            targetLeft = value ? ExpandedLeft : CollapsedLeft;
            StartRendering();
        }

        // 物理像素取整后，视觉上稳定停在屏幕像素网格，不会出现边缘抖动。
        private int ToNativePx(double dip)
        {
            return (int)Math.Round(dip * scale);
        }

        private void StartRendering()
        {
            if (hwnd == IntPtr.Zero) return;
            if (rendering)
            {
                // 连续拖动横向滑块时，只更新弹簧目标；绝不反复读取 HWND
                // 和清空当前速度。实际分辨率变化会先 StopRendering() 再同步。
                animationToLeftPx = ToNativePx(targetLeft);
                animationToWidthPx = Math.Max(1, ToNativePx(targetWidth));
                return;
            }

            Native.RECT rect;
            if (!Native.GetWindowRect(hwnd, out rect))
            {
                StopRendering();
                Width = targetWidth;
                Left = targetLeft;
                return;
            }

            int actualWidth = Math.Max(1, rect.Right - rect.Left);
            int newTargetLeft = ToNativePx(targetLeft);
            int newTargetWidth = Math.Max(1, ToNativePx(targetWidth));

            if (!rendering)
            {
                // 首次启动动画，使用当前真实 HWND 像素坐标，零初速度。
                animationLeftPx = rect.Left;
                animationWidthPx = actualWidth;
                animationLeftVelocityPx = 0;
                animationWidthVelocityPx = 0;
            }
            else if (Math.Abs(animationLeftPx - rect.Left) > 3 ||
                     Math.Abs(animationWidthPx - actualWidth) > 3)
            {
                // 外部 DPI/系统窗口调整优先，以实际窗口位置重新同步。
                animationLeftPx = rect.Left;
                animationWidthPx = actualWidth;
                animationLeftVelocityPx = 0;
                animationWidthVelocityPx = 0;
            }
            // 已经在动时，只换目标，不清空速度。反向不会突然从静止重播。
            animationToLeftPx = newTargetLeft;
            animationToWidthPx = newTargetWidth;
            animationYpx = rect.Top;
            animationHeightPx = Math.Max(1, rect.Bottom - rect.Top);
            lastAppliedLeftPx = rect.Left;
            lastAppliedWidthPx = actualWidth;

            if (IsMotionSettled(animationLeftPx, animationLeftVelocityPx, animationToLeftPx) &&
                IsMotionSettled(animationWidthPx, animationWidthVelocityPx, animationToWidthPx))
            {
                StopRendering();
                return;
            }

            if (!rendering)
            {
                animationLastTicks = Stopwatch.GetTimestamp();
                rendering = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private void StopRendering()
        {
            if (!rendering) return;
            rendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private static bool IsMotionSettled(double position, double velocity, double target)
        {
            return Math.Abs(position - target) <= SettleDistancePx &&
                   Math.Abs(velocity) <= SettleSpeedPxPerSecond;
        }

        // 临界阻尼的解析解：与帧率无关，位置和速度都连续；不会像普通弹簧那样越过目标反弹。
        // 当目标在动画中途变化时，复用上一帧速度，获得自然的过渡手感。
        private static void AdvanceSpring(ref double position, ref double velocity,
            double target, double response, double seconds)
        {
            double offset = position - target;
            double adjustment = velocity + response * offset;
            double decay = Math.Exp(-response * seconds);
            position = target + (offset + adjustment * seconds) * decay;
            velocity = (velocity - response * adjustment * seconds) * decay;
        }

        // 跟随 CompositionTarget.Rendering；使用 Stopwatch 真实时间差，
        // 但限制单帧推进量，以免窗口长时间阻塞后突然飞到另一端。
        private void OnRendering(object sender, EventArgs e)
        {
            try
            {
                long nowTicks = Stopwatch.GetTimestamp();
                double seconds = (nowTicks - animationLastTicks) / (double)Stopwatch.Frequency;
                animationLastTicks = nowTicks;
                if (seconds <= 0) return;
                seconds = Math.Min(0.05, seconds);

                AdvanceSpring(ref animationLeftPx, ref animationLeftVelocityPx,
                    animationToLeftPx, SlideResponse, seconds);
                AdvanceSpring(ref animationWidthPx, ref animationWidthVelocityPx,
                    animationToWidthPx, ResizeResponse, seconds);

                bool leftDone = IsMotionSettled(animationLeftPx, animationLeftVelocityPx,
                    animationToLeftPx);
                bool widthDone = IsMotionSettled(animationWidthPx, animationWidthVelocityPx,
                    animationToWidthPx);
                if (leftDone)
                {
                    animationLeftPx = animationToLeftPx;
                    animationLeftVelocityPx = 0;
                }
                if (widthDone)
                {
                    animationWidthPx = animationToWidthPx;
                    animationWidthVelocityPx = 0;
                }

                int x = (int)Math.Round(animationLeftPx);
                int w = Math.Max(1, (int)Math.Round(animationWidthPx));
                if (x != lastAppliedLeftPx || w != lastAppliedWidthPx)
                {
                    const uint SWP_NOZORDER = 0x0004;
                    const uint SWP_NOACTIVATE = 0x0010;
                    const uint SWP_NOOWNERZORDER = 0x0200;
                    const uint SWP_NOSIZE = 0x0001;
                    uint flags = SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
                    if (w == lastAppliedWidthPx) flags |= SWP_NOSIZE;
                    if (!Native.SetWindowPos(hwnd, IntPtr.Zero, x, animationYpx,
                        w, animationHeightPx, flags))
                    {
                        StopRendering();
                        Width = targetWidth;
                        Left = targetLeft;
                        return;
                    }
                    lastAppliedLeftPx = x;
                    lastAppliedWidthPx = w;
                }

                if (leftDone && widthDone) StopRendering();
            }
            catch (Exception ex) { StopRendering(); LogError(ex); }
        }

        private void LogError(Exception ex)
        {
            if (errorLogged) return;
            errorLogged = true;
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "drawer-error.txt"), ex.ToString()); }
            catch { }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "Local\\DrawerStage2.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("抽屉已经在运行了。把鼠标移到屏幕最左边就会弹出来。", "Desktop Drawer");
                    return 0;
                }
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
                {
                    try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "drawer-error.txt"), e.Exception.ToString()); }
                    catch { }
                    e.Handled = true;
                };
                app.Run(new DrawerWindow());
            }
            return 0;
        }
    }
}
