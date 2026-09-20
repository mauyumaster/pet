// 感知层（P0）—— 只回答一件事：前台换了个应用，这件事值不值得她知道。
//
// ⚠ 为什么「读窗口」与「决定要不要报」必须拆开：
//   Probe() 要吃真系统 API（前台窗口句柄 → 进程名），离线测不了；
//   Decide() 是纯函数，能被一串合成采样逼红。
//   「判据只能被真外部系统验 ＝ 没资格失败」——所以能抽出来的必须抽出来。
//
// ⚠ 为什么事件的单位不是「前台窗口变了」，而是「在一个应用里待够久之后才换」：
//   真实使用里切换极其频繁（点任务栏、切标签页、开菜单都算），
//   没有停留阈值 ⇒ 你每点一次任务栏她就开一次口。那不是活感，是骚扰。
//   素材贫乏的时候，**时机本身就是信号**。
//
// 判据：--watchtest（离线，不读真窗口，喂合成采样序列）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AzhuPet
{
    /// <summary>一次值得她知道的前台变化。</summary>
    internal sealed class Observation
    {
        /// <summary>进入的这个应用（进程名，如 WINWORD；不带 .exe）。P0 用它当去重键。</summary>
        public string Process;
        /// <summary>窗口标题。P0 只作为「她在看什么」的线索，**不参与判断**（标题变得太快）。</summary>
        public string Title;
        /// <summary>上一个应用停留了多少秒 —— 这条观察的**理由**，也是她开口时唯一的时间感。</summary>
        public double PrevDwell;
        public DateTime At;

        /// <summary>
        /// ⚠⚠ 这次观察是**你点的**（托盘「让她说一句」），不是她自己决定的。
        ///
        /// 为什么必须标出来（2026-09-20 用户实拍）：
        ///   手动触发那条路复用「他刚换到 X，上一个窗口他待了 N 秒」这个模板，
        ///   但**那一刻根本没有发生过换窗口这件事** —— 而且 N 秒是上一次自动观察
        ///   **残留下来**的读数（自动感知的状态机当时一步都没走）。
        ///   于是她对着哔哩哔哩说出：「6秒，够你在里面找到想要的？」
        ///   ⇒ 她只能顺着一个假前提编。**手动触发只该回答一件事：现在屏幕上是什么。**
        /// </summary>
        public bool Manual;

        /// <summary>
        /// 这次观察的**种类**。null ＝ 换应用（原有观察）；"roast-title"／"roast-idle" ＝ 吐槽触发。
        /// ⚠ 判据按它分流（BuildPrompt 的说法、stub 的模板、小时总结的统计都按它排除）。
        /// </summary>
        public string Reason;

        public string Describe()
        {
            if (Reason != null && Reason.StartsWith("roast")) return Process + "（吐槽触发：" + Reason + "）";
            return Process + "（先前那个待了 " + PrevDwell.ToString("0") + " 秒）";
        }
    }

    /// <summary>
    /// 一次「前台窗口」读数（**含句柄**）。
    /// ⚠ 为什么要句柄：读屏要截的是「那个窗口」，不是「现在最前面那个」。光有进程名不够 ——
    ///   你点一下托盘，前台就变成 pet 自己，此时「前台是谁」这个问题**对她没有意义**。
    /// </summary>
    internal sealed class Fg
    {
        public IntPtr Hwnd;
        public string Proc, Title, Cls;
        public DateTime At;
        /// <summary>shell 自己的窗口（桌面／任务栏／托盘／开始菜单…）—— 它们不是一个「正在用的应用」。
        /// ⚠ 由 `Native.BelongsToShell(proc, cls)` 判（**进程为主 ＋ 类名兜底**；只查类名会漏）。</summary>
        public bool Shell;

        public double AgeSec { get { return (DateTime.Now - At).TotalSeconds; } }
    }

    internal static class Watcher
    {
        /// <summary>在一个应用里待够这么久，之后的切换才算一件事。可调。
        /// ⚠ 2026-09-20 起这是**基线值**，实际阈值由 `AdaptiveMinDwell(switchesLast30Min)` 给出。</summary>
        public static double MinDwell = 8.0;

        /// <summary>切换频繁时的下探值（秒）。用户 2026-09-20 拍板：专注时不动、切换频繁时降低。</summary>
        public static double MinDwellBusy = 4.0;

        /// <summary>负对照开关（--no-adaptive 置 false）：关掉后恒用基线 `MinDwell`，adaptive 判据必须红。</summary>
        public static bool AdaptiveOn = true;

        /// <summary>
        /// **纯函数**：按「你近半小时换了多少次应用」给出这一拍的停留阈值。
        ///
        /// ⚠⚠ 为什么不写成一个固定数字（用户 2026-09-20 的原话）：
        ///   「你专注时不动，切换频繁时降低」—— 同一个 8 秒，在两种状态下含义相反：
        ///   专注写文档时，8 秒是**必要**的（否则每次切出去看一眼文件都会被报成一次观察）；
        ///   而在疯狂切换的调研状态里，8 秒会把大量真实活动**滤掉**，她显得迟钝。
        ///   ⇒ 阈值该跟着「活动的节奏」走，而不是跟着「我觉得多少秒合适」走。
        ///
        /// ⚠ 输入是**切换次数**这个可数的量，不是「专注度」那种没有真值的抽象量 ——
        ///   后者没法写成判据（本仓：判据的输入必须能自己钉死）。
        /// </summary>
        public static double AdaptiveMinDwell(int switchesLast30Min)
        {
            if (!AdaptiveOn) return MinDwell;                                              // 负对照：恒用基线
            if (switchesLast30Min >= 20) return MinDwellBusy;                              // 一分钟都不到就换一次
            if (switchesLast30Min <= 6) return MinDwell;                                   // 专注：基线
            // 6 → 20 之间线性下探，避免阈值自己跳变（跳变会让她的出现频率忽然改档）
            double t = (switchesLast30Min - 6.0) / (20.0 - 6.0);
            return MinDwell - (MinDwell - MinDwellBusy) * t;
        }

        /// <summary>去重开关。**关掉就是负对照** —— 判据必须在它关掉时变红，否则它的绿没有信息量。</summary>
        public static bool Dedupe = true;

        /// <summary>
        /// 她**最近一次真正看到的、不是她自己的**窗口。
        ///
        /// ⚠⚠ 为什么必须有这个东西（2026-09-20 用户实拍）：
        ///   她自己的窗口会合法地成为前台 —— 你点一下托盘，托盘菜单属于 pet.exe，
        ///   菜单关掉之后前台就落回她自己的窗口。此时若仍按「当前前台」去截，
        ///   结果必然是「**看见了：pet**」——她只看见了自己。
        ///   ⇒ 「她在看什么」这个问题，**当前前台**在「前台＝她自己」时不是答案，
        ///     答案是她**上一条**读数。这也是本仓那条「同一份数据两个落点」的另一面：
        ///     观察所用的口径与截图所用的口径必须**是同一个读数**，不能各问一次。
        /// </summary>
        public static Fg LastForeign;

        /// <summary>仅供负对照（`--no-lastforeign`）：只信当前前台，不许回退到上一条读数。</summary>
        public static bool AllowLastForeign = true;

        private static string _self;

        /// <summary>本进程名。用来把「她自己的窗口」排除在观察之外 ——
        /// 否则你点一下她的聊天窗，她就会看着自己说一句。</summary>
        public static string SelfName()
        {
            if (_self != null) return _self;
            try { _self = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "pet"; }
            catch { _self = "pet"; }
            return _self;
        }

        /// <summary>
        /// 当前前台应用：(进程名, 窗口标题)。读不到时对应项为 null。
        /// ⚠ 这里绝不抛异常 —— 感知层挂掉不应该把桌宠带走（权限受限的进程会让 GetProcessById 抛）。
        /// ⚠ 它**顺带**会把「不是她自己的窗口」记进 `LastForeign`（唯一的口径来源）。
        /// </summary>
        public static Tuple<string, string> Probe()
        {
            var f = ProbeFg();
            Remember(f);
            return AsApp(f);
        }

        /// <summary>
        /// **纯函数**：把一次读数折算成「此刻有没有一个你正在用的应用」。
        ///
        /// ⚠⚠ shell 自己的窗口（桌面／任务栏／托盘溢出窗口）**不是**一个「你在用的应用」，
        ///   一律折算成「没有前台应用」。本案（2026-09-20 实拍）：她开口说的是
        ///   「他刚换到「资源管理器」」—— 而用户只是**点了一下任务栏**，屏幕上正开着哔哩哔哩。
        ///   ⚠ 这不是新规矩，是**把三层口径统一**：`Remember`（记「上一个真实在用的窗口」）
        ///   与 `Usable`／`ChooseReadTarget`（读屏该读谁）**早就都把 shell 排除了**，
        ///   只有「观察」这一层漏了 —— 于是同一个「你在用什么」在三处各说一套。
        /// ⚠ 抽成纯函数才能离线验，而且**必须带正对照**：explorer 打开的**文件夹窗口**
        ///   不是 shell 类，必须照常放行（否则「一刀切掉 explorer」也能全绿）。
        /// </summary>
        public static Tuple<string, string> AsApp(Fg f)
        {
            if (f == null) return Tuple.Create<string, string>(null, null);
            if (f.Shell) return Tuple.Create<string, string>(null, null);
            return Tuple.Create(f.Proc, f.Title);
        }

        /// <summary>前台窗口的完整读数（含句柄／类名／是否 shell）。读不到返回 null，绝不抛。</summary>
        public static Fg ProbeFg()
        {
            try
            {
                IntPtr h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero) return null;

                int pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid <= 0) return null;

                var sb = new StringBuilder(512);
                Native.GetWindowText(h, sb, sb.Capacity);
                string cls = Native.ClassOf(h);

                string proc = null;
                try
                {
                    using (var p = Process.GetProcessById(pid)) proc = p.ProcessName;
                }
                catch { proc = null; }   // 进程已退出／权限不够 —— 当作「读不到」，不当作「切到了某个应用」

                return new Fg
                {
                    Hwnd = h, Proc = proc, Title = sb.ToString(), Cls = cls,
                    // ⚠ 判「谁算外壳」用**进程为主**的那条（`BelongsToShell`）——
                    //   只看类名会漏掉这台机器上真实的壳窗口（2026-09-20：托盘溢出面板）。
                    Shell = Native.BelongsToShell(proc, cls), At = DateTime.Now,
                };
            }
            catch { return null; }
        }

        /// <summary>
        /// 记下「你上一个真实在用的窗口」。**只有**非她自己、非 shell、读得到进程名的窗口才算数；
        /// 其余一律**不改** `LastForeign`（不是清空 —— 清空等于把刚才那个应用忘掉，那正是本案的 bug）。
        /// </summary>
        public static void Remember(Fg f)
        {
            if (f == null) return;
            if (string.IsNullOrEmpty(f.Proc)) return;
            if (f.Proc == SelfName()) return;
            if (f.Shell) return;
            LastForeign = f;
        }

        /// <summary>
        /// 采样一次并记录（1 秒一次，由 SlowTick 调用）。
        /// ⚠⚠ 它**必须独立于「自发说话」开关**：否则你把自发说话关掉，托盘那项
        ///   「她看见了什么…」就永远报「还没见过别的窗口」—— 一个功能的可用性
        ///   寄生在另一个开关的副作用上，正是本仓踩过的那类坑。
        /// </summary>
        public static void RememberForeground()
        {
            Remember(ProbeFg());
        }

        /// <summary>
        /// **纯函数**：该读哪个窗口。
        /// ① 当前前台若是一个「正在用的应用」→ 用它（例如你在别处敲命令时手动触发）；
        /// ② 否则（当前前台是**她自己**／桌面／读不到）→ 回退到上一条真实读数；
        /// ③ 负对照时不许回退 —— 直接把「当前」原样交出去，让下游报出可读原因。
        /// ⚠ 抽成纯函数是为了能用合成输入验「前台是她自己时选谁」—— 这个条件
        ///   没法在无人时真造出来（需要你去点托盘），不能靠它只有一条真机路径。
        /// </summary>
        public static Fg ChooseReadTarget(Fg current, Fg last, string selfName, bool allowLast)
        {
            if (Usable(current, selfName)) return current;
            if (allowLast && Usable(last, selfName)) return last;
            return current;      // 可能是 null、可能是她自己、可能是桌面 —— 由下游报告「为什么读不了」
        }

        private static bool Usable(Fg f, string selfName)
        {
            return f != null && !string.IsNullOrEmpty(f.Proc) && f.Proc != (selfName ?? "pet") && !f.Shell;
        }

        /// <summary>读屏该对准的那个窗口。见 `ChooseReadTarget`。</summary>
        public static Fg ReadTarget()
        {
            return ChooseReadTarget(ProbeFg(), LastForeign, SelfName(), AllowLastForeign);
        }

        /// <summary>
        /// **纯函数**：这次采样算不算一次值得她知道的变化。返回 null ＝ 不值得。
        /// ⚠ 纯函数才能被 --watchtest 用合成序列逼红；写进循环里就只能靠「真的去点任务栏」验，那不叫判据。
        /// ⚠ 阈值参数化（2026-09-20，用户拍板「自适应」）：调用方传 `AdaptiveMinDwell(...)` 的结果进来。
        ///   不传就用静态 `MinDwell`（旧调用点／负对照 `-no-adaptive` 走这条）。
        /// </summary>
        public static Observation Decide(string prevProc, double prevDwell, string curProc, string curTitle,
                                         DateTime now, string selfName = null, double? minDwell = null)
        {
            if (string.IsNullOrEmpty(curProc)) return null;               // 锁屏／切换中／读不到：没有前台
            if (curProc == (selfName ?? SelfName())) return null;         // 她自己（桌宠窗、聊天窗、配置窗）
            if (Dedupe && curProc == prevProc) return null;               // 还没换走，同一应用
            if (string.IsNullOrEmpty(prevProc)) return null;              // 第一次采样：没有「上一个」，谈不上切换
            if (prevDwell < (minDwell ?? MinDwell)) return null;          // 上一个只是路过

            return new Observation
            {
                Process = curProc, Title = curTitle, PrevDwell = prevDwell, At = now,
            };
        }
    }

    /// <summary>
    /// 采样循环。有状态，但 **probe 可注入** ⇒ 离线可测。
    /// ⚠ 停留时长的算法是「累计」而不是「一个采样间隔」：前者才是「这个应用待了多久」，
    ///   后者在任何采样间隔下都算不出 8 秒以上的停留（会把阈值变成空转）。
    /// </summary>
    internal sealed class WatchLoop
    {
        private readonly Func<Tuple<string, string>> _probe;
        private string _prevProc, _prevTitle;
        private DateTime _prevAt;
        private bool _started;
        private double _dwell;          // 上一个（或仍在的）应用累计停留秒数

        public int Samples, Reported;
        public double LastDwell;

        /// <summary>
        /// 近半小时的**换应用**次数（滚动窗口）。给 `AdaptiveMinDwell` 用。
        /// ⚠ 只装「换应用」这一个事件 —— 不装采样、不装标题变化：
        ///   标题变化在浏览器里可以一秒几十次，混进来的话阈值会被瞬间拉到最低
        ///   （判据里那条 `titleChurnDoesNotCountAsSwitching` 就是钉这件事的）。
        /// </summary>
        private readonly Queue<DateTime> _switches = new Queue<DateTime>();

        /// <summary>近半小时切换次数（只读）。</summary>
        public int SwitchesLast30Min(DateTime now)
        {
            PruneSwitches(now);
            return _switches.Count;
        }

        private void PruneSwitches(DateTime now)
        {
            while (_switches.Count > 0 && (now - _switches.Peek()).TotalMinutes > 30) _switches.Dequeue();
        }

        // ---- 吐槽通道（Roast.cs）----
        /// <summary>负对照开关（--no-roast 置 false）：关掉后 Sample 永不产出 roast 观察，判据必须红。</summary>
        public static bool RoastOn = true;
        /// <summary>上次她**任何一次**开口的时刻（含换应用那句）。由 Brain 接线（Gate.LastSpeak）。
        /// ⚠ 可注入是判据的要求：没有它，触发条件里就混进了「闸门的内部状态」这种钉不死的东西。</summary>
        public Func<DateTime> LastSpoke;
        private DateTime _lastRoastAt;
        public int Roasts;

        /// <summary>最近一次读到的前台应用。给「让她说一句」用 —— 只读，不参与状态机。</summary>
        public string Current { get { return _prevProc; } }

        public WatchLoop(Func<Tuple<string, string>> probe) { _probe = probe; }

        /// <summary>采样一次。返回非 null ＝ 这一次值得她知道。</summary>
        public Observation Sample(DateTime now)
        {
            var t = _probe();
            string proc = t == null ? null : t.Item1;
            string title = t == null ? null : t.Item2;
            Samples++;

            // ⚠ 读不到前台、或前台是她自己 —— **完全不参与状态机**（不更新 _prevProc、不累计停留）。
            //   只「不报」却仍更新状态的话：A → 点开她的聊天窗 → 关掉回到 A，
            //   会被算成一次切换（因为 _prevProc 一度变成了她自己），于是你只是点了她一下，
            //   她就冒出一句「换到编辑器了」。跳过比不报更干净。
            if (string.IsNullOrEmpty(proc) || proc == Watcher.SelfName()) return null;

            double sincePrev = _started ? (now - _prevAt).TotalSeconds : 0;
            bool stillSame = proc != null && proc == _prevProc;
            // ⚠ 两种情况下都要加 sincePrev：
            //   上一个应用是「一直持续到本次采样时刻」才被发现已经切走的 —— 不加这一段，
            //   它的停留时长就少算一个采样间隔（8 秒的阈值会因此几乎达不到，判据拿到 9 而不是 12）。
            double prevTotal = _dwell + sincePrev;

            // ⚠ 第一次采样不报（没有「上一个」）—— 否则每次启动她都会对当时的窗口说一句。
            // ⚠⚠ 阈值**每一拍现算**：`AdaptiveMinDwell` 吃的是「近半小时换了几次」，
            //   而那个数字刚刚可能被下面这行改掉（这一拍就是一次切换）——
            //   所以先在**本拍之前**的窗口上算，才不会自己影响自己。
            double minDwell = Watcher.AdaptiveMinDwell(SwitchesLast30Min(now));
            bool switched = _started && proc != _prevProc && !string.IsNullOrEmpty(_prevProc);
            Observation obs = _started ? Watcher.Decide(_prevProc, prevTotal, proc, title, now, null, minDwell) : null;

            // ---- 吐槽通道：只在「同一应用仍在前台」的拍子里找机会（换应用归上面的 Decide 管）----
            // ⚠ 排在状态机更新**之前**：比较的是「上一拍标题 vs 本拍标题」，晚了就被覆盖。
            // ⚠ 产出 roast 观察时 PrevDwell 填**当前应用的累计停留**（prompt 里「已经待了 N 秒」
            //   的素材）；小时总结的统计按 Reason 排除 roast 行，所以不会重复计入时长。
            if (obs == null && RoastOn && LastSpoke != null && _started && stillSame)
            {
                obs = Roast.Decide(proc, _prevProc, title, _prevTitle, now,
                                   LastSpoke(), _lastRoastAt, Roast.CooldownSec, Roast.IdleNudgeSec);
                if (obs != null)
                {
                    obs.PrevDwell = prevTotal;      // 当前应用已待时长（stillSame ⇒ 就是它自己的累计）
                    _lastRoastAt = now;
                    Roasts++;
                }
            }

            // ⚠ 只有**真的换走了**才记一笔（`switched` 而不是 `obs != null`）：
            //   换了但上一个没待够（obs == null）同样是一次切换，它也该把阈值往下带。
            if (switched) _switches.Enqueue(now);

            _dwell = stillSame ? _dwell + sincePrev : 0;
            _prevProc = proc; _prevTitle = title; _prevAt = now; _started = true;
            LastDwell = prevTotal;
            if (obs != null) Reported++;
            return obs;
        }
    }
}
