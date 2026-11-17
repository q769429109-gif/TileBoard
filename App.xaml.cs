using System;
using System.Windows;
using System.Windows.Threading;

namespace TileBoard
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 一个没人管的异常不该让整块板子消失
            DispatcherUnhandledException += OnDispatcherException;

            // 非 UI 线程的异常以前被一个空 lambda 吞掉 —— 进程一声不响地没了，
            // 日志里连个照面都没有。至少把堆栈留下来。
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                Log("UNHANDLED(" + (args.IsTerminating ? "terminate" : "continue") + "): " +
                    args.ExceptionObject);
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log("UNOBSERVED TASK: " + args.Exception);
                args.SetObserved();
            };

            string[] args = Environment.GetCommandLineArgs();

            // 资源管理器右键菜单送来的「固定到磁贴」：先把路径落进队列。
            // 落盘必须在单实例判定之前 —— 抢不到锁的进程只负责叫醒别人然后退出，
            // 路径只能靠这个队列传过去。
            string pinPath = GetPinArgument(args);
            if (pinPath != null)
            {
                Services.PinQueue.Enqueue(pinPath);
                Log("pin request: " + pinPath);
            }

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--diag", StringComparison.OrdinalIgnoreCase))
                {
                    WriteDiag(args[i + 1]);
                    Shutdown();
                    return;
                }
                if (string.Equals(args[i], "--wintest", StringComparison.OrdinalIgnoreCase))
                {
                    RunWinKeySelfTest(args[i + 1]);
                    return;
                }
                if (string.Equals(args[i], "--winwatch", StringComparison.OrdinalIgnoreCase))
                {
                    RunWatcherSelfTest(args[i + 1]);
                    return;
                }
                if (string.Equals(args[i], "--winlive", StringComparison.OrdinalIgnoreCase))
                {
                    RunWatcherLiveTest(args[i + 1]);
                    return;
                }
            }

            // 自检模式（--shot / --pintest / --grouptest …）是一次性进程，会自己退出，
            // 不该被单实例挡住；否则常驻实例一在跑，所有自检都起不来。
            bool selfTest = IsSelfTest(args);

            // 单实例：已经在跑就只把那个实例唤出，自己退出。
            // 两个实例各装一个键盘钩子会互相抵消，按 Win 键等于什么都没发生。
            if (!selfTest && !Native.SingleInstance.TryAcquire())
            {
                Log("second instance -> signal existing one, exit");
                Shutdown();
                return;
            }

            // 常驻实例才需要取证。自检进程跑完就走，写心跳只是噪音。
            if (!selfTest) Native.CrashGuard.Start();

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Log("APP EXIT code=" + e.ApplicationExitCode + " (正常退出路径)");
            // 删掉会话标记 —— 下次启动若还看到它，就说明这一轮是被硬杀的
            Native.CrashGuard.Stop("code=" + e.ApplicationExitCode);
            base.OnExit(e);
        }

        /// <summary>
        /// 实时验证：装一个真轮询监视器，注入一次 Win 按下/抬起，看 Tapped 会不会来。
        /// 注入键和物理键走同一条输入队列，能证明 GetAsyncKeyState 这条路是通的。
        /// 副作用是开始菜单会闪一下，结束时补一次 Esc 收掉。
        /// </summary>
        private async void RunWatcherLiveTest(string path)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var log = new System.Text.StringBuilder();
            int taps = 0;
            bool sawDown = false;

            var watcher = new Native.WinKeyWatcher();
            watcher.Tapped += () =>
            {
                taps++;
                log.AppendLine("tapped at " + DateTime.Now.ToString("HH:mm:ss.fff"));
            };
            watcher.Start();
            log.AppendLine("running=" + watcher.IsRunning);

            await System.Threading.Tasks.Task.Delay(900);

            log.AppendLine("inject_down=" + Native.WinKeyHook.InjectWinDown());

            // 轮询间隔 10ms，这里采一段看它到底读不读得到按下状态
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 400)
            {
                if (Native.WinKeyWatcher.IsKeyDownNow(0x5B)) { sawDown = true; break; }
                await System.Threading.Tasks.Task.Delay(5);
            }
            log.AppendLine("GetAsyncKeyState_saw_down=" + sawDown);

            await System.Threading.Tasks.Task.Delay(120);
            log.AppendLine("inject_up=" + Native.WinKeyHook.InjectWinUp());

            await System.Threading.Tasks.Task.Delay(600);
            log.AppendLine("taps=" + taps + (taps == 1 ? "  ✔" : "  ✘"));

            watcher.Dispose();
            System.IO.File.WriteAllText(path, log.ToString());

            // 把注入撑开的开始菜单按 Esc 收掉
            Native.WinKeyHook.InjectEsc();

            Shutdown();
        }

        /// <summary>
        /// 轮询监视器的状态机自检：喂模拟按键序列，只看判定对不对。
        /// 不注入真按键，所以不会弹出开始菜单、也不会把字符打进别的窗口。
        /// </summary>
        private void RunWatcherSelfTest(string path)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var log = new System.Text.StringBuilder();
            log.AppendLine("=== 状态机 ===");
            foreach (string line in Native.WinKeyWatcher.SelfTest())
                log.AppendLine(line);

            log.AppendLine();
            log.AppendLine("=== 实时取键 ===");

            int live = 0;
            var watcher = new Native.WinKeyWatcher();
            watcher.Tapped += () => live++;
            watcher.Start();

            // 只验「轮询线程起来并能持续跑」，真实按键留给运行时日志
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                log.AppendLine("running=" + watcher.IsRunning);
                log.AppendLine($"live_taps={live}  （此处为 0 正常，没人按键）");
                watcher.Dispose();
                System.IO.File.WriteAllText(path, log.ToString());
                Shutdown();
            };
            timer.Start();
        }

        /// <summary>
        /// Win 键自检：装上钩子后注入一次「按下 → 抬起」，看 Tapped 有没有被触发。
        /// 用 acceptInjected 只为了测试，正式运行时注入事件一律忽略。
        /// </summary>
        private void RunWinKeySelfTest(string path)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var log = new System.Text.StringBuilder();
            bool tapped = false;

            var hook = new Native.WinKeyHook(acceptInjected: true);
            hook.Tapped += () => tapped = true;
            log.AppendLine("installed=" + hook.Install());

            int step = 0;
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };

            timer.Tick += (s, e) =>
            {
                step++;
                if (step == 1)
                {
                    log.AppendLine("inject_down=" + Native.WinKeyHook.InjectWinDown());
                }
                else if (step == 2)
                {
                    log.AppendLine("inject_up=" + Native.WinKeyHook.InjectWinUp());
                }
                else
                {
                    timer.Stop();
                    log.AppendLine("tapped=" + tapped);
                    hook.Dispose();
                    System.IO.File.WriteAllText(path, log.ToString());
                    Shutdown();
                }
            };

            timer.Start();
        }

        /// <summary>
        /// 命令行里只要有 --xxx 开关（--autostart、--pin 除外）就当自检进程。
        /// 这些进程各自跑一遍就退出，不参与单实例。
        /// </summary>
        private static bool IsSelfTest(string[] args)
        {
            foreach (string a in args)
            {
                if (a.Length > 2 && a.StartsWith("--", StringComparison.Ordinal) &&
                    !a.Equals("--autostart", StringComparison.OrdinalIgnoreCase) &&
                    !a.Equals("--pin", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>取 `--pin "路径"` 后面的路径。没有这个参数返回 null。</summary>
        private static string GetPinArgument(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--pin", StringComparison.OrdinalIgnoreCase))
                {
                    string value = args[i + 1];
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
                }
            }
            return null;
        }

        /// <summary>与主窗口共用同一份 hook.log，排查「为什么唤不出」时全靠它。</summary>
        private static void Log(string line)
        {
            try
            {
                string dir = Services.LayoutStore.Directory;
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "hook.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [pid {Environment.ProcessId}] {line}\n");
            }
            catch { }
        }

        /// <summary>把每个应用的图标获取结果写成报表，用来定位图标缺失。</summary>
        private static void WriteDiag(string path)
        {
            var apps = Native.NativeShell.EnumerateApps();
            int ok = 0, fail = 0, small = 0;

            using (var sw = new System.IO.StreamWriter(path, false, System.Text.Encoding.UTF8))
            {
                sw.WriteLine($"枚举到 {apps.Count} 个应用");
                sw.WriteLine("宽度\t状态\tAUMID\t名称");
                foreach (var app in apps)
                {
                    IntPtr h = Native.NativeShell.GetAppImageHandle(app.Aumid, 256);
                    int w = Native.NativeShell.BitmapWidth(h);
                    string err = Native.NativeShell.LastError ?? "";

                    if (h == IntPtr.Zero) fail++;
                    else if (w < 48) small++;
                    else ok++;

                    sw.WriteLine($"{w}\t{(h == IntPtr.Zero ? "--" : "OK")}\t{app.Aumid}\t{app.Name}\t{err}");
                    Native.NativeShell.FreeBitmap(h);
                }
                sw.WriteLine();
                sw.WriteLine($"合计: 正常={ok} 小图标={small} 失败={fail}");
            }
        }

        private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            e.Handled = true;

            // 拦下归拦下，堆栈得留一份 —— 不然只剩"面板没了"这一个现象可查
            Log("UI EXCEPTION: " + e.Exception);

            MessageBox.Show(
                "磁贴板遇到一个问题，已拦下，面板继续运行。\n\n" + e.Exception.Message,
                "磁贴板", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
