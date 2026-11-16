using System;
using System.Collections.Generic;
using System.Linq;

namespace TileBoard.Models
{
    /// <summary>磁贴尺寸，命名与 Win10 导出的 Size 属性对齐。</summary>
    public enum TileSize
    {
        Small = 0,   // 1x1   71 x 71
        Medium = 1,  // 2x2  150 x 150
        Wide = 2,    // 4x2  308 x 150
        Large = 3    // 4x4  308 x 308
    }

    /// <summary>Win10 磁贴网格的几何常量。71 + 8 的节奏是 Win10 原始比例：2*71+8 = 150。</summary>
    public static class TileMetrics
    {
        public const double Cell = 71;      // 单格边长
        public const double Gap = 8;       // 格间距
        public const double Stride = 79;      // Cell + Gap
        public const int GridColumns = 6;       // 每组默认 6 格宽（对应 GroupCellWidth=6）

        public static int SpanX(TileSize s) => s switch
        {
            TileSize.Small => 1,
            TileSize.Medium => 2,
            TileSize.Wide => 4,
            TileSize.Large => 4,
            _ => 2
        };

        public static int SpanY(TileSize s) => s switch
        {
            TileSize.Small => 1,
            TileSize.Medium => 2,
            TileSize.Wide => 2,
            TileSize.Large => 4,
            _ => 2
        };

        /// <summary>给定跨度，算出像素宽高（含内部格间空隙）。</summary>
        public static double Pixels(int span) => span * Cell + (span - 1) * Gap;

        public static string Label(TileSize s) => s switch
        {
            TileSize.Small => "小",
            TileSize.Medium => "中",
            TileSize.Wide => "宽",
            TileSize.Large => "大",
            _ => "中"
        };
    }

    /// <summary>一个磁贴。</summary>
    public class TileItem
    {
        public string Aumid { get; set; }
        public string Name { get; set; }
        public TileSize Size { get; set; } = TileSize.Medium;

        /// <summary>
        /// 组内网格坐标（左列、上行，左上角）。-1 表示「还没排过」，
        /// 由 TileGrid.Normalize 补成第一个能放下的空位。
        /// Win10 的磁贴是挂在网格上的，不是按顺序流下去的 —— 这两个字段就是那套坐标。
        /// </summary>
        public int Column { get; set; } = -1;
        public int Row { get; set; } = -1;

        /// <summary>来自默认布局的原始行列，仅作导入时排序参考。</summary>
        public int SourceColumn { get; set; } = -1;
        public int SourceRow { get; set; } = -1;

        // 这两个是 Size 的推导值，不该进 JSON
        [System.Text.Json.Serialization.JsonIgnore]
        public int SpanX => TileMetrics.SpanX(Size);

        [System.Text.Json.Serialization.JsonIgnore]
        public int SpanY => TileMetrics.SpanY(Size);    }

    /// <summary>一个磁贴分组。</summary>
    public class TileGroup
    {
        /// <summary>新分组的默认名。名字还是它的时候算「没命名过」，标题平时不显示。</summary>
        public const string DefaultName = "命名组";

        public string Name { get; set; } = DefaultName;
        public List<TileItem> Tiles { get; set; } = new List<TileItem>();

        /// <summary>
        /// 折叠起来的组只留一行标题，磁贴全部收起。
        /// 高度由 GroupGrid.PixelHeight 换算，不靠 UI 层的 Visibility 决定板面占位。
        /// </summary>
        public bool Collapsed { get; set; }

        /// <summary>
        /// 名字还是默认值 —— 说明主人没给它起过名。
        /// 历史上用过三个默认名：「命名组」（现行）、「新建分组」、「分组 N」，
        /// 旧布局升上来不该突然满屏都是占位标题，一并算没命名。
        /// </summary>
        public bool IsUnnamed
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Name) || Name == DefaultName)
                    return true;

                if (Name == "新建分组" || Name.StartsWith("新建分组 ", StringComparison.Ordinal))
                    return true;

                // 「分组」「分组 2」「分组 10」—— 更早版本的自动编号默认名
                if (Name == "分组" || (Name.Length > 3 &&
                                        Name.StartsWith("分组", StringComparison.Ordinal) &&
                                        int.TryParse(Name.Substring(3).Trim(), out _)))
                    return true;

                return false;
            }
        }

        /// <summary>
        /// 分组自己在整块板面上的格位（全局，不是组内）。
        /// 一级网格是磁贴的 (Column, Row)，这是第二级 —— 有了它，
        /// 分组才能被拖到板面任意位置，而不是只能按顺序流下去。
        /// -1 表示「还没摆过」，由 GroupGrid.Normalize 装箱补位。
        /// </summary>
        public int Column { get; set; } = -1;
        public int Row { get; set; } = -1;

        /// <summary>
        /// 这个位置是人手动拖出来的。
        /// 面板宽度变化时，人摆过的组原地夹回范围就好，没动过的组允许重新紧凑排布 ——
        /// 否则首帧按窄版装箱、之后拉宽，整板会留下一堆莫名单行空洞。
        /// </summary>
        public bool UserPlaced { get; set; }
    }

    /// <summary>整块磁贴板的持久化模型。</summary>
    public class BoardLayout
    {
        public int Version { get; set; } = 1;
        public string Source { get; set; } = "manual";
        public bool ImportedFromWin10 { get; set; }
        public List<TileGroup> Groups { get; set; } = new List<TileGroup>();

        public int TileCount => Groups.Sum(g => g.Tiles.Count);

        /// <summary>全板去重后的应用清单，给“添加应用”面板用。</summary>
        public IEnumerable<string> KnownAumids() =>
            Groups.SelectMany(g => g.Tiles).Select(t => t.Aumid)
                  .Where(a => !string.IsNullOrWhiteSpace(a))
                  .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
