using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TileBoard.Services
{
    /// <summary>手机面板上的一个快捷操作（短信 / 呼叫 / 照片 / 发送文件）。</summary>
    public class PhoneAction
    {
        public string Label { get; set; }
        public string Url { get; set; }
    }

    /// <summary>「最近」里的一条记录。</summary>
    public class PhoneRecent
    {
        public string AppName { get; set; }
        public string Time { get; set; }
        public string Summary { get; set; }
        public string Url { get; set; }
    }

    /// <summary>整张手机状态卡片。</summary>
    public class PhoneCard
    {
        public string DeviceName { get; set; } = "已连接的手机";
        public string Status { get; set; } = "";
        public List<PhoneAction> Actions { get; } = new List<PhoneAction>();
        public List<PhoneRecent> Recents { get; } = new List<PhoneRecent>();
    }

    /// <summary>
    /// 读 Win11 开始菜单里那个「手机连接」面板的真实数据。
    ///
    /// 面板本身是一张 Adaptive Card，Phone Link 把它写在
    /// %LOCALAPPDATA%\Packages\Microsoft.YourPhone_...\LocalState\StartMenu\StartMenuCompanion.json。
    /// 里面已经有设备名、连接状态、四个功能的 ms-phone: 深链，以及最近通知，
    /// 所以这里不编造任何内容 —— 文件在什么就是什么，换了手机也跟着变。
    /// </summary>
    public static class PhoneCompanion
    {
        private static string CardPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Packages\Microsoft.YourPhone_8wekyb3d8bbwe\LocalState\StartMenu\StartMenuCompanion.json");

        private static readonly string[] KnownActions = { "短信", "呼叫", "照片", "发送文件" };

        /// <summary>读不到卡片就返回 null，界面据此显示「未连接」。</summary>
        public static PhoneCard Load()
        {
            try
            {
                string path = CardPath;
                if (!File.Exists(path)) return null;

                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = doc.RootElement;
                var card = new PhoneCard();

                // speak 形如「starphone - 已从电脑断开连接」，设备名和状态都在这一句里
                if (root.TryGetProperty("speak", out var speakEl) &&
                    speakEl.ValueKind == JsonValueKind.String)
                {
                    string speak = speakEl.GetString() ?? "";
                    int dash = speak.IndexOf(" - ", StringComparison.Ordinal);
                    if (dash > 0)
                    {
                        card.DeviceName = speak.Substring(0, dash).Trim();
                        card.Status = speak.Substring(dash + 3).Trim();
                    }
                    else if (speak.Length > 0)
                    {
                        card.DeviceName = speak.Trim();
                    }
                }

                Walk(root, card);

                // 顺序按截图里那四个功能排，免得 JSON 里的先后不同
                card.Actions.Sort((a, b) =>
                    Array.IndexOf(KnownActions, a.Label).CompareTo(Array.IndexOf(KnownActions, b.Label)));

                return card;
            }
            catch
            {
                return null;
            }
        }

        private static void Walk(JsonElement el, PhoneCard card)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    string type = Str(el, "type");
                    string title = Str(el, "title");
                    string url = Str(el, "url");

                    if (type == "Action.OpenUrl" &&
                        !string.IsNullOrWhiteSpace(title) &&
                        !string.IsNullOrWhiteSpace(url) &&
                        url.StartsWith("ms-phone", StringComparison.OrdinalIgnoreCase))
                    {
                        Classify(title, url, card);
                    }

                    foreach (var p in el.EnumerateObject())
                        Walk(p.Value, card);
                    break;

                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                        Walk(item, card);
                    break;
            }
        }

        private static void Classify(string title, string url, PhoneCard card)
        {
            // 功能项的 title 形如「短信. 查看和发送短信」
            foreach (var label in KnownActions)
            {
                if (!title.StartsWith(label + ".", StringComparison.Ordinal)) continue;
                if (card.Actions.Any(a => a.Label == label)) return;

                card.Actions.Add(new PhoneAction { Label = label, Url = url });
                return;
            }

            // 最近项形如「来自 美团 的 1 条新通知。8 小时前。正文…」
            // 或「星期六 19:02 拍摄的照片」
            if (!title.StartsWith("来自 ", StringComparison.Ordinal) &&
                !title.Contains("拍摄的照片"))
                return;

            if (card.Recents.Any(r => r.Url == url && r.Summary == title)) return;
            card.Recents.Add(ParseRecent(title, url));
        }

        private static PhoneRecent ParseRecent(string title, string url)
        {
            var m = Regex.Match(title, @"^来自 (.+?) 的 \d+ 条新通知。");
            if (m.Success)
            {
                string rest = title.Substring(m.Length);
                int dot = rest.IndexOf('。');

                return new PhoneRecent
                {
                    AppName = m.Groups[1].Value.Trim(),
                    Time = dot > 0 ? rest.Substring(0, dot).Trim() : "",
                    Summary = dot > 0 && dot + 1 < rest.Length ? rest.Substring(dot + 1).Trim() : rest.Trim(),
                    Url = url
                };
            }

            // 照片：把「星期六 19:02」当时间，「拍摄的照片」当摘要
            var t = Regex.Match(title, @"^(.+?)\s*拍摄的照片$");
            if (t.Success)
            {
                return new PhoneRecent
                {
                    AppName = "照片",
                    Time = t.Groups[1].Value.Trim(),
                    Summary = "拍摄的照片",
                    Url = url
                };
            }

            return new PhoneRecent { AppName = "通知", Summary = title, Url = url };
        }

        private static string Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
