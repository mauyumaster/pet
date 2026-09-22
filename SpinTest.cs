// --spintest：离线验「拎起桌宠」的旋转 —— 固定角加速度 ＋ 角速度上限 ＋ 方向由左右半屏决定。
//
// 为什么不塞进 `--dragtest`：`--dragtest` 是**真鼠标**端到端（真的按下、真的移动光标、真的量窗口位移），
// 它验的是「事件送得到吗」；这里要验的是「算术对不对」。后者必须**离线也能逼红** ——
// 靠真拖拽去量角速度，判据就只能被外部系统验，没有资格失败（纪律页第 11 条）。
//
// 需求逐条对应判据（2026-09-22 用户原话）：
//   ① 「角速度迅速增加的旋转（给定一个固定的加速度）」
//        → B 组：逐帧 Δω 恒等于 A·dt；**并且** A 翻倍则斜率翻倍（否则 A 根本没被读进去）
//        → E 组：「到上限所需时间」落在合理量级（抓「ω 被直接赋值成上限」这种偷懒实现）
//   ② 「限定一个最大角速度」
//        → C 组：稳态 ω 精确等于 SpinMaxVel；**并且**换成别的值稳态跟着变
//        → C3：**去掉上限时 ω 必须远大于它**（这条才是 clamp 的判别力 —— 只验「稳态=上限」的话，
//          一个「把 ω 直接写成上限」的实现在这里也全绿）
//   ③ 「左半边屏幕从右往左转，右边屏幕从左往右转」
//        → A 组：`PoseEngine.SpinDirFor` 纯函数的左右/中线/副屏四档
//        → D 组：yaw 真的**逐帧单调**朝那个方向走（不是只看终点符号）
//   ④ 手感衔接：松手后 `_spinVel` 应**沿用拖拽末刻值**（不能清零、不能跳变）→ F 组
//   ⑤ 回归：旧的「飞行阶段 ∝ 横向速度」口径不能被顺手改坏 → H 组
//   ⑥（同日追加）「跨越屏幕半边时，角速度不进行突变而是让加速度反向」
//        → I 组：换向全程逐帧 Δω 恒等于 ±A·dt（**上界**抓「跳变」、**下界**抓「不加速」）
//        → I 组：带迟滞的方向纯函数（带内不翻 / 越带才翻 / band=0 与 SpinDirFor 逐点等价）
//   ⑦（同日追加）「让角速度上限提高」→ J 组：出厂参数自洽（到顶仍 ≤0.8 秒；上限 ≥10 rad/s）
//   ⑧ 接线：换向的**算术**在 I 组验透了，但「壳侧有没有真的调它」离线够不着
//        → K 组：直接读 `PetWindow.cs` 源码把接线形状钉死（源码不在 ⇒ 打印 [skip]，不静默算过）
//
// ⚠ 怎么逼红（**全部实跑过**，下面是实测结果、不是推测）：
//
// 【上一轮：固定加速度 ＋ 角速度上限】
//   · Dragging 分支改回旧口径（`_spinVel += SpinK*SpinDrive*dt;` ＋ 衰减那两行）：
//     合成输入里 SpinDrive 恒为 0 ⇒ 转速整个不动 ⇒ **11 条红**，恰好是 B2＋C4＋D3＋E1＋F1；
//     而 **A 组（方向纯函数）与 H 组（飞行回归）没被误伤** ⇒ 反证这两组确实独立于 Dragging 分支。
//   · `Math.Clamp(_spinVel + SpinAccel*dt, …)` 换成裸累加：**2 条红**
//     （`steady_equals_maxvel` 得 ω=48=A×3）。
//   ⚠⚠ **必须记住的边界**：`spin.max.discriminates`（C3）在「上限根本没实现」时**反而不红** ——
//     它是拿「有上限」与「无上限」对撞，两边都无上限时就撞不出差别。
//     ⇒ **C3 的判别力只覆盖「有上限但值被写死」，抓「完全没实现」的是 C1。**
//     判据的覆盖面比它名字看起来**窄**，这件事只有真跑负对照才知道 —— 正对照堆再多也看不出来。
//
// 【本轮：跨半屏换向 ＋ 提高上限】
//   · 换向处 `_spinVel = 0`（模拟壳侧越权）：**2 条红** —— `flip.omega_continuous`
//     （max|Δω| = 6.4 ≈ 48×A·dt）、`flip.decel_is_linear`（过零帧数变 0）。
//   · 换向处 `_spinVel = -_spinVel`（更「聪明」的错法）：**2 条红**，同样的两条，
//     max|Δω| = 12.67 ≈ 95×A·dt（取反比清零跳得更大）。
//   · 上限改回 8：**1 条红** —— `defaults.max_vel_raised`（把「提高上限」这个需求固化下来的那条）。
//   · **OnMove 的接线注释掉：首版判据没红！** 两次漏检，两次都打印全绿 ——
//     ① 没剥注释（注释掉的代码仍含该子串）② 剥了注释但**查全文**
//     （`OnDown` 里也有同一句「定初值」，于是匹配得到）。修正后重跑：**1 条红** —— `wire.onmove_flips_on_cross`。
//   · OnDown 接线注释掉 ＋ 壳侧越权写角速度（`_spinVel` 得改成 public 才编得过）：
//     **2 条红** —— `wire.ondown_sets_initial_dir`、`wire.shell_never_touches_omega`。
//   ⚠⚠ **本轮最贵的一课**：源码级判据「看着对」而实际**漏检了两次**，两次的输出都是**全绿** ——
//     只有真跑负对照才能发现。凡是「查文本」的判据，都藏着两个前提：**先剥注释**、**限定范围**。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzhuPet
{
    internal static class SpinTest
    {
        const double Dt = 1.0 / 120;   // 固定步长：逐帧差分要干净，不能靠真实帧率

        public static int Run(Cli o)
        {
            var checks = new List<object>();
            bool ok = true;
            Action<bool, string, string> chk = (pass, name, detail) =>
            {
                checks.Add(new Dictionary<string, object> { ["ok"] = pass, ["name"] = name, ["detail"] = detail });
                if (!pass) ok = false;
            };

            // ================= A 组：方向纯函数（左半屏 / 右半屏）=================
            // 主屏 1920 宽 ⇒ 中线 960。
            chk(PoseEngine.SpinDirFor(100, 0, 1920) == -1,
                "spin.dir.left_half_is_neg", "x=100 中线 960 ⇒ " + PoseEngine.SpinDirFor(100, 0, 1920) + "（期望 -1：从右往左）");
            chk(PoseEngine.SpinDirFor(1800, 0, 1920) == 1,
                "spin.dir.right_half_is_pos", "x=1800 ⇒ " + PoseEngine.SpinDirFor(1800, 0, 1920) + "（期望 +1：从左往右）");
            chk(PoseEngine.SpinDirFor(960, 0, 1920) == 1,
                "spin.dir.exact_midline_is_pos", "x=960 正中线 ⇒ " + PoseEngine.SpinDirFor(960, 0, 1920) + "（边界口径：判右）");
            chk(PoseEngine.SpinDirFor(959.9, 0, 1920) == -1,
                "spin.dir.just_left_of_midline", "x=959.9 ⇒ " + PoseEngine.SpinDirFor(959.9, 0, 1920) + "（期望 -1）");
            // ⚠ 判别力：把「恒返回 +1」或「恒返回 -1」这种退化实现抓出来。
            chk(PoseEngine.SpinDirFor(100, 0, 1920) != PoseEngine.SpinDirFor(1800, 0, 1920),
                "spin.dir.discriminates", "左=" + PoseEngine.SpinDirFor(100, 0, 1920)
                    + " 右=" + PoseEngine.SpinDirFor(1800, 0, 1920) + "（必须不同，否则函数没在区分左右）");
            // 副屏在主屏**左边**（工作区 left=-1920），必须按副屏自己的中点判。
            chk(PoseEngine.SpinDirFor(-1500, -1920, 0) == -1 && PoseEngine.SpinDirFor(-500, -1920, 0) == 1,
                "spin.dir.second_monitor_uses_own_mid",
                "副屏 [-1920,0]：x=-1500 ⇒ " + PoseEngine.SpinDirFor(-1500, -1920, 0)
                    + "，x=-500 ⇒ " + PoseEngine.SpinDirFor(-500, -1920, 0) + "（期望 -1 / +1）");

            // ================= B 组：固定角加速度（逐帧线性）=================
            const double A = 16.0, Max = 8.0;
            var trA = Run(A, 1e9, 0.20, Dt);      // 上限拉到无穷 ⇒ 只看纯加速度段
            double worst = 0, worstAt = 0;
            for (int i = 1; i < trA.Count; i++)
            {
                double dw = trA[i][2] - trA[i - 1][2];      // 相邻帧角速度差
                double dev = Math.Abs(dw - A * Dt);         // 应为 A·dt
                if (dev > worst) { worst = dev; worstAt = trA[i][0]; }
            }
            chk(worst < 1e-6,
                "spin.accel.frame_delta_is_constant_A_dt",
                "max|Δω − A·dt| = " + worst.ToString("0.###e+0") + " @t=" + worstAt.ToString("0.###")
                    + "（" + trA.Count + " 帧，A=" + A + " dt=1/" + (1 / Dt).ToString("0") + "）");

            double w1 = Last(trA), tr1 = Last(Run(A / 2, 1e9, 0.20, Dt));
            double ratio = w1 / tr1;
            chk(Math.Abs(ratio - 2.0) < 0.01,
                "spin.accel.read_from_field",
                "A=" + A + " 与 A=" + (A / 2) + " 的末刻 ω 之比 = " + ratio.ToString("0.0000")
                    + "（期望 2 —— 不为 2 就是加速度没从字段读，而是别处来的常数）");

            // ================= C 组：角速度上限（clamp 生效）=================
            var trC = Run(A, Max, 3.0, Dt);
            double steady = Last(trC);
            chk(Math.Abs(steady - Max) < 1e-9,
                "spin.max.steady_equals_maxvel",
                "跑满 3 秒后 ω = " + steady.ToString("0.000000") + " rad/s（期望 " + Max + "）");

            double tReach = 0;
            for (int i = 0; i < trC.Count; i++)
                if (Math.Abs(trC[i][2]) >= Max * 0.999) { tReach = trC[i][0]; break; }
            chk(tReach > 0 && tReach <= 0.8,
                "spin.max.reached_in_time",
                "首次到 99.9% 上限：" + tReach.ToString("0.000") + " 秒（理论 A/Max = "
                    + (Max / A).ToString("0.000") + " 秒；>0.8 秒就算不上「迅速」）");

            double steady2 = Last(Run(A, 3.0, 3.0, Dt));
            chk(Math.Abs(steady2 - 3.0) < 1e-9,
                "spin.max.read_from_field",
                "把上限改成 3 ⇒ 稳态 ω = " + steady2.ToString("0.000000") + "（期望 3 —— 不为 3 就是上限写死了）");

            // ⚠⚠ 这一条才是 clamp 的判别力。只验「稳态 == 上限」的话，
            //    一个「每帧直接 ω = MaxVel」的实现在 C1/C2 上全绿，但它**根本不是固定加速度**。
            double uncapped = Last(Run(A, 1e9, 3.0, Dt));
            chk(uncapped > 40,
                "spin.max.discriminates",
                "把上限去掉 ⇒ ω = " + uncapped.ToString("0.0") + " rad/s（应 = A×3 = 48，远大于 " + Max
                    + "；若这里也是 " + Max + "，说明「上限」根本不是 clamp，而是被写死的）");

            // ================= D 组：方向 —— yaw 逐帧单调 =================
            var trNeg = Run(-A, Max, 1.0, Dt);
            var trPos = Run(A, Max, 1.0, Dt);
            double yawNeg = YawOf(trNeg), yawPos = YawOf(trPos);
            bool monoNeg = Monotone(trNeg, -1), monoPos = Monotone(trPos, +1);
            chk(monoNeg && yawNeg < -1.0,
                "spin.dir.neg_yaw_monotone_decreasing",
                "SpinAccel<0（左半屏）：1 秒后 yaw = " + yawNeg.ToString("0.000")
                    + " rad，逐帧单调递减=" + monoNeg);
            chk(monoPos && yawPos > 1.0,
                "spin.dir.pos_yaw_monotone_increasing",
                "SpinAccel>0（右半屏）：1 秒后 yaw = " + yawPos.ToString("0.000")
                    + " rad，逐帧单调递增=" + monoPos);
            // ⚠ 判别力：两个方向必须给出**异号**结果。若上面两条都"通过"了却同号，说明判据本身写错了。
            chk(Sign(yawNeg) != Sign(yawPos) && Sign(yawNeg) != 0,
                "spin.dir.discriminates",
                "两个方向的末刻 yaw 必须异号且非零： " + yawNeg.ToString("0.0") + " / " + yawPos.ToString("0.0"));

            // ================= E 组：「迅速增加」的量级 =================
            // 抓两种坏实现：① 每帧直接 ω = MaxVel（tReach ≈ 一个 dt）② 加速度被设成极小（tReach 很大）
            chk(tReach >= 3 * Dt,
                "spin.accel.not_instantaneous",
                "tReach = " + tReach.ToString("0.000") + " 秒 ＞ 3×dt（= "
                    + (3 * Dt).ToString("0.000") + "）—— 瞬时到位说明是直接赋值，不是「固定加速度」");

            // ================= F 组：松手瞬间的惯性承接 =================
            // 拖拽 0.25 秒（此时 ω ≈ 4 rad/s）→ 切到 Airborne 且 SpinDrive=0 → 看下一帧还转不转
            var kk = new PoseEngine { Dragging = true, SpinAccel = A, SpinMaxVel = Max };
            double prevYaw = 0, lastDelta = 0;
            for (int i = 0; i < 30; i++) { var pp = kk.Step(Dt); lastDelta = pp.Yaw - prevYaw; prevYaw = pp.Yaw; }
            kk.Dragging = false; kk.Airborne = true; kk.SpinAccel = 0; kk.SpinDrive = 0;
            double handDelta = kk.Step(Dt).Yaw - prevYaw;
            chk(handDelta > 0 && handDelta > lastDelta * 0.9 && handDelta <= lastDelta * 1.001,
                "spin.handoff.keeps_omega",
                "松手前 Δframe=" + lastDelta.ToString("0.00000") + "，松手后首帧 Δframe="
                    + handDelta.ToString("0.00000") + "（同号且未跳升；=0 说明把 _spinVel 清零了）");

            // ================= H 组：回归 —— 飞行阶段的旧口径没被改坏 =================
            // ⚠ 这里**刻意不拿 `SpinK·SpinDrive/SpinDecay = 4.2857` 当理论值**：那是**连续解**，
            //   而引擎是按帧离散递推的（`ω ← (ω + k·d·dt)·e^{-λdt}`），dt=1/120 时两者差 0.6%
            //   （离散稳态 4.2607、连续解 4.2857）。第一版就是这么写的，结果得到一条恒定 0.029 的
            //   **假失败** —— 「照程序的定义量」这条纪律在这里的形态是：**理论值也要用同一套离散口径推**。
            //   所以改成验**行为特征** —— 它们同时也是判别力所在：
            //     ① 稳态为正且有限
            //     ② 稳态**与 SpinDrive 成正比** ← 关键：拎起的新口径是「固定加速度」，稳态与 SpinDrive
            //        **无关**；所以这条能直接分开新旧两套口径（比值会是 1 而不是 2）
            //     ③ 撤掉驱动后角速度会衰减下来（不是恒定转下去）
            double st1 = AirborneSteady(1000, 10.0);
            double st2 = AirborneSteady(2000, 10.0);
            chk(st1 > 0 && Math.Abs(st2 / st1 - 2.0) < 0.005,
                "spin.airborne.legacy_proportional_to_drive",
                "SpinDrive 1000→2000 的稳态 ω：" + st1.ToString("0.0000") + " → " + st2.ToString("0.0000")
                    + "，比 = " + (st2 / st1).ToString("0.0000") + "（期望 2：飞行口径仍是「∝横向速度」；"
                    + "若为 1 说明它被改成了固定加速度）");

            var kd = new PoseEngine { Airborne = true, SpinDrive = 1000 };
            double pv2 = 0, omBefore = 0;
            for (int i = 0; i < 1200; i++) { var pp = kd.Step(Dt); omBefore = (pp.Yaw - pv2) / Dt; pv2 = pp.Yaw; }
            kd.SpinDrive = 0;
            double omAfter = 0;
            for (int i = 0; i < 120; i++) { var pp = kd.Step(Dt); omAfter = (pp.Yaw - pv2) / Dt; pv2 = pp.Yaw; }
            chk(omAfter < omBefore * 0.30,
                "spin.airborne.decays_when_drive_removed",
                "撤掉驱动 1 秒后 ω：" + omBefore.ToString("0.0000") + " → " + omAfter.ToString("0.0000")
                    + "（期望衰减到 30% 以下；e^-1.4 = " + Math.Exp(-1.4).ToString("0.000") + "）");

            // ================= I 组：跨半屏换向（角速度不突变，只让加速度反向）=================
            // 需求原话（2026-09-22 追加）：「拎起桌宠跨越屏幕半边时，角速度不进行突变而是让加速度反向」。
            // 合成输入：先用 -A 跑 0.4 秒（ω = -9.4，**故意没到上限** —— 留出纯加速段好分辨），
            //   再把 SpinAccel 翻成 +A 跑 1.1 秒 ⇒ ω 应**连续**地从 -9.4 穿过 0 升到 +Max。
            //   ⚠ 换向只在**合成输入侧**改 `SpinAccel` 一个字段 —— 与壳侧 OnMove 的动作完全等价。
            const double FlipAt = 0.4;
            var trF = RunWithFlip(-A, Max, FlipAt, 1.5, Dt);
            int fi = (int)Math.Round(FlipAt / Dt) - 1;        // rows[fi] = 换向后第一帧
            Func<int, double> dwd = k => trF[k][2] - trF[k - 1][2];   // 相邻帧角速度差（正常应为 ±A·dt）

            double dwMaxF = 0;
            for (int k = 1; k < trF.Count; k++) dwMaxF = Math.Max(dwMaxF, Math.Abs(dwd(k)));

            // I1 上界：全程没有一帧的 Δω 超过 A·dt ⇒ **没有跳变**。
            //   ⚠ 这是整组最核心的判别力：换向时若写 `_spinVel = 0` ⇒ 该帧 Δω = 9.4 ≈ 47×A·dt；
            //     若写 `_spinVel = -_spinVel` ⇒ Δω = 18.8 ≈ 94×A·dt。两种都被这条一次抓住。
            chk(dwMaxF <= A * Dt + 1e-9,
                "spin.flip.omega_continuous",
                "换向前后全程 max|Δω| = " + dwMaxF.ToString("0.######") + "（应 ≤ A·dt = "
                    + (A * Dt).ToString("0.######") + "；超了就是角速度跳变 —— 本需求的直接判据）");

            // I2 加速度**真的反了号**：换向那一帧 Δω 变号，且换向前一帧还是原号。
            chk(dwd(fi) > 0 && dwd(fi - 1) < 0,
                "spin.flip.accel_sign_reversed",
                "换向前一帧 Δω = " + dwd(fi - 1).ToString("0.######") + "（负），换向后第一帧 Δω = "
                    + dwd(fi).ToString("0.######") + "（正）—— 只反加速度、不动角速度");

            // I3 减速段是**线性**的（Δω 恒定 = +A·dt），不是衰减、不是半路跳变。
            int zeroIdx = -1;
            for (int k = fi; k < trF.Count; k++) if (trF[k][2] > 0) { zeroIdx = k; break; }
            bool linOk = zeroIdx > fi;
            for (int k = fi; k < zeroIdx; k++) if (Math.Abs(dwd(k) - A * Dt) > 1e-9) linOk = false;
            chk(linOk,
                "spin.flip.decel_is_linear",
                "从换向到 ω 过零共 " + (zeroIdx - fi) + " 帧，逐帧 Δω 恒为 +A·dt = " + (A * Dt).ToString("0.######")
                    + " = " + linOk + "（换成衰减或半路跳变，这条变红）");

            // I4 真的**转到另一边**去了：末刻 ω 变正且等于 +Max（新上限）。
            chk(Math.Abs(Last(trF) - Max) < 1e-9,
                "spin.flip.omega_crosses_zero",
                "换向 1.1 秒后 ω = " + Last(trF).ToString("0.000000") + " rad/s（期望 +" + Max
                    + "；停在 0 附近说明只是「刹住」了，没有反向）");

            // I5 换向过程中**不越界**：全程 |ω| ≤ Max。
            double peakAbs = 0;
            for (int k = 0; k < trF.Count; k++) peakAbs = Math.Max(peakAbs, Math.Abs(trF[k][2]));
            chk(peakAbs <= Max + 1e-9,
                "spin.flip.never_exceeds_max",
                "全程峰值 |ω| = " + peakAbs.ToString("0.000000") + " rad/s（上限 " + Max
                    + "；换向时冲过头说明 clamp 没兜住）");

            // I6 下界（判别力）：Δω 必须**确实**回到 A·dt。只卡上界的话，
            //   一个「换向后把加速度也清成 0」的实现（Δω ≡ 0）会在 I1 上全绿 —— 这条是它的守门人。
            chk(dwMaxF >= A * Dt - 1e-9,
                "spin.flip.accel_is_effective",
                "max|Δω| = " + dwMaxF.ToString("0.######") + " ≥ A·dt（若 ≈0 说明换向后加速度不再作用，"
                    + "那只是「停下来」，不是「反向」）");

            // I7 迟滞：**带内不翻**（两个方向都验 —— 迟滞的本质是「记住上一个方向」）。
            chk(PoseEngine.SpinDirForHyst(970, 0, 1920, -1, 40) == -1
                    && PoseEngine.SpinDirForHyst(950, 0, 1920, 1, 40) == 1,
                "spin.hyst.holds_inside_band",
                "中线 960±40：中心 970 带当前 -1 ⇒ " + PoseEngine.SpinDirForHyst(970, 0, 1920, -1, 40)
                    + "，中心 950 带当前 +1 ⇒ " + PoseEngine.SpinDirForHyst(950, 0, 1920, 1, 40)
                    + "（期望 -1 / +1 —— 带内必须保持，否则贴中线时抖一下方向就翻）");

            // I8 迟滞：**越过带才翻**（左右两个方向都验 —— 翻不过去就成了「锁死」）。
            chk(PoseEngine.SpinDirForHyst(1010, 0, 1920, -1, 40) == 1
                    && PoseEngine.SpinDirForHyst(910, 0, 1920, 1, 40) == -1,
                "spin.hyst.flips_outside_band",
                "中心 1010（中线右 50 > 40）带 -1 ⇒ " + PoseEngine.SpinDirForHyst(1010, 0, 1920, -1, 40)
                    + "，中心 910（中线左 50）带 +1 ⇒ " + PoseEngine.SpinDirForHyst(910, 0, 1920, 1, 40)
                    + "（期望 +1 / -1）");

            // I9 band=0 必须**退化成** SpinDirFor（逐点一致）——
            //   迟滞是叠加在旧口径上的，不该顺手改掉「正好压在中线判 +1」这个既有边界口径。
            bool degOk = true;
            double[] xs = { 0, 500, 959, 959.9, 960, 960.1, 1400, 1920 };
            foreach (double x in xs)
                if (PoseEngine.SpinDirForHyst(x, 0, 1920, 0, 0) != PoseEngine.SpinDirFor(x, 0, 1920)) degOk = false;
            chk(degOk,
                "spin.hyst.zero_band_matches_plain",
                "band=0 时与 SpinDirFor 在 " + xs.Length + " 个取样点逐点一致 = " + degOk
                    + "（含中线 960 判 +1 的旧边界口径）");

            // I10 判别力：带内与带外必须给出**不同**结果（否则 band 根本没被读进去）。
            chk(PoseEngine.SpinDirForHyst(970, 0, 1920, -1, 40) != PoseEngine.SpinDirForHyst(1010, 0, 1920, -1, 40),
                "spin.hyst.discriminates",
                "同为 currentDir=-1：中心 970 ⇒ " + PoseEngine.SpinDirForHyst(970, 0, 1920, -1, 40)
                    + "，中心 1010 ⇒ " + PoseEngine.SpinDirForHyst(1010, 0, 1920, -1, 40)
                    + "（必须不同，否则迟滞带形同虚设）");

            // ================= J 组：出厂默认参数的自洽性 =================
            // ⚠ 这组守的是「参数本身」，不是「算术」：改 Pose.cs 的默认值会在这里留痕。
            //   用 `new PoseEngine()` 读出厂值（**不 Step** ⇒ 无副作用、不受随机漂移影响）。
            var def = new PoseEngine();
            double toCapDef = def.SpinMaxVel / def.SpinAccelMag;
            chk(toCapDef > 0.15 && toCapDef <= 0.8,
                "spin.defaults.time_to_cap_is_quick",
                "出厂 SpinAccelMag=" + def.SpinAccelMag + "、SpinMaxVel=" + def.SpinMaxVel
                    + " ⇒ 到顶 " + toCapDef.ToString("0.000") + " 秒（>0.8 秒就谈不上「迅速增加」）");

            // ⚠ 把 2026-09-22「提高上限」这个需求**固化**下来：改回 8 会在这里变红并报出来。
            chk(def.SpinMaxVel >= 10.0,
                "spin.defaults.max_vel_raised",
                "出厂上限 " + def.SpinMaxVel + " rad/s ≈ " + (def.SpinMaxVel * 180 / Math.PI).ToString("0")
                    + "°/s ≈ " + (def.SpinMaxVel / (2 * Math.PI)).ToString("0.00")
                    + " 圈/秒（2026-09-22 按要求从 8 提高到 12；<10 说明这个需求被回退了）");

            // ================= K 组：壳侧接线（源码级）=================
            // ⚠ 为什么要有这组：换向的**算术**在 I 组已经验透，但「壳侧到底有没有在 OnMove 里调它」
            //   离线判据一点也够不着（PetWindow 要真窗口、真鼠标）。这组直接读源码文字把**接线形状**钉死，
            //   抓的是「改了引擎却忘了接线」这种最典型的漏改 —— 成本三行查找。
            // ⚠ 源码不在身边（发布产物里没有 .cs）⇒ **整组 [skip]**，不加入 checks。
            //   与 `--updatetest` 处理 .cmd / README 的口径一致：**跳过要打印出来，不能静默算过**。
            string repoRoot = FindRepoRoot();
            int kGroup = 0;
            if (repoRoot == null)
            {
                Console.WriteLine("  [skip] 附近没有仓库根（发布产物里没有 .cs）—— 壳侧接线未验");
            }
            else
            {
                string shellPath = Path.Combine(repoRoot, "PetWindow.cs");
                string shellSrc = File.Exists(shellPath) ? File.ReadAllText(shellPath) : "";
                string shellCode = StripCommentLines(shellSrc);
                kGroup = 3;
                // ⚠⚠ 这里的两条边界**都是负对照实测出来的**（首版两条全漏检，两次都打印全绿）：
                //   ① 不能 `Contains` 原始源码 —— 注释掉的代码仍是一段包含该子串的文本，
                //      于是「把接线注释掉」（最常见的退化方式）**照样全绿**。⇒ 必须先剥注释。
                //   ② 不能查**全文** —— `OnDown` 里也有同一句 `Pose.SpinAccel = _spinDir * …`（那是**初值**），
                //      查全文时，把 `OnMove` 的**换向**接线注释掉仍然匹配得到 ⇒ 又全绿。
                //      「按下那一刻定初值」与「拖拽途中跨屏换向」是**两件不同的事**，必须分开守。
                //   ⚠ 教训：源码级判据两次「看着对」而实际漏检 —— 只跑正对照是发现不了的。
                string onDown = MethodBody(shellCode, "private void OnDown(", "private void OnMove(");
                string onMove = MethodBody(shellCode, "private void OnMove(", "private void OnUp(");
                bool downWired = onDown.Contains("PoseEngine.SpinDirFor(")
                              && onDown.Contains("Pose.SpinAccel = _spinDir * Pose.SpinAccelMag");
                chk(downWired,
                    "spin.wire.ondown_sets_initial_dir",
                    "OnDown 内同时含 SpinDirFor 调用与 SpinAccel 初值 = " + downWired
                        + "（缺任一个 ⇒ 按下那一刻没定方向）");
                bool moveWired = onMove.Contains("PoseEngine.SpinDirForHyst(")
                              && onMove.Contains("Pose.SpinAccel = _spinDir * Pose.SpinAccelMag");
                chk(moveWired,
                    "spin.wire.onmove_flips_on_cross",
                    "OnMove 内同时含 SpinDirForHyst 调用与 SpinAccel 重写 = " + moveWired
                        + "（缺任一个 ⇒ 引擎改了但壳侧没接线，离线的 I 组照样全绿）");
                // ⚠ 这条仍查**全文**：角速度在哪个方法里都不许被壳侧碰。
                bool omegaInCode = shellCode.Contains("_spinVel");
                chk(!omegaInCode,
                    "spin.wire.shell_never_touches_omega",
                    "PetWindow.cs 的**代码行**里不出现 `_spinVel` = " + (!omegaInCode)
                        + "（角速度只能由引擎改；壳侧碰它就是「突变」的唯一来源）");
            }

            // ================= 防空转（纪律页第 30 条）=================
            // 判据在**空集**上恒真：一个都没匹配到，和全都合格，输出一模一样。
            // 阈值 = 上面各组的条数之和（**不含本条**）：
            //   A6 + B2 + C4 + D3 + E1 + F1 + H2 + I10 + J2 = **31**
            //   ＋ K 组（只在源码在身边时才加入，2 条）⇒ 源码在时 **33**、不在时 **31**。
            // ⚠⚠ 为什么不是 33+1：`chk(pass, name, detail)` 的参数是**先求值**的，
            //   所以在**本条判据内部**读 `checks.Count` 时，「防空转」自己**还没被 Add 进去**
            //   （读到 33，而外部打印是 34）。这个隐式求值顺序是这里最容易算错的地方 ——
            //   2026-09-22 扩这组时为它连错了两次：先按「含本条」写成 34 ⇒ **判据当场变红**，
            //   而当时唯一真正有问题的是阈值、不是被测代码。改成「不含本条」的 33 才对上。
            //   ⇒ 现在**显式赋值给 `executed`**，不再依赖那个顺序，读代码的人也不必去推它。
            //   ⚠ 阈值**偏小**比偏大危险得多：偏大当场变红、立刻被发现；
            //     偏小只是悄悄失去灵敏度 —— 「守门人」退化成「摆设」，而且**输出长得一模一样**。
            int executed = checks.Count;
            int expect = 31 + kGroup;
            chk(executed >= expect,
                "spin.cases_present",
                "实际执行 " + executed + " 项（**不含本条**；含本条共 " + (executed + 1)
                    + " 项），阈值 " + expect + "（< 阈值说明有整组没跑到，此时「全绿」没有意义）");

            // ---- 落盘 ----
            var report = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["params"] = new Dictionary<string, object>
                {
                    ["accelMag"] = A, ["maxVel"] = Max, ["dt"] = Dt,      // ← **合成**参数（判据自己构造的）
                    ["tReachSec"] = tReach,
                    ["rpmAtCap"] = Max / (2 * Math.PI) * 60,
                    ["degPerSecAtCap"] = Max * 180 / Math.PI,
                    ["defaultAccelMag"] = def.SpinAccelMag,               // ← **出厂**参数（Pose.cs 的默认值）
                    ["defaultMaxVel"] = def.SpinMaxVel,
                    ["defaultTimeToCapSec"] = def.SpinMaxVel / def.SpinAccelMag,
                    ["defaultHystPx"] = def.SpinHystPx,
                },
                ["checks"] = checks,
            };
            string outPath = Path.Combine(Path.GetTempPath(), "azhu_spintest.json");
            File.WriteAllText(outPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            try { using (var d = JsonDocument.Parse(File.ReadAllText(outPath, Encoding.UTF8))) { } }
            catch (Exception ex) { ok = false; Console.WriteLine("写出的 JSON 自己解析不了：" + ex.Message); }

            Console.WriteLine("spintest " + (ok ? "OK" : "FAIL") + " —— " + checks.Count + " 项检查，报告 " + outPath);
            // ⚠ 下面两行是**两套不同**的参数，别混：
            //   合成参数是判据自己构造的（**故意与出厂值不同** —— B 组的 read_from_field 正是靠这个差异
            //   证明「参数确实从字段读」；若两者相等，那条判据就退化了）；
            //   出厂参数才是她拎起来时真正会用的。
            Console.WriteLine("  合成参数（判据用）：A=" + A + " rad/s²，上限 " + Max + " rad/s（"
                + (Max * 180 / Math.PI).ToString("0") + "°/s ≈ " + (Max / (2 * Math.PI)).ToString("0.00")
                + " 圈/秒），到顶 " + tReach.ToString("0.000") + " 秒");
            Console.WriteLine("  出厂参数（她实际用）：A=" + def.SpinAccelMag + " rad/s²，上限 " + def.SpinMaxVel
                + " rad/s（" + (def.SpinMaxVel * 180 / Math.PI).ToString("0") + "°/s ≈ "
                + (def.SpinMaxVel / (2 * Math.PI)).ToString("0.00") + " 圈/秒），到顶 "
                + (def.SpinMaxVel / def.SpinAccelMag).ToString("0.000") + " 秒，换向迟滞 ±" + def.SpinHystPx + "px");
            foreach (object c in checks)
            {
                var d = (Dictionary<string, object>)c;
                if (!(bool)d["ok"]) Console.WriteLine("  [x] " + d["name"] + "：" + d["detail"]);
            }
            return ok ? 0 : 1;
        }

        // ------------------------------------------------------------------ 工具

        /// <summary>
        /// 用合成输入驱动引擎，返回逐帧 [t, yaw, ω]。
        /// ω 由 **yaw 差分**算出 —— 刻意不去读引擎内部的 `_spinVel`（private）：
        /// 「读处理结果」会让判据在实现退化时依然全绿（纪律页第 17 条），
        /// 而 yaw 是**真正喂给渲染器**的那一个量，它错了才是真的错。
        /// </summary>
        private static List<double[]> Run(double accel, double maxVel, double seconds, double dt)
        {
            var k = new PoseEngine { Dragging = true, SpinAccel = accel, SpinMaxVel = maxVel };
            var rows = new List<double[]>();
            double prev = 0;
            for (int i = 1; i * dt <= seconds + 1e-9; i++)
            {
                double t = i * dt;
                var p = k.Step(dt);
                rows.Add(new[] { t, p.Yaw, (p.Yaw - prev) / dt });
                prev = p.Yaw;
            }
            return rows;
        }

        /// <summary>
        /// 同 <see cref="Run"/>，但在第 flipSec 秒处**把 SpinAccel 整体反号** ——
        /// 模拟壳侧拖拽途中跨过半屏（壳侧的动作与此完全等价：只改这一个字段）。
        /// ⚠ 反号**不碰** `_spinVel`：引擎里的角速度因此天然连续，这正是「不突变」要验的东西。
        /// </summary>
        private static List<double[]> RunWithFlip(double accel, double maxVel, double flipSec, double totalSec, double dt)
        {
            var k = new PoseEngine { Dragging = true, SpinAccel = accel, SpinMaxVel = maxVel };
            var rows = new List<double[]>();
            double prev = 0;
            int flipFrame = (int)Math.Round(flipSec / dt);
            for (int i = 1; i * dt <= totalSec + 1e-9; i++)
            {
                if (i == flipFrame) k.SpinAccel = -accel;      // ← 换向：**只**改加速度，角速度一个字都不动
                double t = i * dt;
                var p = k.Step(dt);
                rows.Add(new[] { t, p.Yaw, (p.Yaw - prev) / dt });
                prev = p.Yaw;
            }
            return rows;
        }

        /// <summary>
        /// 截出 `startMark` 到 `endMark` 之间的源码 —— 用于把源码级断言**收窄到某个方法体内**。
        /// ⚠ 存在的理由：同一句接线在 `OnDown`（定初值）和 `OnMove`（换向）里各出现一次，
        ///   查全文时分不清是谁，负对照实测会漏检（见 K 组注释②）。
        /// 找不到 startMark 返回空串（⇒ 依赖它的判据变红，正是我们要的）。
        /// </summary>
        private static string MethodBody(string src, string startMark, string endMark)
        {
            int a = src.IndexOf(startMark, StringComparison.Ordinal);
            if (a < 0) return "";
            int b = src.IndexOf(endMark, a + startMark.Length, StringComparison.Ordinal);
            return b > a ? src.Substring(a, b - a) : src.Substring(a);
        }

        /// <summary>从 exe 目录往上找仓库根（含 .git 的目录）。找不到返回 null —— 调用方按「无对象」跳过。</summary>
        private static string FindRepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int up = 0; up < 7 && dir != null; up++)
            {
                if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
                var parent = Directory.GetParent(dir);
                dir = parent == null ? null : parent.FullName;
            }
            return null;
        }

        /// <summary>
        /// 剥掉**整行注释**（`//` 开头的行、以及块注释里 `*` 开头的续行），供源码级判据使用。
        /// ⚠ 存在的理由：`Contains` 对**注释掉的代码**同样为真 —— 首版 K 组没剥注释，
        ///   于是「把接线那行注释掉」的负对照**漏检了**（判据以为接线还在）。
        /// ⚠ 只处理整行注释，不处理行尾注释：对「守一段接线形状」这个用途足够，
        ///   而且不会误伤字符串里的 `//`（如 URL）。行尾注释要连在一起才可能误判，概率极低。
        /// </summary>
        private static string StripCommentLines(string src)
        {
            var sb = new StringBuilder();
            foreach (string line in src.Split('\n'))
            {
                string t = line.TrimStart();
                if (t.StartsWith("//") || t.StartsWith("*")) continue;
                sb.Append(line).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>末帧角速度（rad/s）。</summary>
        private static double Last(List<double[]> tr) { return tr[tr.Count - 1][2]; }

        /// <summary>末帧偏航（rad）。</summary>
        private static double YawOf(List<double[]> tr) { return tr[tr.Count - 1][1]; }

        /// <summary>飞行阶段的稳态角速度（跑够长时间后取末帧差分）。</summary>
        private static double AirborneSteady(double drive, double seconds)
        {
            var k = new PoseEngine { Airborne = true, SpinDrive = drive };
            double pv = 0, om = 0;
            int n = (int)(seconds / Dt);
            for (int i = 0; i < n; i++) { var pp = k.Step(Dt); om = (pp.Yaw - pv) / Dt; pv = pp.Yaw; }
            return om;
        }

        /// <summary>逐帧角速度是否全部同号（方向单调，没有抖动/反转）。</summary>
        private static bool Monotone(List<double[]> tr, int sign)
        {
            int hit = 0;
            for (int i = 0; i < tr.Count; i++)
            {
                if (Math.Abs(tr[i][2]) < 1e-12) continue;      // 第 0 帧之外不该有零
                if (Math.Sign(tr[i][2]) != sign) return false;
                hit++;
            }
            return hit > 0;    // ⚠ 一帧都没命中 = 空转 ⇒ 不算通过
        }

        private static int Sign(double v) { return v > 0 ? 1 : (v < 0 ? -1 : 0); }
    }
}
