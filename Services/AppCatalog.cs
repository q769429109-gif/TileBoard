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

        // 枚举结果落盘的缓存。shell:AppsFolder 有四百多项，每项都要走一次 COM
        // 属性读取，实测 2.2 秒 —— 启动时等不起，先读上次的结果顶上。
        private static string CachePath =>
            System.IO.Path.Combine(LayoutStore.Directory, "apps.cache.json");

        private sealed class CacheRow
        {
            public string Aumid { get; set; }
            public string Name { get; set; }

            /// <summary>第一次在本机见到它的时间（Unix 毫秒）。0 = 缓存格式升级前就在的老应用。</summary>
            public long FirstSeen { get; set; }
        }

        /// <summary>
        /// 每个应用「第一次被枚举到」的时间。Win10 开始菜单顶部那块「最近添加」就是
        /// 靠它挑人的 —— 我们这边 shell:AppsFolder 给不出安装时间，只能自己记。
        ///
        /// 缓存格式升级前就存在的应用一律记 0（老应用），不参与「最近添加」；
        /// 只有装完软件后重新枚举才第一次出现在清单里的，才拿到真实时间。
        /// 这样第一次跑这版不会四百多个应用全被当成新的。
        /// </summary>
        private readonly Dictionary<string, long> _firstSeen =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 还没有任何可信时间戳（全新安装，或旧缓存里 FirstSeen 全是 0）：
        /// 下一次真枚举时要补一轮基线 —— 桌面应用问 exe 的创建时间，
        /// 而不是把四百多个应用统统记成「刚装」。
        /// 首屏读缓存那一步不能做（一百多次文件 IO 会堵住启动），只有后台枚举才补。
        /// </summary>
        private bool _needsBaselineBackfill = true;

        public long FirstSeenOf(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return 0;
            return _firstSeen.TryGetValue(aumid, out var t) ? t : 0;
        }

        /// <summary>
        /// 最近添加的应用，新的在前。窗口天数与条数按 Win10 的观感取：
        /// 三十天内装的东西才叫「最近」，一次最多列出八条，再多就把列表顶出一屏。
        /// </summary>
        public List<NativeShell.AppEntry> RecentAdded(int days = 30, int max = 8)
        {
            long since = DateTimeOffset.Now.AddDays(-days).ToUnixTimeMilliseconds();

            return All
                .Where(a => a != null && FirstSeenOf(a.Aumid) >= since)
                .OrderByDescending(a => FirstSeenOf(a.Aumid))
                .Take(max)
                .ToList();
        }

        /// <summary>
        /// 读上次枚举的清单（几百条 JSON，几毫秒）。首屏只靠它就能算名字和图标，
        /// 真正的 2.2 秒枚举留给后台刷新。
        /// </summary>
        public void LoadCache()
        {
            try
            {
                if (!System.IO.File.Exists(CachePath)) return;

                var rows = System.Text.Json.JsonSerializer.Deserialize<List<CacheRow>>(
                    System.IO.File.ReadAllText(CachePath));
                if (rows == null || rows.Count == 0) return;

                // 整份记录都是 0 = 这套机制之前写的旧缓存，还没有任何时间戳。
                // 补基线的活留给后台那轮枚举（见 Apply），首屏不做文件 IO
                _needsBaselineBackfill = rows.All(r => r.FirstSeen == 0);

                foreach (var r in rows)
                    if (!string.IsNullOrEmpty(r.Aumid)) _firstSeen[r.Aumid] = r.FirstSeen;

                var list = rows
                    .Where(r => !string.IsNullOrEmpty(r.Aumid))
                    .Select(r => new NativeShell.AppEntry { Aumid = r.Aumid, Name = r.Name ?? "" })
                    .OrderBy(a => a.Name, StringComparer.CurrentCulture)
                    .ToList();

                if (list.Count > 0) Apply(list, stamp: false);
            }
            catch { }
        }

        /// <param name="stamp">
        /// 要不要顺手记时间戳。读缓存那一步传 false —— 字典里已经是缓存里的值，
        /// 再走一遍补基线就是一百多次文件 IO，而这时候还在启动路径上。
        /// </param>
        private void Apply(List<NativeShell.AppEntry> list, bool stamp = true)
        {
            if (stamp)
            {
                long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();

                foreach (var e in list)
                {
                    if (string.IsNullOrEmpty(e.Aumid)) continue;

                    bool known = _firstSeen.TryGetValue(e.Aumid, out long seen);

                    if (_needsBaselineBackfill)
                    {
                        // 补基线：0 表示「不知道什么时候装的」，先问 exe 自己；
                        // 问不出来（UWP）就留 0，当老应用，不进最近添加
                        if (seen <= 0)
                        {
                            long guess = GuessInstalledAt(e.Aumid);
                            if (guess > 0) _firstSeen[e.Aumid] = guess;
                            else if (!known) _firstSeen[e.Aumid] = 0;
                        }
                    }
                    else if (!known)
                    {
                        // 清单里新冒出来的：不管 UWP 还是桌面应用，它就是「刚装上」
                        _firstSeen[e.Aumid] = now;
                    }
                }

                _needsBaselineBackfill = false;
            }

            All = list;
            _byAumid.Clear();
            foreach (var e in list) _byAumid[e.Aumid] = e;
            Loaded = true;
        }

        /// <summary>
        /// 猜一个桌面应用的安装时间。shell:AppsFolder 不提供安装日期，UWP 那边更拿不到，
        /// 所以只对「Aumid 就是路径」的桌面应用取 exe 的创建时间 —— 装软件时它才被写出来，
        /// 是能拿到的最接近的答案。UWP 与原始资料都给不出的，记 0（当老应用，不进最近添加）。
        /// </summary>
        private static long GuessInstalledAt(string aumid)
        {
            try
            {
                if (!NativeShell.LooksLikePath(aumid)) return 0;

                var info = new System.IO.FileInfo(aumid);
                if (!info.Exists) return 0;

                return new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds();
            }
            catch { return 0; }
        }

        private void SaveCache(List<NativeShell.AppEntry> list)
        {
            try
            {
                System.IO.Directory.CreateDirectory(LayoutStore.Directory);
                var rows = list
                    .Where(a => !string.IsNullOrEmpty(a.Aumid))
                    .Select(a => new CacheRow
                    {
                        Aumid = a.Aumid,
                        Name = a.Name,
                        FirstSeen = FirstSeenOf(a.Aumid)
                    })
                    .ToList();
                System.IO.File.WriteAllText(CachePath,
                    System.Text.Json.JsonSerializer.Serialize(rows));
            }
            catch { }
        }

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

                Apply(list);
                SaveCache(list);
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
        /// <summary>图标提取累计耗时/次数（缓存命中不计）。启动日志用它定位卡点。</summary>
        public static long IconTotalMs;
        public static int IconExtractCount;

        /// <summary>
        /// 图标磁盘缓存的目录版本。改提取逻辑后把版本号 +1，旧缓存自然作废。
        /// </summary>
        private const string IconCacheVersion = "v2";

        private static string IconCacheDir =>
            System.IO.Path.Combine(LayoutStore.Directory, "icons-" + IconCacheVersion);

        private static string IconCacheFile(string aumid, int pixels)
        {
            // AUMID 里有 `!`、`:`、反斜杠，当文件名会炸；哈希成 40 位十六进制最省心
            byte[] hash = System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(aumid + "|" + pixels));

            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return System.IO.Path.Combine(IconCacheDir, sb + ".png");
        }

        private static BitmapSource TryLoadIconCache(string aumid, int pixels)
        {
            try
            {
                string file = IconCacheFile(aumid, pixels);
                if (!System.IO.File.Exists(file)) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                // 读完立刻释放句柄 —— 否则缓存文件一直被这个进程锁着，清不掉
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(file);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static void SaveIconCache(string aumid, int pixels, BitmapSource src)
        {
            try
            {
                if (src == null) return;
                System.IO.Directory.CreateDirectory(IconCacheDir);

                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                using (var fs = System.IO.File.Create(IconCacheFile(aumid, pixels)))
                    enc.Save(fs);
            }
            catch { }
        }

        public BitmapSource GetIcon(string aumid, int pixels = 256)
        {
            if (string.IsNullOrEmpty(aumid)) return null;

            string key = aumid + "|" + pixels;
            lock (_iconLock)
            {
                if (_iconCache.TryGetValue(key, out var cached)) return cached;
            }

            // 先看盘上的缓存。从 shell 提一个图标实测 74ms（94 个磁贴就是 7 秒，
            // 占了整个启动的 92%），读一张 PNG 只要 1~2ms —— 首次提取后落盘，
            // 之后每次启动都是读图，启动时间从 9.6 秒降到 2.6 秒上下。
            BitmapSource result = TryLoadIconCache(aumid, pixels);

            if (result == null)
            {
                var swIcon = System.Diagnostics.Stopwatch.StartNew();
                result = ExtractIconChecked(aumid, pixels);
                System.Threading.Interlocked.Add(ref IconTotalMs, swIcon.ElapsedMilliseconds);
                System.Threading.Interlocked.Increment(ref IconExtractCount);

                if (result != null) SaveIconCache(aumid, pixels, result);
            }

            lock (_iconLock)
            {
                _iconCache[key] = result;
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
            // steam:// 协议项必须先走 Steam 自己的数据 —— shell 给它的「白页+地球」
            // 通用 URL 图标覆盖率有 0.65，能混过质检线，但它是假的（Explorer 也是靠
            // .url 的 IconFile 才显示封面的）。
            if (aumid.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase))
            {
                var steam = SteamGameIcon(aumid, pixels);
                if (steam != null) return steam;
            }

            IntPtr hbmp = NativeShell.GetAppImageHandle(aumid, pixels);
            BitmapSource bmp = ToBitmap(hbmp);
            NativeShell.FreeBitmap(hbmp);

            if (bmp != null && OpaqueCoverage(bmp) >= 0.35)
                return bmp;

            // 磁盘上的文件夹：图标就是「资源管理器里那个」，不许再按名字去猜快捷方式。
            // D:\game 这种名字一旦跟某个桌面快捷方式撞上，猜出来的图标会顶掉文件夹
            // 自己在 desktop.ini 里指定的那一个 —— 明明 shell 已经给得出来。
            if (System.IO.Directory.Exists(aumid))
            {
                IntPtr dirSmall = NativeShell.GetSmallIconHandle(aumid, 48);
                BitmapSource dirIcon = ToBitmap(dirSmall);
                NativeShell.FreeBitmap(dirSmall);

                // 48px 是这类自定义文件夹图标的原生尺寸（desktop.ini 指向的 dll 里
                // 往往只有一两档小图），磁贴按 48 DIP 显示，交给 WPF 高质量放大
                if (dirIcon != null && OpaqueCoverage(dirIcon) >= 0.2) return dirIcon;

                return bmp;
            }

            // shell 给的是假图标（或没有）：包内 logo（UWP）
            if (aumid.Contains('!'))
            {
                var packaged = ShellActions.GetPackagedAppIcon(aumid, pixels);
                if (packaged != null) return packaged;
            }

            // 快捷方式自带的图标源：Steam 游戏封面藏在开始菜单 .url 的 IconFile 键里，
            // 安装器生成的 .lnk 则写在 IconLocation —— 都比「目标 exe 的图标」更贴本尊
            string iconSource = ShellActions.FindShortcutIconByName(ResolveName(aumid));
            if (iconSource != null)
            {
                hbmp = NativeShell.GetAppImageHandle(iconSource, pixels);
                var viaIconFile = ToBitmap(hbmp);
                NativeShell.FreeBitmap(hbmp);
                if (viaIconFile != null && OpaqueCoverage(viaIconFile) >= 0.35) return viaIconFile;
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

        private static string _steamRoot;

        /// <summary>Steam 安装根目录（HKCU\Software\Valve\Steam\SteamPath），找不到返回 null。</summary>
        private static string SteamRoot()
        {
            if (_steamRoot != null) return _steamRoot;

            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (k?.GetValue("SteamPath") is string p && System.IO.Directory.Exists(p))
                    _steamRoot = p;
            }
            catch { /* 没装 Steam */ }
            return _steamRoot;
        }

        /// <summary>
        /// 从 Steam 本地数据里捞游戏图标（steam://rungameid/&lt;appid&gt; 专用）。
        /// 优先级：librarycache 方图（32px 原生，喂列表）→ 游戏本体 exe 的图标资源
        /// （appmanifest 定位安装目录，喂磁贴）→ logo.png。全空返回 null。
        /// </summary>
        private BitmapSource SteamGameIcon(string aumid, int pixels)
        {
            string root = SteamRoot();
            if (root == null) return null;

            string appid = aumid.Substring("steam://rungameid/".Length).Trim();

            try
            {
                string lcDir = System.IO.Path.Combine(root, "appcache", "librarycache", appid);
                bool haveCache = System.IO.Directory.Exists(lcDir);

                // 方图是唯一来源：应用库和磁贴必须显示同一张图。
                // 之前大尺寸改走「游戏目录里体积最大的 exe」，而 Unity 游戏的
                // UnityCrashHandler64.exe 往往最大 —— 磁贴上就成了白色立方体，
                // 和应用库里的游戏标志完全对不上。用户要求以应用库为准，改回来。
                string square = haveCache ? FindSteamSquare(lcDir) : null;
                if (square != null) return LoadImageFile(square, pixels);

                // 缓存目录里没有方图（游戏没被 Steam 缓存过）：退回 exe，
                // 但排掉崩溃器/卸载器，别再把别的程序的图标顶上来
                string vdf = System.IO.Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (System.IO.File.Exists(vdf))
                {
                    var text = System.IO.File.ReadAllText(vdf);
                    var libMatch = new System.Text.RegularExpressions.Regex("\"path\"\\s+\"([^\"]+)\"");
                    var dirMatch = new System.Text.RegularExpressions.Regex("\"installdir\"\\s+\"([^\"]+)\"");

                    foreach (System.Text.RegularExpressions.Match lib in libMatch.Matches(text))
                    {
                        string acf = System.IO.Path.Combine(
                            lib.Groups[1].Value.Replace("\\\\", "\\"),
                            "steamapps", $"appmanifest_{appid}.acf");
                        if (!System.IO.File.Exists(acf)) continue;

                        var m = dirMatch.Match(System.IO.File.ReadAllText(acf));
                        if (!m.Success) continue;

                        string gameDir = System.IO.Path.Combine(
                            lib.Groups[1].Value.Replace("\\\\", "\\"),
                            "steamapps", "common", m.Groups[1].Value);
                        if (!System.IO.Directory.Exists(gameDir)) continue;

                        // 主 exe 通常是体积最大的那个，试前三个
                        var exes = System.IO.Directory.EnumerateFiles(gameDir, "*.exe")
                            .Where(f => !IsJunkExe(f))
                            .OrderByDescending(f => new System.IO.FileInfo(f).Length)
                            .Take(3);

                        foreach (var exe in exes)
                        {
                            IntPtr hbmp = NativeShell.GetAppImageHandle(exe, pixels);
                            var viaExe = ToBitmap(hbmp);
                            NativeShell.FreeBitmap(hbmp);
                            if (viaExe != null && OpaqueCoverage(viaExe) >= 0.35) return viaExe;
                        }
                    }
                }

                // 最后：logo（透明底艺术字）
                string logo = haveCache ? FindSteamLogo(lcDir) : null;
                if (logo != null)
                {
                    var lg = LoadImageFile(logo, pixels);
                    if (lg != null) return lg;
                }
            }
            catch { /* Steam 数据读不动就走常规兜底 */ }

            return null;
        }

        /// <summary>librarycache 里那张 40 位十六进制命名的方图（原生 32x32）。</summary>
        private static string FindSteamSquare(string lcDir)
        {
            try
            {
                foreach (var f in System.IO.Directory.EnumerateFiles(lcDir, "*.jpg"))
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(f);
                    if (name.Length == 40 && IsHex(name)) return f;
                }
            }
            catch { }
            return null;
        }

        private static string FindSteamLogo(string lcDir)
        {
            try
            {
                string direct = System.IO.Path.Combine(lcDir, "logo.png");
                if (System.IO.File.Exists(direct)) return direct;

                foreach (var f in System.IO.Directory.EnumerateFiles(
                             lcDir, "*", System.IO.SearchOption.AllDirectories))
                {
                    string fn = System.IO.Path.GetFileName(f);
                    if (fn.StartsWith("logo", StringComparison.OrdinalIgnoreCase) &&
                        fn.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        return f;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 这些 exe 的图标不属于游戏本体：Unity 的崩溃器是白色立方体，卸载器和运行库
        /// 安装器各带自己的标志。按体积倒序时它们经常排在最前，直接顶掉真图标。
        /// </summary>
        private static bool IsJunkExe(string path)
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(path);
            foreach (string bad in new[] { "crash", "handler", "unins", "redist",
                                           "setup", "report", "diagnostic" })
                if (n.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static bool IsHex(string s)
        {
            foreach (char c in s)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        /// <summary>按目标宽度解码 png/jpg（WPF 直接读，无需 shell）。</summary>
        private static BitmapSource LoadImageFile(string path, int pixels)
        {
            try
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;

                // 只在「缩小」时按目标宽度解码，省内存。源本来就比目标小（Steam 的
                // 方图只有 32px）就保持原尺寸，放大交给 Image 那边的 HighQuality —
                // 解码器的插值比 WPF 的 Fant 糙，先放大一次再缩放会糊两层。
                int srcW = SourcePixelWidth(path);
                if (srcW <= 0 || srcW > pixels)
                    bi.DecodePixelWidth = Math.Max(16, pixels);

                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>只解析文件头拿宽度，不解码像素。</summary>
        private static int SourcePixelWidth(string path)
        {
            try
            {
                using var fs = System.IO.File.OpenRead(path);
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                    fs,
                    System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                    System.Windows.Media.Imaging.BitmapCacheOption.None);
                return frame.PixelWidth;
            }
            catch { return 0; }
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
