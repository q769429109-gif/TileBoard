using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TileBoard.Services;

namespace TileBoard.Controls
{
    /// <summary>
    /// 菜单内嵌取色器 —— 参考主流取色器的结构：
    /// 色轮（转色相）+ 轮心的明暗方框（定饱和/明度）+ H/S/V 三条 + 样本 + 十六进制输入。
    ///
    /// 交互约定：拖动过程只发 ColorChanged（外面改「眼睛看得见的那一层」，
    /// 不落盘），松手 / 点样本 / 敲回车才发 Committed（外面落盘）。
    /// 灰色和黑色没有色相可言，此时保留轮子上的色相不动，别把人转回红色。
    /// </summary>
    public class ColorPickerPanel : Grid
    {
        public event Action<string> ColorChanged;
        public event Action Committed;

        // 色轮几何：外半径 84 / 内半径 64，轮心方框 88（对角线 124 < 轮孔直径 128，不压环）
        private const double RingSize = 168;
        private const double RingOuter = 84;
        private const double RingInner = 64;
        private const double HueOrbit = 74;      // 色相旋钮走的半径
        private const double SvSize = 88;
        private const double StripHeight = 14;
        private const double KnobSize = 14;

        /// <summary>样本种子：旧色板那 12 个中低明度色，白字仍压得住。</summary>
        private static readonly string[] DefaultPresets =
        {
            "#3A3A3A", "#232323", "#2B4A8B", "#0F6E6E",
            "#2E6B3E", "#6B4FA0", "#9C3E6B", "#A3373B",
            "#B35C1E", "#A87A21", "#5A4636", "#455A64"
        };

        private double _h = 0, _s = 0, _v = 1;   // h: 0-360°，s/v: 0-1

        /// <summary>内容距菜单左缘的内衬：太贴边输入框就像挤在墙上。</summary>
        private const double Gutter = 12;

        private TranslateTransform _svDotPos;

        private Border _preview;
        private TextBox _hexBox;
        private Canvas _ringHost;
        private Ellipse _hueKnob;
        private Rectangle _svHue;
        private Ellipse _svDot;
        private Border _hTrack, _sTrack, _vTrack;
        private Grid _hStrip, _sStrip, _vStrip;
        private TranslateTransform _hKnobX, _sKnobX, _vKnobX;
        private TextBlock _hVal, _sVal, _vVal;
        private WrapPanel _presetHost;
        private List<string> _presetList;

        private static string PresetsPath =>
            System.IO.Path.Combine(LayoutStore.Directory, "presets.json");

        public ColorPickerPanel(string initialHex)
        {
            Width = 230;
            // 左边吃掉菜单模板的 44px 图标列，取色器才能顶到菜单左缘铺开；
            // 内容各行自己留左边距（Grid 没有 Padding），别把输入框挤在墙上
            Margin = new Thickness(-44, 0, 4, 0);

            for (int i = 0; i < 7; i++)
                RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            BuildHeader();
            BuildRing();
            BuildStrips();
            BuildPresets();

            LoadPresets();
            RenderPresets();

            // 没设过颜色就从默认灰起调，别让取色器开在纯白上 —— 白色在深色板上看不见
            SetColor(TryParseHex(initialHex, out _) ? initialHex : "#3A3A3A");

            // 布局完成前滑条 ActualWidth 是 0，旋钮位置算不准；摆好后重算一次
            SizeChanged += (s, e) => RefreshAll();
        }

        // ================= 布局 =================

