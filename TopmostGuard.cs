// 置顶（Topmost）的**唯一口径**与自愈判据。
//
// ⚠⚠ 为什么非要有这个文件（2026-09-25 真故障：用户报「桌宠无法保持在所有界面上方」）：
//   实测现场 —— `%LOCALAPPDATA%\AzhuPet\config.json` 里 `"topmost": true`，
//   而活着的那个窗口
//       GetWindowLong(GWL_EXSTYLE) = 0x08080080  →  TOOLWINDOW | NOACTIVATE | LAYERED
//   **0x00000008（WS_EX_TOPMOST）不在里面**。也就是说：她自认置顶，系统不认。
//
//   三条把置顶位弄丢的路径，全都来自「用**捕获-恢复**模式管这一格状态」：
//     ① 打开设置面板 → 临时降级 → 关闭时 `_w.Topmost = wasTopmost`，写回的是
//        **打开前的那个值**。可面板里的「保存」已经把 `Cfg.Topmost` 改成新值并对下发过了
//        ⇒ 面板一关，新设置被旧值抹掉 ⇒ **勾了「始终置顶」不生效**；
//        更要命的是 `wasTopmost` 从此恒为 false，**自我延续**（下次打开面板捕获到的还是
//        false），只有重启桌宠才好 —— 而配置里明明写着 true。
//     ② `OpenBalanceSettings` 同样写回捕获值，且**不防重入**：面板连开两次，
//        后关的那个会把 Topmost 写成 false（谁先关谁后关决定结果）。
//     ③ 对外部清掉置顶位**没有任何对账**。WPF 的 `Window.Topmost` 属性只是自认 true，
//        真实位被抹掉之后它**永远不会自己回来** —— 这正是「无法**保持**在最上方」那一半。
//
//   两条纪律因此定死：
//     · **归还时的值一律取配置**（`Cfg.Topmost`），绝不写回「借走前捕获」的那个值；
//     · **判「有没有置顶」看真实扩展样式位**，不看 `Window.Topmost` 属性。
//
// ⚠ 本文件是**纯逻辑**（不引用任何窗口类型），所以能被离线判据钉住；
//   真正拨位那一下在 `Native.ReassertTopmost` / `Native.DropTopmost`。
namespace AzhuPet
{
    internal static class TopmostGuard
    {
        /// <summary>`WS_EX_TOPMOST`。⚠ 别和 `WS_EX_TOOLWINDOW`(0x80) 看串 ——
        /// 2026-09-25 量到的 0x08080080 里**只有后者**，这正是故障现场。</summary>
        public const int WS_EX_TOPMOST = 0x00000008;

        /// <summary>真实位在不在。⚠ 这是判「置顶生效没有」的**唯一**依据。</summary>
        public static bool HasTopmostBit(int exStyle)
        {
            return (exStyle & WS_EX_TOPMOST) != 0;
        }

        /// <summary>读-改-写：只动置顶那一位，别的位（TOOLWINDOW／NOACTIVATE／LAYERED／
        /// TRANSPARENT）一个都不许碰 —— 碰掉 TOOLWINDOW 会让她出现在任务栏里。</summary>
        public static int WithTopmost(int exStyle, bool on)
        {
            return on ? (exStyle | WS_EX_TOPMOST) : (exStyle & ~WS_EX_TOPMOST);
        }

        // ---------------------------------------------------------------- 借出／归还
        /// <summary>借走置顶（打开面板期间）。**计数式，不是布尔**：设置主面板里还能再开
        /// 子对话框，布尔会被后关的那个提前归还。</summary>
        public static int Suspend(int hold) { return hold + 1; }

        /// <summary>归还一次。⚠ 多归还一次**不许把计数打成负数** —— 那会让「还欠几次」
        /// 这个事实被抹掉，下一次真借出就会被当成已归还。</summary>
        public static int Resume(int hold) { return hold > 0 ? hold - 1 : 0; }

        public static bool IsSuspended(int hold) { return hold > 0; }

        /// <summary>归还时该写什么值。
        /// ⚠⚠ 两个入参都在，但**结论只由 `cfgTopmost` 决定** —— `captured`（借走前的值）
        /// 传进来只是为了在签名上把话说清楚：**它被故意忽略**。
        /// 旧写法 `_w.Topmost = wasTopmost` 就是错在把 captured 当结论。</summary>
        public static bool Restore(bool cfgTopmost, bool captured)
        {
            return cfgTopmost;
        }

        // ---------------------------------------------------------------- 自愈
        /// <summary>这一拍要不要把置顶位跟配置对账（拨回一致）。
        /// 两个方向都要管：
        ///   · 配置要置顶、位上没有 ⇒ 补回（外部抹掉／借出后没还）；
        ///   · 配置不要置顶、位上却有 ⇒ 撤掉（否则她会一直浮着，而用户已经关掉了开关）。
        /// ⚠ `suspended`（正被别人借走）时**一律不动** —— 否则打开面板的那一下
        ///   会被自愈当场抢回来，面板又被她盖住（这正是当初要临时降级的原因）。</summary>
        public static bool NeedReassert(bool suspended, bool cfgTopmost, bool realBit)
        {
            if (suspended) return false;
            return cfgTopmost != realBit;
        }
    }
}
