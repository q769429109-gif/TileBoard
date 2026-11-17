using System;
using System.Runtime.InteropServices;

namespace TileBoard.Native
{
    /// <summary>
    /// shell 的 ITaskbarList2 接口。全屏应用在前台时，正是 shell 自己把这个窗口
    /// 「标记成全屏」→ 于是任务栏的内容整体滑走（窗口还在原位、还是可见的，
    /// 但变成透明，透过它看到的是底下的游戏画面）。
    ///
    /// 反过来调用 MarkFullscreenWindow(hwnd, FALSE) 就是告诉 shell「全屏结束了」，
    /// 它会立刻把任务栏滑回来。这是唯一不依赖模拟输入、也不改用户设置的办法 ——
    /// 比切 autohide 那套温和（那套会在 UI 线程上卡好几秒）。
    /// </summary>
    internal static class TaskbarList
    {
        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        private class CTaskbarList { }

        [ComImport, Guid("602D4995-B13A-429b-A66E-1935E44F4317"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList2
        {
            // ITaskbarList
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);

            // ITaskbarList2
            void MarkFullscreenWindow(IntPtr hwnd,
                                      [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        }

        /// <summary>
        /// 告诉 shell 这个窗口不再是全屏（任务栏随即滑回来）。
        /// 必须在 STA 线程调用 —— 别在 UI 线程上直接来，见 Taskbar 里那堆 SETSTATE 的教训。
        /// </summary>
        internal static bool ClearFullscreen(IntPtr hwnd) => Mark(hwnd, false);

        /// <summary>
        /// 反过来：把「全屏」标记还回去（收板面后恢复现场）。
        /// 不还的话，从全屏游戏里唤出一次磁贴板，任务栏就会一直压在游戏上面，
        /// 直到游戏自己退出全屏 —— 那是比不显示任务栏更难受的副作用。
        /// </summary>
        internal static bool SetFullscreen(IntPtr hwnd) => Mark(hwnd, true);

        private static bool Mark(IntPtr hwnd, bool fullscreen)
        {
            if (hwnd == IntPtr.Zero) return false;
            try
            {
                var list = (ITaskbarList2)new CTaskbarList();
                list.HrInit();
                list.MarkFullscreenWindow(hwnd, fullscreen);
                Marshal.ReleaseComObject(list);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
