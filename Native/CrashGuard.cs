using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TileBoard.Native
{
    /// <summary>
    /// 死亡取证。回答一个问题：进程消失时，是它自己崩了，还是被别的程序结束了。
    ///
    /// 为什么需要：进程被 TerminateProcess 杀掉时，代码一行都跑不到 —— 正常退出、
    /// 未处理异常、看门狗统统不触发，用户端只看到「过一会就没了」，日志里干净得像
    /// 什么都没发生过。这种时候唯一的证据是「它最后一次还活着是什么时候」。
    ///
    /// 三件取证手段：
    ///   ① 心跳：每 15 秒往 heartbeat.log 追一行（pid / 内存 / 线程数）。被外部杀死时
    ///      心跳会突然断在某个时刻，断点就是死亡时间。
    ///   ② 会话标记：启动时写 session.txt，正常退出时删掉。下次启动发现它还在，
    ///      说明上一轮没走完退出流程（崩溃、被杀、蓝屏都算）。
    ///   ③ 致命异常过滤器：SetUnhandledExceptionFilter 能抓到访问违例这类非托管崩溃，
    ///      这类异常 .NET 的 AppDomain.UnhandledException 未必触发。
    /// </summary>
    internal static class CrashGuard
    {
        private static readonly object Sync = new object();
        private static bool _started;

        private static string HeartPath => Path.Combine(Services.LayoutStore.Directory, "heartbeat.log");
        private static string SessionPath => Path.Combine(Services.LayoutStore.Directory, "session.txt");

        /// <summary>心跳间隔。15 秒足够把「死亡时间」框到一刻钟以内，又不会把日志写爆。</summary>
        private const int BeatMs = 15000;

        public static void Start()
        {
            if (_started) return;
            _started = true;

            try
            {
                Directory.CreateDirectory(Services.LayoutStore.Directory);
                ReportPreviousSession();
                WriteSession();
                InstallFatalFilter();

                var t = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "TileBoard.Heartbeat",
                    Priority = ThreadPriority.BelowNormal
                };
                t.Start();

                Beat("start");
            }
            catch { }
        }

        /// <summary>正常退出时调用：删掉会话标记，下次启动才不会误判成异常结束。</summary>
        public static void Stop(string reason)
        {
            try
            {
                Beat("exit " + reason);
                if (File.Exists(SessionPath)) File.Delete(SessionPath);
            }
            catch { }
        }

        private static void Loop()
        {
            while (true)
            {
                try { Thread.Sleep(BeatMs); } catch { return; }
                Beat("alive");
            }
        }

        private static void Beat(string tag)
        {
            try
            {
                string line;
                using (var p = Process.GetCurrentProcess())
                {
                    p.Refresh();
                    line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  pid {p.Id}  {tag,-12} " +
                           $"ws={p.WorkingSet64 / 1048576}MB  threads={p.Threads.Count}  " +
                           $"handles={p.HandleCount}";
                }

                lock (Sync)
                {
                    Rotate();
                    File.AppendAllText(HeartPath, line + "\n");
                }
            }
            catch { }
        }

        /// <summary>超过 96KB 就只留最后 200 行，防止常驻几年把盘写满。</summary>
        private static void Rotate()
        {
            try
            {
                var fi = new FileInfo(HeartPath);
                if (!fi.Exists || fi.Length < 96 * 1024) return;

                string[] lines = File.ReadAllLines(HeartPath);
                int keep = Math.Min(lines.Length, 200);
                var tail = new string[keep];
                Array.Copy(lines, lines.Length - keep, tail, 0, keep);
                File.WriteAllLines(HeartPath, tail);
            }
            catch { }
        }

        private static void WriteSession()
        {
            string exe = Environment.ProcessPath ?? "";
            File.WriteAllText(SessionPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{Environment.ProcessId}\n{exe}\n");
        }

        /// <summary>
        /// 上次的 session.txt 还存在 = 上一轮没走正常退出。写进 hook.log，
        /// 这样「程序自己崩溃」和「被外部杀掉」迟早能分开：崩了有 FATAL/UNHANDLED，
        /// 被杀则只有心跳断档。
        /// </summary>
        private static void ReportPreviousSession()
        {
            try
            {
                if (!File.Exists(SessionPath)) return;

                string[] old = File.ReadAllLines(SessionPath);
                string when = old.Length > 0 ? old[0] : "?";
                string pid = old.Length > 1 ? old[1] : "?";

                Append($"PREV SESSION NOT CLOSED: started {when} pid {pid} " +
                       "(上一轮没走正常退出：崩溃或被外部结束)");
            }
            catch { }
        }

        // —— 致命异常过滤器 ——

        [DllImport("kernel32.dll")]
        private static extern IntPtr SetUnhandledExceptionFilter(IntPtr filter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ExceptionFilter(IntPtr exceptionPointers);

        // 必须留强引用，否则委托被 GC 后回调进野指针
        private static ExceptionFilter _filter;

        private static void InstallFatalFilter()
        {
            _filter = info =>
            {
                try
                {
                    uint code = 0;
                    if (info != IntPtr.Zero)
                    {
                        IntPtr record = Marshal.ReadIntPtr(info);
                        if (record != IntPtr.Zero) code = (uint)Marshal.ReadInt32(record);
                    }

                    string hex = "0x" + code.ToString("X8");
                    Append($"FATAL native exception code={hex} {Explain(code)}");
                    Beat("fatal " + hex);
                }
                catch { }

                return 0;   // EXCEPTION_CONTINUE_SEARCH：交给 WER 照常处理
            };

            try
            {
                SetUnhandledExceptionFilter(Marshal.GetFunctionPointerForDelegate(_filter));
            }
            catch { }
        }

        private static string Explain(uint code) => code switch
        {
            0xC0000005 => "(访问违例：读写非法内存)",
            0xC00000FD => "(栈溢出)",
            0xC0000409 => "(栈缓冲区检查失败 / fail-fast)",
            0xE0434352 => "(.NET 异常逃逸到顶层)",
            0xC0000374 => "(堆损坏)",
            _ => ""
        };

        /// <summary>写一条到主日志 hook.log，和程序其它日志同一份文件。</summary>
        private static void Append(string line)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Services.LayoutStore.Directory, "hook.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [pid {Environment.ProcessId}] {line}\n");
            }
            catch { }
        }
    }
}
