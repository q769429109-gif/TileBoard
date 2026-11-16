using System;
using System.Collections.Generic;
using System.Linq;
using TileBoard.Models;

namespace TileBoard.Services
{
    /// <summary>
    /// Win10 磁贴的网格代数。
    ///
    /// Win10 的磁贴不是「按顺序流下去」的列表，而是钉在 6 列网格上的坐标方块：
    ///   · 每块磁贴记一个 (Column, Row)，小磁贴占 1x1，中 2x2，宽 4x2，大 4x4
    ///   · 往已有位置上放，冲突的磁贴只会被推到旁边或下面，不会整片重排
    ///   · 全空的列、空的行会自动塌陷，组宽随内容收缩
    /// 这个类就是这三条规则的实现，UI 层只负责把坐标画出来。
    /// </summary>
    public static class TileGrid
    {
        public const int Columns = TileMetrics.GridColumns;

        /// <summary>估算行数上限。磁贴再多也不会超过这个高度，避免分配过大数组。</summary>
        private static int RowCapacity(TileGroup group) =>
            Math.Max(16, (group?.Tiles?.Count ?? 0) * 4 + 8);

        /// <summary>
        /// 整理一组磁贴：补全缺失坐标、消解重叠，然后塌陷空行空列。
        /// 幂等 —— 已经合法的布局再跑一遍不会有任何变化。
        /// </summary>
        public static void Normalize(TileGroup group)
        {
            if (group?.Tiles == null || group.Tiles.Count == 0) return;

            int rows = RowCapacity(group);
            var cells = new bool[rows, Columns];

            // 按坐标排序，让左边的、上面的先落位，符合「已摆好的不动」的直觉
            var ordered = group.Tiles
                .Where(t => t != null)
                .OrderBy(t => t.Row < 0 ? int.MaxValue : t.Row)
                .ThenBy(t => t.Column < 0 ? int.MaxValue : t.Column)
                .ToList();

            foreach (var tile in ordered)
            {
                int sx = Math.Clamp(tile.SpanX, 1, Columns);
                int sy = Math.Max(1, tile.SpanY);

                // 导入的磁贴带原始行列，还没摆过的新磁贴直接用它
                if (tile.Column < 0 && tile.SourceColumn >= 0) tile.Column = tile.SourceColumn;
                if (tile.Row < 0 && tile.SourceRow >= 0) tile.Row = tile.SourceRow;

                int wantCol = tile.Column >= 0 ? Math.Clamp(tile.Column, 0, Columns - sx) : 0;
                int wantRow = tile.Row >= 0 ? Math.Max(0, tile.Row) : 0;

                int col, row;
                if (!TryFit(cells, rows, wantCol, wantRow, sx, sy, out col, out row) &&
                    !TryFit(cells, rows, 0, 0, sx, sy, out col, out row))
                {
                    // 网格已满（极端情况）：叠在最后一行，至少不会消失
                    col = 0;
                    row = Math.Max(0, rows - sy);
                }

                Fill(cells, rows, col, row, sx, sy);
                tile.Column = col;
                tile.Row = row;
            }

            CollapseEmpty(group);
        }

        /// <summary>
        /// 把一块磁贴挪到指定格位。与它重叠的磁贴只让开一次。
        ///
        /// 以前这里是把被挤开的磁贴「找最近空位」往里塞，而 UpdateDrag 每次鼠标移动都会
        /// 再跑一遍，于是同一块磁贴被一步步顶到组的尽头 —— 拖着拖着整组就散了。
        /// 现在让位只有两个选择：回到被拖磁贴的原位（交换），或者沿来向退一格，
        /// 两个都不成就原地不动。配合调用方「每次从拖动起点的快照重算」，
        /// 被让开的磁贴在拖动走开之后会自己归位，和 Win10 一样。
        /// </summary>
        public static void Place(TileGroup group, TileItem moving, int col, int row)
        {
            if (group?.Tiles == null || moving == null) return;

            int sx = Math.Clamp(moving.SpanX, 1, Columns);
            int sy = Math.Max(1, moving.SpanY);

            // 调用方已经把坐标恢复成拖动起点的样子，所以此刻的值就是「原位」
            int homeCol = moving.Column;
            int homeRow = moving.Row;

            col = Math.Clamp(col, 0, Math.Max(0, Columns - sx));
            row = Math.Max(0, row);

            var pushed = group.Tiles
                .Where(t => t != null && !ReferenceEquals(t, moving) &&
                            Overlaps(t, col, row, sx, sy))
                .ToList();

            moving.Column = col;
            moving.Row = row;

            // 让位方向 = 从新位置指回原位，也就是「它往哪边来，被挤的就往哪边退」
            int dirC = Math.Sign(homeCol - col);
            int dirR = Math.Sign(homeRow - row);

            foreach (var other in pushed)
            {
                if (TryStepAside(group, other, homeCol, homeRow, dirC, dirR)) continue;

                // 让不开就留在原地。这里绝不连锁推挤 —— 宁可暂时叠着，
                // 也不能再出现「一路推到底」的场面；松手时 Normalize 会收干净
            }

            CollapseEmpty(group);
        }

