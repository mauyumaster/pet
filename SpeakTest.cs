// --speaktest：离线验**表达环的接线** —— 喂一串合成窗口切换，她到底会不会吐出一句话。
//
// ⚠⚠ 为什么这条判据非有不可：
//   感知／决策／记忆三环曾经**全都写完了、判据也全绿**，但运行时一个调用点都没有
//   （它们只出现在 --watchtest 里）—— 于是桌宠一个字都不会说，而所有检查都是绿的。
//   「逻辑对」和「接上了」是两件事。这个文件专门测**后者**：
//   假时钟 + 假窗口 + 假说话人，唯一真实的东西是**接线**。
//
// 负对照：`--speaktest --no-wire` —— 换成哑说话人，wiredSpeaks 那条**必须变红**。
//   跑不出红 ⇒ 那条判据在测空气（它可能因为别的判据先红了而一次都没真正跑过）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AzhuPet
{
    internal static class SpeakTest
    {
        public static int Run(Cli o)
        {
            bool negative = o.NoWire;

            var checks = new List<object>();
            bool ok = true;

            void Check(string name, bool pass, string detail)
            {
                if (!pass) ok = false;
                checks.Add(new Dictionary<string, object> { ["name"] = name, ["ok"] = pass, ["detail"] = detail ?? "" });
            }

            // ⚠ 自检绝不污染真记忆：否则每跑一次判据，她的记忆里就多几条假记录。
            Memory.OverridePath = Path.Combine(Path.GetTempPath(),
                negative ? "azhu_speaktest_memory_neg.jsonl" : "azhu_speaktest_memory.jsonl");

            // 合成序列：A 待 12 秒 → 切 B（待够）→ 切 C。step=3 ⇒ 两次观察分别在 t=12s、t=24s。
            var seq = new[] { "A", "A", "A", "A", "B", "B", "B", "B", "C" };

            // ================= 负对照：只跑那一条 =================
            if (negative)
            {
                var nr = RunScene("neg", seq, new SilentSpeaker(), cooldown: 0, cap: 99);
                Check("negControl_wire", nr.Brain.Spoken == 0,
                    "哑说话人下开口 " + nr.Brain.Spoken + " 次（必须为 0；仍是 >0 说明 wiredSpeaks 不是靠说话人通过的"
                    + " —— 那条判据没资格失败）");
                Check("negControl_wireObserved", nr.Brain.Observations == 2,
                    "同一串采样仍然产生了 " + nr.Brain.Observations + " 个观察（期望 2）——"
                    + " 用来证明「没说话」不是因为**根本没观察到**（那会让上一条假通过）");
                // ⚠ 第二条负对照：把「异步说话人被当场拒绝」的**旧行为**复现出来，
                //   证明 speakNowAcceptsAsync 那条判据**有资格变红**（不是恒真的装饰）。
                var na = RunScene("negasync", new[] { "A", "A", "A" }, new SlowSpeaker(30), cooldown: 0, cap: 99);
                na.Brain.RefuseAsyncSpeakNow = true;
                var nv = na.Brain.SpeakNow();
                Check("negControl_refuseAsyncSpeakNow",
                    nv != null && !nv.Speak && (nv.Why ?? "").IndexOf("异步", StringComparison.Ordinal) >= 0,
                    "复现旧行为后 SpeakNow 确实拒绝了异步说话人：" + (nv == null ? "(空)" : nv.Why)
                    + " ⇒ speakNowAcceptsAsync 有资格变红");

                return Report(ok, checks, negative, "（负对照模式：只验「换成哑说话人会不会变红」）", "speaktest");
            }

            // ================= 负对照：--speak-foreground（只跑那一条）=================
            // ⚠⚠ 只跑目标那一条，并且**把红的项点名打出来** ——「exit 1 ≠ 我那条判据红了」：
            //   判据串在一起跑时，别的判据会先把它拦成红，目标判据一次都没跑（本仓踩过）。
            if (o.SpeakForeground)
            {
                var no = RunScene("negmanual", new[] { "pet", "pet" }, new StubSpeaker(), cooldown: 0, cap: 99);
                no.Brain.Target = () => new Fg { Proc = "msedge", Title = "哔哩哔哩", At = DateTime.Now };
                no.Brain.SpeakNowUsesForeground = true;     // ← 把旧行为接回来
                var nv = no.Brain.SpeakNow();
                var oo = no.Obs.Count > 0 ? no.Obs[no.Obs.Count - 1] : null;
                // ⚠ 负对照不许「退化通过」：必须**真的送出过一次观察**（否则它什么都没证明），
                //   而且送出的那个对象必须是**错的**（不是 msedge）。
                bool delivered = oo != null;
                Check("negControl_speakNowUsesReadTarget", delivered && oo.Process != "msedge",
                    "接回旧行为后：出口回调 " + no.Out.Count + " 次、观察 " + no.Obs.Count
                    + " 个，最后一个 proc=" + (oo == null ? "(没有)" : (oo.Process ?? "(空 —— 前台是她自己，回退也没得退)"))
                    + "，裁决=" + (nv == null ? "(异步在途)" : (nv.Speak ? "开口「" + nv.Text + "」" : "不说：" + nv.Why))
                    + " ⇒ speakNowUsesReadTarget 有资格变红"
                    + (delivered ? "" : "　⚠ 一次都没送出去：这条负对照是**退化通过**，什么都没证明 ❌"));
                return Report(ok, checks, false,
                    "（负对照模式：只验「接回旧行为后 speakNowUsesReadTarget 会不会变红」）",
                    "speaktest_neg_foreground");
            }

            // ================= A. 接线（本文件存在的理由）=================
            var a = RunScene("a", seq, new StubSpeaker(), cooldown: 0, cap: 99);
            Check("wiredSpeaks", a.Brain.Spoken == 2,
                "喂 2 次「值得她知道」的切换 → 她开口 " + a.Brain.Spoken + " 次（期望 2）。"
                + "⚠ 这条红 ＝ 三环没接上主循环（此前正是如此：判据全绿而她一言不发）");
            Check("wiredObservationCount", a.Brain.Observations == 2,
                "产生观察 " + a.Brain.Observations + " 个（期望 2）");
            Check("wiredTextNonEmpty", a.AllText().Length > 0,
                "她真的说了字：" + (a.AllText().Length > 0 ? "「" + a.First() + "」" : "（一个字都没有）"));

            // ================= B. 闸门是否也接上了 =================
            var b1 = RunScene("b1", seq, new StubSpeaker(), cooldown: 45, cap: 99);
            Check("gateCooldownWired", b1.Brain.Spoken == 1 && b1.Brain.Suppressed == 1,
                "冷却 45 秒、两次观察间隔 12 秒 → 开口 " + b1.Brain.Spoken + " 次、被拦 "
                + b1.Brain.Suppressed + " 次（期望 1／1）");
            Check("gateCooldownWhyReadable", b1.AnyWhy("冷却"),
                "被拦的那次留下了可读原因：" + b1.LastWhy());

            var b2 = RunScene("b2", seq, new StubSpeaker(), cooldown: 0, cap: 1);
            Check("gateDailyCapWired", b2.Brain.Spoken == 1 && b2.Brain.Suppressed == 1,
                "日限额 1、共 2 次观察 → 开口 " + b2.Brain.Spoken + " 次、被拦 " + b2.Brain.Suppressed + " 次（期望 1／1）");
            Check("gateDailyCapWhyReadable", b2.AnyWhy("日限额"), "被拦原因：" + b2.LastWhy());

            // ================= C. 出口回调 + 记忆 =================
            // ⚠ 上面几个场景各自用**独立**的记忆文件；否则 a 写完 b1 又写 b2，
            //   最后「a 写了几行」会读成 6（首跑实况：断言期望 2、实得 6）。
            //   判据之间共用落点，绿也可能只是碰巧 —— 这里显式切回 a 的那一份。
            Memory.OverridePath = MemoryPath("a");
            int expect = a.Brain.Observations;
            Check("outputCallbackFired", a.Out.Count == expect,
                "出口回调被调 " + a.Out.Count + " 次（期望 " + expect + "）——"
                + " ⚠ 说了和被拦都通知，否则「她今天没说话」无法归因");

            int lines = Memory.LineCount();
            // ⚠ 读不到总数就不许报成功 ——「总数变小 ＋ 全绿」是最危险的假通过。
            Check("memoryWiredLines", lines == expect,
                "记忆里写了 " + (lines < 0 ? "读不到（算 FAIL）" : lines.ToString()) + " 行（期望 " + expect + "）");

            int badLines; string rerr;
            var rows = Memory.ReadAll(out badLines, out rerr);
            bool everyWhy = rerr == null && rows.Count > 0;
            if (everyWhy)
                foreach (var r in rows)
                {
                    string w = Str(r, "why");
                    if (string.IsNullOrEmpty(w)) { everyWhy = false; break; }
                }
            Check("memoryAlwaysHasWhy", everyWhy,
                "每条记录都带非空 why（静默跳过必须留可读原因）—— 坏行 " + badLines);

            // ================= D. 气泡占用仲裁 =================
            var arb = new BubbleArbiter();
            bool allow0 = arb.AllowSpeech();
            arb.Note(BubbleArbiter.Status);
            bool allowStatus = arb.AllowSpeech();
            arb.Note(BubbleArbiter.Speech);
            bool allowSpeech = arb.AllowSpeech();
            arb.Clear();
            Check("bubbleArbiter", allow0 && !allowStatus && allowSpeech && arb.Kind == BubbleArbiter.None,
                "初始允许=" + allow0 + "；状态在位时允许台词=" + allowStatus + "（应 false）；台词在位时="
                + allowSpeech + "（应 true）；Clear 后 Kind=" + arb.Kind);

            // ⚠ 不能复用上面跑完的场景去测这一条：那个 WatchLoop 已停在序列末尾，
            //   再 Tick 只会得到「同一应用，不算切换」→ 永远拿不到裁决
            //   （首跑实况就报了「没产生裁决」，而那**不是让路失败，是根本没产生观察**）。
            //   所以新开一个，只跑到第 4 拍，再手动补第 5 拍。
            // ⚠⚠ 2026-09-20 你拍板「气泡流共存」：状态读数和发言不再互斥（旧规则「台词让路」
            //   是单气泡槽的产物，已废）。判据必须**跟着行为走**——现在断言的是台词照说。
            //   本条在行为变更当天红过一次：行为改了判据没改 ⇒ 判据红未必是实现坏，也可能是它在
            //   忠实地守护一条已被废除的规则（先查「这条规则还存在吗」，再动手改实现）。
            var d = RunScene("d", seq, new StubSpeaker(), cooldown: 0, cap: 99, stopAfter: 4);
            d.Brain.Bubble.Note(BubbleArbiter.Status);          // 假装正在显示状态气泡
            var dv = d.Brain.Tick(new DateTime(2026, 9, 19, 9, 0, 0).AddSeconds(12));   // 第 5 拍：切到 B
            Check("speechCoexistsWithStatus", dv != null && dv.Speak,
                "气泡流共存：状态气泡在位时台词**照说**（两者进同一条流，不互相顶替）—— 裁决="
                + (dv == null ? "(没产生裁决)" : dv.Why));

            // ================= E. 台词纪律 =================
            var sp = new StubSpeaker();
            var said = new List<string>();
            foreach (string p in new[] { "chrome", "WINWORD", "Obsidian", "WeirdApp123", "explorer", "WindowsTerminal" })
            {
                var v = sp.SayAsync(new Observation { Process = p, Title = "t", PrevDwell = 12, At = DateTime.Now })
                          .GetAwaiter().GetResult();
                if (v != null && v.Speak) said.Add(v.Text ?? "");
            }
            Check("stubSpeaksAll", said.Count == 6, "stub 对 6 种应用都给了台词（" + said.Count + " 条）");

            var bad = Hits(string.Join("\n", said), PersonaTest.Forbidden);
            Check("stubNoForbidden", bad.Count == 0,
                bad.Count == 0 ? "台词无禁词" : "台词出现禁词：" + string.Join("／", bad));

            // ⚠ 隐私判据：喂给模型的**只能**是应用级信息，绝不能带窗口标题。
            var pObs = new Observation { Process = "WINWORD", Title = "离婚协议书_终稿.docx", PrevDwell = 21, At = DateTime.Now };
            string prompt = LlmSpeaker.BuildPrompt(pObs, null);
            bool noTitle = prompt.IndexOf("离婚协议书", StringComparison.Ordinal) < 0
                        && prompt.IndexOf("终稿", StringComparison.Ordinal) < 0;
            Check("promptHasNoTitle", noTitle && prompt.Length > 0,
                "喂模型的提示不含窗口标题：" + (noTitle ? "「" + prompt + "」" : "⚠ 泄漏了标题 —— " + prompt));

            // ================= E2. 屏幕文字进提示（相关性判据的「接线」那一半） =================
            // ⚠⚠ 这一组的来历（用户实拍反馈）：「读屏解决了，但她的发言还是和屏幕内容无关」。
            //   两个断点，这是**第一个**：`BuildPrompt` 原来只发应用名＋秒数，
            //   OCR 读到的字**从来没进过提示** —— 她压根没看见过，当然说不相关。
            // ⚠ 判据的输入必须自己钉死：把 `ScreenSource` 换成合成源，**绝不读真屏幕**
            //   （否则同一份判据的绿/红会取决于你今天开着哪个窗口）。
            var srcObs = new Observation { Process = "msedge", Title = "离婚协议书_终稿.docx", PrevDwell = 15, At = DateTime.Now };
            const string ScreenSample = "她说这行字是从屏幕上读来的";
            var wasSrc = LlmSpeaker.ScreenSource;
            try
            {
                LlmSpeaker.ScreenSource = () => ScreenSample;
                string withScreen = LlmSpeaker.PromptFor(srcObs);
                // ↓↓ **相关性判据本体**：喂进去的原文必须**逐字**出现在提示里。
                //    她的话若能指认回屏幕内容，前提就是内容原样到了模型手里；
                //    改写／翻译／摘要过的内容，事后都没法指认回屏幕上那一行。
                Check("promptCarriesScreenText",
                    OcrEye.Compact(withScreen).IndexOf(OcrEye.Compact(ScreenSample), StringComparison.Ordinal) >= 0,
                    "屏幕文字原样进了提示：「" + withScreen + "」");
                // 隐私：加了屏幕文字之后，窗口标题**仍然**不许被顺带带出去。
                Check("promptScreenDoesNotSmuggleTitle",
                    withScreen.IndexOf("离婚协议书", StringComparison.Ordinal) < 0
                    && withScreen.IndexOf("终稿", StringComparison.Ordinal) < 0,
                    "上了屏幕文字之后仍未带出窗口标题（两条线各走各的）：「" + withScreen + "」");

                LlmSpeaker.ScreenSource = () => "";
                string noScreen = LlmSpeaker.PromptFor(srcObs);
                Check("promptOmitsScreenSectionWhenEmpty",
                    noScreen.IndexOf("写着", StringComparison.Ordinal) < 0 && noScreen.Length > 0,
                    "没有屏幕文字 → 提示里**不出现**那一段，更不许写一句「没读到」"
                    + "（模型会照着说，正如它照着念秒数）：「" + noScreen + "」");

                LlmSpeaker.ScreenSource = () => { throw new InvalidOperationException("模拟读屏炸了"); };
                string threw = LlmSpeaker.PromptFor(srcObs);
                Check("screenSourceFailureDoesNotMuteHer",
                    threw.Length > 0 && threw.IndexOf("写着", StringComparison.Ordinal) < 0,
                    "读屏抛异常 → 她照样能说话（提示退化成不含屏幕那一版）：「" + threw + "」"
                    + " —— ⚠ 感知坏了不该连表达一起坏");
            }
            finally { LlmSpeaker.ScreenSource = wasSrc; }   // ⚠ 用完必须还原，否则会污染后面的场景

            var longV = LlmSpeaker.Judge(new string('啊', 31), 30);
            Check("llmDropsTooLong", !longV.Speak && (longV.Why ?? "").IndexOf("超", StringComparison.Ordinal) >= 0,
                "31 字（上限 30）→ " + longV.Why);
            var mlV = LlmSpeaker.Judge("第一行\n第二行", 30);
            Check("llmDropsMultiline", !mlV.Speak, "含换行 → " + mlV.Why);
            var okV = LlmSpeaker.Judge("嗯，累了？", 30);
            Check("llmAcceptsShort", okV.Speak, "6 字 → " + (okV.Speak ? "采用" : okV.Why));

            // ================= F. 手动触发 =================
            var f = RunScene("f", seq, new StubSpeaker(), cooldown: 9999, cap: 1);   // 闸门卡死
            f.Brain.Bubble.Note(BubbleArbiter.Speech);
            var fv = f.Brain.SpeakNow();
            Check("speakNowBypassesGate", fv != null && fv.Speak,
                "闸门卡死（冷却 9999 秒、日限额已满）时手动触发仍然开口：" + (fv == null ? "(空)" : (fv.Speak ? "「" + fv.Text + "」" : fv.Why)));

            // ⚠ 这条是 --speakvis **首跑**的真实收获：她说的原话是「……换到pet了。」
            //   —— 前台正是她自己（你点托盘菜单／测试刚把她的窗口弹到前面）。
            //   手动触发那条路径当时没排除 self。这条判据就是那次事故的固化。
            var g0 = RunScene("self", new[] { "pet", "pet", "pet", "pet" }, new StubSpeaker(), cooldown: 0, cap: 99);
            g0.Brain.Bubble.Note(BubbleArbiter.Speech);
            var gv = g0.Brain.SpeakNow();
            string gtxt = gv == null ? "" : (gv.Text ?? "");
            Check("speakNowSkipsSelf",
                gv != null && gv.Speak && gtxt.IndexOf("pet", StringComparison.OrdinalIgnoreCase) < 0,
                "前台是她自己（" + Watcher.SelfName() + "）时手动触发说出：「" + gtxt + "」（不许出现 pet）");

            // ================= F2. 手动触发：看哪个窗口、按哪套说法开口 =================
            // ⚠⚠ 这一组的来历（用户实拍，2026-09-20）：他点了托盘「让她说一句」，她答：
            //   「6秒，够你在里面找到想要的？」—— 屏幕上开着哔哩哔哩，三件事全对不上：
            //     · 观察对象是**任务栏**（explorer）—— 他只是点了一下她的托盘菜单；
            //     · 「6 秒」是上一次自动观察**冻结**下来的旧读数；
            //     · 那一刻**根本没有发生过换窗口**，她却照着「他刚换到 X」的模板说话。
            {
                // ① 说法必须分家：手动那条不许宣称「他刚换了窗口」，也不许带秒数。
                var mObs = new Observation
                {
                    Process = "msedge", Title = "哔哩哔哩", PrevDwell = 6.4, Manual = true, At = DateTime.Now,
                };
                string manual = LlmSpeaker.BuildPrompt(mObs, null);
                Check("manualPromptDropsDwell",
                    manual.IndexOf("秒", StringComparison.Ordinal) < 0
                    && manual.IndexOf("刚换", StringComparison.Ordinal) < 0,
                    "手动触发不提「刚换了窗口」也不带秒数：「" + manual + "」");
                Check("manualPromptNamesCurrentApp",
                    manual.IndexOf(StubSpeaker.Human("msedge"), StringComparison.Ordinal) >= 0,
                    "手动触发说得出现在在用哪个应用：「" + manual + "」");

                // ② 手动那条也要**逐字**带上屏幕文字（相关性判据的另一半）。
                const string ManualScreen = "阿库娅 第二季 第 3 话";
                string mWith = LlmSpeaker.BuildPrompt(mObs, ManualScreen);
                Check("manualPromptCarriesScreenText",
                    OcrEye.Compact(mWith).IndexOf(OcrEye.Compact(ManualScreen), StringComparison.Ordinal) >= 0,
                    "手动触发也把屏幕原文原样送进提示：「" + mWith + "」");

                // ③ 正对照：**自动**观察那条必须保留秒数。
                //   ⚠ 它是这条判据的负对照：若谁把秒数一刀切掉（而不是按 Manual 分家），
                //     这一条会先红 —— 否则 manualPromptDropsDwell 的绿可能只是因为「秒数没了」。
                var aObs = new Observation { Process = "msedge", Title = "t", PrevDwell = 21.4, At = DateTime.Now };
                string auto = LlmSpeaker.BuildPrompt(aObs, null);
                Check("autoPromptKeepsDwell",
                    auto.IndexOf("21", StringComparison.Ordinal) >= 0
                    && auto.IndexOf("刚换到", StringComparison.Ordinal) >= 0,
                    "自动观察仍然带秒数（证明上面那条是**按 Manual 分家**，不是一刀切）：「" + auto + "」");

                // ④ 接线：手动触发的观察对象必须来自 `ReadTarget` 那一口径，而不是「当前前台」。
                //   ⚠ 只验逻辑、不验接线是本仓的老坑（三环全绿而她一言不发），所以这里驱动**真 Brain**，
                //     只把「目标来源」换成合成值（判据的输入必须自己钉死）。
                var hr = RunScene("manualtarget", new[] { "pet", "pet" }, new StubSpeaker(), cooldown: 0, cap: 99);
                hr.Brain.Target = () => new Fg { Proc = "msedge", Title = "哔哩哔哩", At = DateTime.Now };
                hr.Brain.SpeakNow();
                var hObs = hr.Obs.Count > 0 ? hr.Obs[hr.Obs.Count - 1] : null;
                bool targetOk = hObs != null && hObs.Process == "msedge" && hObs.Manual;
                Check("speakNowUsesReadTarget", targetOk,
                    "前台是她自己的菜单时，手动触发仍然说得对（proc="
                    + (hObs == null ? "?" : (hObs.Process ?? "(空)")) + "，Manual="
                    + (hObs == null ? "?" : hObs.Manual.ToString()) + "）"
                    + (targetOk ? "" : "　⚠ 它又变成「按当前前台说话」了"));
                Check("speakNowMarksManual",
                    hObs != null && hObs.Manual && hObs.PrevDwell == 0,
                    "手动触发的观察带 Manual 标记、也不带秒数（PrevDwell="
                    + (hObs == null ? "?" : hObs.PrevDwell.ToString("0.#")) + "）");

                // ⚠⚠ 关键的一条：判据扫的必须是**没注入时的默认值**。
                //   上面 ④ 是注入了合成目标源才通过的 —— 如果生产里 `Target` 忘了接（或接成
                //   「当前前台」），注入的那条照样全绿：**判据在检查副本**（本仓老毛病）。
                //   所以这里新起一个**没动过任何字段**的 Brain，只看它的默认接线。
                var fresh = new Brain(() => Tuple.Create<string, string>("A", "t"), () => DateTime.Now,
                                      new StubSpeaker(), null);
                string tname = fresh.Target == null ? "(null)" : fresh.Target.Method.Name;
                Check("defaultSpeakTargetIsReadTarget",
                    fresh.Target != null && tname.IndexOf("ReadTarget", StringComparison.Ordinal) >= 0,
                    "没注入时手动的目标来源＝" + tname
                    + "（期望 ReadTarget —— 那就是「她该看哪个窗口」的唯一口径）");
                // ⚠ 上一条的**负对照**：证明这个判法真的能区分两种接线，
                //   而不是「无论接什么都绿」（那种绿没有信息量）。
                var alt = new Brain(() => Tuple.Create<string, string>("A", "t"), () => DateTime.Now,
                                    new StubSpeaker(), null);
                alt.Target = Watcher.ProbeFg;
                Check("defaultSpeakTargetJudgeDiscriminates",
                    (alt.Target.Method.Name ?? "").IndexOf("ReadTarget", StringComparison.Ordinal) < 0,
                    "换成「当前前台」（" + alt.Target.Method.Name + "）后同一条判法会给出不同答案"
                    + " ⇒ 它分得清接的是哪一个");

                // ⚠ 负对照：把旧行为接回来，上面那条必须变红 —— 否则它的绿没有信息量。
                //   （`--speak-foreground` 跑的就是这一段，并且会把红的名字打出来。）
                var ho = RunScene("manualold", new[] { "pet", "pet" }, new StubSpeaker(), cooldown: 0, cap: 99);
                ho.Brain.Target = () => new Fg { Proc = "msedge", Title = "哔哩哔哩", At = DateTime.Now };
                ho.Brain.SpeakNowUsesForeground = true;
                ho.Brain.SpeakNow();
                var oObs = ho.Obs.Count > 0 ? ho.Obs[ho.Obs.Count - 1] : null;
                Check("negControl_speakNowUsesReadTarget",
                    oObs != null && oObs.Process != "msedge",
                    "接回旧行为后：观察 " + ho.Obs.Count + " 个，最后一个 proc="
                    + (oObs == null ? "(没有 —— 负对照退化通过，什么都没证明 ❌)" : (oObs.Process ?? "(空)"))
                    + " ⇒ speakNowUsesReadTarget 有资格变红");
            }

            // ================= E3. 吐槽触发的说法（roast 观察）=================
            // ⚠ roast 的前提是「他**没**换窗口」—— prompt 若说「他刚换到 X」就是假陈述
            //   （手动触发那案的镜像：两个触发器共用一套说法，各自都会说假话）。
            {
                var rObs = new Observation
                {
                    Process = "msedge", Title = null, PrevDwell = 320, At = DateTime.Now, Reason = "roast-title",
                };
                string rPrompt = LlmSpeaker.BuildPrompt(rObs, null);
                Check("roastPromptSaysStillUsing",
                    rPrompt.IndexOf("一直在用", StringComparison.Ordinal) >= 0
                    && rPrompt.IndexOf("刚换", StringComparison.Ordinal) < 0
                    && rPrompt.IndexOf("320", StringComparison.Ordinal) >= 0,
                    "roast 说法是「他一直在用X，已经待了 N 秒」：「" + rPrompt + "」");

                const string RoastScreen = "代码跑通了 但是不知道为什么测试全绿";
                string rWith = LlmSpeaker.BuildPrompt(rObs, RoastScreen);
                Check("roastPromptCarriesScreenText",
                    OcrEye.Compact(rWith).IndexOf(OcrEye.Compact(RoastScreen), StringComparison.Ordinal) >= 0,
                    "roast 也把屏幕原文送进提示 —— 吐槽的**素材**就是它（标题只是触发信号）：「" + rWith + "」");

                // 隐私：即便上游把标题塞进了观察（不该发生），prompt 也不许把它带出去。
                var rTitled = new Observation
                {
                    Process = "msedge", Title = "某网页标题", PrevDwell = 320, At = DateTime.Now, Reason = "roast-idle",
                };
                string rTitledPrompt = LlmSpeaker.BuildPrompt(rTitled, null);
                Check("roastPromptNoTitle",
                    rTitledPrompt.IndexOf("某网页标题", StringComparison.Ordinal) < 0,
                    "roast prompt 不含窗口标题（与台词 prompt 同一条隐私判据）");

                // stub（负对照说话人）对 roast 的措辞：不许用「换到X了」那组句式。
                var stub = new StubSpeaker();
                string stubText = stub.SayAsync(rObs).GetAwaiter().GetResult().Text;
                Check("stubRoastUsesRoastLines",
                    stubText.IndexOf("换到", StringComparison.Ordinal) < 0
                    && stubText.IndexOf(StubSpeaker.Human("msedge"), StringComparison.Ordinal) >= 0,
                    "stub 对 roast 说「" + stubText + "」（不许出现「换到」句式）");
            }

            // ================= E4. OpenAI 兼容通道（发布降门槛，2026-09-20）=================
            // 只验纯函数与路由条件 —— 真发 HTTP 不是判据的事（网络是可变外部状态，--llmtest 管端到端）。
            var inner = new List<object>
            {
                new Dictionary<string, object> { ["role"] = "system", ["content"] = "你是阿助。" },
                new Dictionary<string, object> { ["role"] = "user", ["content"] = new List<object>
                    { new Dictionary<string, object> { ["type"] = "text", ["text"] = "今天写什么？" } } },
            };
            var body = TraeChat.BuildOpenAiBody("deepseek-chat", inner);
            var flatMsgs = body["messages"] as List<Dictionary<string, object>>;
            Check("openAiBodyFlattensContent", flatMsgs != null && flatMsgs.Count == 2
                && (string)flatMsgs[1]["content"] == "今天写什么？",
                "content 数组形态 [{type,text}] 必须拍平成字符串 —— 不少兼容端点（DeepSeek/ollama）不收数组");
            Check("openAiBodyCarriesModel", (string)body["model"] == "deepseek-chat"
                && body.ContainsKey("stream") && (bool)body["stream"] == false,
                "body 带 model 与 stream=false（非流式，取 choices[0].message.content）");
            Check("openAiRouteNeedsBothBaseAndKey",
                !TraeChat.ShouldUseOpenAi(null)
                && !TraeChat.ShouldUseOpenAi(Tuple.Create("", "sk-x", "m"))
                && !TraeChat.ShouldUseOpenAi(Tuple.Create("https://x", "", "m"))
                && TraeChat.ShouldUseOpenAi(Tuple.Create("https://x", "sk-x", "m")),
                "路由条件四象限：base 与 key **都有**才走兼容端点，否则一律回落 Trae（原有行为一字不动）");

            // ================= G. 手动触发必须支持**异步**说话人 =================
            // ⚠ 真模型（LlmSpeaker）必然是异步的。若 SpeakNow 只会读 task.IsCompleted，
            //   那么在模型模式下点托盘「让她说一句」会**静默无效** —— 没有任何提示，
            //   现象只是「她没说话」，与「坏了」无法区分。（实测 --llmtest 时才挖出来的坑。）
            // ⚠ 序列故意全用同一个应用：去重后 Tick **不产生观察**，于是这里开口只可能来自
            //   SpeakNow 那一下 —— 否则判据会分不清是谁说的（同「同一读数混进两类对象」）。
            {
                var gs = RunScene("async", new[] { "A", "A", "A" }, new SlowSpeaker(150), cooldown: 0, cap: 99);
                var av = gs.Brain.SpeakNow();
                bool refused = av != null && !av.Speak && (av.Why ?? "").IndexOf("异步", StringComparison.Ordinal) >= 0;
                Check("speakNowAcceptsAsync", !refused,
                    refused ? "⚠ 手动触发把异步说话人当成「不支持」：" + av.Why
                            : "没有当场拒绝异步说话人（返回 " + (av == null ? "null＝在途" : "同步裁决") + "）");

                var sw2 = Stopwatch.StartNew();
                while (sw2.ElapsedMilliseconds < 1500 && gs.Brain.Spoken == 0) System.Threading.Thread.Sleep(25);
                Check("speakNowAsyncDelivers", gs.Brain.Spoken == 1,
                    "等 1.5 秒后开口 " + gs.Brain.Spoken + " 次（期望 1）—— 异步结果必须真的送到出口");
            }

            // ================= H. 落点 =================
            string realPath = Path.Combine(Memory.Dir(), "memory.jsonl");
            bool outside = realPath.IndexOf("40 Projects", StringComparison.OrdinalIgnoreCase) < 0
                        && realPath.IndexOf("坚果云", StringComparison.Ordinal) < 0;
            Check("memoryOutsideSyncDir", outside, "真落点 " + realPath);

            return Report(ok, checks, negative, null, "speaktest");
        }

        // ---------------- 场景驱动 ----------------

        /// <summary>用合成采样序列驱动**完整的接线**（Brain，不是裸的 WatchLoop）。
        /// ⚠ 每个场景一份**独立**的记忆文件 —— 否则前一个场景写的行会被算进后一个场景的期望值。</summary>
        private static Scene RunScene(string tag, string[] seq, ISpeaker speaker, double cooldown, int cap,
                                      int stopAfter = -1)
        {
            Memory.OverridePath = MemoryPath(tag);
            try { File.Delete(Memory.OverridePath); } catch { }

            var feeder = new Feeder(seq);
            var s = new Scene();
            var t0 = new DateTime(2026, 9, 19, 9, 0, 0);
            const double step = 3;

            s.Brain = new Brain(
                feeder.Next,
                () => t0,
                speaker,
                (v, obs, at) => { s.Out.Add(v); s.Obs.Add(obs); });

            s.Brain.Gate.Cooldown = cooldown;
            s.Brain.Gate.DailyCap = cap;
            // ⚠⚠ 手动触发的「她该看哪个窗口」**也要自己钉死**：`Brain.Target` 的默认值是
            //   `Watcher.ReadTarget()`（真读你的桌面）。判据沿用外部可变状态的话，
            //   它的绿/红就取决于你今天开着哪个窗口（本仓明令禁止）。
            //   给 null ＝「没有目标」，于是走回退那条路 —— 旧判据期望的正是那条路。
            s.Brain.Target = () => null;

            int n = stopAfter >= 0 ? Math.Min(stopAfter, seq.Length) : seq.Length;
            for (int i = 0; i < n; i++)
            {
                feeder.I = i;
                s.Brain.Tick(t0.AddSeconds(step * i));
            }
            feeder.I = seq.Length;   // SpeakNow 用：停在最后一个应用上
            return s;
        }

        private static string MemoryPath(string tag)
        {
            return Path.Combine(Path.GetTempPath(), "azhu_speaktest_memory_" + tag + ".jsonl");
        }

        /// <summary>永远有值可返回的合成前台源 —— 越界就复用最后一个（SpeakNow 会在序列末尾被调用）。</summary>
        private sealed class Feeder
        {
            private readonly string[] _seq;
            public int I;
            public Feeder(string[] seq) { _seq = seq; }
            public Tuple<string, string> Next()
            {
                if (_seq == null || _seq.Length == 0) return Tuple.Create<string, string>("A", "标题");
                string p = I >= 0 && I < _seq.Length ? _seq[I] : _seq[_seq.Length - 1];
                return Tuple.Create(p, "标题" + I);
            }
        }

        /// <summary>
        /// 异步说话人：真模型（LlmSpeaker）**必然**长这样 —— 这里用固定延迟模拟，不联网、不花钱。
        /// 专门喂给「手动触发在异步说话人下会不会静默失效」那条判据。
        /// </summary>
        private sealed class SlowSpeaker : ISpeaker
        {
            private readonly int _ms;
            public SlowSpeaker(int ms) { _ms = ms; }
            public string Name { get { return "slow"; } }

            public Task<Verdict> SayAsync(Observation obs)
            {
                return Task.Run(async () =>
                {
                    await Task.Delay(_ms).ConfigureAwait(false);
                    return new Verdict { Speak = true, Text = "我在这儿。", Why = "异步（判据用）" };
                });
            }
        }

        private sealed class Scene
        {
            public Brain Brain;
            public readonly List<Verdict> Out = new List<Verdict>();
            public readonly List<Observation> Obs = new List<Observation>();

            public string AllText()
            {
                var sb = new StringBuilder();
                foreach (var v in Out) if (v != null && v.Speak && v.Text != null) sb.Append(v.Text);
                return sb.ToString();
            }
            public string First()
            {
                foreach (var v in Out) if (v != null && v.Speak && !string.IsNullOrEmpty(v.Text)) return v.Text;
                return "";
            }
            public bool AnyWhy(string needle)
            {
                foreach (var v in Out)
                    if (v != null && v.Why != null && v.Why.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
                return false;
            }
            public string LastWhy()
            {
                for (int i = Out.Count - 1; i >= 0; i--)
                    if (Out[i] != null && Out[i].Why != null) return Out[i].Why;
                return "(没有裁决)";
            }
        }

        // ---------------- 工具 ----------------

        private static List<string> Hits(string s, string[] words)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (string w in words)
                if (s.IndexOf(w, StringComparison.Ordinal) >= 0) list.Add(w);
            return list;
        }

        private static string Str(Dictionary<string, JsonElement> row, string key)
        {
            JsonElement e;
            if (row == null || !row.TryGetValue(key, out e)) return null;
            return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }

        /// <summary>
        /// --llmtest：**真调**模型若干次，把「喂进去的 prompt」与「她真说的话」打出来。
        ///
        /// ⚠ 为什么非有不可：`--speaktest` 验的是 `Judge`（纯函数：超长丢弃／多行丢弃／空内容），
        ///   而 `LlmSpeaker` 的**网络那半段**从来没被跑过 ——「能编译」不等于「能说话」。
        ///   这与「--watchtest 16/16 全绿、桌宠却一个字都不说」是同一个坑：判据绿 ≠ 接上了。
        ///
        /// ⚠ 分工：它只回答「她会**怎么**说」，**不**回答「时机对不对」—— 那是 --speaktest／stub 的活。
        ///   混了你就分不清「她今天说得无聊」是话的问题还是时机的问题。
        /// </summary>
        public static int LlmRun(Cli o)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            int times = o.Times > 0 ? o.Times : 3;
            // 轮着问 stub 映射表里那几个 —— 也是真跑时最常出现的几个。
            string[] apps = { "chrome", "Code", "Obsidian", "PotPlayerMini64" };

            var speaker = new LlmSpeaker();
            // ⚠⚠ 无头自检**不许去读真屏幕**：那会让绿/红取决于你此刻开着哪个窗口
            //   （本仓明令：判据的输入不许沿用外部可变状态）。
            //   要验「带上屏幕上的字她怎么说话」，用 `--screen-text <文本>` 灌一段**合成**文字。
            LlmSpeaker.ScreenSource = string.IsNullOrEmpty(o.ScreenText)
                ? (Func<string>)(() => null)
                : (() => o.ScreenText);
            var checks = new List<object>();
            bool ok = true;

            void Check(string name, bool pass, string detail)
            {
                if (!pass) ok = false;
                checks.Add(new Dictionary<string, object> { ["name"] = name, ["ok"] = pass, ["detail"] = detail ?? "" });
            }

            Console.WriteLine("llmtest —— 真调 " + times + " 次；台词上限 " + speaker.MaxChars
                + " 字；通道 TraeChat(llm_utils_chat, 明文兼容分支)"
                + (o.SpeakManual ? "；**手动触发那套提示**（不提秒数、不说「刚换了窗口」）" : ""));
            Console.WriteLine("⚠ 只发进程名与屏幕上的字，**绝不发窗口标题**（P0 视野纪律：A 档才准出本机）");
            // 判据自己报出用的是哪个模型 —— 否则「换模型」的对照实验无法归因。
            Console.WriteLine("本轮模型 id = " + TraeChat.ResolveModel(o.LlmModel)
                + (string.IsNullOrEmpty(TraeChat.ModelOverride) ? "（默认）" : "（--model 覆盖）"));

            var rows = new List<object>();
            int responded = 0, spoke = 0, overlong = 0, multiline = 0, leakedCount = 0;
            int notCarried = 0, related = 0;

            for (int i = 0; i < times; i++)
            {
                string app = apps[i % apps.Length];
                var obs = new Observation
                {
                    Process = app,
                    // 真跑时这里会有真实标题；但 BuildPrompt **不该**碰它 —— 下面那条判据测的就是这件事。
                    Title = "离婚协议书_终稿.docx",
                    PrevDwell = 60 + 37 * i,
                    // ⚠ 加了 `--speak-manual` 就切到**手动触发**那一套提示：
                    //   同一个应用、同一个秒数，但那条**不该**提「他刚换了窗口」与秒数。
                    //   它只为人工对照存在 —— 用真模型眼看一下两条说法差在哪。
                    Manual = o.SpeakManual,
                    At = DateTime.Now,
                };
                // ⚠ 走 `PromptFor`（＝ SayAsync 走的同一条路），不是直接调 BuildPrompt：
                //   这样「屏幕文字真的被取来并放进了提示」这段接线也顺带被覆盖。
                string prompt = LlmSpeaker.PromptFor(obs);
                bool leaked = prompt.IndexOf("离婚协议书", StringComparison.Ordinal) >= 0
                           || prompt.IndexOf("终稿", StringComparison.Ordinal) >= 0;
                if (leaked) leakedCount++;

                // ⚠ 屏幕文字有没有**真的**被带进这条提示。给了 `--screen-text` 才检查 —— 没给它就没有答案。
                if (!string.IsNullOrEmpty(o.ScreenText)
                    && OcrEye.Compact(prompt).IndexOf(OcrEye.Compact(o.ScreenText), StringComparison.Ordinal) < 0)
                    notCarried++;

                Verdict v;
                var sw = Stopwatch.StartNew();
                try
                {
                    // ⚠ 这里 `.GetAwaiter().GetResult()` 是**安全**的：本入口是无头控制台进程，
                    //   没有 WPF 渲染线程可卡。（运行时主循环里那么写会让桌宠当场僵住 —— 见 Brain.cs。）
                    v = speaker.SayAsync(obs).GetAwaiter().GetResult();
                }
                catch (Exception ex) { v = new Verdict { Speak = false, Why = "异常：" + ex.Message }; }
                sw.Stop();

                if (v != null && (v.Speak || !string.IsNullOrEmpty(v.Text))) responded++;
                if (v != null && v.Speak) spoke++;
                else if (v != null && (v.Why ?? "").IndexOf("超 ", StringComparison.Ordinal) >= 0) overlong++;
                else if (v != null && (v.Why ?? "").IndexOf("多行", StringComparison.Ordinal) >= 0) multiline++;
                // 相关性：她的台词里有没有出现屏幕文字中的某个 ≥2 字片段。
                // ⚠ 这只是**报告项**，不参与 ok 判定 —— 真模型的输出不确定，拿它当硬判据必然假红，
                //   而假红会让人开始无视判据。接线那一半（原文有没有进提示）由 notCarried 硬验。
                bool rel = v != null && v.Speak && RelatedToScreen(v.Text, o.ScreenText);
                if (rel) related++;

                Console.WriteLine();
                Console.WriteLine("[" + (i + 1) + "/" + times + "] 喂给她：" + prompt + (leaked ? "   ⚠ 标题泄漏！" : ""));
                Console.WriteLine("        " + sw.ElapsedMilliseconds + " ms → "
                    + (v != null && v.Speak
                        ? "她说：「" + v.Text + "」（" + v.Text.Length + " 字）"
                          + (string.IsNullOrEmpty(o.ScreenText) ? ""
                             : rel ? "  ← 台词用上了屏幕上的字 ✅" : "  ← 台词与屏幕文字无关（模型的选择，不是接线坏了）")
                        : "（没说：" + (v == null ? "?" : v.Why) + "）"
                          + (v != null && !string.IsNullOrEmpty(v.Text) ? "   模型原文=「" + Inline(v.Text) + "」" : "")));

                rows.Add(new Dictionary<string, object>
                {
                    ["app"] = app,
                    ["prompt"] = prompt,
                    ["titleLeaked"] = leaked,
                    ["speak"] = v != null && v.Speak,
                    ["text"] = v == null ? null : v.Text,
                    ["chars"] = v == null || v.Text == null ? 0 : v.Text.Length,
                    ["why"] = v == null ? "?" : v.Why,
                    ["ms"] = sw.ElapsedMilliseconds,
                    ["screenRelated"] = rel,        // 报告项，不参与 ok
                });
            }

            // ---- 判据 ----
            // ⚠ 这两条测的是**能力存在性**，不是风格问题：
            //   llmResponded 红 ⇒ 通道没通（那就别去调台词，先修通道）。
            //   llmPromptHasNoTitle 红 ⇒ 隐私边界破了（P0 只准 A 档应用级出本机）。
            Check("llmResponded", responded > 0,
                responded > 0 ? ("拿到回复 " + responded + "/" + times + " 次")
                              : "⚠ 一次都没拿到 —— 通道没通；先看凭据与网络，别动台词");
            Check("llmPromptHasNoTitle", leakedCount == 0,
                leakedCount == 0 ? "喂模型的提示不含窗口标题（拿真标题试的）" : "⚠ 泄漏 " + leakedCount + " 次");
            // ⚠ 相关性判据的**可判定那一半**：给了 --screen-text 时，那段文字必须逐字出现在提示里。
            //   （「她有没有用上」是模型的选择，只作为报告项 related —— 拿它当判据必然假红。）
            Check("llmPromptCarriesScreenText", notCarried == 0,
                string.IsNullOrEmpty(o.ScreenText)
                    ? "（没给 --screen-text ⇒ 这一条没有输入，按通过计；要验它请加 --screen-text \"...\"）"
                    : notCarried == 0 ? "给了 " + times + " 次屏幕文字，每次都逐字进了提示"
                                      : "⚠ 有 " + notCarried + " 次屏幕文字**没**进提示（她当然说得不相关）");

            Console.WriteLine();
            Console.WriteLine("llmtest 汇总 —— 拿到回复 " + responded + "/" + times + "，采用 " + spoke
                + "，超长丢弃 " + overlong + "，多行丢弃 " + multiline
                + (string.IsNullOrEmpty(o.ScreenText) ? "" : "；台词用上屏幕文字 " + related + "/" + spoke + "（报告项）"));

            return Report(ok, checks, false,
                "（真调模型：验 LlmSpeaker 的**网络那半段**；只答「她怎么说」，不答「何时说」）", "llmtest");
        }

        /// <summary>把多行原文压成一行便于打印（超长时截断）—— 只用于显示，不参与裁决。</summary>
        private static string Inline(string s)
        {
            s = (s ?? "").Replace("\r", "⏎").Replace("\n", "⏎");
            return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
        }

        /// <summary>
        /// 台词里有没有出现屏幕文字中的某个 **≥2 字连续片段**（比对前先归一化 ——
        /// 中文 OCR 输出带字间空格，朴素 IndexOf 会把「用上了」判成「没关系」，本仓踩过）。
        /// ⚠⚠ 这只是**报告项，不是判据**：真模型的输出不确定，拿它当硬判据必然产生假红，
        ///   而假红会让人开始无视判据（判据的输入必须自己钉死）。
        ///   接线那一半（原文到底有没有送到模型手里）由 `promptCarriesScreenText` /
        ///   `llmPromptCarriesScreenText` 硬验 —— 那是确定性的，能真的红。
        /// </summary>
        private static bool RelatedToScreen(string speech, string screen)
        {
            if (string.IsNullOrEmpty(screen)) return false;
            string a = OcrEye.Compact(speech), b = OcrEye.Compact(screen);
            if (a.Length < 2 || b.Length < 2) return false;
            for (int i = 0; i + 2 <= b.Length; i++)
                if (a.IndexOf(b.Substring(i, 2), StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// --speakprobe：**真读前台**跑一段，把「她观察到了什么、说了什么」打出来。
        /// ⚠ 这一环（今天你屏幕上真的切了个应用）无法离线造 —— 判据只能验接线，验不了环境。
        /// </summary>
        public static int ProbeRun(Cli o)
        {
            double seconds = o.Seconds > 0 ? o.Seconds : 30;
            var brain = new Brain(Watcher.Probe, () => DateTime.Now, new StubSpeaker(),
                (v, obs, at) => Console.WriteLine("  " + at.ToString("HH:mm:ss")
                    + (v != null && v.Speak ? "  她说：「" + v.Text + "」" : "  （没说：" + (v == null ? "?" : v.Why) + "）")),
                s => Console.WriteLine("  " + s));
            brain.Gate.Cooldown = 10;   // 探针模式放宽，好在半分钟里看到效果

            Console.WriteLine("speakprobe —— 真读前台 " + seconds + " 秒；观察阈值 " + Watcher.MinDwell
                + " 秒、冷却 " + brain.Gate.Cooldown + " 秒。换个应用并待够时间才会触发。");
            int n = 0;
            for (double t = 0; t < seconds; t += 1.0)
            {
                brain.Tick();
                n++;
                System.Threading.Thread.Sleep(1000);
            }
            Console.WriteLine("speakprobe 结束 —— 采样 " + n + " 次，观察 " + brain.Observations
                + " 次，开口 " + brain.Spoken + " 次，被拦 " + brain.Suppressed + " 次");
            // ⚠ 采样 0 次是坏的；观察 0 次只是「你没换应用」，不能算失败。
            return n > 0 ? 0 : 1;
        }

        /// <summary>
        /// --speakvis：**真窗口**端到端验「PetWindow → 气泡」这一段。
        /// ⚠ 为什么非有不可：--speaktest 用的是注入的 probe／出口回调，它证明的是 **Brain 内部**通了；
        ///   「她的话到底有没有变成屏幕上那个气泡」发生在 PetWindow 里，离线永远测不到。
        ///   这一段此前正是唯一没被任何判据覆盖的接缝 —— 也就是最容易「写得对、但没接上」的地方。
        /// 判据：气泡可见 ＋ 用途是 speech（不是 status）＋ 文本非空。
        /// 负对照 --speakvis --no-wire：换成哑说话人后气泡**不该**冒出来。
        /// </summary>
        public static int VisRun(Cli o)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            string model = Cli.ResolveModel(o.ModelPath);
            if (model == null) { Console.WriteLine("[speakvis] 找不到模型（用 --model 指定）"); return 2; }
            GlbModel gm = Glb.Load(model);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var cfg = PetConfig.Load();
            cfg.SizeIndex = o.SizeIndex;
            cfg.NightDim = false;              // 调光会持续改 Opacity —— 与本次无关，关掉
            cfg.SpeechOn = false;              // ⚠ 关掉自发采样：判据要的是「我点了一下她说了话」，不是环境噪音
            // ⚠⚠ 说话人必须**显式固定**，不能沿用 config.json —— 否则这条判据的绿取决于
            //   你在托盘里勾了什么（外部可变状态）。实测：勾上「台词用模型生成」后它当场变红，
            //   而红的原因不是接缝断了，是**真模型要 2.4 秒、这里只等 700ms**。
            //   默认用 stub（同步、确定、零成本）验接线；要验真模型加 --speak-llm（等待自动拉长）。
            cfg.SpeechLlm = o.SpeakLlm;
            var r = new WpfPetRenderer(gm);
            PetWindow.ForceSilent = o.NoWire;
            var w = new PetWindow(r, cfg) { ShowInTaskbar = false };
            w.SelfTestMode = true;
            w.Show();

            bool visible = false; string text = null, kind = null;
            var tl = new DispatcherTimer(DispatcherPriority.Send);
            tl.Interval = TimeSpan.FromMilliseconds(40);
            var clock = Stopwatch.StartNew();
            int phase = 0;
            double due = 0;

            tl.Tick += (s, e) =>
            {
                try
                {
                    double t = clock.Elapsed.TotalMilliseconds;
                    if (t < due) return;
                    switch (phase)
                    {
                        case 0: w.Pose.Freeze = true; phase = 1; due = t + 900; break;   // 等它摆稳
                        case 1: w.ForceSpeak(); phase = 2; due = t + (o.SpeakLlm ? 9000 : 700); break;   // 让她说话（真模型要几秒）
                        case 2:
                            visible = w.BubbleVisible;
                            text = w.BubbleText;
                            kind = w.BubbleKind;
                            tl.Stop();
                            app.Shutdown();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[speakvis] 测试自身异常：" + ex);
                    tl.Stop();
                    app.Shutdown();
                }
            };
            tl.Start();
            app.Run();

            var checks = new List<object>();
            bool ok = true;
            Action<string, bool, string> Check = (n, pass, d) =>
            {
                if (!pass) ok = false;
                checks.Add(new Dictionary<string, object> { ["name"] = n, ["ok"] = pass, ["detail"] = d ?? "" });
            };

            if (o.NoWire)
            {
                // 负对照：哑说话人 ⇒ 气泡不该出现。若它**照样**出现，说明 speak.bubble_visible 与说话人无关，
                // 那条判据就是在测空气（它会在「说不出话」时也成立）。
                Check("negative_control.silent_no_bubble", !visible,
                    "哑说话人下气泡可见=" + visible + "（必须 false）");
            }
            else
            {
                Check("speak.bubble_visible", visible, "「让她说一句」后气泡可见=" + visible);
                Check("speak.kind_is_speech", kind == "speech",
                    "气泡用途=" + (kind ?? "(null)") + "（必须是 speech；若是 status，说明走的还是查余额那条路）");
                Check("speak.text_nonempty", !string.IsNullOrEmpty(text),
                    "气泡里的字=「" + (text ?? "") + "」");
            }

            Console.WriteLine("---- 她说话端到端（--speakvis" + (o.NoWire ? " --no-wire" : "") + "）----");
            Console.WriteLine("  可见=" + visible + "  用途=" + (kind ?? "(null)") + "  文本=" + (text ?? "(null)"));
            foreach (var c in checks)
            {
                var d = (Dictionary<string, object>)c;
                Console.WriteLine("  " + (bool)d["ok"] + "  " + d["name"] + "：" + d["detail"]);
            }
            return Report(ok, checks, o.NoWire, "（真窗口：验 PetWindow→气泡 那一段接缝）", "speakvis");
        }

        private static int Report(bool ok, List<object> checks, bool negative, string note, string tag)
        {
            var report = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["note"] = note,
                ["checks"] = checks,
            };
            // ⚠ 四份报告（speaktest／speakvis × 正／负）**不许共用文件名**：
            //   谁后有谁覆盖，而负对照唯一的证据就是那几个数字（同 --watchtest 的老坑）。
            string outPath = Path.Combine(Path.GetTempPath(),
                "azhu_" + tag + (negative ? "_neg" : "") + ".json");
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

            Console.WriteLine("speaktest " + (ok ? "OK" : "FAIL") + " —— " + checks.Count + " 项检查，报告 " + outPath
                + (note == null ? "" : " " + note));
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
