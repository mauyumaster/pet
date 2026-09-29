// 说话频率的三个旋钮（2026-09-29 用户要求：「改成十分钟一句」＋「频率相关设置在设置里可调」）。
//
// ⚠⚠ 为什么把换算收成**一个类**，而不是让设置面板／SpeechGate／Roast 各自去读配置：
//   这三个数字之间有**不变式**，单独调任何一个都会造出两种病态形态 —— 而且两种都不报错，
//   只表现为「她变得很奇怪」：
//
//     ① 「安静保底间隔」必须 ≥「开口冷却」。
//        保底若比冷却短，保底触发会被闸门拦下；而拦下是**每一拍**都会发生的事
//        ⇒ memory.jsonl 被 veto 记录灌爆（不是推测：Roast.cs 文件头记着这个现场）。
//     ② 「吐槽冷却」必须 >「开口冷却」。
//        同族理由：吐槽触发的那一刻，闸门必须已经放行，否则触发白产出一条被拦的观察。
//        ⇒ 这里让它**派生**（= 开口冷却 + 冗余），而不是让用户单独填一个容易填反的数字。
//
//   ⇒ 把两条钳制写在**唯一一处**，面板与判据都只看这一处，就不可能出现两个口径。
//     （本仓老毛病「同一份数据两个落点」，这个文件是它的解药。）
//
// ⚠ 这三个数字的**单位**：配置里存**分钟**（`PetConfig.SpeechCooldownMin` 等，与
//   `SummaryIntervalMin` 同族），运行时全是**秒** —— 换算只在这里发生，别处别自己乘 60。
using System;

namespace AzhuPet
{
    internal static class SpeechFreq
    {
        /// <summary>开口冷却的下限（秒）。低于 1 分钟是骚扰档，不是「多久说一句」。</summary>
        public const double MinCooldownSec = 60;

        /// <summary>吐槽冷却比开口冷却多出的冗余（秒）。> 0 即可。</summary>
        public const double RoastLeadSec = 20;

        /// <summary>⚠ **只供负对照**（`--no-freq-clamp`）：跳过两条钳制，把分钟数原样当秒数用。
        /// 运行时恒为 false —— 判据没被逼红过，它的绿就没有信息量。</summary>
        public static bool NoClamp;

        /// <summary>开口冷却（秒）= 两次开口之间的最小间隔。</summary>
        public static double CooldownSec(PetConfig c)
        {
            double sec = (c == null ? 10 : c.SpeechCooldownMin) * 60;
            return NoClamp ? sec : Math.Max(MinCooldownSec, sec);
        }

        /// <summary>安静保底间隔（秒）= 她多久没开口就冒一句。钳到 ≥ 冷却（见文件头 ①）。</summary>
        public static double IdleNudgeSec(PetConfig c, double cooldownSec)
        {
            double sec = (c == null ? 10 : c.RoastIdleMin) * 60;
            return NoClamp ? sec : Math.Max(cooldownSec, sec);
        }

        /// <summary>吐槽冷却（秒）。恒 = 开口冷却 + 冗余（见文件头 ②）。</summary>
        public static double RoastCooldownSec(double cooldownSec)
        {
            return cooldownSec + RoastLeadSec;
        }

        /// <summary>每日上限（句）。</summary>
        public static int DailyCap(PetConfig c)
        {
            int cap = c == null ? 200 : c.SpeechDailyCap;
            return NoClamp ? cap : Math.Max(1, Math.Min(5000, cap));
        }

        /// <summary>
        /// **唯一落点**：把配置里的三个数字换算成运行时真值，写到闸门与吐槽通道上。
        /// ⚠ 它改的是**静态位**（`Roast.*`）⇒ 判据用完必须还原（判据自己负责，别指望这里）。
        /// ⚠ `gate` 可为 null（面板可能在表达环接线之前就被打开）—— 这时只下发静态位。
        /// </summary>
        public static void Apply(PetConfig c, SpeechGate gate)
        {
            double cd = CooldownSec(c);
            if (gate != null) { gate.Cooldown = cd; gate.DailyCap = DailyCap(c); }
            Roast.IdleNudgeSec = IdleNudgeSec(c, cd);
            Roast.CooldownSec = RoastCooldownSec(cd);
        }
    }
}
