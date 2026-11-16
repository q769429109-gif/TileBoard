using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TileBoard.Controls
{
    /// <summary>
    /// 排布过渡动画（Win10 磁贴的手感）。
    ///
    /// Win10 里磁贴被挤开、分组让位时都是「滑」到新位置的，不是瞬移。
    /// 实现思路：Arrange 照常把子元素摆到新矩形，同时给它挂一个 TranslateTransform，
    /// 从「新位置 - 旧位置」的反向偏移动画归零 —— 视觉上就是从旧位置滑过来。
    ///
    /// 每次移动都会生成新动画，起点取变换的当前值（上一段动画可能没播完），
    /// 所以连续拖动也不会跳帧。拖动中的本体（隐藏/全透明）不参与动画 —— 反正看不见。
    /// </summary>
    public static class ArrangeAnimator
    {
        /// <summary>单段移动动画时长。Win10 大约 150~200ms，太长会显得拖泥带水。</summary>
        private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(160);

        private static readonly ConditionalWeakTable<UIElement, Box> Table =
            new ConditionalWeakTable<UIElement, Box>();

        private sealed class Box
        {
            public Rect Last;                       // 上一次摆到的矩形
            public bool HasLast;
            public TranslateTransform Translate;    // 挂在子元素上的视觉偏移
        }

        /// <summary>
        /// 摆放一个子元素并自动做位置过渡。位置没变时零开销（不建变换、不建动画）。
        /// </summary>
        public static void Arrange(UIElement child, Rect rect)
        {
            bool animate = child is FrameworkElement fe && fe.Visibility == Visibility.Visible &&
                           fe.Opacity > 0.05;

            var box = Table.GetOrCreateValue(child);

            // 先量出新旧差，再真正 Arrange —— Arrange 会触发新一轮布局事件，顺序别搞反
            double dx = 0, dy = 0;
            if (animate && box.HasLast)
            {
                dx = rect.X - box.Last.X;
                dy = rect.Y - box.Last.Y;
                if (Math.Abs(dx) < 0.5) dx = 0;
                if (Math.Abs(dy) < 0.5) dy = 0;
            }

            child.Arrange(rect);
            box.Last = rect;
            box.HasLast = true;

            if (dx == 0 && dy == 0) return;

            var tr = box.Translate;
            if (tr == null)
            {
                tr = new TranslateTransform();
                box.Translate = tr;

                // 子元素可能已有自己的 RenderTransform：并入组里，别顶掉
                switch (child.RenderTransform)
                {
                    case null:
                        child.RenderTransform = tr;
                        break;
                    case TransformGroup group:
                        group.Children.Add(tr);
                        break;
                    case Transform existing:
                        child.RenderTransform = new TransformGroup
                        {
                            Children = { existing, tr }
                        };
                        break;
                }
            }

            // 当前视觉偏移（上一段动画可能进行到一半）：从它接续，而不是从 0 起跳
            double startX = tr.X - dx;
            double startY = tr.Y - dy;

            tr.BeginAnimation(TranslateTransform.XProperty, null);
            tr.BeginAnimation(TranslateTransform.YProperty, null);
            tr.X = startX;
            tr.Y = startY;

            if (Math.Abs(startX) < 0.5 && Math.Abs(startY) < 0.5) return;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var ax = new DoubleAnimation(0, Duration) { EasingFunction = ease };
            var ay = new DoubleAnimation(0, Duration) { EasingFunction = ease };
            tr.BeginAnimation(TranslateTransform.XProperty, ax);
            tr.BeginAnimation(TranslateTransform.YProperty, ay);
        }

        /// <summary>
        /// 子元素被搬出视觉树（重建/换组）后，旧位置记录就作废了 —— 下一次摆放不做过渡。
        /// </summary>
        public static void Forget(UIElement child)
        {
            if (child != null && Table.TryGetValue(child, out var box))
                box.HasLast = false;
        }
    }
}
