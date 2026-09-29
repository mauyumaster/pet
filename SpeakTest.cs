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

            // ============ B'. 说话频率：配置下发 ＋ 两条不变式（2026-09-29 用户要求可调）============
            // ⚠ 为什么值得单独一组：用户拍板「十分钟一句」＋「频率在设置里可调」。
            //   这三个数字之间有**不变式**（保底 ≥ 冷却、吐槽冷却 > 冷却），填反了**不会报错**，
            //   只表现为她刷屏、或把 memory.jsonl 的 veto 记录灌爆 —— 只能靠判据盯着。
            // ⚠ 全局静态位（Roast.*）改完要还原：同一次运行里别的判据也读它们。
            double oldRoastCd = Roast.CooldownSec, oldRoastIdle = Roast.IdleNudgeSec;
            try
            {
                // ① 出厂默认就是十分钟一句 —— 用户那句要求直接锁进判据，防止哪天被顺手改回去
                var dflt = new PetConfig();
                Check("speechFreqDefaultIsTenMinutes",
                    dflt.SpeechCooldownMin == 10 && SpeechFreq.CooldownSec(dflt) == 600
                    && Math.Abs(new SpeechGate().Cooldown - 600) < 0.001,
                    "默认配置 " + dflt.SpeechCooldownMin + " 分钟 → " + SpeechFreq.CooldownSec(dflt)
                    + " 秒；SpeechGate 出厂值 " + new SpeechGate().Cooldown + " 秒（期望 600／600）");

                // ② 配置里的分钟数真能走到闸门上（接线）
                var freqCfg = new PetConfig { SpeechCooldownMin = 3, RoastIdleMin = 7, SpeechDailyCap = 33 };
                var freqGate = new SpeechGate();
                SpeechFreq.Apply(freqCfg, freqGate);
                Check("speechFreqConfigReachesGate",
                    Math.Abs(freqGate.Cooldown - 180) < 0.001 && freqGate.DailyCap == 33,
                    "填 3 分钟／33 句 → 闸门 " + freqGate.Cooldown + " 秒、" + freqGate.DailyCap
                    + " 句（期望 180／33）");

                // ③ 不变式一：保底不得短于冷却（填反了 ⇒ 每拍产出一条被拦的观察 ⇒ 记录灌爆）
                var freqBad = new PetConfig { SpeechCooldownMin = 30, RoastIdleMin = 2 };
                SpeechFreq.Apply(freqBad, freqGate);
                Check("speechFreqIdleNotShorterThanCooldown",
                    Math.Abs(Roast.IdleNudgeSec - 1800) < 0.001,
                    "冷却 30 分钟、保底填 2 分钟 → 保底被抬到 " + Roast.IdleNudgeSec + " 秒（期望 1800）");

                // ④ 不变式二：吐槽冷却恒 > 开口冷却
                Check("roastCooldownAboveGate",
                    Roast.CooldownSec > freqGate.Cooldown
                    && Math.Abs((Roast.CooldownSec - freqGate.Cooldown) - SpeechFreq.RoastLeadSec) < 0.001,
                    "开口 " + freqGate.Cooldown + " 秒 → 吐槽 " + Roast.CooldownSec + " 秒（期望 +"
                    + SpeechFreq.RoastLeadSec + "）");

                // ⑤ 下限：别低到骚扰档（面板最小值 1 分钟，这里防的是配置文件被手改成 0）
                var freqZero = new PetConfig { SpeechCooldownMin = 0 };
                Check("speechFreqFloorHolds",
                    SpeechFreq.CooldownSec(freqZero) >= SpeechFreq.MinCooldownSec,
                    "填 0 分钟 → " + SpeechFreq.CooldownSec(freqZero) + " 秒（下限 "
                    + SpeechFreq.MinCooldownSec + "）");
            }
            finally { Roast.CooldownSec = oldRoastCd; Roast.IdleNudgeSec = oldRoastIdle; }

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
            // ⚠ 2026-09-29 起多行**不再丢弃**（用户拍板「多行输出也可以」）。
            //   原来那条 llmDropsMultiline 断言的是旧行为，现在反过来断言「换行被接受」；
            //   负对照 `--no-multiline` 会让它变红（旧行为：含换行即整句丢弃）。
            var mlV = LlmSpeaker.Judge("第一行\n第二行", 30);
            Check("llmAcceptsMultiline", mlV.Speak,
                "含换行 → " + (mlV.Speak ? "采用（多行不再丢弃）" : "⚠ 被丢弃：" + mlV.Why));
            // ⚠⚠ **配套判据（非有不可）**：字数口径改成只数**可见字符**。否则「放开多行」会被
            //   「超 30 字」原地拦下（两行必然多出换行符），改动等于没做。两条一起把口径钉死：
            //   可见 30 字＋换行 ⇒ 采用；可见 31 字＋换行 ⇒ 仍丢弃。
            var mlFit = LlmSpeaker.Judge(new string('啊', 15) + "\n" + new string('啊', 15), 30);
            Check("llmMultilineCountsVisibleOnly", mlFit.Speak,
                "可见 30 字＋中间一个换行 → " + (mlFit.Speak ? "采用（换行不占字数）" : "⚠ " + mlFit.Why));
            var mlOver = LlmSpeaker.Judge(new string('啊', 15) + "\n" + new string('啊', 16), 30);
            Check("llmMultilineStillCapped", !mlOver.Speak,
                "可见 31 字＋换行 → " + mlOver.Why + "（换行不计入 ≠ 不设上限）");
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

            // ---- 「关思考」：现代混合推理模型必须显式关掉（2026-09-29）----
            // 背景：智谱官方文档写 thinking.type **默认 enabled**，并把 GLM-4.7 归入「强制思考」；
            //   DeepSeek 官方定价页同样写「思考模式（默认）」。而思考 token **计入 max_tokens**，
            //   本程序写死 600 ⇒ 思考跑完正文为空，报的是「模型没有返回内容。」——
            //   **这句话指向不了真因**，看起来像模型坏了或网络问题。
            Check("openAiBodyAsksThinkingOff", TraeChat.AsksThinkingOff(body),
                "兼容端点的请求体必须带 thinking=disabled（不带 ⇒ 现代推理模型的正文会被思考挤空）");
            Check("openAiBodyThinkingIsOptOutable",
                !TraeChat.AsksThinkingOff(TraeChat.BuildOpenAiBody("deepseek-chat", inner, false)),
                "退避路径（显式 disableThinking=false）**不许**带 thinking —— "
                + "否则「模型拒绝关思考」时的退避毫无意义（等于原样再发一次）");
            Check("thinkingRejectJudge",
                TraeChat.LooksLikeThinkingRejected(400, "{\"error\":\"Unsupported parameter: thinking\"}")
                && TraeChat.LooksLikeThinkingRejected(422, "reasoning is not supported")
                && TraeChat.LooksLikeThinkingRejected(400, "{\"message\":\"不支持关闭思考\"}")
                && !TraeChat.LooksLikeThinkingRejected(400, "invalid api key")
                && !TraeChat.LooksLikeThinkingRejected(401, "thinking disabled not allowed")
                && !TraeChat.LooksLikeThinkingRejected(404, "billing/meter not found, thinking"),
                "「模型拒绝关思考」四象限：只认 400/422 × 文本提到 thinking/reasoning/思考；"
                + "401（key）与 404（地址）即便文本里带 thinking 也不退避（退避会把真因盖掉）");
            Check("emptyContentJudge",
                TraeChat.LooksLikeEmptyContent("模型没有返回内容。")
                && !TraeChat.LooksLikeEmptyContent("HTTP 429 限流"),
                "「没吐正文」要能被单独认出来 —— 它是唯一会补上「思考吃光预算」线索的那一种报错");

            // ================= G1. 限流（429）重试与备用通道回落 =================
            // 2026-09-29：用户实测 glm-4.7-flash 在聊天窗里回「HTTP 429 {code:1305 该模型当前访问量过大}」。
            // 官方错误码表（docs.bigmodel.cn/cn/faq/api-code）把 429 家族分得很细，**处置完全不同**：
            //   1302（你发太快）与 1305（**平台算力过载**，与你的频率无关）—— 等一下就好；
            //   1113 欠费／1308·1310·1316-1321 配额／1309 套餐／1311 权限 —— 等到明天也不会好。
            // ⇒ 「429 就一律重试」是错的：对后一组重试只会白花配额，还把「该充值了」拖成看不出原因的慢失败。
            Check("rateLimitJudge",
                TraeChat.LooksLikeTransientOverload(429, "{\"error\":{\"code\":\"1305\",\"message\":\"该模型当前访问量过大，请您稍后再试\"}}")
                && TraeChat.LooksLikeTransientOverload(429, "{\"error\":{\"code\":\"1302\",\"message\":\"您已达到速率限制\"}}")
                && TraeChat.LooksLikeTransientOverload(429, "")     // 只有状态码、没有正文 ⇒ 按标准语义
                && TraeChat.LooksLikeTransientOverload(503, "")
                && !TraeChat.LooksLikeTransientOverload(429, "{\"error\":{\"code\":\"1113\",\"message\":\"您的账户已欠费，请充值后重试\"}}")
                && !TraeChat.LooksLikeTransientOverload(429, "{\"error\":{\"code\":\"1310\",\"message\":\"您已达到每周/每月使用上限\"}}")
                && !TraeChat.LooksLikeTransientOverload(401, "user rate limit reached")
                && !TraeChat.LooksLikeTransientOverload(400, "{\"error\":{\"code\":\"1305\"}}")
                && !TraeChat.LooksLikeTransientOverload(200, ""),
                "限流判据：只认 429/503；429 里 1302/1305 值得「稍后再试」，而 1113(欠费)/1308·1310(配额)"
                + "/1309(套餐)/1311(权限) 重试**毫无意义**；401/400/200 一律不算");

            // ⚠ 下面这一组用**脚本化发送器**驱动（TraeChat.OpenAiSender），不打真网络。
            //   理由：「有没有重试」「有没有回落备用通道」是**行为契约**，而「此刻那个模型挤不挤」
            //   是外部状态 —— 拿真网络当判据输入，判据就会随天气变红变绿
            //   （本仓在 motiontest 的帧步长上刚吃过同一个亏）。延迟也置 0：判据不该真睡 3.6 秒。
            {
                var savedSender = TraeChat.OpenAiSender;
                var savedFallback = TraeChat.FallbackSource;
                var savedDelay = TraeChat.RateLimitDelayMs;
                TraeChat.RateLimitDelayMs = 0;
                // ⚠ 这里**不许**把 RateLimitRetries 硬写成 2：那样 `--no-rate-retry` 这张负对照牌就废了
                //   （判据自己把被测机制拧回默认值 ⇒ 关掉机制它照样绿）。期望次数一律从**当前值**算。
                try
                {
                    var msgs = new List<object>
                    {
                        new Dictionary<string, object> { ["role"] = "user", ["content"] = "在吗" }
                    };
                    const string RATE = "{\"error\":{\"code\":\"1305\",\"message\":\"该模型当前访问量过大，请您稍后再试\"}}";
                    const string BROKE = "{\"error\":{\"code\":\"1113\",\"message\":\"您的账户已欠费，请充值后重试\"}}";
                    const string CHAT_OK = "{\"choices\":[{\"message\":{\"content\":\"在的\"}}]}";
                    const string MAINBASE = "https://main.example.com";

                    // ① 前几次被限流、耗尽重试预算后的一次成功 ⇒ 必须自己重试并拿到回复
                    //    （＝用户那天本该看到的结局）。⚠ 脚本要**刚好耗尽预算**：
                    //    失败条数 = max(1, 当前预算)，末条成功。这样「关掉重试」（--no-rate-retry
                    //    把预算置 0）时首发必失败且不再重试 ⇒ r.Ok 变 false ⇒ 这张负对照牌才有牙。
                    {
                        var urls = new List<string>();
                        var script = new List<(bool Ok, int Status, string Body, string Text)>();
                        for (int k = 0, need = Math.Max(1, TraeChat.RateLimitRetries); k < need; k++)
                            script.Add((false, 429, RATE, "HTTP 429\n" + RATE));
                        script.Add((true, 200, CHAT_OK, "在的"));
                        TraeChat.FallbackSource = () => null;
                        TraeChat.OpenAiSender = ScriptedSender(urls, script.ToArray());
                        var r = TraeChat.ChatOpenAiAsync(MAINBASE, "k", "m", msgs).GetAwaiter().GetResult();
                        Check("rateRetryRecovers",
                            r.Ok && r.Text == "在的" && urls.Count == 1 + TraeChat.RateLimitRetries,
                            "限流（429/1305）应自己重试到成功：实得 成功=" + r.Ok + "、正文「" + r.Text
                            + "」、发了 " + urls.Count + " 次请求（期望 " + (1 + TraeChat.RateLimitRetries)
                            + " = 首发 + " + TraeChat.RateLimitRetries + " 次重试）；备注「" + TraeChat.LastRetryNote + "」");
                    }

                    // ② 一直限流 ⇒ 停在上限（不许无限重试），且报错必须**自己说出**这是服务端过载
                    {
                        var urls = new List<string>();
                        TraeChat.FallbackSource = () => null;
                        TraeChat.OpenAiSender = ScriptedSender(urls, (false, 429, RATE, "HTTP 429\n" + RATE));
                        var r = TraeChat.ChatOpenAiAsync(MAINBASE, "k", "m", msgs).GetAwaiter().GetResult();
                        bool told = r.Text.Contains("服务端") && r.Text.Contains("无关");
                        Check("rateRetryGivesUp",
                            !r.Ok && urls.Count == 1 + TraeChat.RateLimitRetries && told,
                            "一直限流就该停在上限（首发 + " + TraeChat.RateLimitRetries + " 次重试 = "
                            + (1 + TraeChat.RateLimitRetries) + " 次），实得 " + urls.Count + " 次；"
                            + "且报错要自己说明「这是服务端过载、与 key/地址无关」（实得："
                            + (told ? "已说明" : "**没说明** ⇒ 用户只会去反复检查 key 和地址") + "）");
                    }

                    // ③ 欠费类 429：一次都不许重试（重试不会让它变有钱）
                    {
                        var urls = new List<string>();
                        TraeChat.FallbackSource = () => null;
                        TraeChat.OpenAiSender = ScriptedSender(urls, (false, 429, BROKE, "HTTP 429\n" + BROKE));
                        var r = TraeChat.ChatOpenAiAsync(MAINBASE, "k", "m", msgs).GetAwaiter().GetResult();
                        Check("rateRetrySkipsFatal", !r.Ok && urls.Count == 1,
                            "1113（欠费）这类 429 重试毫无意义，必须**一次就停**：实得发了 " + urls.Count
                            + " 次（期望 1）");
                    }

                    // ④ 主通道一直限流、备用通道通 ⇒ 必须改走备用通道（这是用户那天该看到的结局）
                    {
                        var urls = new List<string>();
                        TraeChat.FallbackSource = () => Tuple.Create("https://alt.example.com", "k2", "m2");
                        TraeChat.OpenAiSender = (url, key, body) =>
                        {
                            urls.Add(url);
                            bool alt = url.StartsWith("https://alt.", StringComparison.Ordinal);
                            return Task.FromResult<(bool, int, string, string)>(
                                alt ? (true, 200, CHAT_OK, "在的") : (false, 429, RATE, "HTTP 429\n" + RATE));
                        };
                        var r = TraeChat.ChatOpenAiAsync(MAINBASE, "k", "m", msgs).GetAwaiter().GetResult();
                        bool hitAlt = urls.Count > 0 && urls[urls.Count - 1].StartsWith("https://alt.");
                        Check("rateFallbackToAlt", r.Ok && r.Text == "在的" && hitAlt,
                            "主通道被限流 ⇒ 应改走备用通道并成功：实得 成功=" + r.Ok + "、正文「" + r.Text
                            + "」、最后打到 " + (urls.Count > 0 ? urls[urls.Count - 1] : "(没发)")
                            + "；备注「" + TraeChat.LastRetryNote + "」");
                    }

                    // ⑤ 非限流的失败**不许**回落（key 错落到备用通道只是把一个真因换成另一个真因）
                    {
                        var urls = new List<string>();
                        TraeChat.FallbackSource = () => Tuple.Create("https://alt.example.com", "k2", "m2");
                        TraeChat.OpenAiSender = (url, key, body) =>
                        {
                            urls.Add(url);
                            return Task.FromResult<(bool, int, string, string)>(
                                (false, 401, "{\"error\":{\"code\":\"1000\",\"message\":\"身份验证失败\"}}", "HTTP 401"));
                        };
                        var r = TraeChat.ChatOpenAiAsync(MAINBASE, "bad-key", "m", msgs).GetAwaiter().GetResult();
                        Check("noFallbackOnAuthError",
                            !r.Ok && urls.Count == 1 && urls[0].StartsWith("https://main."),
                            "401（key 错）**不许**回落备用通道：实得发了 " + urls.Count + " 次、首个打到 "
                            + (urls.Count > 0 ? urls[0] : "(没发)")
                            + "（期望只打主通道 1 次）—— 回落会把真因盖住，比不回落更难查");
                    }
                }
                finally
                {
                    TraeChat.OpenAiSender = savedSender;
                    TraeChat.FallbackSource = savedFallback;
                    TraeChat.RateLimitDelayMs = savedDelay;
                }
            }

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

            // ================= H2. 配置下发唯一入口（2026-09-20 主面板引入）=================
            // ⚠ 这条是**接线判据**，不是行为判据：它盯的是「有没有人绕过 ApplyConfig 手写同步」。
            //   本项目老毛病「同一份数据两个落点」——旧托盘里就写过一次裸的 `OcrEye.SendText = ...`。
            //   判据读源码文本（同 PersonaTest 的做法）：扫两个 UI 文件里有没有静态位的裸赋值。
            //   为什么值得一条判据：绕过的后果是「某个入口改了配置但没生效」，而现象只是
            //   「勾了没反应」—— 那正是本项目最贵的一类 bug（功能可用性寄生在别处）。
            //
            // ⚠⚠ 首版**假绿**（2026-09-20 当场抓到）：扫描起点用了 `AppDomain.CurrentDomain.BaseDirectory`
            //   （＝ bin/Release/.../），而**源码不在那儿** ⇒ `File.Exists` 全 false ⇒ 全部 continue
            //   ⇒ 一条都没扫，判据恒绿。**判据扫的对象必须真的存在** —— 找不到源码要报红，不许沉默跳过。
            //   这是「扫描对象必须是输入，不是处理结果」的第三种形态：这次扫的是**空集**。
            {
                // 宿主目录（exe）＋ 工作目录（从源码树跑时）＋ 从这两处向上找含 pet.csproj 的目录，共三处。
                // 一个都找不到 ⇒ **报红**（发布目录里当然没有源码，但发布版也不跑这条判据 —— 见下）。
                string srcDir = FindSourceDir();

                if (srcDir == null)
                    Check("configApplyIsSingleEntry", false,
                        "找不到源码（Tray.cs）—— 这条判据扫不到任何文件就没资格通过。"
                        + "从源码树跑，或从含 pet.csproj 的目录跑。");
                else
                {
                    var offenders = new List<string>();
                    foreach (string uiFile in new[] { "Tray.cs", "SettingsWindow.cs", "ModelSettingsWindow.cs" })
                    {
                        string uiPath = Path.Combine(srcDir, uiFile);
                        if (!File.Exists(uiPath)) continue;              // 该文件不存在（如已删除）＝没事，不是失败
                        string src = File.ReadAllText(uiPath);
                        foreach (string banned in new[] { "OcrEye.SendText =", "OcrEye.Enabled =", "WatchLoop.RoastOn =" })
                        {
                            int at = src.IndexOf(banned, StringComparison.Ordinal);
                            while (at >= 0)
                            {
                                int lineStart = src.LastIndexOf('\n', at) + 1;
                                string line = src.Substring(lineStart, at - lineStart);
                                // 注释里提到名字不算（那是文档）；只拦真正的赋值语句。
                                if (!line.TrimStart().StartsWith("//")) { offenders.Add(uiFile + "@" + banned); break; }
                                at = src.IndexOf(banned, at + 1, StringComparison.Ordinal);
                            }
                        }
                    }
                    Check("configApplyIsSingleEntry", offenders.Count == 0,
                        offenders.Count == 0
                            ? "扫了 " + srcDir + " 的 3 个 UI 文件：没有裸写静态位（一律走 PetWindow.ApplyConfig）"
                            : "发现绕过唯一入口的裸赋值：" + string.Join("；", offenders));

                    // ---- 接线判据：`--llmtest` 必须真的测「用户配的那条通道」（2026-09-29）----
                    // 现场：通道路由原先只接在 `RunNormal` 里，而 `--llmtest` 在 Switch 更早处就 return
                    //   ⇒ 那时 `TraeChat.OpenAiSource` 仍是 null ⇒ **它测的是 Trae 中转通道**。
                    // 后果极坏：用户填好 GLM/DeepSeek 的 base+key、跑 --llmtest 拿到绿色，
                    //   而那条绿与新通道毫无关系 —— 「测错对象的绿」，比红更难发现。
                    // 为什么只能读源码：判据自己就在那个入口里跑，行为判据无法观察到「入口没接线」。
                    {
                        string progPath = Path.Combine(srcDir, "Program.cs");
                        string psrc = File.Exists(progPath) ? File.ReadAllText(progPath) : "";
                        // ⚠ 注释里提到不算 —— **只认真正的调用语句**（同 configApplyIsSingleEntry 对注释的处理）。
                        //   否则「把那行注释掉」也会被判成接上了 ⇒ 判据自己先假绿。
                        int atWire = -1;
                        for (int at = psrc.IndexOf("WireOpenAi(o, null)", StringComparison.Ordinal);
                             at >= 0;
                             at = psrc.IndexOf("WireOpenAi(o, null)", at + 1, StringComparison.Ordinal))
                        {
                            int ls = psrc.LastIndexOf('\n', at) + 1;
                            if (!psrc.Substring(ls, at - ls).TrimStart().StartsWith("//")) { atWire = at; break; }
                        }
                        int atLlm = psrc.IndexOf("if (o.LlmTest)", StringComparison.Ordinal);
                        bool wired = psrc.Length > 0 && atWire >= 0 && atLlm >= 0 && atWire < atLlm;
                        Check("llmTestWiresChannelFirst", wired,
                            psrc.Length == 0 ? "读不到 Program.cs —— 这条判据扫不到文件就没资格通过"
                            : atWire < 0 ? "Program.Main 里找不到**未被注释**的 `WireOpenAi(o, null)` —— 通道路由没接上"
                            : atLlm < 0 ? "Program.Main 里找不到 `if (o.LlmTest)`（开关改名了？判据要跟着改）"
                            : wired ? "通道路由在 --llmtest 分支之前接上（偏移 " + atWire + " < " + atLlm + "）"
                                    : "⚠ 接线在 --llmtest 之后（" + atWire + " > " + atLlm + "）⇒ --llmtest 测的是 Trae 通道");
                    }

                    // ---- 接线判据：说话频率必须真的送到闸门（2026-09-29）----
                    // 上面那几条频率判据验的是**纯函数**（换算对不对）。「配置有没有被送到闸门」
                    // 是另一件事：`SpeechFreq.Apply` 只有两个调用点 —— `StartBrain`（接线时）与
                    // `ApplyConfig`（面板保存时）。任缺一个，症状都是「她按出厂值说话」，
                    // 而纯函数判据照样全绿。⇒ 只能读源码（行为判据观察不到「入口漏接」）。
                    {
                        string pwPath = Path.Combine(srcDir, "PetWindow.cs");
                        string wsrc = File.Exists(pwPath) ? File.ReadAllText(pwPath) : "";

                        // ⚠ 两条约束，缺一条这条判据就会假绿：
                        //   ① 注释里提到不算（本仓踩过：注释掉那行之后判据依然全绿）；
                        //   ② 必须限定在 anchor 之后的**有限行数**内 —— 否则删掉 StartBrain 里那句，
                        //      搜索会一路滑到 ApplyConfig 里的那一句，判定「接线还在」。
                        bool CallAfter(string anchor, int lines)
                        {
                            int from = wsrc.IndexOf(anchor, StringComparison.Ordinal);
                            if (from < 0) return false;
                            int i = from, seen = 0;
                            while (i < wsrc.Length && seen < lines)
                            {
                                int eol = wsrc.IndexOf('\n', i);
                                if (eol < 0) eol = wsrc.Length;
                                string line = wsrc.Substring(i, eol - i);
                                if (!line.TrimStart().StartsWith("//")
                                    && line.IndexOf("SpeechFreq.Apply", StringComparison.Ordinal) >= 0) return true;
                                i = eol + 1; seen++;
                            }
                            return false;
                        }

                        bool inStartBrain = CallAfter("private void StartBrain()", 20);
                        bool inApplyCfg = CallAfter("public void ApplyConfig()", 20);
                        Check("speechFreqWiredToGate", wsrc.Length > 0 && inStartBrain && inApplyCfg,
                            wsrc.Length == 0 ? "读不到 PetWindow.cs —— 这条判据扫不到文件就没资格通过"
                            : inStartBrain && inApplyCfg ? "接线时与面板保存时都下发了频率（两处调用点齐）"
                            : "缺调用点：" + (inStartBrain ? "" : "StartBrain（开机后到首次保存设置之前按出厂值说话）")
                              + (!inStartBrain && !inApplyCfg ? "；" : "")
                              + (inApplyCfg ? "" : "ApplyConfig（面板改了频率不生效）"));
                    }
                }
            }

            // ============ H3. 判据**不许写用户的真配置**（2026-09-29 事故）============
            // 现场：跑一次 `--lifttest` 就把 %LOCALAPPDATA%\AzhuPet\config.json 覆盖成了判据自己
            //   构造的那份（speechOn=false、deepSeekKey/openAiBase 全空）—— 用户刚配好的 GLM key
            //   就这么没了。机制：判据要「真窗口」，而窗口摆位那条路（PlaceFeetOnFloor／SavePos／
            //   SetSize）会 `Cfg.Save()`，直接落到真路径。7 类判据都有窗口（lifttest／motiontest／
            //   dragtest／selftest／spintest／topmosttest／agenttimertest）。
            // ⚠ 这条判据**真的去调一次 Save()**，看守卫有没有拦下它 —— 只断言那个静态开关等于测自己
            //   （判据的读数必须来自被测对象，不是来自它自己的假设）。
            // ⚠ 判据的读数取自**守卫自己的计数器**，而不是「比对文件字节」：后者会被**正在运行的桌宠**
            //   顺手保存一次搅出假红（她保存的是内存里那份好配置，字节变了、值没变）——
            //   「判据的读数不许来自另一个进程的时序」。
            {
                string cfgPath = Path.Combine(PetConfig.Dir, "config.json");
                // ⚠⚠ 本判据**必须处在「非重定向」状态**才有意义 —— 守卫是**两个条件**：
                //   `!RealWriteAllowed` **且** 没设 `AZHU_CONFIG_DIR`（后者是给判据自己的临时沙箱开的门，
                //   见 `PetConfig.Save`）。谁沙箱化跑判据（顺手 `AZHU_CONFIG_DIR=… ./pet.exe --speaktest`），
                //   守卫就按设计放行 ⇒ 计数不涨 ⇒ 本条**假红**，看着像守卫坏了。
                //   ⇒ 这里临时摘掉它。安全性：守卫正常时 Save() 被挡、根本不落盘；万一守卫真坏了，
                //   落盘的是 `Load()` 读出来的**原值**（不是判据构造的那份）⇒ 不会丢 key。这正是本条该冒的险 ——
                //   它存在的意义就是证明守卫真挡得住，只断言那个静态开关等于测自己的假设。
                string savedCfgDir = Environment.GetEnvironmentVariable("AZHU_CONFIG_DIR");
                int before = PetConfig.BlockedSaves;
                try
                {
                    Environment.SetEnvironmentVariable("AZHU_CONFIG_DIR", null);
                    PetConfig.Load().Save();
                }
                finally
                {
                    Environment.SetEnvironmentVariable("AZHU_CONFIG_DIR", savedCfgDir);
                }
                bool blockedOnce = PetConfig.BlockedSaves == before + 1;
                Check("testModeRefusesRealConfigWrite", blockedOnce,
                    blockedOnce
                        ? "判据模式下 Save() 被守卫挡下（挡下计数 " + before + " → " + PetConfig.BlockedSaves
                          + "），真路径 " + cfgPath + " 没被写"
                        : "⚠ 判据模式下 Save() **穿透了守卫**（挡下计数停在 " + PetConfig.BlockedSaves
                          + "）—— 跑一次 --lifttest 就会把 " + cfgPath
                          + " 覆盖成判据构造的那份（speechOn=false、key/base 全空），用户的 key 会丢");
            }

            // ================= H. 落点 =================
            string realPath = Path.Combine(Memory.Dir(), "memory.jsonl");
            bool outside = realPath.IndexOf("40 Projects", StringComparison.OrdinalIgnoreCase) < 0
                        && realPath.IndexOf("坚果云", StringComparison.Ordinal) < 0;
            Check("memoryOutsideSyncDir", outside, "真落点 " + realPath);

            return Report(ok, checks, negative, null, "speaktest");
        }

        /// <summary>脚本化发送器：按调用序号返回响应（越界则一直用最后一条），供限流重试／回落判据使用。
        /// ⚠ 逐个记下收到的 URL —— 「有没有重试」「有没有改走备用通道」只**能**从 URL 序列上看出来：
        ///   光看「最后成功了没」，是分不清「一次就成」与「重试两次才成」的（同「判据的读数必须能分辨对象」）。</summary>
        private static Func<string, string, Dictionary<string, object>,
            Task<(bool Ok, int Status, string Body, string Text)>>
            ScriptedSender(List<string> urls,
                params (bool Ok, int Status, string Body, string Text)[] script)
        {
            int i = 0;
            return (url, key, body) =>
            {
                urls.Add(url);
                var r = i < script.Length ? script[i] : script[script.Length - 1];
                i++;
                return Task.FromResult(r);
            };
        }

        /// <summary>找源码目录（含 Tray.cs 的那个）：exe 目录、工作目录，以及从这两处向上找含 pet.csproj 的目录。
        /// ⚠ 抽出来是为了让所有「读源码的接线判据」共用一份 —— 各写一份迟早漏改（本项目老毛病「同一份数据两个落点」）。
        ///   `internal`（不是 private）：BalanceConfigTest 的凭据解析口径判据也读它，两边必须指同一个目录。
        /// ⚠⚠ 找不到时返回 null，**调用方必须报红**：扫描对象是空集时判据会恒绿，
        ///   这正是 2026-09-20 首版 `configApplyIsSingleEntry` 假绿的成因。</summary>
        internal static string FindSourceDir()
        {
            var roots = new List<string>
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Directory.GetCurrentDirectory(),
            };
            // 从源码树跑（本项目常态）：向上找含 pet.csproj 的目录。
            foreach (string start in roots.ToArray())
            {
                string walk = start;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(walk); i++)
                {
                    if (File.Exists(Path.Combine(walk, "pet.csproj"))) { roots.Add(walk); break; }
                    string up = Path.GetDirectoryName(walk.TrimEnd(Path.DirectorySeparatorChar));
                    if (string.IsNullOrEmpty(up) || up == walk) break;
                    walk = up;
                }
            }
            foreach (string r in roots)
                if (!string.IsNullOrEmpty(r) && File.Exists(Path.Combine(r, "Tray.cs"))) return r;
            return null;
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

            // ⚠⚠ 通道必须打印**实际用的那一条**（2026-09-29 修）：这里原先写死「TraeChat(llm_utils_chat…)」，
            //   而用户一旦填了兼容端点，程序走的就是 OpenAI 通道 —— 打印与实际不符，
            //   于是「换 API 到底有没有生效」在这条唯一的端到端入口上**看不出来**。
            var chSrc = TraeChat.OpenAiSource != null ? TraeChat.OpenAiSource() : null;
            bool chOa = TraeChat.ShouldUseOpenAi(chSrc);
            string chModel = chOa && !string.IsNullOrEmpty(chSrc.Item3)
                ? chSrc.Item3 : TraeChat.ResolveModel(o.LlmModel);
            Console.WriteLine("llmtest —— 真调 " + times + " 次；台词上限 " + speaker.MaxChars + " 字（换行不计；多行已允许）；通道 "
                + (chOa ? "OpenAI 兼容（" + chSrc.Item1 + "）" : "Trae 中转（llm_utils_chat，明文兼容分支）")
                + (o.SpeakManual ? "；**手动触发那套提示**（不提秒数、不说「刚换了窗口」）" : ""));
            if (!chOa)
                Console.WriteLine("⚠ 走的是 Trae 通道 —— base 或 key 为空时就会这样（本机没装 Trae 时它必然失败）。"
                    + "要测 GLM/DeepSeek：填好 base+key，或用 --openai-base / --openai-key / --openai-model 临时指定。");
            Console.WriteLine("⚠ 只发进程名与屏幕上的字，**绝不发窗口标题**（P0 视野纪律：A 档才准出本机）");
            // 判据自己报出用的是哪个模型 —— 否则「换模型」的对照实验无法归因。
            Console.WriteLine("本轮模型 id = " + chModel
                + (string.IsNullOrEmpty(TraeChat.ModelOverride) ? "（默认）" : "（--llm-model 覆盖）")
                + "；请求体 " + (TraeChat.SendThinkingDisabled
                    ? "带 thinking=disabled（关思考）"
                    : "**不带** thinking（负对照 --old-openai-body 已开）"));
            // 说话频率也报一行 —— 改完「设置 → 说话与吐槽 → 触发节奏」想确认她到底多久说一句时，
            // 这一行就是答案（读数来自**配置**，与 PetWindow 下发用的是同一个 SpeechFreq 换算）。
            var freqCfg = PetConfig.Load();
            double freqCd = SpeechFreq.CooldownSec(freqCfg);
            Console.WriteLine("说话频率 = 每 " + (freqCd / 60).ToString("0.##") + " 分钟最多一句（间隔配置 "
                + freqCfg.SpeechCooldownMin.ToString("0.##") + " 分钟）；安静时每 "
                + (SpeechFreq.IdleNudgeSec(freqCfg, freqCd) / 60).ToString("0.##") + " 分钟冒一句保底；每天上限 "
                + SpeechFreq.DailyCap(freqCfg) + " 句");

            var rows = new List<object>();
            int responded = 0, spoke = 0, overlong = 0, multiline = 0, multiDropped = 0, leakedCount = 0;
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
                if (v != null && v.Speak)
                {
                    spoke++;
                    // ⚠ 2026-09-29 起多行会被**采用** ⇒ 这里统计的是「采用的有几条是分行的」。
                    //   多行**丢弃**另有计数（只在负对照 --no-multiline 下才可能非零）——
                    //   两个数分开，否则「多行」这个词会指两件相反的事。
                    if (v.Text != null && v.Text.IndexOf('\n') >= 0) multiline++;
                }
                else if (v != null && (v.Why ?? "").IndexOf("超 ", StringComparison.Ordinal) >= 0) overlong++;
                else if (v != null && (v.Why ?? "").IndexOf("多行", StringComparison.Ordinal) >= 0) multiDropped++;
                // 相关性：她的台词里有没有出现屏幕文字中的某个 ≥2 字片段。
                // ⚠ 这只是**报告项**，不参与 ok 判定 —— 真模型的输出不确定，拿它当硬判据必然假红，
                //   而假红会让人开始无视判据。接线那一半（原文有没有进提示）由 notCarried 硬验。
                bool rel = v != null && v.Speak && RelatedToScreen(v.Text, o.ScreenText);
                if (rel) related++;

                Console.WriteLine();
                Console.WriteLine("[" + (i + 1) + "/" + times + "] 喂给她：" + prompt + (leaked ? "   ⚠ 标题泄漏！" : ""));
                Console.WriteLine("        " + sw.ElapsedMilliseconds + " ms → "
                    + (v != null && v.Speak
                        ? "她说：「" + v.Text + "」（" + Shape(v.Text) + "）"
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
                              : "⚠ 一次都没拿到 —— 先对照上面打印的**通道**：走 Trae ⇒ 多半是没配 base/key；"
                                + "走 OpenAI 兼容 ⇒ 看状态码（401=key 不对，404=base 多写或少写了一段路径）。");
            Check("llmPromptHasNoTitle", leakedCount == 0,
                leakedCount == 0 ? "喂模型的提示不含窗口标题（拿真标题试的）" : "⚠ 泄漏 " + leakedCount + " 次");
            // ⚠ 相关性判据的**可判定那一半**：给了 --screen-text 时，那段文字必须逐字出现在提示里。
            //   （「她有没有用上」是模型的选择，只作为报告项 related —— 拿它当判据必然假红。）
            Check("llmPromptCarriesScreenText", notCarried == 0,
                string.IsNullOrEmpty(o.ScreenText)
                    ? "（没给 --screen-text ⇒ 这一条没有输入，按通过计；要验它请加 --screen-text \"...\"）"
                    : notCarried == 0 ? "给了 " + times + " 次屏幕文字，每次都逐字进了提示"
                                      : "⚠ 有 " + notCarried + " 次屏幕文字**没**进提示（她当然说得不相关）");

            // 退避是**静默**发生的（成功时不报任何错）—— 不主动说，用户就不知道它发生过。
            if (!string.IsNullOrEmpty(TraeChat.LastThinkingNote))
                Console.WriteLine("⚠ 本次运行发生过一次退避：" + TraeChat.LastThinkingNote);
            // 限流处置同理（2026-09-29 加）：不说出来，用户就不知道「刚才其实被限流过、它自己缓过来了」——
            // 而那种情况下他会以为一切正常，直到某天限流久到重试也没用。
            if (!string.IsNullOrEmpty(TraeChat.LastRetryNote))
                Console.WriteLine("⚠ 本次运行发生过限流处置：" + TraeChat.LastRetryNote);

            // 备用通道有没有真的接上：这是端到端入口，用户看不到配置面板里那三个框在这个进程里生效了没。
            var fbNow = TraeChat.FallbackSource != null ? TraeChat.FallbackSource() : null;
            if (TraeChat.ShouldUseOpenAi(fbNow))
                Console.WriteLine("备用通道已配置：" + fbNow.Item1 + "（模型 "
                    + (string.IsNullOrEmpty(fbNow.Item3) ? "同主通道" : fbNow.Item3)
                    + "）—— 主通道被限流时会自动改走它");

            Console.WriteLine();
            Console.WriteLine("llmtest 汇总 —— 拿到回复 " + responded + "/" + times + "，采用 " + spoke
                + "（其中分行 " + multiline + " 条）"
                + "，超长丢弃 " + overlong
                + (multiDropped > 0 ? "，多行丢弃 " + multiDropped + "（⚠ 只在 --no-multiline 下会非零）" : "")
                + (string.IsNullOrEmpty(o.ScreenText) ? "" : "；台词用上屏幕文字 " + related + "/" + spoke + "（报告项）"));

            return Report(ok, checks, false,
                "（真调模型：验 LlmSpeaker 的**网络那半段**；只答「她怎么说」，不答「何时说」）", "llmtest");
        }

        /// <summary>台词的「形状」：可见字数 ＋（分行时）行数。
        /// ⚠ 不能直读 `Text.Length` —— 多行时会把换行符也数进去，看上去像「她话变多了」，其实没有。</summary>
        private static string Shape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "0 字";
            int lines = 1;
            for (int i = 0; i < s.Length; i++) if (s[i] == '\n') lines++;
            int vis = LlmSpeaker.VisibleLength(s);
            return lines > 1 ? vis + " 字·" + lines + " 行" : vis + " 字";
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
