using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using TileBoard.Models;

namespace TileBoard.Services
{
    /// <summary>导入结果，用于向用户如实汇报。</summary>
    public class ImportResult
    {
        public BoardLayout Layout { get; set; }
        public int GroupCount { get; set; }
        public int TileCount { get; set; }
        public int ResolvedCount { get; set; }
        public List<string> Missing { get; } = new List<string>();
        public string VariantInfo { get; set; }
        public string Error { get; set; }
        public bool Ok => Error == null && Layout != null;
    }

    /// <summary>
    /// 读取 Windows 10 磁贴布局（FullDefaultLayout / StartLayout XML）。
    /// 支持两种来源：
    ///   1. 系统模板 %USERPROFILE%\AppData\Local\Microsoft\Windows\Shell\DefaultLayouts.xml
    ///   2. 用户当年导出的 StartLayout（&lt;StartLayout&gt; 根节点）
    /// 只做结构解析，不做像素级还原 —— 未知 AUMID 会保留成占位磁贴，不会丢。
    /// </summary>
    public static class Win10LayoutImporter
    {
        private static readonly XNamespace NsStart = "http://schemas.microsoft.com/Start/2014/StartLayout";
        private static readonly XNamespace NsFull = "http://schemas.microsoft.com/Start/2014/FullDefaultLayout";

        public static string DefaultTemplatePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         @"Microsoft\Windows\Shell\DefaultLayouts.xml");

