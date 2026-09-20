// --watchtest：离线验 P0 的三环 —— 感知（Watcher）／决策（StubSpeaker＋SpeechGate）／记忆（Memory）。
// 不读真窗口、不联网、不读凭据、不污染真记忆。
//
// 为什么感知这一环**必须**离线可验：
//   「她开口的时机对不对」在真实使用里要观察好几天才看得出来，而且无法复现。
//   但「什么算一次切换」是能算的 —— 把状态机抽成纯函数，喂合成采样序列，逐条断言。
//
// 负对照：`--watchtest --no-dedupe` —— 关掉去重后，sameAppNotReported 那条判据**必须变红**。
//   ⚠ 跑不出红 = 那条判据在测空气（它可能在别的判据红了之后才通过，一次都没真正跑过）。
//   所以负对照模式下**只跑那一条**，并且**把命中项的名字打出来**。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzhuPet
{
    internal static class WatchTest
    {
        public static int Run(Cli o)
        {
            bool negative = o.NoDedupe || o.NoAdaptive || o.NoRoast;
            Watcher.Dedupe = !o.NoDedupe;
            Watcher.AdaptiveOn = !o.NoAdaptive;
            WatchLoop.RoastOn = !o.NoRoast;

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

            // ================= 负对照：只跑那一条 =================
            if (o.NoDedupe)
            {
                var n1 = Drive(new[] { "A", "A", "A", "A", "A" });
                Check("negControl_dedupe", n1.Count > 0,
                    "关掉去重后，同一应用连续采样被报了 " + n1.Count + " 次（必须 > 0；仍是 0 说明"
                    + " sameAppNotReported 不是靠去重通过的 —— 那条判据没资格失败）");
                return Report(ok, checks, "neg_dedupe");
            }
            if (o.NoAdaptive)
            {
                // 两步断言（纪律 25）：① 正常档下目标行为真的发生 ② 关掉后它消失。
                Watcher.AdaptiveOn = true;
                var on = DriveBusy(22, 4.5);          // 22 次频繁切换后，B 只待 4.5 秒就切走
                Watcher.AdaptiveOn = false;
                var off = DriveBusy(22, 4.5);
                Check("negControl_adaptive", on.Count > 0 && off.Count == 0,
                    "频繁切换（22 次/半小时）后待 4.5 秒就切走：自适应开=" + on.Count + " 个观察（必须 > 0），"
                    + "关=" + off.Count + " 个（必须 = 0 —— 恒用 8 秒基线时 4.5 秒不够格）");
                return Report(ok, checks, "neg_adaptive");
            }
            if (o.NoRoast)
            {
                // 两步断言：① 接好 LastSpoke 且 RoastOn=true 时，标题变化真的会触发
                //           ② 关掉后同序列 0 条。
                WatchLoop.RoastOn = true;
                var on = DriveRoast(new[] { "A", "A", "A", "A", "A" },
                                    new[] { "一", "一", "一", "一", "新标题" },
                                    new DateTime(2026, 1, 1, 8, 0, 0));
                WatchLoop.RoastOn = false;
                var off = DriveRoast(new[] { "A", "A", "A", "A", "A" },
                                     new[] { "一", "一", "一", "一", "新标题" },
                                     new DateTime(2026, 1, 1, 8, 0, 0));
                int onR = on.ConvertAll(x => x.Reason ?? "").FindAll(r => r.StartsWith("roast")).Count;
                int offR = off.ConvertAll(x => x.Reason ?? "").FindAll(r => r.StartsWith("roast")).Count;
                Check("negControl_roast", onR > 0 && offR == 0,
                    "同一序列：RoastOn=true 时触发 " + onR + " 次（必须 > 0），false 时 " + offR + " 次（必须 = 0）");
                return Report(ok, checks, "neg_roast");
            }

            // ================= A. 感知 =================
            var e1 = Drive(new[] { "A", "A", "A", "A", "A" });
            Check("sameAppNotReported", e1.Count == 0,
                "同一应用连续采样 5 次 → " + e1.Count + " 个观察（期望 0）");

            var e2 = Drive(new[] { "A", "A", "A", "A", "B", "B", "B", "B", "C" });
            bool e2ok = e2.Count == 2 && e2[0].Process == "B" && e2[1].Process == "C";
            Check("switchAfterDwellReported", e2ok,
                "A 待够 → 切 B → 待够 → 切 C：" + e2.Count + " 个观察 ["
                + string.Join(",", e2.ConvertAll(x => x.Process).ToArray()) + "]（期望 B,C）");

            var e3 = Drive(new[] { "A", "B", "C", "D" });
            Check("flipFlopNotReported", e3.Count == 0,
                "每个应用只待 3 秒就切走 → " + e3.Count + " 个观察（期望 0：都是路过）");

            var e4 = Drive(new[] { "A", "A", "A", "A", null, "A" });
            Check("noForegroundNotReported", e4.Count == 0,
                "中途读不到前台（锁屏／切换中）→ " + e4.Count + " 个观察（期望 0）");

            var e5 = Drive(new[] { "A", "A", "A", "A", "pet", "pet", "pet", "pet", "A" });
            Check("selfWindowSkipped", e5.Count == 0,
                "她自己的窗口（pet）待 12 秒再切回 A → " + e5.Count + " 个观察"
                + "（期望 0：点一下她不该被算成一次切换）");

            var e6 = Drive(new[] { "A", "A", "A", "A", "B" });
            bool e6ok = e6.Count == 1 && e6[0].PrevDwell >= 12 && e6[0].Process == "B";
            Check("dwellIsCumulative", e6ok,
                "停留时长按累计算：" + (e6.Count == 1 ? e6[0].PrevDwell.ToString("0.#") + " 秒" : "没报")
                + "（期望 ≥12，而不是一个采样间隔 3 秒）");

            // ⚠⚠ 2026-09-20 实拍：她开口说的是「他刚换到「资源管理器」」—— 而用户只是**点了一下任务栏**，
            //   屏幕上正开着哔哩哔哩。shell 自己的窗口（桌面／任务栏／托盘溢出窗口）**不是一个应用**。
            //   ⚠ 必须带**正对照**：explorer 打开的**文件夹**窗口不是 shell 类，必须照常放行 ——
            //     否则「把 explorer 一刀切掉」也能让这条判据全绿（同族：判据要能区分「拒得对」与「拒得宽」）。
            var shellFg = new Fg { Proc = "explorer", Title = "系统托盘溢出窗口。", Cls = "NotifyIconOverflowWindow", Shell = true };
            var realFg = new Fg { Proc = "explorer", Title = "下载", Cls = "CabinetWClass", Shell = false };
            var sa = Watcher.AsApp(shellFg);
            var ra = Watcher.AsApp(realFg);
            Check("shellWindowIsNotAnApp", sa.Item1 == null && sa.Item2 == null,
                "shell 窗口（类名 " + shellFg.Cls + "）折算成「"
                + (sa.Item1 ?? "此刻没有你在用的应用") + "」（期望：折算成没有）");
            Check("folderWindowIsStillAnApp", ra.Item1 == "explorer",
                "正对照：explorer 的**文件夹**窗口（" + realFg.Cls + "）仍然算应用："
                + (ra.Item1 ?? "⚠ 被一刀切了") + "（期望 explorer）");
            Check("nilReadingIsNotAnApp", Watcher.AsApp(null).Item1 == null,
                "读不到前台（null）仍是 null，不会被伪造成一个应用");

            // ⚠⚠ 「谁算外壳」＝ **进程为主 ＋ 类名兜底**（2026-09-20 实拍「在用文件夹啊。找到什么了？」）。
            //   两个真类名**都是从这台机器上量出来的**（`--shellprobe`，2026-09-20）：
            //   托盘溢出＝`TopLevelWindowForOverflowXamlIsland`、开始菜单宿主＝`XamlExplorerHostIslandWindow_WASDK`。
            //   ⚠ 旧实现的类名表里**一个都没有**（表里写的是 Win10 的 `NotifyIconOverflowWindow`）——
            //   换 Windows 版本换掉的正是**窗口类名**，而**进程名**十年没动。
            //   ⚠ 用两个而不是一个：只用一个的话，谁把那个名字补进表里，这条判据就退化成「查表」，
            //   再也证明不了进程判据还在（判据退化成本身也是一种假通过）。
            Check("shellProcBeatsClassName",
                Native.BelongsToShell("explorer", "TopLevelWindowForOverflowXamlIsland")
                && Native.BelongsToShell("explorer", "XamlExplorerHostIslandWindow_WASDK"),
                "explorer 的两个**表外**窗口（实测类名）仍被判成外壳 ⇒ 进程判据真的在起作用");
            Check("shellFlyoutProcsAreShell",
                Native.BelongsToShell("TextInputHost", "Windows.UI.Core.CoreWindow")
                && Native.BelongsToShell("StartMenuExperienceHost", "")
                && Native.BelongsToShell("SearchHost", "Windows.UI.Core.CoreWindow"),
                "输入法／开始菜单／搜索（只有进程名可认）→ 判成外壳");
            // ⚠ 两条**正对照** —— 少了它们，「把所有东西都判成外壳」也能让上面全绿。
            Check("folderWindowStillAnAppUnderNewRule",
                !Native.BelongsToShell("explorer", "CabinetWClass")
                && !Native.BelongsToShell("explorer", "ExploreWClass"),
                "正对照：explorer 打开的**文件夹**窗口仍是应用（一刀切会把你翻文件也变成「没在用应用」）");
            Check("uwpAppIsNotShell",
                !Native.BelongsToShell("Calculator", "Windows.UI.Core.CoreWindow"),
                "正对照：UWP 应用（计算器）与系统 flyout **类名相同**，只差进程 —— 不能连它一起判成外壳");
            Check("ordinaryAppIsNotShell",
                !Native.BelongsToShell("msedge", "Chrome_WidgetWin_1")
                && !Native.BelongsToShell("pet", "#32770")
                && !Native.BelongsToShell(null, null),
                "普通应用／她自己／读不到 —— 都不算外壳");

            // ================= B. 决策 =================
            var sp = new StubSpeaker();
            var said = new List<string>();
            foreach (string p in new[] { "chrome", "WINWORD", "Obsidian", "WeirdApp123" })
            {
                var v = sp.SayAsync(new Observation { Process = p, Title = "t", PrevDwell = 12, At = DateTime.Now })
                          .GetAwaiter().GetResult();
                if (!v.Speak) { Check("stubSpeaks", false, "stub 对 " + p + " 没开口：" + v.Why); break; }
                said.Add(v.Text ?? "");
            }
            Check("stubSpeaks", said.Count == 4,
                "stub 对 4 种应用都给出了台词（" + said.Count + " 条）");

            bool allShort = true;
            foreach (string s in said) if (s.Trim().Length == 0 || s.Length > 40) allShort = false;
            Check("stubShort", allShort, "台词都非空且 ≤40 字（人格页 §4：说话短，一两句）");

            // ⚠ 这条是风险 8 缺的那一半：禁词表不只拦 persona.md 的**正文**，
            //   也要拦她**真说出口的话**。
            var bad = Hits(string.Join("\n", said), PersonaTest.Forbidden);
            Check("stubNoForbidden", bad.Count == 0,
                bad.Count == 0 ? "4 条台词无禁词" : "台词出现禁词：" + string.Join("／", bad));

            // ================= C. 记忆 =================
            Memory.OverridePath = Path.Combine(Path.GetTempPath(), "azhu_watchtest_memory.jsonl");
            try { File.Delete(Memory.OverridePath); } catch { }

            var obs1 = new Observation { Process = "WINWORD", Title = "第 13 章.docx", PrevDwell = 21.4, At = DateTime.Now };
            var vd = new Verdict { Speak = true, Text = "文档啊。刚才那个弄完了？", Why = "模板台词（stub 负对照）" };
            DateTime now1 = new DateTime(2026, 9, 19, 22, 0, 0);

            string werr = Memory.Append(obs1, vd, sp.Name, now1);
            Check("memoryAppend", werr == null, werr ?? "写入成功");

            int lines = Memory.LineCount();
            // ⚠ 读不到总数就不许报成功 —— 「总数变小 ＋ 全绿」是最危险的假通过。
            Check("memoryLines", lines == 1, "读回行数 " + (lines < 0 ? "失败（算 FAIL）" : lines.ToString()) + "（期望 1）");

            int badLines; string rerr;
            var rows = Memory.ReadAll(out badLines, out rerr);
            bool rowOk = rerr == null && rows.Count == 1 && badLines == 0;
            string saidBack = rowOk ? Str(rows[0], "said") : null;
            Check("memoryReadBack", rowOk && saidBack == vd.Text,
                rowOk ? "读回 said=" + saidBack + "，坏行 " + badLines : ("读回失败：" + rerr)) ;

            // 坏行必须能被**数出来**，不能静默跳过
            try { File.AppendAllText(Memory.OverridePath, "{这不是合法 JSON\n", new UTF8Encoding(false)); } catch { }
            int bad2; Memory.ReadAll(out bad2, out rerr);
            Check("memoryBadLineCounted", bad2 == 1,
                "坏行计数 " + bad2 + "（期望 1：坏行必须可数，否则「解析跳过」会伪装成「她少说了一句」）");

            string realPath = Path.Combine(Memory.Dir(), "memory.jsonl");
            bool outside = realPath.IndexOf("40 Projects", StringComparison.OrdinalIgnoreCase) < 0
                        && realPath.IndexOf("坚果云", StringComparison.Ordinal) < 0;
            Check("memoryOutsideSyncDir", outside, "真落点 " + realPath);

            // ================= D. 闸门 =================
            var g = new SpeechGate { Cooldown = 45, DailyCap = 2 };
            var t0 = new DateTime(2026, 9, 19, 9, 0, 0);
            string why;
            bool a1 = g.Allow(t0, out why); g.Note(t0);
            bool a2 = g.Allow(t0.AddSeconds(10), out why);
            bool a3 = g.Allow(t0.AddSeconds(60), out why); g.Note(t0.AddSeconds(60));
            bool a4 = g.Allow(t0.AddSeconds(120), out why);
            bool a5 = g.Allow(t0.AddSeconds(600), out why);
            Check("gateCooldown", a1 && !a2 && a3,
                "第 1 次放行=" + a1 + "，10 秒后=" + a2 + "（冷却中，应拦），60 秒后=" + a3);
            Check("gateDailyCap", !a4 && !a5,
                "日限额 2 用满后：第 3 次=" + a4 + "，第 4 次=" + a5 + "（都应为 false）");

            // ================= E. 自适应停留阈值（用户拍板：专注不动、频繁降低）=================
            Check("adaptiveCalmUsesBaseline", Watcher.AdaptiveMinDwell(3) == 8.0 && Watcher.AdaptiveMinDwell(6) == 8.0,
                "半小时 3/6 次切换 → " + Watcher.AdaptiveMinDwell(3) + "/" + Watcher.AdaptiveMinDwell(6)
                + " 秒（期望 8/8：专注状态用基线）");
            Check("adaptiveBusyUsesFloor", Watcher.AdaptiveMinDwell(20) == 4.0 && Watcher.AdaptiveMinDwell(40) == 4.0,
                "半小时 20/40 次切换 → " + Watcher.AdaptiveMinDwell(20) + "/" + Watcher.AdaptiveMinDwell(40)
                + " 秒（期望 4/4：一分钟都不到就换一次）");
            Check("adaptiveMidpoint", Watcher.AdaptiveMinDwell(13) == 6.0,
                "半小时 13 次（6–20 的中点）→ " + Watcher.AdaptiveMinDwell(13) + " 秒（期望 6.0：线性下探的中点）");
            bool monotonic = true;
            for (int k = 6; k < 20; k++) if (Watcher.AdaptiveMinDwell(k + 1) > Watcher.AdaptiveMinDwell(k)) monotonic = false;
            Check("adaptiveMonotonic", monotonic,
                "6→20 之间阈值单调不增（跳变会让她的出现频率忽然改档）");
            var busyEvents = DriveBusy(22, 4.5);
            Check("adaptiveActuallyGates", busyEvents.Count > 0,
                "端到端：频繁切换（22 次）后待 4.5 秒切走 → " + busyEvents.Count + " 个观察（期望 ≥1 —— "
                + "8 秒基线永远够不到的短停留，在 busy 档下值得报）");

            // ================= F. 吐槽通道（变化优先＋定时保底）=================
            var roastEvents = DriveRoast(
                new[] { "A", "A", "A", "A", "A", "A", "A", "A", "A", "A", "A" },
                new[] { "一", "一", "一", "一", "新标题", "一", "一", "一", "一", "又一标题", "一" },
                // ⚠ 上次开口必须是「400 秒前」而不是 1 小时前：1 小时 > IdleNudgeSec(600)？不，400 的理由是——
                //   给 1 小时的话，**第 2 拍 idle 保底先触发**（601 > 600），占掉 200 秒冷却，
                //   随后第 5 拍真正的标题变化全被压 ⇒ 判据红的是「实现没实现」，不是「判据想验的事」。
                //   400 秒：>200（不撞发言冷却）且 400+4 < 600（保底不抢跑）⇒ 序列里唯一的触发源就是标题变化。
                new DateTime(2026, 1, 1, 9, 0, 0).AddSeconds(-400));
            int roastTitle = roastEvents.ConvertAll(x => x.Reason ?? "").FindAll(r => r == "roast-title").Count;
            Check("roastTitleChangeFires", roastTitle == 1,
                "同应用内标题变化（第 5 拍）→ " + roastTitle + " 次 roast-title（期望 1）");
            Check("roastCooldownHolds", roastTitle == 1 && roastEvents.Count >= 1
                && roastEvents.ConvertAll(x => x.Reason ?? "").FindAll(r => r.StartsWith("roast")).Count == 1,
                "第 10 拍的第二次标题变化被 200 秒冷却压住（两次变化相距 5 拍 = 5 秒 ≪ 200 秒）");
            Check("roastObservationCarriesNoTitle",
                roastEvents.ConvertAll(x => x.Title).TrueForAll(x => x == null),
                "roast 观察不带窗口标题 —— 标题只是触发信号，进 prompt 的是屏幕文字（隐私判据）");

            var noRoastEvents = DriveRoast(
                new[] { "A", "B", "B", "B", "B" },
                new[] { "一", "固定", "固定", "固定", "固定" },
                new DateTime(2026, 1, 1, 9, 0, 0).AddSeconds(-400));   // 同上：400 秒前，保底不抢跑（理由见上一序列）
            Check("roastNotOnSwitch",
                noRoastEvents.TrueForAll(x => x.Reason == null),
                "换应用的拍子产出的是原有观察（Reason 为空），不重复触发吐槽 —— 一次切换只有一个负责人");

            var spokeEvents = DriveRoast(
                new[] { "A", "A", "A", "A", "A" },
                new[] { "一", "一", "一", "一", "新标题" },
                new DateTime(2026, 1, 1, 9, 0, 0).AddSeconds(-10));   // 她 10 秒前刚说过话
            Check("roastBlockedByRecentSpeak",
                spokeEvents.ConvertAll(x => x.Reason ?? "").TrueForAll(r => !r.StartsWith("roast")),
                "她刚开口 10 秒，同拍标题变化不再触发吐槽（距上次发言 < 200 秒）");

            var idleEvents = DriveRoast(
                new[] { "A", "A", "A" },
                new[] { "同一", "同一", "同一" },
                new DateTime(2026, 1, 1, 9, 0, 0).AddSeconds(-601));  // 她 601 秒没说过话
            bool idleFired = idleEvents.Exists(x => x.Reason == "roast-idle");
            Check("roastIdleNudgeFires", idleFired,
                "标题全程不变、她 601 秒没开口 → 定时保底触发 roast-idle（期望第 2 拍就到点）");

            return Report(ok, checks, null);
        }

        // ---------------- 工具 ----------------

        /// <summary>用合成采样序列驱动状态机。step = 采样间隔（秒）。</summary>
        private static List<Observation> Drive(string[] seq, double step = 3)
        {
            int i = 0;
            var loop = new WatchLoop(() => Tuple.Create(seq[i], "标题" + i));
            var evts = new List<Observation>();
            var t = new DateTime(2026, 1, 1, 9, 0, 0);
            for (i = 0; i < seq.Length; i++)
            {
                Observation o = loop.Sample(t.AddSeconds(step * i));
                if (o != null) evts.Add(o);
            }
            return evts;
        }

        /// <summary>
        /// 频繁切换序列（自适应判据用）：churn 次 A/B 交替（每次间隔 3 秒，都够不着任何阈值），
        /// 然后停在当前应用 `finalDwell` 秒后切到 C。⚠ 最后一拍距上一拍恰好 finalDwell 秒
        /// ⇒ prevDwell 就是 finalDwell —— 判据的输入必须这么精确地钉死。
        /// </summary>
        private static List<Observation> DriveBusy(int churn, double finalDwell)
        {
            int i = 0;
            var t0 = new DateTime(2026, 1, 1, 9, 0, 0);
            var loop = new WatchLoop(() => Tuple.Create(i < churn ? (i % 2 == 0 ? "A" : "B") : "C", "固定"));
            var evts = new List<Observation>();
            for (int k = 0; k < churn; k++)
            {
                i = k;
                Observation o = loop.Sample(t0.AddSeconds(3 * k));
                if (o != null) evts.Add(o);
            }
            i = churn;                                        // 切到 C，距上一拍恰好 finalDwell 秒
            Observation last = loop.Sample(t0.AddSeconds(3 * (churn - 1) + finalDwell));
            if (last != null) evts.Add(last);
            return evts;
        }

        /// <summary>
        /// 吐槽判据用：proc/title 逐拍可钉死，LastSpoke 固定为给定时刻。
        /// ⚠ step=1 秒 —— 标题变化与冷却都是秒级的事，3 秒一步太粗。
        /// </summary>
        private static List<Observation> DriveRoast(string[] procs, string[] titles, DateTime lastSpokeAt, double step = 1)
        {
            int i = 0;
            var loop = new WatchLoop(() => Tuple.Create(procs[i], titles[i]));
            loop.LastSpoke = () => lastSpokeAt;
            var evts = new List<Observation>();
            var t = new DateTime(2026, 1, 1, 9, 0, 0);
            for (i = 0; i < procs.Length; i++)
            {
                Observation o = loop.Sample(t.AddSeconds(step * i));
                if (o != null) evts.Add(o);
            }
            return evts;
        }

        private static List<string> Hits(string s, string[] words)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (string w in words)
                if (s.IndexOf(w, StringComparison.Ordinal) >= 0) list.Add(w);
            return list;
        }

        /// <summary>宽容取值：非字符串一律返回 null（不抛）。</summary>
        private static string Str(Dictionary<string, JsonElement> row, string key)
        {
            JsonElement e;
            if (row == null || !row.TryGetValue(key, out e)) return null;
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }

        /// <summary>
        /// --watchprobe：**真读前台窗口**。
        /// P0 里唯一做不成纯函数的部分（它吃的就是系统 API），所以只能让它在真机上跑一遍、
        /// 把读数打出来给人看。⚠ 「判据只能被真外部系统验 ＝ 没资格失败」——
        /// 这一环的验证方式就是**看着它读对**，而不是等它变红。
        /// ⚠ 2026-09-20 起**两张都打**：原始读数（`ProbeFg`）与折算后的「你在用的应用」
        ///   （`AsApp`）—— 「任务栏被算成一个应用」那个 bug 就藏在这两者之间。
        /// </summary>
        public static int ProbeRun(Cli o)
        {
            int seconds = (int)(o.Seconds > 0 ? o.Seconds : 12);
            Console.WriteLine("watchprobe —— 真读前台，共 " + seconds + " 秒，每 3 秒一次；self=" + Watcher.SelfName());
            int n = 0;
            for (int i = 0; i * 3 < seconds; i++)
            {
                var f = Watcher.ProbeFg();
                var t = Watcher.AsApp(f);
                Console.WriteLine("  t+" + (i * 3).ToString("00") + "s  proc=" + (f == null ? "(读不到)" : (f.Proc ?? "(读不到)"))
                    + "  title=" + (f == null ? "" : (f.Title ?? ""))
                    + "  cls=" + (f == null ? "" : (f.Cls ?? ""))
                    + (f != null && f.Shell ? "  ⚠ shell" : "")
                    + "  → 算作应用：" + (t.Item1 ?? "(没有 —— shell／读不到)"));
                n++;
                System.Threading.Thread.Sleep(3000);
            }
            // ⚠ 读到 0 次要能和「她不想说话」区分开 —— 前者是 Probe 坏了。
            Console.WriteLine("watchprobe 结束 —— 读到 " + n + " 次");
            return n > 0 ? 0 : 1;
        }

        /// <summary>
        /// --shellprobe：把「谁算外壳」**在这台机器上量出来**。
        ///
        /// ⚠⚠ 为什么非要它：`ShellClasses` 是一张**猜出来的**类名表，而 2026-09-20 那个 bug 正是
        ///   「表里没有这台机器上托盘窗口的真类名」（你点开托盘，她开口说「**在用文件夹啊**」）。
        ///   ⇒ 「谁算外壳」必须能在真机上**看一眼**，而不是等她说错话再回头猜（同「先官方文档 → 后本机目录」
        ///   的纪律：**先量，再改**）。
        /// ⚠ 不打印所有顶层窗口（这一台有几百扇，人会淹死）：只打印**壳进程**的窗口 ＋ 当前前台那一扇；
        ///   末尾给出「**靠进程才判出来、类名表里没有**」的那些组合 —— 那是可直接执行的补表清单。
        /// </summary>
        public static int ShellProbeRun(Cli o)
        {
            var all = Native.TopLevelWindows();
            Console.WriteLine("shellprobe —— 顶层窗口共 " + all.Count + " 扇（含隐藏窗口）");
            // ⚠ 枚举到 0 要报红：那说明**枚举本身坏了**，与「这台机器没有壳窗口」是两件事。
            if (all.Count == 0) { Console.WriteLine("⚠ 枚举到 0 扇 → 枚举本身失效（不是「没有壳窗口」）"); return 1; }

            var watch = new List<string>(new[] {
                "explorer", "ShellExperienceHost", "StartMenuExperienceHost",
                "SearchHost", "SearchApp", "TextInputHost", "LockApp", "pet" });
            IntPtr fg = Native.GetForegroundWindow();
            int shown = 0, asShell = 0;
            var missing = new List<string>();     // 靠进程判出来、但类名表里没有的组合

            foreach (IntPtr h in all)
            {
                string proc = ProcOf(h);
                string cls = Native.ClassOf(h);
                bool interesting = h == fg || (proc != null && watch.Contains(proc));
                if (!interesting) continue;

                bool shell = Native.BelongsToShell(proc, cls);
                if (shell) asShell++;
                if (shell && !Native.IsShellClass(cls)) missing.Add(proc + " / " + cls);
                shown++;
                Console.WriteLine("  " + (h == fg ? "[前台] " : "       ")
                    + "proc=" + (proc ?? "(读不到)") + "  cls=" + (cls.Length == 0 ? "(无)" : cls)
                    + "  vis=" + Native.IsWindowVisible(h) + "  算外壳=" + shell
                    + "  title=" + TitleOf(h));
            }

            Console.WriteLine("shellprobe 结束 —— 展示 " + shown + " 扇，判定为外壳 " + asShell + " 扇");
            if (missing.Count > 0)
                Console.WriteLine("⚠ 以下组合**只靠进程名**才判成外壳（类名表里没有）："
                    + string.Join("；", missing.ToArray()));
            return shown > 0 ? 0 : 1;
        }

        private static string ProcOf(IntPtr h)
        {
            try
            {
                int pid; Native.GetWindowThreadProcessId(h, out pid);
                if (pid <= 0) return null;
                using (var p = System.Diagnostics.Process.GetProcessById(pid)) return p.ProcessName;
            }
            catch { return null; }
        }

        private static string TitleOf(IntPtr h)
        {
            try
            {
                var sb = new StringBuilder(512);
                Native.GetWindowText(h, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        private static int Report(bool ok, List<object> checks, string tag)
        {
            var report = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["minDwell"] = Watcher.MinDwell,
                ["dedupe"] = Watcher.Dedupe,
                ["adaptive"] = Watcher.AdaptiveOn,
                ["roast"] = WatchLoop.RoastOn,
                ["checks"] = checks,
            };
            // ⚠ 负对照与正常判据、以及**不同的负对照之间**都不许共用一个报告文件：
            //   谁后有谁覆盖，而「关掉这个开关到底报了几次」这个数字正是负对照唯一的证据。
            string outPath = Path.Combine(Path.GetTempPath(),
                "azhu_watchtest" + (tag == null ? "" : "_" + tag) + ".json");
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

            Console.WriteLine("watchtest " + (ok ? "OK" : "FAIL") + " —— " + checks.Count + " 项检查，报告 " + outPath);
            // ⚠ 红项**必须报出名字** —— 只给一个 exit 1，你分不清是目标判据红了还是别的判据先把它拦住了。
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
