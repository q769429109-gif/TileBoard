using System;
using System.IO;
using Microsoft.Win32;

namespace TileBoard.Native
{
    /// <summary>
    /// 把「固定到磁贴」注册进资源管理器的右键菜单。
    ///
    /// 挂在 HKCU\Software\Classes 而不是 HKLM：不需要管理员权限，
    /// 也不影响别的用户。文件（*）和文件夹（Directory）各挂一份，
    /// 命令就是起一个 `TileBoard.exe --pin "路径"`，由已经在跑的那个实例接手。
    ///
    /// 每次启动都会重写一遍 —— 程序换了目录（重新编译、挪位置）之后，
    /// 菜单里那条命令还能自动指到新路径，不会变成死链。
    /// </summary>
    internal static class ShellMenu
    {
        private const string VerbKey = "TileBoardPin";
        private const string Title = "固定到磁贴（磁贴板）";

        private static readonly string[] Targets =
        {
            @"Software\Classes\*\shell\" + VerbKey,
            @"Software\Classes\Directory\shell\" + VerbKey,
        };

        internal static bool Install()
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

            bool ok = true;

            foreach (string target in Targets)
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(target, true))
                    {
                        if (key == null) { ok = false; continue; }

                        key.SetValue(null, Title, RegistryValueKind.String);
                        key.SetValue("Icon", exe, RegistryValueKind.String);
                        key.SetValue("MultiSelectModel", "Single", RegistryValueKind.String);

                        using (RegistryKey cmd = key.CreateSubKey("command", true))
                        {
                            if (cmd == null) { ok = false; continue; }
                            cmd.SetValue(null, $"\"{exe}\" --pin \"%1\"", RegistryValueKind.String);
                        }
                    }
                }
                catch
                {
                    ok = false;
                }
            }

            return ok;
        }

        internal static void Uninstall()
        {
            foreach (string target in Targets)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(target, false); }
                catch { }
            }
        }

        /// <summary>菜单项是否已经挂上、且指的还是当前这个 exe。</summary>
        internal static bool IsCurrent()
        {
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return false;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Targets[0] + @"\command", false))
                {
                    string cmd = key?.GetValue(null) as string;
                    return cmd != null && cmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
