using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using TileBoard.Models;

namespace TileBoard.Services
{
    /// <summary>
    /// 分组级的网格代数。
    ///
    /// 磁贴有一层自己的 (Column, Row)（见 TileGrid），分组还有一层：
    /// 整块板面被看成一块「列 × 行」的大网格，每个分组占据其中若干格。
    /// 单位与磁贴相同 —— 一格 = 71 + 8，所以分组和磁贴用的是同一套节奏。
    ///
    /// 有了这层坐标，分组才能被拖到板面任意格位；之前分组只是跟着 WrapPanel
    /// 的顺序流下去，模型里连个位置字段都没有，自然拖不动。
    ///
    /// 与 TileGrid 的一个区别：这里不做空行空列塌陷。自由摆放本来就会留空，
    /// 塌陷等于把用户刚拖出来的空位又收回去。
    /// </summary>
    public static class GroupGrid
    {
        /// <summary>组标题占的纵向高度：标题 26 + 与磁贴的间距 8。</summary>
        public const double HeaderHeight = 34;

        /// <summary>面板宽度能放下几列。窗口宽度变了就靠它重算。</summary>
        public static int ColumnsForWidth(double width)
        {
            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
                return TileMetrics.GridColumns;

            return Math.Max(1, (int)Math.Floor((width + TileMetrics.Gap) / TileMetrics.Stride));
        }

        /// <summary>分组占几列 —— 就是组内磁贴用到的列数，组宽随内容收缩。</summary>
        public static int SpanColumns(TileGroup g) => g == null ? 1 : TileGrid.UsedColumns(g);

        /// <summary>分组占几行。</summary>
        public static int SpanRows(TileGroup g) => g == null ? 1 : TileGrid.UsedRows(g);

        /// <summary>分组块的像素尺寸。</summary>
        public static double PixelWidth(TileGroup g) => TileMetrics.Pixels(SpanColumns(g));

        /// <summary>
        /// 分组块的高度。折叠后只剩标题那一条 —— 磁贴收起来了，板面上就不该再占它们的位置，
        /// 否则下面那块空白会一直挡着别的分组放不进来。
        /// </summary>
        public static double PixelHeight(TileGroup g)
        {
            if (g != null && g.Collapsed) return HeaderHeight;

            return SpanRows(g) * TileMetrics.Stride - TileMetrics.Gap + HeaderHeight;
        }

        /// <summary>分组在板面上占的矩形（左上角由自己的格位换算）。</summary>
        public static Rect Box(TileGroup g) => BoxAt(g, g.Column, g.Row);

        private static Rect BoxAt(TileGroup g, int col, int row) =>
            new Rect(col * TileMetrics.Stride, row * TileMetrics.Stride, PixelWidth(g), PixelHeight(g));

        /// <summary>
        /// 整理整块板面：把没有坐标的分组装箱，把越界的分组夹回可视范围，
        /// 把互相压住的分组挤开。幂等。
        /// </summary>
        public static void Normalize(BoardLayout layout, int columns)
        {
            if (layout?.Groups == null || layout.Groups.Count == 0) return;
            columns = Math.Max(1, columns);

            var placed = new List<TileGroup>();

            // 已有坐标的先占位：上、左优先，符合「已经摆好的不动」的直觉
            var anchored = layout.Groups
                .Where(g => g != null && g.Column >= 0 && g.Row >= 0)
                .OrderBy(g => g.Row)
                .ThenBy(g => g.Column)
                .ToList();

            foreach (var g in anchored)
            {
                int sc = Math.Clamp(SpanColumns(g), 1, columns);
                int wantCol = Math.Clamp(g.Column, 0, Math.Max(0, columns - sc));
                int wantRow = Math.Max(0, g.Row);

                int col, row;
                if (!TryFind(placed, g, wantCol, wantRow, columns, out col, out row))
                {
                    col = wantCol;
                    row = wantRow;
                }

                g.Column = col;
                g.Row = row;
                placed.Add(g);
            }

            // 没坐标的（旧布局、新建分组）按列表顺序装到第一个空位
            foreach (var g in layout.Groups.Where(g => g != null && (g.Column < 0 || g.Row < 0)))
            {
                int col, row;
                if (!TryFind(placed, g, 0, 0, columns, out col, out row))
                {
                    col = 0;
                    row = 0;
                }

                g.Column = col;
                g.Row = row;
                placed.Add(g);
            }
        }

