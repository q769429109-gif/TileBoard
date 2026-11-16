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
    /// 只负责两件事：算出当前宽度能放几列、按格位摆放；碰撞与挤开交给 GroupGrid。
    /// </summary>
    public class GroupCanvas : Panel
    {
        /// <summary>当前可用列数（按 MeasureOverride 拿到的宽度算）。</summary>
        public int Columns { get; private set; } = TileMetrics.GridColumns;

        /// <summary>所有分组的底边最大像素值，供外部判断滚动范围。</summary>
        public double ContentBottom { get; private set; }

        private static TileGroup GroupOf(UIElement e) => (e as FrameworkElement)?.Tag as TileGroup;

        protected override Size MeasureOverride(Size availableSize)
        {
            Columns = GroupGrid.ColumnsForWidth(availableSize.Width);

            double width = double.IsNaN(availableSize.Width) || double.IsInfinity(availableSize.Width)
                ? 0
                : availableSize.Width;

            double bottom = 0;

            foreach (UIElement child in InternalChildren)
            {
                // 组块尺寸由内容决定（标题 + 组内网格），父容器不给约束
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                var g = GroupOf(child);
                if (g == null) continue;

                // 坐标若超出当前列数（面板被拖窄了），先夹回来，别让组跑到视野外
                int sc = Math.Clamp(GroupGrid.SpanColumns(g), 1, Columns);
                if (g.Column > Math.Max(0, Columns - sc)) g.Column = Math.Max(0, Columns - sc);
                if (g.Column < 0) g.Column = 0;
                if (g.Row < 0) g.Row = 0;

                bottom = Math.Max(bottom, g.Row * TileMetrics.Stride + child.DesiredSize.Height);
            }

            ContentBottom = bottom;
            return new Size(width, bottom + 4);
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
