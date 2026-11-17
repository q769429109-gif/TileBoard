using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;

namespace TileBoard.Native
{
    /// <summary>
    /// 任务栏（Shell_TrayWnd / Shell_SecondaryTrayWnd）的**只读**查询层。
    ///
    /// 2026-11-17 大改：这个类曾经带一整套「写」操作 —— 临时关自动隐藏（ABM_SETSTATE）、
    /// 把任务栏 SetWindowPos 搬回屏内、插 Z 序、逼 shell 重画。代价是每次唤出都把
    /// UI 线程堵 7~16 秒（shell 全量重排任务栏），连带把低级键盘钩子拖到超时、
    /// Win 键连发被吞、副屏任务栏被搬到错误坐标、进程行为整个乱掉。
    ///
    /// 用户明确要求：不许改电脑的自动隐藏任务栏设置。所以「写」操作全部删干净 ——
    /// 这个类现在只回答「任务栏在哪、多高、是不是自动隐藏、是不是被 cloak」，
    /// 让板面自己调整几何，绝不去动 shell 的状态。
    /// 真正让任务栏在全屏应用里滑回来的手段只有一个，且它不改任何设置：
    /// ITaskbarList2::MarkFullscreenWindow(hwnd, FALSE) —— 见 TaskbarList.cs。
    /// </summary>
    internal static class Taskbar
    {
        private const int ABM_GETSTATE = 0x00000004;

        private const int ABS_AUTOHIDE = 0x1;

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

        private const int DWMWA_CLOAKED = 14;
        private const uint GW_HWNDPREV = 3;
        private const uint GW_OWNER = 4;

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr,
                                                        out int value, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private static APPBARDATA NewBarData() => new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>()
        };

        /// <summary>
        /// 任务栏当前是不是「自动隐藏」。只读查询 —— 调用方据此决定板面要不要
        /// 在底部留一条空位，绝不允许反过来去改它。
        /// </summary>
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

        /// <summary>这个任务栏属于哪块屏。</summary>
        internal static IntPtr MonitorOf(IntPtr hwnd)
        {
            try { return hwnd == IntPtr.Zero ? IntPtr.Zero : MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST); }
            catch { return IntPtr.Zero; }
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

        /// <summary>Z 序前驱（盖在它上面的那一个窗口）的类名，诊断用。</summary>
        internal static string CoveringWindowClass(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "-";
            try
            {
                IntPtr prev = GetWindow(hwnd, GW_HWNDPREV);
                if (prev == IntPtr.Zero) return "(顶层)";

                var sb = new System.Text.StringBuilder(64);
                GetClassName(prev, sb, sb.Capacity);

                IntPtr owner = GetWindow(prev, GW_OWNER);
                var ob = new System.Text.StringBuilder(64);
                if (owner != IntPtr.Zero) GetClassName(owner, ob, ob.Capacity);

                return sb.ToString() + (ob.Length > 0 ? " owner=" + ob : "") +
                       " vis=" + (IsWindowVisible(prev) ? "1" : "0");
            }
            catch { return "?"; }
        }
    }
}
