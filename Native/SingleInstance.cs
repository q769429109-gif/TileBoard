using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TileBoard.Native
{
    /// <summary>
    /// 单实例守卫 + 唤醒信号。
    ///
    /// 两个实例同时装低级键盘钩子会互相抵消：按一下 Win 键，两个钩子都判定为单击，
    /// 一个把板面 Show 出来、另一个立刻 Hide 掉，看起来就是「按了没反应」。
    /// 所以第二个实例只负责把第一个叫醒，然后自己退出。
    ///
    /// 唤醒走命名事件而不是 HWND_BROADCAST —— 广播消息发给的是「所有顶层窗口」，
    /// 实测这里收不到（窗口是 WS_EX_TOOLWINDOW，广播路径不认），
    /// 命名事件是内核对象，跨进程一定送得到。
    /// </summary>
    internal static class SingleInstance
    {
        private const string MutexName = @"Local\TileBoard.SingleInstance";
        private const string SignalName = @"Local\TileBoard.ShowSignal";

        private static Mutex _mutex;
        private static EventWaitHandle _signal;
        private static Thread _listener;

        /// <summary>第二个实例请求唤出时触发（在线程池线程上，订阅方自己转 UI 线程）。</summary>
        public static event Action ShowRequested;

        /// <summary>
        /// 拿到锁返回 true。返回 false 说明已经有实例在跑 —— 此时已经发过一次唤醒信号，
        /// 调用方应当直接退出。
        /// </summary>
        public static bool TryAcquire()
        {
            bool created;
            _mutex = new Mutex(true, MutexName, out created);

            if (!created)
            {
                SignalShow();
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            // 第一个实例负责收信号
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);

            _listener = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (!_signal.WaitOne()) break;
                    }
                    catch
                    {
                        break;
                    }

                    ShowRequested?.Invoke();
                }
            })
            {
                IsBackground = true,
                Name = "TileBoard.ShowListener"
            };

            _listener.Start();
            return true;
        }

        /// <summary>叫醒已经在跑的那个实例。没有实例在跑时静默失败。</summary>
        private static void SignalShow()
        {
            try
            {
                using var handle = EventWaitHandle.OpenExisting(SignalName);
                handle.Set();
            }
            catch
            {
                // 对方还没建好命名事件：极少数情况下会漏一次，直接忽略
            }
        }
    }
}
