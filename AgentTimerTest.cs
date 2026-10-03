// 多 agent 计时读数的**端到端**判据 —— 读数在「拎起→放下」前后**必须还在**。
//
// 背景（2026-09-29 用户报）：
//   「当我的 codex 运行时，桌宠会显示 codex 耗时，但是当我将桌宠提起并放下后，
//     codex 的进行中时间就会消失。」
//
// 为什么必须**离线合成日志**，而不是拿用户真在跑的 Codex 日志来验：
//   ① 不可重复 —— 得先等到真有一个任务在跑；
//   ② 会去读、并且依赖用户当前的真实任务状态，判据变成碰运气；
//   ③ 构造不出「任务刚好在这一拍开始／结束」这类边界。
//   ⇒ `AgentTaskTimer` 有三个根目录覆盖（TraeRootOverride / WorkBuddyRootOverride /
//     CodexRootOverride），本判据把它们指到临时目录，**不碰任何真数据**
//     （同 `PetConfig.Dir` 支持 `AZHU_CONFIG_DIR` 的理由）。
//
// 三段判据，缺一段就证明不了需求：
//   A 拖拽**前**读数必须在 —— 否则「拖拽后还在」是句废话。
//   B **真拖拽**（SendInput 真的按下→分步移动→松开，`LiftCount` 必须真的涨）之后 2 秒内，
//     **每一拍**读数都必须在。
//     ⚠ 不能只看首尾两点：`SlowTick` 是 120 ms 一拍的脉冲，丢一拍在中途是采不到的。
//     ⚠ 「每一拍」这条是本判据的核心 —— 用户看到的「消失」如果是**闪一下又回来**，
//       首尾两点照样全绿，而那正是最可能发生的一类。
//   C **正对照**：把日志补一行 `task_complete` ⇒ 读数必须**换成「耗时」**、
//     再等过 `AfterEndSec`(10 s) 必须**真的消失**。
//     ⚠ 没有 C，「B 通过」可能只是因为我把读数整个焊死在屏幕上 —— 那不是需求，是新 bug。
//   F 开关（`TaskTimerOn`）：关掉 ⇒ 顶上读数必须**立刻消失**；重新打开 ⇒ 必须自己回来。
//     ⚠ 没有 F，「开关关了读数还挂着」会一路静默 —— 本仓最忌讳的那种假状态。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AzhuPet
{
    internal static class AgentTimerTest
    {
        const int StepPx = 14;        // 每步位移（物理像素），**故意大于** LiftThresholdPx=8
        const int Steps = 6;          // 拖拽分几步（分步移动，避免一次性跳过去只收到一个 MouseMove）
        const double ObserveSec = 2.0;   // 松手后的观测窗（要跨过 ≥16 拍 SlowTick）

        private static string P(bool ok) { return ok ? "PASS" : "FAIL"; }
        private static string[] Row(string name, string val, bool ok) { return new[] { name, val, P(ok) }; }
        private static string[] Row(string name, string val) { return new[] { name, val, "PASS" }; }

        private static string _codexFile;

        public static int Run(Cli o)
        {
            var rows = new List<string[]>();
            var notes = new List<string>();

            // ---- D 组先跑：**纯逻辑**、不开窗、不需要模型（验的是「Codex 一个会话横跨多个文件」）----
            // ⚠ 判据模式下 `Program.Main` 会把它关掉（`Enabled = !o.AnyTest()`，理由见那里），
            //   本判据测的**就是**它，必须自己打开 —— D 组也一样，所以放在最前面。
            AgentTaskTimer.Enabled = true;
            AgentTaskTimer.OldCodex = o.OldCodex;
            AgentTaskTimer.NoWbBeat = o.NoWbBeat;
            AgentTaskTimer.NoBeatCap = o.NoBeatCap;
            AgentTaskTimer.NoWbSm = o.NoWbSm;
            RunCodexSessions(rows, notes, o.OldCodex);
            RunWorkBuddyHeartbeat(rows, notes);
            RunWorkBuddyStateMachine(rows, notes);

            string modelPath = Cli.ResolveModel(o.ModelPath);
            if (modelPath == null)
            {
                // D 组已经跑完了，别把它的结论吞掉（找不到模型是 A/B/C 的问题，与 D 无关）。
                Console.Error.WriteLine("找不到模型");
                try { LiftTest.Report(o, rows, notes); } catch { }
                return 1;
            }
            var m = Glb.Load(modelPath);

            // ---- 临时根目录：三家各指一处，TRAE / WorkBuddy 指到不存在的路径 ⇒ 只留 Codex 一个来源 ----
            string temp = Path.Combine(Path.GetTempPath(), "azhu-agenttimertest");
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            string codexRoot = Path.Combine(temp, "codex");
            string day = Path.Combine(codexRoot, DateTime.Now.ToString("yyyy"),
                                               DateTime.Now.ToString("MM"),
                                               DateTime.Now.ToString("dd"));
            Directory.CreateDirectory(day);
            // ⚠ 会话号必须是**带连字符的 UUID**（`ToString("D")`）：`AgentTaskTimer.CodexSession`
            //   按 rollout 文件名的形状认会话号，`ToString("N")`（32 位无连字符）认不出来
            //   ⇒ 这个文件会被整个跳过，A/B/C 三组会全红在一个与被测逻辑无关的地方。
            _codexFile = Path.Combine(day,
                "rollout-" + DateTime.Now.ToString("yyyy-MM-dd'T'HH-mm-ss")
                + "-" + Guid.NewGuid().ToString("D") + ".jsonl");
            // 「上一轮已结束」＋「本轮已开始」⇒ 最近一次开始晚于最近一次结束 ⇒ **正在跑**。
            // ⚠ 两行都要有：只有 `task_started` 时 `EndMs` 恒为 0，验不到「往回扫到上一条 complete」那条路。
            WriteAll(new[]
            {
                Line(DateTime.UtcNow.AddMinutes(-30), 1, "task_complete", "t0"),
                Line(DateTime.UtcNow.AddSeconds(-6),  2, "task_started",  "t1"),
            });
            notes.Add("合成 Codex 日志 = " + _codexFile);

            AgentTaskTimer.CodexRootOverride = codexRoot;
            AgentTaskTimer.TraeRootOverride = Path.Combine(temp, "no-trae");
            AgentTaskTimer.WorkBuddyRootOverride = Path.Combine(temp, "no-workbuddy");
            // ⚠ 心跳目录是**第四个**落点，也要指走 —— 否则判据会去读用户真的
            //   `~/.workbuddy/sessions`（不碰真数据的纪律，同上面三条）。
            AgentTaskTimer.WorkBuddySessionsOverride = Path.Combine(temp, "no-wb-sessions");
            // ⚠ 判据模式下 `Program.Main` 会把它关掉（`Enabled = !o.AnyTest()`，理由见那里），
            //   本判据测的**就是**它，必须自己打开。
            AgentTaskTimer.Enabled = true;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // 三个自由度先钉死（同 --lifttest 的纪律）：托盘会开独立消息泵；自发说话会推台词气泡
            // 把 `BubbleText` 污染成「她刚说的话」；调光会改不透明度。
            var cfg = new PetConfig
            {
                SizeIndex = o.SizeIndex, NightDim = false, ShowTray = false, SpeechOn = false
            };
            var r = new WpfPetRenderer(m);
            var w = new PetWindow(r, cfg) { SelfTestMode = true, ForcedIdle = -1 };
            w.Pose.DozeAfter = 1e9;
            w.Pose.SleepAfter = 2e9;
            w.Show();

            var clock = Stopwatch.StartNew();
            var tl = new DispatcherTimer(DispatcherPriority.Normal);
            tl.Interval = TimeSpan.FromMilliseconds(40);

            // ---- 采样 ----
            string hBefore = null, tBefore = null;   // A：拖拽前的两个落点（气泡流里的那份 / 计时器里那份）
            bool vBefore = false;
            int lift0 = 0;                            // 拖拽前的 LiftCount 基线
            int obsTicks = 0, obsBadTicks = 0;        // B：观测窗内总拍数 / 读数不在的拍数
            int obsNullTicks = 0;                     // 其中「整条被摘掉」（Header 为 null）的拍数
            int obsInvisibleTicks = 0;                // 其中「读数在、气泡窗却不可见」的拍数
            string obsWorst = null;                   // 观测窗内第一次丢读数时的原文（诊断用）
            string obsInvisibleWorst = null;          // 观测窗内第一次「读数在但窗不可见」的诊断
            string hAfterDrag = null;                 // 松手 2 s 后（仍在跑）的读数
            bool vAfterDrag = false;                  // 松手 2 s 后气泡窗是否可见
            double hideKAfterDrag = 0;                // 松手 2 s 后的隐退进度（1 = 已隐退）
            string hDone = null;                      // C：补了 task_complete 之后的读数
            string hGone = "(未采到)";                // C：过 AfterEndSec 之后的读数
            string hGateOff = "(未采到)";             // F：开关关掉后的读数（期望 null）
            string hGateOn = "(未采到)";              // F：开关重新打开后的读数（期望「进行中」）
            double tGate = 0;                         // F：开关翻转的时刻

            int step = 0, sub = 0;
            int atX = (o.At != null && o.At.Length == 2) ? o.At[0] : 900;
            Point hitPt = new Point();
            bool hitFound = false;
            double t0 = 0, tAppend = 0;

            tl.Tick += (s, e) =>
            {
                double t = clock.Elapsed.TotalSeconds;
                try
                {
                    switch (step)
                    {
                        case 0:
                            // 摆位口径同 --lifttest：屏幕中部 + **贴地**。
                            //   ⚠ 不贴地的话她会先自由落体，抛落那条通路会先跑一遍，
                            //     而本判据要验的是「拖拽」那一条。
                            if (t < 0.9) break;
                            w.Left = atX / w.DipScale;
                            w.Top = SystemParameters.WorkArea.Bottom - r.FeetYDip;
                            step = 1;
                            break;

                        case 1:
                            if (t < 1.4) break;
                            for (double fy = 0.45; fy < 0.80 && !hitFound; fy += 0.02)
                            {
                                var p = new Point(w.ActualWidth * 0.5, w.ActualHeight * fy);
                                if (r.HitTest(p)) { hitPt = p; hitFound = true; }
                            }
                            if (!hitFound)
                            {
                                rows.Add(Row("agenttimer.hit_point_found", "未找到可点的身体位置", false));
                                Done(rows, notes, o, w, app);
                                step = 99;
                                break;
                            }
                            MoveCursorTo(w, hitPt);
                            step = 2;
                            break;

                        // 稳定两拍：光标先到位，让窗口把 MouseMove 消化掉再按下
                        case 2:
                            if (t < 2.0) break;
                            MoveCursorTo(w, hitPt);
                            step = 3;
                            break;

                        case 3:
                            if (t < 2.4) break;
                            // ---- A：拖拽前的基线 ----
                            hBefore = w.HeaderText;
                            tBefore = w.TimerHeaderText;
                            vBefore = w.BubbleVisible;
                            lift0 = w.LiftCount;
                            rows.Add(Row("agenttimer.A.header_before_drag",
                                "拖拽前气泡流读数 = " + Show(hBefore), HasRunning(hBefore)));
                            rows.Add(Row("agenttimer.A.timer_before_drag",
                                "拖拽前计时器读数 = " + Show(tBefore), HasRunning(tBefore)));
                            Down();
                            step = 4;
                            break;

                        // ---- B：真的拖起来（分步移动，一步一个 tick）----
                        case 4:
                            if (sub >= Steps) { step = 5; break; }
                            Nudge(StepPx, 0);
                            sub++;
                            break;

                        case 5:
                            Up();
                            t0 = t;
                            step = 6;
                            break;

                        case 6:
                            // ---- B：观测窗内**每一拍**都要有读数 ----
                            string h = w.HeaderText;
                            obsTicks++;
                            if (!HasRunning(h))
                            {
                                obsBadTicks++;
                                if (h == null) obsNullTicks++;
                                if (obsWorst == null)
                                    obsWorst = "第 " + obsTicks + " 拍（松手后 "
                                        + (t - t0).ToString("0.00") + " s）：气泡流读数 = " + Show(h)
                                        + "；计时器读数 = " + Show(w.TimerHeaderText)
                                        + "；气泡窗可见 = " + w.BubbleVisible;
                            }
                            // ⚠⚠ 与上一条分开量：读数**算出来了**（Header 非 null）但气泡窗**不可见**
                            //   —— 用户看到的「消失」正是这一种（数值还在，屏幕上没有）。
                            //   只判 `HeaderText` 会把这一类漏掉，而那是最可能的一种。
                            if (HasRunning(h) && !w.BubbleVisible)
                            {
                                obsInvisibleTicks++;
                                if (obsInvisibleWorst == null)
                                    obsInvisibleWorst = "第 " + obsTicks + " 拍（松手后 "
                                        + (t - t0).ToString("0.00") + " s）：读数 = " + Show(h)
                                        + "；气泡窗可见 = false；气泡窗矩形 = " + Rect(w)
                                        + "；隐退进度 HideK = " + w.HideK.ToString("0.000")
                                        + "；宠物窗可见 = " + w.IsVisible;
                            }
                            if (t - t0 < ObserveSec) break;
                            hAfterDrag = w.HeaderText;
                            vAfterDrag = w.BubbleVisible;
                            hideKAfterDrag = w.HideK;
                            // ---- F：开关（TaskTimerOn）----
                            // ⚠ 插在**这里**（任务仍在跑、文件已被盯住）而不是等 C 之后：C 之后会话
                            //   已结束并被清出 keep，再出现要等 Locate 的 5 s 扫描，会把这条判据拖成慢判据。
                            w.Cfg.TaskTimerOn = false;
                            tGate = t;
                            step = 20;
                            break;

                        // ---- F：开关必须真的管用 ----
                        // 任务**仍在跑**的前提下：关掉开关 ⇒ 顶上读数必须**立刻消失**；重新打开 ⇒ 必须自己回来。
                        // ⚠ 没有这条，「开关关了读数还挂着」会一路静默 —— 那正是本仓最忌讳的假状态。
                        case 20:
                            if (t - tGate < 0.9) break;     // 跨过好几拍 SlowTick(120ms)
                            hGateOff = w.HeaderText;
                            w.Cfg.TaskTimerOn = true;
                            tGate = t;
                            step = 21;
                            break;

                        case 21:
                            if (t - tGate < 1.0) break;     // Poll 每拍都 Compose，够它把读数重算回来
                            hGateOn = w.HeaderText;
                            step = 7;
                            break;

                        // ---- C：正对照 —— 任务真的结束，读数**应该**走掉 ----
                        case 7:
                            Append(Line(DateTime.UtcNow, 3, "task_complete", "t1"));
                            tAppend = t;
                            step = 8;
                            break;

                        case 8:
                            if (t - tAppend < 1.6) break;      // 等增量读那一拍（ReadEverySec=0.5）接上
                            hDone = w.HeaderText;
                            step = 9;
                            break;

                        case 9:
                            // `AfterEndSec`(10 s) 之后必须**真的**消失 —— 这一条证明本判据有区分度：
                            // 读数不是被焊死的，它会（在该走的时候）走。
                            if (t - tAppend < 11.0) break;
                            hGone = w.HeaderText;
                            Analyze();
                            step = 99;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    rows.Add(Row("agenttimer.exception", ex.GetType().Name + ": " + ex.Message, false));
                    try { rows.Add(Row("agenttimer.exception_stack", ex.StackTrace ?? "", true)); } catch { }
                    Done(rows, notes, o, w, app);
                    step = 99;
                }
            };
            tl.Start();
            app.Run();
            return rows.Exists(x => x[2] == "FAIL") ? 1 : 0;

            // ---------------------------------------------------------------- 分析
            void Analyze()
            {
                tl.Stop();
                int liftN = w.LiftCount - lift0;

                rows.Add(Row("agenttimer.B.drag_was_real",
                    "拖拽真的把她拎起来了 " + liftN + " 次（期望 ≥1；0 ⇒ 这次「拖拽」根本没发生，B 组全绿也不算数）",
                    liftN >= 1));
                rows.Add(Row("agenttimer.B.observe_ticks",
                    "松手后观测 " + obsTicks + " 拍（2.0 s ÷ 40 ms ≈ 50 拍）", obsTicks >= 30));
                // ⚠⚠ 本判据的核心一条：**每一拍**都要有读数。
                rows.Add(Row("agenttimer.B.ticks_with_header",
                    (obsTicks - obsBadTicks) + " / " + obsTicks + " 拍读数还在（期望全部）", obsBadTicks == 0));
                // ⚠ 与上一条分开：`Header` 被置 null ＝ **整条被摘掉**（用户看到的就是这个），
                //   而「有文本但不是进行中」是另一回事（例如掉进了「耗时」分支）—— 两种病的药不一样。
                rows.Add(Row("agenttimer.B.ticks_never_null",
                    obsNullTicks + " 拍读数**整条被摘掉**（期望 0）", obsNullTicks == 0));
                if (obsWorst != null) notes.Add("首次丢读数：" + obsWorst);
                if (obsInvisibleWorst != null) notes.Add("首次「读数在但窗不可见」：" + obsInvisibleWorst);
                // ⚠⚠ 这一条才是用户报的「消失」的正主：读数**算得出来**，但气泡窗不在屏幕上。
                //   与 `ticks_never_null` 分开：那一条量「算没算出来」，这一条量「看得见看不见」。
                rows.Add(Row("agenttimer.B.bubble_visible_ticks",
                    (obsTicks - obsInvisibleTicks) + " / " + obsTicks
                    + " 拍气泡窗可见（期望全部；不可见的那些拍读数明明还在）",
                    obsInvisibleTicks == 0));
                rows.Add(Row("agenttimer.B.bubble_visible_after_drag",
                    "松手 2 s 后气泡窗仍可见 = " + vAfterDrag + "（隐退进度 HideK = "
                    + hideKAfterDrag.ToString("0.000") + "）", vAfterDrag));
                rows.Add(Row("agenttimer.B.header_after_drag",
                    "松手 2 s 后读数 = " + Show(hAfterDrag), HasRunning(hAfterDrag)));

                // ---- C：正对照（读数**该走的时候**必须走，否则 B 组没有区分度）----
                rows.Add(Row("agenttimer.C.done_switch",
                    "补 `task_complete` 后读数 = " + Show(hDone) + "（期望含「耗时」）",
                    hDone != null && hDone.Contains("耗时")));
                rows.Add(Row("agenttimer.C.gone_after_window",
                    "过 AfterEndSec(10 s) 后读数 = " + Show(hGone) + "（期望 null：真的走掉）",
                    hGone == null));

                // ---- F：开关（TaskTimerOn）必须真的管用（关＝立刻消失，开＝自己回来）----
                rows.Add(Row("agenttimer.F.gate_off_hides",
                    "开关关掉后读数 = " + Show(hGateOff) + "（期望 null：不能只停 Poll 留着上一次的）",
                    hGateOff == null));
                rows.Add(Row("agenttimer.F.gate_on_shows",
                    "开关重新打开后读数 = " + Show(hGateOn) + "（期望含「进行中」）", HasRunning(hGateOn)));
                Done(rows, notes, o, w, app);
            }
        }

        /// <summary>「读数在，且说的是『进行中』」—— 本判据的唯一口径。
        /// ⚠ 只判非 null 不够：掉进「耗时」分支时读数**还在**，但用户看到的已经变味了。</summary>
        private static bool HasRunning(string h) { return h != null && h.Contains("进行中"); }
        private static string Show(string h) { return h == null ? "null" : "\"" + h.Replace("\n", " / ") + "\""; }

        // ================================================================ D 组：Codex 的「会话 ↔ 文件」
        //
        // 用户报的「提起放下后 codex 耗时消失」，根因在**会话 ↔ 文件**这一层（见 AgentTaskTimer
        // 文件头那段「会话 ≠ 文件」）：Codex 一个会话会横跨多个 rollout 文件（compact／续写会另开一个，
        // 文件名尾部多一段 UUID，但**第一个 UUID 是会话号**，多个文件共用）。旧版按**文件**记状态，
        // 于是 ① 同一会话被拆成两条读数、② 旧文件里悬空的 `task_started` 报假「进行中」、
        // ③ 会话真正的活动已经挪到新文件、旧文件一冻住就把「还在跑」判成「已经没了」。
        // 拖拽只是用户**注意到**它的时刻，不是病因 —— 所以这一组**不开窗、不拖拽**，
        // 直接驱动 `AgentTaskTimer.Poll`（120 ms 一拍的宿主循环里，它本来就是纯计算）。
        //
        // 三段，每段一个会话、跑完把目录清空再进下一段（CodexSessions=2，同时最多盯两个会话，
        // 不清的话上一段会一直占着一个名额）：
        //   D1 合并：悬空 start 在旧文件、完整一轮在新文件 ⇒ **只能一条** Codex 读数、且是「耗时」。
        //   D2 中断：`turn_aborted` 必须算结束（中断后不会再来 `task_complete`）⇒ 不得报「进行中」。
        //   D3 存活：悬空 start 在**已冻住**的旧文件、新文件还在被写 ⇒ 必须仍报「进行中」。
        //   D4 存活：**同一个**文件 mtime 冻住、但文件还在增长 ⇒ 必须仍报「进行中」。
        //            ⚠ D3/D4 才是用户看到的「消失」；D4 是 2026-10-02 那次（mtime 不刷新）。
        // ⚠ `--old-codex`（负对照）下 D1–D3 必须全红 —— 否则本组没有区分度（见 AgentTaskTimer.OldCodex）。
        //   ⚠ D4 **不受它影响**：D4 验的是「mtime 冻住时以文件增长为准」，与 Codex 的状态口径无关。

        private static void RunCodexSessions(List<string[]> rows, List<string> notes, bool oldCodex)
        {
            string root = Path.Combine(Path.GetTempPath(), "azhu-codexsess");
            string day = null;
            try
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
                day = Path.Combine(root, DateTime.Now.ToString("yyyy"),
                                        DateTime.Now.ToString("MM"),
                                        DateTime.Now.ToString("dd"));
                Directory.CreateDirectory(day);

                AgentTaskTimer.CodexRootOverride = root;
                AgentTaskTimer.TraeRootOverride = Path.Combine(root, "no-trae");
                AgentTaskTimer.WorkBuddyRootOverride = Path.Combine(root, "no-workbuddy");
                AgentTaskTimer.WorkBuddySessionsOverride = Path.Combine(root, "no-wb-sessions");

                var t = new AgentTaskTimer();
                double clock = 0;
                // 每拍把 Poll 的时钟推 6 s（> ScanEverySec 5 s）⇒ 每拍都重新 Locate + 增量读。
                // ⚠ 推的是**判据自己的时钟**，不是真实时间：日志里的「多少秒以前」按真实 UTC 写，
                //   两者互不干扰（Poll 的入参只管节流）。
                Func<string> header = () => { clock += 6; t.Poll(clock); return t.Header; };
                Func<string> settle = () => { header(); return header(); };   // 第一拍清掉已删文件，第二拍读新文件
                Action clearDay = () =>
                {
                    try { if (Directory.Exists(day)) Directory.Delete(day, true); } catch { }
                    Directory.CreateDirectory(day);
                };

                // ---- D1：同一个会话的两个文件（compact 前后）⇒ 状态必须**合并成一条** ----
                clearDay();
                string s1 = Guid.NewGuid().ToString("D");
                string a1 = Rollout(day, DateTime.Now.AddMinutes(-4), s1);
                string a2 = Rollout(day, DateTime.Now.AddMinutes(-3), s1, Guid.NewGuid().ToString("D"));
                WriteLines(a1, new[]
                {
                    // ⚠ 悬空：这一轮**没有** task_complete（compact 把它留在了旧文件里）——
                    //   按文件记状态时，它会让读数里多出一条永不消失的假「进行中」。
                    Line(DateTime.UtcNow.AddSeconds(-200), 1, "task_started", "a1"),
                });
                WriteLines(a2, new[]
                {
                    Line(DateTime.UtcNow.AddSeconds(-100), 2, "task_started", "a2"),
                    Line(DateTime.UtcNow.AddSeconds(-5),   3, "task_complete", "a2"),
                });
                Touch(a1, DateTime.UtcNow.AddSeconds(-200));
                Touch(a2, DateTime.UtcNow.AddSeconds(-5));
                notes.Add("D1 会话 " + s1 + " ＝ " + Path.GetFileName(a1) + " ＋ " + Path.GetFileName(a2));

                string h1 = settle();
                rows.Add(Row("agenttimer.D1.merged_one_line",
                    "同会话两个文件 ⇒ Codex 读数 " + CountLines(h1, "Codex") + " 条（期望 1）＝ " + Show(h1),
                    CountLines(h1, "Codex") == 1));
                rows.Add(Row("agenttimer.D1.no_false_running",
                    "旧文件里悬空的 task_started 不得报「进行中」⇒ 读数 = " + Show(h1),
                    h1 != null && h1.Contains("耗时") && !h1.Contains("进行中")));

                // ---- D2：`turn_aborted` 必须算结束 ----
                clearDay();
                string s2 = Guid.NewGuid().ToString("D");
                string b1 = Rollout(day, DateTime.Now.AddMinutes(-2), s2);
                WriteLines(b1, new[]
                {
                    Line(DateTime.UtcNow.AddSeconds(-100), 1, "task_started", "b1"),
                    Line(DateTime.UtcNow.AddSeconds(-5),   2, "turn_aborted",  "b1"),
                });
                Touch(b1, DateTime.UtcNow.AddSeconds(-5));
                notes.Add("D2 会话 " + s2 + " ＝ " + Path.GetFileName(b1) + "（task_started + turn_aborted）");

                string h2 = settle();
                rows.Add(Row("agenttimer.D2.abort_counts_as_end",
                    "`turn_aborted` 之后不得再报「进行中」⇒ 读数 = " + Show(h2),
                    h2 != null && h2.Contains("耗时") && !h2.Contains("进行中")));

                // ---- D3：悬空 start 在**已冻住**的旧文件、活动已挪到新文件 ⇒ 必须仍报「进行中」----
                // ⚠ 旧文件冻了 25 分钟（> NoWriteSec(Codex) 900 s）：只盯它的话这次任务已经「不算活着」了。
                clearDay();
                string s3 = Guid.NewGuid().ToString("D");
                string c1 = Rollout(day, DateTime.Now.AddMinutes(-30), s3);
                string c2 = Rollout(day, DateTime.Now.AddMinutes(-29), s3, Guid.NewGuid().ToString("D"));
                WriteLines(c1, new[]
                {
                    Line(DateTime.UtcNow.AddSeconds(-1500), 1, "task_started", "c1"),
                });
                WriteLines(c2, new[]
                {
                    // 非边界行：它的作用只是「这个文件还在被写」这个事实（+ 最新 mtime）。
                    Line(DateTime.UtcNow.AddSeconds(-2), 2, "token_count", "c1"),
                });
                Touch(c1, DateTime.UtcNow.AddSeconds(-1500));
                Touch(c2, DateTime.UtcNow.AddSeconds(-2));
                notes.Add("D3 会话 " + s3 + " ＝ " + Path.GetFileName(c1) + "（冻住的旧文件）＋ "
                          + Path.GetFileName(c2) + "（还在写的新文件）");

                string h3 = settle();
                rows.Add(Row("agenttimer.D3.new_file_keeps_alive",
                    "会话的活动已挪到新文件、旧文件冻了 25 分钟 ⇒ 仍须报「进行中」＝ " + Show(h3),
                    h3 != null && h3.Contains("进行中")));

                // ---- D4：**单文件**的 mtime 冻住、但文件还在增长 ⇒ 必须仍报「进行中」----
                // ⚠⚠ 现场（2026-10-02 用户报「codex 在运行但桌宠不显示」）：Codex 的 rollout 是**长句柄追加**，
                //   NTFS 的 LastWriteTime 在句柄关闭前不刷新 —— 实测文件内容时间戳已到 17:36、长度还在涨，
                //   mtime 却一直停在创建时刻 17:17:37 ⇒ `silent` 被算成 20 分钟 > NoWriteSec(900)，
                //   正在跑的任务被判死、读数整条消失。
                //   ⚠ D3 盖不住它：D3 靠的是**另一个**新文件的 mtime；这里是**同一个**文件既冻着 mtime 又在长。
                clearDay();
                string s4 = Guid.NewGuid().ToString("D");
                string d1 = Rollout(day, DateTime.Now.AddMinutes(-20), s4);
                WriteLines(d1, new[]
                {
                    Line(DateTime.UtcNow.AddSeconds(-1200), 1, "task_started", "d1"),
                });
                Touch(d1, DateTime.UtcNow.AddSeconds(-1200));    // mtime 冻在 20 分钟前（句柄没关，不刷新）
                settle();                                         // 先接入：此刻按 mtime 判，是「已死」
                WriteLines(d1, new[]                             // 之后文件**继续增长**（这一轮还在跑）
                {
                    Line(DateTime.UtcNow.AddSeconds(-1200), 1, "task_started", "d1"),
                    Line(DateTime.UtcNow.AddSeconds(-2),    2, "token_count",  "d1"),
                });
                Touch(d1, DateTime.UtcNow.AddSeconds(-1200));    // ⚠ mtime 仍冻着 —— 这正是要复现的现场
                notes.Add("D4 会话 " + s4 + " ＝ " + Path.GetFileName(d1) + "（mtime 冻在 20 分钟前，但文件在长）");

                string h4c = settle();
                rows.Add(Row("agenttimer.D4.frozen_mtime_still_grows",
                    "单文件 mtime 冻住、但文件还在增长 ⇒ 仍须报「进行中」＝ " + Show(h4c),
                    h4c != null && h4c.Contains("进行中")));
            }
            catch (Exception ex)
            {
                rows.Add(Row("agenttimer.D.exception", ex.GetType().Name + ": " + ex.Message, false));
            }
            finally
            {
                // ⚠ 必须还原：A/B/C 三组要用它们自己的临时根目录（同一个静态字段，两个落点）。
                AgentTaskTimer.CodexRootOverride = null;
                AgentTaskTimer.TraeRootOverride = null;
                AgentTaskTimer.WorkBuddyRootOverride = null;
                AgentTaskTimer.WorkBuddySessionsOverride = null;
            }
            if (oldCodex) notes.Add("⚠ 负对照 --old-codex 已开：D 组**必须**全红，否则本组没有区分度");
        }

        // ================================================================ E 组：WorkBuddy 的「日志静默 ↔ 真心跳」
        //
        // 用户报（2026-09-29）：「我的 WorkBuddy 正在运行，但桌宠没有时间读数」。
        // 现场（实测）：会话日志 16:36:08 开始第三轮后，最后一行停在 16:41:46，此后 21 分钟一字未写；
        // 但状态机是 `working`、`sessions\233860.json` 的 `lastHeartbeat` 一直在跳。
        // 而存活判据只看「日志最近有没有被写」（NoWriteSec(WorkBuddy)=900 s）⇒ 正在跑的任务被判死。
        // 修法：WorkBuddy 额外走 `sessions\*.json` 的**真心跳**兜底（日志判据原样保留）。
        //
        // 两段，缺一段就证明不了：
        //   E1 日志静默 21 分钟、心跳新鲜 ⇒ **必须仍报「进行中」**（用户看到的那个现场）。
        //   E2 同一份日志、心跳也停 ⇒ **必须消失**。⚠ 没有 E2，E1 的绿可能只是因为
        //     我把读数焊死在屏幕上 —— 那不是需求，是新 bug。
        private static void RunWorkBuddyHeartbeat(List<string[]> rows, List<string> notes)
        {
            string root = Path.Combine(Path.GetTempPath(), "azhu-wbbeat");
            try
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
                string logsRoot = Path.Combine(root, "logs");
                string conv = Path.Combine(logsRoot, DateTime.Now.ToString("yyyy-MM-dd"),
                                           "sdk", "conversations");
                string sessRoot = Path.Combine(root, "sessions");
                Directory.CreateDirectory(conv);
                Directory.CreateDirectory(sessRoot);

                AgentTaskTimer.WorkBuddyRootOverride = logsRoot;
                AgentTaskTimer.WorkBuddySessionsOverride = sessRoot;
                AgentTaskTimer.TraeRootOverride = Path.Combine(root, "no-trae");
                AgentTaskTimer.CodexRootOverride = Path.Combine(root, "no-codex");

                string sid = Guid.NewGuid().ToString("D");
                string log = Path.Combine(conv, sid + ".log");
                WriteLines(log, new[]
                {
                    // 「上一轮已结束」＋「本轮已开始」⇒ 正在跑（口径同 A/B/C 的合成 Codex 日志）。
                    WbLine(DateTime.UtcNow.AddMinutes(-25), "TURN_COMPLETED"),
                    WbLine(DateTime.UtcNow.AddMinutes(-21), "PROMPT_SENT"),
                });
                // ⚠ 日志静默 21 分钟：远超 NoWriteSec(WorkBuddy)=900 s —— 光看日志这次任务已经「不算活着」了。
                Touch(log, DateTime.UtcNow.AddMinutes(-21));
                notes.Add("E 组合成 WorkBuddy 会话日志 = " + log + "（静默 21 分钟）");

                string hb = Path.Combine(sessRoot, "233860.json");
                var t = new AgentTaskTimer();
                double clock = 0;
                Func<string> header = () => { clock += 6; t.Poll(clock); return t.Header; };
                Func<string> settle = () => { header(); return header(); };

                // ---- E0：**基线** —— 日志刚被写过（不靠心跳）也必须报「进行中」----
                // ⚠ 没有这一条，E1 红了就分不清是「心跳没生效」还是「WorkBuddy 的发现/边界整条就不通」。
                Touch(log, DateTime.UtcNow);
                string h0 = settle();
                rows.Add(Row("agenttimer.E0.fresh_log_baseline",
                    "日志刚被写过（无心跳文件）⇒ 基线必须报「进行中」＝ " + Show(h0),
                    h0 != null && h0.Contains("进行中")));

                // ---- E1：心跳新鲜 ⇒ 日志静默也必须报「进行中」----
                Touch(log, DateTime.UtcNow.AddMinutes(-21));
                WriteBeat(hb, sid, DateTime.UtcNow);
                string h1 = settle();
                rows.Add(Row("agenttimer.E1.silent_log_live_beat",
                    "会话日志静默 21 分钟、但心跳新鲜 ⇒ 仍须报「进行中」＝ " + Show(h1),
                    h1 != null && h1.Contains("进行中")));

                // ---- E2：同一份日志，心跳也停 ⇒ 必须消失（否则 E1 的绿没有区分度）----
                WriteBeat(hb, sid, DateTime.UtcNow.AddMinutes(-30));
                Touch(hb, DateTime.UtcNow.AddMinutes(-30));
                string h2 = settle();
                rows.Add(Row("agenttimer.E2.stale_beat_drops",
                    "同一份日志、心跳也停 30 分钟 ⇒ 必须消失 ＝ " + Show(h2),
                    h2 == null || !h2.Contains("进行中")));

                // ---- E3：**回归** —— 正在跑的一轮，其**上一轮结束在 50 分钟前** ⇒ 状态不许被清理逻辑拔掉 ----
                // ⚠ 实测现场（2026-09-29）：16:36:08 开始第三轮，上一轮 `TURN_COMPLETED` 在 16:33:48
                //   （50 分钟前）。`Locate` 里的清理按 `EndMs`（＝**上一轮**的结束时刻）判「已结束且过期」，
                //   于是把**正在跑**的任务连根拔掉 ⇒ 读数整条消失。
                // ⚠ 这一步**故意不开心跳、日志也写新鲜**：把心跳兜底整个排除在外，
                //   剩下的变量只有那条清理 —— 它绿了才能证明清理逻辑本身不再碰正在跑的任务
                //   （E1 的绿则可能是心跳兜底盖住了同一个病）。
                WriteLines(log, new[]
                {
                    WbLine(DateTime.UtcNow.AddMinutes(-56), "TURN_COMPLETED"),
                    WbLine(DateTime.UtcNow.AddMinutes(-50), "PROMPT_SENT"),
                });
                Touch(log, DateTime.UtcNow);                       // 日志新鲜 ⇒ 存活判据必过
                try { File.Delete(hb); } catch { }                 // 无心跳文件 ⇒ 不许靠兜底
                string h3 = settle();
                rows.Add(Row("agenttimer.E3.long_turn_prev_end_old",
                    "正在跑的一轮、上一轮 50 分钟前结束（日志新鲜、无心跳）⇒ 仍须报「进行中」＝ " + Show(h3),
                    h3 != null && h3.Contains("进行中")));

                // ⚠⚠ 进 E4 之前必须把前几步的日志**拨旧**：`Header` 是**多行**的（所有活任务拼成一段），
                //   留着 E3 那条「进行中」（它没有心跳文件，只靠日志新鲜）会让 E4/E5 的
                //   `Contains("进行中")` 永远为真 ⇒ **假绿变假红**，判据失去区分度。
                Touch(log, DateTime.UtcNow.AddMinutes(-60));

                // ---- E4：心跳续命**有上限**（＝用户 2026-09-29 报的「一直显示进行中」）----
                // ⚠ 现场：会话日志撞了 ~10 MiB 封顶后**永久**不再落新事件（连结束标记也没有了），
                //   而心跳只要客户端开着就在跳 ⇒ 没有上限的兜底会把这条假读数一直挂着（实测挂满 StaleSec 的 2 小时）。
                //   取 60 分钟：> WbBeatMaxSilenceSec(30) 且 < StaleSec(2h) ⇒ 它的消失**只能**由「上限」解释。
                string sid4 = Guid.NewGuid().ToString("D");
                string log4 = Path.Combine(conv, sid4 + ".log");
                WriteLines(log4, new[]
                {
                    WbLine(DateTime.UtcNow.AddMinutes(-70), "TURN_COMPLETED"),
                    WbLine(DateTime.UtcNow.AddMinutes(-60), "PROMPT_SENT"),
                });
                Touch(log4, DateTime.UtcNow.AddMinutes(-60));
                string hb4 = Path.Combine(sessRoot, "400001.json");
                WriteBeat(hb4, sid4, DateTime.UtcNow);       // 心跳新鲜（进程活着），但日志已静默 60 分钟
                string h4 = settle();
                rows.Add(Row("agenttimer.E4.beat_window_expires",
                    "日志静默 60 分钟、心跳新鲜 ⇒ 不许再报「进行中」（心跳续命有 30 分钟上限）＝ " + Show(h4),
                    h4 == null || !h4.Contains("进行中")));

                // ---- E5：**封顶的大日志**不再靠心跳续命 ----
                // ⚠ 与 E1 的**唯一差异是文件大小**（两边都是「静默 20 分钟上下 ＋ 心跳新鲜」）
                //   ⇒ E1 天然就是这一条的反向对照：小日志必须报「进行中」，大日志必须不报。
                string sid5 = Guid.NewGuid().ToString("D");
                string log5 = Path.Combine(conv, sid5 + ".log");
                WriteBigWbLog(log5, 8.5, new[]
                {
                    WbLine(DateTime.UtcNow.AddMinutes(-30), "TURN_COMPLETED"),
                    WbLine(DateTime.UtcNow.AddMinutes(-20), "PROMPT_SENT"),
                });
                Touch(log5, DateTime.UtcNow.AddMinutes(-20));
                string hb5 = Path.Combine(sessRoot, "500001.json");
                WriteBeat(hb5, sid5, DateTime.UtcNow);
                string h5 = settle();
                rows.Add(Row("agenttimer.E5.capped_log_no_beat",
                    "日志已 ≥8 MiB（会话日志封顶在 ~10 MiB）＋静默 20 分钟＋心跳新鲜 ⇒ 不许报「进行中」＝ " + Show(h5),
                    h5 == null || !h5.Contains("进行中")));

                // ---- E6：封顶日志 ＋ 心跳**已停** ＋ 静默**不足 900 s** ⇒ 必须消失（＝用户 2026-10-02 报）----
                // ⚠ 与 E5 的差别：E5 的心跳是**新鲜**的、静默 20 分钟；E6 的心跳**已停**、静默只有 10 分钟。
                //   后者才是用户报的现场：会话真的没了（心跳停），可 `silent <= NoWriteSec(900)` 还在续命。
                //   ⚠ 没有这一条，E5 全绿也盖不住「900 s 窗口内的假进行中」—— 那正是本次要修的那段。
                // ⚠⚠ 先清场：`Header` 是**多行**的（所有活任务拼成一段），E4/E5 的日志留着会让 E6 读到
                //   **别人的**「进行中」（负对照 `--no-beat-cap` 下尤其明显）⇒ E6 红得不是地方。
                foreach (string f in Directory.GetFiles(conv, "*.log")) { try { File.Delete(f); } catch { } }
                string sid6 = Guid.NewGuid().ToString("D");
                string log6 = Path.Combine(conv, sid6 + ".log");
                WriteBigWbLog(log6, 8.5, new[]
                {
                    WbLine(DateTime.UtcNow.AddMinutes(-40), "TURN_COMPLETED"),
                    WbLine(DateTime.UtcNow.AddMinutes(-10), "PROMPT_SENT"),
                });
                Touch(log6, DateTime.UtcNow.AddMinutes(-10));
                string hb6 = Path.Combine(sessRoot, "600001.json");
                WriteBeat(hb6, sid6, DateTime.UtcNow.AddMinutes(-30));
                Touch(hb6, DateTime.UtcNow.AddMinutes(-30));   // 心跳也停（会话真的没了）
                string h6 = settle();
                rows.Add(Row("agenttimer.E6.capped_stale_beat_short_silence",
                    "封顶日志 ＋ 心跳停 30 分钟 ＋ 静默仅 10 分钟（< 900 s）⇒ 不许报「进行中」＝ " + Show(h6),
                    h6 == null || !h6.Contains("进行中")));

                // ---- E7：**正对照** —— 封顶日志刚被写过 ⇒ 必须仍报「进行中」----
                // ⚠ 没有这一条，「封顶日志一律判死」也能让 E5/E6 全绿 —— 那不是需求，是新 bug：
                //   一轮**正在跑**的封顶日志（tool_call_update 密到按秒写、mtime 新鲜）会被误杀。
                string sid7 = Guid.NewGuid().ToString("D");
                string log7 = Path.Combine(conv, sid7 + ".log");
                WriteBigWbLog(log7, 8.5, new[]
                {
                    WbLine(DateTime.UtcNow.AddMinutes(-40), "TURN_COMPLETED"),
                    WbLine(DateTime.UtcNow.AddSeconds(-2), "PROMPT_SENT"),
                });
                Touch(log7, DateTime.UtcNow);                  // 刚写过 ⇒ 这一轮真在跑
                string h7 = settle();
                rows.Add(Row("agenttimer.E7.capped_fresh_log_alive",
                    "封顶日志**刚被写过**（无心跳）⇒ 仍须报「进行中」＝ " + Show(h7),
                    h7 != null && h7.Contains("进行中")));
            }
            catch (Exception ex)
            {
                rows.Add(Row("agenttimer.E.exception", ex.GetType().Name + ": " + ex.Message, false));
            }
            finally
            {
                AgentTaskTimer.WorkBuddyRootOverride = null;
                AgentTaskTimer.WorkBuddySessionsOverride = null;
                AgentTaskTimer.TraeRootOverride = null;
                AgentTaskTimer.CodexRootOverride = null;
            }
        }

        // ---- G 组：WorkBuddy 的**工作区状态机日志**（主信号）----
        //
        // 现场（2026-10-02 用户报「workbuddy 的任务计时显示消失了」）：会话日志撞上 ~10 MiB 封顶后
        //   **永久**不再落 `PROMPT_SENT`/`TURN_COMPLETED`，mtime 冻在封顶那一刻 ⇒ 会话日志这条通路
        //   再也读不到新边界，读数整条消失。而同一会话的**工作区状态机日志**
        //   （`logs\<日期>\<工作区>__<hash>.log`）一直在写、**不封顶**，里面有
        //   `event=RUN_PREPARING`（一轮开始）与 `event=AGENT_ENDED`（一轮结束）。
        // 修法：把工作区日志当**主信号**，会话日志那对边界串**原样保留**当兜底（见 AgentTaskTimer）。
        //
        // 三条，缺一条就证明不了：
        //   G3 同一个会话的两份文件都有边界 ⇒ 读数**只能有一条**（证明两会话来源按会话号并成一份）。
        //      ⚠ 没有 G3，「并成一份」这件事一路静默 —— 用户会看到两条一模一样的读数。
        //   G1 会话日志已封顶且冻住、工作区日志里这一轮正跑着 ⇒ **必须报「进行中」**（用户看到的现场）。
        //      ⚠ 负对照 `--no-wb-sm` 下必须**红** —— 否则 G1 的绿可能只是会话日志那条老通路碰巧通了。
        //   G2 工作区日志补一条 `AGENT_ENDED` ⇒ 必须**换成「耗时」**（证明结束信号也接到了）。
        private static void RunWorkBuddyStateMachine(List<string[]> rows, List<string> notes)
        {
            string root = Path.Combine(Path.GetTempPath(), "azhu-wbsm");
            try
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
                string logsRoot = Path.Combine(root, "logs");
                string dayDir = Path.Combine(logsRoot, DateTime.Now.ToString("yyyy-MM-dd"));
                string conv = Path.Combine(dayDir, "sdk", "conversations");
                string sessRoot = Path.Combine(root, "sessions");
                Directory.CreateDirectory(conv);
                Directory.CreateDirectory(sessRoot);

                AgentTaskTimer.WorkBuddyRootOverride = logsRoot;
                AgentTaskTimer.WorkBuddySessionsOverride = sessRoot;
                AgentTaskTimer.TraeRootOverride = Path.Combine(root, "no-trae");
                AgentTaskTimer.CodexRootOverride = Path.Combine(root, "no-codex");

                string sid = Guid.NewGuid().ToString("D");

                // 会话日志：**小而新鲜** —— 它自己那条通路是通的（PROMPT_SENT 在 19 分钟前）。
                string convLog = Path.Combine(conv, sid + ".log");
                WriteLines(convLog, new[]
                {
                    WbLine(DateTime.UtcNow.AddMinutes(-40), "TURN_COMPLETED"),
                    WbLine(DateTime.UtcNow.AddMinutes(-19), "PROMPT_SENT"),
                });
                Touch(convLog, DateTime.UtcNow);

                // 工作区状态机日志：同一会话，**不封顶**，这一轮正跑着（20 分钟前开始）。
                string wsLog = Path.Combine(dayDir, "SecondBrain__f82ebd5c5aa8c6909d1ac1ef755e9f56.log");
                WriteLines(wsLog, new[]
                {
                    WsLine(DateTime.Now.AddMinutes(-45), sid, "AGENT_ENDED"),    // 上一轮结束
                    WsLine(DateTime.Now.AddMinutes(-20), sid, "RUN_PREPARING"),  // 本轮开始
                    WsLine(DateTime.Now.AddMinutes(-19), sid, "RUN_ACCEPTED"),
                    WsLine(DateTime.Now.AddMinutes(-18), sid, "AGENT_STARTED"),
                    WsLine(DateTime.Now.AddMinutes(-1),  sid, "MODEL_STREAM_STARTED"),
                });
                Touch(wsLog, DateTime.UtcNow);        // 工作区日志新鲜 ⇒ 存活判据靠它过（**故意不写心跳**）
                notes.Add("G 组合成工作区状态机日志 = " + wsLog);
                if (AgentTaskTimer.NoWbSm) notes.Add("⚠ 负对照 --no-wb-sm 已开：G1/G2 **必须**变红，否则本组没有区分度");

                var t = new AgentTaskTimer();
                double clock = 0;
                Func<string> header = () => { clock += 6; t.Poll(clock); return t.Header; };
                Func<string> settle = () => { header(); return header(); };

                // ---- G3：同一会话的两份文件都有边界 ⇒ 读数**只能有一条**----
                // ⚠ 会话日志那条边界本来就是同一轮的**兜底**，若没按会话号并成一份，这里会冒出两条。
                string h3 = settle();
                rows.Add(Row("agenttimer.G3.wb_sm_merges_sources",
                    "同一会话的会话日志＋工作区日志都有边界 ⇒ 读数只许一条「进行中」＝ " + Show(h3),
                    CountLines(h3, "进行中") == 1));

                // ---- G1：把会话日志换成**封顶且冻住**的 ⇒ 会话日志这条通路瞎了，只靠工作区日志 ----
                WriteBigWbLog(convLog, 8.5, new[]
                {
                    WbLine(DateTime.UtcNow.AddMinutes(-40), "TURN_COMPLETED"),
                    WbLine(DateTime.UtcNow.AddMinutes(-30), "PROMPT_SENT"),
                });
                Touch(convLog, DateTime.UtcNow.AddMinutes(-30));   // 封顶 ＋ mtime 冻在 30 分钟前
                string h1 = settle();
                rows.Add(Row("agenttimer.G1.wb_sm_overrides_capped_session",
                    "会话日志封顶冻住、工作区状态机日志里本轮在跑 ⇒ 仍须报「进行中」＝ " + Show(h1),
                    h1 != null && h1.Contains("进行中")));

                // ---- G2：工作区日志补 `AGENT_ENDED` ⇒ 必须换成「耗时」----
                File.AppendAllText(wsLog, WsLine(DateTime.Now, sid, "AGENT_ENDED") + "\n", new UTF8Encoding(false));
                Touch(wsLog, DateTime.UtcNow);
                string h2 = settle();
                rows.Add(Row("agenttimer.G2.wb_sm_end_closes_run",
                    "工作区日志补 `AGENT_ENDED` ⇒ 必须换成「耗时」＝ " + Show(h2),
                    h2 != null && h2.Contains("耗时") && !h2.Contains("进行中")));
            }
            catch (Exception ex)
            {
                rows.Add(Row("agenttimer.G.exception", ex.GetType().Name + ": " + ex.Message, false));
            }
            finally
            {
                AgentTaskTimer.WorkBuddyRootOverride = null;
                AgentTaskTimer.WorkBuddySessionsOverride = null;
                AgentTaskTimer.TraeRootOverride = null;
                AgentTaskTimer.CodexRootOverride = null;
            }
        }

        /// <summary>合成一行 WorkBuddy 工作区状态机日志。⚠ 形状照抄真实：
        /// `[<本地时间>] [Info] [pid=…] [SessionRunStateMachine] transition | sessionId=… | event=… | …`
        /// —— 时间戳是**方括号里的本地时间、月/日不补零**（`2026/10/2`），与会话日志的 ISO(UTC) 不同。</summary>
        private static string WsLine(DateTime local, string sessionId, string ev)
        {
            return "[" + local.ToString("yyyy/M/d H:mm:ss.fff", CultureInfo.InvariantCulture) + "]"
                 + " [Info] [pid=53888] [SessionRunStateMachine] transition | sessionId=" + sessionId
                 + " | event=" + ev + " | from=idle | to=preparing | lifecycle=preparing"
                 + " | busy=true | queueBusy=false | elapsedSinceLastTransitionMs=1";
        }

        /// <summary>合成一行 WorkBuddy 会话日志。⚠ 形状照抄真实：`<ISO> state-machine:transition
        /// {"input":"PROMPT_SENT"}` —— `Stamp` 取「第一个空格前」的 token 当时间戳，
        /// 边界串则是整段 `"input":"PROMPT_SENT"` / `"input":"TURN_COMPLETED"`。</summary>
        private static string WbLine(DateTime utc, string input)
        {
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")
                 + " state-machine:transition {\"instanceId\":\"ci-1\",\"input\":\""
                 + input + "\",\"valid\":true}";
        }

        /// <summary>合成一个 WorkBuddy 心跳文件（`sessions\<pid>.json`）。
        /// ⚠ `lastHeartbeat` 是 Unix 毫秒数 —— 存活判据读的就是它。</summary>
        private static void WriteBeat(string path, string sessionId, DateTime utc)
        {
            long ms = new DateTimeOffset(utc).ToUnixTimeMilliseconds();
            File.WriteAllText(path,
                "{\n  \"pid\": 233860,\n  \"lastHeartbeat\": " + ms
                + ",\n  \"sessionId\": \"" + sessionId + "\"\n}\n",
                new UTF8Encoding(false));
        }

        /// <summary>造一个「已经封顶」的 WorkBuddy 会话日志：先把文件撑到 <paramref name="mb"/> MB 以上
        /// （填充行**不含任何边界串**，否则会污染 Prime 的回扫），再把那对边界放到**尾部**
        /// —— Prime 是从尾巴往回扫的，这样第一块就能命中。
        /// ⚠ 真实封顶点实测 ≈ 10 MiB（见 AgentTaskTimer.WbLogBigBytes）；造 8.5 MB 足以越过「够大」的门槛。</summary>
        private static void WriteBigWbLog(string path, double mb, string[] tailLines)
        {
            var sb = new StringBuilder();
            string pad = "2026-01-01T00:00:00.000Z filler:noop {\"pad\":\"" + new string('x', 200) + "\"}\n";
            long target = (long)(mb * 1024 * 1024);
            while (sb.Length < target) sb.Append(pad);
            foreach (string l in tailLines) sb.Append(l).Append('\n');
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>合成一个 rollout 文件名。⚠ 会话号必须是**带连字符的 UUID**：`AgentTaskTimer`
        /// 按文件名形状认会话号（见 `CodexSessRe`），无连字符的 32 位串它认不出来。
        /// <paramref name="sub"/> 非空时模拟 compact／续写另开的那个文件（下划线后接新的 rollout id）。</summary>
        private static string Rollout(string dayDir, DateTime local, string sess, string sub = null)
        {
            return Path.Combine(dayDir,
                "rollout-" + local.ToString("yyyy-MM-dd'T'HH-mm-ss") + "-" + sess
                + (sub == null ? "" : "_" + sub) + ".jsonl");
        }

        private static void WriteLines(string path, string[] lines)
        {
            File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }

        /// <summary>把文件的最后写入时间拨到指定时刻 —— 「这个文件多久没被写了」是存活判据的唯一输入。</summary>
        private static void Touch(string path, DateTime utc) { File.SetLastWriteTimeUtc(path, utc); }

        private static int CountLines(string h, string word)
        {
            if (h == null) return 0;
            int n = 0;
            foreach (string s in h.Split('\n')) if (s.Contains(word)) n++;
            return n;
        }

        /// <summary>气泡窗的屏幕矩形（物理像素）—— 用来分辨「不可见」是「被收起」还是「被摆到屏幕外」。</summary>
        private static string Rect(PetWindow w)
        {
            int[] r = w.BubbleRectPx();
            if (r == null || r.Length < 4) return "(无)";
            if (r[0] == 0 && r[1] == 0 && r[2] == 0 && r[3] == 0) return "(未落定)";
            return "(" + r[0] + "," + r[1] + " " + r[2] + "x" + r[3] + ")";
        }

        // ---- 合成日志：形状照抄真实 rollout（`"timestamp":"…Z","type":"event_msg","payload":{"type":"…"}`）----
        private static string Line(DateTime utc, int ordinal, string type, string turn)
        {
            return "{\"timestamp\":\"" + utc.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff") + "Z\""
                 + ",\"ordinal\":" + ordinal
                 + ",\"type\":\"event_msg\",\"payload\":{\"type\":\"" + type + "\",\"turn_id\":\"" + turn + "\"}}";
        }
        private static void WriteAll(string[] lines)
        {
            File.WriteAllText(_codexFile, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        }
        private static void Append(string line)
        {
            File.AppendAllText(_codexFile, line + "\n", new UTF8Encoding(false));
        }

        private static void Done(List<string[]> rows, List<string> notes, Cli o, PetWindow w, Application app)
        {
            // 收尾必须显式 Shutdown：`app.Run()` 要靠它才返回（漏了会「跑完判据却永不退出」）。
            try { LiftTest.Report(o, rows, notes); } catch { }
            try { w.Close(); } catch { }
            app.Shutdown();
        }

        private static void MoveCursorTo(PetWindow w, Point pDip)
        {
            Native.RECT r;
            Native.GetWindowRect(w.Handle, out r);
            Native.SetCursorPos(r.Left + (int)Math.Round(pDip.X * w.DipScale),
                                r.Top + (int)Math.Round(pDip.Y * w.DipScale));
        }
        private static void Down() { Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero); }
        private static void Up() { Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero); }
        private static void Nudge(int dx, int dy)
        {
            Native.POINT c;
            if (Native.GetCursorPos(out c)) Native.SetCursorPos(c.X + dx, c.Y + dy);
        }
    }
}