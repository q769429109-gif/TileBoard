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

            BitmapSource result = null;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                hbmp = NativeShell.GetAppImageHandle(aumid, pixels);

                // shell 对一批桌面桥应用（Office C2R / 老 Edge / Skype）出不了图，
                // 直接进包里从 manifest 声明的 exe / logo 提
                if (hbmp == IntPtr.Zero && aumid.Contains('!'))
                {
                    var packaged = ShellActions.GetPackagedAppIcon(aumid, pixels);
                    if (packaged != null)
                    {
                        lock (_iconLock) _iconCache[aumid + "|" + pixels] = packaged;
                        return packaged;
                    }
                }

                // Office C2R 这类 AUMID 的 AppsFolder 项 shell 解析不了资产，且
                // .lnk 里也不存 AUMID 属性 —— 按名字找同名 lnk，对目标 exe 提 icon
                if (hbmp == IntPtr.Zero)
                {
                    string target = ShellActions.FindShortcutTargetByName(ResolveName(aumid));
                    if (target != null) hbmp = NativeShell.GetAppImageHandle(target, pixels);
                }

                if (hbmp != IntPtr.Zero)
                {
                    result = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hbmp, IntPtr.Zero,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    result.Freeze();
                }
            }
            catch
            {
                result = null;
            }
            finally
            {
                NativeShell.FreeBitmap(hbmp);
            }

            lock (_iconLock)
            {
                _iconCache[aumid + "|" + pixels] = result;
            }
            return result;
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
