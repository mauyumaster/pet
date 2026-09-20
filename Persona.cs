// 她的人格前缀 —— 从库里的 persona.md 读，逐字节送进模型。
//
// 为什么读文件而不是硬编码在 C# 里：
//   ① 你可以直接改措辞，不用重新构建（人格是要反复调的东西）；
//   ② 它是**可审计的** —— 你能一眼看到「她被告知了什么」，而不用翻源码；
//   ③ 逐字节稳定 ⇒ 每次都吃到前缀缓存（便宜几十倍）。所以**不要**往里拼时间戳。
//
// 为什么注入点在 TraeChat 而不是调用方：
//   调用方会忘。做成机制 ⇒ 任何路径（聊天窗、--chat 自检、将来的观察回路）都必然带上人格。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AzhuPet
{
    internal static class Persona
    {
        private static readonly Regex CommentRe = new Regex(@"<!--[\s\S]*?-->", RegexOptions.Compiled);
        private static readonly Regex GreetingRe = new Regex(
            @"<!--\s*greeting\s*-->(?<g>[\s\S]*?)<!--\s*/greeting\s*-->", RegexOptions.Compiled);
        /// <summary>文件开头的 YAML frontmatter。⚠ 它不是 HTML 注释，不剥掉会被当成正文送进模型 ——
        /// 于是「类型: 项目 / 标签: [桌宠, 人设]」这种属性会混进她的人格前缀。</summary>
        private static readonly Regex FrontmatterRe = new Regex(
            @"\A\s*---\r?\n[\s\S]*?\r?\n---[ \t]*\r?\n", RegexOptions.Compiled);

        /// <summary>库里那份真值的相对落点（相对仓库根）。</summary>
        private static readonly string[] RelativePath = { "40 Projects", "阿助（桌宠）", "persona.md" };

        /// <summary>
        /// 找不到 persona.md 时的骨架。⚠ 这是**副本，不是真值** —— 一旦被用上就是一处分叉，
        /// 所以 --personatest 见到 UsedFallback 直接判红。只保骨架，不补细节。
        /// </summary>
        private const string Skeleton =
            "你是阿助。你住在一块屏幕里，坐在毛玉的桌面上。\n" +
            "他叫毛玉，句子里用「你」。你可以有情绪，但不能要求他什么。\n" +
            "说话短，一两句。问句多于判断句。不确定就说不确定。\n" +
            "不催他做事，不管他的身体，不给建议，不主动报数字，不介绍自己。\n";

        private const string FallbackGreeting = "在的。";

        /// <summary>system 正文（已剥离全部 HTML 注释）。</summary>
        public static string Text { get; private set; }
        /// <summary>打开聊天窗时的第一句话。</summary>
        public static string Greeting { get; private set; }
        /// <summary>实际读到的路径；null = 没找到。</summary>
        public static string SourcePath { get; private set; }
        /// <summary>是否退回了内置骨架（＝出了问题）。</summary>
        public static bool UsedFallback { get; private set; }
        /// <summary>非空 = 可读的失败原因。⚠ 不许静默跳过：她变得不像她，必须留下原因。</summary>
        public static string Problem { get; private set; }

        static Persona() { Reload(null); }

        /// <summary>重新读取。改完 persona.md 打开聊天窗即可生效，不必重启进程。</summary>
        public static void Reload(string explicitPath)
        {
            Text = null; Greeting = null; SourcePath = null; UsedFallback = false; Problem = null;

            string path = Resolve(explicitPath);
            if (path == null)
            {
                UseSkeleton("找不到 persona.md（已从 exe 目录与工作目录各向上找 8 层，并查了 --persona 与 AZHU_PERSONA）。");
                return;
            }
            SourcePath = path;

            string raw;
            try { raw = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception ex) { UseSkeleton("读 persona.md 失败：" + ex.Message); return; }

            // 行尾归一化。⚠ 不归一化，同一份内容用 LF 存和用 CRLF 存会送出两组不同的字节，
            // 于是「逐字节稳定的前缀」这条缓存前提被行尾悄悄破坏 —— 实测就这么被绊过一次。
            raw = raw.Replace("\r\n", "\n").Replace("\r", "\n");

            // greeting 先取（它的标记本身也是注释形式），再从正文里剥掉一切注释。
            Match gm = GreetingRe.Match(raw);
            string greeting = gm.Success ? CommentRe.Replace(gm.Groups["g"].Value, "").Trim() : null;

            string body = Regex.Replace(StripComments(StripFrontmatter(raw)), @"\n{3,}", "\n\n").Trim();

            // ⚠ 注释未闭合时，StripComments 会**静默地什么都不剥** —— 整段注释当成正文送进模型，
            // 于是她会读到「不写禁词／不要拼时间戳」这类关于她自己的规格（＝自指）。
            // 实测踩过：漏一个 --> 时，只有恰好注释里带机制词才会被别的判据接住，那是运气不是判据。
            if (body.IndexOf("<!--", StringComparison.Ordinal) >= 0 || body.IndexOf("-->", StringComparison.Ordinal) >= 0)
            {
                UseSkeleton("persona.md 的 HTML 注释没闭合 —— 正文里还留着 <!-- 或 -->。"
                    + "这会把整段注释当成正文送进模型（她会读到关于自己的规格），所以按坏文件处理。");
                return;
            }

            if (body.Length < 60)
            {
                UseSkeleton("persona.md 剥离注释后只剩 " + body.Length + " 字，太短 —— 多半是注释没闭合（漏了 -->）。");
                return;
            }

            Text = body;
            Greeting = string.IsNullOrEmpty(greeting) ? FallbackGreeting : greeting;
        }

        /// <summary>剥掉文件开头的 YAML frontmatter（Obsidian 属性块）。</summary>
        public static string StripFrontmatter(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : FrontmatterRe.Replace(text, "", 1);
        }

        /// <summary>剥掉全文的 HTML 注释。暴露出来是为了让 --personatest 扫「原文」而不只是扫 Persona.Text
        /// —— 否则退回骨架时，禁词／机制词判据会失去意义（那正是「总数变小 ＋ 全绿」的假通过）。</summary>
        public static string StripComments(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : CommentRe.Replace(text, "");
        }

        /// <summary>按「显式参数 &gt; 环境变量 &gt; 向上搜仓库」定位 persona.md。</summary>
        public static string Resolve(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;
            string env = Environment.GetEnvironmentVariable("AZHU_PERSONA");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            // 两个起点：exe 目录（正常启动）＋ 工作目录（从源码树跑自检时）。
            // 与 Cli.ResolveModel 同一套路 —— 少了第二个，从别处跑 --personatest 就得每次手写全路径。
            foreach (string start in new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                if (string.IsNullOrEmpty(start)) continue;
                string d = start.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                for (int i = 0; i < 8 && !string.IsNullOrEmpty(d); i++)
                {
                    string p = Path.Combine(d, RelativePath[0], RelativePath[1], RelativePath[2]);
                    if (File.Exists(p)) return p;
                    string up = Path.GetDirectoryName(d);
                    if (string.IsNullOrEmpty(up) || up == d) break;
                    d = up;
                }
            }
            return null;
        }

        /// <summary>
        /// 保证 messages 第一条是 system（人格）。
        /// ⚠ 抽成**纯函数**，是为了它能被离线自检（--personatest）逼红 ——
        ///   只靠真接口验的判据没有资格失败。
        /// </summary>
        public static List<object> WithSystem(IReadOnlyList<object> messages)
        {
            var list = new List<object>();
            bool hasSystem = false;
            if (messages != null)
            {
                foreach (object m in messages)
                {
                    var d = m as IDictionary<string, object>;
                    object role;
                    if (d != null && d.TryGetValue("role", out role) && (role as string) == "system") hasSystem = true;
                    list.Add(m);
                }
            }
            if (!hasSystem) list.Insert(0, Message("system", Text));
            return list;
        }

        /// <summary>OpenAI 风格的一条消息（content 是分片数组 —— 与 Trae 兼容通道的既有格式一致）。</summary>
        public static Dictionary<string, object> Message(string role, string text)
        {
            return new Dictionary<string, object>
            {
                ["role"] = role,
                ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } },
            };
        }

        private static void UseSkeleton(string reason)
        {
            UsedFallback = true;
            Problem = reason;
            Text = Skeleton;
            Greeting = FallbackGreeting;
            // 留痕到 %LOCALAPPDATA%\AzhuPet\（不进 Obsidian 库 ⇒ 不进坚果云）。
            // 「文件没找到」必须留下可读原因，不能渲染成「少说了一句话」。
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzhuPet");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "persona_missing.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + reason + "\r\n", Encoding.UTF8);
            }
            catch { }
        }
    }
}
