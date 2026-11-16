using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace TileBoard.Native
{
    /// <summary>
    /// 轮询式 Win 键监视器 —— 低级键盘钩子的兜底路径。
    ///
    /// 为什么需要它：WH_KEYBOARD_LL 是挂在一条全局钩子链上的，只要有一个程序
    /// 在更外侧装钩子并且「吞掉」按键（回调里直接返回 1，不调 CallNextHookEx），
    /// 我们的回调就永远收不到；回调慢过 LowLevelHooksTimeout 时系统还会悄悄摘钩。
    /// 这两种情况下钩子那边一片安静，看起来就像「按了没反应」，而且没法从进程内自证。
    ///
    /// GetAsyncKeyState 读的是原始按键状态，跟钩子链无关，谁也抢不走。
    /// 代价是只能「看」，不能「吞」—— 真开始菜单会不会一起弹出来，取决于钩子那路是否还活着。
    /// </summary>
    public sealed class WinKeyWatcher : IDisposable
    {
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;

        /// <summary>轮询间隔。人手按 Win 通常 60~150ms，10ms 的采样绝不会漏。</summary>
        private const int PollMs = 10;

        /// <summary>按住超过这个时长就不算「轻点」，避免误触。</summary>
        private const int MaxHoldMs = 1500;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private readonly Func<int, bool> _isDown;
        private readonly int[] _comboKeys;

        private Thread _thread;
        private volatile bool _stop;

        private bool _wasDown;
        private bool _combo;
        private bool _stuck;
        private int _holdStart;

        /// <summary>干净轻点 Win 键时触发（在监视线程上，订阅方自己切回 UI 线程）。</summary>
        public event Action Tapped;

        public bool IsRunning => _thread != null && _thread.IsAlive;

        public WinKeyWatcher() : this(vk => (GetAsyncKeyState(vk) & 0x8000) != 0) { }

        /// <summary>注入自定义取键函数 —— 自检用它跑状态机，不需要真按键。</summary>
        public WinKeyWatcher(Func<int, bool> isDown)
        {
            _isDown = isDown ?? throw new ArgumentNullException(nameof(isDown));
            _comboKeys = BuildComboKeys();
        }

        /// <summary>直接读一次按键状态（自检用，正常路径走轮询线程）。</summary>
        public static bool IsKeyDownNow(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        public void Start()
        {
            if (_thread != null) return;

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "TileBoard.WinKeyWatcher"
            };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _thread?.Join(300);
            _thread = null;
        }

        private void Loop()
        {
            while (!_stop)
            {
                try
                {
                    Tick();
                }
                catch
                {
                    // 监视线程绝不能死：死了就等于 Win 键彻底失灵
                    _wasDown = false;
                    _combo = false;
                }

                Thread.Sleep(PollMs);
            }
        }

        private void Tick()
        {
            bool down = _isDown(VK_LWIN) || _isDown(VK_RWIN);

            if (down && !_wasDown)
            {
                // 按下瞬间已经按着 Ctrl/Alt/Shift 的，从一开始就当组合键
                _holdStart = Environment.TickCount;
                _combo = _isDown(VK_CONTROL) || _isDown(VK_MENU) || _isDown(VK_SHIFT);
            }
            else if (down && _wasDown)
            {
                // 按住期间出现了别的键 —— Win+E / Win+D 这类，绝不当作唤出
                if (!_combo && AnyComboKeyDown()) _combo = true;

                // 卡死自愈：钩子把抬起吞掉时，这里可能永远读成「按下」。
                // 真按住 Win 超过 5 秒本身没有意义，直接把这个状态判废，
                // 免得之后每一次按键都变成「中间那次抬起丢了」的哑火。
                if (!_stuck && Environment.TickCount - _holdStart > 5000)
                {
                    _stuck = true;
                    _combo = true;
                }
            }
            else if (!down && _wasDown)
            {
                bool tap = !_combo && !_stuck && Environment.TickCount - _holdStart <= MaxHoldMs;
                _combo = false;
                _stuck = false;

                if (tap) Tapped?.Invoke();
            }

            _wasDown = down;
        }

        private bool AnyComboKeyDown()
        {
            for (int i = 0; i < _comboKeys.Length; i++)
                if (_isDown(_comboKeys[i])) return true;
            return false;
        }

        /// <summary>比 Win 组合常见的那批键：字母数字、Tab/空格/回车/退格/方向、F1-F12。</summary>
        private static int[] BuildComboKeys()
        {
            var keys = new List<int>();

            for (int vk = 0x30; vk <= 0x39; vk++) keys.Add(vk);   // 0-9
            for (int vk = 0x41; vk <= 0x5A; vk++) keys.Add(vk);   // A-Z

            keys.AddRange(new[]
            {
                0x09,               // Tab
                0x0D,               // Enter
                0x08,               // Backspace
                0x20,               // Space
                0x25, 0x26, 0x27, 0x28,   // 方向键
                0x2D, 0x2E,         // Insert / Delete
                0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0,  // ; = , - . / `
                0xDB, 0xDC, 0xDD, 0xDE,                     // [ \ ] '
            });

            for (int vk = 0x70; vk <= 0x7B; vk++) keys.Add(vk);   // F1-F12
            keys.Add(0x2C);                                        // PrintScreen

            return keys.ToArray();
        }

        /// <summary>
        /// 状态机自检：喂一串模拟按键序列，看判定结果对不对。
        /// 不碰真实键盘，所以不会弹出开始菜单、不会把字符打进别的窗口。
        /// </summary>
        internal static List<string> SelfTest()
        {
            var lines = new List<string>();
            int taps = 0;

            // 用一份可变的状态表模拟键盘
            var keys = new HashSet<int>();
            Func<int, bool> probe = vk => keys.Contains(vk);

            var w = new WinKeyWatcher(probe);
            w.Tapped += () => taps++;

            // 各个用例之间手动调 Tick，不用起线程，时序完全可控
            void Press(params int[] vks) { foreach (var v in vks) keys.Add(v); w.Tick(); }
            void Release(params int[] vks) { foreach (var v in vks) keys.Remove(v); w.Tick(); }

            void Case(string name, Action body, bool shouldTap)
            {
                taps = 0;
                keys.Clear();
                w.ResetForTest();
                body();
                lines.Add($"{name}  ->  taps={taps}  {(taps > 0 == shouldTap ? "✔" : "✘")}");
            }

            Case("单按 Win", () => { Press(VK_LWIN); Release(VK_LWIN); }, true);
            Case("单按右 Win", () => { Press(VK_RWIN); Release(VK_RWIN); }, true);
            Case("Win+E", () => { Press(VK_LWIN); Press(0x45); Release(0x45); Release(VK_LWIN); }, false);
            Case("Win+D", () => { Press(VK_LWIN); Press(0x44); Release(0x44); Release(VK_LWIN); }, false);
            Case("Ctrl+Win", () => { Press(VK_CONTROL); Press(VK_LWIN); Release(VK_LWIN); Release(VK_CONTROL); }, false);
            Case("Win 按住不放", () =>
            {
                Press(VK_LWIN);
                w.ForceHoldForTest(2000);
                Release(VK_LWIN);
            }, false);

            return lines;
        }

        // ---- 仅供 SelfTest 使用的小钩子，正常路径不碰 ----

        private void ResetForTest()
        {
            _wasDown = false;
            _combo = false;
            _stuck = false;
            _holdStart = 0;
        }

        private void ForceHoldForTest(int ms)
        {
            _holdStart -= ms;
        }
    }
}
