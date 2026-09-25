// --topmosttest：离线＋半在线验「置顶（Topmost）」这一格状态。
//
// ⚠⚠ 为什么这条自检非有不可（2026-09-25 真故障：用户报「桌宠无法保持在所有界面上方」）：
//   现场量到的是 —— `config.json` 里 `"topmost": true`，而活着的窗口
//   `GetWindowLong(GWL_EXSTYLE) = 0x08080080`（TOOLWINDOW|NOACTIVATE|LAYERED），
//   **0x00000008（WS_EX_TOPMOST）不在里面**。也就是「她自认置顶，系统不认」。
//
//   而这个故障**没有任何一条既有判据能发现**：所有自检都在验逻辑（配置读写、通道路由、
//   版式），没有一条验「配置说置顶，那个窗口真的置顶了吗」。
//   ⇒ 这里把它变成两件可以量出来的事：
//     A. 纯逻辑：借出/归还的**计数**语义、归还时的值**只由配置决定**、自愈的判定表；
//     B. 半在线：真建一个窗口，量 `Native.ReassertTopmost` / `DropTopmost`
//        是不是**真的**把 `WS_EX_TOPMOST` 位拨过去了 —— 「以为拨了」与「真拨了」之间
//        必须有一次量测（本仓老规矩）。
//
// ⚠ 它**不碰**桌宠自己的窗口、不写配置、不联网；那个用来量位的窗口建在屏幕外且立刻关掉。
// ⚠ 样品用的是**现场真值** 0x08080080 与它的置顶版 0x08080088 —— 拿真值当样品，
//   比拿 0x0 / 0x8 这种玩具值更能抓住「看串了 TOOLWINDOW(0x80)」这类错。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace AzhuPet
{
    internal static class TopmostTest
    {
        /// <summary>现场真值：实测某个非置顶的桌宠窗口的扩展样式。</summary>
        private const int RealNonTopmost = unchecked((int)0x08080080);   // TOOLWINDOW|NOACTIVATE|LAYERED
        /// <summary>同一个窗口「置顶版」：只多那一位。</summary>
        private const int RealTopmost = unchecked((int)0x08080088);

        public static int Run()
        {
            int pass = 0, fail = 0;
            try
            {
                Logic(ref pass, ref fail);
                Native_RealWindow(ref pass, ref fail);
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine("[FAIL] 未处理异常：" + ex.Message);
            }
            Console.WriteLine("置顶测试：PASS " + pass + " / FAIL " + fail);
            return fail == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ A. 纯逻辑
        private static void Logic(ref int pass, ref int fail)
        {
            // ---- 位读得对不对（用现场真值）----
            Check(!TopmostGuard.HasTopmostBit(RealNonTopmost), "现场真值 0x08080080 判为「没置顶」", ref pass, ref fail);
            Check(TopmostGuard.HasTopmostBit(RealTopmost), "同一位加上 0x8 后判为「已置顶」", ref pass, ref fail);
            // ⚠ 这两条要成对：只看一条时，「永远返回 false」也能全绿。
            Check(TopmostGuard.HasTopmostBit(0x08080080) != TopmostGuard.HasTopmostBit(0x08080088),
                "这一对样品本身有分辨力（不是恒为同一个答案）", ref pass, ref fail);
            Check(!TopmostGuard.HasTopmostBit(0x00080080), "TOOLWINDOW|LAYERED（0x80080）里没有置顶位", ref pass, ref fail);

            // ---- 拨位不许碰掉别的位 ----
            int on = TopmostGuard.WithTopmost(RealNonTopmost, true);
            Check(on == RealTopmost, "拨上 = 现场真值 | 0x8（逐位相加的结果）", ref pass, ref fail);
            Check((on & 0x00000080) != 0 && (on & 0x08000000) != 0 && (on & 0x00080000) != 0,
                "拨上后 TOOLWINDOW / NOACTIVATE / LAYERED 都还在", ref pass, ref fail);
            int off = TopmostGuard.WithTopmost(RealTopmost, false);
            Check(off == RealNonTopmost, "拨下后逐位回到现场真值", ref pass, ref fail);

            // ---- 借出／归还是计数，不是布尔 ----
            int hold = 0;
            Check(!TopmostGuard.IsSuspended(hold), "初始没人借走", ref pass, ref fail);
            hold = TopmostGuard.Suspend(hold);
            Check(TopmostGuard.IsSuspended(hold), "借走一次 → 处于借出态", ref pass, ref fail);
            hold = TopmostGuard.Suspend(hold);
            hold = TopmostGuard.Resume(hold);
            Check(TopmostGuard.IsSuspended(hold), "借两次还一次 → 仍是借出态（布尔写法会在这里提前归还）", ref pass, ref fail);
            hold = TopmostGuard.Resume(hold);
            Check(!TopmostGuard.IsSuspended(hold), "借两次还两次 → 归还完毕", ref pass, ref fail);
            hold = TopmostGuard.Resume(hold);
            Check(hold == 0 && !TopmostGuard.IsSuspended(hold), "多还一次不会把计数打成负数", ref pass, ref fail);

            // ---- 核心：归还时的值只由配置决定 ----
            bool cfg = true, captured = false;
            bool byCap = captured;                                 // 旧写法的结论
            bool byCfg = TopmostGuard.Restore(cfg, captured);       // 新写法的结论
            Check(byCap == false, "负对照：旧写法（写回捕获值）在这个场景下得 false", ref pass, ref fail);
            Check(byCfg == true, "归还按配置：得 true", ref pass, ref fail);
            Check(byCap != byCfg, "两种写法在这个场景下结论**不同** —— 说明这条判据分辨得出来", ref pass, ref fail);
            Check(TopmostGuard.Restore(false, true) == false, "配置说关、捕获说开 → 结论仍是关", ref pass, ref fail);

            // ---- 自愈判定表 ----
            Check(TopmostGuard.NeedReassert(false, true, false), "没借走＋配置要＋位不在 → 该补", ref pass, ref fail);
            Check(!TopmostGuard.NeedReassert(false, true, true), "没借走＋配置要＋位在 → 不该动（幂等，否则每秒抢 z 序）", ref pass, ref fail);
            Check(TopmostGuard.NeedReassert(false, false, true), "没借走＋配置不要＋位却在 → 该撤", ref pass, ref fail);
            Check(!TopmostGuard.NeedReassert(false, false, false), "没借走＋配置不要＋位不在 → 一致，不该动", ref pass, ref fail);
            Check(!TopmostGuard.NeedReassert(true, true, false), "**借走期间一律不动**（否则打开面板时自愈会把面板盖住）", ref pass, ref fail);
            Check(!TopmostGuard.NeedReassert(true, false, true), "借走期间不动（另一个方向也一样）", ref pass, ref fail);
        }

        // ------------------------------------------------------------------ B. 真窗口
        private static void Native_RealWindow(ref int pass, ref int fail)
        {
            Form f = null;
            try
            {
                f = new Form();
                f.FormBorderStyle = FormBorderStyle.None;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.Manual;
                f.Location = new Point(-4000, -4000);   // 屏幕外：真建一个显示中的窗口，但不打扰用户
                f.Size = new Size(8, 8);
                f.Show();
                Application.DoEvents();
                IntPtr h = f.Handle;
                Check(h != IntPtr.Zero, "拿到真窗口句柄", ref pass, ref fail);
                if (h == IntPtr.Zero) return;

                Check(!Native.HasTopmostBit(h), "新建的普通窗口初始没有置顶位", ref pass, ref fail);

                Check(Native.ReassertTopmost(h), "ReassertTopmost 报告成功", ref pass, ref fail);
                Application.DoEvents();
                Check(Native.HasTopmostBit(h), "**真位上**确实多了 0x8（不是只信返回值）", ref pass, ref fail);

                Check(Native.ReassertTopmost(h), "再补一次仍成功", ref pass, ref fail);
                Check(Native.HasTopmostBit(h), "重复补不破坏状态（幂等）", ref pass, ref fail);

                Check(Native.DropTopmost(h), "DropTopmost 报告成功", ref pass, ref fail);
                Application.DoEvents();
                Check(!Native.HasTopmostBit(h), "**真位上**确实去掉了 0x8", ref pass, ref fail);

                // 手动把位读出来与 guard 的判定对齐（同一份口径，不许两套）
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                Check(TopmostGuard.HasTopmostBit(ex) == Native.HasTopmostBit(h),
                    "TopmostGuard 与 Native 对同一个句柄给出同一个答案（口径只有一处）", ref pass, ref fail);
            }
            finally
            {
                try { if (f != null) { f.Close(); f.Dispose(); } } catch { }
            }
        }

        private static void Check(bool ok, string name, ref int pass, ref int fail)
        {
            if (ok) { pass++; Console.WriteLine("[PASS] " + name); }
            else { fail++; Console.WriteLine("[FAIL] " + name); }
        }
    }
}
