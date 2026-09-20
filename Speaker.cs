// 决策层（P0）—— 给定一次观察，判断「要不要开口、开口说什么」。
//
// 为什么先做 stub 而不是直接上 LLM：
//   若一上来就是 LLM，你分不清「她今天说得无聊」是**话的问题**还是**时机的问题**。
//   stub 是「时机对了、话很笨」的对照组 —— 它绿的时候，问题就落在话上，反之落在时机上。
//   （项目老毛病：没有负对照的绿没有信息量。）
//
// ⚠ stub 的台词同样要过禁词表。人设的禁词（主人／老板／用户／亲爱的）拦的是
//   「她真说出口的话」，不只是 persona.md 的正文 —— 那一半判据在这里落地。
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AzhuPet
{
    /// <summary>一次决策的结果。⚠ 说不说都必须有 Why —— 静默跳过必须留可读原因。</summary>
    internal sealed class Verdict
    {
        public bool Speak;
        public string Text;
        public string Why;
    }

    internal interface ISpeaker
    {
        string Name { get; }
        Task<Verdict> SayAsync(Observation obs);
    }

    /// <summary>模板说话人（负对照）。不联网、不花钱、逐字节可预测。</summary>
    internal sealed class StubSpeaker : ISpeaker
    {
        public string Name { get { return "stub"; } }

        private int _n;

        private static readonly string[] Lines =
        {
            "……换到{app}了。",
            "{app}啊。刚才那个弄完了？",
            "你在这儿待挺久了。",
            "刚才那个窗口，关掉了？",
        };

        // ⚠ 不知道是哪个应用时**不能**挑带 {app} 的那几条 —— 否则她会说「……换到这个了。」
        //   （首次实跑 --speakvis 时她说的正是「……换到 pet 了。」：前台是她自己的窗口。）
        private static readonly string[] Generic =
        {
            "你在忙啊。",
            "刚才那个，弄完了？",
            "我在这儿。",
        };

        // 吐槽触发用的模板（roast 观察）。⚠ 不能用 Lines —— 那组全是「换到X了」的句式，
        // 而 roast 的前提恰恰是**他没换窗口**（说了就是假陈述，同手动触发那案的镜像）。
        private static readonly string[] RoastLines =
        {
            "在{app}里待挺久了啊。",
            "{app}，看什么呢。",
            "{app}逛这么久，发现什么了。",
        };

        // 进程名 → 说出口的样子。映射不到就用原名（会显得笨，那正是 stub 该有的样子）。
        private static readonly Dictionary<string, string> Names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome"] = "浏览器", ["msedge"] = "浏览器", ["firefox"] = "浏览器",
            ["Code"] = "编辑器", ["devenv"] = "编辑器", ["notepad"] = "记事本",
            ["WINWORD"] = "文档", ["EXCEL"] = "表格", ["POWERPNT"] = "幻灯片",
            ["Obsidian"] = "库", ["explorer"] = "文件夹", ["WeChat"] = "微信",
            ["QQ"] = "QQ", ["WindowsTerminal"] = "终端", ["cmd"] = "终端",
            ["Telegram"] = "聊天窗", ["PotPlayerMini64"] = "片子",
        };

        public Task<Verdict> SayAsync(Observation obs)
        {
            bool known = obs != null && !string.IsNullOrEmpty(obs.Process);
            bool roast = obs != null && obs.Reason != null && obs.Reason.StartsWith("roast");
            string text;
            if (known && roast)
            {
                int i = _n++ % RoastLines.Length;
                text = RoastLines[i].Replace("{app}", Human(obs.Process));
            }
            else if (known)
            {
                int i = _n++ % Lines.Length;
                text = Lines[i].Replace("{app}", Human(obs.Process));
            }
            else
            {
                int i = _n++ % Generic.Length;
                text = Generic[i];
            }
            return Task.FromResult(new Verdict { Speak = true, Text = text, Why = "模板台词（stub 负对照）" });
        }

        public static string Human(string proc)
        {
            if (string.IsNullOrEmpty(proc)) return "这个";
            string v;
            return Names.TryGetValue(proc, out v) ? v : proc;
        }
    }

    /// <summary>
    /// 最小闸门：冷却 ＋ 日限额。
    /// ⚠ 它**不是** P2 的完整抑制闸（打字中／全屏／被忽略降频都在那边），
    ///   但没有它 P0 一跑就是骚扰 —— 你无法判断「时机对不对」，因为每 8 秒就有一句。
    ///   放在决策之前：闸门管**客观条件**，人格管**软适应**，两者不能改同一个量。
    /// </summary>
    internal sealed class SpeechGate
    {
        // ⚠ 2026-09-20 用户拍板「统一放宽」：45 → 180 秒（支持吐槽通道后每句之间隔几分钟）。
        //   ⚠ Roast.CooldownSec（200）必须**大于**这个值 —— 否则吐槽触发会被这里拦下，
        //     每秒产出一条被拦的观察，memory.jsonl 会被 veto 记录灌爆（理由见 Roast.cs 文件头）。
        public double Cooldown = 180;    // 两次开口之间的最小间隔（秒）
        public int DailyCap = 200;       // 每天最多几句（原 60，用户拍板 200）

        private DateTime _last = DateTime.MinValue;
        private int _day = -1, _today;

        /// <summary>上次放行开口的时刻。给吐槽通道的触发条件用（它要知道「她多久没说话了」）。</summary>
        public DateTime LastSpeak { get { return _last; } }

        /// <summary>允许开口吗。不允许时 why 必须写清是**哪一道**闸拦的。</summary>
        public bool Allow(DateTime now, out string why)
        {
            if (_day != now.DayOfYear) { _day = now.DayOfYear; _today = 0; }
            if (_today >= DailyCap) { why = "日限额已到（" + _today + "/" + DailyCap + "）"; return false; }

            double since = (now - _last).TotalSeconds;
            if (_last != DateTime.MinValue && since < Cooldown)
            {
                why = "冷却中（还差 " + (Cooldown - since).ToString("0") + " 秒）";
                return false;
            }
            why = "可以开口";
            return true;
        }

        public void Note(DateTime now) { _last = now; _today++; }

        public int Today { get { return _today; } }
    }
}
