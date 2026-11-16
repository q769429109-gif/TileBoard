using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TileBoard.Models;

namespace TileBoard.Controls
{
    /// <summary>
    /// 磁贴网格面板。
    ///
    /// 旧实现是「行优先装箱」：谁排前面谁先占位，磁贴没法停在指定位置。
    /// 现在每块磁贴自带 (Column, Row) 格位，这个面板只做两件事：
    /// 按坐标算出像素矩形、按内容返回真实尺寸（组宽随内容收缩，和 Win10 一致）。
    /// </summary>
    public class TileGroupPanel : Panel
    {
        public static readonly DependencyProperty SpanXProperty =
            DependencyProperty.RegisterAttached("SpanX", typeof(int), typeof(TileGroupPanel),
                new FrameworkPropertyMetadata(2,
                    FrameworkPropertyMetadataOptions.AffectsParentMeasure |
                    FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static readonly DependencyProperty SpanYProperty =
            DependencyProperty.RegisterAttached("SpanY", typeof(int), typeof(TileGroupPanel),
                new FrameworkPropertyMetadata(2,
                    FrameworkPropertyMetadataOptions.AffectsParentMeasure |
                    FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static readonly DependencyProperty ColumnProperty =
            DependencyProperty.RegisterAttached("Column", typeof(int), typeof(TileGroupPanel),
                new FrameworkPropertyMetadata(0,
                    FrameworkPropertyMetadataOptions.AffectsParentMeasure |
                    FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static readonly DependencyProperty RowProperty =
            DependencyProperty.RegisterAttached("Row", typeof(int), typeof(TileGroupPanel),
                new FrameworkPropertyMetadata(0,
                    FrameworkPropertyMetadataOptions.AffectsParentMeasure |
                    FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static void SetSpanX(UIElement element, int value) => element.SetValue(SpanXProperty, value);
        public static int GetSpanX(UIElement element) => (int)element.GetValue(SpanXProperty);

        public static void SetSpanY(UIElement element, int value) => element.SetValue(SpanYProperty, value);
        public static int GetSpanY(UIElement element) => (int)element.GetValue(SpanYProperty);

        public static void SetColumn(UIElement element, int value) => element.SetValue(ColumnProperty, value);
        public static int GetColumn(UIElement element) => (int)element.GetValue(ColumnProperty);

        public static void SetRow(UIElement element, int value) => element.SetValue(RowProperty, value);
        public static int GetRow(UIElement element) => (int)element.GetValue(RowProperty);

        /// <summary>实际占用的列数与行数，供外部算落点用。</summary>
        public int UsedColumns { get; private set; } = TileMetrics.GridColumns;
        public int UsedRows { get; private set; } = 1;

        protected override Size MeasureOverride(Size availableSize)
        {
            int cols = 1, rows = 1;

            foreach (UIElement child in InternalChildren)
            {
                int sx = Math.Clamp(GetSpanX(child), 1, TileMetrics.GridColumns);
                int sy = Math.Max(1, GetSpanY(child));

                child.Measure(new Size(TileMetrics.Pixels(sx), TileMetrics.Pixels(sy)));

                cols = Math.Max(cols, GetColumn(child) + sx);
                rows = Math.Max(rows, GetRow(child) + sy);
            }

            UsedColumns = Math.Clamp(cols, 1, TileMetrics.GridColumns);
            UsedRows = rows;

            // 高度 = 行数 * 步距 - 最后一格的下边距，和 Win10 的行距完全一致
            double height = rows * TileMetrics.Stride - TileMetrics.Gap;
            return new Size(TileMetrics.Pixels(UsedColumns), Math.Max(0, height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (UIElement child in InternalChildren)
            {
                int sx = Math.Clamp(GetSpanX(child), 1, TileMetrics.GridColumns);
                int sy = Math.Max(1, GetSpanY(child));

                double x = GetColumn(child) * TileMetrics.Stride;
                double y = GetRow(child) * TileMetrics.Stride;

                // 位置变了就滑过去（Win10 磁贴的手感），位置没变时零开销
                ArrangeAnimator.Arrange(child,
                    new Rect(x, y, TileMetrics.Pixels(sx), TileMetrics.Pixels(sy)));
            }
            return finalSize;
        }

        /// <summary>坐标变了之后让面板重排（不重建视觉树，拖拽时才有实时感）。</summary>
        public void Reflow()
        {
            InvalidateMeasure();
            InvalidateArrange();
        }
    }
}
