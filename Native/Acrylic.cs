using System;
using System.Runtime.InteropServices;

namespace TileBoard.Native
{
    /// <summary>
    /// 给无边框窗口挂上 DWM 的亚克力模糊。
    /// Win10 的开始菜单在「透明效果」打开时就是这个观感：背后内容被模糊，再叠一层染色。
    /// 走的是未文档化的 SetWindowCompositionAttribute，TranslucentTB / FluentWPF 用的也是这条路。
    /// 失败时不影响程序运行，只是退回半透明纯色。
    /// </summary>
    internal static class Acrylic
    {
        private const int WCA_ACCENT_POLICY = 19;
        private const int ACCENT_DISABLED = 0;
        private const int ACCENT_ENABLE_BLURBEHIND = 3;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public uint GradientColor;   // 注意：ABGR，不是 WPF 的 ARGB
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        /// <summary>开启亚克力。tint 为 ABGR 颜色，alpha 决定染色厚度。</summary>
        internal static bool Enable(IntPtr hwnd, uint tintAbgr)
        {
            return Apply(hwnd, ACCENT_ENABLE_ACRYLICBLURBEHIND, tintAbgr);
        }

        /// <summary>关掉模糊，回到普通的模糊/纯透明合成。</summary>
        internal static bool Disable(IntPtr hwnd)
        {
            return Apply(hwnd, ACCENT_ENABLE_BLURBEHIND, 0);
        }

        /// <summary>彻底关掉合成效果 —— 只剩窗口自身画的那层半透明底。</summary>
        internal static bool Off(IntPtr hwnd)
        {
            return Apply(hwnd, ACCENT_DISABLED, 0);
        }

        private static bool Apply(IntPtr hwnd, int state, uint tintAbgr)
        {
            if (hwnd == IntPtr.Zero) return false;

            var policy = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = 2,          // 四边都模糊，否则只有一侧生效
                GradientColor = tintAbgr,
                AnimationId = 0
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr buffer = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(policy, buffer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = buffer,
                    SizeOfData = size
                };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
