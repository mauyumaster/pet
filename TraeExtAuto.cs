// 「Trae 重启后自动补投」—— 把"Trae 一重启扩展就哑掉"这件事变成桌宠自己的责任。
//
// 为什么必须有它（2026-10-01 用户拍板）：
//   Trae SOLO CN 启动时**不扫**用户扩展目录（`solo-lite`，见 TraeExtInstaller.FolderNameAt），
//   扩展**只在**「Trae 正在跑 且 扩展目录里冒出一个新目录名」的那一刻被加载。于是每次 Trae 重启
//   之后自动续期都是哑的；而令牌 14 天到期 ⇒ 靠"人记得点按钮"就等于没有。桌面端本来就是常驻程序，
//   这件事该它自己盯。
//
// ⚠ 三条纪律（都是本项目踩过的坑，别删）：
//   ① **判据/自测里绝不许跑它** —— 它真会往 Trae 的目录里写。闸门两道：本类的 `Enabled`，
//      以及 PetWindow 只在 `!SelfTestMode` 时挂它（同「判据不许写生产日志」那条纪律：
//      判据跑在用户真机上，用户真有一个 Trae）。
//   ② **每个 Trae 会话最多投一次** —— 一个 id 一个会话只激活一次，多投只是堆副本
//      （判定收在 `TraeExtInstaller.DecideAuto` 的第 ③ 条，这里不重复一遍）。
//   ③ **不删任何东西** —— 删旧换不回激活，还会往 Trae 的 `.obsolete` 里写字（见 `DeployPlan`）。
//
// 判据：`--traeexttest` 验的是 `DecideAuto` 那个纯函数（本类只做"什么时候问、问了之后怎么做"）。
using System;
using System.IO;

namespace AzhuPet
{
    internal static class TraeExtAuto
    {
        /// <summary>总闸。默认开；判据/自测路径必须关掉（见文件头 ①）。</summary>
        public static bool Enabled = true;

        /// <summary>自检间隔（秒）。⚠ 不能挂在 120 ms 的宿主节拍上无脑跑 —— 每次自检都要枚举几百个进程、
        /// 并对名字含 trae 的开句柄读启动时刻；而"Trae 重启"是分钟级事件，两分钟一次绰绰有余。</summary>
        public static double CheckEverySec = 120.0;

        /// <summary>最近一次自检的结论原话（**无论投没投**）。界面与日志读它，谁也别重算。</summary>
        public static string LastNote;

        /// <summary>自动补投成功的次数。只观测（`--traeexttest` 会打印它），不参与任何判断。</summary>
        public static long Deploys;

        private static double _lastCheckSec = double.NegativeInfinity;

        /// <summary>宿主节拍（`SlowTick`，120 ms）调它，节流在内部。返回 null = 这次没动作、什么都没发生；
        /// 非 null = 刚刚**真的补投过一份**，给日志与气泡用。</summary>
        public static string Poll(double nowSec)
        {
            if (!Enabled) return null;
            if (nowSec - _lastCheckSec < CheckEverySec) return null;
            _lastCheckSec = nowSec;
            return RunOnce();
        }

        /// <summary>真判一次并按需投放。返回 null = 这次没动作；否则是给日志/气泡看的一句话。
        /// ⚠ 判据**不调它**（它会写盘）—— 判据调 `TraeExtInstaller.DecideAuto` 那个纯函数。
        /// ⚠ 任何异常都不许漏出去：它挂在桌宠的常驻计时器上，抛一次就是一次崩溃。</summary>
        public static string RunOnce()
        {
            try
            {
                var d = TraeExtInstaller.DecideAutoNow();
                LastNote = d.Why;
                if (!d.Should) return null;

                string detail;
                string err = TraeExtInstaller.Install(out detail);
                if (err != null) { LastNote = "自动补投失败：" + err; return LastNote; }
                Deploys++;
                LastNote = "已自动补投：" + detail;
                return LastNote;
            }
            catch (Exception ex)
            {
                LastNote = "自动补投异常：" + ex.Message;
                return LastNote;
            }
        }

        /// <summary>给判据用：把节流计时归零，好让下一次 `Poll` 立刻真的走一遍。
        /// ⚠ 刻意**只清节流、不碰 `Enabled`** —— 两件事分开，判据才不会顺手把总闸打开。</summary>
        public static void ResetThrottle() { _lastCheckSec = double.NegativeInfinity; }

        /// <summary>命令行入口（`--traeextauto`）：只**读**三份现场并打印判定，**一个字节都不写**。
        /// ⚠ 存在的理由：「它为什么没自动补投」这个问题必须能当场回答 —— 否则用户只看到"没动静"，
        ///   而没动静的原因有四种（见 `TraeExtInstaller.DecideAuto`），猜是猜不出来的。
        /// ⚠ 刻意做成**只读**：真投一次会动 Trae 的目录，那件事只该由"用户点按钮"或"宿主自检"触发。
        /// ⚠ 本机实测（2026-10-01）它会打印出 `Trae 进程 15 个 · 启动 10-01 17:26:01` —— 这是
        ///   `TraeStartUtc()` 唯一的外部证据：读不到启动时刻时它会静默返回 null，
        ///   而"永远不触发"和"当前不需要补投"在界面上长得一模一样。</summary>
        public static int DiagRun()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            Console.WriteLine("---- Trae 重启后自动补投（诊断，只读）----");
            Console.WriteLine("总闸      : " + (Enabled ? "开" : "关") + " · 自检间隔 " + CheckEverySec + " 秒");

            var ours = TraeExtInstaller.OurFolders(TraeExtInstaller.ResolveExtensionsDir());
            Console.WriteLine("扩展副本  : " + ours.Count + " 份"
                + (ours.Count > 0 ? "（最近一份 " + Path.GetFileName(ours[0]) + "）" : ""));

            var start = TraeExtInstaller.TraeStartUtc();
            int n = 0;
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcesses())
                {
                    try { if (p.ProcessName.IndexOf("trae", StringComparison.OrdinalIgnoreCase) >= 0) n++; }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            Console.WriteLine("Trae 进程 : " + n + " 个"
                + (start.HasValue
                   ? " · 本次启动 " + start.Value.ToLocalTime().ToString("MM-dd HH:mm:ss")
                   : " · **读不到启动时刻**"));
            var run = TraeExtInstaller.SessionLive(start, TraeExtInstaller.LogTime());
            var log = TraeExtInstaller.LogTime();
            Console.WriteLine("扩展日志  : " + (log.HasValue
                ? log.Value.ToLocalTime().ToString("MM-dd HH:mm:ss") + "  " + TraeExtInstaller.TmpLogPath()
                : "**不存在**（＝扩展从来没有真的跑起来过）"));
            Console.WriteLine("本会话    : " + (run == true ? "扩展已跑过" : run == false ? "扩展还没跑过" : "判不了"));
            Console.WriteLine("自动补投累计: " + Deploys + " 次");

            var d = TraeExtInstaller.DecideAutoNow();
            Console.WriteLine();
            Console.WriteLine(d.Should ? "[该补投]" : "[不需要补投]");
            Console.WriteLine("  " + d.Why);
            Console.WriteLine();
            Console.WriteLine("⚠ 本命令只读；要真投一次，用「余额配置 → 安装同步扩展」。");
            return 0;
        }
    }
}
