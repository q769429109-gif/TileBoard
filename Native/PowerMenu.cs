using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TileBoard.Native
{
    /// <summary>
    /// 系统电源菜单的调用封装。
    ///
    /// Windows 从来没有公开过「弹开始菜单电源浮出」的 API —— 那个浮出在
    /// StartMenuExperienceHost 里，只能靠 UI 自动化点。但它底下那张
    /// 「关闭 Windows」对话框（桌面按 Alt+F4 弹的同一个，睡眠/关机/重启/休眠
    /// 全在系统的下拉里）是 explorer 的桌面窗口处理 SC_CLOSE 时弹出的。
    ///
    /// 所以这里的做法就是：给 shell 桌面窗口寄一条 WM_SYSCOMMAND/SC_CLOSE，
    /// 让 explorer 自己把系统对话框摆出来 —— 我们一个字都不编，UI 归系统。
    ///
    /// 注意：这是 Topmost 分层窗口的场外因素 —— 对话框是普通顶层窗口，
    /// 调用方必须先把板面收起来，否则整块板面会把它压在下面。
    /// </summary>
    public static class PowerMenu
    {
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_CLOSE = 0xF060;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int length);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder buffer, int length);

        /// <summary>shell 的桌面窗口句柄（Progman / WorkerW），取不到返回 Zero。</summary>
        public static IntPtr ShellWindow() => GetShellWindow();

        /// <summary>
        /// 请 explorer 弹系统「关闭 Windows」对话框。
        /// 返回 false = 连消息都没寄出去（shell 窗口不存在）。
        /// 对话框是否真的出现，要调用方过一会儿用 <see cref="FindShutdownDialog"/> 确认。
        /// </summary>
        public static bool RequestShutdownDialog()
        {
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) return false;
            return PostMessage(shell, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
        }

        /// <summary>
        /// 在顶层窗口里找那张「关闭 Windows」对话框：类名 #32770，标题随语言变
        /// （简体 关闭 Windows / 英文 Shut Down Windows）。找不到返回 Zero。
        /// </summary>
        public static IntPtr FindShutdownDialog()
        {
            IntPtr found = IntPtr.Zero;

            EnumWindowsProc proc = (hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                var cls = new StringBuilder(64);
                GetClassName(hWnd, cls, cls.Capacity);
                if (cls.ToString() != "#32770") return true;

                var title = new StringBuilder(160);
                GetWindowText(hWnd, title, title.Capacity);
                string t = title.ToString();

                bool hit = t.Contains("关闭 Windows") || t.Contains("关闭windows", StringComparison.OrdinalIgnoreCase)
                           || t.Contains("Shut Down Windows", StringComparison.OrdinalIgnoreCase);
                if (!hit) return true;

                found = hWnd;
                return false;   // 命中就停
            };

            EnumWindows(proc, IntPtr.Zero);
            GC.KeepAlive(proc);
            return found;
        }

        /// <summary>把对话框关掉（自检和取消用）。</summary>
        public static void CloseWindow(IntPtr hWnd)
        {
            if (hWnd != IntPtr.Zero) PostMessage(hWnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// 诊断用：把所有可见顶层窗口的「句柄 | 类名 | 标题」列出来。
        /// 探针拿它做前后对照 —— 只想知道 shell 到底有没有冒出新窗口。
        /// </summary>
        public static System.Collections.Generic.List<string> DumpTopWindows()
        {
            var List = new System.Collections.Generic.List<string>();

            EnumWindowsProc proc = (hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var cls = new StringBuilder(64);
                GetClassName(hWnd, cls, cls.Capacity);
                var title = new StringBuilder(160);
                GetWindowText(hWnd, title, title.Capacity);
                if (title.Length > 0 || cls.ToString().StartsWith("#32770"))
                    List.Add("0x" + hWnd.ToInt64().ToString("X") + " | " + cls + " | " + title);
                return true;
            };

            EnumWindows(proc, IntPtr.Zero);
            GC.KeepAlive(proc);
            return List;
        }
    }
}
