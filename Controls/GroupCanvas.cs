using System;
using System.Windows;
using System.Windows.Controls;
using TileBoard.Models;
using TileBoard.Services;

namespace TileBoard.Controls
{
    /// <summary>
    /// 分组画布。
    ///
    /// 原来的容器是 WrapPanel：分组按顺序一个接一个流下去，位置由「排在第几个」
    /// 决定，所以怎么拖都拖不动。这里换成按坐标摆放 —— 每个分组块都带
    /// TileGroup，用自己的 (Column, Row) 定位，和磁贴用的是同一套网格。
    ///
    /// 多屏改版后板面支持横向滚动：内容可以比面板视口宽，超出的部分
    /// 用横向位置条滑过去看。所以这里对 (Column, Row) 只做一件事 ——
    /// 原样摆放。绝不能把超出视口的组夹回来：一夹就是一次整板重排，
    /// 用户在宽屏上摆好的布局到窄屏就全毁了（2026-11-16 布局乱两次翻车的根源之一）。
    /// </summary>
    public class GroupCanvas : Panel
    {
        /// <summary>所有分组的底边最大像素值，供外部判断滚动范围。</summary>
        public double ContentBottom { get; private set; }

        /// <summary>所有分组的右边缘最大像素值 —— 板面内容的总宽度。</summary>
        public double ContentRight { get; private set; }

        private static TileGroup GroupOf(UIElement e) => (e as FrameworkElement)?.Tag as TileGroup;

        protected override Size MeasureOverride(Size availableSize)
        {
            double bottom = 0;
            double right = 0;

            foreach (UIElement child in InternalChildren)
            {
                // 组块尺寸由内容决定（标题 + 组内网格），父容器不给约束
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                var g = GroupOf(child);
                if (g == null) continue;

                if (g.Column < 0) g.Column = 0;
                if (g.Row < 0) g.Row = 0;

                bottom = Math.Max(bottom, g.Row * TileMetrics.Stride + child.DesiredSize.Height);
                right = Math.Max(right, g.Column * TileMetrics.Stride + child.DesiredSize.Width);
            }

            ContentBottom = bottom;
            ContentRight = right;

            // 宽度回报内容实际撑开的范围（可能比视口宽 → ScrollViewer 出横向位置条）；
            // 高度同旧逻辑：底边 + 一点余量。
            return new Size(right, bottom + 4);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (UIElement child in InternalChildren)
            {
                var g = GroupOf(child);
                if (g == null) continue;

                double x = Math.Max(0, g.Column) * TileMetrics.Stride;
                double y = Math.Max(0, g.Row) * TileMetrics.Stride;

                // 分组让位也走过渡：被挤开的组滑一步，不是瞬移
                ArrangeAnimator.Arrange(child,
                    new Rect(x, y, child.DesiredSize.Width, child.DesiredSize.Height));
            }

            return finalSize;
        }
    }
}
