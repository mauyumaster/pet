// --fstest：验「全屏隐退」的判定 —— **点一下桌面不该让桌宠隐退**。
//
// 为什么这条必须离线可验：
//   现象只在「用户点了一下桌面」的那一刻出现。那一刻前台窗口是 shell 自己的 `Progman`
//   （Win10 壁纸层是 `WorkerW`），它的矩形与显示器**逐边相等** —— 旧判据只量了
//   「铺满屏幕」，于是把它当成游戏，桌宠当场淡出。
//
//   真正的「沉浸式全屏」有**两个**必要条件：① 铺满屏幕 ② **盖住 shell**。
//   旧判据只有 ①，所以它在**现象没发生**时也成立（任何铺满屏幕的东西都算全屏）——
//   这正是本仓那条「判据选错 ⇒ 没有资格变红」的形状。
//
//   `GetForegroundWindow` 那一半没法在无人时复现（谁也没法让前台恰好是桌面），
//   但「铺满 ＋ 是 shell 该不该算全屏」是纯算术 ⇒ 抽成 `Native.ClassifyFullscreen`，
//   喂合成输入逐条断言。真数据那半由 `--fsprobe` 补（拿真实桌面窗口验判据本身）。
//
// 负对照：`--fstest --no-shellguard` —— 关掉 shell 排除后 desktopIsNotFullscreen **必须变红**
//   （它会退回旧行为：Progman 被判成全屏）。跑不出红 = 那条判据在测空气。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzhuPet
{
    internal static class FsTest
    {
        public static int Run(Cli o)
        {
            bool negative = o.NoShellGuard;
            Native.ShellGuardOff = negative;

            var checks = new List<object>();
            bool ok = true;

            void Check(string name, bool pass, string detail)
            {
                if (!pass) ok = false;
                checks.Add(new Dictionary<string, object>
                {
                    ["name"] = name, ["ok"] = pass, ["detail"] = detail ?? "",
                });
            }

            Native.RECT mon = Rect(0, 0, 1920, 1080);      // 一台普通 1080p 显示器的整屏
            Native.RECT full = Rect(0, 0, 1920, 1080);     // 铺满
            Native.RECT maximized = Rect(0, 0, 1920, 1040); // 最大化（让出任务栏 40px）

            // ================= 负对照：只跑那一条 =================
            if (negative)
            {
                bool v = Native.ClassifyFullscreen("Progman", full, mon);
                Check("negControl_shellGuard", v,
                    "关掉 shell 排除后，「Progman ＋ 铺满屏幕」被判成 " + (v ? "全屏（旧行为复现 ✅）" : "非全屏")
                    + "（必须为全屏；仍是非全屏说明 desktopIsNotFullscreen 不是靠这张表通过的 —— 它没资格失败）");
                return Report(ok, checks, negative, "（负对照模式：只验「关掉 shell 排除会不会变红」）");
            }

            // ================= A. bug 的复现：shell 窗口铺满 ≠ 全屏 =================
            bool desktop = Native.ClassifyFullscreen("Progman", full, mon);
            Check("desktopIsNotFullscreen", !desktop,
                "桌面窗口 Progman ＋ 铺满屏幕 → " + (desktop ? "全屏（❌ 就是用户踩到的 bug：点桌面桌宠隐退）" : "非全屏 ✅"));

            bool worker = Native.ClassifyFullscreen("WorkerW", full, mon);
            Check("workerwIsNotFullscreen", !worker,
                "壁纸层 WorkerW ＋ 铺满屏幕 → " + (worker ? "全屏 ❌" : "非全屏 ✅") + "（动态壁纸也挂在这个类上）");

            bool tray = Native.ClassifyFullscreen("Shell_TrayWnd", full, mon);
            Check("taskbarIsNotFullscreen", !tray,
                "任务栏 Shell_TrayWnd ＋ 铺满屏幕 → " + (tray ? "全屏 ❌" : "非全屏 ✅"));

            bool sechint = Native.ClassifyFullscreen("shelldll_defview", full, mon);
            Check("classNameCaseInsensitive", !sechint,
                "类名大小写不敏感（喂小写 sheldll_defview）→ " + (sechint ? "全屏 ❌" : "非全屏 ✅")
                + "（Windows 类名比较本来就不分大小写，探针/手抄都可能给小写）");

            // ================= B. 正对照：真全屏**不能**被一起干掉 =================
            bool game = Native.ClassifyFullscreen("UnityWndClass", full, mon);
            Check("gameIsStillFullscreen", game,
                "游戏窗口 UnityWndClass ＋ 铺满屏幕 → " + (game ? "全屏 ✅" : "非全屏 ❌（排除 shell 把手滑连带排除了真应用）"));

            bool mpv = Native.ClassifyFullscreen("mpv", full, mon);
            Check("playerIsStillFullscreen", mpv,
                "播放器 mpv ＋ 铺满屏幕 → " + (mpv ? "全屏 ✅" : "非全屏 ❌"));

            // ================= C. 第一关（铺满）必须还在 =================
            bool maxwin = Native.ClassifyFullscreen("Chrome_WidgetWin_1", maximized, mon);
            Check("maximizedIsNotFullscreen", !maxwin,
                "最大化窗口（1040 高，让出任务栏）→ " + (maxwin ? "全屏 ❌（第一关被拆了）" : "非全屏 ✅"));

            bool off5 = Native.ClassifyFullscreen("Foo", Rect(0, 0, 1920, 1075), mon);
            Check("offsetBeyondToleranceRejected", !off5,
                "差 5 px（超出 TOL=3）→ " + (off5 ? "全屏 ❌（容差被放大了）" : "非全屏 ✅"));

            bool off2 = Native.ClassifyFullscreen("Foo", Rect(0, 0, 1920, 1078), mon);
            Check("toleranceStillAlive", off2,
                "差 2 px（在 TOL=3 内）→ " + (off2 ? "全屏 ✅" : "非全屏 ❌（容差被误删，无边框全屏会漏判）"));

            // 多显示器：判定必须对**该窗口所在显示器**，不是主显示器
            Native.RECT mon2 = Rect(1920, 0, 3840, 1080);
            bool second = Native.ClassifyFullscreen("UnityWndClass", Rect(1920, 0, 3840, 1080), mon2);
            Check("perMonitorRect", second,
                "副显示器上的全屏（矩形与副屏逐边相等）→ " + (second ? "全屏 ✅" : "非全屏 ❌"));

            return Report(ok, checks, negative, null);
        }

        /// <summary>
        /// --fsprobe：**真读**系统里的 shell 窗口，用真数据验判据本身。
        /// ⚠ 它不模拟「用户点桌面」（那会抢焦点、最小化所有窗口），但它证明的是更硬的一件事：
        ///   **真实的桌面窗口确实铺满屏幕**，于是旧判据对它的判定必然是「全屏」——
        ///   用户踩到的现象由此从「据说」变成「算术上必然」。
        /// </summary>
        public static int ProbeRun(Cli o)
        {
            var checks = new List<object>();
            bool ok = true;
            void Check(string name, bool pass, string detail)
            {
                if (!pass) ok = false;
                checks.Add(new Dictionary<string, object>
                {
                    ["name"] = name, ["ok"] = pass, ["detail"] = detail ?? "",
                });
            }

            var rows = new List<object>();

            void Look(string tag, string cls)
            {
                IntPtr h = Native.FindWindow(cls, null);
                if (h == IntPtr.Zero)
                {
                    // ⚠ 找不到**不许静默跳过** —— 缺失路径必须留可读原因（本仓旧坑：
                    //   「文件没找到」被静默渲染成「少一行」）。
                    rows.Add(new Dictionary<string, object> { ["tag"] = tag, ["class"] = cls, ["found"] = false });
                    return;
                }
                Native.RECT r;
                Native.GetWindowRect(h, out r);
                IntPtr mon = Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST);
                var mi = new Native.MONITORINFO();
                mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO));
                Native.GetMonitorInfo(mon, ref mi);

                bool vNew = Native.IsFullscreenWindow(h);
                Native.ShellGuardOff = true;
                bool vOld = Native.IsFullscreenWindow(h);
                Native.ShellGuardOff = false;

                bool rectFills = Native.ClassifyFullscreen("__nonsense__", r, mi.rcMonitor);

                rows.Add(new Dictionary<string, object>
                {
                    ["tag"] = tag,
                    ["class"] = Native.ClassOf(h),
                    ["found"] = true,
                    ["rect"] = Str(r),
                    ["monitor"] = Str(mi.rcMonitor),
                    ["rectFillsMonitor"] = rectFills,
                    ["verdictNew"] = vNew,
                    ["verdictOld"] = vOld,
                });

                if (tag == "desktop")
                {
                    Check("probe.desktopRectFillsMonitor", rectFills,
                        "真实桌面窗口的矩形 " + Str(r) + " 与显示器 " + Str(mi.rcMonitor)
                        + " 逐边相等？" + (rectFills ? "是 ✅（这就是旧判据必然误判的前提）" : "否 —— 前提不成立，本机桌宠可能本来就不会因此隐退"));
                    Check("probe.shellGuardFlipsDesktopVerdict", !vNew && vOld,
                        "同一个真实桌面窗口：新判据=" + (vNew ? "全屏" : "非全屏") + "，旧判据=" + (vOld ? "全屏" : "非全屏")
                        + "（期望 非全屏/全屏 —— 这条直接证明「旧代码 + 真实桌面 = 必然隐退」）");
                }
            }

            Look("desktop", "Progman");
            Look("wallpaper", "WorkerW");
            Look("taskbar", "Shell_TrayWnd");

            // 当前前台窗口：探针跑在终端里，读到的多半是终端 —— 作旁证记录，不作判据。
            IntPtr fg = Native.GetForegroundWindow();
            Native.RECT fr; Native.GetWindowRect(fg, out fr);
            rows.Add(new Dictionary<string, object>
            {
                ["tag"] = "foreground",
                ["class"] = Native.ClassOf(fg),
                ["rect"] = Str(fr),
                ["verdictNew"] = Native.ForegroundIsFullscreen(IntPtr.Zero),
            });

            string outPath = Path.Combine(Path.GetTempPath(), "azhu_fsprobe.json");
            try
            {
                File.WriteAllText(outPath, JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["ok"] = ok, ["checks"] = checks, ["rows"] = rows,
                }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                using (var doc = JsonDocument.Parse(File.ReadAllText(outPath, Encoding.UTF8))) { }
            }
            catch (Exception ex) { ok = false; Console.WriteLine("写出的 JSON 自己解析不了：" + ex.Message); }

            Console.WriteLine("fsprobe " + (ok ? "OK" : "FAIL") + " —— 报告 " + outPath);
            foreach (object row in rows)
            {
                var d = (Dictionary<string, object>)row;
                Console.WriteLine("  " + d["tag"] + "：class=" + (d.ContainsKey("class") ? d["class"] : "?")
                    + " rect=" + (d.ContainsKey("rect") ? d["rect"] : "?")
                    + (d.ContainsKey("found") && !(bool)d["found"] ? "  ⚠ 未找到该窗口类"
                       : "  new=" + Yes(d, "verdictNew") + " old=" + Yes(d, "verdictOld")));
            }
            foreach (object c in checks)
            {
                var d = (Dictionary<string, object>)c;
                Console.WriteLine("  " + ((bool)d["ok"] ? "[v] " : "[x] ") + d["name"] + "：" + d["detail"]);
            }
            if (checks.Count == 0) { Console.WriteLine("  [x] 一项都没跑 —— 算 FAIL"); ok = false; }
            return ok ? 0 : 1;
        }

        private static string Yes(Dictionary<string, object> d, string k)
        {
            if (!d.ContainsKey(k)) return "-";
            return (bool)d[k] ? "全屏" : "非全屏";
        }

        private static string Str(Native.RECT r)
        {
            return r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom;
        }

        private static Native.RECT Rect(int l, int t, int r, int b)
        {
            return new Native.RECT { Left = l, Top = t, Right = r, Bottom = b };
        }

        private static int Report(bool ok, List<object> checks, bool negative, string note)
        {
            var report = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["note"] = note,
                ["shellGuardOff"] = Native.ShellGuardOff,
                ["checks"] = checks,
            };
            // ⚠ 负对照与正常判据不许共用一个报告文件：谁后有谁覆盖，而负对照的证据正是那几个判定值。
            string outPath = Path.Combine(Path.GetTempPath(),
                negative ? "azhu_fstest_neg.json" : "azhu_fstest.json");
            try
            {
                File.WriteAllText(outPath,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                using (var doc = JsonDocument.Parse(File.ReadAllText(outPath, Encoding.UTF8))) { }
            }
            catch (Exception ex)
            {
                ok = false;
                Console.WriteLine("写出的 JSON 自己解析不了：" + ex.Message);
            }

            Console.WriteLine("fstest " + (ok ? "OK" : "FAIL") + " —— " + checks.Count + " 项检查，报告 " + outPath
                + (note == null ? "" : " " + note));
            // ⚠ 红项必须报出名字 —— 只给一个 exit 1，分不清是目标判据红了还是别的判据先把它拦住了。
            foreach (object c in checks)
            {
                var d = (Dictionary<string, object>)c;
                if (!(bool)d["ok"]) Console.WriteLine("  [x] " + d["name"] + "：" + d["detail"]);
            }
            if (checks.Count == 0) { Console.WriteLine("  [x] 一项都没跑 —— 算 FAIL"); ok = false; }
            return ok ? 0 : 1;
        }
    }
}
