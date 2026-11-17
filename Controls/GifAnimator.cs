using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TileBoard.Controls
{
    /// <summary>
    /// 自定义图片里的动图：把 gif 的全部帧解出来，挂在一个共享计时器上逐帧换 Source。
    /// 每块磁贴的 Image 各记自己的进度，所以不是「同频闪烁」；
    /// 计时器的间隔跟着「最近要换的那一帧」走，没人播就停掉，不空转。
    /// </summary>
    internal static class GifAnimator
    {
        private const int MaxFrames = 400;                   // 帧数超过这个数就只当静态图，别把内存吃光
        private const long MaxFramePixels = 4096L * 4096L;   // 单帧像素上限同理
        private const int MinInterval = 10;
        private const int MaxInterval = 100;

        private sealed class FrameSet
        {
            public BitmapSource[] Images;
            public int[] Delays;      // 每帧停留的毫秒数
        }

        private sealed class Cursor
        {
            public FrameSet Set;
            public int Index;
            public int Elapsed;       // 当前这帧已经走了多少毫秒
        }

        // 值可能是 null：表示「这个路径不是动图」，记住它，免得每次重建磁贴都去读一遍盘
        private static readonly Dictionary<string, FrameSet> Cache =
            new Dictionary<string, FrameSet>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<Image, Cursor> Playing =
            new Dictionary<Image, Cursor>();

        private static DispatcherTimer _timer;
        private static DateTime _last;

        /// <summary>
        /// 给一张自定义图片挂上播放。不是动图（静态图 / 打不开 / 只有一帧）就什么都不做，
        /// 保留调用方已经设好的第一帧。
        /// </summary>
        public static void Attach(Image image, string path)
        {
            if (image == null || string.IsNullOrWhiteSpace(path)) return;
            if (Playing.ContainsKey(image)) return;

            FrameSet set = Load(path);
            if (set == null || set.Images.Length < 2) return;

            if (image.IsLoaded)
            {
                Register(image, set);
                return;
            }

            // 还没进可视化树：等它 Loaded 再登记。建了却没挂上去的元素（重建界面时的中间态）
            // 就不会占着一个播放位，也就不会把计时器一直顶着空转
            RoutedEventHandler onLoaded = null;
            onLoaded = (s, e) =>
            {
                image.Loaded -= onLoaded;
                if (!Playing.ContainsKey(image)) Register(image, set);
            };
            image.Loaded += onLoaded;
        }

        private static void Register(Image image, FrameSet set)
        {
            Playing[image] = new Cursor { Set = set };
            image.Source = set.Images[0];
            image.Unloaded += OnUnloaded;
            Start();
        }

        private static void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Image img)
            {
                img.Unloaded -= OnUnloaded;
                Playing.Remove(img);
                StopIfIdle();
            }
        }

        private static void Start()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(MinInterval)
                };
                _timer.Tick += OnTick;
            }

            if (_timer.IsEnabled) return;

            _last = DateTime.UtcNow;
            _timer.Interval = TimeSpan.FromMilliseconds(MinInterval);
            _timer.Start();
        }

        private static void StopIfIdle()
        {
            if (_timer == null || Playing.Count > 0) return;
            _timer.Stop();
        }

        private static void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            int dt = (int)Math.Round((now - _last).TotalMilliseconds);
            _last = now;

            if (dt < 0) dt = 0;
            if (dt > 250) dt = 250;     // 系统卡了一下，别一次往前跳好几帧

            if (Playing.Count == 0)
            {
                StopIfIdle();
                return;
            }

            int next = MaxInterval;

            // 换 Source 有可能触发 Unloaded（元素已经在树外），遍历用快照
            foreach (var pair in new List<KeyValuePair<Image, Cursor>>(Playing))
            {
                Image img = pair.Key;
                Cursor cur = pair.Value;

                // 折叠 / 被别的东西盖住时不推进时间：露出来时从停下的那一帧接着走
                if (!img.IsVisible)
                {
                    if (next > 50) next = 50;
                    continue;
                }

                cur.Elapsed += dt;

                int guard = 0;
                while (cur.Elapsed >= cur.Set.Delays[cur.Index] && guard++ < 8)
                {
                    cur.Elapsed -= cur.Set.Delays[cur.Index];
                    cur.Index = (cur.Index + 1) % cur.Set.Images.Length;
                    img.Source = cur.Set.Images[cur.Index];
                }

                int rest = cur.Set.Delays[cur.Index] - cur.Elapsed;
                if (rest < next) next = rest;
            }

            _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(next, MinInterval, MaxInterval));
        }

        // ================= 解码 =================

        private static FrameSet Load(string path)
        {
            if (Cache.TryGetValue(path, out FrameSet hit)) return hit;

            FrameSet set = null;

            // 只对 .gif 下手：别的格式没有多帧可言，省掉一次读盘
            if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                try
                {
                    using (var fs = File.OpenRead(path))
                    {
                        var dec = new GifBitmapDecoder(fs,
                            BitmapCreateOptions.PreservePixelFormat,
                            BitmapCacheOption.OnLoad);

                        if (dec.Frames.Count >= 2 && dec.Frames.Count <= MaxFrames)
                        {
                            var images = new BitmapSource[dec.Frames.Count];
                            var delays = new int[dec.Frames.Count];

                            bool ok = true;
                            for (int i = 0; i < dec.Frames.Count; i++)
                            {
                                BitmapFrame frame = dec.Frames[i];

                                if ((long)frame.PixelWidth * frame.PixelHeight > MaxFramePixels)
                                {
                                    ok = false;
                                    break;
                                }

                                if (frame.CanFreeze) frame.Freeze();
                                images[i] = frame;
                                delays[i] = DelayOf(frame);
                            }

                            if (ok) set = new FrameSet { Images = images, Delays = delays };
                        }
                    }
                }
                catch
                {
                    set = null;     // 坏文件 / 不认识的编码：退回静态图，别让磁贴整个消失
                }
            }

            Cache[path] = set;
            return set;
        }

        /// <summary>
        /// 每帧的停留时间。gif 里存的是 1/100 秒；0 和 1（不到 20ms）太快的
        /// 按规范当 100ms 处理，否则会变成肉眼看不见的鬼畜闪烁。
        /// </summary>
        private static int DelayOf(BitmapFrame frame)
        {
            try
            {
                if (frame.Metadata is BitmapMetadata meta)
                {
                    object v = meta.GetQuery("/grctlext/Delay");
                    int cs = v is ushort u ? u : (v is int i ? i : 0);
                    if (cs >= 2) return cs * 10;
                }
            }
            catch
            {
                // 没有这段扩展信息：走下面的默认值
            }

            return 100;
        }
    }
}
