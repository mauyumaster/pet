// 「握住」与「拎起」的**端到端**区分验证 —— 「按住不动不该转圈」这件事只能量、不能看。
//
// 背景（2026-09-27 用户报）：
//   「我单纯点击桌宠，如果我鼠标按压的时间长一点就会让桌宠转圈。我认为我们需要加一个判定逻辑：
//     仅有当桌宠被拎起来时才触发转圈，当桌宠站在地上时不触发转圈、仅跳跃。」
//
// 病因（读代码能定位，但**必须量出来才算验完**）：
//   ① `OnDown` 从前一按下就 `Pose.Dragging = true` ＋ `Pose.SpinAccel = ±A`，而
//      `Pose.Step` 的 `Dragging` 分支是 `_spinVel += SpinAccel * dt`（**线性升速、不衰减**）
//      ⇒ 只要鼠标按着不动，角速度就一路往上爬 ⇒ 她自转。
//   ② 更隐蔽的一处：`SlowTick`（120 ms 一拍）里写着 `if (_dragging) Pose.Dragging = true;`，
//      会把 `OnDown` 刚设的 false **覆盖回 true** ⇒ 只改 OnDown 不改这里，bug 会从慢路径复发。
//      ⚠ 这一处**只有当「按住不动」持续足够久（跨过多拍 SlowTick）时才暴露** —— 所以 A 组
//        必须真的按住 2 秒（≥16 拍），按 200 ms 是量不出来的。
//
// 三个必须**分别**成立的实验（缺一个就证明不了需求）：
//   A 按住不动 2 秒 → **不该转**：`Pose.Dragging` 恒 false（连续观测）、`LiftCount == 0`、|Δyaw| ≈ 0。
//   B 点一下（位移 ~0）→ **只跳、不转**：`LiftCount == 0` 且 `Pose.Lift` 出现跳跃包络。
//   C 真的拖起来 → **要转**：`LiftCount == 1` 且 |Δyaw| 显著 > 0。
//     ⚠ 没有 C，「A/B 通过」可能只是因为我把转圈整个禁掉了 —— 那不是需求，是新 bug。
//   D 拎起转圈后松手、落地 → **必须停转**（2026-09-27 用户报的第二个 bug：落地后一直转）。
//     ⚠ D 与 A 是**两条不同的通路**，别合并：A 管「握住但没拎起」时 `SlowTick` 不该点火；
//       D 管「拎起过、松手了」时 `SlowTick` 不该把 `Pose.Dragging` 重新点上火。
//       修 A 的那一行（`_dragging && _lifted`）只是**顺带**给 D 上了保险，D 自己的病根在
//       `OnUp` 漏清 `_lifted` ⇒ 两个负对照（--old-lift-gate / --no-lift-clear）必须各红各的。
//
// 观测口径（两个坑，都在这个项目栽过）：
//   · `Yaw` 差分 ≠ 角速度：非拖拽时她走**回正贝塞尔**，`CursorYaw`（光标跟随）会合法地改变 Yaw。
//     ⇒ 判「转没转」**不能看 |Δyaw| 小不小**（回正会让它非零），要看**单调累计转角**是否超过回正的额度。
//     本测试里「她动不动」的唯一判据是 `Pose.Dragging` + `LiftCount`，`Σ|Δyaw|` 只作量化佐证。
//   · 采样必须**连续**（挂 `CompositionTarget.Rendering`），不能只读首尾两帧 ——
//     `SlowTick` 是 120 ms 一拍的脉冲式覆盖，首尾两点采不到它。
//
// 负对照（两个，各守一条通路）：
//   `--old-lift-gate`   ⇒ `SlowTick` 回到上一版的 `if (_dragging)`。必须让 **A 组变红、C 组仍绿**。
//   `--no-lift-clear`   ⇒ `OnUp` 跳过清 `_lifted`。必须让 **D 组变红、A/B/C 仍绿**。
//   ⚠ 只有这样才能证明「A 量到的**就是**那条覆盖通路」「D 量到的**就是**那条残留通路」。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AzhuPet
{
    internal static class LiftTest
    {
        const int StepPx = 14;      // 每步位移（物理像素），**故意大于** LiftThresholdPx=8
                                    //   （判定看「相对按下点的累计位移」，2 步 × 14 必过）
        const int Steps = 6;        // 拖拽分几步
        const double HoldSec = 2.0; // A 组「按住不动」的时长（要跨过 ≥16 拍 SlowTick）

        private static string P(bool ok) { return ok ? "PASS" : "FAIL"; }
        private static string[] Row(string name, string val, bool ok) { return new[] { name, val, P(ok) }; }
        private static string[] Row(string name, string val) { return new[] { name, val, "PASS" }; }

        public static int Run(Cli o)
        {
            var rows = new List<string[]>();
            var notes = new List<string>();
            string modelPath = Cli.ResolveModel(o.ModelPath);
            if (modelPath == null) { Console.Error.WriteLine("找不到模型"); return 1; }
            var m = Glb.Load(modelPath);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // ⚠ 三个自由度必须先钉死（同 --motiontest 的纪律），否则量到的不是「转不转」：
            //   ShowTray=false  托盘会开独立消息泵；
            //   SpeechOn=false  `SayLine` 里有 `Pose.TriggerJump()` ⇒ 她自发说话会污染 B 组
            //                   「点一下 = 跳跃」的判据（这是真实发生过的假红）；
            //   DozeAfter 推远  打盹会改呼吸节奏与姿态基线。
            var cfg = new PetConfig
            {
                SizeIndex = o.SizeIndex, NightDim = false, ShowTray = false, SpeechOn = false
            };
            var r = new WpfPetRenderer(m);
            var w = new PetWindow(r, cfg) { SelfTestMode = true, ForcedIdle = -1 };
            w.Pose.DozeAfter = 1e9;
            w.Pose.SleepAfter = 2e9;
            w.OldLiftGate = o.OldLiftGate;      // 负对照：SlowTick 回到 `if (_dragging)`
            w.NoLiftClear = o.NoLiftClear;      // 负对照：OnUp 跳过清 `_lifted`（让 D 组变红）
            w.LiftTrace = new List<string>();   // 诊断：把每次拎起判定的明细记下来
            w.Show();

            var clock = Stopwatch.StartNew();
            var tl = new DispatcherTimer(DispatcherPriority.Normal);
            tl.Interval = TimeSpan.FromMilliseconds(40);

            // ---- 连续采样（挂 CompositionTarget.Rendering，与 --motiontest 同口径）----
            int phase = 0;                       // 0=空转 1=A 按住不动 2=B 点击 3=C 拖拽
            int dragFrames = 0;                  // A 窗内 `Pose.Dragging == true` 的采样帧数
            int aFrames = 0;
            int aLift0 = 0;                      // A 开始时的 LiftCount 基线
            int aFirstLiftFrame = -1;            // A 窗内第几帧进入「拎起」（-1 = 没有）
            double aLiftTop = 0;                 // 进入拎起那一刻的窗口 Top（看它是否在动）
            double aTopMin = double.MaxValue, aTopMax = double.MinValue;   // A 窗内 Top 的极值
            int aCur0x = 0, aCur0y = 0, aCurMax = 0, aCurMaxY = 0;        // A 窗内光标相对起点的最大位移
            double aSumYaw = 0, aLastYaw = 0;
            double aLiftMax = 0;                 // A 里 Lift 的最大幅度（她不该跳：按住不该触发跳跃）
            bool aSeenLifted = false;
            int bLift0 = 0;
            double bJumpMax = 0, bJumpMin = 0;
            bool bSeenAirborne = false;
            int cLift0 = 0;
            double cSumYaw = 0, cLastYaw = 0;
            bool cSeenDragging = false;
            // ---- D 组：松手落地后必须停转 ----
            double dLastYaw = 0, dSumYaw = 0;      // 落地后观测窗内的累计 |Δyaw|
            double dTailYaw = 0;                   // 观测窗**末段 0.5 s** 的累计 |Δyaw|（回正早该跑完）
            double dLastSpin = 0;                  // 窗末的角速度绝对值（用户口径：「角速度逐渐减小」）
            int dFrames = 0, dDraggingFrames = 0;  // 窗内采样帧数 / `Pose.Dragging` 为 true 的帧数
            int dLiftN = 0;                        // 松手后 LiftCount 又涨了多少（期望 0）
            int dLiftBase = 0;                     // 松手那一刻的 LiftCount 绝对值（D 的基线）
            double tUp = 0, tLand = 0;             // 松手时刻 / 落地停稳时刻

            EventHandler sampler = (s2, e2) =>
            {
                double yaw = w.Pose.LastYaw;
                if (phase == 1)
                {
                    aFrames++;
                    if (w.Top < aTopMin) aTopMin = w.Top;
                    if (w.Top > aTopMax) aTopMax = w.Top;
                    // ⚠ 诊断：量 A 窗内**光标自己**动了多少 —— `OnMove` 的 `moved` 只由光标位移决定
                    //   （`nx - _dragLeft0 = c.X - _dragOrigin.X`），所以「拎起」若在 A 里发生，
                    //   只可能是光标在动（手抖／系统复位／我们自己碰了它）。
                    Native.POINT cp;
                    if (Native.GetCursorPos(out cp))
                    {
                        if (aFrames == 1) { aCur0x = cp.X; aCur0y = cp.Y; }
                        int dx = Math.Abs(cp.X - aCur0x), dy = Math.Abs(cp.Y - aCur0y);
                        if (dx > aCurMax) aCurMax = dx;
                        if (dy > aCurMaxY) aCurMaxY = dy;
                    }
                    if (w.Pose.Dragging) dragFrames++;
                    // ⚠ 诊断：记下「拎起」是在 A 窗的第几帧发生、那一刻窗口在哪 ——
                    //   用来区分「手抖/窗口仍在落定」与「真的有人拖了它」。
                    if (w.Lifted && !aSeenLifted) { aFirstLiftFrame = aFrames; aLiftTop = w.Top; }
                    if (w.Lifted) aSeenLifted = true;
                    double dl = Math.Abs(w.Pose.LastLift);
                    if (dl > aLiftMax) aLiftMax = dl;
                    if (aFrames > 1) aSumYaw += Math.Abs(yaw - aLastYaw);
                    aLastYaw = yaw;
                }
                else if (phase == 2)
                {
                    // 跳跃叠加 = Lift 减去呼吸分量（`LastJumpLift` 就是「除呼吸外的竖直叠加」）
                    double e = w.Pose.LastJumpLift;
                    if (e > bJumpMax) bJumpMax = e;
                    if (e < bJumpMin) bJumpMin = e;
                    if (w.Airborne) bSeenAirborne = true;
                }
                else if (phase == 3)
                {
                    if (w.Pose.Dragging) cSeenDragging = true;
                    cSumYaw += Math.Abs(yaw - cLastYaw);
                    cLastYaw = yaw;
                }
            };

            int step = 0, sub = 0;
            int atX = (o.At != null && o.At.Length == 2) ? o.At[0] : 900;
            Point hitPt = new Point();
            bool hitFound = false;
            double tA0 = 0, tB0 = 0, tAbort = 0;
            int aDownBase = 0, aDownN = 0, aLiftN = 0, bDownBase = 0, bDownN = 0, bLiftN = 0, cDownBase = 0, cLiftN = 0;

            tl.Tick += (s, e) =>
            {
                double t = clock.Elapsed.TotalSeconds;
                try
                {
                    switch (step)
                    {
                        case 0:
                            // 摆位口径同 DragTest：屏幕中部 + **贴地**。
                            //   ⚠ 不贴地的话她会先自由落体，`Airborne` 分支也会给角速度
                            //     （`_spinVel += SpinK·SpinDrive·dt`），Δyaw 就不是「拖出来的」了。
                            if (t < 0.8) break;
                            w.Left = atX / w.DipScale;
                            w.Top = SystemParameters.WorkArea.Bottom - r.FeetYDip;
                            step = 1;
                            break;

                        case 1:
                            if (t < 1.6) break;
                            for (double fy = 0.45; fy < 0.80 && !hitFound; fy += 0.02)
                            {
                                var p = new Point(w.ActualWidth * 0.5, w.ActualHeight * fy);
                                if (r.HitTest(p)) { hitPt = p; hitFound = true; }
                            }
                            if (!hitFound) { rows.Add(Row("lift.hit_point_found", "未找到", false)); Done(rows, notes, o, w, app); step = 99; break; }
                            rows.Add(Row("lift.hit_point_found", hitPt.X.ToString("0.#") + "," + hitPt.Y.ToString("0.#"), true));
                            CompositionTarget.Rendering += sampler;
                            MoveCursorTo(w, hitPt);
                            tAbort = t;
                            step = 20;                     // 先把光标放好、让它稳定，再按
                            break;

                        // 稳定拍：光标已到位，等窗口把这次 MouseMove 消化掉再 Down
                        case 20:
                            if (t - tAbort < 0.25) break;
                            MoveCursorTo(w, hitPt);            // 再钉一次（窗口可能刚落定）
                            tAbort = t;
                            step = 21;
                            break;

                        case 21:
                            if (t - tAbort < 0.25) break;
                            aDownBase = w.DownCount;
                            step = 2;
                            break;

                        // ---------------- A：按住不动 HoldSec 秒 ----------------
                        // ⚠⚠ 本判据的核心。这 2 秒跨过 ≥16 拍 SlowTick：
                        //   若 SlowTick 那行还是 `if (_dragging)`，`Pose.Dragging` 会在这儿被点上火，
                        //   角速度一路爬到 SpinMaxVel ⇒ dragFrames / Δyaw 双红。
                        case 2:
                            aFrames = 0; dragFrames = 0; aSumYaw = 0; aLiftMax = 0; aSeenLifted = false;
                            aFirstLiftFrame = -1; aTopMin = double.MaxValue; aTopMax = double.MinValue;
                            aCurMax = 0; aCurMaxY = 0;
                            aLastYaw = w.Pose.LastYaw;
                            aLift0 = w.LiftCount;
                            phase = 1;
                            Down();
                            tA0 = t;
                            step = 3;
                            break;

                        case 3:
                            if (t - tA0 < HoldSec) break;
                            aDownN = w.DownCount - aDownBase;      // A 的按下到底送达没有
                            aLiftN = w.LiftCount - aLift0;         // ⚠⚠ 必须在 A 的**结束**采样！
                            phase = 0;
                            step = 4;
                            break;

                        case 4:
                            Up();
                            step = 40;
                            break;

                        // 松手后稳定拍：等 A 的任何后果（若有）落定，再进 B
                        case 40:
                            if (t - tA0 < HoldSec + 1.2) break;
                            MoveCursorTo(w, hitPt);
                            tAbort = t;
                            step = 41;
                            break;

                        case 41:
                            if (t - tAbort < 0.25) break;
                            MoveCursorTo(w, hitPt);
                            tAbort = t;
                            step = 42;
                            break;

                        case 42:
                            if (t - tAbort < 0.25) break;
                            bDownBase = w.DownCount;
                            bLift0 = w.LiftCount;
                            bJumpMax = 0; bJumpMin = 0; bSeenAirborne = false;
                            phase = 2;
                            Down();
                            tB0 = t;
                            step = 6;
                            break;

                        case 6:
                            if (t - tB0 < 0.18) break;      // 按住 180ms —— 比从前的 bug 触发还久
                            bDownN = w.DownCount - bDownBase;
                            bLiftN = w.LiftCount - bLift0;  // ⚠ 必须在 B 的**结束**采样（不然被 C 的拎起污染）
                            Up();                            // 位移 0 ⇒ 走「点一下」分支
                            step = 7;
                            break;

                        case 7:
                            if (t - tB0 < 1.4) break;       // 等跳跃包络（JumpT=0.44s）跑完
                            phase = 0;
                            MoveCursorTo(w, hitPt);
                            tAbort = t;
                            step = 80;
                            break;

                        case 80:
                            if (t - tAbort < 0.25) break;
                            MoveCursorTo(w, hitPt);
                            tAbort = t;
                            step = 81;
                            break;

                        // ---------------- C：真的拖起来 ----------------
                        case 81:
                            if (t - tAbort < 0.25) break;
                            cDownBase = w.DownCount;
                            cLift0 = w.LiftCount;
                            cLastYaw = w.Pose.LastYaw; cSumYaw = 0;
                            cSeenDragging = false;
                            MoveCursorTo(w, hitPt);
                            Native.GetWindowRect(w.Handle, out cRect0);
                            phase = 3;
                            Down();
                            sub = 0;
                            step = 9;
                            break;

                        case 9:
                            // 分步移动：一步一个 tick，避免一次性跳过去只收到一个 MouseMove
                            if (sub >= Steps) { step = 10; break; }
                            Nudge(StepPx, 0);
                            sub++;
                            break;

                        case 10:
                            cLiftN = w.LiftCount - cLift0;  // ⚠ C 的增量在松手前采（松手后走抛物，不再有拎起）
                            // ⚠⚠ D 组的基线必须在**这里**采：C 那段真的把她拎起来转了，`_spinVel`
                            //   此刻是满速。松手后她走 `Airborne` 分支（有 `Math.Exp(-SpinDecay*dt)` 衰减），
                            //   落地停稳后落进 `else` 回正分支（`_spinVel = 0`）。D 要验的正是「落地之后」
                            //   那一段——所以基线现在采、观测窗在落地之后再开。
                            dSumYaw = 0; dFrames = 0; dDraggingFrames = 0; dTailYaw = 0; dLastSpin = 0;
                            dLastYaw = w.Pose.LastYaw;
                            // ⚠⚠ `dLiftBase` 必须用「松手那一刻的绝对值」，不能用 C 组的 `cLift0`
                            //   （那是 C 的起点，`w.LiftCount - cLift0` 到 D 结束时会把 **C 那次合法的
                            //   拎起**也算进来，恒红一条 —— 首跑就是这么红的）。
                            dLiftBase = w.LiftCount;
                            // ⚠⚠ 关掉 C 的采样相位**必须在这里**：sampler 里 `phase==3` 持续往
                            //   `cSumYaw` 累加，不关的话松手后的抛物/落地转动会被算进 **C 的判据**、
                            //   把 `lift.C.sum_yaw > 45°` 白送成一个假绿（C 的观测窗就该在松手那刻结束）。
                            phase = 0;
                            Up();
                            tUp = t;                       // 松手时刻（用于超时保护）
                            step = 12;
                            break;

                        // ---------------- D：松手落地后 ⇒ 必须停转 ----------------
                        // ⚠⚠ 本组是**用户 2026-09-27 报的第二个 bug** 的判据：
                        //   「拎起转圈后松手、她落回地上，但转圈没有停止，松手后角速度逐渐减小的逻辑没了」。
                        //   病因：`OnUp` 只清 `_dragging` 没清 `_lifted`，而 `SlowTick`（120 ms 一拍）
                        //   按 `_lifted` 写 `Pose.Dragging` ⇒ 松手后 ≤120 ms 内它被写回 true ⇒
                        //   `Pose.Step` 落回 **Dragging 分支（不衰减）**，而 `SpinAccel` 已清 0
                        //   ⇒ 角速度**原值恒定保持**，永远转下去。
                        //   ⇒ 本组必须在**修好之前**变红：判据是落地后那段的 `Σ|Δyaw|` 仍很大。
                        //   ⚠ 观测窗要等「落地停稳」再开 —— 自由落体那段的转动是**合法的**
                        //     （`Airborne` 分支本来就该转、且有 `Exp(-SpinDecay·dt)` 衰减），算进来会误伤。
                        case 12:
                            // 等她落地停稳：`w.Airborne` 由 true 变 false（StepMotion 里停稳后置 false）。
                            // ⚠ 超时保护（4 s）：万一她卡在墙上/贴边没停下，也让测试继续、别挂死。
                            if (w.Airborne && t - tUp < 4.0) break;
                            // ⚠ 落地那一刻**重采 yaw 基线**：从 `case 10` 松手到这里隔了整段落地等待，
                            //   不重采的话 `case 13` 第一次累加会把「落地期间」的转动一次性算进观测窗
                            //   （Airborne 那段的转动是**合法的**，且有衰减），造成一次虚假跳变。
                            dLastYaw = w.Pose.LastYaw;
                            dSumYaw = 0;
                            tLand = t;
                            step = 13;
                            break;

                        case 13:
                            // 观测窗：落地后 **1.2 秒**。
                            // ⚠⚠ 判据**不能**只看整窗的 `Σ|Δyaw|`：落地后她走**回正贝塞尔**，
                            //   而拎起时已经转了好几圈（C 组实测 504°），回正要把 `_yaw` 从 8+ rad
                            //   拉回 `CursorYaw` ⇒ 那一趟贝塞尔本身就有**几百度的合法转角**
                            //   （`_retDur` 上限 0.55 s）。拿整窗累计去判「停没停」会把回正误判成"还在转"。
                            //   ⇒ 拆成两个各自干净的判据：
                            //     ① **窗末**（最后 0.5 s）的 `Σ|Δyaw|` —— 回正早该跑完，这里必须≈0；
                            //     ② 窗末的**角速度** `LastSpinVel` —— 用户原话「角速度逐渐减小」，它必须≈0。
                            //        ⚠ 这里读 `LastSpinVel` **不算"拿实现的输出去验实现"**：
                            //          被测对象就是「角速度会不会衰减」，它是**被测的量**本身，
                            //          （与 `spin.wire.*` 那类"不许壳侧碰 `_spinVel`"是两回事）。
                            double dy = Math.Abs(w.Pose.LastYaw - dLastYaw);
                            dSumYaw += dy;
                            dLastYaw = w.Pose.LastYaw;
                            if (t - tLand > 1.2 - 0.5) dTailYaw += dy;      // 末段 0.5 s
                            dLastSpin = Math.Abs(w.Pose.LastSpinVel);
                            if (w.Pose.Dragging) dDraggingFrames++;
                            dFrames++;
                            if (t - tLand < 1.2) break;
                            dLiftN = w.LiftCount - dLiftBase;   // ⚠ 基线是**松手那一刻**的绝对值（见 case 10）
                            step = 11;
                            break;

                        case 11:
                            phase = 0;
                            Native.GetWindowRect(w.Handle, out cRect1);
                            CompositionTarget.Rendering -= sampler;
                            tl.Stop();
                            Analyze();
                            // ⚠⚠ 收尾必须显式 Shutdown：`app.Run()` 要靠 Shutdown 才返回。
                            //   第一版漏了这一步，跑完判据却永不退出 —— 表现是「测试挂了 3 分钟没输出」，
                            //   而真正的原因只是**少关一次门**（判据本身其实已经跑完并打印了）。
                            Done(rows, notes, o, w, app);
                            step = 99;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    rows.Add(Row("lift.exception", ex.GetType().Name + ": " + ex.Message, false));
                    try { rows.Add(Row("lift.exception_stack", ex.StackTrace ?? "", true)); } catch { }
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
                int aMoved = (cRect1.Left - cRect0.Left);
                rows.Add(Row("lift.samples", "A " + aFrames + " 帧 / " + HoldSec.ToString("0.#") + "s"
                    + "（跨 ≥" + (int)(HoldSec * 1000 / 120) + " 拍 SlowTick）", aFrames > 60));
                // ⚠⚠ 前置闸：三个实验各按了一次，若 `DownCount` 没涨，说明鼠标事件**压根没送到**，
                //   后面所有判据都是「没量到东西」而不是「量到了好东西」——
                //   那种绿是**没有信息量的绿**（本仓原则：一个从没红过的测试，其绿没有信息量）。
                //   实测踩过：第一次跑 A 全绿，其实只是 Down 没送达，B/C 全「绿」在 0 上。
                rows.Add(Row("lift.down_delivered", "A=" + aDownN + " B=" + bDownN
                    + " C=" + (w.DownCount - cDownBase) + " 次（三段都必须 ≥1）",
                    aDownN >= 1 && bDownN >= 1 && (w.DownCount - cDownBase) >= 1));

                // ---- A 组：按住不动 ⇒ 不转 ----
                // ① `Pose.Dragging` 必须**全程 false**。这是最直接的判据：它一为 true，
                //    `Pose.Step` 的 `_spinVel += SpinAccel·dt` 就开始爬。
                rows.Add(Row("lift.A.dragging_frames", dragFrames + " / " + aFrames + " 帧为 true（期望 0）",
                    dragFrames == 0));
                // ② 从未进入「拎起」态。
                rows.Add(Row("lift.A.seen_lifted", aSeenLifted ? "是" : "否（期望否）", !aSeenLifted));
                // ③ `LiftCount` 没涨。
                //    ⚠⚠ 用 `aLiftN`（A 窗**结束时**采的增量），**不是** `w.LiftCount - aLift0`。
                //      后者在最后才求值，会被 C 那次**合法**的拎起污染 —— 第一版就是这么误报红的：
                //      A 明明全程 dragging=0、光标没动，却报 lift_count=1，追下去才发现是变量求值时机错了。
                //      （教训：**增量必须在窗口内采样**，`w.X - 窗口起点 X` 这种写法只在窗口内成立。）
                rows.Add(Row("lift.A.lift_count", aLiftN + "（期望 0）", aLiftN == 0));
                rows.Add(Row("lift.A.lift_diag", "第 " + aFirstLiftFrame + " 帧进入拎起，Top∈["
                    + aTopMin.ToString("0.#") + "," + aTopMax.ToString("0.#") + "] 跨度="
                    + (aTopMax - aTopMin).ToString("0.##") + " DIP；光标在 A 窗内最多动了 dx="
                    + aCurMax + " dy=" + aCurMaxY + " px"));
                // 诊断：把 OnMove 的拎起判定明细打出来（只看前若干条，别刷屏）
                // ⚠ 条数放到 12：C 组分 6 步走，加上 A/B 的重钉拍，4 条根本看不到「松手后还有没有 OnMove」
                //   —— 而 D 组 `no_relift` 若为红，恰恰要的就是这个证据。
                if (w.LiftTrace != null && w.LiftTrace.Count > 0)
                {
                    int n = Math.Min(w.LiftTrace.Count, 12);
                    for (int i = 0; i < n; i++) rows.Add(Row("lift.trace" + i, w.LiftTrace[i]));
                }
                // ④ 累计转角 ≈ 0。⚠ 只作佐证：非拖拽时有回正贝塞尔，`Σ|Δyaw|` 天然非零，
                //    门限取「真转起来会达到的量级」的一小部分，用它拦住「转了好几圈」那种量级。
                //    ⚠ 不要用 `Pose.YawMin/YawMax`（那是 Extremes，从启动累计，含 A 之前的历史）；
                //      这里量的是**A 窗内**的累积，只有连续采样拿得到。
                double spinBudget = w.Pose.SpinMaxVel * HoldSec * 180 / Math.PI;
                rows.Add(Row("lift.A.sum_yaw", "Σ|Δyaw| = " + (aSumYaw * 180 / Math.PI).ToString("0.0")
                    + "°（真转起来会到 " + spinBudget.ToString("0") + "° 量级；仅佐证）",
                    aSumYaw * 180 / Math.PI < spinBudget * 0.10));
                // ⑤「按住」不该触发跳跃（用户原话：站在地上只跳跃 = 点击才跳，不是按住就跳）
                rows.Add(Row("lift.A.lift_amp", "±" + aLiftMax.ToString("0.####") + " m（呼吸 ±0.012）",
                    aLiftMax <= 0.0122));

                // ---- B 组：点一下 ⇒ 只跳不转 ----
                rows.Add(Row("lift.B.lift_count", bLiftN + "（期望 0：点击不是拎起）", bLiftN == 0));
                double bJump = Math.Max(bJumpMax, -bJumpMin);
                rows.Add(Row("lift.B.jump_amp", "±" + bJump.ToString("0.####") + " m（跳跃峰值 JumpLift=0.060）",
                    bJump > 0.02));                      // 必须真的跳起来，否则「只是没跳」冒充「只跳不转」
                rows.Add(Row("lift.B.not_airborne", bSeenAirborne ? "出现腾空" : "无腾空（期望无）", !bSeenAirborne));

                // ---- C 组：真的拖起来 ⇒ 要转 ----
                rows.Add(Row("lift.C.lift_count", cLiftN + "（期望 1：拖过阈值才拎起）", cLiftN == 1));
                rows.Add(Row("lift.C.seen_dragging", cSeenDragging ? "是（期望是）" : "否", cSeenDragging));
                rows.Add(Row("lift.C.moved_px", "A " + aMoved + "px（C 段窗口应有位移）", aMoved >= StepPx * 2));
                rows.Add(Row("lift.C.sum_yaw", "Σ|Δyaw| = " + (cSumYaw * 180 / Math.PI).ToString("0.0")
                    + "°（拎起后应显著转动）", cSumYaw * 180 / Math.PI > 45));

                // ---- D 组：松手落地后**必须停转**（2026-09-27 用户报的第二个 bug）----
                //   ⚠⚠ 本组必须**在修好之前变红**。判据的判别力来自「落地后 1.2 s 内累计转角」：
                //     · 修好：落地即落进 `else` 回正分支（`_spinVel = 0`），只剩回正贝塞尔的**一次性**
                //       小角度（把满速遗留的 yaw 拉回 CursorYaw，通常 < 几十度，且是单调收敛的）。
                //     · 未修：`Pose.Dragging` 被 SlowTick 写回 true ⇒ 不衰减的 Dragging 分支
                //       ⇒ 1.2 s × 12 rad/s ≈ 13.7 rad ≈ **787°** 量级（实际会更多，因为不衰减一直转）。
                //   ⇒ 门限取「真转起来会到的一小部分」，两边差一个数量级。
                double dBudget = w.Pose.SpinMaxVel * 1.2 * 180 / Math.PI;   // 若满速转满 1.2 s 的度数
                // ⚠ 门限按 tick 算：观测窗 1.2 s、tick 40 ms ⇒ 名义 30 帧，实测 26（落地等待占了开头）。
                //   取 20 留足余量（门限贴着实测值会变成一条**机器越慢越红**的假红判据）。
                rows.Add(Row("lift.D.settle_frames", dFrames + " 帧落地后采样（1.2 s ÷ 40ms ≈ 30 帧）", dFrames >= 20));
                rows.Add(Row("lift.D.dragging_frames", dDraggingFrames + " / " + dFrames
                    + " 帧 `Pose.Dragging` 仍为 true（期望 0：松手后不该再算拖拽）", dDraggingFrames == 0));
                // ① 整窗累计只作**观察窗**（回正贝塞尔本身有几百度的合法转角，见 case 13 的注释）。
                rows.Add(Row("lift.D.sum_yaw", "落地后 Σ|Δyaw| = " + (dSumYaw * 180 / Math.PI).ToString("0.0")
                    + "°（含回正那趟的合法大转角，若不停转会到 " + dBudget.ToString("0") + "° 量级；仅观察）"));
                // ② 判别性的那一条：**窗末 0.5 s** 还在不在转。
                //   ⚠ 门限 60°：回正上限 0.55 s，末段 0.5 s 内它早该收敛完（只剩零星抖动）；
                //     而不衰减的 bug 版在这 0.5 s 会转 12×0.5 ≈ 6 rad ≈ **344°**。差着 5 倍以上。
                rows.Add(Row("lift.D.tail_yaw", "落地后**最后 0.5 s** 的 Σ|Δyaw| = "
                    + (dTailYaw * 180 / Math.PI).ToString("0.0") + "°（期望 < 60°；不停转会到 ~344°）",
                    dTailYaw * 180 / Math.PI < 60));
                // ③ 最贴合用户原话的一条：「松手后角速度逐渐减小」⇒ 窗末角速度必须 ≈ 0。
                rows.Add(Row("lift.D.end_spinvel", "窗末 |角速度| = " + dLastSpin.ToString("0.###")
                    + " rad/s（期望 ≈0；拎起满速 SpinMaxVel = " + w.Pose.SpinMaxVel.ToString("0.#") + "）",
                    dLastSpin < 0.5));
                rows.Add(Row("lift.D.no_relift", "松手后 LiftCount 又涨了 " + dLiftN + " 次（期望 0）", dLiftN == 0));
            }
        }

        private static Native.RECT cRect0 = new Native.RECT(), cRect1 = new Native.RECT();

        private static void Done(List<string[]> rows, List<string> notes, Cli o, PetWindow w, Application app)
        {
            Report(o, rows, notes);
            try { w.Close(); } catch { }
            app.Shutdown();
        }

        private static Point MoveCursorTo(PetWindow w, Point pDip)
        {
            Native.RECT r;
            Native.GetWindowRect(w.Handle, out r);
            int px = r.Left + (int)Math.Round(pDip.X * w.DipScale);
            int py = r.Top + (int)Math.Round(pDip.Y * w.DipScale);
            Native.SetCursorPos(px, py);
            return new Point(px, py);
        }

        private static void Down() { Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero); }
        private static void Up() { Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero); }
        private static void Nudge(int dx, int dy)
        {
            Native.POINT c;
            if (Native.GetCursorPos(out c)) Native.SetCursorPos(c.X + dx, c.Y + dy);
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
