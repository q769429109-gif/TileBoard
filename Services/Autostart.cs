using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace TileBoard.Services
{
    /// <summary>
    /// 开机自启。三层落地，一路退到底，绝不再只报一句「写入失败」。
    ///
    /// 为什么三层：2026-11-18 用户把这软件给朋友，对方勾自启直接失败，而旧实现
    /// 用裸 try/catch 把 schtasks 的退出码和错误文本全吞了，无从下手。自启注册
    /// 在别人的机器上会被三类东西拦：安全软件的 HIPS（创建登录自启任务是恶意
    /// 软件的典型行为）、组策略、以及非管理员 + 受限令牌。任何单一途径都可能挂。
    ///
    /// ① 进程内 Task Scheduler COM（首选）：不启动任何子进程，绕开对 schtasks.exe
    ///    的进程创建拦截，失败还能拿到精确 HRESULT。
    /// ② schtasks.exe：COM 被禁（有些精简系统删了 taskschd 的类型库）时走这条。
    /// ③ HKCU\...\Run 键：只写当前用户注册表，不需要任何权限，几乎不可能失败。
    ///    代价是 Win11 对 Run 键自启做错峰拉起，登录后要等 1~3 分钟才轮到我们。
    ///
    /// 三层全挂才返回 false，并把三层的原始错误一起交出去。
    /// </summary>
    internal static class Autostart
    {
        private const string TaskName = "TileBoard";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "TileBoard";

        // taskschd.h 里的枚举值
        private const int TASK_TRIGGER_LOGON = 9;
        private const int TASK_ACTION_EXEC = 0;
        private const int TASK_CREATE_OR_UPDATE = 6;
        private const int TASK_LOGON_INTERACTIVE_TOKEN = 3;

        /// <summary>最近一次 Enable 的结果说明（给人看，同时进日志）。</summary>
        internal static string LastDetail { get; private set; } = "";

        /// <summary>true = 这次是靠注册表 Run 键兜住的，启动会比计划任务慢。</summary>
        internal static bool LastUsedRunKeyFallback { get; private set; }

        private static string Command
        {
            get
            {
                string exe = Environment.ProcessPath;
                return string.IsNullOrEmpty(exe) ? null : $"\"{exe}\" --autostart";
            }
        }

        /// <summary>已开启：注册表键或计划任务任一存在即算。</summary>
        internal static bool IsEnabled()
        {
            if (RunKeyExists()) return true;          // 微秒级，先看它
            return TaskExistsViaCom() || TaskExistsViaSchtasks();
        }

        /// <summary>给日志用的命令预览。</summary>
        internal static string CommandPreview()
        {
            string cmd = Command;
            return cmd == null ? "(null)" : $"计划任务 {TaskName}: {cmd}";
        }

        internal static bool Enable()
        {
            LastDetail = "";
            LastUsedRunKeyFallback = false;

            try
            {
                string exe = Environment.ProcessPath;
                string cmd = Command;
                if (cmd == null)
                {
                    LastDetail = "取不到自身路径（Environment.ProcessPath 为空）";
                    return false;
                }

                // 程序在临时目录（有人直接从压缩包内双击运行）→ 路径重启即失效，
                // 自启会「写入成功但开机无反应」，比写失败更难查
                string tmp = Path.GetTempPath();
                bool inTemp = !string.IsNullOrEmpty(tmp)
                              && exe.StartsWith(tmp, StringComparison.OrdinalIgnoreCase);

                // ① 进程内 COM
                string comErr = RegisterTaskViaCom(exe);
                if (comErr == null)
                {
                    RemoveRunKey();
                    LastDetail = "计划任务已注册（COM）";
                    if (inTemp) LastDetail += "；但程序在临时目录，重启后路径可能失效";
                    return true;
                }

                // ② schtasks.exe
                var st = RunSchtasks("/Create", "/TN", TaskName, "/TR", cmd, "/SC", "ONLOGON", "/F");
                if (st.Ok)
                {
                    RemoveRunKey();
                    LastDetail = "计划任务已注册（schtasks）";
                    if (inTemp) LastDetail += "；但程序在临时目录，重启后路径可能失效";
                    return true;
                }

                // ③ 注册表 Run 键
                if (WriteRunKey(cmd))
                {
                    LastUsedRunKeyFallback = true;
                    LastDetail = $"计划任务失败（COM: {comErr} / schtasks: {st.Detail}）；" +
                                 "已改用注册表自启，开机后约 1~3 分钟才会出现";
                    return true;
                }

                LastDetail = $"全部失败（COM: {comErr} / schtasks: {st.Detail} / Run 键写入失败）";
                return false;
            }
            catch (Exception ex)
            {
                LastDetail = "异常 " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        internal static bool Disable()
        {
            LastDetail = "";
            LastUsedRunKeyFallback = false;

            try
            {
                string comErr = DeleteTaskViaCom();
                var st = RunSchtasks("/Delete", "/TN", TaskName, "/F");
                RemoveRunKey();

                // 任务本来就不存在时，两条删路都会「失败」，这不算错
                bool stillThere = TaskExistsViaCom();
                if (stillThere)
                {
                    LastDetail = $"计划任务删不掉（COM: {comErr} / schtasks: {st.Detail}）";
                    return false;
                }

                LastDetail = "已关闭自启";
                return true;
            }
            catch (Exception ex)
            {
                LastDetail = "异常 " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // ---------- ① Task Scheduler COM ----------

        /// <summary>成功返回 null，失败返回错误说明。不拉起任何子进程。</summary>
        private static string RegisterTaskViaCom(string exePath)
        {
            try
            {
                Type type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return "系统没有 Schedule.Service（任务计划服务可能被移除）";

                dynamic service = Activator.CreateInstance(type);
                service.Connect();
                dynamic root = service.GetFolder("\\");

                dynamic task = service.NewTask(0);
                task.RegistrationInfo.Description = "TileBoard 登录时自动启动";
                task.RegistrationInfo.Author = "TileBoard";

                dynamic trigger = task.Triggers.Create(TASK_TRIGGER_LOGON);
                trigger.UserId = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                trigger.Enabled = true;

                dynamic action = task.Actions.Create(TASK_ACTION_EXEC);
                action.Path = exePath;
                action.Arguments = "--autostart";
                action.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";

                task.Settings.Enabled = true;
                task.Settings.DisallowStartIfOnBatteries = false;
                task.Settings.StopIfGoingOnBatteries = false;
                task.Settings.ExecutionTimeLimit = "PT0S";
                task.Settings.MultipleInstances = 0;      // 已在跑就不起第二个

                // TASK_LOGON_INTERACTIVE_TOKEN：用当前登录令牌，不需要密码，非管理员可注册
                root.RegisterTaskDefinition(TaskName, task, TASK_CREATE_OR_UPDATE,
                                            null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
                return null;
            }
            catch (Exception ex)
            {
                int hr = 0;
                try { hr = System.Runtime.InteropServices.Marshal.GetHRForException(ex); } catch { }
                return hr != 0 ? $"0x{hr:X8} {ex.Message}" : $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        private static bool TaskExistsViaCom()
        {
            try
            {
                Type type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return false;

                dynamic service = Activator.CreateInstance(type);
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                folder.GetTask(TaskName);     // 任务不存在会抛
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string DeleteTaskViaCom()
        {
            try
            {
                Type type = Type.GetTypeFromProgID("Schedule.Service");
                if (type == null) return "无 Schedule.Service";

                dynamic service = Activator.CreateInstance(type);
                service.Connect();
                dynamic folder = service.GetFolder("\\");
                folder.DeleteTask(TaskName, 0);
                return null;
            }
            catch (Exception ex)
            {
                int hr = 0;
                try { hr = System.Runtime.InteropServices.Marshal.GetHRForException(ex); } catch { }
                return hr != 0 ? $"0x{hr:X8}" : ex.Message;
            }
        }

        // ---------- ② schtasks.exe ----------

        private static bool TaskExistsViaSchtasks()
        {
            var r = RunSchtasks("/Query", "/TN", TaskName);
            return r.Ok;
        }

        /// <summary>
        /// 调一次 schtasks.exe。旧实现只看 ExitCode 且把它吞进裸 catch，
        /// 出问题时连「拒绝访问」还是「任务名冲突」都分不出来 —— 现在把
        /// 退出码和输出原样带回来。
        /// </summary>
        private static (bool Ok, string Detail) RunSchtasks(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    // 全路径而不是裸 "schtasks"：不给 PATH 劫持留缝
                    FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string a in args) psi.ArgumentList.Add(a);

                using (Process p = Process.Start(psi))
                {
                    // 先读干净两个流再等，否则缓冲区满会互相卡死
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();

                    if (!p.WaitForExit(20000))
                    {
                        try { p.Kill(); } catch { }
                        return (false, "/Query 或 /Create 超时 20s");
                    }

                    if (p.ExitCode == 0) return (true, "");

                    string text = (stderr + " " + stdout).Trim();
                    if (text.Length > 220) text = text.Substring(0, 220) + "…";
                    return (false, $"exit={p.ExitCode} {text}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------- ③ HKCU Run 键 ----------

        private static bool RunKeyExists()
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

        private static bool WriteRunKey(string cmd)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true))
                {
                    if (key == null) return false;
                    key.SetValue(RunValueName, cmd, RegistryValueKind.String);
                }
                return RunKeyExists();
            }
            catch
            {
                return false;
            }
        }

        private static bool RemoveRunKey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key?.GetValue(RunValueName) == null) return true;
                    key.DeleteValue(RunValueName, false);
                }
                return true;
            }
            catch
            {
                // 删不掉不影响新方案；老键留着也只是多一次单实例互斥
                return false;
            }
        }
    }
}
