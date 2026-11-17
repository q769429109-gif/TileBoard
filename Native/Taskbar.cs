using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace TileBoard.Native
{
    /// <summary>
    /// 任务栏（Shell_TrayWnd / Shell_SecondaryTrayWnd）的定位、置顶与自动隐藏控制。
    ///
    /// 为什么需要这一层：板面是 Topmost 分层窗口，唤出瞬间它会拿到焦点。
    /// 任务栏虽然也是置顶层，但同一层里后激活的在前 —— 于是板面一出来，
    /// 任务栏就被压到后面去（自动隐藏的任务栏更是干脆不弹，因为鼠标移到底边
    /// 命中的是板面而不是任务栏的触发区）。要让「唤出磁贴板时任务栏一起出来」，
    /// 只能用 Win32 把任务栏重新提到置顶层最前，并在它处于自动隐藏状态时
    /// 临时取消自动隐藏，收板面时再还原。
    /// </summary>
    internal static class Taskbar
    {
        private const int ABM_GETSTATE = 0x00000004;
        private const int ABM_SETSTATE = 0x0000000A;

        private const int ABS_AUTOHIDE = 0x1;
        private const int ABS_ALWAYSONTOP = 0x2;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const int MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public RECT rc;
            public IntPtr lParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
                                                int x, int y, int cx, int cy, uint flags);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private static APPBARDATA NewBarData() => new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>()
        };

        /// <summary>任务栏当前是不是「自动隐藏」。用户没开就是 false，后面全都不用管。</summary>
        internal static bool IsAutoHide()
        {
            try
            {
                var data = NewBarData();
                UIntPtr state = SHAppBarMessage(ABM_GETSTATE, ref data);
                return ((int)state.ToUInt64() & ABS_AUTOHIDE) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 临时改自动隐藏。enable=false → 任务栏常显（会立刻滑出来）；true → 恢复自动隐藏。
        /// 这是改系统设置，调用方必须成对使用，收板面/退出程序时还原。
        /// </summary>
        internal static bool SetAutoHide(bool enable)
        {
            try
            {
                var data = NewBarData();
                data.lParam = (IntPtr)(enable ? (ABS_AUTOHIDE | ABS_ALWAYSONTOP) : ABS_ALWAYSONTOP);
                SHAppBarMessage(ABM_SETSTATE, ref data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 某块屏上的任务栏窗口。主屏叫 Shell_TrayWnd，副屏是若干个 Shell_SecondaryTrayWnd。
        /// monitor 传 IntPtr.Zero 时返回全部。
        /// </summary>
        internal static List<IntPtr> ForMonitor(IntPtr monitor)
        {
            var list = new List<IntPtr>();

            try
            {
                IntPtr primary = FindWindow("Shell_TrayWnd", null);
                if (primary != IntPtr.Zero) list.Add(primary);

                EnumWindows((hwnd, _) =>
                {
                    var sb = new System.Text.StringBuilder(64);
                    if (GetClassName(hwnd, sb, sb.Capacity) == 0) return true;

                    // 主任务栏已经在上面加过了，这里只收副屏的
                    if (!string.Equals(sb.ToString(), "Shell_SecondaryTrayWnd",
                                       StringComparison.Ordinal)) return true;

                    list.Add(hwnd);
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            if (monitor == IntPtr.Zero) return list;

            var matched = new List<IntPtr>();
            foreach (IntPtr hwnd in list)
            {
                try
                {
                    if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST) == monitor)
                        matched.Add(hwnd);
                }
                catch { }
            }
            return matched;
        }

        /// <summary>任务栏窗口的物理像素矩形。</summary>
        internal static bool TryGetRect(IntPtr hwnd, out Rect rect)
        {
            rect = Rect.Empty;
            try
            {
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return false;
                if (r.Right <= r.Left || r.Bottom <= r.Top) return false;
                rect = new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsVisible(IntPtr hwnd)
        {
            try { return hwnd != IntPtr.Zero && IsWindowVisible(hwnd); }
            catch { return false; }
        }

        /// <summary>把任务栏重新提到置顶层最前，但不抢焦点（板面还得是当前活动窗口）。</summary>
        internal static void BringToFront(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                             SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch { }
        }
    }
}
