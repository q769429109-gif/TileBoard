using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TileBoard.Native
{
    /// <summary>
    /// 低级键盘钩子，只做一件事：把「单独按 Win 键」变成唤出/收起磁贴板。
    ///
    /// 关键难点：不能把 Win+E / Win+D / Win+L 这类组合键一起吞掉。
    /// 做法是按下 Win 时先扣住不放行，若期间出现其它键，就用 SendInput 把 Win 按下补发给系统，
    /// 让系统按正常组合键处理；只有「Win 按下 → 期间无其它键 → Win 抬起」才判为单击。
    /// </summary>
    public sealed class WinKeyHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const uint VK_LWIN = 0x5B;
        private const uint VK_RWIN = 0x5C;
        private const uint VK_SHIFT = 0x10;
        private const uint VK_CONTROL = 0x11;
        private const uint VK_MENU = 0x12;

        private const uint LLKHF_INJECTED = 0x10;
        private const uint INPUT_KEYBOARD = 1;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(uint vKey);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

        private const int THREAD_PRIORITY_HIGHEST = 2;

        private const uint WM_QUIT = 0x0012;

        private readonly LowLevelKeyboardProc _proc;   // 必须持有引用，否则会被 GC 回收导致崩溃
        private readonly bool _acceptInjected;         // 仅自检模式用：接受自己注入的按键
        private IntPtr _hook = IntPtr.Zero;

        private Thread _thread;
        private uint _threadId;

        /// <summary>装上钩子失败时的原始错误码，写进日志比「FAILED」有用。</summary>
        public int LastError { get; private set; }

        private bool _winDown;      // 物理 Win 键处于「被我们扣住」的状态
        private bool _combo;        // 期间出现过其它键
        private bool _replayed;     // 已把 Win 按下补发给系统

        private long _lastEventTick;   // 最近一次收到任何按键回调的时刻（TickCount64）

        /// <summary>干净单击 Win 键时触发。</summary>
        public event Action Tapped;

        /// <summary>
        /// 每次看到 Win 键按下/抬起都报一次（只在需要排查时订阅）。
        /// 排查「按了没反应」时，它能区分两种完全不同的情况：
        /// 有 down 无 tap ＝ 键到了但我们判定成组合键；连 down 都没有 ＝ 键根本没到钩子。
        /// </summary>
        public event Action<string> SawWinKey;

        public bool IsHooked => _hook != IntPtr.Zero;

        public WinKeyHook(bool acceptInjected = false)
        {
            _proc = HookCallback;
            _acceptInjected = acceptInjected;
        }

        /// <summary>
        /// 装钩子必须用自己的线程 + 自己的消息循环。
        ///
        /// 低级键盘钩子的回调是在装钩子的那个线程上跑的，系统还给它卡了超时
        /// （LowLevelHooksTimeout，默认 300ms）：回调慢了，系统直接把钩子摘掉，
        /// 之后 Win 键再没人管 —— 表现就是「用一阵子突然失效」。
        /// UI 线程要取图标、要渲染整块板面，随时可能卡过 300ms，绝不能拿来挂钩子。
        /// </summary>
        public bool Install()
        {
            if (_thread != null) return IsHooked;

            bool ok = false;
            var ready = new ManualResetEventSlim(false);

            _thread = new Thread(() =>
            {
                // 钩子回调有 300ms 超时，线程抢不到 CPU 就会被系统摘钩 —— 优先级提上来。
                SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);

                _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);

                if (_hook == IntPtr.Zero)
                {
                    LastError = Marshal.GetLastWin32Error();
                }
                else
                {
                    _threadId = GetCurrentThreadId();
                    ok = true;
                }

                ready.Set();

                if (!ok) return;

                // 这条循环就是钩子的生命线：循环一停，系统立刻当它已经卸掉了
                while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                if (_hook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                }
            })
            {
                IsBackground = true,
                Name = "TileBoard.WinKeyHook"
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            ready.Wait(3000);
            return ok;
        }

        /// <summary>
        /// 存活探测：注入一颗没有任何实际作用的 F24，看回调会不会进来。
        ///
        /// 为什么需要：钩子装上（SetWindowsHookEx 返回非零）不等于还在链上 ——
        /// 被别的软件抢先吞掉、或被系统按 LowLevelHooksTimeout 摘掉之后，
        /// 进程内看不到任何异常，只表现为「按 Win 键没反应」，没法自证。
        /// </summary>
        public bool Probe(int timeoutMs = 450)
        {
            if (_hook == IntPtr.Zero) return false;

            long before = Interlocked.Read(ref _lastEventTick);

            const uint VK_F24 = 0x87;
            SendKey(VK_F24, 0);
            SendKey(VK_F24, 0x0002);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (Interlocked.Read(ref _lastEventTick) != before) return true;
                Thread.Sleep(10);
            }

            return false;
        }

        public void Dispose()
        {
            uint tid = _threadId;
            _threadId = 0;

            if (tid != 0) PostThreadMessage(tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);

            _thread?.Join(1500);
            _thread = null;

            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            // 先记一笔：只要有回调进来，就说明钩子还挂在链上
            Interlocked.Exchange(ref _lastEventTick, Environment.TickCount64);

            if (nCode < 0) return CallNextHookEx(_hook, nCode, wParam, lParam);

            int message = wParam.ToInt32();
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

            // 自己注入的事件一律放行，否则会自锁
            if ((info.flags & LLKHF_INJECTED) != 0 && !_acceptInjected)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            bool isWinKey = info.vkCode == VK_LWIN || info.vkCode == VK_RWIN;
            bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
            bool isUp = message == WM_KEYUP || message == WM_SYSKEYUP;

            if (isWinKey)
            {
                SawWinKey?.Invoke($"vk=0x{info.vkCode:X2} {(isDown ? "down" : "up")} " +
                                  $"combo={_combo} replayed={_replayed} injected={((info.flags & LLKHF_INJECTED) != 0)}");

                if (isDown)
                {
                    // 已经按着 Ctrl/Alt/Shift 时按 Win，属于组合键，直接放行
                    if (IsAnyModifierDown())
                        return CallNextHookEx(_hook, nCode, wParam, lParam);

                    if (!_winDown)
                    {
                        _winDown = true;
                        _combo = false;
                        _replayed = false;
                    }
                    return (IntPtr)1;   // 扣住，先不放行
                }

                if (isUp)
                {
                    if (!_winDown)
                        return CallNextHookEx(_hook, nCode, wParam, lParam);

                    bool wasCombo = _combo || _replayed;
                    _winDown = false;
                    _combo = false;
                    _replayed = false;

                    if (wasCombo)
                    {
                        // 组合键：物理抬起要放行，才能把补发的 Win 按下解掉
                        return CallNextHookEx(_hook, nCode, wParam, lParam);
                    }

                    // 干净单击：两头都吞掉，系统不会弹出自带开始菜单
                    Tapped?.Invoke();
                    return (IntPtr)1;
                }
            }
            else if (_winDown && !_replayed)
            {
                // Win 被扣住期间出现了别的键 —— 判定为组合键，把 Win 按下补给系统
                _replayed = SendWinDown();
                _combo = _replayed;
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static bool IsAnyModifierDown()
        {
            return (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
                || (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
        }

        private static bool SendWinDown()
        {
            return SendKey(VK_LWIN, 0);
        }

        /// <summary>自检用：注入一次 Win 键按下 / 抬起。</summary>
        public static bool InjectWinDown() => SendKey(VK_LWIN, 0);

        public static bool InjectWinUp() => SendKey(VK_LWIN, 0x0002 /* KEYEVENTF_KEYUP */);

        /// <summary>自检用：注入一次 Esc，收掉被注入撑开的开始菜单。</summary>
        public static bool InjectEsc()
        {
            const uint VK_ESCAPE = 0x1B;
            bool down = SendKey(VK_ESCAPE, 0);
            bool up = SendKey(VK_ESCAPE, 0x0002);
            return down && up;
        }

        /// <summary>通用注入：按一下再抬起。电源按钮借它敲 Win+X / U。</summary>
        public static bool InjectVk(uint vk) => SendKey(vk, 0) && SendKey(vk, 0x0002);

        private static bool SendKey(uint vk, uint flags)
        {
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
            // 注入失败就退回「单击」语义，绝不能让系统以为 Win 被按住不放
            return SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) == 1;
        }
    }
}
