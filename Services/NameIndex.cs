using System;
using System.Globalization;

namespace TileBoard.Services
{
    /// <summary>
    /// 应用名的首字母分组 —— Win10「所有应用」列表左边那一列 A / B / C 就是靠它分的。
    ///
    /// 中文不能取 Unicode 码位，得取拼音首字母：「计算器」归 J、「微信」归 W。
    /// 这里不引第三方拼音库，用的是排序法：把每个声母的第一个汉字当作栅栏，
    /// 「阿八嚓搭…匝」这 23 个字在中文本地化排序里严格按声母递升，
    /// 拿目标字和它们逐个比大小，最后一个不大于它的栅栏就是它的声母。
    /// 这样连多音字都交给系统排序规则去判，比硬查表准。
    ///
    /// 数字归 #，标点符号与其它文字（假名、西里尔、书名号开头）归 & ——
    /// Win10 的字母索引里 & 和 # 是两个独立格子，& 还排在 # 前面。
    /// </summary>
    public static class NameIndex
    {
        /// <summary>数字开头的组。</summary>
        public const char Other = '#';

        /// <summary>符号与无法判拼音的其它文字开头的组。</summary>
        public const char Symbol = '&';

        /// <summary>23 个声母的栅栏字。i、u、v 开头不存在，所以没有 I / U / V。</summary>
        private const string Boundaries = "阿八嚓搭蛾发噶哈击喀垃妈拿哦啪期然撒塌挖昔压匝";

        private const string BoundaryLetters = "ABCDEFGHJKLMNOPQRSTWXYZ";

        private static readonly CompareInfo Zh = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;

        /// <summary>取一个名字该归哪个字母组。</summary>
        public static char Group(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return Other;

            string s = name.TrimStart(' ', '\t', '"', '\'', '（', '(');
            if (s.Length == 0) return Other;

            char c = s[0];

            if (c >= 'a' && c <= 'z') return (char)(c - 32);
            if (c >= 'A' && c <= 'Z') return c;

            if (c >= '0' && c <= '9') return Other;

            // 基本汉字区之外的一律进 &：假名、西里尔、emoji、书名号都没有拼音可判，
            // Win10 把这类东西和标点放在同一个符号组里
            if (c < 0x4E00 || c > 0x9FFF) return Symbol;

            string one = c.ToString();
            int hit = -1;

            for (int i = 0; i < Boundaries.Length; i++)
            {
                if (Zh.Compare(one, Boundaries[i].ToString()) >= 0) hit = i;
                else break;
            }

            return hit < 0 ? Other : BoundaryLetters[hit];
        }

        /// <summary>
        /// 组的排序权重：&amp; 最前，然后 #，再 A→Z。
        /// 注意这里不能用「声母表」的下标 —— 声母表里没有 I / U / V，
        /// 而英文名（IDLE、Ubisoft、VLC）本来就会落在 I / U / V 上，
        /// 拿声母下标去排会把它们当成 # 混进数字组里。
        /// </summary>
        public static int Rank(char letter)
        {
            if (letter == '\0') return -2;   // 「最近添加」这种不参与字母排序的伪标题
            if (letter == Symbol) return -1;
            if (letter >= 'A' && letter <= 'Z') return letter - 'A' + 1;
            return 0;   // # 以及一切非字母
        }

        /// <summary>按中文习惯比较两个名字（拼音序，和开始菜单一致）。</summary>
        public static int Compare(string a, string b)
        {
            return Zh.Compare(a ?? string.Empty, b ?? string.Empty, CompareOptions.None);
        }

        /// <summary>标题文字。& 和 # 各自一格，和 Win10 的索引面板对得上。</summary>
        public static string Header(char letter)
        {
            if (letter == Symbol) return "&";
            if (letter == Other) return "#";
            return letter.ToString();
        }
    }
}
