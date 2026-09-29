// 多 Agent 任务计时：把「哪个 agent 的任务跑了多久 / 刚跑完用了多久」变成桌宠气泡流顶上的一组实时读数。
//
// 三个 agent 各有一条**带时间戳的日志行**作为任务边界，本类只 tail 这些文件，不碰任何 agent 本身：
//
//   TRAE       %APPDATA%\TRAE SOLO CN\logs\<会话>\window<N>\renderer.log
//              [StreamDomainService] Stream started  /  [StreamDomainService] Stream finalized
//   WorkBuddy  %USERPROFILE%\.workbuddy\logs\<日期>\sdk\conversations\<会话ID>.log
//              "input":"PROMPT_SENT"  /  "input":"TURN_COMPLETED"
//   Codex      %USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl
//              "type":"task_started"  /  "type":"task_complete"  /  "type":"turn_aborted"
//
// ⚠⚠ 以上全是各 agent 的**实现细节文案，不是稳定 API**。若某一家读数不再出现，
//    第一件事就是来这里核对它那两个边界串还在不在（三家各自会随版本改字）。
//
// ⚠ 为什么不用项目级 Hook（`.trae/hooks.json`）：SOLO CN 把 Hooks 设置页整块隐藏了
//   （灰度开关 iCubeApp.hooks.enable 在 SOLO 版被 ku(i) 判成永远关闭），Hook 从未触发，
//   状态文件 `trae_task_state.json` 一次都没生成过。日志是唯一还能拿到的、带时间戳的任务信号。
//
// ⚠ 为什么按「会话」而不是「全局一条」记状态：多会话／多 agent 可以**同时**在跑。
//   旧版把所有被盯的文件塌缩成一份全局 start/end，只会留下最后报到的那个任务，
//   并发时互相覆盖 —— 这是从单任务扩到多任务时**唯一必须改的结构**。
// ⚠⚠ 会话 ≠ 文件：TRAE 的**同一个** renderer.log 里会先后（甚至同时）出现好几个 sessionId
//   （实测同一 window1 里就有 6aba1e…／6ab4d6…／6aba52… 三条对话）。
//   所以状态键是「文件路径 + 日志行里的 sessionId」，按文件记仍会在同窗口并发时互相覆盖。
//   WorkBuddy 是一会话一文件，sessionId 取空串，键退化成文件路径。
//
// ⚠⚠⚠ Codex 反过来：**一个会话会横跨多个文件**。compact／续写时它另开一个 rollout 文件，
//   文件名变成 `rollout-<时间>-<会话UUID>_<新的UUID>.jsonl` —— 第一个 UUID 仍是会话号，
//   两（或多）个文件共用它（实测 2026-09-29 的 01a0ebc6… 就是 14-07-17 与 14-08-51 两个文件）。
//   按文件记状态有两个真实后果：
//     ① 同一个会话被拆成两条读数（各自从自己文件里的边界算）；
//     ② 旧文件里那条**没等到 `task_complete` 的** `task_started` 会一直悬着
//        ⇒ 读数里多出一条永不消失的假「Codex 进行中」，直到该文件 15 分钟没被写才被存活判据清掉。
//   所以 Codex 的状态键是**会话号**（取文件名里第一段 UUID），同会话的多个文件**合并**成一份状态。
//
// ⚠ 为什么读数要每拍重算、而不是「读日志那一拍才算」：秒数必须每秒跳一次。
//   若只在读文件的那一拍更新文本，120ms 的轮询会让秒数看起来一顿一顿。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AzhuPet
{
    internal sealed class AgentTaskTimer
    {
        /// <summary>`--xxxtest` 时置 false：判据不该被「此刻恰好有任务在跑」污染。
        /// ⚠ 这个闸是**必须**的，不是保险 —— 跑判据的人往往正开着 agent，
        ///   只要有任务在跑，判据窗口里就会凭空多出常驻读数行，
        ///   把气泡流的宽度／高度判据整个带偏。</summary>
        public static bool Enabled = true;

        // ---- 只给判据用的根目录覆盖（`--agenttimertest`）----
        // ⚠⚠ 三条都是 null ＝ 走真实路径。判据**必须**能把它们指到临时目录：
        //   否则「验读数会不会丢」这件事只能拿用户真在跑的 Codex 日志做实验 ——
        //   那既不可重复（要等真有任务），又会去读/依赖用户当前的真实任务状态。
        //   （同 PetConfig.Dir 支持 AZHU_CONFIG_DIR 的理由：判据离线、不碰真数据。）
        public static string TraeRootOverride;
        public static string WorkBuddyRootOverride;
        public static string CodexRootOverride;
        // ⚠ WorkBuddy 的**心跳**目录（`sessions\<pid>.json`）与它的日志根目录是**两个落点**
        //   （日志在 `logs\`、心跳在 `sessions\`），所以是第四个覆盖，不能并进上面那个。
        public static string WorkBuddySessionsOverride;

        /// <summary>负对照（`--old-codex`）：把 Codex 的**状态口径**退回上一版 ——
        /// ① 状态按**文件**记（同一个会话的多个 rollout 文件各算一条，互不相干）；
        /// ② 结束只认 `task_complete`，不认 `turn_aborted`。
        /// （只退这两条语义，**不退**发现/配额那一层 —— 判据要证明的就是这两条通路。）
        /// ⚠ 存在的意义：没有它，`--agenttimertest` 的 D 组全绿说明不了任何事 ——
        ///   可能只是判据写歪了。开着它 D 组必须红（见 AgentTimerTest 的 D 组）。
        /// 正式路径上绝不开。</summary>
        public static bool OldCodex;

        /// <summary>负对照（`--no-wb-beat`）：关掉 WorkBuddy 的**真心跳兜底**，退回「只看日志静默」。
        /// ⚠ 存在的意义同 <see cref="OldCodex"/>：没有它，E1 的绿说明不了任何事 ——
        ///   可能只是判据写歪了。开着它 E1 必须红（见 AgentTimerTest 的 E 组）。
        /// 正式路径上绝不开。</summary>
        public static bool NoWbBeat;

        private const double ScanEverySec = 5.0;          // 重新定位日志文件的节流（agent 重启会换目录）
        private const double ReadEverySec = 0.5;          // 增量读日志的节流
        private const double StaleSec = 2 * 3600;         // 单个任务跑超过 2 小时 ⇒ 当作残留
        private const double AfterEndSec = 10;            // 跑完后「本次耗时」留在屏幕上的时长
        private const int MaxLines = 5;                   // 读数最多占几行（并发多时防气泡撑爆）

        // 首次接入一个文件时，从**尾部往回**按块扫描，直到最近一次「开始」和「结束」都找到。
        // ⚠ 不能只读固定的一段尾巴：WorkBuddy 单个会话日志动辄 6–8 MB，一个长轮次的
        //   `PROMPT_SENT` 可能落在窗口之外 —— 那样「启动桌宠时正在跑的任务」会永远不显示，
        //   因为它的开始时间读不到，后续的增量读也补不回来。
        private const int PrimeChunkBytes = 1024 * 1024;
        private const int PrimeMaxChunks = 24;            // 最多往回找 24 MB，兜住超长轮次

        // 每个 agent 最多同时盯几个文件。配额存在的意义是**限制 IO**：
        // WorkBuddy 的历史会话日志、Codex 的历史 rollout 加起来几百个，
        // 全盯上就是每半秒几百次文件读，桌宠会被自己的监控拖垮。
        private const int TraeQuota = 4;
        private const int WorkBuddyQuota = 4;
        // ⚠ Codex 的配额按**会话**算，不是按文件：一个会话会横跨多个 rollout 文件
        //   （见文件头「会话 ≠ 文件」那段），只盯 2 个文件时同一会话的新旧两个文件
        //   就可能被别的会话挤掉一个，那份状态立刻少一半。
        private const int CodexSessions = 2;          // 最多同时盯几个 Codex 会话
        private const int CodexFilesPerSession = 3;   // 每个会话最多盯几个文件（compact 几次就几个）

        // WorkBuddy 有**真心跳**：`%USERPROFILE%\.workbuddy\sessions\<pid>.json` 里的
        // `sessionId` ＋ `lastHeartbeat`（实测约 30–60 s 一跳）。
        // ⚠ 为什么非要有它：WorkBuddy 一轮对话**中途可以十几分钟不写会话日志**
        //   （2026-09-29 实测：状态机停在 `working`、心跳一直在跳，会话日志静默 21 分钟）。
        //   而存活判据原本只看「日志最近有没有被写」⇒ 正在跑的任务被从读数里抹掉。
        private const double WbBeatFreshSec = 180;    // 心跳多久算新鲜（容 3–6 次丢跳）

        private enum Agent { Trae, WorkBuddy, Codex }

        /// <summary>一个被 tail 的日志文件。**只负责「读到哪了」**，任务状态不挂在这里
        /// （见 <see cref="Task"/>）—— 一个文件里可能有多个会话。</summary>
        private sealed class Source
        {
            public string Path;
            public Agent Agent;
            public string Tag;               // 文件级标签：TRAE 的 w1、WorkBuddy/Codex 的会话号片段
            public string SessId;            // **文件级**会话号：只有 Codex 有（取文件名里第一段 UUID）
            public long Pos;                 // 已消费到的字节偏移
            public string Carry;             // 上一段结尾那半行（换行还没来）
            public DateTime WriteUtc;        // 该文件最后被写的时间（判断「还有人在写吗」）
        }

        /// <summary>一个任务 ＝ 某个会话最近一次的 start/end。
        /// ⚠⚠ 状态键必须是「文件路径 + sessionId」：TRAE 的一个 renderer.log 里有多条对话，
        ///   挂在文件上＝同窗口并发时两条对话互相覆盖（旧版正是这么错的）。
        /// ⚠⚠⚠ 但 Codex 要**反过来**：它一个会话横跨多个文件，状态键是会话号，
        ///   这些文件里的边界要**合并**成一份（见文件头「会话 ≠ 文件」那段）。
        ///   所以这里存的是 <see cref="Paths"/>（一份状态可以挂在多个文件上），
        ///   而不是单个 Path —— 存活判据取这些文件里**最新被写的那个**。</summary>
        private sealed class Task
        {
            public Agent Agent;
            public string Tag;               // 多任务并存时用来区分（会话号片段）
            // 承载这份状态的所有日志文件。TRAE / WorkBuddy 恒为 1 个；Codex 一个会话可能有好几个。
            public readonly HashSet<string> Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public long StartMs, EndMs;
        }

        /// <summary>要显示的文本（多行，每行一个任务）；没有可显示的任务时为 null。</summary>
        public string Header { get; private set; }

        private readonly Dictionary<string, Source> _src = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        // 键 = 文件路径 + "|" + sessionId（TRAE 多会话同文件时靠它分开）
        private readonly Dictionary<string, Task> _task = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private double _lastScanT = double.NegativeInfinity;
        private double _lastReadT = double.NegativeInfinity;

        /// <summary>每拍调一次（SlowTick，120ms）。IO 按 <see cref="ReadEverySec"/> 节流，
        /// 但**读数每拍都按墙上时钟重算** —— 秒数才会平滑地跳。</summary>
        public void Poll(double nowSec)
        {
            Header = null;
            if (!Enabled) return;

            if (nowSec - _lastScanT >= ScanEverySec) { _lastScanT = nowSec; Locate(); }
            if (_src.Count == 0) return;
            if (nowSec - _lastReadT >= ReadEverySec) { _lastReadT = nowSec; ReadNew(); }

            Header = Compose();
        }

        // ==== 发现：把三个 agent 的候选日志文件收进来 ====

        private void Locate()
        {
            var found = new List<Source>();
            // 每个 agent 各自兜异常：一家目录枚举失败不该让另两家一起熄火。
            try { DiscoverTrae(found); } catch { }
            try { DiscoverWorkBuddy(found); } catch { }
            try { DiscoverCodex(found); } catch { }
            // WorkBuddy 的存活兜底（真心跳）—— 与日志发现同一节流（5 s），见 RefreshWbHeartbeats。
            try { RefreshWbHeartbeats(); } catch { }

            // 按配额取「最后写入」最新的几个
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Agent a in new[] { Agent.Trae, Agent.WorkBuddy, Agent.Codex })
            {
                var list = found.FindAll(s => s.Agent == a);
                list.Sort((x, y) => y.WriteUtc.CompareTo(x.WriteUtc));
                if (a == Agent.Codex) KeepCodex(list, keep);   // ⚠ Codex 按**会话**配额（见 KeepCodex）
                else
                {
                    int quota = Quota(a);
                    for (int i = 0; i < list.Count && i < quota; i++) keep.Add(list[i].Path);
                }
            }

            // ⚠ 正在跑的任务，其来源必须留住：配额是按「最新写入」切的，
            //   一个长工具调用期间它的日志可能被同 agent 的其它文件挤出去，
            //   那样这次任务会从读数里凭空消失。
            //   但文件本身若已不在（被清理），就不能留 —— 否则它会永远占着一个配额位。
            foreach (var kv in _src)
                if (File.Exists(kv.Key) && HasRunning(kv.Key)) keep.Add(kv.Key);

            foreach (string gone in new List<string>(_src.Keys))
                if (!keep.Contains(gone)) _src.Remove(gone);

            // 任务随来源一起回收：来源被配额挤掉或文件已删，它承载的会话就没人再读了。
            // ⚠ Codex 一份状态挂在多个文件上：只把**已经不在 `_src` 里的那些文件**摘掉，
            //   还剩文件就说明这份状态还有人读（别因为旧文件被挤掉就把整个会话判死）。
            // 另：TRAE 一个文件里会不断开出新会话，**已结束**且早过显示窗口的必须清掉，
            //   否则 _task 只增不减（5 分钟足够覆盖 AfterEndSec 的显示期）。
            // ⚠⚠ 必须带上「已结束」这个条件（`EndMs >= StartMs`）：`EndMs` 是**上一轮**的结束时刻，
            //   一轮正在跑的任务（`StartMs > EndMs`）它的 `EndMs` 完全可能是很久以前。
            //   只按 `EndMs > 0` 判就会把**正在跑**的任务连根拔掉 —— 实测 WorkBuddy 2026-09-29：
            //   16:36:08 开始第三轮，上一轮结束在 16:33:48，50 分钟后这条任务被这里删掉 ⇒
            //   读数整条消失（正是用户报的「WorkBuddy 在跑、桌宠没读数」）。
            //   心跳兜底也救不了它 —— 任务已经不在 `_task` 里了，`Compose` 根本遍历不到。
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (string k in new List<string>(_task.Keys))
            {
                Task t = _task[k];
                t.Paths.RemoveWhere(p => !_src.ContainsKey(p));
                if (t.Paths.Count == 0) { _task.Remove(k); continue; }
                if (t.StartMs > 0 && t.EndMs >= t.StartMs && nowMs - t.EndMs > 300_000) _task.Remove(k);
            }

            foreach (var s in found)
                if (keep.Contains(s.Path) && !_src.ContainsKey(s.Path)) _src[s.Path] = Prime(s);
        }

        /// <summary>Codex 的配额：**先按会话切，再按会话内的文件切**。
        /// <paramref name="list"/> 已按最后写入时间降序。
        /// ⚠ 不能退化成「取最新 N 个文件」：一个会话横跨多个文件时，那 N 个可能全被同一个
        ///   会话（甚至别的会话）占满，正在跑的那个会话反而一个文件都留不下 —— 读数整条消失。</summary>
        private static void KeepCodex(List<Source> list, HashSet<string> keep)
        {
            var filesPerSess = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var admitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Source s in list)
            {
                string sess = string.IsNullOrEmpty(s.SessId) ? s.Path : s.SessId;
                if (!admitted.Contains(sess))
                {
                    if (admitted.Count >= CodexSessions) continue;
                    admitted.Add(sess);
                    filesPerSess[sess] = 0;
                }
                if (filesPerSess[sess] >= CodexFilesPerSession) continue;
                filesPerSess[sess] = filesPerSess[sess] + 1;
                keep.Add(s.Path);
            }
        }

        /// <summary>该文件下是否有「开始晚于结束」的会话（＝还在跑）。</summary>
        private bool HasRunning(string path)
        {
            foreach (var t in _task.Values)
                if (t.StartMs > t.EndMs && t.Paths.Contains(path))
                    return true;
            return false;
        }

        private static void DiscoverTrae(List<Source> outp)
        {
            foreach (string root in TraeRoots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (string sess in Directory.GetDirectories(root))
                    foreach (string win in Directory.GetDirectories(sess, "window*"))
                    {
                        string f = Path.Combine(win, "renderer.log");
                        if (File.Exists(f)) outp.Add(Make(f, Agent.Trae, WindowTag(win)));
                    }
            }
        }

        private static void DiscoverWorkBuddy(List<Source> outp)
        {
            string root = !string.IsNullOrEmpty(WorkBuddyRootOverride)
                ? WorkBuddyRootOverride
                : Path.Combine(UserProfile(), ".workbuddy", "logs");
            if (!Directory.Exists(root)) return;

            // 会话日志按日期分目录：logs\<日期>\sdk\conversations\*.log（另有 logs\sdk\conversations 的旧布局）
            var dirs = new List<string>();
            string direct = Path.Combine(root, "sdk", "conversations");
            if (Directory.Exists(direct)) dirs.Add(direct);
            foreach (string d in Directory.GetDirectories(root))
            {
                string sub = Path.Combine(d, "sdk", "conversations");
                if (Directory.Exists(sub)) dirs.Add(sub);
            }

            foreach (string d in dirs)
                foreach (string f in Directory.GetFiles(d, "*.log"))
                {
                    // 只收 UUID 命名的会话日志：同目录下还有 operation.log（任务列表查询日志，
                    // 没有边界串），收进来它会白占一个配额位，把真正的会话挤掉。
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (!Guid.TryParse(stem, out _)) continue;
                    outp.Add(Make(f, Agent.WorkBuddy, Head(stem, 4)));
                }
        }

        private static void DiscoverCodex(List<Source> outp)
        {
            string root = !string.IsNullOrEmpty(CodexRootOverride)
                ? CodexRootOverride
                : Path.Combine(UserProfile(), ".codex", "sessions");
            if (!Directory.Exists(root)) return;

            // ⚠ 目录很深（YYYY\MM\DD），历史 rollout 上百个 —— 全量枚举后由 Locate 按**会话**配额筛选。
            //   这里**不**提前按文件数截断：一个会话横跨多个文件，先按文件切会把同一会话切散。
            string[] files;
            try { files = Directory.GetFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories); }
            catch { return; }

            foreach (string f in files)
            {
                try
                {
                    string sess = CodexSession(f);
                    if (sess == null) continue;          // 名字认不出会话号 ⇒ 不是 Codex 的 rollout
                    Source s = Make(f, Agent.Codex, Head(sess, 4));
                    s.SessId = sess;
                    outp.Add(s);
                }
                catch { }
            }
        }

        private static Source Make(string path, Agent a, string tag)
        {
            return new Source
            {
                Path = path,
                Agent = a,
                Tag = tag,
                WriteUtc = File.GetLastWriteTimeUtc(path),
            };
        }

        private static int Quota(Agent a)
        {
            switch (a)
            {
                case Agent.Trae: return TraeQuota;
                case Agent.WorkBuddy: return WorkBuddyQuota;
                // ⚠ Codex 不走这里：它按会话配额（KeepCodex），文件数不是它的口径。
                default: return CodexSessions * CodexFilesPerSession;
            }
        }

        /// <summary>刚接入一个文件：从尾部往回按块扫，把**最近那条会话**的「最近一次开始／结束」补上。
        /// ⚠ 不能只从文件末尾开始跟 —— 桌宠启动时若已有任务在跑，那次「开始」在历史里。
        /// ⚠⚠ 也不能「扫到一对 start/end 就收工」：TRAE 一个文件里有多条对话，往回扫到的可能是
        ///   **上一条**对话的一对边界 —— 那会把「当前正在跑的任务」错报成上一条的耗时。
        ///   所以先定住最近一条边界所属的 sessionId，只认它的行；扫到别的会话就停。</summary>
        private Source Prime(Source s)
        {
            s.Pos = 0; s.Carry = null;
            try
            {
                var fi = new FileInfo(s.Path);
                long len = fi.Length;
                s.WriteUtc = fi.LastWriteTimeUtc;

                // ⚠⚠ Codex 必须在**扫边界之前**就把「这个文件属于哪个会话」登记上，
                //   不能等扫到边界才登记：一个会话的多个 rollout 文件里，**有的文件一条边界都没有**
                //   （compact 之后接着写、但这一轮的开始还在上一个文件里）。
                //   漏登记它的后果不是「少一条读数」，而是**存活判据看不到这个文件**
                //   ⇒ 会话明明还在写，却因为「最新被写的那个文件没登记」被判成已经没了、读数消失。
                if (s.Agent == Agent.Codex && !string.IsNullOrEmpty(s.SessId))
                    EnsureTask(s, s.SessId).Paths.Add(s.Path);

                string sess = null;
                long startMs = 0, endMs = 0;
                long to = len;
                for (int c = 0; c < PrimeMaxChunks && to > 0; c++)
                {
                    long from = Math.Max(0, to - PrimeChunkBytes);
                    string[] lines = ReadText(s.Path, from, to).Split('\n');
                    // from>0 时，这段的第一行是被块边界切断的半行 —— 由下一块补齐，这里跳过。
                    int lo = from > 0 ? 1 : 0;
                    bool stop = false;
                    for (int i = lines.Length - 1; i >= lo; i--)
                    {
                        long ms = Stamp(s, lines[i], out bool isStart, out bool isEnd, out string sk);
                        if (ms <= 0) continue;
                        if (sess == null) sess = sk;
                        else if (!string.Equals(sk, sess, StringComparison.Ordinal)) { stop = true; break; }
                        if (isStart) { if (startMs == 0) startMs = ms; }
                        else if (isEnd) { if (endMs == 0) endMs = ms; }
                        if (startMs != 0 && endMs != 0) { stop = true; break; }
                    }
                    if (stop) break;
                    to = from;
                }

                if (sess != null)
                {
                    // ⚠ **合并**而不是覆盖：Codex 一个会话横跨多个文件，本文件里的边界只是
                    //   这份状态的一部分（另一部分在同一个会话的另一个文件里）。
                    //   覆盖会把先读到的那份抹掉 ⇒ 读数在「新文件刚被接入」那一拍闪一下。
                    Task t = EnsureTask(s, sess);
                    t.Paths.Add(s.Path);
                    if (startMs > t.StartMs) t.StartMs = startMs;
                    if (endMs > t.EndMs) t.EndMs = endMs;
                }
                s.Pos = len;
            }
            catch { s.Pos = 0; }
            return s;
        }

        /// <summary>增量读：只读上次偏移之后的新字节，按整行消费，半行留到下一拍。</summary>
        private void ReadNew()
        {
            foreach (var kv in _src)
            {
                Source s = kv.Value;
                try
                {
                    var fi = new FileInfo(s.Path);
                    long len = fi.Length;
                    s.WriteUtc = fi.LastWriteTimeUtc;
                    if (len < s.Pos) { s.Pos = 0; s.Carry = null; }   // 被截断 / 轮转 ⇒ 从头再来
                    if (len == s.Pos) continue;

                    string chunk = ReadText(s.Path, s.Pos, len);
                    s.Pos = len;
                    string text = s.Carry == null ? chunk : s.Carry + chunk;

                    int nl = text.LastIndexOf('\n');
                    if (nl < 0) { s.Carry = text; continue; }         // 一行都还没凑齐
                    s.Carry = text.Substring(nl + 1);
                    string body = text.Substring(0, nl);

                    int from = 0;
                    while (from <= body.Length)
                    {
                        int e = body.IndexOf('\n', from);
                        if (e < 0) e = body.Length;
                        Apply(s, body.Substring(from, e - from));
                        from = e + 1;
                    }
                }
                // 单个文件读失败（被删 / 被独占）不该拖垮其它文件；下一拍再试。
                catch { }
            }
        }

        private void Apply(Source s, string line)
        {
            long ms = Stamp(s, line, out bool isStart, out bool isEnd, out string sess);
            if (ms <= 0) return;

            Task t = EnsureTask(s, sess);
            t.Paths.Add(s.Path);

            if (isStart) { if (ms > t.StartMs) t.StartMs = ms; }
            else if (isEnd) { if (ms > t.EndMs) t.EndMs = ms; }
        }

        /// <summary>取（没有就建）某条会话的状态。⚠ 建出来的可能是一份 **StartMs=0 的空状态**：
        /// Codex 的一个文件可能一条边界都没有（见 <see cref="Prime"/> 里那段）。空状态在
        /// <see cref="Compose"/> 里被 `StartMs <= 0` 跳过 ⇒ 不显示；等它的**任一**文件出现边界时自动活过来。</summary>
        private Task EnsureTask(Source s, string sess)
        {
            string k = Key(s, sess);
            if (!_task.TryGetValue(k, out Task t))
                _task[k] = t = new Task { Agent = s.Agent, Tag = TaskTag(s, sess) };
            return t;
        }

        /// <summary>状态键。⚠ 两种 agent 的「会话 ↔ 文件」关系**正好相反**：
        /// TRAE 是**一个文件多个会话**（键要带行里的 sessionId），
        /// Codex 是**一个会话多个文件**（键只认会话号，多个文件合并到同一份状态）。</summary>
        private static string Key(Source s, string sess)
        {
            if (!OldCodex && s.Agent == Agent.Codex && !string.IsNullOrEmpty(sess)) return "codex|" + sess;
            return s.Path + "|" + (sess ?? "");
        }

        /// <summary>多任务并存时那截区分标识。TRAE 用会话号 —— 同一个 window 里可能有好几条对话，
        /// 只报窗口（w1）两条就分不清了；WorkBuddy 一文件一会话、Codex 一会话多文件，
        /// 两者的文件级标签本来就取自会话号（见 DiscoverWorkBuddy / DiscoverCodex），直接用它。</summary>
        private static string TaskTag(Source s, string sess)
        {
            if (s.Agent == Agent.Trae && !string.IsNullOrEmpty(sess)) return Head(sess, 6);
            return s.Tag;
        }

        // ==== 组装读数 ====

        private sealed class Entry { public Task T; public double El; public double Key; }

        private string Compose()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var run = new List<Entry>();
            var done = new List<Entry>();

            foreach (var t in _task.Values)
            {
                if (t.StartMs <= 0) continue;
                // 存活看**文件**有没有在被写（会话自己不会报心跳），所以取承载它的那些文件里
                // **最新被写的那个**。⚠ 不能只看某一个：Codex 一个会话横跨多个文件，旧文件
                // 在 compact 之后就冻住了，只盯它会把「还在跑」判成「已经没了」。
                DateTime newest = DateTime.MinValue; bool has = false;
                foreach (string p in t.Paths)
                {
                    if (!_src.TryGetValue(p, out Source src)) continue;
                    has = true;
                    if (src.WriteUtc > newest) newest = src.WriteUtc;
                }
                if (!has) continue;
                // ⚠ WorkBuddy 额外走**真心跳**兜底：它一轮对话中途可以十几分钟不写日志，
                //   只看「日志静默」会把正在跑的任务判死（见 RefreshWbHeartbeats 那段）。
                //   是**兜底**不是替代 —— 日志判据原样保留。
                bool alive = (DateTime.UtcNow - newest).TotalSeconds <= NoWriteSec(t.Agent)
                             || (!NoWbBeat && WbBeatAlive(t));

                if (t.StartMs > t.EndMs)
                {
                    double el = (nowMs - t.StartMs) / 1000.0;
                    if (el < 0 || el > StaleSec) continue;
                    // ⚠ agent 被关掉时「结束」标记永远不会来，只靠 StaleSec 会挂 2 小时。
                    //   日志长时间没被写过 ⇒ 那次任务已经不在了。
                    if (!alive) continue;
                    run.Add(new Entry { T = t, El = el, Key = el });
                }
                else if (t.EndMs > 0)
                {
                    double el = (t.EndMs - t.StartMs) / 1000.0;
                    double since = (nowMs - t.EndMs) / 1000.0;
                    if (el < 0 || since < 0 || since > AfterEndSec) continue;
                    done.Add(new Entry { T = t, El = el, Key = t.EndMs });
                }
            }

            if (run.Count == 0 && done.Count == 0) return null;

            // 同一 agent 同时有多个任务进读数时，补一个短标识 —— 否则两条「TRAE 进行中」分不清是谁。
            var per = new Dictionary<Agent, int>();
            foreach (var e in run) per[e.T.Agent] = Get(per, e.T.Agent) + 1;
            foreach (var e in done) per[e.T.Agent] = Get(per, e.T.Agent) + 1;

            run.Sort((a, b) => b.Key.CompareTo(a.Key));    // 跑得最久的排最上
            done.Sort((a, b) => b.Key.CompareTo(a.Key));   // 最近完成的排前面

            var lines = new List<string>();
            foreach (var e in run) lines.Add(Line(e, per[e.T.Agent] > 1, true));
            foreach (var e in done) lines.Add(Line(e, per[e.T.Agent] > 1, false));

            if (lines.Count > MaxLines) lines.RemoveRange(MaxLines, lines.Count - MaxLines);
            return string.Join("\n", lines);
        }

        private static string Line(Entry e, bool withTag, bool running)
        {
            string name = AgentName(e.T.Agent);
            if (withTag && e.T.Tag != null) name = name + " " + e.T.Tag;
            return running ? name + " 进行中 " + Clock(e.El) : name + " 耗时 " + Human(e.El);
        }

        private static int Get(Dictionary<Agent, int> d, Agent a)
        {
            return d.TryGetValue(a, out int v) ? v : 0;
        }

        private static string AgentName(Agent a)
        {
            switch (a)
            {
                case Agent.Trae: return "TRAE";
                case Agent.WorkBuddy: return "WorkBuddy";
                default: return "Codex";
            }
        }

        /// <summary>日志空闲多久算「那次任务已经不在了」。TRAE 实测空闲时也至少每两分钟写一行；
        /// 该任务的承载文件（日志）多久没被写了 ⇒ 判「还有人在写吗」。
        /// ⚠ WorkBuddy 另有**真心跳**兜底（`WbBeatAlive`）—— 日志判据这里原样保留，是兜底不是替代。</summary>
        private static double NoWriteSec(Agent a)
        {
            return a == Agent.Trae ? 600 : 900;
        }

        // ==== 各 agent 的边界串与时间戳 ====

        private static string StartMark(Agent a)
        {
            switch (a)
            {
                // ⚠ TRAE 必须整串匹配：日志里另有 `done finalized stream` 这类近义文案，
                //   只按后半个词匹配会误判。
                case Agent.Trae: return "[StreamDomainService] Stream started";
                case Agent.WorkBuddy: return "\"input\":\"PROMPT_SENT\"";
                default: return "\"type\":\"task_started\"";
            }
        }

        private static string EndMark(Agent a)
        {
            switch (a)
            {
                case Agent.Trae: return "[StreamDomainService] Stream finalized";
                case Agent.WorkBuddy: return "\"input\":\"TURN_COMPLETED\"";
                // ⚠ 带上 `"type":` 前缀：否则 `"task_completed"` 这种更长的词也会被命中。
                default: return "\"type\":\"task_complete\"";
            }
        }

        /// <summary>Codex 的「这一轮被中断」。⚠ 必须算作结束 —— 见 <see cref="IsEnd"/>。</summary>
        private const string CodexAbortMark = "\"type\":\"turn_aborted\"";

        /// <summary>这一行是不是「任务结束」。
        /// ⚠⚠ Codex 除了 `task_complete` 还有 `turn_aborted`（用户按停／中断）：中断之后**不会**
        ///   再来 `task_complete`。只认 `task_complete` 的话，那条 `task_started` 会一直悬着
        ///   ⇒ 读数里多出一条永不消失的假「Codex 进行中」，直到该文件 15 分钟没被写才被存活判据清掉。
        ///   实测（2026-09-29 的 01a0ebc6…）：14:08:21 开始、14:08:45 中断的那一轮就是这么变成假「进行中」的。</summary>
        private static bool IsEnd(Agent a, string line)
        {
            if (line.IndexOf(EndMark(a), StringComparison.Ordinal) >= 0) return true;
            return !OldCodex && a == Agent.Codex && line.IndexOf(CodexAbortMark, StringComparison.Ordinal) >= 0;
        }

        /// <summary>认出一行是不是任务边界，并取出它的时间戳（毫秒）。
        /// 不是边界、或时间戳读不出来，都返回 0 ⇒ 调用方当作「没有」。</summary>
        private static long Stamp(Source s, string line, out bool isStart, out bool isEnd, out string sess)
        {
            Agent a = s.Agent;
            sess = SessionOf(s, line);
            isStart = line.IndexOf(StartMark(a), StringComparison.Ordinal) >= 0;
            isEnd = !isStart && IsEnd(a, line);
            if (!isStart && !isEnd) return 0;

            // TRAE / WorkBuddy 的日志行以 `<ISO> <正文>` 开头，时间戳就是第一个空格前的 token；
            // Codex 的 rollout 是纯 JSONL，时间戳在 `"timestamp":"..."` 字段里。
            if (a == Agent.Codex) return JsonStamp(line);

            int sp = line.IndexOf(' ');
            if (sp <= 0) return 0;
            return ParseIso(line.Substring(0, sp));
        }

        /// <summary>这一行（或这个文件）属于哪条会话。
        /// ⚠ TRAE 的 renderer.log **一个文件里有多条对话**，只能从行的 `"sessionId":"…"` 取；
        ///   Codex **一个会话横跨多个文件**，会话号在**文件名**里（行里没有），由 DiscoverCodex 预先取好；
        ///   WorkBuddy 一文件一会话，返回空串（键退化成文件路径）。
        /// ⚠ TRAE 取不到时返回 null 而不是空串：null＝「这家没有会话概念」（标签退回文件级），
        ///   空串会被当成一个真实会话键，两者在标签选择上语义不同。</summary>
        private static string SessionOf(Source s, string line)
        {
            if (s.Agent == Agent.Codex) return s.SessId;
            if (s.Agent != Agent.Trae) return "";
            const string key = "\"sessionId\":\"";
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int j = line.IndexOf('"', i);
            return j > i ? line.Substring(i, j - i) : null;
        }

        /// <summary>Codex rollout 文件名里的**会话号** ＝ 时间戳之后的第一段 UUID。
        /// 形如 `rollout-2026-09-29T14-08-51-01a0ebc6-…-7327db71c7d0.jsonl`，
        /// 或 compact 后另开的 `rollout-<时间>-<同一个会话UUID>_<新的UUID>.jsonl`。
        /// ⚠ 取**第一段** UUID：第二段（下划线后面那个）是新的 rollout id，同一会话的多个文件各不相同，
        ///   拿它当会话号就等于又退回「按文件记状态」。</summary>
        private static readonly Regex CodexSessRe = new Regex(
            @"^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-"
            + @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            RegexOptions.Compiled);

        private static string CodexSession(string path)
        {
            Match m = CodexSessRe.Match(Path.GetFileName(path));
            return m.Success ? m.Groups[1].Value : null;
        }

        // ==== WorkBuddy 的真心跳（存活判据的兜底）====
        //
        // 为什么需要它：WorkBuddy 一轮对话**中途可以十几分钟不写会话日志**（长工具调用／模型
        // 长时间生成时）。实测 2026-09-29：会话日志 16:36:08 开始第三轮，最后一行停在 16:41:46，
        // 此后 21 分钟一字未写 —— 但状态机是 `working`、`sessions\<pid>.json` 的 `lastHeartbeat`
        // 一直在跳。只按「日志静默」判死，正在跑的任务就从读数里消失了。
        // ⚠ 这是**兜底**不是替代：日志判据原样保留 —— 心跳文件格式将来变了，只是退回今天的行为
        //   （读数又会闪断），不会更糟。
        // ⚠ 这个目录**只增不减**（实测 09-23 的会话文件今天还在），所以只能按**心跳新鲜度**认活，
        //   不能按「文件存在」认 —— 否则历史会话会永远算活着。
        private readonly HashSet<string> _wbLive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Regex WbSessRe = new Regex(
            "\"sessionId\"\\s*:\\s*\"([0-9a-fA-F-]{36})\"", RegexOptions.Compiled);
        private static readonly Regex WbBeatRe = new Regex(
            "\"lastHeartbeat\"\\s*:\\s*(\\d{10,})", RegexOptions.Compiled);

        /// <summary>刷新「哪些 WorkBuddy 会话的心跳还新鲜」。挂在 Locate（5 s 一节流）上 ——
        /// 心跳本身 30–60 s 一跳，5 s 的采样足够，而这个目录里的文件会越积越多。</summary>
        private void RefreshWbHeartbeats()
        {
            _wbLive.Clear();
            string root = !string.IsNullOrEmpty(WorkBuddySessionsOverride)
                ? WorkBuddySessionsOverride
                : Path.Combine(UserProfile(), ".workbuddy", "sessions");
            if (!Directory.Exists(root)) return;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string[] files;
            try { files = Directory.GetFiles(root, "*.json"); } catch { return; }
            foreach (string f in files)
            {
                // 先用 mtime 粗筛：心跳一跳这个文件就重写一次，mtime 不新鲜 ⇒ 心跳必然也不新鲜。
                //   省掉的是「历史会话文件越积越多」时的全量读取。
                try { if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds > WbBeatFreshSec) continue; }
                catch { continue; }

                long len;
                try { len = new FileInfo(f).Length; } catch { continue; }
                string txt = ReadText(f, 0, len);
                if (string.IsNullOrEmpty(txt)) continue;

                Match ms = WbSessRe.Match(txt), mb = WbBeatRe.Match(txt);
                if (!ms.Success || !mb.Success) continue;
                long beat;
                if (!long.TryParse(mb.Groups[1].Value, out beat)) continue;
                if ((nowMs - beat) / 1000.0 > WbBeatFreshSec) continue;
                _wbLive.Add(ms.Groups[1].Value);
            }
        }

        /// <summary>这份 WorkBuddy 状态承载的会话，心跳还新鲜吗。
        /// ⚠ WorkBuddy 一文件一会话、会话号**就是日志文件名**（见 DiscoverWorkBuddy），
        ///   所以直接从 Paths 里的文件名取即可 —— 不必给 Task 再加一个字段。</summary>
        private bool WbBeatAlive(Task t)
        {
            if (t.Agent != Agent.WorkBuddy) return false;
            foreach (string p in t.Paths)
                if (_wbLive.Contains(Path.GetFileNameWithoutExtension(p))) return true;
            return false;
        }

        private static long JsonStamp(string line)
        {
            const string key = "\"timestamp\":\"";
            int i = line.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return 0;
            i += key.Length;
            int j = line.IndexOf('"', i);
            if (j < 0) return 0;
            return ParseIso(line.Substring(i, j - i));
        }

        private static long ParseIso(string s)
        {
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var dto) ? dto.ToUnixTimeMilliseconds() : 0;
        }

        // ==== 路径与标签 ====

        private static IEnumerable<string> TraeRoots()
        {
            if (!string.IsNullOrEmpty(TraeRootOverride)) { yield return TraeRootOverride; yield break; }
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            yield return Path.Combine(roaming, "TRAE SOLO CN", "logs");
            yield return Path.Combine(roaming, "TRAE CN", "logs");
        }

        private static string UserProfile()
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        /// <summary>TRAE 的窗口目录 `window1` → 标签 `w1`。</summary>
        private static string WindowTag(string winDir)
        {
            var sb = new StringBuilder();
            foreach (char c in Path.GetFileName(winDir)) if (char.IsDigit(c)) sb.Append(c);
            return sb.Length > 0 ? "w" + sb : null;
        }

        private static string Head(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return null;
            return s.Length <= n ? s : s.Substring(0, n);
        }

        /// <summary>⚠ 必须共享读写：agent 正在写这个文件，独占打开会把它的日志写坏或阻塞。</summary>
        private static string ReadText(string path, long from, long to)
        {
            int n = (int)(to - from);
            if (n <= 0) return string.Empty;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(from, SeekOrigin.Begin);
                byte[] buf = new byte[n];
                int got = 0;
                while (got < n)
                {
                    int r = fs.Read(buf, got, n - got);
                    if (r <= 0) break;
                    got += r;
                }
                return Encoding.UTF8.GetString(buf, 0, got);
            }
        }

        /// <summary>跑动中的读数：`mm:ss`，超过一小时补 `h:mm:ss`。</summary>
        public static string Clock(double sec)
        {
            int t = (int)Math.Floor(sec);
            if (t < 0) t = 0;
            int h = t / 3600, m = (t % 3600) / 60, s = t % 60;
            return h > 0
                ? h + ":" + m.ToString("00") + ":" + s.ToString("00")
                : m.ToString("00") + ":" + s.ToString("00");
        }

        /// <summary>跑完的总耗时：口语化（「3 分 12 秒」），不足一分钟只报秒。</summary>
        public static string Human(double sec)
        {
            int t = (int)Math.Round(sec);
            if (t < 0) t = 0;
            if (t < 60) return t + " 秒";
            int m = t / 60, s = t % 60;
            if (m < 60) return s == 0 ? m + " 分" : m + " 分 " + s + " 秒";
            int h = m / 60; m = m % 60;
            return h + " 小时 " + (m == 0 ? "" : m + " 分 ") + (s == 0 ? "" : s + " 秒");
        }
    }
}