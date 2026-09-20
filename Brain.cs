// 表达环（P0 第四环）—— 把前三环**接起来**，并决定「她说的话送到哪里」。
//
// ⚠⚠ 为什么这一环要单独抽一个类，而不是在 PetWindow.SlowTick 里直接写十行：
//   前三环（Watcher／StubSpeaker／SpeechGate／Memory）一度**全是悬空代码** ——
//   它们只在 --watchtest 里被调用，运行时主循环里一个调用点都没有。
//   于是判据 16/16 全绿，桌宠却一个字都不会说。
//   教训：**判据全绿证明的是「逻辑对」，不能证明「接上了」。**
//   所以这一环的重点不是逻辑（逻辑上一环已验过），而是**把接线本身做成可测的**：
//   probe 注入、时钟注入、speaker 注入、出口是回调 —— 于是「喂一串窗口切换，
//   它会不会真的吐出一句话」变成一条**有资格变红**的判据（--speaktest）。
//
// ⚠ 线程纪律：真 LLM 是异步的，而 `_out` 回调要落到 WPF 控件上。
//   所以这里**绝不 await / 绝不阻塞**（项目铁律：不在渲染线程 await），
//   异步结果用 ContinueWith 抛回回调，由 UI 层自己 Dispatcher 切回去。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AzhuPet
{
    /// <summary>
    /// 气泡占用仲裁。
    /// ⚠ 必要性：`_bubbleWin` **一个窗口两个用途**（查状态 ／ 她说话）—— 本项目的老毛病
    ///   （同类：「同一份数据两个落点」「一个进程干两件事」）。直接接台词会互相顶掉。
    /// 规则：**状态可以打断台词，台词不许打断状态**。
    /// 纯逻辑、不碰 UI ⇒ 能离线逼红。
    /// </summary>
    internal sealed class BubbleArbiter
    {
        public const int None = 0, Status = 1, Speech = 2;

        public int Kind { get; private set; }

        /// <summary>台词现在能显示吗。**只问不改** —— 有副作用的「检查」会让判据很难写。</summary>
        public bool AllowSpeech() { return Kind != Status; }

        /// <summary>气泡开始显示某一类内容。</summary>
        public void Note(int kind) { Kind = kind; }

        public void Clear() { Kind = None; }
    }

    /// <summary>
    /// P0 的接线：感知（WatchLoop）→ 闸门（SpeechGate）→ 决策（ISpeaker）→ 表达（回调）。
    /// 所有外部依赖（时钟、前台读取、说话人、出口）都是注入的 ⇒ 可离线驱动。
    /// </summary>
    internal sealed class Brain
    {
        private readonly Func<Tuple<string, string>> _probe;
        private readonly Func<DateTime> _now;
        private readonly Action<Verdict, Observation, DateTime> _out;
        private readonly Action<string> _trace;
        private readonly WatchLoop _watch;

        public readonly BubbleArbiter Bubble = new BubbleArbiter();
        public ISpeaker Speaker;
        public SpeechGate Gate = new SpeechGate();

        /// <summary>⚠ **只供负对照**：复现「SpeakNow 只会读 IsCompleted」的旧行为，
        /// 用来把 speakNowAcceptsAsync 那条判据逼红 —— 判据没被逼红过，它的绿就没有信息量。
        /// 运行时恒为 false。</summary>
        public bool RefuseAsyncSpeakNow;

        /// <summary>
        /// 手动触发时「她现在在看哪个窗口」的来源。默认＝`Watcher.ReadTarget()`（**唯一口径**）。
        /// ⚠ 抽成字段的理由与 `LlmSpeaker.ScreenSource` 相同：判据的输入必须自己钉死，
        ///   不能真的去读你的桌面 —— 否则同一条判据的绿/红取决于你今天开着哪个窗口。
        /// </summary>
        public Func<Fg> Target = Watcher.ReadTarget;

        /// <summary>⚠ **只供负对照**（`--speak-foreground`）：复现旧行为 —— 手动触发用
        /// 「那一瞬间的当前前台」，于是前台是任务栏／她自己的菜单时，她会拿一份很旧的观察说话。
        /// 用来把 `speakNowUsesReadTarget` 逼红。运行时恒为 false。</summary>
        public bool SpeakNowUsesForeground;

        public int Samples, Observations, Spoken, Suppressed;
        public DateTime LastTick = DateTime.MinValue;

        private bool _pending;   // 真 LLM 在途：防止异步返回期间又发起一次

        public Brain(Func<Tuple<string, string>> probe, Func<DateTime> now,
                     ISpeaker speaker, Action<Verdict, Observation, DateTime> output,
                     Action<string> trace = null)
        {
            _probe = probe;
            _now = now ?? (() => DateTime.Now);
            Speaker = speaker;
            _out = output;
            _trace = trace;
            _watch = new WatchLoop(probe);
        }

        /// <summary>
        /// 吐槽通道的接线：把「她上次开口的时刻」喂给采样循环。
        /// ⚠ 单独一个方法而不是塞进构造函数 —— 判据要能**不接**它跑（负对照：
        ///   不接线时 Sample 永不产出 roast 观察，roast 判据必须点名变红）。
        /// </summary>
        public void WireRoast()
        {
            _watch.LastSpoke = () => Gate.LastSpeak;
        }

        /// <summary>最近一次观察到的应用（给托盘「让她说一句」用）。</summary>
        public string CurrentApp { get { return _watch.Current; } }

        public Verdict Tick() { return Tick(_now()); }

        /// <summary>
        /// 一次采样。返回非 null ＝ 这一拍就**产生了台词或被闸门拦下**（同步 speaker，即 stub）。
        /// 真 LLM 是异步的：返回 null，话稍后从 output 回调出来。
        /// </summary>
        public Verdict Tick(DateTime now)
        {
            LastTick = now;
            Samples++;
            Observation obs;
            try { obs = _watch.Sample(now); }
            catch (Exception ex) { Trace("感知异常：" + ex.Message); return null; }
            if (obs == null) return null;      // 不值得她知道 —— 不记、不说（否则一天几万条）

            Observations++;

            string why;
            if (!Gate.Allow(now, out why) || _pending)
            {
                if (_pending) why = "上一句还在生成中";
                var veto = new Verdict { Speak = false, Why = why };
                Suppressed++;
                Memory.Append(obs, veto, Name(), now);
                _out?.Invoke(veto, obs, now);
                return veto;
            }

            Task<Verdict> task;
            try { task = Speaker.SayAsync(obs); }
            catch (Exception ex) { return Deliver(new Verdict { Speak = false, Why = "说话人异常：" + ex.Message }, obs, now); }
            if (task == null) return Deliver(new Verdict { Speak = false, Why = "说话人返回了空任务" }, obs, now);

            if (task.IsCompleted)
            {
                Verdict v;
                try { v = task.GetAwaiter().GetResult(); }
                catch (Exception ex) { v = new Verdict { Speak = false, Why = "说话人抛错：" + ex.Message }; }
                return Deliver(v, obs, now);
            }

            // ---- 异步路径（真 LLM）----
            // ⚠ 这里**绝不能** .Wait()/.Result：那会卡住渲染线程，桌宠当场僵住。
            //   也不 await：Tick 是同步方法，且调用方是 DispatcherTimer。
            _pending = true;
            var at = now;
            task.ContinueWith(t =>
            {
                Verdict v;
                try { v = t.Status == TaskStatus.RanToCompletion ? t.Result : new Verdict { Speak = false, Why = "说话任务失败：" + t.Exception?.GetBaseException().Message }; }
                catch (Exception ex) { v = new Verdict { Speak = false, Why = "说话任务异常：" + ex.Message }; }
                lock (this) { _pending = false; }
                Deliver(v, obs, at);
            });
            return null;
        }

        /// <summary>
        /// 手动触发一次（托盘「让她说一句」/ --speakprobe）。
        /// 绕过停留阈值与闸门 —— 是你点的，不是她自作主张。⚠ 但**仍然要过气泡仲裁**：
        /// 否则会出现「查着余额，她忽然插一句」。
        /// </summary>
        public Verdict SpeakNow()
        {
            DateTime now = _now();
            string proc = null, title = null;

            // ---- ① 她该看哪个窗口：问**唯一的口径**，而不是「这一刻的前台」----
            // ⚠⚠ 2026-09-20 实拍那句「6秒，够你在里面找到想要的？」就坏在这里：
            //   你点的是托盘菜单，而**菜单属于 pet.exe** ⇒ 「当前前台」是**她自己**；
            //   于是回退到 `CurrentApp`（上一条自动观察）—— 那条观察是**很久以前**的一次
            //   任务栏点击（explorer），秒数也是那时冻结下来的。她拿到的前一句于是成了
            //   「他刚换到「资源管理器」，上一个窗口他待了 6 秒」。
            //   `Watcher.ReadTarget()` 本来就是为这件事写的（前台是她自己／桌面时，答案是
            //   她**上一条真读数**，而那一条每秒都在刷新）—— 手动触发这条路漏了用它。
            Fg t = null;
            if (!SpeakNowUsesForeground && Target != null)
            {
                try { t = Target(); }
                catch { t = null; }     // 读不到就退到旧路径：感知坏了不该让她说不出话
            }

            if (t != null && !string.IsNullOrEmpty(t.Proc))
            {
                proc = t.Proc; title = t.Title;
            }
            else
            {
                var tp = _probe();
                proc = tp == null ? null : tp.Item1;
                title = tp == null ? null : tp.Item2;

                // ⚠⚠ 前台可能正是**她自己**（你刚点了托盘菜单，或测试刚把她的窗口弹到前面）。
                //   不排除的话，你点一下托盘她就会说「换到 pet 了」—— 把自己当成了观察对象。
                //   （这不是假想：--speakvis 首跑读到的原话就是「……换到pet了。」）
                //   回退到最近一次真实观察；连它都没有时才交给说话人自己决定怎么措辞。
                if (string.IsNullOrEmpty(proc) || proc == Watcher.SelfName())
                {
                    proc = CurrentApp;
                    title = null;   // 回退后原标题不再对应这个应用（宁可没有，也不能张冠李戴）
                }
            }

            var obs = new Observation
            {
                Process = proc,
                Title = title,
                // ⚠⚠ 手动触发**没有**「上一个窗口他待了多久」这件事：填 0 并标 Manual=true，
                //   由 `LlmSpeaker.BuildPrompt` 换一套说法。
                //   原来这里填的是 `_watch.LastDwell` —— 而它只在**自动感知前进时**才更新，
                //   于是关掉「她会自己说话」之后它是个**冻结的旧值**（又一个「功能可用性
                //   寄生在另一个开关上」：手动触发的正确性取决于自动感知在跑）。
                PrevDwell = 0,
                Manual = true,
                At = now,
            };
            Verdict v;
            try
            {
                var task = Speaker.SayAsync(obs);
                if (task == null) return Deliver(new Verdict { Speak = false, Why = "说话人返回了空任务" }, obs, now, countGate: false);
                if (task.IsCompleted)
                {
                    try { v = task.GetAwaiter().GetResult(); }
                    catch (Exception ex) { v = new Verdict { Speak = false, Why = "说话人抛错：" + ex.Message }; }
                    return Deliver(v, obs, now, countGate: false);
                }

                // 负对照分支：复现旧行为（见 RefuseAsyncSpeakNow 的注释）。
                if (RefuseAsyncSpeakNow)
                    return Deliver(new Verdict { Speak = false, Why = "手动触发不支持异步说话人（负对照复现旧行为）" },
                                   obs, now, countGate: false);

                // ⚠⚠ 异步说话人（真模型**必然**是异步的）绝不能当场判「不支持」。
                //   原先这里写的是 `task.IsCompleted ? ... : 不支持异步` —— 于是在模型模式下
                //   点托盘「让她说一句」会**静默无效**：没有任何提示，现象只是「她没说话」，
                //   与「坏了」无法区分。走与 Tick 相同的 ContinueWith 路径。
                _pending = true;
                var at = now;
                task.ContinueWith(t =>
                {
                    Verdict r;
                    try { r = t.Status == TaskStatus.RanToCompletion ? t.Result : new Verdict { Speak = false, Why = "说话任务失败：" + t.Exception?.GetBaseException().Message }; }
                    catch (Exception ex) { r = new Verdict { Speak = false, Why = "说话任务异常：" + ex.Message }; }
                    lock (this) { _pending = false; }
                    Deliver(r, obs, at, countGate: false);
                });
                return null;
            }
            catch (Exception ex) { return Deliver(new Verdict { Speak = false, Why = "说话人异常：" + ex.Message }, obs, now, countGate: false); }
        }

        private Verdict Deliver(Verdict v, Observation obs, DateTime now, bool countGate = true)
        {
            // ⚠ 2026-09-20 用户拍板「气泡流共存」：状态读数和发言不再互斥。
            //   以前这里「状态在位时台词让路」（AllowSpeech）—— 那是单气泡槽的产物；
            //   现在两者进入同一条气泡流，不再需要互相顶替。
            if (v != null && v.Speak)
            {
                if (countGate) Gate.Note(now);
                Spoken++;
            }
            Memory.Append(obs, v, Name(), now);
            _out?.Invoke(v, obs, now);
            return v;
        }

        private string Name() { return Speaker == null ? "(无说话人)" : Speaker.Name; }

        private void Trace(string s) { if (_trace != null) _trace(s); }
    }

    /// <summary>
    /// 真 LLM 说话人（走 TraeChat 明文通道，不扣积分）。
    /// ⚠ 与 StubSpeaker 的分工：stub 验**时机**，它验**话**。两者必须能互相替换 ——
    ///   否则你分不清「她今天说得无聊」是话的问题还是时机的问题。
    /// </summary>
    internal sealed class LlmSpeaker : ISpeaker
    {
        public string Name { get { return "trae"; } }

        /// <summary>
        /// 屏幕文字的来源。默认＝`OcrEye.TextForSpeaking`（**它自己管着「读／发」两个开关与敏感窗口**）。
        /// ⚠ 抽成可注入的字段，是为了让判据能离线验「文字真的到了说话人手里」这段接线 ——
        ///   否则只能靠真联网＋真屏幕，那不叫判据（本仓：接线只能真窗口验，但接线**存在**必须可断言）。
        /// </summary>
        public static Func<string> ScreenSource = OcrEye.TextForSpeaking;

        /// <summary>台词上限（人格页 §4：一两句，多了就不像说话，像播报）。可被 --speak-max 覆盖。</summary>
        public int MaxChars = 30;

        public async Task<Verdict> SayAsync(Observation obs)
        {
            var msgs = new List<object> { Msg("user", PromptFor(obs)) };
            var (ok, text) = await TraeChat.ChatAsync(msgs);
            if (!ok) return new Verdict { Speak = false, Why = "模型没回：" + One(text) };
            return Judge(text, MaxChars);
        }

        /// <summary>
        /// 长短与空值的裁决。**抽成纯函数** ⇒ 不必联网就能验「超长必须被丢弃」。
        /// ⚠ 超限**丢弃**、不截断：半句话比不说更糟，而且会让「为什么没说话」无法归因。
        /// </summary>
        public static Verdict Judge(string text, int max)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return new Verdict { Speak = false, Why = "模型回了空内容" };
            if (text.Length > max) return new Verdict { Speak = false, Text = text, Why = "超 " + max + " 字（丢弃，不截断）" };
            if (text.IndexOf('\n') >= 0) return new Verdict { Speak = false, Text = text, Why = "多行（一句台词不该分行）" };
            return new Verdict { Speak = true, Text = text, Why = "模型（trae）" };
        }

        /// <summary>
        /// **接线本体**：取屏幕文字 → 拼成提示。`SayAsync` 只调它这一个东西。
        /// ⚠⚠ 抽成独立的 public 静态函数，理由只有一个：**接线必须可断言**。
        ///   否则「文字真的到了说话人手里」这件事只能靠真联网＋真屏幕去验 ——
        ///   那不叫判据（本仓：判据的输入必须自己钉死，不能沿用外部可变状态）。
        ///   现在把 `ScreenSource` 换成合成源，离线就能验「进了没有、原不原样、崩了会不会哑」。
        /// </summary>
        public static string PromptFor(Observation obs)
        {
            string screen = null;
            try { screen = ScreenSource == null ? null : ScreenSource(); }
            catch { screen = null; }        // ⚠ 读屏失败绝不该让她说不出话（感知坏 ≠ 表达坏）
            return BuildPrompt(obs, screen);
        }

        /// <summary>
        /// 喂给模型的「她在看什么」。
        /// ⚠⚠ **只发进程名与停留时长，绝不发窗口标题。**
        ///   标题里是文档名／网页标题／聊天对象 —— 那属于「屏幕内容出本机」。
        ///   P0 的视野纪律：A 档（应用级）可以出本机，B 档以上默认关。
        ///   这条是**隐私判据**（--speaktest 的 promptHasNoTitle），不是风格问题。
        ///
        /// ⚠ 2026-09-20 起多了第二个入参 `screenText`：**屏幕上的字**（用户拍板「接进去」）。
        ///   它与标题不是一回事：标题是**元数据**（随时可取、她本可以白看一眼），
        ///   屏幕文字是**内容**（要截屏＋本地识别才拿得到）。所以文字这一路必须由
        ///   `OcrSendText` 显式打开，而且**怎么截、截谁的、能不能发**全部由 `OcrEye` 自己判完 ——
        ///   这里只管「有就放进去，没有就不放」，**绝不在这里补一句「没读到」**
        ///   （那种话会被模型照着说，正如它照着念秒数一样）。
        ///
        /// ⚠ 2026-09-20 起看 `obs.Manual` 分两套说法：**手动触发**（你点「让她说一句」）没有
        ///   「他刚换了窗口」这件事，也不该带秒数 —— 详见 `Observation.Manual` 的注释。
        /// </summary>
        public static string BuildPrompt(Observation obs, string screenText)
        {
            double dwell = obs == null ? 0 : obs.PrevDwell;
            bool known = obs != null && !string.IsNullOrEmpty(obs.Process);
            bool manual = obs != null && obs.Manual;
            bool roast = obs != null && obs.Reason != null && obs.Reason.StartsWith("roast");

            string head;
            if (manual)
            {
                // ⚠⚠ 手动触发（你点了「让她说一句」）**没有**「他刚换了窗口」这件事。
                //   2026-09-20 实拍：她对着哔哩哔哩说「6秒，够你在里面找到想要的？」——
                //   「6 秒」是上一次自动观察**残留**下来的读数，而「他刚换到 X」压根没发生过。
                //   手动触发只该回答一件事：**现在屏幕上是什么**。
                head = known
                    ? "他现在在用「" + StubSpeaker.Human(obs.Process) + "」。"
                    : "我看不出他现在在用哪个窗口。";
            }
            else if (roast)
            {
                // 吐槽触发：他**没有**换窗口 —— 说「刚换到」就是假陈述。
                // 素材是「应用名＋已待时长」；屏幕文字（若有）由下方共用尾巴追加，
                // 仍受 OcrSendText 门控 —— 标题只是触发信号，绝不进提示。
                head = known
                    ? "他最近一直在用「" + StubSpeaker.Human(obs.Process) + "」，已经待了 " + dwell.ToString("0") + " 秒。"
                    : "我看不出他在用哪个窗口。";
            }
            else
            {
                // ⚠ 不知道是哪个应用时**不要**塞一个占位的「这个」进去 —— 模型会照着说。
                //   宁可少给一点信息，也不给一条假的。
                head = known
                    ? "他刚换到「" + StubSpeaker.Human(obs.Process) + "」，上一个窗口他待了 " + dwell.ToString("0") + " 秒。"
                    : "他刚换了个窗口，上一个他待了 " + dwell.ToString("0") + " 秒。";
            }

            if (string.IsNullOrEmpty(screenText)) return head;
            // ⚠ 明写「可能有错字」与「别当原文引用」：本机 OCR 实测会把「普通」认成「普囱」，
            //   不说清她会把错字当成事实讲出来（同「凡写进 prompt 的东西都会出现在台词里」）。
            // ⚠ 措辞写「屏幕上」而**不是**「他现在这个窗口」：屏幕文字由 `OcrEye` 自己判定的目标
            //   窗口读出，极端时序下（他刚切走／前台变成任务栏）可能与上面那个应用不是同一个窗口 ——
            //   那就写成了一句假陈述（同「同一份数据两个落点」）。
            return head + " 屏幕上写着（本机识别，可能有错字）：" + screenText;
        }

        private static Dictionary<string, object> Msg(string role, string text)
        {
            return new Dictionary<string, object>
            {
                ["role"] = role,
                ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } },
            };
        }

        private static string One(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }
    }

    /// <summary>哑说话人：永远不开口。**负对照专用** —— 用它时 --speaktest 的接线判据必须变红。</summary>
    internal sealed class SilentSpeaker : ISpeaker
    {
        public string Name { get { return "silent"; } }
        public Task<Verdict> SayAsync(Observation obs)
        {
            return Task.FromResult(new Verdict { Speak = false, Why = "哑说话人（负对照）" });
        }
    }
}
