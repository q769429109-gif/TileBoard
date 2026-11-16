using System;
using Microsoft.Win32;

namespace TileBoard.Services
{
    /// <summary>
    /// 开机自启：只写 HKCU\...\Run，不需要管理员权限，也不装服务。
    /// 关掉就是把这一项删掉 —— 别留下一个指向已删除文件的空壳，
    /// 否则每次开机 Windows 都要去找一次那个 exe。
    /// </summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string Name = "TileBoard";

        private static string Command
        {
            get
            {
                string exe = Environment.ProcessPath;
                return string.IsNullOrEmpty(exe) ? null : $"\"{exe}\" --autostart";
            }
        }

        internal static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return key?.GetValue(Name) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool Enable()
        {
            try
            {
                string cmd = Command;
                if (cmd == null) return false;

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true))
                {
                    if (key == null) return false;
                    key.SetValue(Name, cmd, RegistryValueKind.String);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static bool Disable()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return true;
                    key.DeleteValue(Name, false);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