        private void BuildHeader()
        {
            var header = new Grid { Height = 26, Margin = new Thickness(Gutter, 0, 0, 2) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _hexBox = new TextBox
            {
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2)),
                Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x88, 0x88, 0x88)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 1),
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _hexBox.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                if (ApplyHexText()) Committed?.Invoke();
                e.Handled = true;
            };
            _hexBox.LostFocus += (s, e) => ApplyHexText();

            _preview = new Border
            {
                Width = 64,
                Height = 22,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                SnapsToDevicePixels = true
            };

            header.Children.Add(_hexBox);
            header.Children.Add(_preview);
            Grid.SetColumn(_hexBox, 0);
            Grid.SetColumn(_preview, 1);

            SetRow(header, 0);
            Children.Add(header);
        }

        private void BuildRing()
        {
            _ringHost = new Canvas
            {
                Width = RingSize,
                Height = RingSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 2),
                Cursor = Cursors.Hand,
                // 背景透明让整块画布接得住鼠标：色环线全关了命中测试（省 360 次命中），
                // 点环上的任何位置都落到画布本身，转色相才稳
                Background = Brushes.Transparent
            };

            double c = RingSize / 2;

            // 色环没有锥形渐变刷子可用，360 条半径线每条一个色相，拼出来就是环
            for (int deg = 0; deg < 360; deg++)
            {
                double rad = deg * Math.PI / 180;
                var line = new Line
                {
                    X1 = c + Math.Sin(rad) * RingOuter,
                    Y1 = c - Math.Cos(rad) * RingOuter,
                    X2 = c + Math.Sin(rad) * RingInner,
                    Y2 = c - Math.Cos(rad) * RingInner,
                    StrokeThickness = 2.5,
                    Stroke = new SolidColorBrush(FromHsv(deg, 1, 1)),
                    IsHitTestVisible = false
                };
                _ringHost.Children.Add(line);
            }

            // 轮心：饱和度(横) × 明度(竖) 方框。两层渐变叠加是经典做法：
            // 底层白→当前色相的纯色，上层从上到下透明→黑
            var sv = new Grid
            {
                Width = SvSize,
                Height = SvSize,
                // 背景透明才接得住整块方框的鼠标，别只靠里面那层渐变矩形
                Background = Brushes.Transparent,
                Cursor = Cursors.Cross
            };

            _svHue = new Rectangle { RadiusX = 2, RadiusY = 2 };
            var shade = new Rectangle
            {
                RadiusX = 2,
                RadiusY = 2,
                Fill = new LinearGradientBrush(
                    Color.FromArgb(0, 0, 0, 0), Colors.Black, new Point(0, 0), new Point(0, 1)),
                IsHitTestVisible = false
            };
            _svDot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                // 游标是 Grid 子元素，Canvas 附加属性对它无效 —— 用位移变换定位
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = new TranslateTransform()
            };
            _svDotPos = (TranslateTransform)_svDot.RenderTransform;

            sv.Children.Add(_svHue);
            sv.Children.Add(shade);
            sv.Children.Add(_svDot);

            _ringHost.Children.Add(sv);
            Canvas.SetLeft(sv, (RingSize - SvSize) / 2);
            Canvas.SetTop(sv, (RingSize - SvSize) / 2);

            _hueKnob = new Ellipse
            {
                Width = 16,
                Height = 16,
                Stroke = Brushes.White,
                StrokeThickness = 2.5,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false
            };
            _ringHost.Children.Add(_hueKnob);

            _ringHost.MouseLeftButtonDown += (s, e) =>
            {
                _ringHost.CaptureMouse();
                DragHue(e);
                e.Handled = true;
            };
            _ringHost.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragHue(e); };
            _ringHost.MouseLeftButtonUp += (s, e) =>
            {
                _ringHost.ReleaseMouseCapture();
                Committed?.Invoke();
                e.Handled = true;
            };

            sv.MouseLeftButtonDown += (s, e) =>
            {
                sv.CaptureMouse();
                DragSv(e);
                e.Handled = true;
            };
            // 拖方框时把 Move 吃掉：不冒到画布上，否则路过色环时旋钮会跟着跑
            sv.MouseMove += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed) DragSv(e);
                e.Handled = true;
            };
            sv.MouseLeftButtonUp += (s, e) =>
            {
                sv.ReleaseMouseCapture();
                Committed?.Invoke();
                e.Handled = true;
            };

            SetRow(_ringHost, 1);
            Children.Add(_ringHost);
        }

        private void BuildStrips()
        {
            MakeStripRow(2, "H", Color.FromRgb(0xE0, 0x6C, 0x6C),
                out _hStrip, out _hTrack, out _hKnobX, out _hVal);
            MakeStripRow(3, "S", Color.FromRgb(0x6F, 0xBF, 0x73),
                out _sStrip, out _sTrack, out _sKnobX, out _sVal);
            MakeStripRow(4, "V", Color.FromRgb(0x6C, 0x9B, 0xE0),
                out _vStrip, out _vTrack, out _vKnobX, out _vVal);
        }

        private void MakeStripRow(int row, string label, Color tint,
                                  out Grid stripHost, out Border track,
                                  out TranslateTransform knobX, out TextBlock readout)
        {
            var g = new Grid { Height = StripHeight + 8, Margin = new Thickness(Gutter, 1, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            var tag = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(tint),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 滑条本体是 Grid：背景 Transparent 才接得到鼠标，轨道和旋钮都摆里面
            var strip = new Grid
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Stretch
            };

            track = new Border
            {
                Height = StripHeight,
                CornerRadius = new CornerRadius(StripHeight / 2),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x88, 0x88, 0x88)),
                VerticalAlignment = VerticalAlignment.Center
            };

            knobX = new TranslateTransform();
            var knob = new Ellipse
            {
                Width = KnobSize,
                Height = KnobSize,
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = knobX,
                IsHitTestVisible = false
            };

            strip.Children.Add(track);
            strip.Children.Add(knob);

            readout = new TextBlock
            {
                Text = "0",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            strip.MouseLeftButtonDown += (s, e) =>
            {
                strip.CaptureMouse();
                DragStrip(strip, t => { SetChannel(label, t); });
                e.Handled = true;
            };
            strip.MouseMove += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed) DragStrip(strip, t => { SetChannel(label, t); });
            };
            strip.MouseLeftButtonUp += (s, e) =>
            {
                strip.ReleaseMouseCapture();
                Committed?.Invoke();
                e.Handled = true;
            };

            stripHost = strip;

            g.Children.Add(tag);
            g.Children.Add(strip);
            g.Children.Add(readout);
            Grid.SetColumn(tag, 0);
            Grid.SetColumn(strip, 1);
            Grid.SetColumn(readout, 2);

            // 宽度在布局前是 0，摆好了才补得准旋钮位置
            strip.SizeChanged += (s, e) => RefreshAll();

            SetRow(g, row);
            Children.Add(g);
        }

        private void BuildPresets()
        {
            _presetHost = new WrapPanel { Margin = new Thickness(Gutter, 1, 0, 0) };

            // 「样本」小标题压在色块上面，跟参考图一个层次
            var cap = new TextBlock
            {
                Text = "样本",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                FontSize = 11,
                Margin = new Thickness(Gutter, 6, 0, 1)
            };
            SetRow(cap, 5);
            SetRow(_presetHost, 6);
            Children.Add(cap);
            Children.Add(_presetHost);
        }

        private void RenderPresets()
        {
            _presetHost.Children.Clear();

            foreach (string hex in _presetList)
            {
                string pinned = hex;
                if (!TryParseHex(pinned, out Color color)) continue;

                var chip = new Border
                {
                    Width = 26,
                    Height = 20,
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(color),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x88, 0x88, 0x88)),
                    Cursor = Cursors.Hand,
                    ToolTip = pinned
                };
                chip.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    SetColor(pinned);
                    Push();
                    Committed?.Invoke();
                };
                _presetHost.Children.Add(chip);
            }

            // 「+ 存当前色」的样本块，参考图右下角那个加号
            var add = new Border
            {
                Width = 26,
                Height = 20,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88)),
                Cursor = Cursors.Hand,
                ToolTip = "把当前颜色存进样本"
            };
            add.Child = new TextBlock
            {
                Text = "+",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            add.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                string hex = HexOf(CurrentColor);
                if (_presetList.Exists(p => string.Equals(p, hex, StringComparison.OrdinalIgnoreCase))) return;
                if (_presetList.Count >= 40) _presetList.RemoveAt(_presetList.Count - 1);
                _presetList.Insert(0, hex);
                SavePresets();
                RenderPresets();
            };
            _presetHost.Children.Add(add);
        }

        // ================= 交互 =================

        private void DragHue(MouseEventArgs e)
        {
            // 谁在拖方框（捕获在别人手里）就不轮到色相说话
            if (Mouse.Captured != null && Mouse.Captured != _ringHost) return;

            var p = e.GetPosition(_ringHost);
            double dx = p.X - RingSize / 2;
            double dy = p.Y - RingSize / 2;

            // 轮心方框是 SV 的地盘：点在里面不算转色相，防事件漏过来抢
            double r = Math.Sqrt(dx * dx + dy * dy);
            if (r < RingInner - 2) return;

            // 12 点钟方向为 0°，顺时针
            _h = (Math.Atan2(dx, -dy) * 180 / Math.PI + 360) % 360;
            Push();
        }

        private void DragSv(MouseEventArgs e)
        {
            var p = e.GetPosition(_svHue);
            _s = Math.Clamp(p.X / SvSize, 0, 1);
            _v = Math.Clamp(1 - p.Y / SvSize, 0, 1);
            Push();
        }

        private void DragStrip(Grid strip, Action<double> set)
        {
            double w = strip.ActualWidth;
            if (w <= 0) return;
            var p = Mouse.GetPosition(strip);
            set(Math.Clamp(p.X / w, 0, 1));
        }

        private void SetChannel(string label, double t)
        {
            switch (label)
            {
                case "H": _h = t * 360; break;
                case "S": _s = t; break;
                case "V": _v = t; break;
            }
            Push();
        }

        private bool ApplyHexText()
        {
            string text = _hexBox.Text?.Trim() ?? "";
            if (!TryParseHex(text, out Color c))
            {
                _hexBox.Text = HexOf(CurrentColor);   // 输得不合法就还原，别让人卡在坏状态
                return false;
            }

            SetColor(HexOf(c));
            Push();
            return true;
        }

        /// <summary>外部设色（打开菜单 / 点样本 / 输十六进制）。不动事件，只刷界面。</summary>
        public void SetColor(string hex)
        {
            if (TryParseHex(hex, out Color c))
            {
                ToHsv(c, out double h, out double s, out double v);
                _s = s;
                _v = v;
                // 灰、黑没有色相：保留轮子上的色相，别把人拽回红色
                if (s > 0.001 && v > 0.001) _h = h;
            }
            RefreshAll();
        }

        private void Push()
        {
            RefreshAll();
            ColorChanged?.Invoke(HexOf(CurrentColor));
        }

        // ================= 渲染 =================

        public Color CurrentColor => FromHsv(_h, _s, _v);

        private void RefreshAll()
        {
            var c = CurrentColor;

            _preview.Background = new SolidColorBrush(c);
            if (!_hexBox.IsFocused) _hexBox.Text = HexOf(c);

            // 色相旋钮
            double rad = _h * Math.PI / 180;
            double cx = RingSize / 2;
            Canvas.SetLeft(_hueKnob, cx + Math.Sin(rad) * HueOrbit - _hueKnob.Width / 2);
            Canvas.SetTop(_hueKnob, cx - Math.Cos(rad) * HueOrbit - _hueKnob.Height / 2);

            // 方框游标：左上角对齐 + 位移 = 饱和度向右、明度向下
            _svDotPos.X = _s * SvSize - _svDot.Width / 2;
            _svDotPos.Y = (1 - _v) * SvSize - _svDot.Height / 2;

            // 三条轨道的渐变随色相/饱和/明度变
            _hTrack.Background = RainbowGradient();
            _sTrack.Background = new LinearGradientBrush(FromHsv(_h, 0, _v), FromHsv(_h, 1, _v), 0);
            _vTrack.Background = new LinearGradientBrush(FromHsv(_h, _s, 0), FromHsv(_h, _s, 1), 0);
            _svHue.Fill = new LinearGradientBrush(Colors.White, FromHsv(_h, 1, 1), 0);

            _hVal.Text = Math.Round(_h) + "°";
            _sVal.Text = Math.Round(_s * 100) + "";
            _vVal.Text = Math.Round(_v * 100) + "";

            PlaceKnob(_hStrip, _hKnobX, _h / 360);
            PlaceKnob(_sStrip, _sKnobX, _s);
            PlaceKnob(_vStrip, _vKnobX, _v);
        }

        /// <summary>旋钮左缘 = t ×（条宽 − 旋钮宽）。布局完成前条宽是 0，摆好后 SizeChanged 会再刷一遍。</summary>
        private static void PlaceKnob(Grid strip, TranslateTransform knob, double t)
        {
            double w = strip.ActualWidth;
            knob.X = w <= 0 ? 0 : Math.Clamp(t, 0, 1) * (w - KnobSize);
        }

        // ================= HSV / RGB =================

        /// <summary>HSV → RGB。h 单位度，s/v 0-1。</summary>
        public static Color FromHsv(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double cc = v * s;
            double hp = h / 60;
            double x = cc * (1 - Math.Abs(hp % 2 - 1));

            double r = 0, g = 0, b = 0;
            switch ((int)hp % 6)
            {
                case 0: r = cc; g = x; break;
                case 1: r = x; g = cc; break;
                case 2: g = cc; b = x; break;
                case 3: g = x; b = cc; break;
                case 4: r = x; b = cc; break;
                case 5: r = cc; b = x; break;
            }

            double m = v - cc;
            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        /// <summary>RGB → HSV。</summary>
        public static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double d = max - min;

            v = max;
            s = max <= 0 ? 0 : d / max;

            h = 0;
            if (d > 0)
            {
                if (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * ((b - r) / d + 2);
                else h = 60 * ((r - g) / d + 4);
            }
            if (h < 0) h += 360;
        }

        private static string HexOf(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static bool TryParseHex(string hex, out Color c)
        {
            c = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;

            string s = hex.Trim().TrimStart('#');
            if (s.Length != 6 && s.Length != 8) return false;

            try
            {
                byte r = Convert.ToByte(s.Substring(s.Length - 6, 2), 16);
                byte g = Convert.ToByte(s.Substring(s.Length - 4, 2), 16);
                byte b = Convert.ToByte(s.Substring(s.Length - 2, 2), 16);
                c = Color.FromRgb(r, g, b);
                return true;
            }
            catch { return false; }
        }

        private static Brush RainbowGradient()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            for (int i = 0; i <= 6; i++)
                brush.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));
            return brush;
        }

        // ================= 样本持久化 =================

        private void LoadPresets()
        {
            _presetList = new List<string>(DefaultPresets);

            try
            {
                if (!File.Exists(PresetsPath)) return;
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PresetsPath));
                if (list != null && list.Count > 0) _presetList = list;
            }
            catch { /* 读不出来就用种子 */ }
        }

        private void SavePresets()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PresetsPath) ?? ".");
                File.WriteAllText(PresetsPath, JsonSerializer.Serialize(_presetList));
            }
            catch { /* 存盘失败不阻断 UI */ }
        }
    }
}