        /// <summary>
        /// 让一块磁贴退开一步。顺序是：回原位（交换）→ 沿来向退一格 → 往下 → 往右。
        /// 全都站不下就返回 false，调用方不要再逼它 —— 宁可暂时叠着也不能连锁。
        /// </summary>
        private static bool TryStepAside(TileGroup group, TileItem tile,
                                         int homeCol, int homeRow, int dirC, int dirR)
        {
            if (homeCol >= 0 && homeRow >= 0 && CanSit(group, tile, homeCol, homeRow))
            {
                tile.Column = homeCol;
                tile.Row = homeRow;
                return true;
            }

            var tries = new List<(int Col, int Row)>(3);

            // 沿让位方向退一格（同尺寸互换时最自然）
            if (dirC != 0 || dirR != 0)
                tries.Add((tile.Column + dirC, tile.Row + dirR));

            // 新磁贴从组外插进来时没有「原位」可用。
            // 这里退的是「自己的一整块」，不是一格 —— 2x2 的磁贴往下挪一格仍然压在
            // 新来的那块上，只有挪到它整个高度之下才算真的让开。
            tries.Add((tile.Column, tile.Row + Math.Max(1, tile.SpanY)));
            tries.Add((tile.Column + Math.Max(1, tile.SpanX), tile.Row));

            foreach (var (col, row) in tries)
            {
                if (!CanSit(group, tile, col, row)) continue;

                tile.Column = Math.Max(0, col);
                tile.Row = Math.Max(0, row);
                return true;
            }

            return false;
        }

        private static bool CanSit(TileGroup group, TileItem self, int col, int row)
        {
            int sx = Math.Clamp(self.SpanX, 1, Columns);
            int sy = Math.Max(1, self.SpanY);

            if (col < 0 || row < 0 || col + sx > Columns) return false;

            foreach (var t in group.Tiles)
            {
                if (t == null || ReferenceEquals(t, self)) continue;
                if (Overlaps(t, col, row, sx, sy)) return false;
            }

            return true;
        }

        private static bool Overlaps(TileItem t, int col, int row, int sx, int sy) =>
            col < t.Column + t.SpanX && t.Column < col + sx &&
            row < t.Row + t.SpanY && t.Row < row + sy;

        /// <summary>一组的实际列数（Win10 的组宽会随内容收缩，不是永远 6 格）。</summary>
        public static int UsedColumns(TileGroup group)
        {
            if (group?.Tiles == null || group.Tiles.Count == 0) return 1;
            int max = 1;
            foreach (var t in group.Tiles)
                if (t != null) max = Math.Max(max, t.Column + t.SpanX);
            return Math.Clamp(max, 1, Columns);
        }

        /// <summary>一组的实际行数。</summary>
        public static int UsedRows(TileGroup group)
        {
            if (group?.Tiles == null || group.Tiles.Count == 0) return 1;
            int max = 1;
            foreach (var t in group.Tiles)
                if (t != null) max = Math.Max(max, t.Row + t.SpanY);
            return max;
        }

        /// <summary>格位是否在组内已占用区域之外 —— 用于判断「是不是拖到了空白处」。</summary>
        public static bool IsFree(TileGroup group, int col, int row, int spanX, int spanY, TileItem ignore = null)
        {
            if (group?.Tiles == null) return true;
            foreach (var t in group.Tiles)
            {
                if (t == null || ReferenceEquals(t, ignore)) continue;
                bool overlapX = col < t.Column + t.SpanX && t.Column < col + spanX;
                bool overlapY = row < t.Row + t.SpanY && t.Row < row + spanY;
                if (overlapX && overlapY) return false;
            }
            return true;
        }

        // ---------- 内部 ----------

        /// <summary>从 (wantCol, wantRow) 出发找最近的空位；同一行优先往右，再换下一行。</summary>
        private static bool TryFit(bool[,] cells, int rows, int wantCol, int wantRow,
                                   int sx, int sy, out int col, out int row)
        {
            for (int r = Math.Max(0, wantRow); r + sy <= rows; r++)
            {
                int start = (r == wantRow) ? Math.Max(0, wantCol) : 0;
                for (int c = start; c + sx <= Columns; c++)
                {
                    if (IsFree(cells, rows, c, r, sx, sy))
                    {
                        col = c;
                        row = r;
                        return true;
                    }
                }
            }

            col = row = -1;
            return false;
        }

        private static bool IsFree(bool[,] cells, int rows, int col, int row, int sx, int sy)
        {
            if (col < 0 || row < 0 || col + sx > Columns || row + sy > rows) return false;
            for (int r = row; r < row + sy; r++)
                for (int c = col; c < col + sx; c++)
                    if (cells[r, c]) return false;
            return true;
        }

        private static void Fill(bool[,] cells, int rows, int col, int row, int sx, int sy)
        {
            for (int r = row; r < Math.Min(rows, row + sy); r++)
                for (int c = col; c < Math.Min(Columns, col + sx); c++)
                    if (r >= 0 && c >= 0) cells[r, c] = true;
        }

        /// <summary>塌陷全空的列与行：整列没东西就把右边的左移一格，整行没东西就把下面的上移一行。</summary>
        private static void CollapseEmpty(TileGroup group)
        {
            if (group?.Tiles == null) return;

            int guard = 0;
            while (guard++ < 64)
            {
                int maxCol = group.Tiles.Max(t => t.Column + t.SpanX) - 1;
                bool moved = false;

                for (int c = 0; c < maxCol && !moved; c++)
                {
                    if (ColumnEmpty(group, c))
                    {
                        foreach (var t in group.Tiles)
                            if (t.Column > c) t.Column--;
                        moved = true;
                    }
                }
                if (moved) continue;

                int maxRow = group.Tiles.Max(t => t.Row + t.SpanY) - 1;
                for (int r = 0; r < maxRow && !moved; r++)
                {
                    if (RowEmpty(group, r))
                    {
                        foreach (var t in group.Tiles)
                            if (t.Row > r) t.Row--;
                        moved = true;
                    }
                }

                if (!moved) return;
            }
        }

        private static bool ColumnEmpty(TileGroup group, int col) =>
            group.Tiles.All(t => !(t.Column <= col && col < t.Column + t.SpanX));

        private static bool RowEmpty(TileGroup group, int row) =>
            group.Tiles.All(t => !(t.Row <= row && row < t.Row + t.SpanY));
    }
}
