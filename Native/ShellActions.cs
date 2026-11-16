using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TileBoard.Native
{
    /// <summary>
    /// 右键菜单动作的原生封装：文件位置 / 任务栏固定 / 管理员运行 / 卸载。
    ///
    /// 实现要点：
    /// - 执行动词统一走 ShellExecuteEx + SEE_MASK_INVOKEIDLIST + pidl ——
    ///   让 shell 自己解析目标的 IContextMenu 并调用规范动词（runas / taskbarpin / uninstall…），
    ///   不用自己 BindToHandler 折腾 IContextMenu（BHID_SFUIObject 这类 GUID 容易记错）。
    /// - 枚举动词（--verbprobe 诊断用）走 SHCreateItemFromParsingName →
    ///   IShellItem::BindToHandler(BHID_SFUIObject)：直调 IShellFolder.GetUIObjectOf
    ///   在本机实测 AV，BindToHandler 是 shell 官方封装层，行为与资源管理器一致。
    /// - Win32 应用的 AUMID（形如 Chrome.XXX，非路径）本身没有文件位置，
    ///   要去开始菜单扫 .lnk，读快捷方式属性里的 System.AppUserModel.ID 找到对应项。
    /// </summary>
    public static class ShellActions
    {
        /// <summary>最近一次动作的失败原因，给状态栏与诊断报告用。</summary>
        public static string LastError { get; private set; }

        // ===================== 经典 IID / CLSID =====================

        private static readonly Guid IID_IShellFolder =
            new Guid("000214E6-0000-0000-C000-000000000046");
        private static readonly Guid IID_IContextMenu =
            new Guid("000214E4-0000-0000-C000-000000000046");
        private static readonly Guid IID_IShellItem =
            new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        // BHID_SFUIObject（shlguid.h，SDK 10.0.22621.0 原文）：
        // 语义就是「对 shell 项调 GetUIObjectOf」，bind 出 IContextMenu 的标准姿势
        private static readonly Guid BHID_SFUIObject =
            new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5");
        private static readonly Guid IID_IShellLinkW =
            new Guid("000214F9-0000-0000-C000-000000000046");
        private static readonly Guid CLSID_ShellLink =
            new Guid("00021401-0000-0000-C000-000000000046");

        private const uint SHCONTF_FOLDERS = 0x20;
        private const uint SHCONTF_NONFOLDERS = 0x40;
        private const uint SHCONTF_INCLUDEHIDDEN = 0x80;

        private const uint SHGDN_NORMAL = 0x0000;
        private const uint SHGDN_INFOLDER = 0x0001;
        private const uint SHGDN_FORPARSING = 0x8000;

        private const int SW_SHOWNORMAL = 1;

        // ===================== 结构体 =====================

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hMonitor;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct CMINVOKECOMMANDINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;          // LPCSTR：规范动词（taskbarpin 等）全是 ASCII
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct STRRET
        {
            [FieldOffset(0)] public int uType;      // 0=OLESTR 1=offset 2=cStr
            [FieldOffset(4)] public IntPtr pOleStr;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
            public uint dwFileType;
            public uint dwCreatorInfo;
            public ushort wTypeFlags;
            public ushort wreserved0;
            public ushort wreserved1;
            public uint dwReserved3;
            public uint dwReserved4;
            public uint dwReserved5;
            public uint dwReserved6;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pwszVal;   // VT_LPWSTR = 31 时有效
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPERTYKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        // ===================== COM 接口 =====================

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            void ParseDisplayName(IntPtr hwnd, IntPtr pbc,
                [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
                out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            void EnumObjects(IntPtr hwnd, uint grfFlags,
                [MarshalAs(UnmanagedType.Interface)] out IEnumIDList ppenumIDList);
            void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out object ppv);
            void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out object ppv);
            [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            void CreateViewObject(IntPtr hwnd, ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out object ppv);
            // 注意：COM 接口方法里的数组默认按 SAFEARRAY 封送，而原生要的是
            // 裸指针数组 —— 不显式标 LPArray 就是访问违例（本机已实测）
            void GetAttributesOf(uint cidl,
                [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
            void GetUIObjectOf(IntPtr hwnd, uint cidl,
                [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid,
                IntPtr pvReserved, [MarshalAs(UnmanagedType.Interface)] out object ppv);
            void GetDisplayNameOf(IntPtr pidl, uint uFlags, out STRRET pName);
            void SetNameOf(IntPtr hwnd, IntPtr pidl,
                [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport, Guid("000214F2-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IEnumIDList
        {
            [PreserveSig] int Next(uint celt,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] rgelt,
                out uint pceltFetched);
            void Skip(uint celt);
            void Reset();
            [PreserveSig] int Clone(out IEnumIDList ppenum);
        }

        [ComImport, Guid("000214E4-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu,
                uint idCmdFirst, uint idCmdLast, uint uFlags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
            [PreserveSig] int GetCommandString(uint idCmd, uint uType,
                IntPtr pwReserved, IntPtr pszName, uint cchMax);
        }

        // IShellItem：Vista 后的封装层，BindToHandler 内部替我们处理
        // 「父文件夹 + GetUIObjectOf」那套繁琐且容易踩坑的直调。
        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid,
                [MarshalAs(UnmanagedType.Interface)] out object ppv);
            void GetParent([MarshalAs(UnmanagedType.Interface)] out object ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch,
                out WIN32_FIND_DATAW pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out ushort pwHotkey);
            void SetHotkey(ushort wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
                int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint cProps);
            void GetAt(uint iProp, out PROPERTYKEY pkey);
            void GetValue(ref PROPERTYKEY pkey, out PROPVARIANT pv);
            void SetValue(ref PROPERTYKEY pkey, ref PROPVARIANT value);
            void Commit();
        }

        // ===================== P/Invoke =====================

        [DllImport("shell32.dll", PreserveSig = false)]
        private static extern void SHGetDesktopFolder(out IShellFolder ppshf);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO sei);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void StrRetToBuf(ref STRRET pstrret, IntPtr pidl,
            [Out] StringBuilder pszBuf, uint cchBuf);

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CoCreateInstance(ref Guid clsid, IntPtr pUnkOuter,
            uint dwClsContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackagesByFamilyName(string familyName, ref uint count,
            IntPtr[] packageFullNames, ref uint bufferLength, [Out] char[] buffer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackagePathByFullName(string packageFullName,
            ref uint pathLength, [Out] char[] path);

        private static readonly PROPERTYKEY PKEY_AppUserModel_ID = new PROPERTYKEY
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5
        };

        private const uint GCS_VERBW = 0x0005;   // 规范动词（语言无关，如 taskbarpin）
        private const uint GCS_STRINGW = 0x0004; // 菜单显示文字

        // ===================== 基础设施 =====================

        /// <summary>解析一个 shell 路径（文件路径 / shell:AppsFolder\AUMID / shell:::{CLSID}）为 pidl。</summary>
        private static IntPtr ParsePidl(IShellFolder desktop, string parsing)
        {
            uint attrs = 0;
            desktop.ParseDisplayName(IntPtr.Zero, IntPtr.Zero, parsing,
                out _, out IntPtr pidl, ref attrs);
            return pidl;
        }

        private static string DisplayName(IShellFolder parent, IntPtr childPidl, uint flags)
        {
            parent.GetDisplayNameOf(childPidl, flags, out STRRET sr);
            var sb = new StringBuilder(1024);
            StrRetToBuf(ref sr, childPidl, sb, 1024);
            if (sr.uType == 0 && sr.pOleStr != IntPtr.Zero) CoTaskMemFree(sr.pOleStr);
            return sb.ToString();
        }

        // ===================== 动词：执行 =====================

        /// <summary>
        /// 对某个 shell 项执行规范动词（runas / taskbarpin / uninstall / opencontaining…）。
        /// parsing 可以是文件路径、shell:AppsFolder\AUMID 或 shell:::{CLSID}\子项。
        /// 返回 false 时LastError 有失败原因。
        /// </summary>
        public static bool InvokeItemVerb(string parsing, string verb)
        {
            LastError = null;
            IShellFolder desktop = null;
            IntPtr pidl = IntPtr.Zero;
            try
            {
                SHGetDesktopFolder(out desktop);
                pidl = ParsePidl(desktop, parsing);

                var sei = new SHELLEXECUTEINFO
                {
                    cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                    // SEE_MASK_FLAG_NO_UI：失败静默返回，错误交给调用方兜底 ——
                    // 否则 ShellExecuteEx 自己弹系统错误框，兜底链根本没机会跑
                    fMask = 0x00004000 /*SEE_MASK_UNICODE*/ | 0x00000004 /*SEE_MASK_IDLIST*/
                          | 0x0000000C /*SEE_MASK_INVOKEIDLIST*/
                          | 0x00000400 /*SEE_MASK_FLAG_NO_UI*/,
                    lpVerb = verb,
                    nShow = SW_SHOWNORMAL,
                    lpIDList = pidl
                };

                if (!ShellExecuteEx(ref sei))
                {
                    int err = Marshal.GetLastWin32Error();
                    // SE_ERR_ 系列塞在 hInstApp 里（<= 32），普通 Win32 错误在 GetLastError
                    int inst = sei.hInstApp != IntPtr.Zero ? sei.hInstApp.ToInt32() : 0;
                    LastError = inst is > 0 and <= 32
                        ? $"SE_ERR_{inst}"
                        : $"Win32Error {err}";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message.Trim();
                return false;
            }
            finally
            {
                if (pidl != IntPtr.Zero) ILFree(pidl);
                if (desktop != null) Marshal.ReleaseComObject(desktop);
            }
        }

        // ===================== 动词：枚举（诊断用） =====================

        /// <summary>
        /// 枚举某个 shell 项右键菜单的全部动词。
        /// 返回 (规范动词, 菜单文字)；规范动词读不到时为空串。
        /// </summary>
        public static List<(string Verb, string Text)> GetVerbs(string parsing)
        {
            LastError = null;
            var result = new List<(string, string)>();
            IContextMenu pcm = null;
            IntPtr hmenu = IntPtr.Zero;
            object itemObj = null;
            try
            {
                // SHCreateItemFromParsingName → BindToHandler(BHID_SFUIObject)：
                // 直调 IShellFolder.GetUIObjectOf 在本机实测访问违例（AV），
                // 改走 shell 自己的封装层，行为与资源管理器右键一致
                Guid iidItem = IID_IShellItem;
                SHCreateItemFromParsingName(parsing, IntPtr.Zero, ref iidItem, out itemObj);
                var item = (IShellItem)itemObj;

                Guid bhid = BHID_SFUIObject;
                Guid iidCm = IID_IContextMenu;
                item.BindToHandler(IntPtr.Zero, ref bhid, ref iidCm, out object cmObj);
                pcm = (IContextMenu)cmObj;

                hmenu = CreatePopupMenu();
                int hr = pcm.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0x00000040 /*CMF_ITEMMENU*/);
                if (hr < 0)
                {
                    LastError = "QueryContextMenu hr=" + hr;
                    return result;
                }
                int count = hr & 0xFFFF;

                for (uint id = 1; id < (uint)(1 + count); id++)
                {
                    string verb = null, text = null;
                    IntPtr buf = Marshal.AllocHGlobal(1024);
                    try
                    {
                        if (pcm.GetCommandString(id, GCS_VERBW, IntPtr.Zero, buf, 512) == 0)
                            verb = Marshal.PtrToStringUni(buf);
                        if (pcm.GetCommandString(id, GCS_STRINGW, IntPtr.Zero, buf, 512) == 0)
                            text = Marshal.PtrToStringUni(buf);
                    }
                    catch { /* 个别项不支持 GetCommandString，跳过 */ }
                    finally { Marshal.FreeHGlobal(buf); }

                    result.Add((verb ?? "", text ?? ""));
                }
                return result;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message.Trim();
                return result;
            }
            finally
            {
                if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
                if (pcm != null) Marshal.ReleaseComObject(pcm);
                if (itemObj != null) Marshal.ReleaseComObject(itemObj);
            }
        }

        // ===================== 文件夹枚举（诊断 pin 文件夹用） =====================

        /// <summary>枚举一个 shell 文件夹的直属子项，返回 (显示名, 相对解析名)。</summary>
        public static List<(string Display, string Parsing)> EnumerateFolder(string folderParsing)
        {
            LastError = null;
            var result = new List<(string, string)>();
            IShellFolder desktop = null, folder = null;
            IEnumIDList penum = null;
            IntPtr pidl = IntPtr.Zero;
            try
            {
                SHGetDesktopFolder(out desktop);
                pidl = ParsePidl(desktop, folderParsing);

                Guid iidFolder = IID_IShellFolder;
                desktop.BindToObject(pidl, IntPtr.Zero, ref iidFolder, out object folderObj);
                folder = (IShellFolder)folderObj;

                folder.EnumObjects(IntPtr.Zero,
                    SHCONTF_FOLDERS | SHCONTF_NONFOLDERS | SHCONTF_INCLUDEHIDDEN, out penum);

                while (true)
                {
                    var items = new IntPtr[1];
                    uint fetched = 0;
                    if (penum.Next(1, items, out fetched) != 0 || fetched == 0) break;

                    try
                    {
                        string display = DisplayName(folder, items[0], SHGDN_NORMAL);
                        string rel = DisplayName(folder, items[0], SHGDN_INFOLDER | SHGDN_FORPARSING);
                        result.Add((display, rel));
                    }
                    finally { CoTaskMemFree(items[0]); }
                }
                return result;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message.Trim();
                return result;
            }
            finally
            {
                if (penum != null) Marshal.ReleaseComObject(penum);
                if (folder != null) Marshal.ReleaseComObject(folder);
                if (pidl != IntPtr.Zero) ILFree(pidl);
                if (desktop != null) Marshal.ReleaseComObject(desktop);
            }
        }

        // ===================== 快捷方式：AUMID 反查（开始菜单扫 .lnk） =====================

        private static readonly Dictionary<string, (string Lnk, string Target)> _shortcutCache =
            new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 拿一个 Win32 应用的 AUMID 去开始菜单找它注册时用的 .lnk。
        /// 返回 (快捷方式路径, 快捷方式指向的 exe)；找不到为 (null, null)。
        /// 菜单每次打开都可能调用，结果按 AUMID 缓存 —— 开始菜单总共就百来个 .lnk。
        /// </summary>
        public static (string LnkPath, string TargetPath) FindShortcutByAumid(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return (null, null);
            lock (_shortcutCache)
            {
                if (_shortcutCache.TryGetValue(aumid, out var hit)) return hit;
            }

            var hitResult = (LnkPath: (string)null, TargetPath: (string)null);
            var roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            };

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) continue;
                foreach (var lnk in System.IO.Directory.EnumerateFiles(root, "*.lnk",
                    System.IO.SearchOption.AllDirectories))
                {
                    string id = ReadShortcutAumid(lnk);
                    if (string.IsNullOrEmpty(id) ||
                        !string.Equals(id, aumid, StringComparison.OrdinalIgnoreCase)) continue;

                    hitResult = (lnk, ReadShortcutTarget(lnk));
                    lock (_shortcutCache) _shortcutCache[aumid] = hitResult;
                    return hitResult;
                }
            }

            lock (_shortcutCache) _shortcutCache[aumid] = hitResult;
            return hitResult;
        }

        /// <summary>读 .lnk 的 System.AppUserModel.ID 属性（快捷方式拿到的固定资格证）。</summary>
        private static string ReadShortcutAumid(string lnkPath)
        {
            object linkObj = null;
            try
            {
                Guid clsid = CLSID_ShellLink, iid = IID_IShellLinkW;
                CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out linkObj);

                var persist = (System.Runtime.InteropServices.ComTypes.IPersistFile)linkObj;
                persist.Load(lnkPath, 0x00000020 /*STGM_READ*/);

                var store = (IPropertyStore)linkObj;
                var key = PKEY_AppUserModel_ID;
                store.GetValue(ref key, out PROPVARIANT pv);
                try
                {
                    return pv.vt == 31 /*VT_LPWSTR*/ && pv.pwszVal != IntPtr.Zero
                        ? Marshal.PtrToStringUni(pv.pwszVal)
                        : null;
                }
                finally
                {
                    if (pv.vt == 31 && pv.pwszVal != IntPtr.Zero) CoTaskMemFree(pv.pwszVal);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (linkObj != null) Marshal.ReleaseComObject(linkObj);
            }
        }

        private static string ReadShortcutTarget(string lnkPath)
        {
            object linkObj = null;
            try
            {
                Guid clsid = CLSID_ShellLink, iid = IID_IShellLinkW;
                CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out linkObj);

                var persist = (System.Runtime.InteropServices.ComTypes.IPersistFile)linkObj;
                persist.Load(lnkPath, 0x00000020);

                var link = (IShellLinkW)linkObj;
                var sb = new StringBuilder(260);
                link.GetPath(sb, 260, out _, 0 /*SLGP_SHORTPATH? 0 = SLGP_UNCPRIORITY*/);
                return sb.Length > 0 ? sb.ToString() : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (linkObj != null) Marshal.ReleaseComObject(linkObj);
            }
        }

        // ===================== 打开文件位置 =====================

        /// <summary>在资源管理器里定位到某个文件（选中高亮）。</summary>
        public static void RevealInExplorer(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;
            // /select 的参数要带引号，直接当参数传给 explorer，不走 ShellExecuteEx 的动词链
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + filePath + "\"",
                UseShellExecute = true
            });
        }

        /// <summary>打开一个文件夹。</summary>
        public static void OpenFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + dir + "\"",
                UseShellExecute = true
            });
        }

        // ===================== 包安装目录（UWP / 桌面桥） =====================

        /// <summary>
        /// 按包族名取安装目录（C:\Program Files\WindowsApps\...）。
        /// AUMID 里 '!' 前面就是包族名，形如 Microsoft.WindowsNotepad_8wekyb3d8bbwe。
        /// </summary>
        public static string GetPackageInstallPath(string packageFamilyName)
        {
            try
            {
                uint count = 0, length = 0;
                int rc = GetPackagesByFamilyName(packageFamilyName, ref count, null, ref length, null);
                if (rc != 122 /*ERROR_INSUFFICIENT_BUFFER*/ || length == 0) return null;

                var buffer = new char[length];
                var names = new IntPtr[count];
                rc = GetPackagesByFamilyName(packageFamilyName, ref count, names, ref length, buffer);
                if (rc != 0 || count == 0) return null;

                // 名字是一串以 \0 结尾的串拼在 buffer 开头
                string fullName = Marshal.PtrToStringUni(names[0]);
                if (string.IsNullOrEmpty(fullName)) return null;

                uint pathLen = 0;
                rc = GetPackagePathByFullName(fullName, ref pathLen, null);
                if (rc != 122 || pathLen == 0) return null;

                var path = new char[pathLen];
                rc = GetPackagePathByFullName(fullName, ref pathLen, path);
                if (rc != 0) return null;

                string dir = new string(path, 0, (int)pathLen).TrimEnd('\0');
                return dir.Length > 0 ? dir : null;
            }
            catch
            {
                return null;
            }
        }

        // ===================== 卸载：注册表反查 =====================

        /// <summary>
        /// 按 exe 路径在卸载表里找它所属软件的卸载命令。
        /// 匹配优先级：DisplayIcon 完全一致 > InstallLocation 是 exe 的上级目录。
        /// </summary>
        public static (string DisplayName, string UninstallString) FindUninstallEntry(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return (null, null);

            try
            {
                exePath = System.IO.Path.GetFullPath(exePath);
            }
            catch { return (null, null); }

            var roots = new[]
            {
                Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall")
            };

            (string, string) strong = (null, null);
            (string, string) weak = (null, null);

            foreach (var root in roots)
            {
                if (root == null) continue;
                using (root)
                {
                    foreach (var subName in root.GetSubKeyNames())
                    {
                        using var key = root.OpenSubKey(subName);
                        if (key == null) continue;

                        string uninstall = key.GetValue("UninstallString") as string;
                        if (string.IsNullOrWhiteSpace(uninstall)) continue;

                        string display = key.GetValue("DisplayName") as string ?? subName;

                        // DisplayIcon 形如 "C:\...\app.exe,0" 或带引号
                        string icon = (key.GetValue("DisplayIcon") as string ?? "")
                            .Trim('"');
                        int comma = icon.LastIndexOf(',');
                        if (comma > 1) icon = icon.Substring(0, comma);

                        if (string.Equals(icon, exePath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (strong.Item1 == null) strong = (display, uninstall);
                        }
                        else
                        {
                            string loc = (key.GetValue("InstallLocation") as string ?? "").Trim('"');
                            if (loc.Length > 1 &&
                                exePath.StartsWith(loc, StringComparison.OrdinalIgnoreCase))
                            {
                                if (weak.Item1 == null) weak = (display, uninstall);
                            }
                        }
                    }
                }
            }

            return strong.Item1 != null ? strong : weak;
        }
    }
}
