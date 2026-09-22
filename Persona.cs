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

        /// <summary>库里那份真值的文件名。</summary>
        private const string PersonaFileName = "persona.md";

        /// <summary>真值所在的上一层锚点（相对仓库根）。</summary>
        private const string ProjectsAnchor = "40 Projects";

        /// <summary>
        /// 项目目录名的**候选**（真值所在处，按优先级排列）。
        ///
        /// ⚠⚠ 这里留一段**误判的自述，给以后的人（包括我）**：
        ///   2026-09-22 我看到这行是「阿助（桌宠）」，而 `40 Projects\` 下还有个
        ///   「阿助娘化形象」目录，就去查 `阿助娘化形象\persona.md` —— File not found，
        ///   于是断言「目录名写错了 ⇒ 这条路从来没成功过」，还据此动手"修"了代码。
        ///   结果**负对照没红**，追下去才发现：`40 Projects\阿助（桌宠）\persona.md`
        ///   **真的存在**（2605 B，那是她的「人设集」目录，里面有 她是谁.md、阿助（桌宠）.md）。
        ///   原来那句一直是**对的**，错的是我的搜索。
        ///   ⇒ 老坑重演：「我在本机没找到」被升级成了「它不存在」。
        ///     **搜索返回空不是证据，只说明我搜的路径不对。**
        /// 所以这里**保持原优先级**：「阿助（桌宠）」是真值，新目录名只作后备韧性。
        /// </summary>
        private static readonly string[] ProjectDirNames = { "阿助（桌宠）", "阿助娘化形象" };

        /// <summary>
        /// 真值在项目目录内的相对子路径候选，**顺序即优先级**。
        /// 第 1 项是真值；第 2 项是发布副本（带 CC BY-NC-SA 许可头，见 pack-release.cmd 的说明）。
        /// ⚠ 两份的**正文目前逐字节相同**（剥掉 frontmatter 与注释后 sha1 一致，
        ///   2026-09-22 实测 608 字；raw 差 156 B 就是那个许可头）。
        ///   但它们是**两个落点、靠手抄同步** —— 一旦漂移，「她是谁」就会取决于从哪儿启动，
        ///   而运行时不报任何错。防漂移判据见 PersonaTest 的 sourcesConsistent。
        /// </summary>
        private static readonly string[][] SubPathCandidates =
        {
            new[] { PersonaFileName },
            new[] { "pet", PersonaFileName },
        };

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
                // ⚠ 诊断信息必须**可执行**：只说「找不到」没用，要写清找过哪些具体候选，
                //   否则下次她变得不像她时，还得靠人重读源码才知道该把文件放哪儿。
                //   候选列表从常量算出来，不手写 —— 免得改了候选、诊断还说着旧的。
                List<string> subs = new List<string>();
                foreach (string[] s in SubPathCandidates) subs.Add(string.Join("\\", s));
                UseSkeleton("找不到 persona.md。查过 --persona 与 AZHU_PERSONA；也试过两个起点"
                    + "（exe 目录 " + AppDomain.CurrentDomain.BaseDirectory
                    + " ／ 工作目录 " + Directory.GetCurrentDirectory() + "）下的裸 " + PersonaFileName
                    + "，以及各自向上 8 层内的 " + ProjectsAnchor + "\\{"
                    + string.Join("|", ProjectDirNames) + "}\\{" + string.Join("|", subs) + "}。");
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

        /// <summary>按「显式参数 &gt; 环境变量 &gt; 候选落点」定位 persona.md。</summary>
        public static string Resolve(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;
            string env = Environment.GetEnvironmentVariable("AZHU_PERSONA");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

            foreach (string p in EnumerateCandidates())
                if (File.Exists(p)) return p;
            return null;
        }

        /// <summary>
        /// 所有候选落点（**含不存在的**），按优先级排列。Resolve 取第一个存在的。
        ///
        /// 两个起点：exe 目录（正常启动）＋ 工作目录（从源码树跑自检时）。
        /// 与 Cli.ResolveModel 同一套路 —— 少了第二个，从别处跑 --personatest 就得每次手写全路径。
        /// ⚠ 发布形态（zip 解压后 exe 与 persona.md 同目录）也要能找到：Release 里没有库结构，
        ///   「向上搜仓库」永远撞不到 —— 所以先列 exe 目录／工作目录下的**裸 persona.md**。
        ///   这一步放最前：与 exe 同目录的文件优先于仓库深处的同名文件（部署的比源码树的更「真」）。
        ///
        /// ⚠ 把它从 Resolve 里抽出来，是为了 ExistingSources() 能复用同一份枚举 ——
        ///   否则「解析用一套顺序、检查用另一套」，判据查的和实际读的就不是同一个东西了。
        /// </summary>
        public static List<string> EnumerateCandidates()
        {
            var list = new List<string>();
            Action<string> add = delegate (string p)
            {
                if (string.IsNullOrEmpty(p)) return;
                string full;
                try { full = Path.GetFullPath(p); } catch { return; }
                if (!list.Exists(delegate (string x) { return string.Equals(x, full, StringComparison.OrdinalIgnoreCase); }))
                    list.Add(full);
            };

            string[] starts = { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() };

            // 第 1 轮：两个起点下的**裸** persona.md，必须单独成轮且放最前。
            // ⚠ 注释里那句「与 exe 同目录的文件优先于仓库深处的同名文件」就是这一轮的意义。
            //   发布形态（zip 解压后 exe 与 persona.md 同目录）全靠它 —— 一旦让「向上搜」
            //   插到前面，只要安装目录附近存在一个 40 Projects，她就会去读那儿的人格。
            //   （这一轮是我 2026-09-22 重构时差点弄丢的语义：当时把两轮并成了一轮交叉执行。）
            foreach (string start in starts)
            {
                if (string.IsNullOrEmpty(start)) continue;
                string d0 = start.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                add(Path.Combine(d0, PersonaFileName));
            }

            // 第 2 轮：向上搜「项目目录内的相对落点」（开发布局用）。
            foreach (string start in starts)
            {
                if (string.IsNullOrEmpty(start)) continue;
                string d = start.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                for (int i = 0; i < 8 && !string.IsNullOrEmpty(d); i++)
                {
                    // 目录名 × 子路径 逐个试：两个维度里任一写死，这条路就静默失效（见 ProjectDirNames）。
                    foreach (string dirName in ProjectDirNames)
                    {
                        string projDir = Path.Combine(d, ProjectsAnchor, dirName);
                        foreach (string[] sub in SubPathCandidates)
                        {
                            string[] full = new string[sub.Length + 1];
                            full[0] = projDir;
                            Array.Copy(sub, 0, full, 1, sub.Length);
                            add(Path.Combine(full));
                        }
                    }
                    string up = Path.GetDirectoryName(d);
                    if (string.IsNullOrEmpty(up) || up == d) break;
                    d = up;
                }
            }
            return list;
        }

        /// <summary>
        /// 所有**实际存在**的 persona.md 落点（真值 ＋ 发布副本 ＋ 裸文件），按优先级排列。
        ///
        /// ⚠ 为什么专门有这个：真值（她的「人设集」目录 `40 Projects\阿助（桌宠）\persona.md`）
        ///   与发布副本（`pet\persona.md`，多一个 CC BY-NC-SA 许可头）是**两个落点、靠手抄同步**。
        ///   一旦漂移，「她是谁」就取决于从哪儿启动 —— 而运行时**一个错都不报**，
        ///   你只会觉得「今天她有点怪」，查起来极难。所以让 --personatest 把它判红。
        ///   实测（2026-09-22）：两份并存且正文逐字节相同（剥 frontmatter/注释后 608 字同 sha1）。
        /// </summary>
        public static List<string> ExistingSources()
        {
            var found = new List<string>();
            foreach (string p in EnumerateCandidates())
                if (File.Exists(p)) found.Add(p);
            return found;
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
