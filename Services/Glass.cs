using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TileBoard.Native;

namespace TileBoard.Services
{
    /// <summary>
    /// 自己实现的毛玻璃：拿一张「唤出瞬间的桌面」当底，按强度缩小再放大，得到可控的模糊程度。
    ///
    /// DWM 的亚克力模糊半径是系统写死的，滑块的百分比只能改染色透明度 ——
    /// 实际观感就只有「开」和「关」两档。所以这里不用 DWM，自己模糊：
    /// 强度 s 映射到缩小倍数 div（1 倍＝原样，12 倍＝重糊），
    /// 缩小后做一遍盒式模糊去掉插值的方块感，再交给 Image 用 HighQuality 拉回原尺寸。
    /// </summary>
    internal static class Glass
    {
        /// <summary>唤出瞬间抓到的原始桌面。整个显示期间复用，拖滑块时重新模糊它就行。</summary>
        private static BitmapSource _raw;

        /// <summary>按缩小倍数缓存结果。强度落到 14 个档位，整段显示期间最多算 14 张。</summary>
        private static readonly System.Collections.Generic.Dictionary<int, BitmapSource> Cache
            = new System.Collections.Generic.Dictionary<int, BitmapSource>();

        internal static bool HasImage => _raw != null;

        /// <summary>清掉旧底图（宽度变了、换了显示器，重抓之前先作废）。</summary>
        internal static void Clear()
        {
            _raw = null;
            Cache.Clear();
        }

        /// <summary>
        /// 窗口隐藏时调用，抓一张背后的桌面（整块工作区）。
        /// 抓不到就保持 null，调用方退回 DWM 亚克力。
        /// </summary>
        internal static bool Capture(IntPtr hwnd) => Capture(hwnd, Rect.Empty);

        /// <summary>
        /// 带目标屏物理矩形的多屏版本：面板要出在副屏时，hwnd 可能还挂在主屏上，
        /// 按窗口所在屏抓就会抓错 —— 所以调用方直接把目标屏的工作区物理矩形递进来。
        /// Empty 则退回按 hwnd 所在屏抓。
        /// </summary>
        internal static bool Capture(IntPtr hwnd, Rect physical)
        {
            _raw = physical.IsEmpty
                ? ScreenCapture.GrabWorkArea(hwnd)
                : ScreenCapture.Grab((int)physical.X, (int)physical.Y,
                                     (int)physical.Width, (int)physical.Height);
            Cache.Clear();
            return _raw != null;
        }

        internal static BitmapSource Current => _raw;

        /// <summary>强度对应的缩小倍数。2 倍起（再低肉眼分不出来），最高 14 倍。</summary>
        internal static int DivFor(double strength)
        {
            double s = Math.Clamp(strength, 0, 1);
            return Math.Clamp(2 + (int)Math.Round(s * 12), 2, 14);
        }

        /// <summary>按强度生成模糊后的底图。强度 ≤ 0 直接返回原图（等于不模糊）。</summary>
        internal static BitmapSource Render(double strength)
        {
            if (_raw == null) return null;

            if (Math.Clamp(strength, 0, 1) <= 0.02) return _raw;

            int div = DivFor(strength);
            if (Cache.TryGetValue(div, out BitmapSource cached)) return cached;

            var bmp = RenderFrom(_raw, strength);
            Cache[div] = bmp;
            return bmp;
        }

        /// <summary>同上，但从任意底图生成 —— 自检拿合成图验证算法时用。</summary>
        internal static BitmapSource RenderFrom(BitmapSource source, double strength)
        {
            if (source == null) return null;

            double s = Math.Clamp(strength, 0, 1);
            if (s <= 0.02) return source;

            // 缩小倍数才是「强度」本身。
            // 早先把盒式模糊半径写死成 2，结果 10% 就已经糊死，右半段形同虚设 ——
            // 现在半径固定 1，只负责消掉插值的方块感，强弱全交给缩小倍数。
            int div = DivFor(s);

            try
            {
                int w = Math.Max(1, source.PixelWidth / div);
                int h = Math.Max(1, source.PixelHeight / div);

                var small = new TransformedBitmap(source,
                    new ScaleTransform((double)w / source.PixelWidth, (double)h / source.PixelHeight));
                if (small.CanFreeze) small.Freeze();

                return Blur(small, 1);
            }
            catch
            {
                return source;
            }
        }

        /// <summary>小图上的可分离盒式模糊。尺寸只有原图 1/div，几十毫秒都不到。</summary>
        private static BitmapSource Blur(BitmapSource src, int radius)
        {
            int w = src.PixelWidth;
            int h = src.PixelHeight;
            int stride = w * 4;

            var pixels = new byte[stride * h];
            src.CopyPixels(pixels, stride, 0);

            var tmp = new byte[pixels.Length];

            // 横向
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int b = 0, g = 0, r = 0, a = 0, n = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int xx = x + k;
                        if (xx < 0 || xx >= w) continue;
                        int i = row + xx * 4;
                        b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; a += pixels[i + 3];
                        n++;
                    }
                    int o = row + x * 4;
                    tmp[o] = (byte)(b / n); tmp[o + 1] = (byte)(g / n);
                    tmp[o + 2] = (byte)(r / n); tmp[o + 3] = (byte)(a / n);
                }
            }

            // 纵向
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    int b = 0, g = 0, r = 0, a = 0, n = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int yy = y + k;
                        if (yy < 0 || yy >= h) continue;
                        int i = yy * stride + x * 4;
                        b += tmp[i]; g += tmp[i + 1]; r += tmp[i + 2]; a += tmp[i + 3];
                        n++;
                    }
                    int o = y * stride + x * 4;
                    pixels[o] = (byte)(b / n); pixels[o + 1] = (byte)(g / n);
                    pixels[o + 2] = (byte)(r / n); pixels[o + 3] = (byte)(a / n);
                }
            }

            var outBmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            outBmp.Freeze();
            return outBmp;
        }
    }
}
