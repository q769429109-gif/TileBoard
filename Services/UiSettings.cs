using System;
using System.IO;
using System.Text.Json;

namespace TileBoard.Services
{
    /// <summary>面板外观等界面级设置。和磁贴布局分开存，改宽度不会碰布局文件。</summary>
    public class UiSettings
    {
        /// <summary>面板宽度（DIP）。Win10 的面板宽度本来就是可拖拽的，这里也允许拖到满屏。</summary>
        public double PanelWidth { get; set; } = 0;

        /// <summary>面板高度（DIP）。0 表示铺满工作区高度。</summary>
        public double PanelHeight { get; set; } = 0;

        /// <summary>是否全屏：宽高都等于工作区。</summary>
        public bool FullScreen { get; set; }

        /// <summary>背景不透明度 0.15 ~ 1.0。越低，桌面透得越多。毛玻璃开着时这层同时是亚克力上的染色。</summary>
        public double BackOpacity { get; set; } = 0.65;

        /// <summary>是否启用亚克力模糊（系统 DWM）。显卡/远程桌面下若不合适可关掉，退回纯半透明。</summary>
        public bool Acrylic { get; set; } = true;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = true };

        private static string FilePath => Path.Combine(LayoutStore.Directory, "settings.json");

        public static UiSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new UiSettings();
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Options) ?? new UiSettings();
            }
            catch
            {
                return new UiSettings();
            }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(LayoutStore.Directory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
            }
            catch { /* 存盘失败不影响使用 */ }
        }
    }
}
