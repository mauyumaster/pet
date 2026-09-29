// 竖直运动量测 —— 「**无交互**时她为什么会上下跳」这个问题，只能量、不能看。
//
// 背景（2026-09-26 用户报）：有时候不做任何操作，桌宠会**高频上下跳动，然后逐渐变慢直到停止**。
//
// 这个描述有两个可判定的成分，必须分开量，因为它们的数学形态完全不同：
//   ① 「上下跳动」= `Top`（窗口位置）或 `Lift`（渲染内浮沉）出现了周期性的竖直往复；
//   ② 「逐渐变慢直到停止」= 要么**幅度**单调衰减（阻尼），要么**周期**单调变长。
//      ⚠ 这两者指向完全不同的病因，光看描述分不出来 —— 所以判据得把「幅度序列」和
//      「过零点间隔序列」都记下来，让人能从数字上读出是哪一种。
//
// 为什么不能靠读代码下结论：
//   - 物理侧 `StepMotion` 只在 `_airborne` 时跑，而 `_airborne = true` 全项目**只有一处**
//     （`OnUp` 松手）。⇒ 纯推理会得出「无交互不可能上下跳」的结论。
//   - 姿态侧 `Pose.Step` 的每个包络（跳 0.44 s / 挤压 0.30 s）都是**定时长**的，
//     也不产生「逐渐变慢」。
//   ⇒ 两条推理都说不通，正说明我漏掉了某条通路。这种时候唯一的出路是**把序列打出来**。
//
// 判据（都能红）：
//   A 组「无交互时窗口不该动」：全程 `_airborne` 必须恒 false；`Top` 的单帧位移必须 ≈ 0。
//   B 组「无交互时姿态不该突变」：`Lift` 的单帧位移相对**呼吸基线的单帧步长**不得超阈值。
//   C 组「记录形态」：把 `Lift` 的过零点间隔与峰谷幅度打成一行，人来读是哪种衰减。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AzhuPet
{
    internal static class MotionTest
    {
        private static string[] Row(string name, string val, bool ok)
        {
            return new[] { name, val, ok ? "PASS" : "FAIL" };
        }
        private static string[] Row(string name, string val)
        {
            return new[] { name, val, "PASS" };     // 陈述事实的行，不参与判定
        }

        // 一帧的采样
        private struct Sample
        {
            public double T;        // 秒（与 OnRender 同一个时钟基准的表达，见下）
            public double Top;      // 窗口 Top（DIP）
            public double Lift;     // Pose.Lift（米）
            public double Breathe;  // Lift 的呼吸分量（米）
            public double Extra;    // Lift 的其余分量（跳跃等，米）
            public bool Airborne;
            public double Dt;       // 本帧采样间隔（秒）—— dt 抖动的直接证据
        }

        public static int Run(Cli o)
        {
            var rows = new List<string[]>();
            var notes = new List<string>();

            string modelPath = Cli.ResolveModel(o.ModelPath);
            if (modelPath == null) { Console.Error.WriteLine("找不到模型"); return 1; }
            var m = Glb.Load(modelPath);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // ⚠ ShowTray=false：托盘会开独立的消息泵与菜单，与「量她自己的运动」无关，能少一个变量就少一个
            // ⚠⚠ SpeechOn=false 是**必须的**，不是可选优化：`SayLine` 里有
            //   `if (!Pose.Dragging && !Pose.Airborne) Pose.TriggerJump();`（「她开口也雀跃一下」，
            //   2026-09-20 的需求）。⇒ 只要表达环在自发说话，`Lift` 就会被叠上 `JumpLift = 0.060 m`，
            //   于是本判据的 `extra_amp_observed`（要求 ±0）与 `lift_max_frame_step` 必然报红 ——
            //   而那是**合法行为**，不是故障（第一版实测 37 帧非零、叠加 ±0.06 m，就是她自己在说话）。
            //   ⚠ 这与「把打盹推到极远」是同一条纪律：**先把与被测量无关的自由度钉死**。
            //     否则量到的不是「有没有 bug」，而是「她这会儿爱不爱说话」。
            var cfg = new PetConfig
            {
                SizeIndex = o.SizeIndex, NightDim = false, ShowTray = false, SpeechOn = false
            };
            var r = new WpfPetRenderer(m);
            // ⚠ ForcedIdle = -1 ⇒ **不许**因为「空闲」而打盹/入睡。
            //   本判据要量的是「完全静止、清醒、无交互」这一档；打盹会改呼吸节奏，
            //   那是合法行为，混进来会让判据的形态分析读错。
            var w = new PetWindow(r, cfg) { SelfTestMode = true, ForcedIdle = -1 };
            // ⚠⚠ 把打盹/入睡**推到极远**：本判据量的是「清醒、静止、无交互」这一档。
            //   不锁的话会被机器真实空闲时长带跑 —— 实测本机空闲 >180 s ⇒ `DozeK` 冲到 1
            //   ⇒ `breatheAmp = 0.012 × 1.45 = 0.0174`，呼吸幅合法地变大 45%，
            //   而那条「呼吸不许超 0.012」的判据就会**误报红**。
            //   （这正是一次真实的假红：打盹是合法行为，不该被当成故障。判据必须先把
            //     与被测量的东西无关的自由度钉死，否则量到的不是「有没有 bug」而是「机器忙不忙」。）
            w.Pose.DozeAfter = 1e9;
            w.Pose.SleepAfter = 2e9;
            w.Show();

            var samples = new List<Sample>();
            var clock = Stopwatch.StartNew();
            double t0 = 0;
            double dur = o.Seconds;                 // 复用 --seconds，默认 7 ⇒ 命令行给 --seconds 25
            bool started = false;
            long airTrueFrames = 0, jumpLiftFrames = 0;
            double maxTopStep = 0, maxLiftStep = 0;

            // 采样器：挂在 CompositionTarget.Rendering 上、**在 PetWindow.StartLoop 之后注册**
            //   ⇒ 同一帧里它排在 OnRender 后面，读到的就是本帧更新后的 Top/Lift。
            // ⚠ 不能改用 DispatcherTimer：那是另开一拍，与渲染不同拍，采到的 Top 是「某一瞬间」的，
            //   会把「帧内先改 Top 再画」拆成两帧的假跳变。
            int lastRendered = 0;
            double lastRenderT = 0;
            var renderDts = new List<double>();     // 真正的「渲染帧间隔」序列
            var animDts = new List<double>();       // 真正的「交给动画的 dt」序列（恒定性判据用）
            EventHandler sampler = null;
            sampler = (s2, e2) =>
            {
                if (!started) return;
                double t = clock.Elapsed.TotalSeconds;
                // ⚠⚠ 只认**真的渲染了的那一拍**：`RenderedFrames` 是 PetWindow 里
                //   「通过限帧闸门并画了一帧」的计数器（`OnRender` 里 ++）。
                //   靠 `Dt >= 阈值` 去猜哪一拍渲染了是不行的 —— 同一渲染帧内夹着的
                //   中间拍，其间隔可能也大于半个帧预算，会把它们混进来，得到的
                //   「间隔比」是采样节拍的比，不是渲染节拍的比（第一版就是这么误报红的）。
                bool rendered = w.RenderedFrames != lastRendered;
                if (rendered)
                {
                    if (lastRendered != 0)
                    {
                        double rd = t - lastRenderT;
                        if (rd < 0.5) renderDts.Add(rd);
                    }
                    lastRenderT = t;
                    lastRendered = w.RenderedFrames;
                    animDts.Add(w.LastAnimDt);      // 本帧真正交给动画的步长
                }
                if (samples.Count > 0)
                {
                    var prev = samples[samples.Count - 1];
                    double dTop = Math.Abs(w.Top - prev.Top);
                    double dLift = Math.Abs(w.Pose.LastLift - prev.Lift);
                    if (dTop > maxTopStep) maxTopStep = dTop;
                    if (dLift > maxLiftStep) maxLiftStep = dLift;
                }
                if (w.Airborne) { airTrueFrames++; }
                if (w.Pose.LastJumpLift != 0) jumpLiftFrames++;
                samples.Add(new Sample {
                    T = t, Top = w.Top,
                    Lift = w.Pose.LastLift,
                    Breathe = w.Pose.LastBreatheLift,
                    Extra = w.Pose.LastJumpLift,
                    Airborne = w.Airborne,
                    Dt = t - (samples.Count > 0 ? samples[samples.Count - 1].T : t)
                });
            };

            var tl = new DispatcherTimer(DispatcherPriority.Normal);
            tl.Interval = TimeSpan.FromMilliseconds(100);
            tl.Tick += (s2, e2) =>
            {
                double t = clock.Elapsed.TotalSeconds;
                if (!started)
                {
                    if (t < 1.5) return;                 // 等首帧布局与初始摆放落定
                    w.GoHome();                          // 摆到右下角、脚踩工作区底边
                    started = true;
                    t0 = t;
                    CompositionTarget.Rendering += sampler;
                    return;
                }
                if (t - t0 < dur) return;

                CompositionTarget.Rendering -= sampler;
                tl.Stop();

                // ---------------------------------------------------------- 分析
                var ss = samples;
                rows.Add(Row("motion.samples", ss.Count + " 帧 / " + dur.ToString("0.#") + "s", ss.Count > 60));

                // ---- A 组：无交互时窗口不该动 ----
                // ⚠⚠ 这是本判据的**核心**。`_airborne` 唯一置 true 的地方是 `OnUp`，
                //   而本测试全程不产生任何鼠标事件 ⇒ 只要它为 true，就是**存在一条我们不知道的通路**
                //   把窗口置成了腾空 —— 那正是「无交互却上下跳」的机制性证据。
                rows.Add(Row("motion.airborne_frames", airTrueFrames + " 帧为腾空",
                    airTrueFrames == 0));
                // 单帧窗口位移：停稳时应当是**恰好 0**（StepMotion 直接 return，没人写 Top）。
                // 给 0.5 DIP 的容差是留给 DPI/吸附这类合法的一次性调整，不是留给周期性运动的。
                rows.Add(Row("motion.top_max_frame_step", maxTopStep.ToString("0.####") + " DIP",
                    maxTopStep < 0.5));

                // ---- B 组：姿态不该突变 ----
                // ⚠ 前提：`SpeechOn=false`（见上方注释）—— 不然她自发说话时的 `TriggerJump`
                //   会合法地叠上 0.060 m，这两条必然报红。
                // 基线：呼吸在 60 Hz 下的单帧步长上界 = Amp·sin'(max)·dt = Amp·(2π/T)·dt
                //   Amp=0.012 m，T=3.4 s，dt=1/60 ⇒ 0.00037 m（与 --selftest 的幅度口径同源）。
                // 取 3× 作门限：正常呼吸到不了，而「相位跳变」一类的突变会一次就到 10~60×。
                double baseline = 0.012 * (2 * Math.PI / 3.4) / 60.0;
                double ratio = maxLiftStep / baseline;
                rows.Add(Row("motion.lift_max_frame_step",
                    maxLiftStep.ToString("0.######") + " m = 呼吸基线的 " + ratio.ToString("0.0") + "×",
                    ratio < 3.0));

                // ---- C 组：把形态打出来（陈述行，不参与判定）----
                rows.Add(Row("motion.lift_extremes",
                    "min=" + MinLift(ss).ToString("0.#####") + " max=" + MaxLift(ss).ToString("0.#####")
                    + " (呼吸幅 ±0.012m)"));
                rows.Add(Row("motion.lift_zero_crossings", ZeroCrossings(ss).ToString()
                    + " 次过零 / " + dur.ToString("0.#") + "s"));
                rows.Add(Row("motion.top_range",
                    "min=" + MinTop(ss).ToString("0.###") + " max=" + MaxTop(ss).ToString("0.###")
                    + " 跨度=" + (MaxTop(ss) - MinTop(ss)).ToString("0.####") + " DIP"));

                // ---- D 组：把 Lift 拆成「呼吸」与「其余」两份 ----
                // ⚠⚠ 这是定位病因的分水岭：
                //   若超幅出在**呼吸分量**上 ⇒ 病在 `breatheT` 的相位跳变（`_t` 是绝对值，
                //     `breatheT` 一改相位就整体跳，跳量随 `_t` 线性增长）；
                //   若出在**其余分量**上 ⇒ 病在 `TriggerJump` 被反复点火（有东西在无交互时说话/触发）。
                //   两者修法完全不同，所以先拆开、再判定。
                double bMax = 0;
                foreach (var s in ss) bMax = Math.Max(bMax, Math.Abs(s.Breathe));
                double eMax = 0;
                foreach (var s in ss) eMax = Math.Max(eMax, Math.Abs(s.Extra));
                rows.Add(Row("motion.breathe_amp_observed", "±" + bMax.ToString("0.#####") + " m",
                    bMax <= 0.0122));        // 配置 BreatheAmp = 0.012，给 1.7% 容差
                rows.Add(Row("motion.extra_amp_observed", "±" + eMax.ToString("0.#####") + " m"
                    + "（非呼吸叠加，" + jumpLiftFrames + " 帧非零）",
                    eMax < 1e-6));

                // ---- E 组：dt 抖动 ----
                // ⚠ 起因：25 秒那次量到 comp≈93 Hz 而 rendered≈46 Hz＝comp/2 —— 限帧门槛
                //   `1/Fps - 0.0015` 与合成器节拍不整除时，`dt` 会在「通过」与「不通过」之间
                //   交替，且**每帧累加的余数不同** ⇒ `dt` **不规则抖动**。
                //   后果：一切按 dt 积分/推进的量（呼吸相位、抛物线、包络）都跟着抖，
                //   屏幕上就是「一顿一顿」地动。这不是姿态问题，是**主循环的时间基准问题**。
                double dtMin = double.MaxValue, dtMax = 0, dtSum = 0; int dtN = 0;
                foreach (var s in ss)
                {
                    if (s.Dt <= 0) continue;
                    dtMin = Math.Min(dtMin, s.Dt); dtMax = Math.Max(dtMax, s.Dt);
                    dtSum += s.Dt; dtN++;
                }
                double dtAvg = dtN > 0 ? dtSum / dtN : 0;
                rows.Add(Row("motion.frame_dt",
                    "min=" + (dtMin == double.MaxValue ? 0 : dtMin).ToString("0.#####")
                    + " max=" + dtMax.ToString("0.#####")
                    + " avg=" + dtAvg.ToString("0.#####") + " s"
                    + " (名义 1/60=" + (1.0 / 60).ToString("0.#####") + ")"));
                // ⚠⚠ 这两条是上面那个 bug 的**守门人**，必须能红（见 PetWindow.OnRender 的注释）。
                //
                //   ① `frame_dt_jitter`（**陈述行，不参与判定**）：渲染帧之间**间隔**的均匀度。
                //      ⚠ 它为什么不当判据：间隔受合成器拍漂移支配（实测 comp 在 88~100 Hz 之间
                //        一直漂），「2 拍还是 3 拍才渲染一帧」本就不规则 —— 这是**物理事实**，
                //        不是缺陷。实测新写法 2.49×、旧写法 2.57×，**两者几乎一样** ⇒
                //        它区分不出修好没修好，拿它当判据只会长期误报红。
                //        （第一版就是这么栽的：把「间隔比 < 1.35」当判据，结果修复后仍红。）
                //      ⇒ 它保留下来只作**观察窗**：想看限帧在什么节奏上跑，读这一行。
                //   ② `anim_rate`：**动画推进速率**是否 ≈ 1.0×。← **真正的守门人**                //      定义：`Σ(各帧 dt) ÷ Σ(各渲染帧之间的真实间隔)` —— 也就是「动画走过的时间
                //      ÷ 真实走过的时间」。
                //      · = 1.0 ⇒ 动画按真实时间跑，`JumpT = 0.44 s` 就真的是 0.44 s；
                //      · **< 1.0 ⇒ 所有动画整体变慢**（用户说的「跳跃有点太慢」正是这个）；
                //      · > 1.0 ⇒ 整体变快。
                //      ⚠⚠ 这条是 2026-09-26 第二轮才立的，起因是一次**全绿却错着**的修复：
                //        我上一版为了消抖把 `dt` **固定**成 `1/Fps`，而实际渲染只有约 44 帧/秒
                //        ⇒ 速率 = 44 × (1/60) = **0.79×** —— 跳跃 0.44 s 实际跑了 **0.57 s**。
                //        **当时 12 条判据全绿**，因为没有任何一条在量「时间的**尺度**对不对」：
                //        它们只看了「步长均不均匀」，而「**均匀地慢**」它完全看不见。
                //      ⇒ 教训（已写进项目记忆）：**量「稳不稳」的判据，替代不了量「准不准」的判据。**
                //        一个被优化得很平滑的偏差，仍然是偏差。
                double span = 0, dSum = 0;
                foreach (double d in renderDts) span += d;
                foreach (double d in animDts) dSum += d;
                double rate = span > 1e-9 ? dSum / span : 0;
                rows.Add(Row("motion.anim_rate",
                    "Σdt=" + dSum.ToString("0.###") + "s ÷ 真实=" + span.ToString("0.###") + "s = **"
                    + rate.ToString("0.000") + "×**"
                    + "（1.000=不慢不快；据此 JumpT " + "0.44s 实跑 "
                    + (0.44 / (rate > 0 ? rate : 1)).ToString("0.###") + "s）",
                    animDts.Count >= 30 && rate > 0.94 && rate < 1.06));

                // ③ `anim_dt_floor`：交给动画的 `dt` **不许小于一个帧预算**。← 第二条守门人
                //      ⚠⚠ 这条是「两种缺陷」的**分水岭**，必须与 `anim_rate` 一起看：
                //        · `anim_rate` 守的是**时间总量**（总量错 ⇒ 动画整体快/慢）；
                //        · 本条守的是**时间颗粒度**（颗粒度错 ⇒ 动画一顿一顿）。
                //      两者**互相独立**，缺一条就会漏掉一种缺陷。实测过：
                //        「固定步长」写法 → 总量 0.79×（rate 红）但颗粒度完美（本条绿）；
                //        「丢时间」旧写法 → 总量 1.00×（rate **绿**）但颗粒度乱（本条红）。
                //      ⇒ 只留 `anim_rate` 会让负对照全绿（我一度就是这样，差点收工）；
                //        补上本条之后，负对照才有资格红。
                //
                //      机理：累加器通过门槛才渲染 ⇒ `dt` 必然 ≥ 门槛；
                //        而「不达标就 return 且不更新 `_lastT`」的写法会交出**瞬时间隔**
                //        （一拍 0.0106 s，**小于**门槛 0.0152），那就是颗粒度的破口。
                double gate = 1.0 / 60.0 - 0.0015;
                int tooSmall = 0;
                foreach (double d in animDts) if (d > 0 && d < gate - 1e-4) tooSmall++;
                rows.Add(Row("motion.anim_dt_floor",
                    tooSmall + " / " + animDts.Count + " 帧的 dt < 门槛 " + gate.ToString("0.#####") + "s"
                    + "（应为 0 ⇒ dt 从不是「不足一帧」的碎块）",
                    animDts.Count >= 30 && tooSmall == 0));

                // ④ `anim_dt_spread`（**陈述行**）：`dt` 的离散度 —— 想看「2 拍/3 拍混排到什么程度」时读它。
                double aMin2 = double.MaxValue, aMax2 = 0;
                foreach (double d in animDts) { aMin2 = Math.Min(aMin2, d); aMax2 = Math.Max(aMax2, d); }
                rows.Add(Row("motion.anim_dt_spread",
                    animDts.Count + " 帧: min=" + (aMin2 == double.MaxValue ? 0 : aMin2).ToString("0.######")
                    + " max=" + aMax2.ToString("0.######")
                    + " 比值=" + (aMin2 > 0 && aMin2 != double.MaxValue ? (aMax2 / aMin2).ToString("0.00") : "0")
                    + "×（受合成器漂移支配，本行不判定）"));

                // 渲染节流日志：她常驻时 comp/rendered 两条曲线 —— dt 抖动会在这里露出来
                foreach (string line in w.RateLog) notes.Add("rate " + line);

                Report(o, rows, notes);
                try { w.Close(); } catch { }
                app.Shutdown();
            };
            tl.Start();
            app.Run();
            return rows.Exists(x => x[2] == "FAIL") ? 1 : 0;
        }

        private static double MinLift(List<Sample> ss)
        {
            double v = double.MaxValue;
            foreach (var s in ss) if (s.Lift < v) v = s.Lift;
            return ss.Count == 0 ? 0 : v;
        }
        private static double MaxLift(List<Sample> ss)
        {
            double v = double.MinValue;
            foreach (var s in ss) if (s.Lift > v) v = s.Lift;
            return ss.Count == 0 ? 0 : v;
        }
        private static double MinTop(List<Sample> ss)
        {
            double v = double.MaxValue;
            foreach (var s in ss) if (s.Top < v) v = s.Top;
            return ss.Count == 0 ? 0 : v;
        }
        private static double MaxTop(List<Sample> ss)
        {
            double v = double.MinValue;
            foreach (var s in ss) if (s.Top > v) v = s.Top;
            return ss.Count == 0 ? 0 : v;
        }

        /// <summary>数 Lift 序列穿过其均值的次数 —— 频率的粗测。周期越短、次数越多。</summary>
        private static int ZeroCrossings(List<Sample> ss)
        {
            if (ss.Count < 3) return 0;
            double sum = 0;
            foreach (var s in ss) sum += s.Lift;
            double mean = sum / ss.Count;
            int n = 0;
            bool above = ss[0].Lift > mean;
            for (int i = 1; i < ss.Count; i++)
            {
                bool a = ss[i].Lift > mean;
                if (a != above) { n++; above = a; }
            }
            return n;
        }

        internal static void Report(Cli o, List<string[]> rows, List<string> notes)
        {
            var sb = new StringBuilder();
            int fails = 0;
            foreach (string[] row in rows)
            {
                if (row[2] == "FAIL") fails++;
                sb.AppendLine((row[2] == "PASS" ? "[PASS] " : "[FAIL] ") + row[0] + " = " + row[1]);
            }
            foreach (string n in notes) sb.AppendLine("       " + n);
            sb.AppendLine("TOTAL pass=" + (rows.Count - fails) + " fail=" + fails);
            Console.Out.Write(sb.ToString());
            Console.Out.Flush();
            if (o.OutFile != null)
            {
                var j = new StringBuilder("{\n  \"checks\": [\n");
                for (int i = 0; i < rows.Count; i++)
                    j.Append("    {\"name\": \"").Append(rows[i][0]).Append("\", \"value\": \"")
                     .Append(rows[i][1].Replace("\\", "/").Replace("\"", "'")).Append("\", \"result\": \"")
                     .Append(rows[i][2]).Append("\"}").Append(i == rows.Count - 1 ? "\n" : ",\n");
                j.Append("  ]\n}\n");
                try { System.IO.File.WriteAllText(o.OutFile, j.ToString(), new UTF8Encoding(false)); } catch { }
            }
        }
    }
}
