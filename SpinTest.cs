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
//
// ⚠ 怎么逼红（2026-09-22 **实跑过**，下面是实测结果、不是推测）：
//   负对照 1 — 把 `Pose.cs` 的 Dragging 分支改回旧口径（`_spinVel += SpinK*SpinDrive*dt;` ＋ 衰减那两行）：
//     合成输入里 SpinDrive 恒为 0 ⇒ 转速整个不动 ⇒ **11 条红**，恰好是
//     B 组 2 ＋ C 组 4 ＋ D 组 3 ＋ E 1 ＋ F 1；而 **A 组（方向纯函数）与 H 组（飞行回归）没被误伤**
//     ⇒ 反证这两组确实独立于 Dragging 分支（不是「恰好一起绿」）。
//   负对照 2 — 把 `Math.Clamp(_spinVel + SpinAccel*dt, …)` 换成裸累加：**2 条红**
//     （`steady_equals_maxvel` 得 ω=48=A×3、`read_from_field` 把上限改成 3 也没用）。
//   ⚠⚠ **必须记住的边界**：`spin.max.discriminates`（C3）在「上限根本没实现」时**反而不红** ——
//     它是拿「有上限」与「无上限」对撞，两边都无上限时就撞不出差别。
//     ⇒ **C3 的判别力只覆盖「有上限但值被写死」，抓「完全没实现」的是 C1。**
//     判据的覆盖面比它名字看起来**窄**，这件事只有真跑负对照才知道 —— 正对照堆再多也看不出来。
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

            // ================= 防空转（纪律页第 30 条）=================
            // 判据在**空集**上恒真：一个都没匹配到，和全都合格，输出一模一样。
            // 阈值 = 上面各组的**条数之和**（A6 + B2 + C4 + D3 + E1 + F1 + H2 = 19）。
            // ⚠ 上面任何一组加/删条目，都要同步改这里 —— 写死数字而不是 `> 0`，就是为了让「整组没跑到」也拦得住。
            // ⚠ 这条**首跑就抓到过一次**：我按 20 写、实际 19（H 组从 1 条改 2 条时数错），
            //   它当场变红。可见它不是摆设 —— 也正因如此，它自己不该被当成"凑数的一条"。
            chk(checks.Count >= 19,
                "spin.cases_present",
                "实际执行 " + checks.Count + " 项（< 19 说明有整组没跑到，此时「全绿」没有意义）");

            // ---- 落盘 ----
            var report = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["params"] = new Dictionary<string, object>
                {
                    ["accelMag"] = A, ["maxVel"] = Max, ["dt"] = Dt,
                    ["tReachSec"] = tReach,
                    ["rpmAtCap"] = Max / (2 * Math.PI) * 60,
                    ["degPerSecAtCap"] = Max * 180 / Math.PI,
                },
                ["checks"] = checks,
            };
            string outPath = Path.Combine(Path.GetTempPath(), "azhu_spintest.json");
            File.WriteAllText(outPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            try { using (var d = JsonDocument.Parse(File.ReadAllText(outPath, Encoding.UTF8))) { } }
            catch (Exception ex) { ok = false; Console.WriteLine("写出的 JSON 自己解析不了：" + ex.Message); }

            Console.WriteLine("spintest " + (ok ? "OK" : "FAIL") + " —— " + checks.Count + " 项检查，报告 " + outPath);
            Console.WriteLine("  参数：A=" + A + " rad/s²，上限 " + Max + " rad/s（"
                + (Max * 180 / Math.PI).ToString("0") + "°/s ≈ " + (Max / (2 * Math.PI)).ToString("0.00")
                + " 圈/秒），到顶 " + tReach.ToString("0.000") + " 秒");
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
