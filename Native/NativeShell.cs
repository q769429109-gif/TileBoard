using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TileBoard.Native
{
    /// <summary>
    /// shell:AppsFolder 枚举与图标提取的原生封装。
    /// 走 AppsFolder 是唯一不需要管理员权限、又能同时覆盖 UWP 与 Win32 的路径。
    /// </summary>
    public static class NativeShell
    {
        // ---- GUID 常量 ----
        private static readonly Guid FOLDERID_AppsFolder =
            new Guid("1e87508d-89c2-42f0-8a7e-645a0f50ca58");

        private static readonly Guid BHID_EnumItems =
            new Guid("94f60519-2850-4924-aa5a-d15e84868039");

        private static readonly Guid IID_IShellItem =
            new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        private const uint SIGDN_NORMALDISPLAY = 0x00000000;
        private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;

        private const int SIIGBF_BIGGERSIZEOK = 0x01;
        private const int SIIGBF_ICONONLY = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SIZE
        {
            public int cx;
            public int cy;
            public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumShellItems
        {
            void Next(uint celt, out IntPtr rgelt, out uint pceltFetched);
            void Skip(uint celt);
            void Reset();
            void Clone(out IEnumShellItems ppenum);
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetKnownFolderItem(
            ref Guid rfid, uint dwFlags, IntPtr hToken, ref Guid riid, out IntPtr ppv);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out BITMAP lpvObject);

        /// <summary>取位图宽度，用来判断图标是不是只有 16px 那种缩略版本。</summary>
        public static int BitmapWidth(IntPtr hbmp)
        {
            if (hbmp == IntPtr.Zero) return 0;
            return GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out BITMAP bm) != 0 ? bm.bmWidth : 0;
        }

        /// <summary>最近一次取图失败的原因，供诊断输出用。</summary>
        public static string LastError { get; private set; }

        /// <summary>AppsFolder 中的一个可见应用项。</summary>
        public sealed class AppEntry
        {
            /// <summary>
            /// 唯一标识。两种形态：
            ///   UWP —— AppUserModelID，形如 Microsoft.WindowsStore_8wekyb3d8bbwe!App
            ///   桌面 —— 绝对路径，形如 C:\...\python.exe
            /// 绝不能截断，截断后 shell:AppsFolder 找不到项。
            /// </summary>
            public string Aumid { get; set; }

            /// <summary>显示名。</summary>
            public string Name { get; set; }

            public bool IsFilePath =>
                !string.IsNullOrEmpty(Aumid) &&
                (Aumid.Length > 2 && Aumid[1] == ':') || (Aumid?.StartsWith(@"\\") ?? false);
        }

        /// <summary>枚举 shell:AppsFolder，返回全部可在开始菜单出现的应用。</summary>
        public static List<AppEntry> EnumerateApps()
        {
            var result = new List<AppEntry>();
            IntPtr folderPtr = IntPtr.Zero;
            IShellItem folder = null;
            object enumObj = null;
            IntPtr enumPtr = IntPtr.Zero;

            try
            {
                Guid folderId = FOLDERID_AppsFolder;
                Guid iidItem = IID_IShellItem;
                int hr = SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iidItem, out folderPtr);
                if (hr != 0 || folderPtr == IntPtr.Zero) return result;

                folder = (IShellItem)Marshal.GetObjectForIUnknown(folderPtr);

                Guid bhid = BHID_EnumItems;
                Guid iidEnum = typeof(IEnumShellItems).GUID;
                folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out enumPtr);
                if (enumPtr == IntPtr.Zero) return result;

                enumObj = Marshal.GetObjectForIUnknown(enumPtr);
                var enumerator = (IEnumShellItems)enumObj;

                while (true)
                {
                    IntPtr one = IntPtr.Zero;
                    uint fetched = 0;
                    enumerator.Next(1, out one, out fetched);
                    if (fetched == 0 || one == IntPtr.Zero) break;

                    IShellItem item = null;
                    try
                    {
                        item = (IShellItem)Marshal.GetObjectForIUnknown(one);

                        string name = GetDisplay(item, SIGDN_NORMALDISPLAY);
                        string parsingName = GetDisplay(item, SIGDN_PARENTRELATIVEPARSING);

                        if (!string.IsNullOrWhiteSpace(parsingName))
                        {
                            result.Add(new AppEntry
                            {
                                Aumid = parsingName,             // 原样保留，路径或 AUMID 都不动
                                Name = string.IsNullOrWhiteSpace(name) ? parsingName : name
                            });
                        }
                    }
                    catch { /* 单项失败不影响整轮枚举 */ }
                    finally
                    {
                        if (item != null) Marshal.ReleaseComObject(item);
                        Marshal.Release(one);
                    }
                }
            }
            catch { /* 枚举整体失败时返回已收集部分 */ }
            finally
            {
                if (enumObj != null) Marshal.ReleaseComObject(enumObj);
                if (enumPtr != IntPtr.Zero) Marshal.Release(enumPtr);
                if (folder != null) Marshal.ReleaseComObject(folder);
                if (folderPtr != IntPtr.Zero) Marshal.Release(folderPtr);
            }

            return result;
        }

        private static string GetDisplay(IShellItem item, uint sigdn)
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                item.GetDisplayName(sigdn, out ptr);
                return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                if (ptr != IntPtr.Zero) CoTaskMemFree(ptr);
            }
        }

        /// <summary>标识是否本身就是磁盘路径。</summary>
        public static bool LooksLikePath(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            return (id.Length > 2 && id[1] == ':') || id.StartsWith(@"\\");
        }

        private static IEnumerable<string> ParsingCandidates(string id)
        {
            if (LooksLikePath(id))
            {
                yield return id;
                yield break;
            }
            yield return "shell:AppsFolder\\" + id;
            yield return id;
        }

        /// <summary>取某个应用的 256px 图标位图句柄；失败返回 IntPtr.Zero。</summary>
        public static IntPtr GetAppImageHandle(string aumid, int pixels)
        {
            LastError = null;
            if (string.IsNullOrWhiteSpace(aumid)) return IntPtr.Zero;

            foreach (string parsing in ParsingCandidates(aumid))
            {
                object obj = null;
                IntPtr hbmp = IntPtr.Zero;
                try
                {
                    Guid iid = typeof(IShellItemImageFactory).GUID;
                    object shItem;
                    SHCreateItemFromParsingName(parsing, IntPtr.Zero, ref iid, out shItem);
                    obj = shItem;
                    var factory = (IShellItemImageFactory)shItem;
                    factory.GetImage(new SIZE(pixels, pixels), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbmp);
                    if (hbmp != IntPtr.Zero) return hbmp;
                }
                catch (Exception ex)
                {
                    LastError = ex.GetType().Name + ": " + ex.Message.Trim();
                }
                finally
                {
                    if (obj != null) Marshal.ReleaseComObject(obj);
                }
            }

            return IntPtr.Zero;
        }

        public static void FreeBitmap(IntPtr hbmp)
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
        }
    }
}
