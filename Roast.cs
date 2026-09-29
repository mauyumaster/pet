// 吐槽通道（2026-09-20，用户拍板「混合：变化优先＋定时保底」）——
//   ① 变化优先：同一个应用里窗口**标题**明显变了（在浏览器里换了页面、换了个文档）⇒ 值得吐槽一句；
//   ② 定时保底：长时间（默认 10 分钟）什么触发都没有 ⇒ 冒一句，免得她显得死掉。
//
// ⚠⚠ 标题在这里只是**触发信号**，绝不进提示 —— 「绝不发窗口标题」是隐私判据（--speaktest
//   的 promptHasNoTitle），吐槽也不能绕过它。吐槽的**素材**来自屏幕文字（`ScreenSource`，
//   由 `OcrSendText` 门控）；文字拿不到时她只能对着应用名与待的时长说（这个空档写在托盘标签上）。
//
// ⚠ 为什么换应用（proc 变化）不归这里管：那是原有观察的职责（「他刚换到 X」）。
//   同一次切换若两路都触发，她会连说两句 —— 两个触发器必须对同一件事只有一个负责。
//
// ⚠⚠ 这两个值**不是常量了**（2026-09-29）：它们跟着「设置 → 说话与吐槽 → 触发节奏」里
//   用户填的频率走，由 `SpeechFreq.Apply()` 唯一一处下发。这里的初值 = **默认配置换算出来的值**
//   （10 分钟一句 ⇒ 冷却 620、保底 600），所以万一某条路径漏接下发，行为也仍是新的默认行为，
//   不会悄悄退回旧值。
//
// ⚠ 为什么必须「吐槽冷却 > 开口冷却」：吐槽触发的那一刻，全局闸门必须已经放行。
//   触发间隔若比闸门短，触发会被闸门拦下 ⇒ **每一拍**产出一条被拦的观察 ⇒
//   memory.jsonl 被 veto 记录灌爆。⇒ 这个关系由 `SpeechFreq.RoastCooldownSec()` **派生**保证，
//   谁都不该手写这两个值（判据 `roastCooldownAboveGate` 会盯着）。
using System;

namespace AzhuPet
{
    internal static class Roast
    {
        /// <summary>两次吐槽之间的最小间隔（秒）。必须 &gt; `SpeechGate.Cooldown`，理由见文件头。
        /// ⚠ 由 `SpeechFreq.Apply()` 下发；初值 = 默认配置（10 分钟 + 20 秒冗余）。</summary>
        public static double CooldownSec = 620;

        /// <summary>定时保底：连续这么久没有任何发言，就冒一句（秒）。
        /// ⚠ 由 `SpeechFreq.Apply()` 下发；初值 = 默认配置（10 分钟）。</summary>
        public static double IdleNudgeSec = 600;

        /// <summary>
        /// **纯函数**：这一拍该不该冒一句吐槽。返回 null ＝ 不该。
        /// ⚠ 全部输入都可钉死（没有「当前前台」这种可变外部状态）⇒ 判据离线可打。
        /// </summary>
        public static Observation Decide(string curProc, string prevProc, string curTitle, string prevTitle,
                                         DateTime now, DateTime lastSpokeAt, DateTime lastRoastAt,
                                         double roastCooldownSec, double idleNudgeSec, string selfName = null)
        {
            if (string.IsNullOrEmpty(curProc)) return null;                    // 没有可吐槽的应用
            if (curProc == (selfName ?? Watcher.SelfName())) return null;      // 她自己（同观察层纪律）
            if (curProc != prevProc) return null;                              // 换应用归原观察管（文件头）

            double sinceSpoke = lastSpokeAt == DateTime.MinValue ? double.MaxValue : (now - lastSpokeAt).TotalSeconds;
            double sinceRoast = lastRoastAt == DateTime.MinValue ? double.MaxValue : (now - lastRoastAt).TotalSeconds;

            bool titleChanged = !string.IsNullOrEmpty(curTitle)
                && !string.Equals(curTitle, prevTitle, StringComparison.Ordinal);

            if (titleChanged)
            {
                if (sinceRoast < roastCooldownSec || sinceSpoke < roastCooldownSec) return null;
                return new Observation { Process = curProc, PrevDwell = 0, At = now, Reason = "roast-title" };
            }

            // 定时保底：标题没动，而且她太久没开过口了。
            if (sinceSpoke >= idleNudgeSec && sinceRoast >= idleNudgeSec)
                return new Observation { Process = curProc, PrevDwell = 0, At = now, Reason = "roast-idle" };

            return null;
        }
    }
}
