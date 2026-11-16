using System;
using System.Collections.Generic;
using System.IO;

namespace TileBoard.Services
{
    /// <summary>
    /// 跨进程的「固定到磁贴」请求队列。
    ///
    /// 资源管理器右键菜单只能起一个新进程（`TileBoard.exe --pin "路径"`），
    /// 而真正干活的是已经在跑的那个实例 —— 单实例信号只带得动「该唤出了」这一个布尔量，
    /// 带不了路径。所以路径先落到这个目录里，信号再去叫醒对方，对方起来后把目录里的都取走。
    /// 每个请求一个文件，多个请求同时来也不会互相覆盖。
    /// </summary>
    internal static class PinQueue
    {
        private static string QueueDirectory => Path.Combine(LayoutStore.Directory, "pin-queue");

        internal static void Enqueue(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                System.IO.Directory.CreateDirectory(QueueDirectory);
                string name = $"pin-{DateTime.UtcNow.Ticks}-{Environment.ProcessId}.txt";
                File.WriteAllText(Path.Combine(QueueDirectory, name), path);
            }
            catch { /* 落不了盘就只是这一次没固定上，不该把调用方拖崩 */ }
        }

        /// <summary>取走全部待处理路径，并删掉对应文件。按写入时间顺序返回。</summary>
        internal static List<string> Drain()
        {
            var result = new List<string>();

            try
            {
                if (!System.IO.Directory.Exists(QueueDirectory)) return result;

                var files = new List<FileInfo>(new DirectoryInfo(QueueDirectory).GetFiles("pin-*.txt"));
                files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

                foreach (var file in files)
                {
                    try
                    {
                        string text = File.ReadAllText(file.FullName).Trim();
                        if (text.Length > 0) result.Add(text);
                        File.Delete(file.FullName);
                    }
                    catch
                    {
                        // 单个文件坏掉不影响其它的
                    }
                }
            }
            catch { }

            return result;
        }
    }
}