        /// <summary>
        /// 把某个分组挪到指定格位。与它压住的分组只让开一次。
        ///
        /// 和磁贴的 Place 同一个教训：以前这里是「找最近空位」往里塞，
        /// UpdateGroupDrag 每次鼠标移动都再跑一遍，同一组就被一步步顶到板面尽头。
        /// 现在让位只有两个选择：回到被拖分组的原位（交换），或者沿来向退一步
        /// （步长按自己的跨度），两个都不成就原地不动。配合调用方
        /// 「每次从拖动起点的快照重算」，被让开的分组在拖动走开后自己归位。
        /// </summary>
        public static void Place(BoardLayout layout, TileGroup moving, int columns, int col, int row)
        {
            if (layout?.Groups == null || moving == null) return;
            columns = Math.Max(1, columns);

            // 调用方已经把板面恢复成拖动起点的样子，此刻的值就是「原位」
            int homeCol = moving.Column;
            int homeRow = moving.Row;

            int sc = Math.Clamp(SpanColumns(moving), 1, columns);
            col = Math.Clamp(col, 0, Math.Max(0, columns - sc));
            row = Math.Max(0, row);

            var pushed = layout.Groups
                .Where(g => g != null && !ReferenceEquals(g, moving) &&
                            Box(g).IntersectsWith(BoxAt(moving, col, row)))
                .ToList();

            moving.Column = col;
            moving.Row = row;

            // 让位方向 = 从新位指回原位：它从哪边来，被压的就往哪边退
            int dirC = Math.Sign(homeCol - col);
            int dirR = Math.Sign(homeRow - row);

            foreach (var other in pushed)
            {
                if (TryStepAside(layout, moving, other, homeCol, homeRow, dirC, dirR, columns))
                    continue;

                // 让不开就留在原地。绝不连锁推挤 —— 宁可暂时压着，
                // 也不能「一路推到底」；松手时 Normalize 会收干净
            }
        }

        /// <summary>
        /// 让一个分组退开一步。顺序：回原位（交换）→ 沿来向退一格 → 往下 → 往右。
        /// 步长按自己的跨度 —— 组比磁贴更宽更高，挪一格往往还压在新来的那块上。
        /// </summary>
        private static bool TryStepAside(BoardLayout layout, TileGroup moving, TileGroup g,
                                         int homeCol, int homeRow, int dirC, int dirR, int columns)
        {
            if (homeCol >= 0 && homeRow >= 0 && IsFreeAt(layout, moving, g, homeCol, homeRow))
            {
                g.Column = homeCol;
                g.Row = homeRow;
                return true;
            }

            var tries = new List<(int Col, int Row)>(3);

            if (dirC != 0 || dirR != 0)
                tries.Add((g.Column + dirC, g.Row + dirR));

            tries.Add((g.Column, g.Row + Math.Max(1, SpanRows(g))));
            tries.Add((g.Column + Math.Max(1, SpanColumns(g)), g.Row));

            foreach (var (c, r) in tries)
            {
                int sc = Math.Clamp(SpanColumns(g), 1, columns);
                if (c < 0 || r < 0 || c + sc > columns) continue;
                if (!IsFreeAt(layout, moving, g, c, r)) continue;

                g.Column = c;
                g.Row = r;
                return true;
            }

            return false;
        }

        /// <summary>这格放不放得下：和板面上其余分组（排除自己和正在拖的那块）都不相交。</summary>
        private static bool IsFreeAt(BoardLayout layout, TileGroup moving, TileGroup self,
                                     int col, int row)
        {
            Rect box = BoxAt(self, col, row);

            foreach (var p in layout.Groups)
            {
                if (p == null || ReferenceEquals(p, self) || ReferenceEquals(p, moving))
                    continue;
                if (box.IntersectsWith(Box(p))) return false;
            }

            return true;
        }

        /// <summary>窗口坐标（相对板面原点）落在哪一格 —— 拖动时的吸附目标。</summary>
        public static void SnapFromPoint(TileGroup moving, double x, double y, int columns,
                                         out int col, out int row)
        {
            int sc = Math.Clamp(SpanColumns(moving), 1, columns);

            col = (int)Math.Round(x / TileMetrics.Stride);
            row = (int)Math.Round(y / TileMetrics.Stride);

            col = Math.Clamp(col, 0, Math.Max(0, columns - sc));
            row = Math.Max(0, row);
        }

        // ---------- 内部 ----------

        /// <summary>从目标位开始找位置：先试原位，再按行优先扫最近的空位。</summary>
        private static bool TryFind(List<TileGroup> placed, TileGroup self,
                                    int wantCol, int wantRow, int columns,
                                    out int col, out int row)
        {
            int sc = Math.Clamp(SpanColumns(self), 1, columns);
            int startCol = Math.Clamp(wantCol, 0, Math.Max(0, columns - sc));
            int startRow = Math.Max(0, wantRow);

            if (IsFreeAt(placed, self, startCol, startRow))
            {
                col = startCol;
                row = startRow;
                return true;
            }

            for (int r = startRow; r < startRow + 512; r++)
            {
                int c0 = r == startRow ? startCol : 0;
                for (int c = c0; c + sc <= columns; c++)
                {
                    if (IsFreeAt(placed, self, c, r))
                    {
                        col = c;
                        row = r;
                        return true;
                    }
                }
            }

            col = startCol;
            row = startRow;
            return false;
        }

        private static bool IsFreeAt(List<TileGroup> placed, TileGroup self, int col, int row)
        {
            Rect box = BoxAt(self, col, row);

            foreach (var p in placed)
            {
                if (ReferenceEquals(p, self)) continue;
                if (box.IntersectsWith(Box(p))) return false;
            }

            return true;
        }
    }
}
