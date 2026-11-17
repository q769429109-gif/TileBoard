using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TileBoard.Services
{
    /// <summary>
    /// 开机自启：计划任务方案（2026-11-17 起）。
    ///
    /// 旧方案写 HKCU\...\Run 键，功能正常 —— 但 Win11 对 Run 键自启做「错峰拉起」，
    /// 实测开机后要等 2~3 分钟才轮到我们（用户在这窗口里检查就以为没自启）。
    /// 改用任务计划程序的「用户登录时」触发器：登录后几秒内就起，不经排队。
    /// 只动当前用户的任务，不需要管理员权限。
    ///
    /// 旧 Run 键不主动清理会双路拉起 —— 单实例互斥能兜住（第二个实例只发个显示请求），
    /// 但多此一举，Enable/Disable 时顺手删干净。
    /// </summary>
    internal static class Autostart
    {
        private const string TaskName = "TileBoard";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "TileBoard";

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
            // 计划任务在不在；老 Run 键还没清干净也算开着（迁移期兼容）
            return TaskExists() || LegacyRunKeyExists();
        }

        /// <summary>给日志用的命令预览（内部字段只读展示，不暴露真实可写入口）。</summary>
        internal static string CommandPreview()
        {
            string cmd = Command;
            return cmd == null ? "(null)" : $"计划任务 {TaskName}: {cmd}";
        }

        internal static bool Enable()
        {
            try
            {
                string cmd = Command;
                if (cmd == null) return false;

                RemoveLegacyRunKey();   // 老方案退场，防双路拉起

                // 登录时触发、交互令牌运行、只有当前用户 —— 非管理员可注册
                return Schtasks("/Create", "/TN", TaskName,
                                "/TR", cmd,
                                "/SC", "ONLOGON", "/F");
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
                Schtasks("/Delete", "/TN", TaskName, "/F");   // 任务不存在就算了
                RemoveLegacyRunKey();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>调一次 schtasks.exe。退出码 0 = 成功。</summary>
        private static bool Schtasks(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                // 全路径而不是裸 "schtasks"：不给 PATH 劫持留缝
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); } catch { /* 已退出就算了 */ }
                    return false;
                }
                return p.ExitCode == 0;
            }
        }

        private static bool TaskExists()
        {
            try
            {
                return Schtasks("/Query", "/TN", TaskName);
            }
            catch
            {
                return false;
            }
        }

        // ---------- 旧 Run 键方案（保留清理逻辑做迁移） ----------

        private static bool LegacyRunKeyExists()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    return key?.GetValue(RunValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void RemoveLegacyRunKey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    key?.DeleteValue(RunValueName, false);
                }
            }
            catch
            {
                // 删不掉不影响新方案；老键留着也只是多一次单实例互斥
            }
        }
    }
}