        private static readonly string[] CandidatePaths =
        {
            DefaultTemplatePath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         @"Microsoft\Windows\Shell\LayoutModification.xml"),
        };

        /// <summary>自动探测磁盘上现存的布局文件。</summary>
        public static string DetectLayoutFile()
        {
            foreach (var p in CandidatePaths)
                if (File.Exists(p)) return p;
            return null;
        }

        public static ImportResult Import(string path, AppCatalog catalog)
        {
            var result = new ImportResult();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    result.Error = "找不到布局文件：" + (path ?? "(空)");
                    return result;
                }

                XDocument doc = XDocument.Load(path);
                var root = doc.Root;
                if (root == null) { result.Error = "布局文件为空。"; return result; }

                // 情形 A：用户导出的 <StartLayout> 直接就是根节点
                if (root.Name.LocalName == "StartLayout")
                {
                    var groups = ReadGroups(root, catalog, result);
                    result.VariantInfo = "用户导出的 StartLayout";
                    result.Layout = Build(groups);
                    Finish(result);
                    return result;
                }

                // 情形 B：系统模板，需要在多个变体里挑一个最贴近普通桌面版的
                var variants = root.Descendants(NsFull + "StartLayout").ToList();
                if (variants.Count == 0)
                    variants = root.Descendants().Where(e => e.Name.LocalName == "StartLayout").ToList();

                if (variants.Count == 0) { result.Error = "文件里没有任何 StartLayout 变体。"; return result; }

                XElement best = null;
                int bestScore = int.MinValue;
                string bestInfo = null;

                foreach (var v in variants)
                {
                    int score = Score(v);
                    var tiles = v.Descendants().Count(e => e.Name.LocalName == "Tile");
                    string info = $"变体: {AttrSummary(v)}（{tiles} 个磁贴）";
                    if (score > bestScore) { bestScore = score; best = v; bestInfo = info; }
                }

                var read = ReadGroups(best, catalog, result);
                result.VariantInfo = bestInfo;
                result.Layout = Build(read);
                Finish(result);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        private static void Finish(ImportResult r)
        {
            r.GroupCount = r.Layout.Groups.Count;
            r.TileCount = r.Layout.TileCount;
            r.Layout.ImportedFromWin10 = true;
            r.Layout.Source = "win10-defaultlayout";
            if (r.TileCount == 0) r.Error = "布局里没有解析出任何磁贴。";
        }

        /// <summary>变体打分：排除教育版/商业版/SKU 限定，剩下的按磁贴数取多的。</summary>
        private static int Score(XElement variant)
        {
            int score = 0;
            string edu = (string)variant.Attribute("EducationModeEnabled");
            string commercial = (string)variant.Attribute("CommercialDevice");
            string sku = (string)variant.Attribute("SKU");

            if (string.Equals(edu, "true", StringComparison.OrdinalIgnoreCase)) score -= 1000;
            if (string.Equals(commercial, "true", StringComparison.OrdinalIgnoreCase)) score -= 500;
            if (!string.IsNullOrEmpty(sku)) score -= 300;
            if (!string.IsNullOrEmpty((string)variant.Attribute("TestVariantName"))) score -= 50;

            string cellWidth = (string)variant.Attribute("GroupCellWidth");
            if (cellWidth == "6") score += 60;
            else if (cellWidth == "8") score += 10;

            score += variant.Descendants().Count(e => e.Name.LocalName == "Tile") * 2;
            return score;
        }

        private static string AttrSummary(XElement e)
        {
            var parts = e.Attributes().Select(a => $"{a.Name.LocalName}={a.Value}").ToList();
            return parts.Count == 0 ? "(无属性)" : string.Join(", ", parts);
        }

        private static List<TileGroup> ReadGroups(XElement container, AppCatalog catalog, ImportResult result)
        {
            var groups = new List<TileGroup>();
            var groupNodes = container.Descendants()
                .Where(e => e.Name.LocalName == "Group")
                .ToList();

            if (groupNodes.Count == 0)
            {
                // 没有 Group 包裹的裸 Tile，归到一个默认组
                var solo = container.Descendants().Where(e => e.Name.LocalName == "Tile").ToList();
                if (solo.Count > 0)
                {
                    groups.Add(new TileGroup
                    {
                        Name = "导入的磁贴",
                        Tiles = solo.Select(t => ReadTile(t, catalog, result)).Where(t => t != null).ToList()
                    });
                }
                return groups;
            }

            int index = 0;
            foreach (var g in groupNodes)
            {
                index++;
                var tiles = g.Descendants()
                    .Where(e => e.Name.LocalName == "Tile")
                    .Select(t => ReadTile(t, catalog, result))
                    .Where(t => t != null)
                    // 优先还原原始行列位置
                    .OrderBy(t => t.SourceRow)
                    .ThenBy(t => t.SourceColumn)
                    .ToList();

                if (tiles.Count == 0) continue;

                string name = (string)g.Attribute("Name")
                              ?? (string)g.Attribute("LocalizedNameResourceTag")
                              ?? $"分组 {index}";

                groups.Add(new TileGroup { Name = FriendlyGroupName(name, index), Tiles = tiles });
            }

            return groups;
        }

        private static TileItem ReadTile(XElement t, AppCatalog catalog, ImportResult result)
        {
            string aumid = (string)t.Attribute("AppUserModelID")
                           ?? (string)t.Attribute("AppUserModelId");
            if (string.IsNullOrWhiteSpace(aumid)) return null;

            var item = new TileItem
            {
                Aumid = aumid.Trim(),
                Size = ParseSize((string)t.Attribute("Size")),
                SourceColumn = ParseInt((string)t.Attribute("Column")),
                SourceRow = ParseInt((string)t.Attribute("Row")),
            };

            if (catalog != null && catalog.Contains(aumid))
            {
                item.Name = catalog.ResolveName(aumid);
                result.ResolvedCount++;
            }
            else
            {
                item.Name = catalog != null ? catalog.ResolveName(aumid) : aumid;
                result.Missing.Add(aumid);
            }

            return item;
        }

        private static TileSize ParseSize(string s)
        {
            return (s ?? "").Trim().ToLowerInvariant() switch
            {
                "1x1" => TileSize.Small,
                "2x2" => TileSize.Medium,
                "4x2" => TileSize.Wide,
                "4x4" => TileSize.Large,
                _ => TileSize.Medium
            };
        }

        private static int ParseInt(string s) =>
            int.TryParse(s, out int v) ? v : -1;

        private static string FriendlyGroupName(string raw, int index)
        {
            if (string.IsNullOrWhiteSpace(raw)) return $"分组 {index}";
            if (raw.StartsWith("TileGrid_", StringComparison.OrdinalIgnoreCase))
                return raw.Equals("TileGrid_DefaultGroup1", StringComparison.OrdinalIgnoreCase)
                    ? "常用"
                    : $"分组 {index}";
            return raw;
        }

        private static BoardLayout Build(List<TileGroup> groups)
        {
            var layout = new BoardLayout { ImportedFromWin10 = true };
            if (groups == null || groups.Count == 0)
            {
                groups = new List<TileGroup> { new TileGroup { Name = "常用" } };
            }
            layout.Groups = groups;
            return layout;
        }
    }
}
