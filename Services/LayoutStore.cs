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
                return layout?.Groups != null && layout.Groups.Count > 0 ? layout : null;
            }
            catch
            {
                return null;
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
