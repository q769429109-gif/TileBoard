using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TileBoard.Native;

namespace TileBoard.Services
{
    /// <summary>
    /// 应用清单与图标缓存。
    /// 清单来源是 shell:AppsFolder —— 和开始菜单同源，UWP 与 Win32 一视同仁。
    /// </summary>
    public class AppCatalog
    {
        private readonly Dictionary<string, NativeShell.AppEntry> _byAumid =
            new Dictionary<string, NativeShell.AppEntry>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, BitmapSource> _iconCache =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        private readonly object _iconLock = new object();

        public List<NativeShell.AppEntry> All { get; private set; } = new List<NativeShell.AppEntry>();

        public bool Loaded { get; private set; }

        /// <summary>后台线程枚举全部应用，避免首帧卡住。</summary>
        public Task LoadAsync()
        {
            return Task.Run(() =>
            {
                var list = NativeShell.EnumerateApps()
                    .GroupBy(a => a.Aumid, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderBy(a => a.Name, StringComparer.CurrentCulture)
                    .ToList();

                All = list;
                _byAumid.Clear();
                foreach (var e in list) _byAumid[e.Aumid] = e;
                Loaded = true;
            });
        }

        public bool Contains(string aumid) =>
            !string.IsNullOrEmpty(aumid) && _byAumid.ContainsKey(aumid);

        /// <summary>取显示名；本机没有这个应用时退回文件名或 AUMID 尾段。</summary>
        public string ResolveName(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "未知应用";
            if (_byAumid.TryGetValue(aumid, out var e)) return e.Name;

            if (NativeShell.LooksLikePath(aumid))
            {
                try { return System.IO.Path.GetFileNameWithoutExtension(aumid); }
                catch { return aumid; }
            }

            int bang = aumid.IndexOf('!');
            string tail = bang > 0 ? aumid.Substring(bang + 1) : aumid;
            int dot = tail.LastIndexOf('.');
            return dot > 0 && dot < tail.Length - 1 ? tail.Substring(dot + 1) : tail;
        }

        /// <summary>取图标（已 Freeze，可跨线程使用）。取不到返回 null。</summary>
        public BitmapSource GetIcon(string aumid, int pixels = 256)
        {
            if (string.IsNullOrEmpty(aumid)) return null;

            lock (_iconLock)
            {
                string key = aumid + "|" + pixels;
                if (_iconCache.TryGetValue(key, out var cached)) return cached;
            }

            BitmapSource result = ExtractIconChecked(aumid, pixels);

            lock (_iconLock)
            {
                _iconCache[aumid + "|" + pixels] = result;
            }
            return result;
        }

        /// <summary>
        /// 带质检的图标提取链。
        ///
        /// 坑：shell 对「原生图标太小、撑不起大尺寸请求」的文件（HMCL-3.5.4.exe 只有一个
        /// 16px 级图标帧），GetImage 不放大而是返回「白页框 + 小图标居中」的文件占位缩略图 ——
        /// 磁贴上就是一个小点。所以提取结果必须量不透明覆盖率：覆盖率过低判定为假图标，
        /// 依次降级：包内 logo → 按名字找 lnk 读目标 exe → 48px 真图标（磁贴按 48 DIP 显示，
        /// WPF 高质量放大，和资源管理器大图标视图同款处理）。
        /// </summary>
        private BitmapSource ExtractIconChecked(string aumid, int pixels)
        {
            IntPtr hbmp = NativeShell.GetAppImageHandle(aumid, pixels);
            BitmapSource bmp = ToBitmap(hbmp);
            NativeShell.FreeBitmap(hbmp);

            if (bmp != null && OpaqueCoverage(bmp) >= 0.35)
                return bmp;

            // shell 给的是假图标（或没有）：包内 logo（UWP）
            if (aumid.Contains('!'))
            {
                var packaged = ShellActions.GetPackagedAppIcon(aumid, pixels);
                if (packaged != null) return packaged;
            }

            // 按名字找同名 lnk，对目标 exe 提 icon（Office C2R 这类）
            string target = ShellActions.FindShortcutTargetByName(ResolveName(aumid));
            if (target != null)
            {
                hbmp = NativeShell.GetAppImageHandle(target, pixels);
                var viaLnk = ToBitmap(hbmp);
                NativeShell.FreeBitmap(hbmp);
                if (viaLnk != null && OpaqueCoverage(viaLnk) >= 0.35) return viaLnk;
            }

            // 最后的救命稻草：小尺寸请求能逼 shell 放大原生迷你图标（ICONONLY@48 实测有效）
            hbmp = NativeShell.GetSmallIconHandle(aumid, 48);
            var small = ToBitmap(hbmp);
            NativeShell.FreeBitmap(hbmp);
            if (small != null && OpaqueCoverage(small) >= 0.3) return small;

            return bmp;   // 兜底到底都失败：至少把第一张（哪怕是假的）交出去，别给空白
        }

        private static BitmapSource ToBitmap(IntPtr hbmp)
        {
            if (hbmp == IntPtr.Zero) return null;
            try
            {
                var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hbmp, IntPtr.Zero,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 不透明像素占比。正常图标 0.4+；「文件框缩略图」这类假图标只有 0.1~0.2 的框线。
        /// </summary>
        internal static double OpaqueCoverage(BitmapSource bmp)
        {
            try
            {
                int w = bmp.PixelWidth, h = bmp.PixelHeight;
                if (w == 0 || h == 0) return 0;
                int stride = w * 4;
                var px = new byte[stride * h];
                bmp.CopyPixels(px, stride, 0);

                long opaque = 0;
                for (int i = 3; i < px.Length; i += 4)
                    if (px[i] >= 16) opaque++;

                return opaque / (double)(w * h);
            }
            catch
            {
                return 1;   // 量不了就当它是真图标，别误杀
            }
        }

        /// <summary>按名字哈希出一个稳定的磁贴底色，模拟 Win10 每应用一个主题色。</summary>
        public static Color AccentFor(string aumid)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in aumid ?? "?") h = h * 31 + c;
                double hue = Math.Abs(h) % 360;
                return FromHsl(hue, 0.50, 0.30);   // 中饱和深色，接近 Win10 磁贴观感
            }
        }

        private static Color FromHsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = l - c / 2;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }
    }
}
