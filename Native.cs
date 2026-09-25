// Win32 声明集中在这里。桌宠壳需要的东西几乎全是「问系统」：
// 光标在哪、人多久没操作了、前台窗口有没有全屏、屏幕工作区多大。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AzhuPet
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        // ⚠ MONITORINFO 必须按 ref 传：它虽是 blittable 结构，但**按值传的 [In,Out] 不会把
        //   修改拷回托管的局部变量** —— 那样 mon.rcMonitor/rcWork 会一直是 0，于是
        //   「全屏检测」永远判 false、「工作区」永远是 0,0→0,0（本机实测踩到过）。
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] public static extern int SetWindowLong(IntPtr hWnd, int idx, int val);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        // 主动发消息：用来查询 WM_NCHITTEST 的**真值**。比"移动光标再看 LastHitKind"可靠 ——
        // Windows 会缓存 hit test，窗口静止时在同一窗口内部移动光标可能根本不重新调用它。
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        // 感知层用：前台窗口属于哪个进程、标题是什么。
        // ⚠ GetWindowText 的 EntryPoint 必须显式写 W 并配 CharSet.Unicode —— 这个导入**没有** A/W 自动映射
        //   （它是一个宏，不是两个导出函数），不指定就按 ANSI 调，中文标题会变成乱码。
        [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
        public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        /// <summary>
        /// 所有顶层窗口的句柄（给 `--shellprobe` 用）。
        /// ⚠ 存在的理由：**「谁算外壳」必须能在这台机器上量出来**，而不是靠一张类名表猜 ——
        ///   本轮那个 bug 正是「表里没有这台机器上托盘窗口的真类名」。
        /// </summary>
        public static List<IntPtr> TopLevelWindows()
        {
            var list = new List<IntPtr>();
            try { EnumWindows((h, l) => { list.Add(h); return true; }, IntPtr.Zero); } catch { }
            return list;
        }

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        /// <summary>置顶位现在在不在（**唯一**判据，别去看 `Window.Topmost` 属性）。
        /// 逻辑在 `TopmostGuard` 里，这里只是把窗口句柄接上去。</summary>
        public static bool HasTopmostBit(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            return TopmostGuard.HasTopmostBit(GetWindowLong(hWnd, GWL_EXSTYLE));
        }

        /// <summary>把置顶**真的**拨过去。
        /// ⚠⚠ 不能靠 `Window.Topmost = true` 来救：那位属性是依赖属性，
        ///   它**自认已经是 true** 时值没变 ⇒ 属性回调不跑 ⇒ 一个 `SetWindowPos` 都不会发生。
        ///   于是「位被别人抹掉」之后属性怎么设都回不来 —— 这就是「无法**保持**在最上方」的成因。
        ///   直接 `SetWindowPos` 才绕得开这层缓存。</summary>
        public static bool ReassertTopmost(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            return SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>撤掉置顶（配置里关了、位却还开着时用）。</summary>
        public static bool DropTopmost(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return false;
            return SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public const int WM_NCHITTEST = 0x0084;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;

        /// <summary>距上次键鼠输入过去了多少秒。</summary>
        public static double IdleSeconds()
        {
            var lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref lii)) return 0;
            uint now = unchecked((uint)Environment.TickCount);
            uint d = unchecked(now - lii.dwTime);
            return d / 1000.0;
        }

        /// <summary>
        /// shell（外壳）自己的窗口类名 —— 它们铺满屏幕**不等于**「有人在玩游戏／放片」。
        ///
        /// ⚠ 为什么必须有这张表：桌面窗口天生就铺满整块屏幕。点一下桌面空白处，前台窗口
        ///   就变成 `Progman`（Win10 壁纸层是 `WorkerW`），矩形与显示器逐边相等 ——
        ///   于是「全屏检测」把它当成游戏，桌宠当场淡出。**用户什么都没做错，是判据把
        ///   「铺满屏幕」当成了「沉浸式应用」。**
        ///   （这与本仓那条「判据选错 ⇒ 现象没发生时也成立」同族：全屏的两个必要条件里
        ///     少了一个 —— 铺满 ＋ **盖住 shell**。这里用类名把 shell 自己摘掉。）
        /// ⚠ 2026-09-20 实测（`--shellprobe`）：**这张表远不完备** —— 同一台机器上 explorer 的托盘溢出
        ///   在 **Win11** 叫 `TopLevelWindowForOverflowXamlIsland`，表里没有（表里是 Win10 的名字）；
        ///   量出来「只靠进程名才认得出来」的组合有 30 多条。⇒ 「谁算外壳」的主判据是**进程名**，
        ///   见 `BelongsToShell`；这张表只作兜底（第三方外壳／动态壁纸）。
        /// </summary>
        private static readonly string[] ShellClasses =
        {
            "Progman",                    // 桌面（图标层宿主）
            "WorkerW",                    // 桌面（壁纸层，Win7+；动态壁纸也挂这儿）
            "SHELLDLL_DefView",           // 桌面图标容器
            "Shell_TrayWnd",              // 主任务栏
            "Shell_SecondaryTrayWnd",     // 副显示器任务栏
            "NotifyIconOverflowWindow",   // 托盘溢出面板（**Win10** 的类名）
            "TaskListThumbnailWnd",       // 悬停预览
        };

        /// <summary>仅供负对照：关掉 shell 排除，复现旧的「铺满就算全屏」行为。</summary>
        public static bool ShellGuardOff;

        /// <summary>
        /// **纯查表**：这个类名是不是 shell 自己的窗口。
        /// ⚠ 它**故意不吃 ShellGuardOff** —— 那是「全屏判据」的负对照开关。读屏这一层也要
        ///   排除 shell（否则你点一下桌面，她读到的就是壁纸层），但那两件事不能共用一个开关：
        ///   否则跑 `--fstest --no-shellguard` 会顺手把读屏那一半的行为也改掉（两个判据互相污染）。
        /// </summary>
        public static bool IsShellClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            for (int i = 0; i < ShellClasses.Length; i++)
                if (string.Equals(ShellClasses[i], cls, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool IsShellWindow(string cls)
        {
            if (ShellGuardOff) return false;
            return IsShellClass(cls);
        }

        /// <summary>
        /// shell 自己的**进程** —— 这条是「谁算外壳」的**主判据**。
        /// ⚠⚠ 为什么主判据必须看进程而不是类名（2026-09-20 用户实拍）：
        ///   你点开托盘溢出面板，她开口说「**在用文件夹啊。找到什么了？**」—— `explorer` 被译成「文件夹」。
        ///   而那个窗口**不在** `ShellClasses` 表里：Win10→Win11 换掉的正是**窗口类名**，
        ///   而**进程名几乎不变**（`explorer`／`ShellExperienceHost`／`StartMenuExperienceHost`／
        ///   `SearchHost`／`TextInputHost` 十年没动）。⇒ 枚举类名天然会漏，枚举进程稳得多。
        /// </summary>
        private static readonly string[] ShellProcs =
        {
            "explorer",                  // 桌面／任务栏／托盘／开始菜单／此电脑（Win11 的壳也在这儿）
            "ShellExperienceHost",       // 音量／网络／通知中心等系统 flyout（Win10）
            "StartMenuExperienceHost",   // 开始菜单（Win10/11 独立进程）
            "SearchHost", "SearchApp",   // 搜索（Win11／Win10）
            "TextInputHost",             // 输入法候选／表情面板
            "LockApp",                   // 锁屏
        };

        /// <summary>**例外**：explorer.exe 的**文件夹窗口**是货真价实的应用，不许被一刀切掉。</summary>
        private static readonly string[] FolderClasses =
        {
            "CabinetWClass",   // 文件资源管理器（此电脑／回收站／任意文件夹）
            "ExploreWClass",   // 老式资源管理器窗口（XP 遗留，仍可能出现）
        };

        private static bool IsFolderWindowClass(string cls)
        {
            if (string.IsNullOrEmpty(cls)) return false;
            for (int i = 0; i < FolderClasses.Length; i++)
                if (string.Equals(FolderClasses[i], cls, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// **纯函数**：这个前台窗口是不是「外壳自己的」—— 也就是**不是一个你正在用的应用**。
        ///
        /// ⚠⚠ 它与 `IsShellWindow(cls)`（**全屏判据**用）**不是同一个问题，故意不合并**：
        ///   · 全屏判据问「这是不是一个沉浸式应用」⇒ **宁窄勿宽**（「任务视图」铺满屏幕时让她隐退是好事）；
        ///   · 这一层问「这算不算你在用一个应用」⇒ **宁宽勿窄**（把壳说成「你在用的应用」＝ 说错话，
        ///     代价高于「少说一句」）。⇒ 两个问法留两个函数，各自的偏向写在各自的注释里。
        ///
        /// ⚠ 判据必须带**两个正对照**，否则「一刀切成『没在用应用』」也能全绿：
        ///   ① explorer.exe 的**文件夹窗口**必须仍算应用；② **UWP 应用**（`Windows.UI.Core.CoreWindow`，
        ///   比如计算器／设置）必须仍算应用 —— 它与系统 flyout 共用类名，差别只在**进程**。
        /// </summary>
        public static bool BelongsToShell(string proc, string cls)
        {
            if (IsShellClass(cls)) return true;                       // ② 兜第三方外壳与动态壁纸
            if (string.IsNullOrEmpty(proc)) return false;
            if (IsFolderWindowClass(cls)) return false;               // 例外：文件夹窗口算应用
            for (int i = 0; i < ShellProcs.Length; i++)               // ① 主判据：进程
                if (string.Equals(ShellProcs[i], proc, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>读窗口类名（失败给空串，绝不抛）。</summary>
        public static string ClassOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new System.Text.StringBuilder(256);
                return GetClassName(h, sb, sb.Capacity) > 0 ? sb.ToString() : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// **纯判定**：窗口矩形铺满它所在显示器的整块屏幕，且它不是 shell 自己的窗口。
        /// ⚠ 这条是纯的（只吃矩形与类名），所以能喂合成输入离线逼红 —— 真调
        ///   `GetForegroundWindow` 的那一半没法在无人时复现「前台恰好是桌面」。
        /// </summary>
        public static bool ClassifyFullscreen(string cls, RECT win, RECT mon)
        {
            if (IsShellWindow(cls)) return false;
            const int TOL = 3;
            return Math.Abs(win.Left - mon.Left) <= TOL
                && Math.Abs(win.Top - mon.Top) <= TOL
                && Math.Abs(win.Right - mon.Right) <= TOL
                && Math.Abs(win.Bottom - mon.Bottom) <= TOL;
        }

        /// <summary>前台窗口是否是「沉浸式全屏」（= 有人在玩游戏／放片）。</summary>
        public static bool ForegroundIsFullscreen(IntPtr self)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == self) return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            IntPtr mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(mon, ref mi)) return false;
            return ClassifyFullscreen(ClassOf(fg), r, mi.rcMonitor);
        }

        /// <summary>对**指定**窗口跑同一套全屏判定（探针用：拿真实桌面窗口验判据本身）。</summary>
        public static bool IsFullscreenWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            RECT r;
            if (!GetWindowRect(hwnd, out r)) return false;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(mon, ref mi)) return false;
            return ClassifyFullscreen(ClassOf(hwnd), r, mi.rcMonitor);
        }

        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string title);

        /// <summary>某个窗口所在显示器的工作区（避开任务栏）。比 WorkAreaAt 可靠 —— 不依赖 WindowFromPoint。</summary>
        public static bool WindowWorkArea(IntPtr hwnd, out RECT work)
        {
            work = new RECT();
            if (hwnd == IntPtr.Zero) return false;
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(mon, ref mi)) return false;
            work = mi.rcWork;
            return true;
        }

        /// <summary>某个屏幕点所在的显示器工作区（避开任务栏）。</summary>
        public static RECT WorkAreaAt(int x, int y)
        {
            var pt = new POINT(); pt.X = x; pt.Y = y;
            IntPtr hwnd = WindowFromPoint(pt);
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi)) return mi.rcWork;
            var r = new RECT();
            r.Left = 0; r.Top = 0;
            r.Right = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            r.Bottom = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            return r;
        }

        // ============================ 屏幕内容读取（L4 · 像素级）============================
        // ⚠ 这一层与 A/B 档性质不同：A 档（进程名＋标题）**不出本机**，像素级要**外发**。
        //   所以它默认关，由 `eyeOn` 显式打开 —— 不可逆的性质不能靠口头承诺。

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        // ---- 截一个**指定**窗口（而不是「当前前台」）时需要的三个门 ----
        // ⚠ 为什么需要它们：她自己的窗口（托盘菜单、状态气泡）会**合法地**成为前台，
        //   此时「截当前前台」等于截她自己。所以改为截「上一个真实窗口」，而那个窗口
        //   此时多半在后台、可能已经关掉或最小化 —— 下面三个函数就是用来在截之前问清楚的。
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        /// <summary>窗口是不是最小化了。最小化的窗口 PrintWindow 会画出空白 ⇒ 必须提前拒。</summary>
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);

        /// <summary>把本线程的输入队列挂到别的线程上 —— 只在测试里用，为了能**造出**
        /// 「前台是她自己」这个真实条件（Windows 默认不允许非前台进程抢前台）。</summary>
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

        /// <summary>把窗口内容画进指定 DC。flags=2（PW_RENDERFULLCONTENT）才能拿到
        /// DirectComposition / 硬件加速渲染的窗口（Chrome、Electron）；给 0 会得到黑图。
        /// ⚠ 它的好处是**只画这个窗口** ⇒ 天然不含遮挡物与她自己。</summary>
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
                                                                  IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

        public const uint SRCCOPY = 0x00CC0020;
        public const uint PW_RENDERFULLCONTENT = 2;
    }
}
