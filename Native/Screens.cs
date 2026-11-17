using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace TileBoard.Native
{
    /// <summary>
    /// 多屏工作区换算。
    ///
    /// 为什么不用 SystemParameters.WorkArea：它永远返回主屏的工作区，
    /// 面板想出在副屏就必须自己问 Win32「这块屏的工作区在哪」。
    /// PerMonitorV2 下每块屏 DPI 各自独立，WPF 的 Left/Top/Width/Height 是 DIP，
    /// 所以这里同时给出物理像素矩形（抓屏用）和按目标屏 DPI 换算过的 DIP 矩形（摆窗口用）。
    /// </summary>
    internal static class Screens
    {
        /// <summary>一块屏的工作区：DIP 给布局用，物理像素给抓屏用。</summary>
        internal sealed class Info
        {
            public IntPtr Monitor;
            public Rect Dip;       // WPF 坐标（DIP），按这块屏自己的 DPI 换算
            public Rect Physical;  // 物理像素，GDI 抓屏直接用
            public uint Dpi;

            // 整屏矩形（含任务栏占的那条）。用它判断「工作区是不是等于整屏」——
            // 全屏应用里任务栏被挤走时，工作区会涨到整屏高度，板面照那个尺寸做
            // 就会把整块屏铺满，系统于是继续把任务栏按在全屏模式里不还给你。
            public Rect MonitorDip;
            public Rect MonitorPhysical;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;

        /// <summary>鼠标光标所在屏的工作区。唤出面板时用它决定「出在哪块屏」。</summary>
        internal static bool TryNearCursor(out Info info)
        {
            info = null;

            if (!GetCursorPos(out POINT p)) return false;
            return TryOfPoint(p.X, p.Y, out info);
        }

        /// <summary>窗口所在屏的工作区。</summary>
        internal static bool TryOfWindow(IntPtr hwnd, out Info info)
        {
            info = null;

            if (hwnd == IntPtr.Zero) return false;
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;
            return TryOfMonitor(monitor, out info);
        }

        /// <summary>物理像素坐标所在屏的工作区。</summary>
        private static bool TryOfPoint(int x, int y, out Info info)
        {
            info = null;

            var pt = new POINT { X = x, Y = y };
            IntPtr monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;
            return TryOfMonitor(monitor, out info);
        }

        /// <summary>
        /// 取一块屏的工作区，物理 → DIP 换算用这块屏自己的 DPI。
        /// 拿不到 DPI（理论上只有 pre-1607 系统才会）就按 96 处理，等于 100% 缩放。
        /// </summary>
        private static bool TryOfMonitor(IntPtr monitor, out Info info)
        {
            info = null;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref mi)) return false;

            uint dpi = 96;
            try
            {
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
                    dpi = dx;
            }
            catch { }   // shcore 缺失等极端情况：按 96 兜底

            RECT w = mi.rcWork;
            RECT m = mi.rcMonitor;

            info = new Info
            {
                Monitor = monitor,
                Dpi = dpi,
                Physical = new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top),
                Dip = new Rect(
                    w.Left * 96.0 / dpi,
                    w.Top * 96.0 / dpi,
                    (w.Right - w.Left) * 96.0 / dpi,
                    (w.Bottom - w.Top) * 96.0 / dpi),
                MonitorPhysical = new Rect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top),
                MonitorDip = new Rect(
                    m.Left * 96.0 / dpi,
                    m.Top * 96.0 / dpi,
                    (m.Right - m.Left) * 96.0 / dpi,
                    (m.Bottom - m.Top) * 96.0 / dpi)
            };
            return true;
        }
    }
}
