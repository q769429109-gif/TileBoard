using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using TileBoard.Controls;
using TileBoard.Models;
using TileBoard.Services;

namespace TileBoard
{
    /// <summary>「所有应用」列表的行模型。图标异步补，避免一次拉几百个图标卡住界面。</summary>
    /// <summary>
    /// 应用库列表里的一行。
    /// 字母标题和真正的应用行共用这一个类 —— 标题只是 IsHeader=true 的哑行，
    /// 列表就不用上 CollectionViewSource 分组，虚拟化也不会因为分组掉链子。
    /// </summary>
    public class AppRow : INotifyPropertyChanged
    {
        private BitmapSource _icon;
        private bool _pinned;

        public string Aumid { get; set; }
        public string Name { get; set; }

        /// <summary>分到哪个字母组。标题行没用，应用行只作排序权重用。</summary>
        public char Letter { get; set; }

        /// <summary>这一行是不是字母标题（A、B、#…），不是应用。</summary>
        public bool IsHeader { get; set; }

        /// <summary>标题行显示的文字。</summary>
        public string HeaderText { get; set; }

        public BitmapSource Icon
        {
            get => _icon;
            set { _icon = value; Raise(nameof(Icon)); }
        }

        /// <summary>这个应用已经在板上了 —— 行尾给个灰字提示，免得白拖一趟。</summary>
        public bool IsPinned
        {
            get => _pinned;
            set { _pinned = value; Raise(nameof(IsPinned)); }
        }

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class MainWindow : Window
    {
        private readonly AppCatalog _catalog = new AppCatalog();
        private BoardLayout _layout;

        private bool _suppressHide;   // 弹对话框时不要自动隐藏
        private bool _renaming;

        // 拖拽状态
        private Border _dragBorder;
        private TileItem _dragItem;
        private TileGroup _dragGroup;
        private Point _dragOrigin;
        private Point _grabOffset;
        private FrameworkElement _ghost;
        private bool _mouseDown;
        private bool _dragging;

        // 落点是否是「组外空白」——松手就新建一个分组
        private bool _dropIntoNewGroup;
        private Border _newGroupHint;

        /// <summary>
        /// 这次拖的磁贴是不是刚从应用库拎进来的新磁贴（板上原本没有）。
        /// 新磁贴在落进某个组之前不属于任何组，EndDrag 要按这个分支去收尾 ——
        /// 否则拖到空白处就变成「没有源组可搬」，磁贴直接蒸发。
        /// </summary>
        private bool _dragIsNew;

        /// <summary>
        /// 拖动开始那一刻，全场每块磁贴的格位。
        ///
        /// 拖动过程中每次鼠标移动都先按它把整块板恢复原样，再重新算一次落点 ——
        /// 这样「被挤开的磁贴」是每次重算出来的，拖动走开后它自己就归位了。
        /// 不这么做的话，Place 的修改会一轮轮叠加，被推的磁贴只会被越推越远。
        /// </summary>
        private Dictionary<TileItem, (int Column, int Row)> _dragSnapshot;

        // 应用库列表里的拖动判据：先记按下的是哪一行，动过 6px 才算拖
        private AppRow _listDragRow;
        private Point _listDragOrigin;
        private bool _listMouseDown;

        /// <summary>最近一次磁贴落点的板面坐标，新分组就生在那一格。</summary>
        private Point _lastBoardPoint;

        /// <summary>刚点「新建分组」建出来、还没放磁贴的那个组，暂时不清理。</summary>
        private TileGroup _keepEmptyGroup;

        /// <summary>上次归一用的列数。列数变了才需要把没动过的组重新排。</summary>
        private int _lastColumns;

        // 分组拖动（与磁贴拖动互斥）
        private TileGroup _groupDragGroup;
        private FrameworkElement _groupDragBlock;
        private FrameworkElement _groupGhost;
        private RenderTargetBitmap _ghostSnapshot;      // 仅自检用：看抓到的那张位图长什么样
        private Point _groupDragOrigin;
        private Point _groupGrabOffset;
        private bool _groupMouseDown;
        private bool _groupDragging;

        /// <summary>
        /// 分组拖动开始那一刻，全场每个分组的格位。与 _dragSnapshot 同一套账：
        /// 每次鼠标移动先按它把板面归位，再重算落点 —— 被挤开的分组才会自己回来，
        /// 不然 Place 的修改一轮轮叠加，组就被越推越远。
        /// </summary>
        private Dictionary<TileGroup, (int Column, int Row)> _groupDragSnapshot;

        // 淡出进行中：期间再按 Win 要能取消隐藏
        private bool _hiding;

        // 每次显隐都递增，用来作废「在途的淡出收尾回调」
        private int _fadeToken;

        // 唤出时刻。窗口真正拿到焦点之前会先报到一次 Deactivated，
        // 那一下不能当成「用户点了外面」，否则面板会刚淡入就自己淡掉
        private DateTime _shownAt = DateTime.MinValue;

        private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(180);
        private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(140);

        private const int HotkeyId = 0xB001;
        private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
        private const uint VK_SPACE = 0x20;

        private Native.WinKeyHook _winHook;
        private Native.WinKeyWatcher _winWatcher;
        private System.Threading.Timer _hookWatch;

        /// <summary>
        /// 钩子与轮询是两条独立的判定路径，同一次按键可能两边都报。
        /// 350ms 内的第二次直接丢掉 —— 否则按一下会「开了又立刻关」。
        /// </summary>
        private long _lastTapTick;

        /// <summary>钩子最近一次看到 Win 键的时刻，用来判断「轮询看见了而钩子没看见」。</summary>
        private long _lastHookSawTick;
        private long _lastRebuildTick;
        private System.Windows.Threading.DispatcherTimer _toastTimer;

        // 外观设置（面板宽度、亚克力开关），与磁贴布局分开存
        private readonly UiSettings _ui = UiSettings.Load();

        // 右边缘拖拽改宽度
        private bool _resizing;
        private double _resizeStartX;
        private double _resizeStartWidth;
        private double _restoreWidth = 560;

        private const double MinPanelWidth = 560;
        private const double MinPanelHeight = 320;

        // 上边缘拖高度
        private bool _resizingH;
        private double _resizeStartY;
        private double _resizeStartHeight;

        // 外观浮层里的滑块初始化时也会触发 ValueChanged，这个标记挡住那几次
        private bool _appearanceReady;

        // 拖滑块时每帧都写 settings.json 太浪费，攒一下再落盘
        private System.Windows.Threading.DispatcherTimer _settingsSaveTimer;

        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string cls, string title);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        public MainWindow()
        {
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;
            Deactivated += OnDeactivated;
            Closing += OnClosing;
            KeyDown += OnKeyDown;
            MouseMove += OnWindowMouseMove;
            MouseLeftButtonUp += OnWindowMouseLeftButtonUp;

            _toastTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.6)
            };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                Toast.Opacity = 0;
            };

            // 面板改宽后，分组按列重新等距分布，别让右边空一大条；
            // 应用库的宽度也要跟着走，否则窄板上它会把磁贴区吃掉大半
            BoardScroll.SizeChanged += (s, e) =>
            {
                UpdateGroupLayout();
                UpdateAppsPanelWidth();
            };

            // 窗口尺寸/位置一动，垫底那张桌面图就得跟着挪，否则全屏时会整体错位
            SizeChanged += (s, e) => UpdateGlassPlacement();
            LocationChanged += (s, e) => UpdateGlassPlacement();

            // 又有人双击了一次 exe：那个实例被单实例挡下，只留这条信号过来
            Native.SingleInstance.ShowRequested += () =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    LogHookState2("show requested by another launch");
                    HideSettingsPage(false);
                    HidePhonePage();

                    // 资源管理器右键「固定到磁贴」也是走这条信号，路径在队列里
                    if (HandlePinRequests()) return;

                    ShowBoard();
                }));
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;

            // 设置页 / 手机页开着就只收页，别顺手把整个面板关掉
            if (SettingsPage.Visibility == Visibility.Visible)
            {
                HideSettingsPage();
                e.Handled = true;
                return;
            }

            if (PhonePage.Visibility == Visibility.Visible)
            {
                HidePhonePage();
                e.Handled = true;
                return;
            }

            // 拖动到一半按 Esc：先把拖的东西放回去，别顺手把整个面板关掉
            if (_groupDragging || _groupMouseDown)
            {
                CancelGroupDrag();
                Save();
                RenderAll();
                e.Handled = true;
                return;
            }

            if (_dragging || _mouseDown)
            {
                // 从应用库拎出来的新磁贴：Esc 是「不要了」，不是「随手放这儿」
                bool wasNew = _dragIsNew;

                _mouseDown = false;
                Mouse.Capture(null);

                if (wasNew) CancelLibraryDrag();
                else EndDrag();

                e.Handled = true;
                return;
            }

            if (AppsPanel.Visibility == Visibility.Visible)
            {
                HideAppsPanel();
                e.Handled = true;
                return;
            }

            HideBoard();
            e.Handled = true;
        }

        // ================= 启动 =================

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);

            // 备用热键（万一 Win 键钩子被别的软件抢先，还能用这个开）
            bool hotkey = RegisterHotKey(handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE);

            // Win10 的开始菜单是直角，Win11 默认给圆角，这里强制不圆
            int corner = 1;   // DWMWCP_DONOTROUND
            DwmSetWindowAttribute(handle, 33, ref corner, sizeof(int));

            // 不进 Alt+Tab —— Win10 的开始菜单也不在里面
            SetWindowLong(handle, GWL_EXSTYLE,
                GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

            ApplyAppearance(handle);

            // 单独按 Win 键唤出。自检进程不挂钩子 —— 否则会和常驻实例抢，
            // 自检跑完就把用户的 Win 键带走了。
            if (!IsSelfTestRun())
            {
                _winHook = MakeHook();
                bool hooked = _winHook.Install();

                // 装上不等于还在链上：探一发 F24，没有回调就重装（重装会排到链首）
                bool alive = hooked && _winHook.Probe();
                if (hooked && !alive)
                {
                    _winHook.Dispose();
                    _winHook = MakeHook();
                    hooked = _winHook.Install();
                    alive = hooked && _winHook.Probe();
                    LogHookState2($"hook reinstalled  hooked={hooked}  alive={alive}");
                }

                if (!hooked)
                    SetStatus($"Win 键接管失败（错误码 {_winHook.LastError}），请用 Ctrl+Alt+空格 唤出");
                LogHookState(hooked);
                LogHookState2($"hook_alive={alive}  （False 表示被别的软件吞掉或已被系统摘钩，靠轮询兜底）");

                // 钩子那条链别人能抢、系统也会悄悄摘，所以再挂一条不看钩子链的轮询。
                // 只要有一路活着，Win 键就能唤出。
                _winWatcher = new Native.WinKeyWatcher();
                _winWatcher.Tapped += () => Dispatcher.BeginInvoke(new Action(() => OnWinKeyTap("poll")));
                _winWatcher.Start();
                LogHookState2($"WinKeyWatcher started  running={_winWatcher.IsRunning}");

                StartHookWatchdog();
            }

            ApplyWin10Geometry();
            ApplyAppearance(handle);
            LogHookState2($"width={Width:0}x{Height:0}  acrylic={_ui.Acrylic}  full={_ui.FullScreen}  " +
                          $"work={SystemParameters.WorkArea.Width:0}  hotkey={hotkey}  hook={_winHook?.IsHooked}");
        }

        /// <summary>
        /// 看门狗：定期探一次钩子是否还活着，掉线就重装。
        ///
        /// 必须自动修 —— 系统按 LowLevelHooksTimeout 摘钩、或别的软件在链外套一层把键吞掉，
        /// 这两种情况进程内都拿不到任何错误码，`IsHooked` 依旧返回 true，
        /// 表现就是「用着用着 Win 键忽然不灵了」，而且重启才能恢复。
        /// </summary>
        private void StartHookWatchdog()
        {
            _hookWatch = new System.Threading.Timer(_ =>
            {
                var hook = _winHook;
                if (hook == null || !hook.IsHooked) return;
                if (hook.Probe()) return;          // 还活着，什么都不做

                // 探不到就重装。重装会把我们排到钩子链最前面，
                // 顺便把「外面有个软件在吞键」这种也一并挤掉。
                Dispatcher.BeginInvoke(new Action(() => RebuildHook("watchdog")));
            }, null, 30000, 30000);
        }

        private void RebuildHook(string reason)
        {
            try
            {
                _winHook?.Dispose();
                _winHook = MakeHook();
                bool ok = _winHook.Install();
                bool alive = ok && _winHook.Probe();
                LogHookState2($"hook rebuilt ({reason})  installed={ok}  alive={alive}");
            }
            catch (Exception ex)
            {
                LogHookState2("hook rebuild failed: " + ex.Message);
            }
        }

        /// <summary>新建一个钩子并接好事件。日志一律走 UI 线程 —— 回调里有 300ms 超时，不能碰磁盘。</summary>
        private Native.WinKeyHook MakeHook()
        {
            var hook = new Native.WinKeyHook();
            hook.Tapped += () => Dispatcher.BeginInvoke(new Action(() => OnWinKeyTap("hook")));

            // Win 键每次按下/抬起都留痕：排查时能分清「键没到」和「到了但被判成组合键」
            hook.SawWinKey += info => Dispatcher.BeginInvoke(new Action(() =>
            {
                _lastHookSawTick = Environment.TickCount64;
                LogHookState2($"hook saw {info}");
            }));

            return hook;
        }

        /// <summary>把 Win 键接管结果写进日志，出问题时一眼能看出来。</summary>
        private static void LogHookState(bool hooked)
        {
            try
            {
                System.IO.Directory.CreateDirectory(LayoutStore.Directory);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(LayoutStore.Directory, "hook.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  WinKeyHook={(hooked ? "installed" : "FAILED")}  build={Environment.OSVersion.Version}\n");
            }
            catch { }
        }

        /// <summary>
        /// 贴左下角。全屏时宽高都等于工作区；非全屏时用上次拖出来的宽高，底边贴住工作区底部。
        /// Win10 的开始菜单也是从底部往上长的，所以这里对齐的是底边而不是顶边。
        /// </summary>
        private void ApplyWin10Geometry()
        {
            Rect work = SystemParameters.WorkArea;

            double w = _ui.FullScreen
                ? work.Width
                : Math.Clamp(_ui.PanelWidth > 0 ? _ui.PanelWidth : 560,
                             MinPanelWidth, Math.Max(MinPanelWidth, work.Width));

            double h = _ui.FullScreen
                ? work.Height
                : Math.Clamp(_ui.PanelHeight > 0 ? _ui.PanelHeight : work.Height,
                             MinPanelHeight, work.Height);

            Width = w;
            Height = h;
            Left = work.Left;
            Top = work.Top + (work.Height - h);

            // 底图抓的是整块工作区，窗口尺寸怎么变都还在图内 —— 只要把它挪对位置。
            // 早先这里直接清空底图，所以「切一次全屏毛玻璃就没了」，直到下次唤出才回来。
            UpdateGlassPlacement();
        }

        /// <summary>
        /// 把垫底的桌面图对到屏幕上：图是整块工作区，窗口只是它的一个子矩形，
        /// 所以这里按「窗口在屏幕坐标系里的偏移」反向平移，再裁到窗口边界。
        /// 尺寸、位置一变就得重算 —— 否则全屏时画面会整体错开一截。
        /// </summary>
        private void UpdateGlassPlacement()
        {
            if (GlassImage == null) return;

            Rect work = SystemParameters.WorkArea;

            GlassImage.HorizontalAlignment = HorizontalAlignment.Left;
            GlassImage.VerticalAlignment = VerticalAlignment.Top;
            GlassImage.Width = Math.Max(1, work.Width);
            GlassImage.Height = Math.Max(1, work.Height);

            var shift = GlassImage.RenderTransform as TranslateTransform;
            if (shift == null)
            {
                shift = new TranslateTransform();
                GlassImage.RenderTransform = shift;
            }

            shift.X = -(Left - work.Left);
            shift.Y = -(Top - work.Top);
        }

        /// <summary>
        /// 应用外观：底色不透明度 + 毛玻璃强度。
        ///
        /// 毛玻璃这块不再用 DWM 亚克力：它的模糊半径是系统写死的，面板上那个百分比
        /// 只能改染色深浅，观感就只剩「开/关」两档。现在改成 —— 唤出时截一张背后的桌面，
        /// 按强度自己缩小+盒式模糊，当作垫底图像。强度才是连续的。
        /// 抓不到桌面（远程会话、锁屏切换等）才退回 DWM 亚克力。
        /// </summary>
        private void ApplyAppearance(IntPtr handle)
        {
            // 1) 面板底色：alpha 直接由「背景透明度」决定
            byte a = (byte)Math.Round(255 * Math.Clamp(_ui.BackOpacity, 0.15, 1.0));
            var back = new SolidColorBrush(Color.FromArgb(a, 0x1B, 0x1B, 0x1B));

            BoardSurface.Background = back;

            // 设置页 / 手机页是整页，底色必须跟板面一模一样，否则切页时会「暗一下」
            if (SettingsPage != null) SettingsPage.Background = back;
            if (PhonePage != null) PhonePage.Background = back;

            double s = Math.Clamp(_ui.BlurStrength, 0, 1);

            // 还没底图、而此刻整窗是透明的（首帧 / 刚启动还没淡入）—— 顺手抓一张。
            // 窗口不透明的时候绝对不能抓，抓到的会是自己。
            if (_ui.Acrylic && !Services.Glass.HasImage && RootGrid != null && RootGrid.Opacity < 0.05)
            {
                if (Services.Glass.Capture(handle))
                {
                    var g = Services.Glass.Current;
                    LogHookState2($"Glass capture (apply) {g.PixelWidth}x{g.PixelHeight}");
                }
                else LogHookState2("Glass capture (apply) failed");
            }

            // 2) 总开关关掉：什么模糊都不要，背后是清晰桌面
            if (!_ui.Acrylic)
            {
                HideGlass();
                Native.Acrylic.Off(handle);
                LogHookState2($"Acrylic=off  opacity={_ui.BackOpacity:0.00}  blur=0");
                return;
            }

            // 3) 自己模糊：强度连续可调
            if (s > 0.02 && Services.Glass.HasImage)
            {
                var bmp = Services.Glass.Render(s);
                if (bmp != null && GlassImage != null)
                {
                    UpdateGlassPlacement();
                    GlassImage.Source = bmp;
                    GlassImage.Visibility = Visibility.Visible;

                    // 自家的模糊已经铺满整窗，系统亚克力再糊一遍是纯浪费，而且会和垫图错位
                    Native.Acrylic.Off(handle);

                    LogHookState2($"Glass=on  strength={s:0.00}  bitmap={bmp.PixelWidth}x{bmp.PixelHeight}  " +
                                  $"opacity={_ui.BackOpacity:0.00}");
                    return;
                }
            }

            HideGlass();

            // 4) 强度为 0：不模糊，只保留半透明
            if (s <= 0.02)
            {
                Native.Acrylic.Off(handle);
                LogHookState2($"Glass=off  strength=0  opacity={_ui.BackOpacity:0.00}");
                return;
            }

            // 5) 有强度但没抓到桌面：退回 DWM 亚克力，至少还有糊，只是分不出档位
            byte tint = (byte)Math.Round(255 * (0.9 - 0.55 * s));
            uint tintAbgr = ((uint)tint << 24) | 0x001A1A1A;

            bool ok = Native.Acrylic.Enable(handle, tintAbgr);
            LogHookState2($"Acrylic={(ok ? "ok" : "failed")}  fallback  opacity={_ui.BackOpacity:0.00}  " +
                          $"blur={s:0.00}  tint={tint}");
        }

        private void HideGlass()
        {
            if (GlassImage == null) return;
            GlassImage.Source = null;
            GlassImage.Visibility = Visibility.Collapsed;
        }

        /// <summary>把外观相关的结果也写进同一份日志，方便排查「为什么看着不透明」。</summary>
        private static void LogHookState2(string line)
        {
            try
            {
                System.IO.Directory.CreateDirectory(LayoutStore.Directory);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(LayoutStore.Directory, "hook.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [pid {Environment.ProcessId}] {line}\n");
            }
            catch { }
        }

        // ================= 设置页 / 手机页 =================

        /// <summary>切到设置页（整块替换磁贴区），并把控件对到当前设置。</summary>
        private void ShowSettingsPage()
        {
            HidePhonePage();
            ShowPageOver(SettingsPage);
            SetRailActive(BtnAppearance, true);

            // 先挡住 ValueChanged，否则赋值时就会把「当前值」当成用户改动再写回去
            _appearanceReady = false;

            OpacitySlider.Value = Math.Round(Math.Clamp(_ui.BackOpacity, 0.15, 1.0) * 100);
            BlurSlider.Value = Math.Round(Math.Clamp(_ui.BlurStrength, 0, 1) * 100);
            FullScreenCheck.IsChecked = _ui.FullScreen;
            AcrylicCheck.IsChecked = _ui.Acrylic;

            SyncSizeControls();
            AutostartCheck.IsChecked = Services.Autostart.IsEnabled();

            UpdateAppearanceLabels();
            _appearanceReady = true;
        }

        /// <summary>
        /// 宽度/高度滑块与「铺满屏幕宽度」跟随窗口真实尺寸：
        /// 拖边缘改过大小之后，滑块必须跟着动，否则拖一下再进来滑块还停在旧位置。
        /// </summary>
        private void SyncSizeControls()
        {
            Rect work = SystemParameters.WorkArea;

            WidthSlider.Minimum = MinPanelWidth;
            WidthSlider.Maximum = Math.Max(MinPanelWidth, work.Width);
            HeightSlider.Minimum = MinPanelHeight;
            HeightSlider.Maximum = Math.Max(MinPanelHeight, work.Height);

            bool full = _ui.FullScreen;

            WidthSlider.Value = Math.Clamp(Width, MinPanelWidth, WidthSlider.Maximum);
            HeightSlider.Value = Math.Clamp(Height, MinPanelHeight, HeightSlider.Maximum);

            // 全屏时宽高由屏幕决定，滑块改成手动值没有意义
            WidthSlider.IsEnabled = !full;
            HeightSlider.IsEnabled = !full;
            FullWidthCheck.IsEnabled = !full;
            FullWidthCheck.IsChecked = Width >= work.Width - 1;

            if (WidthValue != null) WidthValue.Text = $"{Math.Round(Width)}";
            if (HeightValue != null) HeightValue.Text = $"{Math.Round(Height)}";
        }

        /// <summary>
        /// 显示整页时把磁贴区收起来。
        /// 页面底色是带 alpha 的，板面要是还摆在下面，磁贴会从页面后面透出来，
        /// 文字全叠在图标上 —— 之前就是这个样子。
        /// </summary>
        private void ShowPageOver(FrameworkElement page)
        {
            BoardSurface.Visibility = Visibility.Collapsed;
            page.Visibility = Visibility.Visible;
        }

        private static void ShowBoardSurface(UIElement board)
        {
            if (board != null) board.Visibility = Visibility.Visible;
        }

        private void HideSettingsPage(bool save = true)
        {
            bool wasOpen = SettingsPage.Visibility == Visibility.Visible;

            SettingsPage.Visibility = Visibility.Collapsed;
            if (wasOpen) ShowBoardSurface(BoardSurface);
            SetRailActive(BtnAppearance, false);

            if (wasOpen && save) _ui.Save();
        }

        private void HidePhonePage()
        {
            bool wasOpen = PhonePage.Visibility == Visibility.Visible;

            PhonePage.Visibility = Visibility.Collapsed;
            if (wasOpen) ShowBoardSurface(BoardSurface);
            SetRailActive(BtnPhone, false);
        }

        /// <summary>当前页对应的侧边栏按钮压亮，否则看不出自己在哪一页。</summary>
        private static void SetRailActive(Button button, bool active)
        {
            if (button == null) return;

            button.Background = active
                ? (Brush)Application.Current.Resources["HoverBack"]
                : Brushes.Transparent;
        }

        /// <summary>侧边栏「个性化」：没开就切过去，开着再点就回板面。</summary>
        private void BtnAppearance_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsPage.Visibility == Visibility.Visible) HideSettingsPage();
            else ShowSettingsPage();
        }

        private void PhoneBack_Click(object sender, RoutedEventArgs e)
        {
            HidePhonePage();
        }

        private void UpdateAppearanceLabels()
        {
            if (OpacityValue != null) OpacityValue.Text = $"{Math.Round(_ui.BackOpacity * 100)}%";
            if (BlurValue != null) BlurValue.Text = $"{Math.Round(_ui.BlurStrength * 100)}%";
        }

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_appearanceReady) return;

            _ui.BackOpacity = Math.Clamp(e.NewValue / 100.0, 0.15, 1.0);
            UpdateAppearanceLabels();
            ApplyAppearance(new WindowInteropHelper(this).Handle);
            QueueSettingsSave();
        }

        private void BlurSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_appearanceReady) return;

            _ui.BlurStrength = Math.Clamp(e.NewValue / 100.0, 0, 1);
            UpdateAppearanceLabels();
            ApplyAppearance(new WindowInteropHelper(this).Handle);
            QueueSettingsSave();
        }

        private void FullScreenCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_appearanceReady) return;

            bool want = FullScreenCheck.IsChecked == true;
            if (want == _ui.FullScreen) return;   // ToggleFullScreen 回写勾选框时别转圈

            ToggleFullScreen();

            _appearanceReady = false;
            SyncSizeControls();
            _appearanceReady = true;
        }

        private void AcrylicCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_appearanceReady) return;

            _ui.Acrylic = AcrylicCheck.IsChecked == true;

            // 从关到开时底图可能已经作废（关着的时候不抓图），
            // 这一刻窗口是可见的，抓到的会是自己 —— 交给下次唤出，这里先把状态落到外观上。
            ApplyAppearance(new WindowInteropHelper(this).Handle);
            QueueSettingsSave();

            SetStatus(_ui.Acrylic ? "毛玻璃已启用" : "毛玻璃已关闭，只剩半透明");
        }

        private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_appearanceReady || _ui.FullScreen) return;

            double w = Math.Clamp(e.NewValue, MinPanelWidth, SystemParameters.WorkArea.Width);
            _ui.PanelWidth = w;
            _ui.FullScreen = false;

            ApplyWin10Geometry();
            UpdateGroupLayout();

            _appearanceReady = false;
            SyncSizeControls();
            _appearanceReady = true;

            QueueSettingsSave();
        }

        private void HeightSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_appearanceReady || _ui.FullScreen) return;

            double h = Math.Clamp(e.NewValue, MinPanelHeight, SystemParameters.WorkArea.Height);
            _ui.PanelHeight = h;
            _ui.FullScreen = false;

            ApplyWin10Geometry();
            UpdateGroupLayout();

            _appearanceReady = false;
            SyncSizeControls();
            _appearanceReady = true;

            QueueSettingsSave();
        }

        private void FullWidthCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_appearanceReady || _ui.FullScreen) return;

            Rect work = SystemParameters.WorkArea;
            bool want = FullWidthCheck.IsChecked == true;
            bool isFull = Width >= work.Width - 1;
            if (want == isFull) return;

            if (want)
            {
                _restoreWidth = Width;
                _ui.PanelWidth = work.Width;
            }
            else
            {
                double w = Math.Max(MinPanelWidth, Math.Min(_restoreWidth, work.Width));
                _ui.PanelWidth = w;
            }

            _ui.FullScreen = false;
            ApplyWin10Geometry();
            UpdateGroupLayout();

            _appearanceReady = false;
            SyncSizeControls();
            _appearanceReady = true;

            QueueSettingsSave();
            SetStatus(want ? "磁贴铺满整个屏幕宽度" : "恢复窄版");
        }

        private void AutostartCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!_appearanceReady) return;

            bool want = AutostartCheck.IsChecked == true;
            bool ok = want ? Services.Autostart.Enable() : Services.Autostart.Disable();

            if (!ok)
            {
                _appearanceReady = false;
                AutostartCheck.IsChecked = Services.Autostart.IsEnabled();
                _appearanceReady = true;
                SetStatus("自启项写入失败");
                return;
            }

            SetStatus(want ? "已设为开机自启" : "已取消开机自启");
        }

        private void SettingsBack_Click(object sender, RoutedEventArgs e)
        {
            HideSettingsPage();
        }

        /// <summary>外观改动 250ms 内只落盘一次。</summary>
        private void QueueSettingsSave()
        {
            if (_settingsSaveTimer == null)
            {
                _settingsSaveTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _settingsSaveTimer.Tick += (s, e) =>
                {
                    _settingsSaveTimer.Stop();
                    _ui.Save();
                };
            }

            _settingsSaveTimer.Stop();
            _settingsSaveTimer.Start();
        }

        // ================= 右边缘改宽度 =================

        private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleFullWidth();
                e.Handled = true;
                return;
            }

            _resizing = true;
            _resizeStartX = e.GetPosition(this).X;
            _resizeStartWidth = Width;
            if (Width < SystemParameters.WorkArea.Width - 1) _restoreWidth = Width;
            ResizeGrip.CaptureMouse();
            e.Handled = true;
        }

        private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizing) return;

            double work = SystemParameters.WorkArea.Width;
            double target = _resizeStartWidth + (e.GetPosition(this).X - _resizeStartX);
            Width = Math.Clamp(target, MinPanelWidth, Math.Max(MinPanelWidth, work));
            e.Handled = true;
        }

        private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_resizing) return;
            _resizing = false;
            ResizeGrip.ReleaseMouseCapture();

            _ui.PanelWidth = Width;
            _ui.FullScreen = false;
            _ui.Save();
            e.Handled = true;
        }

        // ================= 上边缘改高度 =================

        private void ResizeTop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleFullScreen();
                e.Handled = true;
                return;
            }

            _resizingH = true;
            _resizeStartY = PointToScreen(e.GetPosition(this)).Y;
            _resizeStartHeight = Height;
            ResizeGripTop.CaptureMouse();
            e.Handled = true;
        }

        private void ResizeTop_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_resizingH) return;

            Rect work = SystemParameters.WorkArea;
            double dy = PointToScreen(e.GetPosition(this)).Y - _resizeStartY;

            // 顶部往下拖 = 变矮，底边始终贴住工作区底部
            double h = Math.Clamp(_resizeStartHeight - dy, MinPanelHeight, work.Height);
            Height = h;
            Top = work.Top + (work.Height - h);
            e.Handled = true;
        }

        private void ResizeTop_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_resizingH) return;
            _resizingH = false;
            ResizeGripTop.ReleaseMouseCapture();

            _ui.PanelHeight = Height;
            _ui.FullScreen = false;
            _ui.Save();
            UpdateGroupLayout();
            e.Handled = true;
        }

        /// <summary>全屏 ↔ 上次的尺寸。</summary>
        private void ToggleFullScreen()
        {
            _ui.FullScreen = !_ui.FullScreen;

            if (!_ui.FullScreen)
            {
                Rect work = SystemParameters.WorkArea;

                if (_ui.PanelWidth <= 0 || _ui.PanelWidth >= work.Width - 1)
                    _ui.PanelWidth = Math.Min(960, work.Width);

                if (_ui.PanelHeight <= 0 || _ui.PanelHeight >= work.Height - 1)
                    _ui.PanelHeight = Math.Min(900, work.Height);
            }

            _ui.Save();
            ApplyWin10Geometry();
            UpdateGroupLayout();

            if (FullScreenCheck != null) FullScreenCheck.IsChecked = _ui.FullScreen;
            SetStatus(_ui.FullScreen ? "磁贴板已全屏" : "已退出全屏，可拖边框调整大小");
        }

        /// <summary>双击右边缘：满屏宽度 ↔ 上次的宽度。</summary>
        private void ToggleFullWidth()
        {
            double work = SystemParameters.WorkArea.Width;

            if (Width >= work - 1)
            {
                Width = Math.Max(MinPanelWidth, _restoreWidth);
                SetStatus("恢复窄版");
            }
            else
            {
                _restoreWidth = Width;
                Width = work;
                SetStatus("磁贴铺满整个屏幕宽度");
            }

            _ui.PanelWidth = Width;
            _ui.Save();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 尺寸在第一次显示之前就定好。否则首次 Show 会用 XAML 里的 560 渲染一帧，
            // 下一帧再撑到满宽 —— 那一下就是主人看到的「打开时闪一下」。
            ApplyWin10Geometry();

            _layout = LayoutStore.Load();
            await _catalog.LoadAsync();

            if (_layout == null || _layout.TileCount == 0)
            {
                GenerateFromCatalog();
                SetStatus($"按本机 {_catalog.All.Count} 个应用生成了初始板面");
            }

            RenderAll();

            // 应用库等到点开时再填：四百多行的图标没必要每次启动都取一遍

            // 资源管理器右键菜单里那两条命令挂在当前 exe 上。程序换了目录会自动改指。
            if (!IsSelfTestRun() && !Native.ShellMenu.IsCurrent())
            {
                bool ok = Native.ShellMenu.Install();
                LogHookState2($"shell menu pin: {(ok ? "installed" : "FAILED")}");
            }

            // 右键菜单起进程来固定东西：第一次运行时这个实例就是干活的那个
            HandlePinRequests();

            // 开机自启时不主动弹出来，等按 Win 键
            if (HasFlag("--autostart")) Hide();

            // 调试/验收用：--layoutdump <txt> 打印列数与每组坐标
            string dumpPath = GetArg("--layoutdump");
            if (dumpPath != null) { DumpLayout(dumpPath); return; }

            // 调试/验收用：--grouptest <txt> 跑一遍「抓起分组→吸附格位→挤开邻居」
            string groupTestPath = GetArg("--grouptest");
            if (groupTestPath != null) { RunGroupTest(groupTestPath); return; }

            // 调试/验收用：--dragtest <txt> 自己跑一遍「拿起→移动→落位」
            string dragTestPath = GetArg("--dragtest");
            if (dragTestPath != null) { RunDragTest(dragTestPath); return; }

            // 调试/验收用：--fadetest <txt> 采样一遍淡入淡出的透明度曲线
            string fadeTestPath = GetArg("--fadetest");
            if (fadeTestPath != null) { RunFadeTest(fadeTestPath); return; }

            // 调试/验收用：--showprobe <txt> 真实订阅唤出信号并逐 10ms 采样，
            // 与外部录屏对时间轴用 —— 排「唤出瞬间闪一下」
            string showProbePath = GetArg("--showprobe");
            if (showProbePath != null) { RunShowProbe(showProbePath); return; }

            // 调试/验收用：--glasstest <txt> 验毛玻璃强度是否连续生效
            string glassTestPath = GetArg("--glasstest");
            if (glassTestPath != null) { RunGlassTest(glassTestPath); return; }

            // 调试/验收用：--shotglass <png> 走完「隐藏→抓桌面→唤出」再截图
            string glassShot = GetArg("--shotglass");
            if (glassShot != null) { await CaptureGlassShot(glassShot); return; }

            // 调试/验收用：--shotdrag <png> 拍一张「拖到组外」中途的样子
            string dragShot = GetArg("--shotdrag");
            if (dragShot != null) { await CaptureDragShot(dragShot); return; }

            // 调试/验收用：--shotgroupdrag <png> 拍一张「分组拖到别处」中途的样子
            string groupDragShot = GetArg("--shotgroupdrag");
            if (groupDragShot != null) { await CaptureGroupDragShot(groupDragShot); return; }

            // 调试/验收用：--shotphoneonly <png> 只渲染手机面板本身
            string phoneOnly = GetArg("--shotphoneonly");
            if (phoneOnly != null) { await CapturePhoneOnly(phoneOnly); return; }

            // 调试/验收用：--shotappearance <png> 切到设置页后截图
            string appearanceShot = GetArg("--shotappearance");
            if (appearanceShot != null)
            {
                ShowSettingsPage();
                await Task.Delay(400);
                LogHookState2($"Appearance w={SettingsPage.ActualWidth:0} h={SettingsPage.ActualHeight:0} " +
                              $"opacity={OpacitySlider.Value:0} blur={BlurSlider.Value:0} full={FullScreenCheck.IsChecked}");

                // 顺手把浮层按原尺寸单独存一张，整屏缩略图看不清 11px 的说明文字
                RevealForCapture();
                SettingsPage.UpdateLayout();
                CaptureElement(SettingsPage,
                    System.IO.Path.Combine(System.IO.Path.GetDirectoryName(appearanceShot) ?? ".",
                                           System.IO.Path.GetFileNameWithoutExtension(appearanceShot) + "-only.png"));

                await CaptureAndExit(appearanceShot);
                return;
            }

            // 调试/验收用：--menudump <txt> 把右键菜单的条目树导出来
            string menuDumpPath = GetArg("--menudump");
            if (menuDumpPath != null) { DumpMenus(menuDumpPath); return; }

            // 调试/验收用：--fullscreentest <txt> 开关全屏后毛玻璃还在不在
            string fsTestPath = GetArg("--fullscreentest");
            if (fsTestPath != null) { RunFullScreenTest(fsTestPath); return; }

            // 调试/验收用：--settingtest <txt> 逐个改设置项再断言，验「设置页真的能改」
            string settingTestPath = GetArg("--settingtest");
            if (settingTestPath != null) { RunSettingTest(settingTestPath); return; }

            // 调试/验收用：--menurender <txt> 真把右键菜单打开，再从视觉树里核对滚动条样式
            string menuRenderPath = GetArg("--menurender");
            if (menuRenderPath != null) { RunMenuRenderTest(menuRenderPath); return; }

            // 调试/验收用：--shellmenu <txt> 装一遍资源管理器右键菜单并读回来
            string shellMenuPath = GetArg("--shellmenu");
            if (shellMenuPath != null) { RunShellMenuTest(shellMenuPath); return; }

            // 调试/验收用：--pintest <txt> 跑一遍「把文件/文件夹固定成磁贴」
            string pinTestPath = GetArg("--pintest");
            if (pinTestPath != null) { RunPinTest(pinTestPath); return; }

            // 调试/验收用：--appstest <txt> 验应用库的字母分组与「拖出来变磁贴」
            string appsTestPath = GetArg("--appstest");
            if (appsTestPath != null) { RunAppsTest(appsTestPath); return; }

            // 调试/验收用：--foldtest <txt> 验分组折叠高度、未命名判定、磁贴让位只走一步
            string foldTestPath = GetArg("--foldtest");
            if (foldTestPath != null) { RunFoldTest(foldTestPath); return; }

            // 调试/验收用：--shotapps <png> 打开应用库截图
            string appsShot = GetArg("--shotapps");
            if (appsShot != null) { await CaptureAppsShot(appsShot); return; }

            // 调试/验收用：--groupshot <png> 看一眼未命名分组的标题隐藏与折叠态
            string groupShot = GetArg("--groupshot");
            if (groupShot != null) { await CaptureGroupShot(groupShot); return; }

            // 调试/验收用：--menushot <png> 打开磁贴右键菜单后抓屏
            // （菜单住在 Popup 里，不在窗口可视树上，RenderTargetBitmap 抓不到）
            string menuShot = GetArg("--menushot");
            if (menuShot != null) { await CaptureMenuShot(menuShot); return; }

            // 调试/验收用：--shotphone <png> 打开手机连接面板后截图
            string phoneShot = GetArg("--shotphone");
            if (phoneShot != null)
            {
                BtnPhone_Click(this, null);
                await Task.Delay(400);
                LogHookState2($"PhonePanel w={PhonePage.ActualWidth:0} h={PhonePage.ActualHeight:0} " +
                              $"bodyW={PhoneBody.ActualWidth:0} vis={PhonePage.Visibility} " +
                              $"device={_phoneCard?.DeviceName} actions={_phoneCard?.Actions.Count} " +
                              $"recents={_phoneCard?.Recents.Count}");
                await CaptureAndExit(phoneShot);
                return;
            }

            // 调试/验收用：--shot <路径> 让应用把自身界面渲染成 PNG 后退出
            string shotPath = GetShotPath();
            if (shotPath != null) await CaptureAndExit(shotPath);

            // 双击 exe 启动：窗口是被 StartupUri 直接摆上来的，还停在 Opacity 0 ——
            // 看着像"双击没反应"，而且这时跳过了 ShowBoard，毛玻璃底图也抓不到。
            // 先 Hide 让出屏幕（否则抓到的桌面就是自己），再走一遍正常的唤出。
            if (!HasFlag("--autostart") && !IsSelfTestRun())
            {
                Hide();
                ShowBoard();
            }
        }

        private static bool HasFlag(string flag)
        {
            return Array.Exists(Environment.GetCommandLineArgs(),
                a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>命令行里带 --xxx 开关（--autostart、--pin 除外）就是自检运行。</summary>
        private static bool IsSelfTestRun()
        {
            foreach (string a in Environment.GetCommandLineArgs())
            {
                if (a.Length > 2 && a.StartsWith("--", StringComparison.Ordinal) &&
                    !a.Equals("--autostart", StringComparison.OrdinalIgnoreCase) &&
                    !a.Equals("--pin", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string GetShotPath()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--shot", StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static string GetArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        /// <summary>
        /// 拖拽自检：不依赖真实鼠标，直接把「拿起 → 移动 → 落位」这条链走一遍，
        /// 把每一步的结果写出来。鼠标路由之外的所有环节都被覆盖。
        /// </summary>
        private async void RunDragTest(string path)
        {
            await Task.Delay(1500);   // 等布局与图标稳定

            // 这个测试会真的挪磁贴、真的新建分组，收尾必须整份回滚 ——
            // 否则跑一次自检，用户板上的坐标就被测试改掉了
            var snapshot = LayoutStore.Clone(_layout);

            var lines = new List<string>();
            try
            {
                if (_layout == null)
                {
                    lines.Add("FAIL 没有布局");
                }
                else
                {
                    lines.Add("==== 布局合法性 ====");
                    foreach (var g in _layout.Groups)
                        lines.Add($"{g.Name}  列{TileGrid.UsedColumns(g)} 行{TileGrid.UsedRows(g)}");
                    lines.Add("overlap = " + (HasOverlap() ? "存在重叠 ✘" : "无重叠 ✔"));
                    lines.Add("groups  = " + _layout.Groups.Count);

                    // ---- 同组拖拽：拖到另一块磁贴的格位上 ----
                    var group = _layout.Groups.FirstOrDefault(g => !g.Collapsed && g.Tiles.Count >= 3);
                    if (group == null)
                    {
                        lines.Add("SKIP 没有含 3 个以上磁贴的分组");
                    }
                    else
                    {
                        var tile = group.Tiles[0];
                        var onto = group.Tiles[2];
                        lines.Add("");
                        lines.Add("==== 同组拖拽 ====");
                        lines.Add($"before   = {tile.Name} @ ({tile.Column},{tile.Row})");
                        lines.Add($"目标磁贴 = {onto.Name} @ ({onto.Column},{onto.Row})");

                        int oldCol = tile.Column, oldRow = tile.Row;
                        var ontoBorder = FindTileBorder(onto);

                        if (ontoBorder == null)
                        {
                            lines.Add("FAIL 找不到目标磁贴的可视元素");
                        }
                        else
                        {
                            DragOnto(tile, group, ontoBorder);
                            lines.Add($"after    = {tile.Name} @ ({tile.Column},{tile.Row})");
                            lines.Add("结果     = " +
                                (tile.Column != oldCol || tile.Row != oldRow ? "落位已改变 ✔" : "位置没动 ✘"));
                            lines.Add("overlap  = " + (HasOverlap() ? "存在重叠 ✘" : "无重叠 ✔"));
                        }
                    }

                    // ---- 拖到组外：应当自动新建一个分组 ----
                    var source = _layout.Groups.FirstOrDefault(g => !g.Collapsed && g.Tiles.Count >= 2);
                    if (source != null)
                    {
                        int groupsBefore = _layout.Groups.Count;
                        var tile = source.Tiles[0];

                        lines.Add("");
                        lines.Add("==== 拖到组外 ====");

                        _dragItem = tile;
                        _dragGroup = source;
                        _dragBorder = FindTileBorder(tile);
                        _mouseDown = true;
                        StartDrag(_dragBorder.TranslatePoint(new Point(20, 20), this));

                        // 右下角是磁贴内容区之外的空白
                        var outside = new Point(ActualWidth - 12, ActualHeight - 12);
                        UpdateDrag(outside);
                        lines.Add("newGroupHint = " + (_dropIntoNewGroup ? "已显示 ✔" : "未显示 ✘"));

                        EndDrag();
                        lines.Add($"分组数   = {groupsBefore} → {_layout.Groups.Count}");
                        lines.Add("结果     = " +
                            (_layout.Groups.Count == groupsBefore + 1 ? "已新建分组 ✔" : "没有新建 ✘"));
                        lines.Add("分组名   = " + string.Join(" | ", _layout.Groups.Select(g => g.Name)));
                    }

                    lines.Add("");
                    lines.Add("==== 全部磁贴坐标 ====");
                    foreach (var g in _layout.Groups)
                        lines.Add($"[{g.Name}] " +
                            string.Join("  ", g.Tiles.Select(t => $"{t.Name}@({t.Column},{t.Row})")));
                }
            }
            catch (Exception ex)
            {
                lines.Add("EX " + ex.GetType().Name + ": " + ex.Message);
            }

            if (RestoreLayout(snapshot))
                lines.Add("已还原板面 = " + DescribeGroups());

            System.IO.File.WriteAllLines(path, lines);
            Application.Current.Shutdown();
        }

        /// <summary>把布局退回自检前的快照，并落盘。</summary>
        private bool RestoreLayout(BoardLayout snapshot)
        {
            if (snapshot == null) return false;

            try
            {
                _layout = snapshot;
                Save();
                RenderAll();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>自检：把两个右键菜单的条目树导成文本，确认真加进去了。</summary>
        private void DumpMenus(string path)
        {
            var lines = new List<string>();

            void Walk(ItemCollection items, int depth)
            {
                foreach (object o in items)
                {
                    if (o is Separator) { lines.Add(new string(' ', depth * 2) + "---"); continue; }
                    if (o is MenuItem mi)
                    {
                        string head = mi.Header?.ToString() ?? "(无标题)";
                        lines.Add(new string(' ', depth * 2) + "- " + head);
                        if (mi.Items.Count > 0) Walk(mi.Items, depth + 1);
                    }
                }
            }

            try
            {
                lines.Add("==== 菜单样式 ====");
                var menuScrollKey = new ComponentResourceKey(typeof(FrameworkElement), "MenuScrollViewer");
                var scrollStyle = Application.Current.TryFindResource(menuScrollKey) as Style;
                bool ours = scrollStyle != null &&
                            scrollStyle.Setters.OfType<Setter>().Any(s =>
                                s.Property == ScrollViewer.TemplateProperty);
                lines.Add("菜单滚动条模板被覆盖 = " + (ours ? "✔ 走 Win10 细轨" : "✘ 还是系统默认"));

                lines.Add("==== 板面空白右键 ====");
                Walk(BuildBoardMenu().Items, 0);

                lines.Add("");
                lines.Add("==== 磁贴右键 ====");

                var firstTile = _layout?.Groups.SelectMany(g => g.Tiles).FirstOrDefault();
                var firstGroup = _layout?.Groups.FirstOrDefault(g =>
                    firstTile != null && g.Tiles.Contains(firstTile));

                if (firstTile == null)
                {
                    lines.Add("SKIP 板上没有磁贴");
                }
                else
                {
                    lines.Add("目标磁贴: " + firstTile.Name);
                    Walk(BuildTileMenu(firstTile, firstGroup).Items, 0);
                }
            }
            catch (Exception ex)
            {
                lines.Add("EX " + ex.GetType().Name + ": " + ex.Message);
            }

            System.IO.File.WriteAllLines(path, lines);
            Application.Current.Shutdown();
        }

        /// <summary>自检：把磁盘路径做成磁贴这条链路跑一遍（起名、图标、落位、去重、落盘）。</summary>
        private void RunPinTest(string path)
        {
            var lines = new List<string>();

            var known = new HashSet<TileItem>(
                _layout?.Groups.SelectMany(g => g.Tiles) ?? Enumerable.Empty<TileItem>());

            // 添加磁贴会把组撑宽，Normalize 顺手把后面的组挤走 —— 坐标也得记下来一起还原
            var groupPos = new Dictionary<TileGroup, (int Col, int Row)>();
            foreach (var g in _layout?.Groups ?? new List<TileGroup>())
                if (g != null) groupPos[g] = (g.Column, g.Row);

            try
            {
                var probes = new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    typeof(MainWindow).Assembly.Location
                }
                .Where(p => !string.IsNullOrWhiteSpace(p) &&
                            (System.IO.File.Exists(p) || System.IO.Directory.Exists(p)))
                .ToList();

                lines.Add("候选路径 = " + probes.Count);
                lines.Add("添加前磁贴数 = " + (_layout?.Groups.Sum(g => g.Tiles.Count) ?? 0));

                AddPathTiles(probes, _layout?.Groups.FirstOrDefault());

                lines.Add("添加后磁贴数 = " + (_layout?.Groups.Sum(g => g.Tiles.Count) ?? 0));

                foreach (string probe in probes)
                {
                    var t = _layout.Groups.SelectMany(g => g.Tiles).FirstOrDefault(x =>
                        string.Equals(x.Aumid, probe, StringComparison.OrdinalIgnoreCase));

                    bool icon = t != null && _catalog.GetIcon(probe, 64) != null;

                    lines.Add(t == null
                        ? $"MISS  {probe}"
                        : $"OK    {probe}\n      name={t.Name}  icon={(icon ? "有" : "无")}  " +
                          $"pos=({t.Column},{t.Row})  size={t.Size}");
                }

                // 同一个路径再来一次，应该被跳过而不是加出第二块
                int before = _layout.Groups.Sum(g => g.Tiles.Count);
                AddPathTiles(probes.Take(1), _layout.Groups.FirstOrDefault());
                lines.Add("重复添加：" + (before == _layout.Groups.Sum(g => g.Tiles.Count) ? "跳过 ✔" : "重复了 ✘"));

                var reload = LayoutStore.Load();
                bool persisted = reload != null && probes.Any(p =>
                    reload.Groups.SelectMany(g => g.Tiles).Any(x =>
                        string.Equals(x.Aumid, p, StringComparison.OrdinalIgnoreCase)));
                lines.Add("落盘检查 = " + (persisted ? "已写入 ✔" : "未写入 ✘"));
            }
            catch (Exception ex)
            {
                lines.Add("EX " + ex.GetType().Name + ": " + ex.Message);
            }

            // 自检不能把测试磁贴留在用户板上：删掉所有新加的，再存回去
            try
            {
                foreach (var g in _layout.Groups)
                    g.Tiles.RemoveAll(t => !known.Contains(t));

                foreach (var kv in groupPos)
                {
                    kv.Key.Column = kv.Value.Col;
                    kv.Key.Row = kv.Value.Row;
                }

                Save();
                RenderAll();
                lines.Add("已还原板面，当前磁贴数 = " + _layout.Groups.Sum(g => g.Tiles.Count));
            }
            catch (Exception ex)
            {
                lines.Add("还原失败 " + ex.Message);
            }

            System.IO.File.WriteAllLines(path, lines);
            Application.Current.Shutdown();
        }

        /// <summary>
        /// 分组折叠 / 未命名判定 / 磁贴让位的自检。全部在模型层跑，不碰真布局 ——
        /// 这几条要验的是「坐标怎么变」，和屏幕上画在哪没有关系，也就不需要等首帧。
        /// </summary>
        private void RunFoldTest(string path)
        {
            var lines = new List<string>();

            // ---- 1. 新建分组的默认名与「未命名」判定 ----
            var fresh = new TileGroup();
            lines.Add($"新组默认名 = 「{fresh.Name}」 未命名={fresh.IsUnnamed}  " +
                      (fresh.Name == TileGroup.DefaultName && fresh.IsUnnamed ? "✔" : "✘"));

            fresh.Name = "常用";
            lines.Add($"起名后 未命名={fresh.IsUnnamed}  " + (!fresh.IsUnnamed ? "✔ 标题常驻" : "✘"));

            fresh.Name = "   ";
            lines.Add($"清空后 未命名={fresh.IsUnnamed}  " + (fresh.IsUnnamed ? "✔ 回到隐藏态" : "✘"));

            // ---- 2. 折叠高度 ----
            var g = new TileGroup();
            var a = new TileItem { Aumid = "a", Name = "A", Column = 0, Row = 0 };
            var b = new TileItem { Aumid = "b", Name = "B", Column = 2, Row = 0 };
            var c = new TileItem { Aumid = "c", Name = "C", Column = 4, Row = 0 };
            g.Tiles.AddRange(new[] { a, b, c });

            double expanded = GroupGrid.PixelHeight(g);
            g.Collapsed = true;
            double folded = GroupGrid.PixelHeight(g);
            g.Collapsed = false;

            lines.Add($"展开高={expanded:0} 折叠高={folded:0} (标题带={GroupGrid.HeaderHeight:0})  " +
                      (Math.Abs(folded - GroupGrid.HeaderHeight) < 0.1 && expanded > folded ? "✔" : "✘"));

            // ---- 3. 让位只走一步：压上去应该交换 ----
            TileGrid.Place(g, a, 2, 0);
            lines.Add($"A 压到 B → A=({a.Column},{a.Row}) B=({b.Column},{b.Row})  " +
                      (a.Column == 2 && b.Column == 0
                          ? "✔ 交换，B 去了 A 的原位"
                          : $"✘ 期望 A(2,0) B(0,0)"));

            // ---- 4. 继续拖：快照回滚后重算，B 必须归位 ----
            a.Column = 0; a.Row = 0;
            b.Column = 2; b.Row = 0;
            c.Column = 4; c.Row = 0;

            TileGrid.Place(g, a, 4, 0);
            lines.Add($"A 再压到 C → A=({a.Column},{a.Row}) B=({b.Column},{b.Row}) C=({c.Column},{c.Row})  " +
                      (a.Column == 4 && b.Column == 2 && c.Column == 0
                          ? "✔ B 已归位，C 换到 A 的原位"
                          : "✘ 有磁贴被一路推走了"));

            // ---- 5. 从组外插进来（没有原位可换），也只能让一格 ----
            var incoming = new TileItem { Aumid = "n", Name = "N", Column = -1, Row = -1 };
            g.Tiles.Add(incoming);

            TileGrid.Place(g, incoming, 2, 0);
            lines.Add($"新磁贴压住 B → N=({incoming.Column},{incoming.Row}) B=({b.Column},{b.Row})  " +
                      (incoming.Column == 2 && incoming.Row == 0 && !(b.Column == 2 && b.Row == 0)
                          ? "✔ B 让开一格"
                          : "✘ B 没让开"));

            // ---- 6. 越界 ----
            int maxCol = g.Tiles.Max(t => t.Column + t.SpanX);
            lines.Add($"最右到第 {maxCol} 列（上限 {TileGrid.Columns}）  " +
                      (maxCol <= TileGrid.Columns ? "✔" : "✘ 越界"));

            // 重叠检查：让位之后组里不该有两块压在一起
            bool overlap = false;
            for (int i = 0; i < g.Tiles.Count; i++)
                for (int j = i + 1; j < g.Tiles.Count; j++)
                {
                    var x = g.Tiles[i];
                    var y = g.Tiles[j];
                    if (x.Column < y.Column + y.SpanX && y.Column < x.Column + x.SpanX &&
                        x.Row < y.Row + y.SpanY && y.Row < x.Row + x.SpanY) overlap = true;
                }
            lines.Add("无重叠 = " + (overlap ? "✘ 有磁贴叠住了" : "✔"));

            System.IO.File.WriteAllLines(path, lines);
            Application.Current.Shutdown();
        }

        /// <summary>
        /// 应用库自检：
        ///   ① 面板能开、行数与目录对得上；
        ///   ② 每一行的字母组和 NameIndex 算出来的一致，标题不重复、不跳序；
        ///   ③ 从列表里拖一个应用进已有分组，磁贴真的落进去；
        ///   ④ 拖到组外空白，真的长出带名字的新分组。
        /// 跑完把测试磁贴和分组全撤掉，不污染用户的板面。
        /// </summary>
        private async void RunAppsTest(string path)
        {
            // 必须等第一帧布局走完：分组面板的 RenderSize 出来之前，
            // 「这一点落在哪个组」根本无从判断（全是 0x0）
            await Task.Delay(1400);

            var lines = new List<string>();

            var knownTiles = new HashSet<TileItem>(
                _layout?.Groups.SelectMany(g => g.Tiles) ?? Enumerable.Empty<TileItem>());
            var knownGroups = new List<TileGroup>(_layout?.Groups ?? new List<TileGroup>());
            var groupPos = new Dictionary<TileGroup, (int Col, int Row)>();
            foreach (var g in knownGroups)
                if (g != null) groupPos[g] = (g.Column, g.Row);

            try
            {
                lines.Add($"目录应用数 = {_catalog.All.Count}");

                ShowAppsPanel();
                RevealForCapture();
                UpdateLayout();

                var rows = AppsList.ItemsSource as List<AppRow> ?? new List<AppRow>();
                var headers = rows.Where(r => r.IsHeader).ToList();
                var apps = rows.Where(r => !r.IsHeader).ToList();

                lines.Add($"面板可见 = {AppsPanel.Visibility}  w={AppsPanel.ActualWidth:0}  " +
                          $"行数={rows.Count}  应用={apps.Count}  字母段={headers.Count}  " +
                          $"已在板上={apps.Count(a => a.IsPinned)}");

                // 字母段必须严格递增（# 在最前），不能有重复段
                bool ordered = true;
                int lastRank = -1;
                foreach (var h in headers)
                {
                    int rank = NameIndex.Rank(h.Letter);
                    if (rank <= lastRank) ordered = false;
                    lastRank = rank;
                }
                lines.Add("字母段递增 = " + (ordered ? "✔" : "✘"));
                lines.Add("字母顺序   = " + string.Join(" ", headers.Select(h => h.HeaderText)));

                // 每一行的归属必须和名字算出来的一致
                int mismatched = apps.Count(a => NameIndex.Group(a.Name) != a.Letter);
                lines.Add("分组错配   = " + mismatched + (mismatched == 0 ? " ✔" : " ✘"));

                lines.Add("--- 前 24 行（[X] 是字母标题） ---");
                foreach (var r in rows.Take(24))
                    lines.Add(r.IsHeader ? $"[{r.HeaderText}]" : $"  {r.Letter}  {r.Name}");

                // 中文名单独抽几个，确认走的是拼音首字母而不是码位
                lines.Add("--- 中文抽样 ---");
                foreach (string probe in new[] { "计算器", "微信", "钉钉", "记事本", "网易云音乐", "阿里云盘" })
                {
                    var hit = apps.FirstOrDefault(a =>
                        a.Name.IndexOf(probe, StringComparison.CurrentCultureIgnoreCase) >= 0);
                    lines.Add(hit == null
                        ? $"  {probe} = 本机没有"
                        : $"  {probe} → {hit.Letter}  （{hit.Name}）");
                }

                if (_layout == null || _layout.Groups.Count == 0)
                {
                    lines.Add("板面没有分组，跳过拖放测试");
                }
                else
                {
                    // ③ 拖进已有分组：挑一个还没在板上的应用
                    var known = new HashSet<string>(
                        _layout.Groups.SelectMany(g => g.Tiles).Select(t => t.Aumid),
                        StringComparer.OrdinalIgnoreCase);

                    AppRow free = apps.FirstOrDefault(a => !known.Contains(a.Aumid));
                    if (free == null)
                    {
                        lines.Add("列表里没有未固定的应用，跳过拖放测试");
                    }
                    else
                    {
                        AppRow row = free;
                        AppRow row2 = apps.LastOrDefault(a => !known.Contains(a.Aumid) &&
                                                              !string.Equals(a.Aumid, row.Aumid));

                        // 目标组：第二个组（没有就用第一个）
                        TileGroup group = _layout.Groups.Count > 1 ? _layout.Groups[1] : _layout.Groups[0];
                        int groupIndex = _layout.Groups.IndexOf(group);
                        var block = GroupsHost.Children.Count > groupIndex
                            ? GroupsHost.Children[groupIndex] as FrameworkElement : null;

                        if (block == null)
                        {
                            lines.Add("找不到分组块，跳过拖放测试");
                        }
                        else
                        {
                            Rect bounds = block.TransformToAncestor(this)
                                               .TransformBounds(new Rect(block.RenderSize));
                            var dropPoint = new Point(bounds.X + bounds.Width / 2,
                                                      bounds.Y + bounds.Height / 2);

                            int before = group.Tiles.Count;
                            StartLibraryDrag(row, dropPoint);
                            lines.Add($"拖起「{row.Name}」→ 面板保持可见 = " +
                                      (AppsPanel.Visibility == Visibility.Visible ? "✔" : "✘"));

                            // 指针还压在列表上时不该解析落点：面板盖着板面左侧，
                            // 底下那几个分组看不见，在那儿落位等于对着空气放磁贴
                            Point panelCenter = AppsPanel.TranslatePoint(
                                new Point(AppsPanel.ActualWidth / 2, AppsPanel.ActualHeight / 2), this);

                            _dropIntoNewGroup = true;   // 假装刚才滑到过空白处
                            UpdateDrag(panelCenter);
                            lines.Add("  拖到列表上方 → 落点被取消 = " +
                                      (_dropIntoNewGroup ? "✘ 仍按组外处理" : "✔"));

                            // 落点判定走模型坐标，这里顺手核对它认的是不是目标那一组
                            bool resolved = TryResolveDrop(dropPoint, out TileGroup hitGroup,
                                                           out int hitCol, out int hitRow);
                            lines.Add($"解析目标   = {(resolved ? "#" + _layout.Groups.IndexOf(hitGroup) : "组外")} " +
                                      $"(期望 #{groupIndex})  col={hitCol} row={hitRow}");

                            UpdateDrag(dropPoint);
                            EndDrag();

                            var landed = _layout.Groups.SelectMany(g => g.Tiles)
                                .FirstOrDefault(t => string.Equals(t.Aumid, row.Aumid,
                                                                   StringComparison.OrdinalIgnoreCase));

                            bool inTarget = group.Tiles.Any(t => ReferenceEquals(t, landed));
                            lines.Add(landed == null
                                ? "落进分组   = ✘ 磁贴没生成"
                                : $"落进分组   = {(inTarget ? "✔" : "✘")}  组内 {before} → {group.Tiles.Count}  " +
                                  $"pos=({landed.Column},{landed.Row})  span={landed.SpanX}x{landed.SpanY}");

                            var reload = LayoutStore.Load();
                            bool persisted = reload != null && reload.Groups.SelectMany(g => g.Tiles)
                                .Any(t => string.Equals(t.Aumid, row.Aumid, StringComparison.OrdinalIgnoreCase));
                            lines.Add("落盘       = " + (persisted ? "✔" : "✘"));

                            // ④ 拖到组外空白：应长出一个新分组。
                            // 「空白」不能写死右下角 —— 分组是自由摆放的，哪儿空得看模型：
                            // 从右下往左上扫，找第一个「面板不盖、落点解析不到任何组」的格心
                            if (row2 != null)
                            {
                                int groupsBefore = _layout.Groups.Count;
                                Point origin2 = GroupsHost.TranslatePoint(new Point(0, 0), this);
                                double stride = TileMetrics.Stride;
                                int scanCols = Math.Min(CurrentColumns(), 32);
                                Point emptyPoint = default;
                                bool foundEmpty = false;

                                for (int r = 48; r >= 0 && !foundEmpty; r--)
                                    for (int c = scanCols - 1; c >= 5 && !foundEmpty; c--)
                                    {
                                        var pt = new Point(origin2.X + c * stride + stride / 2,
                                                            origin2.Y + r * stride + stride / 2);
                                        if (AppsPanel.Visibility == Visibility.Visible &&
                                            IsOverAppsPanel(pt)) continue;
                                        if (TryResolveDrop(pt, out _, out _, out _)) continue;

                                        emptyPoint = pt;
                                        foundEmpty = true;
                                    }

                                if (!foundEmpty)
                                {
                                    lines.Add("拖到空白   = SKIP 板面上找不到可解析为空白的点");
                                }
                                else
                                {
                                    StartLibraryDrag(row2, emptyPoint);
                                    UpdateDrag(emptyPoint);
                                    bool hinted = _newGroupHint != null &&
                                                  _newGroupHint.Visibility == Visibility.Visible;
                                    EndDrag();

                                    var fresh = _layout.Groups.FirstOrDefault(g =>
                                        g.Tiles.Any(t => string.Equals(t.Aumid, row2.Aumid,
                                                                       StringComparison.OrdinalIgnoreCase)));
                                    lines.Add($"拖到空白   = 新组提示{(hinted ? "✔" : "✘")}  " +
                                              $"分组 {groupsBefore} → {_layout.Groups.Count}  " +
                                              (fresh != null ? $"命中组「{fresh.Name}」({fresh.Column},{fresh.Row})" : "命中失败 ✘"));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add("EX " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
            }

            // 还原：删掉所有新磁贴，撤掉测试长出来的分组，坐标复位
            try
            {
                foreach (var g in _layout.Groups)
                    g.Tiles.RemoveAll(t => !knownTiles.Contains(t));

                _layout.Groups.RemoveAll(g => !knownGroups.Contains(g));
                _layout.Groups.RemoveAll(g => g.Tiles.Count == 0 && knownGroups.Count > 0 &&
                                              knownGroups.Contains(g) == false);

                foreach (var kv in groupPos)
                {
                    kv.Key.Column = kv.Value.Col;
                    kv.Key.Row = kv.Value.Row;
                }

                Save();
                RenderAll();
                HideAppsPanel(false);

                lines.Add("已还原板面：磁贴数 = " + _layout.Groups.Sum(g => g.Tiles.Count) +
                          "，分组数 = " + _layout.Groups.Count);
            }
            catch (Exception ex)
            {
                lines.Add("还原失败 " + ex.Message);
            }

            System.IO.File.WriteAllLines(path, lines);
            Application.Current.Shutdown();
        }

        /// <summary>把应用库面板的样子拍下来。宽度临时拉大，才看得到「左边列表、右边磁贴」的关系。</summary>
        private async Task CaptureAppsShot(string path)
        {
            Width = 1080;
            Height = 720;

            BtnApps_Click(this, null);
            await Task.Delay(1600);

            LogHookState2($"AppsPanel w={AppsPanel.ActualWidth:0} h={AppsPanel.ActualHeight:0} " +
                          $"rows={AppsList.Items.Count} vis={AppsPanel.Visibility} " +
                          $"bg={AppsPanel.Background}");

            RevealForCapture();
            AppsPanel.UpdateLayout();

            // 整窗一张 + 面板本体一张：整屏缩略图看不清 12.5px 的字母标题
            CaptureElement(AppsPanel,
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? ".",
                                       System.IO.Path.GetFileNameWithoutExtension(path) + "-only.png"));

            await CaptureAndExit(path);
        }

        /// <summary>
        /// 分组外观截图：摆一块只有两个组的临时板 —— 左边未命名（标题平时不显示），
        /// 右边已命名且折叠（只剩一条标题 + 两条横杠）。不落盘。
        /// </summary>
        private async Task CaptureGroupShot(string path)
        {
            await Task.Delay(1500);
            ShowBoard();
            await Task.Delay(600);

            // 从现有板面借几块真磁贴，图标才出得来
            var pool = (_layout?.Groups ?? new List<TileGroup>())
                .Where(g => g?.Tiles != null)
                .SelectMany(g => g.Tiles)
                .Where(t => t != null)
                .Take(4)
                .ToList();

            TileItem Clone(int index, string fallback)
            {
                var src = pool.Count > index ? pool[index] : null;
                return new TileItem
                {
                    Aumid = src?.Aumid,
                    Name = src?.Name ?? fallback,
                    Size = TileSize.Medium
                };
            }

            var board = new BoardLayout();

            var unnamed = new TileGroup();      // 默认名「命名组」= 未命名
            unnamed.Tiles.Add(Clone(0, "Eagle"));
            unnamed.Tiles.Add(Clone(1, "PureRef"));

            var folded = new TileGroup { Name = "已折叠的组", Collapsed = true };
            folded.Tiles.Add(Clone(2, "Outlook"));
            folded.Tiles.Add(Clone(3, "Excel"));

            board.Groups.Add(unnamed);
            board.Groups.Add(folded);

            _layout = board;
            RenderAll();
            await Task.Delay(700);

            var info = string.Join(" | ", board.Groups.Select(g =>
                $"{g.Name}{(g.Collapsed ? "(折叠)" : "")} @({g.Column},{g.Row}) " +
                $"box={GroupGrid.Box(g).Width:0}x{GroupGrid.Box(g).Height:0}"));
            LogHookState2("groupshot " + info);

            // 按钮到底画没画出来、画在哪儿 —— 26px 的标题行缩图上看不清，直接量
            var hdr = FindGroupHeader(folded);
            if (hdr != null)
            {
                Rect hr = hdr.TransformToAncestor(this).TransformBounds(new Rect(hdr.RenderSize));
                var btn = (hdr.Child as Panel)?.Children.OfType<Button>().FirstOrDefault();
                if (btn != null)
                {
                    Rect br = btn.TransformToAncestor(this).TransformBounds(new Rect(btn.RenderSize));
                    var barsPanel = btn.Content as Panel;
                    LogHookState2($"header = {hr.X:0},{hr.Y:0} {hr.Width:0}x{hr.Height:0}  " +
                                  $"btn = {br.X:0},{br.Y:0} {br.Width:0}x{br.Height:0}  " +
                                  $"bars = {barsPanel?.Children.Count} 高{barsPanel?.ActualHeight:0}");
                }
            }

            // 抓屏抓不到：ShowBoard 之后别的窗口一抢焦点，板面就被 Deactivated 收起来了。
            // 改成自己渲染，同时把底板临时调成不透明 —— 半透明压在半透明上，整张图糊成一片
            _ui.BackOpacity = 1.0;
            ApplyAppearance(new WindowInteropHelper(this).Handle);
            RevealForCapture();
            await Task.Delay(300);

            // 只留左上那一条：整窗图里 26px 的标题行缩完根本看不出按钮是不是两根线
            if (Content is Visual root)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var rtb = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX)),
                    Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY)),
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                rtb.Render(root);

                int cw = (int)Math.Min(rtb.PixelWidth, Math.Round(760 * dpi.DpiScaleX));
                int ch = (int)Math.Min(rtb.PixelHeight, Math.Round(260 * dpi.DpiScaleY));

                var crop = new CroppedBitmap(rtb, new Int32Rect(0, 0, cw, ch));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(crop));
                using (var fs = System.IO.File.Create(path))
                    encoder.Save(fs);
            }

            Application.Current.Shutdown();
        }

        /// <summary>
        /// 菜单截图自检：真的把磁贴右键菜单打开，再从屏幕上抓一块。
        /// 菜单住在 Popup 里，不属于窗口可视树，RenderTargetBitmap 根本渲染不到它。
        /// </summary>
        private async Task CaptureMenuShot(string path)
        {
            await Task.Delay(1500);

            ShowBoard();
            await Task.Delay(700);

            var group = _layout?.Groups.FirstOrDefault(g => g != null && g.Tiles.Count > 0);
            var tile = group?.Tiles.FirstOrDefault();

            if (tile == null)
            {
                System.IO.File.WriteAllText(path + ".txt", "板面上没有磁贴");
                Application.Current.Shutdown();
                return;
            }

            var menu = BuildTileMenu(tile, group);
            menu.Placement = PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = 480;
            menu.VerticalOffset = 120;
            menu.IsOpen = true;

            await Task.Delay(900);
            LogHookState2($"menushot open={menu.IsOpen} w={menu.ActualWidth:0} h={menu.ActualHeight:0}");

            // 只抓菜单这一块：整屏缩略图里 12.5px 的字根本看不清对比度
            var origin = menu.PointToScreen(new Point(0, 0));
            int x = (int)Math.Max(0, origin.X - 10);
            int y = (int)Math.Max(0, origin.Y - 10);
            int w = (int)Math.Min(2400, menu.ActualWidth + 20);
            int h = (int)Math.Min(1300, menu.ActualHeight + 20);

            var shot = Native.ScreenCapture.Grab(x, y, w, h);
            if (shot != null)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(shot));
                using (var fs = System.IO.File.Create(path))
                    encoder.Save(fs);
            }

            menu.IsOpen = false;
            await Task.Delay(200);

            Application.Current.Shutdown();
        }

        /// <summary>
        /// 淡入淡出自检：唤出后按时间采样根容器的实际透明度。
        /// 曲线应该是「从 0 平滑升到 1」，而不是一直 1（没动画）或先 1 再归零（先闪一帧）。
        /// </summary>
        /// <summary>
        /// 与外部录屏对时间轴的探针：真实订阅单实例唤出信号，唤出后逐 20ms
        /// 采样透明度（只记变化）。文件里的毫秒时间戳和录屏帧时刻互相印证。
        /// </summary>
        private async void RunShowProbe(string path)
        {
            var lines = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            void Log(string s)
            {
                lines.Add($"{sw.ElapsedMilliseconds,6}ms  {s}");
                // 逐行落盘：探针可能随外部进程树一起被回收，攒到最后会丢
                try { System.IO.File.WriteAllLines(path, lines); } catch { }
            }

            Activated += (s, e) => Log("Activated");
            Deactivated += (s, e) => Log("Deactivated");

            // 自检模式没走单实例，这里自己把信号接上 —— 和常驻实例同一条唤出链路
            Native.SingleInstance.ShowRequested += () =>
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Log("signal -> ShowBoard 开始");
                    ShowBoard();
                    Log("ShowBoard 返回");
                }));

            if (!Native.SingleInstance.TryAcquire())
                Log("警告：已有实例在跑，信号发给了别人");

            Log("探针就绪，等待信号");

            // StartupUri 把窗口摆在 Opacity 0 —— IsVisible 已是 true，ShowBoard 会被
            // 「已可见」守卫弹掉。先真正 Hide 一次，再等信号才有完整唤出可看
            Hide();
            await Task.Delay(300);
            Log("已隐藏，IsVisible=" + IsVisible);

            // 最多等 25s；期间凡是可见就跟踪透明度变化，只记变化点
            double lastOp = -1;
            while (DateTime.UtcNow.Ticks < 0 || sw.ElapsedMilliseconds < 25000)
            {
                await Task.Delay(20);

                if (IsVisible)
                {
                    double op = RootGrid.Opacity;
                    if (Math.Abs(op - lastOp) > 0.005)
                    {
                        Log($"opacity={op:0.000}");
                        lastOp = op;
                    }
                }
                else if (lastOp != -1)
                {
                    Log("hidden");
                    lastOp = -1;
                }
            }

            System.IO.File.WriteAllLines(path, lines);
            Application.Current.Shutdown();
        }

        private async void RunFadeTest(string path)
        {
            var lines = new List<string>();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int clock() => (int)sw.ElapsedMilliseconds;

            void Log(string s) => lines.Add($"{clock(),5}ms  {s}");

            try
            {
                // 记录激活/位置事件：闪烁若是「唤出被失焦打断」，时间轴会对得上
                Activated += (s, e) => Log("Activated");
                Deactivated += (s, e) => Log("Deactivated (handler 判定: suppress=" +
                                             _suppressHide + " renaming=" + _renaming +
                                             " within400ms=" +
                                             ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 400) + ")");
                LocationChanged += (s, e) => Log("LocationChanged");
                SizeChanged += (s, e) => Log($"SizeChanged {Width:0}x{Height:0}");

                Hide();
                await Task.Delay(200);
                Log($"隐藏后 visible={IsVisible} opacity={RootGrid.Opacity:0.00}");

                // 模拟真实场景：唤出前焦点在别的窗口上（ExplorerPatcher / 资源管理器）
                var tray = FindWindowW("Shell_TrayWnd", null);
                if (tray != IntPtr.Zero) SetForegroundWindow(tray);
                await Task.Delay(150);
                Log("外部窗口已持有焦点，开始唤出");

                ShowBoard();
                Log($"ShowBoard返回 opacity={RootGrid.Opacity:0.00}");

                // 10ms 采样 600ms：透明度曲线要单调上升，任何回落都是被打断的证据
                for (int t = 0; t < 600; t += 10)
                {
                    await Task.Delay(10);
                    Log($"opacity={RootGrid.Opacity:0.000} shift={RootShift.Y:0.0} visible={IsVisible}");
                }

                // ===== 第二幕：模拟真实使用 —— 失焦收起，等稳定后再唤出一次 =====
                Log("----- 失焦收起 -----");
                if (tray != IntPtr.Zero) SetForegroundWindow(tray);
                await Task.Delay(900);
                Log($"收起后 visible={IsVisible} opacity={RootGrid.Opacity:0.00}");

                Log("----- 第二次唤出 -----");
                ShowBoard();
                Log($"ShowBoard#2返回 opacity={RootGrid.Opacity:0.00}");

                for (int t = 0; t < 600; t += 10)
                {
                    await Task.Delay(10);
                    Log($"opacity={RootGrid.Opacity:0.000} shift={RootShift.Y:0.0} visible={IsVisible}");
                }
            }
            catch (Exception ex)
            {
                lines.Add("EX " + ex.GetType().Name + ": " + ex.Message);
            }

            System.IO.File.WriteAllLines(path, lines);
            Application.Current.Shutdown();
        }

        /// <summary>沿真实拖拽链路把 item 拖到 onto 的格位上（落点让左上角精确对齐）。</summary>
        private void DragOnto(TileItem item, TileGroup group, FrameworkElement onto)
        {
            _dragItem = item;
            _dragGroup = group;
            _dragBorder = FindTileBorder(item);
            if (_dragBorder == null) return;

            _mouseDown = true;
            StartDrag(_dragBorder.TranslatePoint(new Point(20, 20), this));

            Point topLeft = onto.TranslatePoint(new Point(0, 0), this);
            UpdateDrag(new Point(topLeft.X + _grabOffset.X, topLeft.Y + _grabOffset.Y));
            EndDrag();
        }

        /// <summary>检查同一组内有没有两块磁贴在坐标上重叠。</summary>
        private bool HasOverlap()
        {
            if (_layout == null) return false;

            foreach (var group in _layout.Groups)
            {
                var tiles = group.Tiles.Where(t => t != null).ToList();
                for (int i = 0; i < tiles.Count; i++)
                {
                    for (int j = i + 1; j < tiles.Count; j++)
                    {
                        var a = tiles[i];
                        var b = tiles[j];
                        bool ox = a.Column < b.Column + b.SpanX && b.Column < a.Column + a.SpanX;
                        bool oy = a.Row < b.Row + b.SpanY && b.Row < a.Row + a.SpanY;
                        if (ox && oy) return true;
                    }
                }
            }
            return false;
        }

        // ================= 分组拖动自检 =================

        /// <summary>打印当前列数与每组的实际坐标 — 排查布局问题时比截图可靠。</summary>
        private void DumpLayout(string path)
        {
            var lines = new List<string>
            {
                $"Window.Width          = {Width:0.#}",
                $"Window.ActualWidth    = {ActualWidth:0.#}",
                $"GroupsHost.ActualWidth= {GroupsHost?.ActualWidth:0.#}",
                $"ViewportWidth         = {BoardScroll?.ViewportWidth:0.#}",
                $"Columns               = {CurrentColumns()}"
            };

            if (_layout != null)
            {
                foreach (var g in _layout.Groups)
                {
                    lines.Add($"{g.Name,-8} ({g.Column},{g.Row}) " +
                              $"span={GroupGrid.SpanColumns(g)}x{GroupGrid.SpanRows(g)} " +
                              $"px={GroupGrid.PixelWidth(g):0}x{GroupGrid.PixelHeight(g):0} " +
                              $"user={g.UserPlaced}");
                }
            }

            System.IO.File.WriteAllLines(path, lines);
            Application.Current.Shutdown();
        }

        /// <summary>只把手机面板渲染成 PNG —— 验收它的排版，不被整屏缩略图误导。</summary>
        private async Task CapturePhoneOnly(string path)
        {
            await Task.Delay(1500);

            BtnPhone_Click(this, null);
            await Task.Delay(500);

            RevealForCapture();
            PhonePage.UpdateLayout();

            CaptureElement(PhonePage, path);
            Application.Current.Shutdown();
        }

        /// <summary>把任意一块界面按它自己的坐标系渲染成 PNG（不带父容器偏移）。</summary>
        private static void SaveBitmap(BitmapSource bmp, string path)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = System.IO.File.Create(path))
                enc.Save(fs);
        }

        private static void CaptureElement(FrameworkElement el, string path)
        {
            int w = Math.Max(1, (int)Math.Ceiling(el.ActualWidth));
            int h = Math.Max(1, (int)Math.Ceiling(el.ActualHeight));

            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);

            // 不能直接 rtb.Render(el)：那会带上元素相对父容器的偏移，
            // 面板本来在窗口左下角，画到这块小画布上就整块跑出去了。
            // 用 VisualBrush 取它自身坐标系的内容才不偏移。
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), null,
                    new Rect(0, 0, w, h));

                var vb = new VisualBrush(el)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
            }

            rtb.Render(dv);

            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path))
                enc.Save(fs);
        }

        /// <summary>
        /// 走真实的唤出流程再截图：先 Hide（这样抓屏幕抓不到自己），
        /// 再 ShowBoard（内部会抓桌面、生成模糊底图），最后整窗渲染成 PNG。
        /// </summary>
        private async Task CaptureGlassShot(string path)
        {
            await Task.Delay(1200);

            Hide();
            await Task.Delay(150);

            ShowBoard();
            await Task.Delay(600);

            var src = GlassImage?.Source as BitmapSource;
            LogHookState2($"GlassShot vis={GlassImage?.Visibility} 底图=" +
                          (src != null ? $"{src.PixelWidth}x{src.PixelHeight}" : "NULL") +
                          $" 强度={_ui.BlurStrength:0.00} 透明度={_ui.BackOpacity:0.00}");

            RevealForCapture();
            SaveRootPng(path);
            Application.Current.Shutdown();
        }

        /// <summary>
        /// 回归自检：开关全屏之后垫底的桌面图还在不在、有没有对到正确位置。
        /// 之前这里会把底图整块作废，表现就是「切一次全屏毛玻璃就没了」。
        /// 结束时把用户的尺寸设置原样写回去，自检不留痕。
        /// </summary>
        private async void RunFullScreenTest(string path)
        {
            var lines = new List<string>();

            bool oldFull = _ui.FullScreen;
            double oldW = _ui.PanelWidth;
            double oldH = _ui.PanelHeight;

            try
            {
                await Task.Delay(400);

                Hide();
                await Task.Delay(120);
                ShowBoard();                 // 走真实唤出：先抓桌面，再显示
                await Task.Delay(400);

                void Snap(string label)
                {
                    bool has = GlassImage.Source != null;
                    string vis = GlassImage.Visibility.ToString();
                    var shift = GlassImage.RenderTransform as System.Windows.Media.TranslateTransform;
                    Rect work = SystemParameters.WorkArea;

                    bool placed = shift != null
                                  && Math.Abs(shift.X + (Left - work.Left)) < 0.6
                                  && Math.Abs(shift.Y + (Top - work.Top)) < 0.6
                                  && Math.Abs(GlassImage.Width - work.Width) < 0.6
                                  && Math.Abs(GlassImage.Height - work.Height) < 0.6;

                    lines.Add($"{label,-10} 窗口={Width:0}x{Height:0}  图存在={has}  " +
                              $"可见={vis}  图尺寸={GlassImage.Width:0}x{GlassImage.Height:0}  " +
                              $"位置对齐={(placed ? "✔" : "✘")}");

                    if (!has || vis != "Visible") lines.Add("    ✘ 毛玻璃丢了");
                }

                Snap("初始");

                ToggleFullScreen();
                await Task.Delay(300);
                Snap("切到全屏");

                ToggleFullScreen();
                await Task.Delay(300);
                Snap("退出全屏");

                lines.Add("——");
                lines.Add("期望：三种状态下 图存在=True、可见=Visible、位置对齐=✔");
            }
            catch (Exception ex)
            {
                lines.Add("EXCEPTION " + ex);
            }
            finally
            {
                _ui.FullScreen = oldFull;
                _ui.PanelWidth = oldW;
                _ui.PanelHeight = oldH;
                _ui.Save();
                ApplyWin10Geometry();
                UpdateGroupLayout();

                System.IO.File.WriteAllText(path, string.Join("\n", lines));
                Application.Current.Shutdown();
            }
        }

        /// <summary>
        /// 设置页功能自检：把每个控件都改一遍，然后看窗口/设置/注册表是不是真的跟着动了。
        /// 截图只能证明「画出来了」，证明不了「拖了有用」—— 这一节验的是后者。
        /// 跑完把用户原来的设置原样写回去。
        /// </summary>
        private async void RunSettingTest(string path)
        {
            var lines = new List<string>();

            bool oldFull = _ui.FullScreen;
            double oldW = _ui.PanelWidth;
            double oldH = _ui.PanelHeight;
            double oldOpacity = _ui.BackOpacity;
            double oldBlur = _ui.BlurStrength;
            bool oldAcrylic = _ui.Acrylic;
            bool oldAutostart = Services.Autostart.IsEnabled();

            void Check(string name, bool ok, string detail)
                => lines.Add($"{name,-22} {(ok ? "✔" : "✘")}  {detail}");

            try
            {
                await Task.Delay(300);
                Hide();
                await Task.Delay(120);
                ShowBoard();
                await Task.Delay(300);

                ShowSettingsPage();
                await Task.Delay(250);

                Rect work = SystemParameters.WorkArea;

                // 从全屏退出，否则宽高滑块是禁用的
                if (_ui.FullScreen) FullScreenCheck.IsChecked = false;
                await Task.Delay(200);
                Check("退出全屏", Width < work.Width - 1 || Height < work.Height - 1,
                      $"窗口={Width:0}x{Height:0}");

                // 宽度
                WidthSlider.Value = Math.Min(900, WidthSlider.Maximum);
                await Task.Delay(150);
                Check("宽度滑块", Math.Abs(Width - WidthSlider.Value) < 1.5,
                      $"滑块={WidthSlider.Value:0} 窗口={Width:0} 记录={_ui.PanelWidth:0}");

                // 高度
                HeightSlider.Value = Math.Min(820, HeightSlider.Maximum);
                await Task.Delay(150);
                Check("高度滑块", Math.Abs(Height - HeightSlider.Value) < 1.5,
                      $"滑块={HeightSlider.Value:0} 窗口={Height:0} 记录={_ui.PanelHeight:0}");

                // 铺满宽度
                FullWidthCheck.IsChecked = true;
                await Task.Delay(200);
                Check("铺满屏幕宽度", Math.Abs(Width - work.Width) < 1.5, $"窗口宽={Width:0}");
                FullWidthCheck.IsChecked = false;
                await Task.Delay(200);
                Check("恢复窄版", Width < work.Width - 1, $"窗口宽={Width:0}");

                // 毛玻璃总开关
                AcrylicCheck.IsChecked = false;
                await Task.Delay(150);
                Check("关毛玻璃", !_ui.Acrylic && GlassImage.Visibility != Visibility.Visible,
                      $"acrylic={_ui.Acrylic} 图={GlassImage.Visibility}");
                AcrylicCheck.IsChecked = true;
                await Task.Delay(150);
                Check("开毛玻璃", _ui.Acrylic, $"acrylic={_ui.Acrylic} 图={GlassImage.Visibility}");

                // 透明度 / 强度
                OpacitySlider.Value = 42;
                BlurSlider.Value = 66;
                await Task.Delay(150);
                Check("透明度滑块", Math.Abs(_ui.BackOpacity - 0.42) < 0.011,
                      $"{_ui.BackOpacity:0.00}");
                Check("强度滑块", Math.Abs(_ui.BlurStrength - 0.66) < 0.011,
                      $"{_ui.BlurStrength:0.00}");

                // 自启
                AutostartCheck.IsChecked = false;
                await Task.Delay(120);
                Check("关自启", !Services.Autostart.IsEnabled(), "");
                AutostartCheck.IsChecked = true;
                await Task.Delay(120);
                Check("开自启", Services.Autostart.IsEnabled(), "");

                // 全屏来回切两次，看毛玻璃还在不在
                FullScreenCheck.IsChecked = true;
                await Task.Delay(250);
                bool ok1 = GlassImage.Source != null && GlassImage.Visibility == Visibility.Visible;
                FullScreenCheck.IsChecked = false;
                await Task.Delay(250);
                bool ok2 = GlassImage.Source != null && GlassImage.Visibility == Visibility.Visible;
                Check("切全屏后毛玻璃", ok1 && ok2, $"全屏={ok1} 退出={ok2}");
            }
            catch (Exception ex)
            {
                lines.Add("EXCEPTION " + ex);
            }
            finally
            {
                _ui.FullScreen = oldFull;
                _ui.PanelWidth = oldW;
                _ui.PanelHeight = oldH;
                _ui.BackOpacity = oldOpacity;
                _ui.BlurStrength = oldBlur;
                _ui.Acrylic = oldAcrylic;
                _ui.Save();

                if (oldAutostart) Services.Autostart.Enable();
                else Services.Autostart.Disable();

                ApplyWin10Geometry();
                UpdateGroupLayout();
                ApplyAppearance(new WindowInteropHelper(this).Handle);

                System.IO.File.WriteAllText(path, string.Join("\n", lines));
                Application.Current.Shutdown();
            }
        }

        /// <summary>
        /// 真把板面右键菜单打开，再从它的视觉树里把滚动条捞出来核对：
        /// 宽度是不是 4、模板是不是 Win10ScrollBar 那套。
        /// 只查资源键不够 —— 得证明菜单模板真的把那个键用上了。
        /// </summary>
        private async void RunMenuRenderTest(string path)
        {
            var lines = new List<string>();

            Hide();
            ShowBoard();
            await Task.Delay(400);

            ContextMenu menu = null;
            try
            {
                menu = BuildBoardMenu();
                menu.PlacementTarget = BoardSurface;
                menu.Placement = PlacementMode.Center;
                menu.IsOpen = true;

                await Task.Delay(350);

                lines.Add($"菜单打开={menu.IsOpen}  实际尺寸={menu.ActualWidth:0}x{menu.ActualHeight:0}");

                var scrollBar = FindDescendant<ScrollBar>(menu);
                if (scrollBar == null)
                {
                    lines.Add("滚动条 = 没找到 ✘");
                }
                else
                {
                    lines.Add($"滚动条宽={scrollBar.Width:0}  最小宽={scrollBar.MinWidth:0}  " +
                              $"方向={scrollBar.Orientation}  可见={scrollBar.Visibility}");
                    lines.Add("样式来源 = " + (scrollBar.Width <= 5 ? "✔ Win10 细轨" : "✘ 系统默认"));
                }

                // 顺便把「固定到磁贴」不该出现这件事也断言一下
                bool hasPin = menu.Items.OfType<MenuItem>().Any(m =>
                    (m.Header?.ToString() ?? "").Contains("固定"));
                lines.Add("板面菜单里还有『固定到磁贴』= " + (hasPin ? "✘ 有" : "✔ 没有"));

                menu.IsOpen = false;

                // 再来一个长到装不下的菜单：这时候滚动条必须真的出现，且能滚
                var tall = MakeMenu();
                for (int i = 0; i < 120; i++)
                    tall.Items.Add(new MenuItem { Header = "测试项 " + (i + 1) });

                tall.PlacementTarget = BoardSurface;
                tall.Placement = PlacementMode.Center;
                tall.IsOpen = true;
                await Task.Delay(350);

                var sb2 = FindDescendant<ScrollBar>(tall);
                var sv2 = FindDescendant<ScrollViewer>(tall);
                lines.Add("");
                lines.Add($"长菜单 尺寸={tall.ActualWidth:0}x{tall.ActualHeight:0}  " +
                          $"可滚高度={sv2?.ScrollableHeight ?? 0:0}");

                if (sb2 == null)
                    lines.Add("长菜单滚动条 = 没找到 ✘");
                else
                    lines.Add($"长菜单滚动条 宽={sb2.Width:0} 可见={sb2.Visibility} " +
                              $"Maximum={sb2.Maximum:0}  " +
                              (sb2.Visibility == Visibility.Visible && sb2.Width <= 5
                                  ? "✔ 出现了，且是细轨"
                                  : "✘ 该出现却没出现"));

                tall.IsOpen = false;
            }
            catch (Exception ex)
            {
                lines.Add("EXCEPTION " + ex.Message);
            }
            finally
            {
                if (menu != null) menu.IsOpen = false;
                System.IO.File.WriteAllText(path, string.Join("\n", lines));
                Application.Current.Shutdown();
            }
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;

            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T hit) return hit;

                var deeper = FindDescendant<T>(child);
                if (deeper != null) return deeper;
            }

            return null;
        }

        /// <summary>装一遍资源管理器右键菜单，再把注册表读回来核对。</summary>
        private void RunShellMenuTest(string path)
        {
            var lines = new List<string>();

            try
            {
                lines.Add("exe = " + Environment.ProcessPath);
                lines.Add("install = " + (Native.ShellMenu.Install() ? "✔" : "✘"));
                lines.Add("is_current = " + (Native.ShellMenu.IsCurrent() ? "✔" : "✘"));

                foreach (string sub in new[] { @"*\shell\TileBoardPin", @"Directory\shell\TileBoardPin" })
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser
                               .OpenSubKey(@"Software\Classes\" + sub, false))
                    {
                        lines.Add($"HKCU\\Software\\Classes\\{sub}");
                        lines.Add("  标题 = " + (key?.GetValue(null) as string ?? "(空)"));
                        lines.Add("  图标 = " + (key?.GetValue("Icon") as string ?? "(空)"));
                        using (var cmd = key?.OpenSubKey("command", false))
                            lines.Add("  命令 = " + (cmd?.GetValue(null) as string ?? "(空)"));
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add("EXCEPTION " + ex.Message);
            }

            System.IO.File.WriteAllText(path, string.Join("\n", lines));
            Application.Current.Shutdown();
        }


        /// <summary>
        /// 毛玻璃强度自检：真截一张桌面，再按不同强度生成底图，
        /// 用「相邻像素平均梯度」度量糊到什么程度 —— 强度上去，这个数必须单调下降。
        /// 只会做出「模糊开/关」的实现，这串数字会是平的。
        /// </summary>
        private async void RunGlassTest(string path)
        {
            await Task.Delay(600);

            var lines = new List<string>();
            string dir = System.IO.Path.GetDirectoryName(path) ?? ".";
            string stem = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(path));

            try
            {
                bool got = Glass.Capture(new WindowInteropHelper(this).Handle);
                var raw = Glass.Current;
                lines.Add("capture = " + (got && raw != null
                    ? $"{raw.PixelWidth}x{raw.PixelHeight} ✔" : "失败 ✘"));

                if (raw != null)
                {
                    SaveBitmap(raw, stem + "-raw.png");

                    // 真实桌面可能是一整块纯色（暗色壁纸），拿它当尺子量不出强弱。
                    // 再拿一张带细节的合成图跑一遍，验的是算法本身。
                    var probe = MakeDetailProbe(Math.Min(1600, raw.PixelWidth),
                                                Math.Min(900, raw.PixelHeight));

                    double last = double.MaxValue;
                    bool monotonic = true;

                    lines.Add("—— 合成细节图（验算法）——");

                    foreach (double s in new[] { 0.0, 0.1, 0.3, 0.5, 0.7, 1.0 })
                    {
                        var bmp = Glass.RenderFrom(probe, s);
                        double sharp = Sharpness(bmp);
                        if (sharp > last + 0.05) monotonic = false;
                        last = sharp;

                        lines.Add($"强度={s:0.0}  底图={bmp.PixelWidth}x{bmp.PixelHeight}  相邻梯度={sharp:0.00}");

                        if (s < 0.05 || Math.Abs(s - 0.5) < 0.01 || s > 0.99)
                            SaveBitmap(bmp, $"{stem}-{s:0.0}.png");
                    }

                    lines.Add("单调下降 = " + (monotonic ? "✔ 强度真的在影响画面" : "✘ 滑块没起作用"));

                    lines.Add("—— 真实桌面 ——");
                    foreach (double s in new[] { 0.0, 0.3, 1.0 })
                    {
                        var bmp = Glass.RenderFrom(raw, s);
                        lines.Add($"强度={s:0.0}  底图={bmp.PixelWidth}x{bmp.PixelHeight}  相邻梯度={Sharpness(bmp):0.00}");
                    }

                    // 拖滑块时每一档都要重新出图，耗时必须足够小，否则手感是"一格一格卡"
                    var sw = Stopwatch.StartNew();
                    Glass.Render(0.35);
                    double first = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    Glass.Render(0.36);          // 同一档位：应当命中缓存
                    double second = sw.Elapsed.TotalMilliseconds;
                    lines.Add($"耗时  首次={first:0.0}ms  同档再取={second:0.0}ms（档数 {Glass.DivFor(0.0)}~{Glass.DivFor(1.0)}）");
                }
            }
            catch (Exception ex)
            {
                lines.Add("EX " + ex.GetType().Name + " " + ex.Message);
            }

            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllLines(path, lines);
            Application.Current.Shutdown();
        }

        /// <summary>自检用：一张混合了高/中/低空间频率的图，用来量模糊程度。真实桌面可能是一整块纯色，量不出东西。</summary>
        private static BitmapSource MakeDetailProbe(int w, int h)
        {
            int stride = w * 4;
            var px = new byte[stride * h];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;

                    bool check = ((x / 2) + (y / 2)) % 2 == 0;      // 2px 棋盘格（最高频）
                    int band = ((x / 40) + (y / 40)) % 6;           // 40px 色块（中频）
                    int v = check ? 45 + band * 32 : 150 + band * 12;

                    if (((x - y) % 90 + 90) % 90 < 3) v = 240;      // 细斜线

                    px[i] = (byte)Math.Clamp(v, 0, 255);
                    px[i + 1] = (byte)Math.Clamp(255 - v / 2, 0, 255);
                    px[i + 2] = (byte)Math.Clamp(v, 0, 255);
                    px[i + 3] = 255;
                }
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// 相邻像素平均绝对差（取蓝通道）。越小＝越糊。
        /// 先统一拉到 1024 宽再量：底图尺寸随强度变化，直接比小图的梯度等于比不同尺子，
        /// 而用户看到的是拉回窗口尺寸之后的效果。
        /// </summary>
        private static double Sharpness(BitmapSource bmp)
        {
            if (bmp == null) return 0;

            const int normWidth = 1024;
            if (Math.Abs(bmp.PixelWidth - normWidth) > 1)
            {
                // 走和 Image 控件同一条 HighQuality 缩放路径，量到的才是屏幕上真正的观感
                double kh = (double)bmp.PixelHeight * normWidth / bmp.PixelWidth;
                int nh = Math.Max(1, (int)Math.Round(kh));

                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
                    dc.DrawImage(bmp, new Rect(0, 0, normWidth, nh));
                }

                var rtb = new RenderTargetBitmap(normWidth, nh, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                if (rtb.CanFreeze) rtb.Freeze();
                bmp = rtb;
            }

            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            int stride = w * 4;
            var px = new byte[stride * h];
            bmp.CopyPixels(px, stride, 0);

            long sum = 0, n = 0;
            int stepY = Math.Max(1, h / 140);

            for (int y = 0; y < h; y += stepY)
            {
                int row = y * stride;
                for (int x = 4; x < w; x += 3)
                {
                    int i = row + x * 4;
                    sum += Math.Abs(px[i] - px[i - 4]);
                    n++;
                }
            }

            return n == 0 ? 0 : (double)sum / n;
        }

        /// <summary>不走鼠标，直接把「抓起分组 → 吸附格位 → 挤开邻居」跑一遍。</summary>
        private async void RunGroupTest(string path)
        {
            await Task.Delay(1800);   // 等图标与布局稳定

            // 同上：拖动会改分组坐标并写盘，先留一份原样
            var snapshot = LayoutStore.Clone(_layout);

            var lines = new List<string>();
            try
            {
                if (_layout == null || _layout.Groups.Count < 2)
                {
                    lines.Add("SKIP 分组不足 2 个，无法验证挤开");
                }
                else
                {
                    int cols = CurrentColumns();
                    lines.Add("columns = " + cols);
                    lines.Add("before  = " + DescribeGroups());

                    var moving = _layout.Groups[0];
                    var block = FindGroupBlock(moving);

                    if (block == null)
                    {
                        lines.Add("FAIL 找不到组块的可视元素");
                    }
                    else
                    {
                        _groupDragGroup = moving;
                        _groupDragBlock = block;
                        _groupMouseDown = true;

                        Point start = block.TranslatePoint(new Point(6, 6), this);
                        StartGroupDrag(start);
                        lines.Add("ghost   = " + (_groupGhost != null ? "created" : "NULL"));
                        lines.Add("hole    = 原组不透明度 " + block.Opacity.ToString("0.00"));

                        // 目标：第 2 行、最右边能站下的位置
                        int tc = Math.Max(0, cols - GroupGrid.SpanColumns(moving));
                        int tr = 1;

                        Point origin = GroupsHost.TranslatePoint(new Point(0, 0), this);
                        Point drop = new Point(
                            origin.X + tc * TileMetrics.Stride + _groupGrabOffset.X,
                            origin.Y + tr * TileMetrics.Stride + _groupGrabOffset.Y);

                        UpdateGroupDrag(drop);
                        lines.Add($"target  = col {tc} row {tr}");
                        lines.Add($"after   = {moving.Name} @ ({moving.Column},{moving.Row})");
                        lines.Add("结果    = " + (moving.Column != 0 || moving.Row != 0
                                                ? "落位已改变 ✔" : "位置没动 ✘"));
                        lines.Add("overlap = " + (GroupsOverlap() ? "存在重叠 ✘" : "无重叠 ✔"));
                        lines.Add("越界    = " + (GroupsOutOfBounds(cols) ? "有组超出列数 ✘" : "全部在界内 ✔"));

                        // 拖回原位：被挤开的组必须跟着归位 —— 这是「只让一格」的另一半
                        UpdateGroupDrag(new Point(start.X, start.Y));

                        bool restored = _layout.Groups
                            .Where(g => g != null && !ReferenceEquals(g, moving))
                            .All(g => snapshot.Groups.Any(s => s.Name == g.Name &&
                                                     s.Column == g.Column && s.Row == g.Row));
                        lines.Add("拖回原位 = 其余组全部归位 " + (restored ? "✔" : "✘ " + DescribeGroups()));

                        // 再拖回目标，测落定收尾
                        UpdateGroupDrag(drop);

                        EndGroupDrag();
                        lines.Add("ghost清理 = DragLayer 子元素 " + DragLayer.Children.Count);

                        // 松手后的板面必须真无重叠：拖动中允许暂时相交，
                        // 落定后 Normalize 得把这笔账收干净
                        lines.Add("落定overlap = " + (GroupsOverlap() ? "存在重叠 ✘" : "无重叠 ✔"));

                        // 重新读盘，确认坐标真的存住了
                        var reloaded = LayoutStore.Load();
                        string cmp = reloaded == null
                            ? "读取失败 ✘"
                            : (reloaded.Groups.Any(g => g.Name == moving.Name &&
                                                        g.Column == moving.Column &&
                                                        g.Row == moving.Row)
                                ? "坐标已落盘 ✔" : "坐标未落盘 ✘");
                        lines.Add("持久化  = " + cmp);
                        lines.Add("最终    = " + DescribeGroups());
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add("EX " + ex.GetType().Name + ": " + ex.Message);
            }

            // 先把「坐标已落盘」验完，再把板面退回原样：验证的是机制，不是留个结果给用户看
            if (RestoreLayout(snapshot))
                lines.Add("已还原板面 = " + DescribeGroups());

            System.IO.File.WriteAllLines(path, lines);
            Application.Current.Shutdown();
        }

        private string DescribeGroups()
        {
            if (_layout == null) return "(空)";
            return string.Join(" | ", _layout.Groups.Select(g => $"{g.Name}({g.Column},{g.Row})"));
        }

        private FrameworkElement FindGroupBlock(TileGroup group)
        {
            if (group == null) return null;

            foreach (UIElement child in GroupsHost.Children)
                if (child is FrameworkElement fe && ReferenceEquals(fe.Tag, group)) return fe;

            return null;
        }

        /// <summary>任意两个分组的矩形是否相交。</summary>
        private bool GroupsOverlap()
        {
            // 拖动中允许被挤的组和被拖组的落点暂时相交：让位只走一格（主人的要求），
            // 跨度一大挪一格必然还压着一角。此刻被拖组的本体是隐藏的，视觉不打架；
            // 拖走后按快照归位，松手后 Normalize 收干净。所以只查「其余组互相」
            TileGroup moving = _groupDragging ? _groupDragGroup : null;

            if (_layout == null) return false;

            var gs = _layout.Groups.Where(g => g != null && !ReferenceEquals(g, moving)).ToList();
            for (int i = 0; i < gs.Count; i++)
                for (int j = i + 1; j < gs.Count; j++)
                    if (GroupGrid.Box(gs[i]).IntersectsWith(GroupGrid.Box(gs[j]))) return true;

            return false;
        }

        /// <summary>有没有分组被移出可视列范围。</summary>
        private bool GroupsOutOfBounds(int cols)
        {
            if (_layout == null) return false;

            return _layout.Groups.Any(g => g != null &&
                (g.Column < 0 || g.Row < 0 ||
                 g.Column + GroupGrid.SpanColumns(g) > cols));
        }

        private async Task CaptureAndExit(string path)
        {
            await Task.Delay(2500);   // 等图标与布局稳定
            RevealForCapture();
            SaveRootPng(path);
            Application.Current.Shutdown();
        }

        /// <summary>
        /// 截图用：模拟把一块磁贴拖到组外，停在「松手就新建分组」的位置，
        /// 把跟随影像和虚线框一起拍下来。
        /// </summary>
        private async Task CaptureDragShot(string path)
        {
            await Task.Delay(1800);

            var group = _layout?.Groups.FirstOrDefault(g => g.Tiles.Count >= 3);
            if (group != null)
            {
                var tile = group.Tiles[0];
                _dragItem = tile;
                _dragGroup = group;
                _dragBorder = FindTileBorder(tile);

                if (_dragBorder != null)
                {
                    _mouseDown = true;
                    StartDrag(_dragBorder.TranslatePoint(new Point(20, 20), this));

                    // 第一行分组下方的空地：那里应当出现虚线框
                    UpdateDrag(new Point(320, ActualHeight - 140));
                }
            }

            await Task.Delay(250);
            RevealForCapture();
            SaveRootPng(path);
            Application.Current.Shutdown();
        }

        /// <summary>截图用：抓起一个分组拖到板面另一处，把跟随影像和原位的空洞一起拍下来。</summary>
        private async Task CaptureGroupDragShot(string path)
        {
            await Task.Delay(1800);

            var moving = _layout?.Groups.FirstOrDefault();
            var block = FindGroupBlock(moving);

            if (moving != null && block != null)
            {
                int beforeCol = moving.Column;
                int beforeRow = moving.Row;

                _groupDragGroup = moving;
                _groupDragBlock = block;
                _groupMouseDown = true;

                StartGroupDrag(block.TranslatePoint(new Point(10, 10), this));

                int cols = CurrentColumns();
                int tc = Math.Max(0, cols - GroupGrid.SpanColumns(moving) - 1);
                int tr = _layout.Groups.Where(g => g != null).Max(g => g.Row) + 1;

                Point origin = GroupsHost.TranslatePoint(new Point(0, 0), this);
                UpdateGroupDrag(new Point(
                    origin.X + tc * TileMetrics.Stride + _groupGrabOffset.X,
                    origin.Y + tr * TileMetrics.Stride + _groupGrabOffset.Y));

                await Task.Delay(200);

                // 跟随影像的左上角 vs 鼠标减抓取偏移，两者应当重合；差得多就是渲染带了父偏移
                var ghost = _groupGhost;
                double gx = ghost != null ? Canvas.GetLeft(ghost) : double.NaN;
                double gy = ghost != null ? Canvas.GetTop(ghost) : double.NaN;

                LogHookState2($"GroupDrag {moving.Name}: ({beforeCol},{beforeRow}) -> ({moving.Column},{moving.Row})  " +
                              $"ghost=({gx:0},{gy:0})  size={ghost?.ActualWidth ?? 0:0}x{ghost?.ActualHeight ?? 0:0}  " +
                              $"block={block.ActualWidth:0}x{block.ActualHeight:0}  cols={cols}");

                string stem = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(path) ?? ".",
                    System.IO.Path.GetFileNameWithoutExtension(path));

                // 把跟在鼠标上的那块影像、以及它用的那张位图各存一份，肉眼对一下就知道丢在哪一步
                if (_ghostSnapshot != null) SaveBitmap(_ghostSnapshot, stem + "-snap.png");
                if (ghost != null) { ghost.UpdateLayout(); CaptureElement(ghost, stem + "-ghost.png"); }
                CaptureElement(block, stem + "-block.png");
            }

            RevealForCapture();
            SaveRootPng(path);
            Application.Current.Shutdown();
        }

        /// <summary>截图模式没有走淡入，根容器还停在透明状态，这里直接摊开。</summary>
        private void RevealForCapture()
        {
            RootGrid.BeginAnimation(OpacityProperty, null);
            RootShift.BeginAnimation(TranslateTransform.YProperty, null);
            RootGrid.Opacity = 1;
            RootShift.Y = 0;
            UpdateLayout();
        }

        private void SaveRootPng(string path)
        {
            var root = Content as Visual;
            if (root == null) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY)),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);

            // 先垫一层假的「桌面」，这样半透明背景在截图里能看出透光程度
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
            {
                var brush = new LinearGradientBrush(
                    Color.FromRgb(0x33, 0x5C, 0x8A), Color.FromRgb(0x1E, 0x36, 0x52), 45);
                dc.DrawRectangle(brush, null, new Rect(0, 0, ActualWidth, ActualHeight));
            }
            rtb.Render(backdrop);
            rtb.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path))
                encoder.Save(fs);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                ToggleBoard();
                handled = true;
            }

            return IntPtr.Zero;
        }

        /// <summary>钩子 / 轮询任一路报上来的「轻点 Win 键」，去重后交给 ToggleBoard。</summary>
        private void OnWinKeyTap(string source)
        {
            long now = Environment.TickCount64;
            long gap = now - _lastTapTick;

            if (gap < 350)
            {
                LogHookState2($"win key tapped  source={source}  dropped (dup {gap}ms)");
                return;
            }

            _lastTapTick = now;
            LogHookState2($"win key tapped  source={source}  visible={IsVisible}  " +
                          $"hiding={_hiding}  opacity={RootGrid.Opacity:0.00}");
            ToggleBoard();

            // 轮询看见了、钩子却没看见 —— 说明钩子已经不在链首（被别的软件挡在外面）。
            // 这是唯一能证明「有人在吞键」的信号，收到就把钩子重装到链首。
            if (source == "poll" && now - _lastHookSawTick > 600 && now - _lastRebuildTick > 60000)
            {
                _lastRebuildTick = now;
                Dispatcher.BeginInvoke(new Action(() => RebuildHook("poll-only")),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void ToggleBoard()
        {
            if (IsVisible && !_hiding) HideBoard();
            else ShowBoard();
        }

        /// <summary>
        /// 唤出面板。
        /// 三个坑都在这里：不能反复切换 Topmost（每次切换都让 DWM 重排一次 Z 序，会闪），
        /// 透明度必须在 Show 之前就是 0（否则先以不透明画一帧再开始动画），
        /// 动画要等窗口真正进入渲染队列后再起（立刻起会和首帧合成抢跑）。
        /// </summary>
        private void ShowBoard()
        {
            if (IsVisible && !_hiding) return;

            _hiding = false;
            ++_fadeToken;
            HideAppsPanel(false);

            // Win10 唤出开始菜单永远回到主界面，不会停在上次翻到的某一页
            HideSettingsPage(false);
            HidePhonePage();

            // 尺寸只在真的变了才改，避免无谓的重建
            Rect work = SystemParameters.WorkArea;
            double wantW = Math.Clamp(_ui.PanelWidth > 0 ? _ui.PanelWidth : 560,
                                      MinPanelWidth, Math.Max(MinPanelWidth, work.Width));
            if (Math.Abs(Width - wantW) > 0.5) ApplyWin10Geometry();

            // 首帧必须是全透明的
            RootGrid.BeginAnimation(OpacityProperty, null);
            RootShift.BeginAnimation(TranslateTransform.YProperty, null);
            RootGrid.Opacity = 0;
            RootShift.Y = 10;

            // 抓毛玻璃底图必须赶在本窗口重新可见之前：此刻屏幕上是真桌面，抓不到自己。
            // 之后整段显示期间都复用这张图，拖滑块只是重新模糊它，不必再截屏。
            if (_ui.Acrylic)
            {
                bool got = Services.Glass.Capture(new WindowInteropHelper(this).Handle);
                var raw = Services.Glass.Current;
                LogHookState2(got && raw != null
                    ? $"Glass capture {raw.PixelWidth}x{raw.PixelHeight}"
                    : "Glass capture failed -> 退回 DWM 亚克力");
            }

            ApplyAppearance(new WindowInteropHelper(this).Handle);

            Topmost = true;             // 只置顶一次，播放完就不再动它
            Show();
            Activate();
            SetForegroundWindow(new WindowInteropHelper(this).Handle);
            _shownAt = DateTime.UtcNow;

            // 淡入不交给 BeginAnimation：满屏分层窗口的首帧合成会把 UI 线程憋住 ~100ms，
            // 动画时钟和 Render 优先级的回调都在这条被憋住的队伍里，实测要么拖到首帧
            // 之后才起（屏上瞬移出现），要么中途停摆再重启（闪一下）。改成逐帧手动驱动 ——
            // Rendering 每帧渲染前触发一格，第 0 格仍是 0（首帧不抢跑），之后逐格爬升；
            // 就算中途被别的活儿堵住，也只是画面暂停，绝不会回跳或瞬移。
            StartFadeIn();
        }

        /// <summary>手动淡入用的代次号：每次唤出/收起都递增，作废在途的逐格回调。</summary>
        private int _fadeSeq;

        /// <summary>淡入一共走多少格（帧）。60Hz 下 12 格 ≈ 200ms，和 Win10 开始菜单一个节奏。</summary>
        private const int FadeInFrames = 12;

        private static double EaseOutCubic(double x) => 1 - Math.Pow(1 - x, 3);

        /// <summary>
        /// 逐帧淡入。挂在 CompositionTarget.Rendering 上，每帧把透明度推进一步；
        /// 不用动画时钟，所以不受 dispatcher 阻塞影响 —— 卡了就停格，绝不会跳变。
        /// </summary>
        private void StartFadeIn()
        {
            if (!IsVisible) return;

            _fadeSeq++;
            int seq = _fadeSeq;
            int frame = 0;

            EventHandler step = null;
            step = (s, e) =>
            {
                // 期间被收起或重新唤出过：撤掉回调，别和新的一套打架
                if (seq != _fadeSeq || !IsVisible)
                {
                    CompositionTarget.Rendering -= step;
                    if (!IsVisible) { RootGrid.Opacity = 0; RootShift.Y = 10; }
                    return;
                }

                double k = EaseOutCubic(Math.Min(1.0, frame / (double)(FadeInFrames - 1)));
                RootGrid.Opacity = k;
                RootShift.Y = 10 * (1 - k);

                frame++;
                if (frame >= FadeInFrames)
                    CompositionTarget.Rendering -= step;
            };
            CompositionTarget.Rendering += step;
        }

        /// <summary>淡出后再隐藏。Win10 的开始菜单收回时也是先淡掉，不是啪一下消失。</summary>
        private void HideBoard()
        {
            if (!IsVisible || _hiding) return;

            _hiding = true;
            _fadeSeq++;             // 作废在途的逐格淡入，别和淡出动画打架
            int token = ++_fadeToken;

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new System.Windows.Media.Animation.DoubleAnimation(
                RootGrid.Opacity, 0, FadeOut) { EasingFunction = ease };

            fade.Completed += (s, e) =>
            {
                // 期间又被唤出或重新收起过，这一次的收尾就作废
                if (token != _fadeToken) return;

                RootGrid.BeginAnimation(OpacityProperty, null);
                RootShift.BeginAnimation(TranslateTransform.YProperty, null);
                RootGrid.Opacity = 0;
                RootShift.Y = 10;
                Topmost = false;
                _hiding = false;
                Hide();
            };

            RootGrid.BeginAnimation(OpacityProperty, fade);
        }

        private void OnDeactivated(object sender, EventArgs e)
        {
            if (_suppressHide || _renaming) return;

            // 刚唤出的一小段时间内忽略失焦：窗口在真正拿到焦点之前会先报一次 Deactivated，
            // 若把它当成「点了外面」，就会看到面板刚淡入一半又淡出去 —— 也就是那一下闪烁
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 400) return;

            HideBoard();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
            _hookWatch?.Dispose();
            _hookWatch = null;
            _winWatcher?.Dispose();
            _winWatcher = null;
            _winHook?.Dispose();
            _winHook = null;

            // 不要在这里写 _ui.PanelWidth = Width：
            // 退出时若是全屏，Width 就是整块屏幕，存下去等于把「非全屏尺寸」抹成满宽，
            // 下次退出全屏就恢复不回原来的宽度了。宽度只在拖拽结束时记录。
            // 自检跑完直接 Shutdown，此时内存里的坐标还是测试拖出来的位置。
            // 真要落盘的话，跑一次截图就把用户的分组位置改了 —— 自检绝不写盘。
            if (!IsSelfTestRun())
            {
                _ui.Save();
                Save();
            }
        }

        // ================= 渲染 =================

        private void RenderAll()
        {
            double keepOffset = BoardScroll?.VerticalOffset ?? 0;

            GroupsHost.Children.Clear();
            if (_layout == null) return;

            // 空分组不该占着一块地：磁贴被搬空后就把这个组撤掉。
            // 例外是刚点了「新建分组」、还等着人往里拖磁贴的那一个
            if (_layout.Groups.Count > 1)
            {
                if (_keepEmptyGroup != null && _keepEmptyGroup.Tiles.Count > 0)
                    _keepEmptyGroup = null;

                _layout.Groups.RemoveAll(g => g != null && g.Tiles.Count == 0 &&
                                              !ReferenceEquals(g, _keepEmptyGroup));
            }

            // 两层坐标都要先整理：组内的磁贴先摆好，组宽组高才定得下来，
            // 然后才能按真实尺寸去排整块板面上的分组
            foreach (var group in _layout.Groups)
                TileGrid.Normalize(group);

            GroupGrid.Normalize(_layout, CurrentColumns());
            _lastColumns = CurrentColumns();

            foreach (var group in _layout.Groups)
                GroupsHost.Children.Add(BuildGroupBlock(group));

            GroupsHost.InvalidateMeasure();

            // 重建后把滚动位置还原，别让面板莫名跳到中间
            Dispatcher.BeginInvoke(new Action(() =>
            {
                BoardScroll?.ScrollToVerticalOffset(keepOffset);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 当前板面能放几列。以分组画布的实际宽度为准；还没量过就退回视口宽度，
        /// 两者都没有时按 Win10 的 6 列算。
        /// </summary>
        private int CurrentColumns()
        {
            // 取「窗口反推」和「画布实测」里更大的那个。
            // 首帧画布的 ActualWidth 还是上一次布局的值（比如 XAML 里的 560），
            // 只有它会把整块板按窄版装箱，之后宽度生效也回不来了。
            double byWindow = Width - 48 - 6 - 34;      // 图标栏 + 拖拽条 + 左右边距
            double byHost = GroupsHost?.ActualWidth ?? 0;

            if (byHost <= 0 && BoardScroll != null && BoardScroll.ViewportWidth > 0)
                byHost = Math.Max(0, BoardScroll.ViewportWidth - 21);

            double w = Math.Max(byHost, byWindow);

            if (w <= 0) return TileMetrics.GridColumns;

            return GroupGrid.ColumnsForWidth(w);
        }

        /// <summary>
        /// 只重排一个组的几何，不重建视觉树 —— 拖拽时每次鼠标移动都要用它，
        /// 走 RenderAll 会重新取 422 个图标，手感会拖沓。
        /// </summary>
        private void ReflowGroup(TileGroup group)
        {
            if (group == null || _layout == null) return;

            for (int i = 0; i < GroupsHost.Children.Count && i < _layout.Groups.Count; i++)
            {
                if (!ReferenceEquals(_layout.Groups[i], group)) continue;
                if (GroupsHost.Children[i] is not StackPanel block) continue;

                var panel = block.Children.OfType<TileGroupPanel>().FirstOrDefault();
                if (panel == null) return;

                SyncTileCoords(panel);
                panel.Reflow();
                return;
            }
        }

        /// <summary>
        /// 把模型里的格位同步到面板的附加属性上。
        ///
        /// 面板排版读的是 TileGroupPanel.Column/Row，而模型改的是 TileItem.Column/Row，
        /// 两者只在 CreateTile 那一刻对齐过。少了这一步，拖动时模型坐标已经交换了，
        /// 画面却纹丝不动 —— 要让位必须两边一起走。
        /// </summary>
        private static void SyncTileCoords(TileGroupPanel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (child is not Border border || border.Tag is not TileItem tile) continue;

                TileGroupPanel.SetColumn(border, Math.Max(0, tile.Column));
                TileGroupPanel.SetRow(border, Math.Max(0, tile.Row));
            }
        }

        /// <summary>
        /// 面板宽度变了：分组按新的列数重新归一。
        /// 人手动摆过的组保留原位（只夹回可视范围），没动过的组重新紧凑排布 ——
        /// 否则首帧按窄版装箱、之后拉宽，整板会留下大片莫名的空洞。
        /// </summary>
        private void UpdateGroupLayout()
        {
            if (_layout == null || GroupsHost == null) return;

            int cols = CurrentColumns();

            if (_lastColumns != 0 && cols != _lastColumns)
            {
                foreach (var g in _layout.Groups)
                {
                    if (g == null || g.UserPlaced) continue;
                    g.Column = -1;
                    g.Row = -1;
                }
            }

            _lastColumns = cols;

            foreach (var g in _layout.Groups)
                TileGrid.Normalize(g);

            GroupGrid.Normalize(_layout, cols);
            GroupsHost.InvalidateMeasure();
        }

        private UIElement BuildGroupBlock(TileGroup group)
        {
            // 组块的尺寸由内容自然决定（标题 + 组内网格），位置交给 GroupCanvas，
            // 所以这里不设 Width / Margin —— 那是 WrapPanel 时代的写法
            var block = new StackPanel
            {
                Tag = group,
                // 整块可抓：只给标题行留抓手的时代，组一大就找不到能拖的地方了。
                // 透明底是必须的 —— 没有底色的面板收不到命中测试，空隙会直接穿到板面
                Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll,

                // 折叠之后组里一块磁贴都不建，面板宽度会塌成 0，标题行跟着缩到文字那么宽，
                // 折叠按钮就贴到字右边去了。给一个和组同宽的下限，两种状态才对得上
                MinWidth = GroupGrid.PixelWidth(group)
            };

            // 磁贴自己会把 MouseLeftButtonDown handled 掉，能走到这里的只有空白处
            block.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                BeginGroupDragCandidate(group, block, e);
            };

            // Win10 的分组标题是纯文字，没有底板；比磁贴名称更暗一档
            var title = new TextBlock
            {
                Text = group.IsUnnamed ? TileGroup.DefaultName : group.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xAE)),
                FontSize = 12.5,
                FontWeight = FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 30, 0),   // 给右边的折叠按钮让位
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            // 没起过名的组不摆标题：一排「命名组」摆在板面上又吵又没用。
            // 鼠标进到标题区才亮出来，想改名照样双击
            var foldButton = BuildFoldButton(group);

            if (group.IsUnnamed) title.Opacity = 0;

            // 折叠按钮跟 Win10 一样是「悬停才出现」：一直挂在那儿的话，
            // 没命名的组就是一行空白加一个孤零零的按钮，比标题还显眼。
            // header 是 block 的孩子，进出 header 必然进出 block，一套就够
            foldButton.Opacity = 0;

            block.MouseEnter += (s, e) =>
            {
                if (group.IsUnnamed) title.Opacity = 1;
                foldButton.Opacity = 1;
            };
            block.MouseLeave += (s, e) =>
            {
                if (group.IsUnnamed) title.Opacity = 0;
                foldButton.Opacity = 0;
            };

            var headerContent = new Grid();
            headerContent.Children.Add(title);
            headerContent.Children.Add(foldButton);

            var header = new Border
            {
                Height = 26,           // 与 GroupGrid.HeaderHeight 的 26 + 8 对齐
                Background = Brushes.Transparent,
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,   // 整行都能抓起来拖
                Child = headerContent,
                Tag = group,       // 新建分组后靠它找回标题，直接进入命名
                Cursor = Cursors.SizeAll,
                ToolTip = "拖动分组任意位置（标题和空隙都能抓）；双击标题重命名"
            };
            header.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    CancelGroupDrag();
                    BeginRename(group, header, title);
                    e.Handled = true;
                    return;
                }

                BeginGroupDragCandidate(group, block, e);
            };

            var panel = new TileGroupPanel();

            // 折叠的组一块磁贴都不建：省的是构建时间，也让组高度的账算得干净
            if (!group.Collapsed)
            {
                foreach (var tile in group.Tiles)
                    panel.Children.Add(CreateTile(group, tile));
            }

            block.Children.Add(header);
            block.Children.Add(panel);
            return block;
        }

        /// <summary>
        /// 分组右上角那两条横杠（Win10 的折叠按钮）。用两根 1.4px 的细条拼，
        /// 不走字体图标 —— 图标库里那个三横的汉堡比参考图多一条，粗细也对不上。
        /// </summary>
        private UIElement BuildFoldButton(TileGroup group)
        {
            var bars = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var barBrush = new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xAE));

            bars.Children.Add(new Border
            {
                Width = 14,
                Height = 2,
                Background = barBrush,
                CornerRadius = new CornerRadius(1)
            });
            bars.Children.Add(new Border
            {
                Width = 14,
                Height = 2,
                Background = barBrush,
                CornerRadius = new CornerRadius(1),
                Margin = new Thickness(0, 4, 0, 0)
            });

            var button = new Button
            {
                Content = bars,
                Width = 24,
                Height = 22,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                ToolTip = group.Collapsed ? "展开分组" : "折叠分组"
            };

            button.MouseEnter += (s, e) => barBrush.Color = Color.FromRgb(0xF2, 0xF2, 0xF2);
            button.MouseLeave += (s, e) => barBrush.Color = Color.FromRgb(0xAE, 0xAE, 0xAE);

            button.Click += (s, e) =>
            {
                ToggleGroupCollapsed(group);
                e.Handled = true;
            };

            return button;
        }

        /// <summary>折叠 / 展开一个分组。折叠后组只占标题那一行，磁贴全收进数据里不删。</summary>
        private void ToggleGroupCollapsed(TileGroup group)
        {
            if (group == null) return;

            group.Collapsed = !group.Collapsed;
            Save();
            RenderAll();

            SetStatus(group.Collapsed
                ? "已折叠「" + (group.IsUnnamed ? TileGroup.DefaultName : group.Name) + "」"
                : "已展开「" + (group.IsUnnamed ? TileGroup.DefaultName : group.Name) + "」");
        }

        /// <summary>磁贴统一底色。Win10 关掉「在开始菜单使用应用颜色」后就是这个灰，和主人给的参考一致。</summary>
        private static readonly Brush TileBack = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)));
        private static readonly Brush TileBackHover = Freeze(new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x46)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        private UIElement CreateTile(TileGroup group, TileItem tile)
        {
            int spanX = TileMetrics.SpanX(tile.Size);
            int spanY = TileMetrics.SpanY(tile.Size);

            var border = new Border
            {
                Width = TileMetrics.Pixels(spanX),
                Height = TileMetrics.Pixels(spanY),
                Background = TileBack,
                Cursor = Cursors.Hand,
                Tag = tile,
                ToolTip = tile.Name + "\n" + tile.Aumid,
                SnapsToDevicePixels = true
            };
            TileGroupPanel.SetSpanX(border, spanX);
            TileGroupPanel.SetSpanY(border, spanY);

            // 磁贴的位置由数据里的格位决定，不是由它在 Children 里的次序决定
            TileGroupPanel.SetColumn(border, Math.Max(0, tile.Column));
            TileGroupPanel.SetRow(border, Math.Max(0, tile.Row));

            border.Child = BuildTileSurface(tile, spanX, spanY, out Border hover);

            border.MouseEnter += (s, e) =>
            {
                hover.Opacity = 1;
                border.Background = TileBackHover;
            };
            border.MouseLeave += (s, e) =>
            {
                hover.Opacity = 0;
                border.Background = TileBack;
            };

            border.MouseLeftButtonDown += Tile_MouseLeftButtonDown;
            border.MouseRightButtonUp += Tile_MouseRightButtonUp;

            return border;
        }

        /// <summary>磁贴内部：图标居中偏上、名称压在左下角。拖动时的跟随影像也复用它。</summary>
        private Grid BuildTileSurface(TileItem tile, int spanX, int spanY, out Border hover)
        {
            var grid = new Grid();

            int iconPx = tile.Size switch
            {
                TileSize.Small => 32,
                TileSize.Medium => 48,
                TileSize.Wide => 44,
                TileSize.Large => 96,
                _ => 48
            };

            var bmp = _catalog.GetIcon(tile.Aumid, 256);
            FrameworkElement visual;

            if (bmp != null)
            {
                visual = new Image
                {
                    Source = bmp,
                    Width = iconPx,
                    Height = iconPx,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            }
            else
            {
                // 本机没有这个应用：画个字母占位，别让它变成空白方块
                visual = new TextBlock
                {
                    Text = string.IsNullOrEmpty(tile.Name) ? "?" :
                           tile.Name.Substring(0, 1).ToUpperInvariant(),
                    FontSize = iconPx * 0.52,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            if (tile.Size == TileSize.Wide)
            {
                visual.HorizontalAlignment = HorizontalAlignment.Left;
                visual.Margin = new Thickness(18, 0, 0, 0);
            }

            grid.Children.Add(visual);

            if (tile.Size != TileSize.Small)
            {
                var name = new TextBlock
                {
                    Text = tile.Name,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(9, 0, 9, 7),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = TileMetrics.Pixels(spanX) - 18,
                    IsHitTestVisible = false
                };
                grid.Children.Add(name);
            }

            // Win10 悬停时整块磁贴泛白一点点
            hover = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                Opacity = 0,
                IsHitTestVisible = false
            };
            grid.Children.Add(hover);

            return grid;
        }

        // ================= 拖拽 / 启动 =================

        private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border || border.Tag is not TileItem tile) return;

            _dragBorder = border;
            _dragItem = tile;
            _dragGroup = _layout?.Groups.FirstOrDefault(g => g.Tiles.Contains(tile));
            _dragOrigin = e.GetPosition(this);
            _mouseDown = true;
            _dragging = false;

            // 捕获挂在窗口上，不挂在磁贴上：拖动时会重建界面，挂在磁贴上的捕获会跟着失效
            Mouse.Capture(this, CaptureMode.Element);

            // 别冒泡到分组块 —— 那会同时把整个分组当成被拖的对象
            e.Handled = true;
        }

        private void OnWindowMouseMove(object sender, MouseEventArgs e)
        {
            if (_groupDragging)
            {
                UpdateGroupDrag(e.GetPosition(this));
                return;
            }

            if (_groupMouseDown)
            {
                Point gp = e.GetPosition(this);
                if (Math.Abs(gp.X - _groupDragOrigin.X) > 5 || Math.Abs(gp.Y - _groupDragOrigin.Y) > 5)
                    StartGroupDrag(gp);
                return;
            }

            if (_dragging)
            {
                UpdateDrag(e.GetPosition(this));
                return;
            }

            if (!_mouseDown || _dragBorder == null) return;

            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _dragOrigin.X) > 5 || Math.Abs(p.Y - _dragOrigin.Y) > 5)
                StartDrag(p);
        }

        private void OnWindowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 分组拖动优先收尾：它和磁贴拖动的字段是分开的
            if (_groupMouseDown || _groupDragging)
            {
                if (_groupDragging)
                {
                    EndGroupDrag();
                }
                else
                {
                    CancelGroupDrag();
                }

                Mouse.Capture(null);
                return;
            }

            if (!_mouseDown) return;

            _mouseDown = false;
            Mouse.Capture(null);

            if (_dragging)
            {
                EndDrag();
                return;
            }

            TileItem item = _dragItem;
            _dragBorder = null;
            _dragItem = null;
            _dragGroup = null;

            if (item != null) Launch(item.Aumid);
        }

        private void StartDrag(Point p)
        {
            if (_dragBorder == null || _dragItem == null) return;
            _dragging = true;

            int sx = TileMetrics.SpanX(_dragItem.Size);
            int sy = TileMetrics.SpanY(_dragItem.Size);

            Point topLeft = _dragBorder.TranslatePoint(new Point(0, 0), this);
            _grabOffset = new Point(p.X - topLeft.X, p.Y - topLeft.Y);

            var ghost = new Border
            {
                Width = TileMetrics.Pixels(sx),
                Height = TileMetrics.Pixels(sy),
                Background = TileBack,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Opacity = 0.92,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 2,
                    Direction = 270,
                    Opacity = 0.55,
                    Color = Colors.Black
                }
            };
            ghost.Child = BuildTileSurface(_dragItem, sx, sy, out _);

            DragLayer.Children.Add(ghost);
            _ghost = ghost;

            // 拍一张全场的坐标快照：拖动中每次落点变化都从这里重算，
            // 而不是在上一次的推挤结果上继续叠加
            TakeDragSnapshot();

            HideDraggedTile();
            UpdateDrag(p);
        }

        private void TakeDragSnapshot()
        {
            _dragSnapshot = new Dictionary<TileItem, (int Column, int Row)>();

            if (_layout?.Groups == null) return;

            foreach (var g in _layout.Groups)
            {
                if (g?.Tiles == null) continue;
                foreach (var t in g.Tiles)
                    if (t != null) _dragSnapshot[t] = (t.Column, t.Row);
            }
        }

        /// <summary>把全场磁贴的格位退回拖动开始时的样子（被拖的那块除外）。</summary>
        private void RestoreDragSnapshot()
        {
            if (_dragSnapshot == null || _layout?.Groups == null) return;

            foreach (var g in _layout.Groups)
            {
                if (g?.Tiles == null) continue;
                foreach (var t in g.Tiles)
                {
                    if (t == null || ReferenceEquals(t, _dragItem)) continue;
                    if (_dragSnapshot.TryGetValue(t, out var home))
                    {
                        t.Column = home.Column;
                        t.Row = home.Row;
                    }
                }
            }
        }

        private void UpdateDrag(Point p)
        {
            if (_ghost == null || _dragItem == null) return;

            Canvas.SetLeft(_ghost, p.X - _grabOffset.X);
            Canvas.SetTop(_ghost, p.Y - _grabOffset.Y);

            // 每次移动都先归位再重算。放在落点判定之前，因为 Place 判断「交换回原位」
            // 时读的就是各磁贴当前的坐标。
            RestoreDragSnapshot();

            // 指针还压在应用库上：不算落点。
            // 面板宽 336 盖着板面左侧，底下那几个分组是看不见的 —— 在这里解析落点
            // 等于让人对着空气落位。等指针真的移到板面上再开始判定。
            if (AppsPanel.Visibility == Visibility.Visible && IsOverAppsPanel(p))
            {
                _dropIntoNewGroup = false;
                HideNewGroupHint();
                return;
            }

            if (!TryResolveDrop(p, out TileGroup target, out int col, out int row))
            {
                // 拖到任何分组之外：松手就在这里开一个新分组。
                // 记下板面坐标，新分组才能生在落点那一格，而不是被装到末尾
                _dropIntoNewGroup = true;
                _lastBoardPoint = TranslatePoint(
                    new Point(p.X - _grabOffset.X, p.Y - _grabOffset.Y), GroupsHost);
                ShowNewGroupHint(p);
                return;
            }

            _dropIntoNewGroup = false;
            HideNewGroupHint();

            if (ReferenceEquals(target, _dragGroup))
            {
                // 同组内：改坐标、只重排这一个面板，磁贴实时让位。
                // 这里不再「位置没变就早退」—— 坐标虽然没变，但上一次移动把别人挤开了，
                // 早退会让那些磁贴一直停在错的地方，回不到原位
                TileGrid.Place(_dragGroup, _dragItem, col, row);
                ReflowGroup(_dragGroup);
                HideDraggedTile();
                return;
            }

            // 跨组：模型里搬家。目标组的人位在 RestoreDragSnapshot 里已经退回去了，
            // 所以 Place 看到的是「一组原本摆好的磁贴 + 一块新来的」，不会累积推挤
            _dragGroup?.Tiles.Remove(_dragItem);
            _dragItem.Column = col;
            _dragItem.Row = row;
            target.Tiles.Add(_dragItem);
            TileGrid.Place(target, _dragItem, col, row);
            _dragGroup = target;

            RenderAll();
            HideDraggedTile();
        }

        private void EndDrag()
        {
            _dragging = false;

            if (_ghost != null)
            {
                DragLayer.Children.Remove(_ghost);
                _ghost = null;
            }
            HideNewGroupHint();

            TileItem item = _dragItem;
            TileGroup source = _dragGroup;
            bool newGroup = _dropIntoNewGroup;
            bool wasNew = _dragIsNew;

            _dragBorder = null;
            _dragItem = null;
            _dragGroup = null;
            _dropIntoNewGroup = false;
            _dragIsNew = false;
            _dragSnapshot = null;

            if (newGroup && item != null)
            {
                if (source != null) CommitNewGroup(source, item);
                else CommitNewGroupFromLibrary(item);   // 从应用库拖到组外空白
                return;
            }

            if (wasNew && item != null)
            {
                // 列表留着没关，这一行得立刻标成「已在板上」，
                // 否则同一个应用还能再拖一次，落出两块一样的磁贴
                MarkPinned(item.Aumid);
                SetStatus("已添加磁贴：" + item.Name);
            }

            Save();
            RenderAll();
        }

        /// <summary>
        /// 从应用库拖出来的新应用落在空白处：就地开一个组把它装进去，标题直接进入命名。
        /// 和 CommitNewGroup 的区别是没有「源组」可以搬空，所以只负责新建。
        /// </summary>
        private TileGroup CommitNewGroupFromLibrary(TileItem item)
        {
            if (_layout == null || item == null) return null;

            item.Column = 0;
            item.Row = 0;

            var group = new TileGroup();
            group.Tiles.Add(item);

            int cols = CurrentColumns();
            int gcol = (int)Math.Round(Math.Max(0, _lastBoardPoint.X) / TileMetrics.Stride);
            int grow = (int)Math.Round(Math.Max(0, _lastBoardPoint.Y) / TileMetrics.Stride);
            group.Column = Math.Clamp(gcol, 0, Math.Max(0, cols - 1));
            group.Row = Math.Max(0, grow);

            _layout.Groups.Add(group);

            Save();
            RenderAll();
            SetStatus("已添加磁贴：" + item.Name);

            Dispatcher.BeginInvoke(new Action(() => BeginRenameGroup(group)),
                System.Windows.Threading.DispatcherPriority.Loaded);
            return group;
        }

        // ================= 分组拖动 =================

        /// <summary>按住组标题：先记为候选，移动超过阈值才算真的开始拖。</summary>
        private void BeginGroupDragCandidate(TileGroup group, FrameworkElement block, MouseButtonEventArgs e)
        {
            if (group == null || block == null) return;
            if (_dragging || _groupDragging) return;

            _groupDragGroup = group;
            _groupDragBlock = block;
            _groupDragOrigin = e.GetPosition(this);
            _groupMouseDown = true;

            Mouse.Capture(this, CaptureMode.Element);
            e.Handled = true;
        }

        /// <summary>双击重命名之前，把候选拖动状态清干净，否则会留下捕获。</summary>
        private void CancelGroupDrag()
        {
            _groupMouseDown = false;
            _groupDragging = false;

            // 半路取消：板面退回拖动前的样子，不然被挤开的组就永远回不来了
            RestoreGroupDragSnapshot();
            _groupDragSnapshot = null;
            _groupDragGroup = null;
            _groupDragBlock = null;

            if (_groupGhost != null)
            {
                DragLayer.Children.Remove(_groupGhost);
                _groupGhost = null;
            }

            Mouse.Capture(null);
            RenderAll();
        }

        private void StartGroupDrag(Point p)
        {
            if (_groupDragGroup == null || _groupDragBlock == null) return;
            _groupDragging = true;

            // 先把全场分组的坐标拍下来，UpdateGroupDrag 每次移动都靠它先归位
            _groupDragSnapshot = new Dictionary<TileGroup, (int Column, int Row)>();
            if (_layout?.Groups != null)
                foreach (var g in _layout.Groups)
                    if (g != null) _groupDragSnapshot[g] = (g.Column, g.Row);

            var block = _groupDragBlock;
            Point topLeft = block.TranslatePoint(new Point(0, 0), this);
            _groupGrabOffset = new Point(p.X - topLeft.X, p.Y - topLeft.Y);

            // 直接抓一张组块位图当跟随影像 —— 组块里的图标已经取好了，
            // 重建一个副本会再走一遍取图标，拖起来会顿。
            //
            // 不能用 RenderTargetBitmap.Render(block)：组块在 GroupCanvas 里是由 Arrange
            // 摆到 (列*79, 行*79) 的，Render 会把这个父级偏移算进去，内容整块被挤出画框 ——
            // 实测采样非透明像素 = 0，屏幕上就是「分组一拖就没了」。
            // 走 VisualBrush 画进 DrawingVisual，从 (0,0) 开始铺，和整窗截图同一条通道。
            int w = Math.Max(1, (int)Math.Ceiling(block.ActualWidth));
            int h = Math.Max(1, (int)Math.Ceiling(block.ActualHeight));

            var dpi = VisualTreeHelper.GetDpi(this);
            double scale = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;

            var snapshot = new DrawingVisual();
            using (var dc = snapshot.RenderOpen())
            {
                var brush = new VisualBrush(block)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
            }

            var bmp = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Round(w * scale)),
                Math.Max(1, (int)Math.Round(h * scale)),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(snapshot);

            // 诊断：位图里到底有没有东西。全透明＝内容没进画框，屏幕上看就是「拖没了」
            int stride = bmp.PixelWidth * 4;
            var probe = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(probe, stride, 0);
            int opaque = 0;
            for (int i = 3; i < probe.Length; i += 4 * 97)
                if (probe[i] != 0) opaque++;

            var ghost = new Border
            {
                Width = w,
                Height = h,
                Opacity = 0.92,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 22,
                    ShadowDepth = 3,
                    Direction = 270,
                    Opacity = 0.6,
                    Color = Colors.Black
                },
                Child = new Image
                {
                    Source = bmp,
                    Stretch = Stretch.None,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                }
            };

            DragLayer.Children.Add(ghost);
            _groupGhost = ghost;
            _ghostSnapshot = bmp;

            block.Opacity = 0;      // 原位留空，只有影像跟着鼠标走
            UpdateGroupDrag(p);

            // 抓到空位图就是「拖起来分组没了」的老毛病又回来了：只在异常时写日志，正常拖动不刷盘
            if (opaque == 0 || IsSelfTestRun())
            {
                Point ghostPos = ghost.TranslatePoint(new Point(0, 0), RootGrid);
                LogHookState2($"GroupGhost bmp={w}x{h} 采样非透明={opaque} " +
                              $"pos=({ghostPos.X:0},{ghostPos.Y:0}) " +
                              $"layer={DragLayer.ActualWidth:0}x{DragLayer.ActualHeight:0} " +
                              $"layerChildren={DragLayer.Children.Count}");
            }
        }

        private void UpdateGroupDrag(Point p)
        {
            if (_groupGhost == null || _groupDragGroup == null || _layout == null) return;

            Canvas.SetLeft(_groupGhost, p.X - _groupGrabOffset.X);
            Canvas.SetTop(_groupGhost, p.Y - _groupGrabOffset.Y);

            // 与磁贴同一套账：先按拖动起点的快照把全场归位，再算这一次的落点。
            // 不先归位的话，上一轮被挤开的组会带着「已挪过」的坐标参与下一轮计算，
            // 一次让一格就变成一次推一格 —— 拖着拖着整块板被推到尽头
            RestoreGroupDragSnapshot();

            // 换算到板面坐标再吸附到格位 —— 分组和磁贴用的是同一格 71+8
            Point origin = GroupsHost.TranslatePoint(new Point(0, 0), this);
            int cols = CurrentColumns();

            GroupGrid.SnapFromPoint(_groupDragGroup,
                p.X - _groupGrabOffset.X - origin.X,
                p.Y - _groupGrabOffset.Y - origin.Y,
                cols, out int col, out int row);

            // 这里不能「位置没变就早退」：坐标没变也可能上一轮挤开过别人，
            // 归位之后还得走一遍 Place 让该让位的让位、该回来的回来
            GroupGrid.Place(_layout, _groupDragGroup, cols, col, row);
            GroupsHost.InvalidateMeasure();
            GroupsHost.UpdateLayout();

            if (_groupDragBlock != null) _groupDragBlock.Opacity = 0;
        }

        /// <summary>把全场分组恢复成拖动起点的格位。</summary>
        private void RestoreGroupDragSnapshot()
        {
            if (_groupDragSnapshot == null || _layout?.Groups == null) return;

            foreach (var g in _layout.Groups)
            {
                if (g != null && _groupDragSnapshot.TryGetValue(g, out var home))
                {
                    g.Column = home.Column;
                    g.Row = home.Row;
                }
            }
        }

        private void EndGroupDrag()
        {
            _groupMouseDown = false;
            _groupDragging = false;

            // 这个位置是人摆的，之后改宽度别把它重新流走
            if (_groupDragGroup != null) _groupDragGroup.UserPlaced = true;

            if (_groupGhost != null)
            {
                DragLayer.Children.Remove(_groupGhost);
                _groupGhost = null;
            }

            if (_groupDragBlock != null) _groupDragBlock.Opacity = 1;

            _groupDragGroup = null;
            _groupDragBlock = null;
            _groupDragSnapshot = null;

            Save();
            RenderAll();
        }

        /// <summary>
        /// 在落点处开一个新分组，磁贴放进去，并立刻让标题进入可输入状态 ——
        /// Win10 里把磁贴拖到组外就是这样得到一个可命名的新分组。
        /// </summary>
        private TileGroup CommitNewGroup(TileGroup source, TileItem item)
        {
            if (_layout == null) return null;

            source.Tiles.Remove(item);
            item.Column = 0;
            item.Row = 0;

            var group = new TileGroup();
            group.Tiles.Add(item);

            // 新分组生在落点那一格：分组现在有全局坐标，不必再靠列表顺序决定位置
            int cols = CurrentColumns();
            int gcol = (int)Math.Round(Math.Max(0, _lastBoardPoint.X) / TileMetrics.Stride);
            int grow = (int)Math.Round(Math.Max(0, _lastBoardPoint.Y) / TileMetrics.Stride);
            group.Column = Math.Clamp(gcol, 0, Math.Max(0, cols - 1));
            group.Row = Math.Max(0, grow);

            // 插在原组后面，位置才不会跳
            int insert = _layout.Groups.IndexOf(source);
            insert = insert < 0 ? _layout.Groups.Count : insert + 1;
            insert = Math.Clamp(insert, 0, _layout.Groups.Count);
            _layout.Groups.Insert(insert, group);

            // 被搬空的原组直接撤掉 —— Win10 的空分组不会留在板上
            if (source.Tiles.Count == 0 && _layout.Groups.Count > 1)
                _layout.Groups.Remove(source);

            Save();
            RenderAll();

            Dispatcher.BeginInvoke(new Action(() => BeginRenameGroup(group)),
                System.Windows.Threading.DispatcherPriority.Loaded);
            return group;
        }

        /// <summary>
        /// 窗口坐标 → 「哪一组的哪一格」。磁贴左上角吸附到最近的格位，而不是自由漂，
        /// 这就是 Win10 的方格限制：随便放，但只能停在格子上。
        /// </summary>
        private bool TryResolveDrop(Point p, out TileGroup group, out int col, out int row)
        {
            group = null;
            col = row = 0;
            if (_layout == null || _dragItem == null) return false;

            int sx = Math.Clamp(_dragItem.SpanX, 1, TileMetrics.GridColumns);
            int sy = Math.Max(1, _dragItem.SpanY);

            // 命中区完全按模型算，不看可视元素 —— 分组画在哪儿由 (Column, Row) 唯一决定，
            // 两者本来就是同一个矩形。走视觉树有两个坑：面板刚重建还没过布局时 RenderSize 是 0，
            // 命中会漏；子元素顺序和 _layout.Groups 的下标也未必对得上。
            Point host = GroupsHost.TranslatePoint(new Point(0, 0), this);

            foreach (var g in _layout.Groups)
            {
                if (g == null) continue;

                Rect box = GroupGrid.Box(g);
                double panelX = host.X + box.X;
                double panelY = host.Y + box.Y + GroupGrid.HeaderHeight;
                double panelW = box.Width;
                double panelH = Math.Max(0, box.Height - GroupGrid.HeaderHeight);

                // 组宽只到「用到的列数」，但 Win10 的分组能往外长列，
                // 所以命中区至少放宽到 4 格宽，底部再给 16px 吸附余量
                var hit = new Rect(panelX, panelY,
                                   Math.Max(panelW, TileMetrics.Pixels(Math.Min(4, TileMetrics.GridColumns))) + 8,
                                   panelH + 16);
                if (!hit.Contains(p)) continue;

                double left = p.X - panelX - _grabOffset.X;
                double top = p.Y - panelY - _grabOffset.Y;

                col = (int)Math.Round(left / TileMetrics.Stride);
                row = (int)Math.Round(top / TileMetrics.Stride);

                col = Math.Clamp(col, 0, Math.Max(0, TileMetrics.GridColumns - sx));
                row = Math.Max(0, row);

                group = g;
                return true;
            }

            return false;
        }

        /// <summary>拖到组外时在落点画一个虚线框，表示松手会长出一个新分组。</summary>
        private void ShowNewGroupHint(Point p)
        {
            if (_newGroupHint == null)
            {
                _newGroupHint = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
                    IsHitTestVisible = false
                };
                DragLayer.Children.Insert(0, _newGroupHint);
            }

            int sx = Math.Clamp(_dragItem?.SpanX ?? 2, 1, TileMetrics.GridColumns);
            int sy = Math.Max(1, _dragItem?.SpanY ?? 2);

            // 画成「一个新分组的地盘」：至少 4 格宽，上方留出标题的高度。
            // 只画磁贴那么大，会被跟随影像整个盖住，等于没有提示
            _newGroupHint.Width = Math.Max(TileMetrics.Pixels(sx),
                                           TileMetrics.Pixels(Math.Min(4, TileMetrics.GridColumns)));
            _newGroupHint.Height = TileMetrics.Pixels(sy) + 34;
            _newGroupHint.Visibility = Visibility.Visible;

            Canvas.SetLeft(_newGroupHint, p.X - _grabOffset.X);
            Canvas.SetTop(_newGroupHint, p.Y - _grabOffset.Y - 34);
        }

        private void HideNewGroupHint()
        {
            if (_newGroupHint != null) _newGroupHint.Visibility = Visibility.Collapsed;
        }

        /// <summary>让某个分组的标题直接进入输入状态。</summary>
        private void BeginRenameGroup(TileGroup group)
        {
            var header = FindGroupHeader(group);

            // 标题现在装在 Grid 里（右边还有折叠按钮），不再是 header 的直接子元素
            var title = (header?.Child as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();
            if (title != null) BeginRename(group, header, title);
        }

        private Border FindGroupHeader(TileGroup group)
        {
            if (group == null) return null;
            foreach (var block in GroupsHost.Children.OfType<StackPanel>())
                foreach (UIElement child in block.Children)
                    if (child is Border b && ReferenceEquals(b.Tag, group)) return b;
            return null;
        }

        /// <summary>把正在拖的那块磁贴在原位做成空洞，只有跟随鼠标的影像在动。</summary>
        private void HideDraggedTile()
        {
            var border = FindTileBorder(_dragItem);
            if (border != null) border.Opacity = 0;
        }

        private Border FindTileBorder(TileItem tile)
        {
            if (tile == null) return null;
            foreach (var block in GroupsHost.Children.OfType<StackPanel>())
                foreach (var panel in block.Children.OfType<TileGroupPanel>())
                    foreach (var el in panel.Children.OfType<Border>())
                        if (ReferenceEquals(el.Tag, tile)) return el;
            return null;
        }

        private void Tile_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border border || border.Tag is not TileItem tile) return;

            var group = _layout?.Groups.FirstOrDefault(g => g.Tiles.Contains(tile));
            var menu = BuildTileMenu(tile, group);
            menu.PlacementTarget = border;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private void Launch(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return;
            try
            {
                if (Native.NativeShell.LooksLikePath(aumid))
                {
                    Process.Start(new ProcessStartInfo(aumid) { UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid)
                    {
                        UseShellExecute = true
                    });
                }
                HideBoard();
            }
            catch (Exception ex)
            {
                SetStatus("启动失败：" + ex.Message);
            }
        }

        // ================= 右键菜单 =================

        /// <summary>右键菜单统一深色，跟板面一个调子。</summary>
        private static ContextMenu MakeMenu()
        {
            // 底色、圆角、滚动条全部交给 App.xaml 里的 TileMenu 样式：
            // 菜单里的滚动条要和磁贴板同款细条，不能是资源管理器那套带箭头的粗条
            return new ContextMenu
            {
                Style = (Style)Application.Current.Resources["TileMenu"],
                HasDropShadow = true
            };
        }

        /// <summary>板面空白处右键：固定文件/文件夹、新建分组、个性化设置。</summary>
        private void BoardSurface_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 磁贴和分组标题都把自己那次右键 handled 了，能冒到这里的都是空白
            var menu = BuildBoardMenu();

            menu.PlacementTarget = BoardSurface;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private ContextMenu BuildBoardMenu()
        {
            var menu = MakeMenu();

            var newGroup = new MenuItem { Header = "新建分组", Icon = MenuIcon("\uE710") };
            newGroup.Click += (s, ev) => AddGroup_Click(this, null);
            menu.Items.Add(newGroup);

            var appearance = new MenuItem { Header = "个性化设置…", Icon = MenuIcon("\uE790") };
            appearance.Click += (s, ev) => ShowSettingsPage();
            menu.Items.Add(appearance);

            menu.Items.Add(new Separator());

            var import = new MenuItem { Header = "导入 Win10 布局…", Icon = MenuIcon("\uE8B5") };
            import.Click += (s, ev) => Import_Click(this, null);
            menu.Items.Add(import);

            var regenerate = new MenuItem { Header = "按本机应用重新生成", Icon = MenuIcon("\uE72C") };
            regenerate.Click += (s, ev) => Generate_Click(this, null);
            menu.Items.Add(regenerate);

            menu.Items.Add(new Separator());

            var openDir = new MenuItem { Header = "打开布局文件位置", Icon = MenuIcon("\uE8B7") };
            openDir.Click += (s, ev) => Run("explorer.exe", LayoutStore.Directory);
            menu.Items.Add(openDir);

            return menu;
        }

        /// <summary>
        /// 处理队列里的「固定到磁贴」请求（来自资源管理器右键菜单或 --pin）。
        /// 返回 true 表示这次唤醒只为固定东西，不需要把面板摆出来 ——
        /// Win10 的「固定到开始屏幕」也是什么都不弹。
        /// </summary>
        private bool HandlePinRequests()
        {
            var paths = Services.PinQueue.Drain();
            if (paths.Count == 0) return false;

            AddPathTiles(paths, null);
            LogHookState2($"pin request handled: {paths.Count} item(s)");
            return true;
        }

        /// <summary>
        /// 把磁盘上的路径做成磁贴。文件与文件夹走同一条路：
        /// Aumid 位置直接存绝对路径，Launch 认路径就交给 shell 打开，
        /// 图标由 SHCreateItemFromParsingName 从 shell 直接取，不用另写一套。
        /// </summary>
        private void AddPathTiles(IEnumerable<string> paths, TileGroup into)
        {
            if (paths == null) return;
            if (_layout == null) _layout = LayoutStore.CreateEmpty();

            var target = into;
            if (target == null || !_layout.Groups.Contains(target))
                target = _layout.Groups.FirstOrDefault(g => g != null);

            if (target == null)
            {
                target = new TileGroup { Name = "固定" };
                _layout.Groups.Add(target);
            }

            int added = 0, skipped = 0;

            foreach (string raw in paths)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string id = raw.Trim().Trim('"');

                bool exists = _layout.Groups.Any(g => g != null && g.Tiles.Any(t =>
                    string.Equals(t.Aumid, id, StringComparison.OrdinalIgnoreCase)));

                if (exists) { skipped++; continue; }

                target.Tiles.Add(new TileItem
                {
                    Aumid = id,
                    Name = _catalog.ResolveName(id),
                    Size = TileSize.Medium
                });
                added++;
            }

            if (added == 0)
            {
                SetStatus(skipped > 0 ? "这些已经在磁贴板上了" : "没有选中任何东西");
                return;
            }

            Save();
            RenderAll();

            SetStatus(skipped > 0
                ? $"已固定 {added} 项，{skipped} 项已存在"
                : $"已固定 {added} 项到「{target.Name}」");
        }

        private ContextMenu BuildTileMenu(TileItem tile, TileGroup group)
        {
            var menu = MakeMenu();

            menu.Items.Add(new MenuItem { Header = tile.Name, IsEnabled = false });
            menu.Items.Add(new Separator());

            foreach (TileSize size in Enum.GetValues(typeof(TileSize)))
            {
                var sizeValue = size;
                var mi = new MenuItem
                {
                    Header = $"{TileMetrics.Label(size)}磁贴",
                    IsCheckable = true,
                    IsChecked = tile.Size == sizeValue
                };
                mi.Click += (s, e) =>
                {
                    tile.Size = sizeValue;
                    Save();
                    RenderAll();
                };
                menu.Items.Add(mi);
            }

            menu.Items.Add(new Separator());

            var moveMenu = new MenuItem { Header = "移到分组", Icon = MenuIcon("\uE838") };
            if (_layout != null)
            {
                foreach (var g in _layout.Groups.ToList())
                {
                    if (ReferenceEquals(g, group)) continue;
                    var target = g;
                    var mi = new MenuItem { Header = g.Name };
                    mi.Click += (s, e) =>
                    {
                        if (group != null) group.Tiles.Remove(tile);
                        target.Tiles.Add(tile);
                        Save();
                        RenderAll();
                    };
                    moveMenu.Items.Add(mi);
                }
            }
            moveMenu.Items.Add(new Separator());
            var newGroupItem = new MenuItem { Header = "新建分组…", Icon = MenuIcon("\uE710") };
            newGroupItem.Click += (s, e) =>
            {
                if (group != null) CommitNewGroup(group, tile);
                else
                {
                    var ng = new TileGroup();
                    ng.Tiles.Add(tile);
                    _layout?.Groups.Add(ng);
                    Save();
                    RenderAll();
                    Dispatcher.BeginInvoke(new Action(() => BeginRenameGroup(ng)),
                        System.Windows.Threading.DispatcherPriority.Loaded);
                }
            };
            moveMenu.Items.Add(newGroupItem);
            menu.Items.Add(moveMenu);

            menu.Items.Add(new Separator());

            var appearanceItem = new MenuItem { Header = "个性化设置…", Icon = MenuIcon("\uE790") };
            appearanceItem.Click += (s, e) => ShowSettingsPage();
            menu.Items.Add(appearanceItem);

            menu.Items.Add(new Separator());

            var open = new MenuItem { Header = "打开", Icon = MenuIcon("\uE768") };
            open.Click += (s, e) => Launch(tile.Aumid);
            menu.Items.Add(open);

            var remove = new MenuItem { Header = "从磁贴板移除", Icon = MenuIcon("\uE74D") };
            remove.Click += (s, e) =>
            {
                group?.Tiles.Remove(tile);
                Save();
                RenderAll();
            };
            menu.Items.Add(remove);

            return menu;
        }

        // ================= 分组重命名 =================

        private void BeginRename(TileGroup group, Border header, TextBlock title)
        {
            if (_renaming) return;
            _renaming = true;

            var box = new TextBox
            {
                Text = group?.Name,
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(3, 0, 3, 0),
                FontSize = 12.5,
                MinWidth = 90,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            header.Child = box;
            box.Focus();
            box.SelectAll();

            bool committed = false;

            void Commit()
            {
                if (committed) return;
                committed = true;
                group.Name = string.IsNullOrWhiteSpace(box.Text) ? TileGroup.DefaultName : box.Text.Trim();
                _renaming = false;
                Save();
                RenderAll();
            }

            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { committed = true; _renaming = false; RenderAll(); }
            };
            box.LostFocus += (s, e) => Commit();
        }

        // ================= 顶部按钮 =================

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            string path = Win10LayoutImporter.DetectLayoutFile();

            _suppressHide = true;
            try
            {
                if (path == null)
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = "选择 Windows 10 磁贴布局文件",
                        Filter = "布局 XML (*.xml)|*.xml|所有文件 (*.*)|*.*"
                    };
                    if (dlg.ShowDialog(this) != true) return;
                    path = dlg.FileName;
                }

                if (!_catalog.Loaded)
                {
                    MessageBox.Show(this, "应用清单还没加载完，稍等一下再试。", "磁贴板");
                    return;
                }

                var result = Win10LayoutImporter.Import(path, _catalog);

                if (!result.Ok)
                {
                    MessageBox.Show(this, "导入失败：" + result.Error, "磁贴板");
                    return;
                }

                _layout = result.Layout;
                Save();
                RenderAll();

                string msg = $"来源：{result.VariantInfo}\n" +
                             $"解析出 {result.GroupCount} 个分组、{result.TileCount} 个磁贴。\n" +
                             $"其中 {result.ResolvedCount} 个在本机存在，{result.Missing.Count} 个已失效。";

                if (result.Missing.Count > 0)
                {
                    msg += "\n\n失效项（保留为占位磁贴，不会丢失布局）：\n" +
                           string.Join("\n", result.Missing.Take(10)) +
                           (result.Missing.Count > 10 ? $"\n… 以及另外 {result.Missing.Count - 10} 个" : "");
                }

                MessageBox.Show(this, msg, "导入结果");
                SetStatus($"已导入 {result.TileCount} 个磁贴，其中 {result.ResolvedCount} 个可用");
            }
            finally
            {
                _suppressHide = false;
            }
        }

        private void Generate_Click(object sender, RoutedEventArgs e)
        {
            _suppressHide = true;
            try
            {
                var answer = MessageBox.Show(this,
                    "这会用本机应用重新生成整块板面，当前布局会被覆盖。继续吗？",
                    "磁贴板", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) return;
            }
            finally
            {
                _suppressHide = false;
            }

            GenerateFromCatalog();
            RenderAll();
            SetStatus($"已按本机 {_catalog.All.Count} 个应用重新生成");
        }

        private void AddGroup_Click(object sender, RoutedEventArgs e)
        {
            if (_layout == null) return;

            var group = new TileGroup();
            _keepEmptyGroup = group;      // 空组会被自动清理，这个是例外
            _layout.Groups.Add(group);
            Save();
            RenderAll();

            // 新建的分组直接进入命名状态，省得再双击一次
            Dispatcher.BeginInvoke(new Action(() => BeginRenameGroup(group)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ================= 手机连接面板 =================

        private PhoneCard _phoneCard;
        private bool _phoneTried;

        private static readonly Brush PhoneRowHover =
            Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));

        private void BtnPhone_Click(object sender, RoutedEventArgs e)
        {
            if (PhonePage.Visibility == Visibility.Visible)
            {
                HidePhonePage();
                return;
            }

            if (!_phoneTried)
            {
                _phoneTried = true;
                _phoneCard = PhoneCompanion.Load();
            }

            BuildPhonePanel();
            HideSettingsPage(false);       // 两页互斥，同时开着谁也看不见谁
            ShowPageOver(PhonePage);
            SetRailActive(BtnPhone, true);
        }

        private void BuildPhonePanel()
        {
            if (PhoneBody == null) return;
            PhoneBody.Children.Clear();

            var card = _phoneCard;
            if (card == null)
            {
                PhoneBody.Children.Add(new TextBlock
                {
                    Text = "读不到手机连接状态。手机连接可能没启动过，或这台电脑还没配对手机。",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xCF)),
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(2, 4, 2, 10)
                });

                PhoneBody.Children.Add(MakePhoneRow("\uE8EA", "打开手机连接", null, "ms-phone:"));
                return;
            }

            // ---- 设备名与状态（一行卡片，与下面的动作行左对齐） ----
            var head = new Grid { Margin = new Thickness(2, 2, 2, 12) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            head.Children.Add(new TextBlock
            {
                Text = "\uE8EA",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 26,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            });

            var headText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            headText.Children.Add(new TextBlock
            {
                Text = card.DeviceName,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (!string.IsNullOrWhiteSpace(card.Status))
            {
                headText.Children.Add(new TextBlock
                {
                    Text = card.Status,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xA6)),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
            }

            Grid.SetColumn(headText, 1);
            head.Children.Add(headText);
            PhoneBody.Children.Add(head);

            // ---- 四个功能 ----
            foreach (var action in card.Actions)
            {
                string glyph = action.Label switch
                {
                    "短信" => "\uE8BD",
                    "呼叫" => "\uE717",
                    "照片" => "\uE91B",
                    "发送文件" => "\uE8A5",
                    _ => "\uE8EA"
                };

                PhoneBody.Children.Add(MakePhoneRow(glyph, action.Label, null, action.Url));
            }

            // ---- 最近 ----
            if (card.Recents.Count > 0)
            {
                PhoneBody.Children.Add(new TextBlock
                {
                    Text = "最近",
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                    Margin = new Thickness(8, 14, 0, 6)
                });

                foreach (var r in card.Recents)
                {
                    string glyph = r.AppName == "照片" ? "\uE91B" : "\uE8BD";
                    PhoneBody.Children.Add(MakePhoneRow(glyph, r.AppName, r.Summary, r.Url, r.Time));
                }
            }
        }

        /// <summary>一行可点的条目：图标 + 主文 +（可选）副文/时间。</summary>
        private FrameworkElement MakePhoneRow(string glyph, string title, string subtitle,
                                              string url, string time = null)
        {
            var grid = new Grid();

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            grid.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 10, 0)
            });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                text.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 0)
                });
            }

            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (!string.IsNullOrWhiteSpace(time))
            {
                var t = new TextBlock
                {
                    Text = time,
                    FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 2, 0)
                };

                Grid.SetColumn(t, 2);
                grid.Children.Add(t);
            }

            var row = new Border
            {
                Child = grid,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 8, 8, 8),
                Cursor = Cursors.Hand
            };

            row.MouseEnter += (s, e) => row.Background = PhoneRowHover;
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (s, e) => OpenPhoneUrl(url);

            return row;
        }

        /// <summary>用 ms-phone: 深链打开手机连接里对应的那一页。</summary>
        private void OpenPhoneUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                HidePhonePage();
                HideBoard();
            }
            catch (Exception ex)
            {
                SetStatus("打开手机连接失败：" + ex.Message);
            }
        }

        private void BtnApps_Click(object sender, RoutedEventArgs e) => ToggleAppsPanel();

        /// <summary>侧边栏新加的那颗「应用库」，和上面那颗进的是同一个查看器。</summary>
        private void BtnLibrary_Click(object sender, RoutedEventArgs e) => ToggleAppsPanel();

        private void AppsClose_Click(object sender, RoutedEventArgs e) => HideAppsPanel();

        private void ToggleAppsPanel()
        {
            if (AppsPanel.Visibility == Visibility.Visible) HideAppsPanel();
            else ShowAppsPanel();
        }

        private void ShowAppsPanel()
        {
            HideSettingsPage(false);
            HidePhonePage();
            ShowBoardSurface(BoardSurface);   // 板面必须在场：这里是要把应用拖上去的
            UpdateAppsPanelWidth();

            SetRailActive(BtnApps, true);
            SetRailActive(BtnLibrary, true);

            // 先清空搜索框再显示：反过来的话 TextChanged 会先填一遍，下面又填一遍
            SearchBox.Text = string.Empty;
            AppsPanel.Visibility = Visibility.Visible;

            FillLibrary(string.Empty);
            SearchBox.Focus();
        }

        /// <summary>
        /// 应用库的宽度不能写死 336：面板最窄 560 时它会吃掉大半个板面，
        /// 右边连一块磁贴都放不下。按板面宽度的六成取，夹在 260~336 之间。
        /// </summary>
        private void UpdateAppsPanelWidth()
        {
            double board = BoardSurface?.ActualWidth ?? 0;
            if (board <= 0) return;

            AppsPanel.Width = Math.Clamp(board * 0.6, 260, 336);
        }

        /// <param name="restoreBoard">
        /// 收起时要不要把板面显回来。面板自己收的时候板面本来就露着，不必动；
        /// 从设置页跳进来时则由 ShowBoardSurface 统一处理。
        /// </param>
        private void HideAppsPanel(bool restoreBoard = true)
        {
            bool wasOpen = AppsPanel.Visibility == Visibility.Visible;

            AppsPanel.Visibility = Visibility.Collapsed;
            SetRailActive(BtnApps, false);
            SetRailActive(BtnLibrary, false);

            _listMouseDown = false;
            _listDragRow = null;

            if (wasOpen && restoreBoard && !_dragging) ShowBoardSurface(BoardSurface);
        }

        private void BtnDocuments_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))
                { UseShellExecute = true });
            }
            catch (Exception ex) { SetStatus("打开文档失败：" + ex.Message); }
        }

        private void BtnTools_Click(object sender, RoutedEventArgs e)
        {
            var menu = MakeMenu();

            menu.Items.Add(MakePowerItem("导入 Win10 布局…", () => Import_Click(this, null)));
            menu.Items.Add(MakePowerItem("按本机应用重新生成", () => Generate_Click(this, null)));
            menu.Items.Add(MakePowerItem("新建分组", () => AddGroup_Click(this, null)));
            menu.Items.Add(new Separator());

            // 外观与尺寸的可调项已经全部收进「个性化」页，这里只留一个入口
            menu.Items.Add(MakePowerItem("个性化设置…", ShowSettingsPage));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakePowerItem("打开布局文件位置",
                () => Run("explorer.exe", LayoutStore.Directory)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakePowerItem("退出磁贴板", Close));

            menu.PlacementTarget = BtnTools;
            menu.Placement = PlacementMode.Top;
            menu.IsOpen = true;
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("打开设置失败：" + ex.Message); }
        }

        private void BtnPower_Click(object sender, RoutedEventArgs e)
        {
            var menu = MakeMenu();

            menu.Items.Add(MakePowerItem("锁定", () => Run("rundll32.exe", "user32.dll,LockWorkStation")));
            menu.Items.Add(MakePowerItem("注销", () => Run("shutdown.exe", "/l")));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakePowerItem("重启", () => ConfirmAndRun("重启", "/r")));
            menu.Items.Add(MakePowerItem("关机", () => ConfirmAndRun("关机", "/s")));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakePowerItem("退出磁贴板", Close));

            menu.PlacementTarget = BtnPower;
            menu.Placement = PlacementMode.Top;
            menu.IsOpen = true;
        }

        /// <summary>菜单项左侧的 16px 线性图标（Win10 磁贴菜单同款 MDL2 线性图标）。</summary>
        private static TextBlock MenuIcon(string glyph) => new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        };

        private MenuItem MakePowerItem(string header, Action action, string glyph = null)
        {
            var mi = new MenuItem { Header = header };
            if (glyph != null) mi.Icon = MenuIcon(glyph);
            mi.Click += (s, e) => action();
            return mi;
        }

        private void ConfirmAndRun(string what, string arg)
        {
            _suppressHide = true;
            try
            {
                var answer = MessageBox.Show(this, $"确定要{what}吗？", "磁贴板",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
            }
            finally
            {
                _suppressHide = false;
            }
            Run("shutdown.exe", arg + " /t 5");
        }

        private void Run(string file, string args)
        {
            try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("执行失败：" + ex.Message); }
        }

        // ================= 应用库（Win10「所有应用」的字母表） =================

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchHint != null)
                SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                    ? Visibility.Visible : Visibility.Collapsed;

            if (AppsPanel.Visibility == Visibility.Visible)
                FillLibrary(SearchBox.Text);
        }

        /// <summary>
        /// 重新填一遍列表：按字母组分段，每段前面插一条标题行。
        /// 用的是「标题行也是行」的平铺做法，不上 CollectionViewSource ——
        /// 分组一旦交给 WPF，虚拟化就会打折，四百多个应用滚起来就不跟手了。
        /// </summary>
        private void FillLibrary(string filter)
        {
            if (!_catalog.Loaded) return;

            IEnumerable<Native.NativeShell.AppEntry> query = _catalog.All
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Name));

            if (!string.IsNullOrWhiteSpace(filter))
            {
                query = query.Where(a =>
                    a.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                    (a.Aumid?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
            }

            // 首字母先算一次：中文取拼音首字母要跑二十多次本地化比较，
            // 塞进比较器里会让四百多项的排序多算几千遍
            var apps = query
                .Select(a => new { Entry = a, Letter = NameIndex.Group(a.Name) })
                .ToList();

            // 先按字母组，再按拼音序 —— Win10 中文开始菜单就是这个次序
            apps.Sort((x, y) =>
            {
                int byGroup = NameIndex.Rank(x.Letter).CompareTo(NameIndex.Rank(y.Letter));
                return byGroup != 0 ? byGroup : NameIndex.Compare(x.Entry.Name, y.Entry.Name);
            });

            var onBoard = new HashSet<string>(
                _layout?.Groups.SelectMany(g => g.Tiles).Select(t => t.Aumid)
                       ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            var rows = new List<AppRow>(apps.Count + 24);
            char current = '\0';

            foreach (var item in apps)
            {
                var app = item.Entry;
                char letter = item.Letter;

                if (letter != current)
                {
                    current = letter;
                    rows.Add(new AppRow
                    {
                        IsHeader = true,
                        Letter = letter,
                        HeaderText = NameIndex.Header(letter),
                        Name = NameIndex.Header(letter)
                    });
                }

                rows.Add(new AppRow
                {
                    Aumid = app.Aumid,
                    Name = app.Name,
                    Letter = letter,
                    IsPinned = onBoard.Contains(app.Aumid)
                });
            }

            AppsList.ItemsSource = rows;

            // 图标后台逐个补：先出字，图标随后贴上去
            var iconTarget = rows;
            _ = Task.Run(() =>
            {
                foreach (var row in iconTarget)
                {
                    if (row.IsHeader) continue;

                    var icon = _catalog.GetIcon(row.Aumid, 32);
                    if (icon == null) continue;

                    Dispatcher.Invoke(() =>
                    {
                        if (ReferenceEquals(AppsList.ItemsSource, iconTarget)) row.Icon = icon;
                    });
                }
            });
        }

        /// <summary>从鼠标事件里捞出它落在哪一行；落在标题或空白上返回 null。</summary>
        private static AppRow RowFromEvent(MouseEventArgs e)
        {
            var el = e.OriginalSource as DependencyObject;

            while (el != null && el is not ListBoxItem)
                el = VisualTreeHelper.GetParent(el);

            var row = (el as ListBoxItem)?.DataContext as AppRow;
            return row == null || row.IsHeader ? null : row;
        }

        private void AppsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _listMouseDown = false;
            _listDragRow = null;

            var row = RowFromEvent(e);
            if (row == null) return;

            _listDragRow = row;
            _listDragOrigin = e.GetPosition(this);
            _listMouseDown = true;
        }

        private void AppsList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_listMouseDown || _listDragRow == null || _dragging) return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _listMouseDown = false;
                return;
            }

            Point p = e.GetPosition(this);
            if (Math.Abs(p.X - _listDragOrigin.X) <= 6 && Math.Abs(p.Y - _listDragOrigin.Y) <= 6)
                return;

            StartLibraryDrag(_listDragRow, p);
        }

        private void AppsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_listMouseDown || _listDragRow == null) return;

            AppRow row = _listDragRow;
            _listMouseDown = false;
            _listDragRow = null;

            if (_dragging) return;

            // 没拖动就是单击 —— Win10 的所有应用是点一下直接启动
            Launch(row.Aumid);
            HideBoard();
        }

        private void AppsList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var row = RowFromEvent(e);
            if (row == null) return;

            var menu = MakeMenu();

            menu.Items.Add(MakePowerItem("打开", () => { Launch(row.Aumid); HideBoard(); }, "\uE768"));
            menu.Items.Add(MakePowerItem(row.IsPinned ? "已经在磁贴板上" : "固定到磁贴板", () =>
            {
                PinAumid(row.Aumid);
                FillLibrary(SearchBox.Text);
            }, row.IsPinned ? null : "\uE718"));

            menu.PlacementTarget = AppsList;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;

            e.Handled = true;
        }

        /// <summary>
        /// 从应用库里拎起一个应用，就地变成一块正在拖的磁贴 ——
        /// 后续的移动、吸附、落位全部复用板面那套拖动逻辑，只在 EndDrag 里分支收尾。
        /// </summary>
        private void StartLibraryDrag(AppRow row, Point p)
        {
            _listMouseDown = false;
            _listDragRow = null;

            if (row == null || _layout == null) return;

            if (IsOnBoard(row.Aumid))
            {
                SetStatus("「" + row.Name + "」已经在板上了，直接拖板上那块改位置");
                return;
            }

            var size = TileSize.Medium;
            int sx = TileMetrics.SpanX(size);
            int sy = TileMetrics.SpanY(size);

            _dragIsNew = true;
            _dragItem = new TileItem { Aumid = row.Aumid, Name = row.Name, Size = size };
            _dragGroup = null;
            _dragBorder = null;

            // 列表里没有「抓在磁贴哪一点」这回事，光标压在磁贴正中最好瞄
            _grabOffset = new Point(TileMetrics.Pixels(sx) / 2.0, TileMetrics.Pixels(sy) / 2.0);
            _mouseDown = true;
            _dragging = true;

            var ghost = new Border
            {
                Width = TileMetrics.Pixels(sx),
                Height = TileMetrics.Pixels(sy),
                Background = TileBack,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Opacity = 0.92,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 2,
                    Direction = 270,
                    Opacity = 0.55,
                    Color = Colors.Black
                }
            };
            ghost.Child = BuildTileSurface(_dragItem, sx, sy, out _);

            DragLayer.Children.Add(ghost);
            _ghost = ghost;

            // 列表不动。以前这里会先把面板收起来「让出落点」，代价是拖完一次就得
            // 重新点开一次应用库 —— 连续摆十块磁贴要点十次。面板留着，落点交给
            // UpdateDrag 里那段「指针还在面板上就别解析」来兜。
            SetRailActive(BtnApps, true);
            SetRailActive(BtnLibrary, true);

            Mouse.Capture(this, CaptureMode.Element);
            UpdateDrag(p);
        }

        /// <summary>拖到一半不要了：丢掉跟随影像，退回应用库，板上什么都不留。</summary>
        private void CancelLibraryDrag()
        {
            _dragging = false;
            _mouseDown = false;
            _dragIsNew = false;
            _dropIntoNewGroup = false;

            if (_ghost != null)
            {
                DragLayer.Children.Remove(_ghost);
                _ghost = null;
            }
            HideNewGroupHint();

            _dragBorder = null;
            _dragItem = null;
            _dragGroup = null;

            // 面板本来就没收起，这里不能再 ShowAppsPanel()：
            // 那会把搜索框清空、列表重填一遍，等于把用户刚滚到的位置抹掉
        }

        /// <summary>指针是否压在应用库面板上（面板可见时才有意义）。</summary>
        private bool IsOverAppsPanel(Point p)
        {
            try
            {
                Point local = TranslatePoint(p, AppsPanel);
                return local.X >= 0 && local.Y >= 0
                    && local.X <= AppsPanel.ActualWidth
                    && local.Y <= AppsPanel.ActualHeight;
            }
            catch
            {
                return false;   // 面板刚重建、还没进可视树时算「不在上面」
            }
        }

        private bool IsOnBoard(string aumid)
        {
            if (_layout == null || string.IsNullOrEmpty(aumid)) return false;

            return _layout.Groups.Any(g => g != null && g.Tiles.Any(t => t != null &&
                string.Equals(t.Aumid, aumid, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// 就地给列表里的某一行打上「已在板上」。整表重填会把滚动位置和搜索词
        /// 一起清掉，拖完一块磁贴就跳回列表顶部 —— 所以只改这一行。
        /// </summary>
        private void MarkPinned(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return;
            if (AppsList?.ItemsSource is not IEnumerable<AppRow> rows) return;

            foreach (var row in rows)
            {
                if (row == null || row.IsHeader) continue;
                if (!string.Equals(row.Aumid, aumid, StringComparison.OrdinalIgnoreCase)) continue;

                row.IsPinned = true;
                return;
            }
        }

        /// <summary>右键菜单里的「固定到磁贴板」：塞进第一个分组。</summary>
        private void PinAumid(string aumid)
        {
            if (string.IsNullOrWhiteSpace(aumid)) return;
            if (_layout == null || _layout.Groups.Count == 0)
                _layout = LayoutStore.CreateEmpty();

            if (IsOnBoard(aumid))
            {
                SetStatus("这个应用已经在板上了");
                return;
            }

            _layout.Groups[0].Tiles.Add(new TileItem
            {
                Aumid = aumid,
                Name = _catalog.ResolveName(aumid),
                Size = TileSize.Medium
            });

            Save();
            RenderAll();
            SetStatus("已固定：" + _catalog.ResolveName(aumid));
        }

        // ================= 生成 / 持久化 =================

        private void GenerateFromCatalog()
        {
            var apps = _catalog.All
                .Where(a => !string.IsNullOrWhiteSpace(a.Aumid))
                .Take(72)
                .ToList();

            var layout = new BoardLayout { Source = "generated" };

            const int perGroup = 12;
            int index = 0;
            for (int i = 0; i < apps.Count; i += perGroup)
            {
                index++;
                var group = new TileGroup { Name = index == 1 ? "常用" : $"分组 {index}" };
                foreach (var app in apps.Skip(i).Take(perGroup))
                {
                    group.Tiles.Add(new TileItem
                    {
                        Aumid = app.Aumid,
                        Name = app.Name,
                        Size = TileSize.Medium
                    });
                }
                layout.Groups.Add(group);
            }

            if (layout.Groups.Count == 0)
                layout.Groups.Add(new TileGroup { Name = "常用" });

            _layout = layout;
            Save();
        }

        private void Save()
        {
            if (_layout != null) LayoutStore.Save(_layout);
        }

        private void SetStatus(string text)
        {
            if (ToastText == null) return;
            ToastText.Text = text;
            Toast.Opacity = 1;
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }
}
