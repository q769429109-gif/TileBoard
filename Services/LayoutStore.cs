using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using TileBoard.Models;

namespace TileBoard.Services
{
    /// <summary>布局的磁盘持久化。写在 %APPDATA%\TileBoard\layout.json，不碰 C 盘系统目录以外的东西。</summary>
    public static class LayoutStore
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TileBoard");

        public static string FilePath => Path.Combine(Directory, "layout.json");

        public static BoardLayout Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                string json = File.ReadAllText(FilePath);
                var layout = JsonSerializer.Deserialize<BoardLayout>(json, Options);
                if (layout?.Groups == null || layout.Groups.Count == 0) return null;

                HealFolders(layout);
                return layout;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Win10 的文件夹至少要装两块磁贴。早期版本允许「只剩一块还留着」，
        /// 这种文件夹读盘时就地治愈：把那一块放回组里，文件夹本身丢掉。
        /// 不清的话它会一直挂在板上，占着 2x2 的位却只显示一个缩略格。
        /// </summary>
        private static void HealFolders(BoardLayout layout)
        {
            foreach (var g in layout.Groups)
            {
                if (g?.Tiles == null) continue;

                for (int i = g.Tiles.Count - 1; i >= 0; i--)
                {
                    var t = g.Tiles[i];
                    if (t == null || !t.IsFolder) continue;

                    var kids = new List<TileItem>();
                    foreach (var c in t.Children)
                        if (c != null) kids.Add(c);

                    if (kids.Count >= 2) continue;

                    // 先把剩下的一两块插回文件夹原来的位置，再把文件夹摘掉 ——
                    // 顺序反过来会让下标错位，磁贴落到组的末尾去
                    for (int k = 0; k < kids.Count; k++)
                    {
                        kids[k].Column = -1;
                        kids[k].Row = -1;
                        g.Tiles.Insert(i, kids[k]);
                    }

                    g.Tiles.Remove(t);
                }
            }
        }

        public static void Save(BoardLayout layout)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(layout, Options));
            }
            catch { /* 存盘失败不阻断 UI */ }
        }

        /// <summary>
        /// 深拷贝一份布局（走 JSON 往返）。
        /// 自检拿它当「原样快照」：拖放测试必然会改坐标并写盘，
        /// 跑完先整体回滚再存，用户那块板才不会被测试挪过。
        /// </summary>
        public static BoardLayout Clone(BoardLayout layout)
        {
            if (layout == null) return null;

            try
            {
                string json = JsonSerializer.Serialize(layout, Options);
                return JsonSerializer.Deserialize<BoardLayout>(json, Options);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>首次启动的兜底：给一块空板。</summary>
        public static BoardLayout CreateEmpty()
        {
            return new BoardLayout
            {
                Source = "manual",
                Groups = new List<TileGroup>
                {
                    new TileGroup { Name = "常用" }
                }
            };
        }
    }
}
