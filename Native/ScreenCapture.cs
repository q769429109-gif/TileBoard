using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace TileBoard.Native
{
    /// <summary>
    /// 把屏幕某块矩形抓成位图。
    ///
    /// 为什么需要它：DWM 的亚克力（ACCENT_ENABLE_ACRYLICBLURBEHIND）模糊半径是写死的，
    /// 系统没给任何旋钮 —— 想调「毛玻璃强度」只能自己截一张背后的画面，
    /// 按需要的程度去模糊，再把结果垫在窗口最底下。
    /// </summary>
    internal static class ScreenCapture
    {
        private const int SRCCOPY = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
                                          IntPtr src, int srcX, int srcY, int rop);

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>
        /// 抓窗口所在显示器的工作区。
        ///
        /// 为什么抓整块工作区而不是窗口矩形：窗口一改尺寸（切全屏、拖边框），
        /// 按窗口矩形抓的底图就装不下新的区域，只能作废 —— 表现就是「切一次全屏毛玻璃就没了」。
        /// 工作区是面板的最大可能范围，面板永远是它的子矩形，
        /// 所以整段显示期间只需要抓一次，之后无论怎么缩放都只是挪一下画面。
        /// </summary>
        internal static BitmapSource GrabWorkArea(IntPtr hwnd)
        {
            IntPtr monitor = hwnd != IntPtr.Zero
                ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
                : IntPtr.Zero;

            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    RECT w = info.rcWork;
                    return Grab(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top);
                }
            }

            // 拿不到显示器信息就退回窗口矩形，至少还有模糊
            return GrabWindow(hwnd);
        }

        /// <summary>抓窗口所在的那块屏幕。窗口此刻必须不可见（Hide 或整层全透明），否则抓到自己。</summary>
        internal static BitmapSource GrabWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            if (!GetWindowRect(hwnd, out RECT r)) return null;

            return Grab(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        /// <summary>抓屏幕上一块矩形，返回冻结好的位图；失败返回 null（调用方退回 DWM 亚克力）。</summary>
        internal static BitmapSource Grab(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;

            IntPtr screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            IntPtr memDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldObj = IntPtr.Zero;

            try
            {
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return null;

                hBitmap = CreateCompatibleBitmap(screenDc, width, height);
                if (hBitmap == IntPtr.Zero) return null;

                oldObj = SelectObject(memDc, hBitmap);

                // 只用 SRCCOPY：CAPTUREBLT 会把分层窗口一并抓进去，等于把自己的面板拍进来
                if (!BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY))
                    return null;

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                if (source.CanFreeze) source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
            finally
            {
                // SelectObject 一定要换回原对象，否则这张 DDB 释放不掉，唤出几百次就漏几百张
                if (memDc != IntPtr.Zero && oldObj != IntPtr.Zero)
                    SelectObject(memDc, oldObj);

                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }
}
