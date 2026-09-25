// 自更新 —— 「检查版本 → 下载 → 下次启动替换」三段式。
//
// ⚠⚠ 设计前提（**实测过，不是想当然**）：Windows 允许**正在运行的进程重命名自己的 exe**。
//   我把一个正在跑的 .NET 程序改名成 xxx.exe.old，成功了。这条事实决定了整个设计：
//   **不需要**任何辅助脚本 / 独立 updater 进程 / 批处理 —— 程序自己就能让位。
//
// 三段各自独立、互不阻塞：
//   ① CheckAsync  —— 拉 version.json、比版本号。纯网络读，失败即静默降级。
//   ② DownloadAsync —— 下 zip、解压到 staging、校验（两个文件都在 + 大小非零）。
//   ③ ApplyPending —— **在主程序启动的最早期**跑：把 staging 的 pet.exe/persona.md 换过来。
//
// 为什么替换必须放在「启动最早期」而不是「下载完立刻做」：
//   下载完那一刻，**当前 exe 正被自己占用**（虽然能改名，但改名后当前进程仍在用旧文件句柄，
//   且 persona.md 可能正被读取）⇒ 留到下次启动、在任何人碰这两个文件之前做，最干净。
//
// ⚠⚠ 为什么 pet.exe 与 persona.md **必须一起换**：
//   persona.md 是 CC BY-NC-SA 的人格文本，与代码分离；只换 exe 不换人格，
//   就会出现「新程序 + 旧人格」的错位 —— 这正是本项目记过的「同一份数据两个落点」的坑。
//   所以 staging 校验**两个文件都要在**，缺一不替换。
//
// ⚠ 与发布形态绑定：本设计假设产物是「pet.exe + persona.md 两个文件」（框架依赖单文件）。
//   若哪天改成自包含或拆成多 dll，替换清单要跟着改 —— 别让它悄悄失配。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AzhuPet
{
    internal static class Updater
    {
        // ==== 更新源 ====
        // ⚠ 这是唯一需要维护的地址。发布时把 version.json 与 zip 一起挂到 GitHub Release，
        //   地址改了只改这一处（别在别处再写一份 URL）。
        //   用 raw 直链读 json（轻、无需 API token）；zip 走 release 直链。
        // ⚠⚠ 大小写敏感：仓库名是 **pet**（GitHub Desktop 用文件夹名建的仓，2026-09-21 实测确认）。
        //   `raw.githubusercontent.com` 路径**区分大小写** —— 写成 AzhuPet 会 404，
        //   而 404 在这条链路上表现为「检查更新失败」，看不到「地址写错了」。
        //   ⚠ 实测教训：本地文件夹叫「阿助娘化形象」、代码命名空间叫 AzhuPet，都会让人误以为
        //   仓库名也是 AzhuPet。**别猜仓库名，去看 `git remote -v`。**
        public const string DefaultFeedUrl =
            "https://raw.githubusercontent.com/mauyumaster/pet/main/version.json";

        /// <summary>仓库主页（设置面板「关于」栏给用户点的那个链接）。</summary>
        public const string RepoUrl = "https://github.com/mauyumaster/pet";

        // ==== 状态落点：都在配置目录下，不进 Obsidian 库 ====
        private static string StageDir { get { return Path.Combine(PetConfig.Dir, "update"); } }
        private static string PendingFlag { get { return Path.Combine(StageDir, "pending.json"); } }

        // ==== 下载来的 zip 里必须有的文件（与 pack-release.cmd 的产物一致）====
        // ⚠ 这三件是「缺一不可」：少 pet.exe 没得跑；少 persona.md 她退回骨架音色；
        //   少 model/ 她启动就弹「找不到 chibi_maid_pet.glb」直接退出（2026-09-21 真实翻车）。
        //   所以模型也从「可选」升格成「必须有」—— 校验的核心价值就是拦住「装完反而不能用」。
        private static readonly string[] PayloadNames =
            { "pet.exe", "persona.md", "model/chibi_maid_pet.glb", "WebView2Loader.dll" };

        // ⚠ 向后兼容：2026-09-21 之前的发布包（v0.1.0 及更早）**没有模型**。
        //   如果一口咬定「没有模型就作废」，那些版本永远无法被更新覆盖。
        //   判据：**核心文件（pet.exe/persona.md）缺一即作废；模型缺失则容忍**，
        //   但一旦包里带了模型，就必须完整（有名字、非空）。
        // ⚠ WebView2Loader.dll 同理**必须容忍缺失**（2026-09-25 加）：它是 0.1.2 才引入 WebView2 时
        //   该随包分发却漏掉的那一个原生加载器（见 WebTest.cs 顶部的实测）。已经发出去的 0.1.0/0.1.1
        //   包里没有它，若据「缺它就作废」就再也推不动那些版本 —— 而那恰恰是最需要被修好的用户。
        //   ⇒ 它在包里就装上，不在就跳过（老用户先升到带它的版本，下一个包再补上）。
        private static bool IsCorePayload(string name)
        {
            return name == "pet.exe" || name == "persona.md";
        }

        // ---------------------------------------------------------------- 版本号

        /// <summary>当前版本，如 "0.1.0"。**单一来源是 pet.csproj 的 &lt;Version&gt;** ——
        /// 这里不硬编码，避免「同一份数据两个落点」。</summary>
        public static string CurrentVersion
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                    if (info != null && !string.IsNullOrEmpty(info.InformationalVersion))
                    {
                        // NuGet 的 SourceLink 会往后面粘 "+abcdef" 之类的构建元数据，比较前剥掉
                        string v = info.InformationalVersion;
                        int plus = v.IndexOf('+');
                        if (plus > 0) v = v.Substring(0, plus);
                        return v.Trim();
                    }
                    var ver = asm.GetName().Version;
                    if (ver != null) return ver.Major + "." + ver.Minor + "." + ver.Build;
                }
                catch { }
                return "0.0.0";
            }
        }

        /// <summary>语义化版本比较：a 比 b 新返回 &gt; 0，旧返回 &lt; 0，相同 0。
        ///
        /// ⚠⚠ **不能用字符串比大小** —— 那会把 "0.10.0" 判成比 "0.9.0" **旧**（'1' &lt; '9'），
        ///   而 0.10.0 明明是新的。这个坑在版本号跨两位数时必然出现，且症状是「有新版本却不提示」，
        ///   极难联想到版本比较。⇒ 逐段转数字比。
        /// 非数字段（如 "1.0.0-beta"）按「有后缀 &lt; 无后缀」处理（预发布版更旧），与 semver 一致。</summary>
        public static int CompareVersions(string a, string b)
        {
            int[] pa = ParseVersion(a), pb = ParseVersion(b);
            for (int i = 0; i < 3; i++)
            {
                if (pa[i] != pb[i]) return pa[i] > pb[i] ? 1 : -1;
            }
            // 主版本相同时：带后缀的是预发布，判更旧
            bool sa = HasSuffix(a), sb = HasSuffix(b);
            if (sa != sb) return sa ? -1 : 1;
            return 0;
        }

        private static bool HasSuffix(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            return v.IndexOf('-') >= 0;
        }

        /// <summary>把 "1.2.3" / "v1.2.3" / "1.2" / "1" 解析成三段数字。解不出的一段记 0。</summary>
        private static int[] ParseVersion(string v)
        {
            var r = new int[] { 0, 0, 0 };
            if (string.IsNullOrEmpty(v)) return r;
            v = v.Trim();
            if (v.StartsWith("v") || v.StartsWith("V")) v = v.Substring(1);
            int dash = v.IndexOf('-');                 // 丢掉 -beta 之类
            if (dash >= 0) v = v.Substring(0, dash);
            string[] parts = v.Split('.');
            for (int i = 0; i < 3 && i < parts.Length; i++)
            {
                int n;
                if (int.TryParse(parts[i].Trim(), out n)) r[i] = n;
            }
            return r;
        }

        // ---------------------------------------------------------------- ① 检查

        internal sealed class CheckResult
        {
            public bool Ok;                  // 检查本身成功（网络+解析都对）
            public string RemoteVersion;    // 远端版本号
            public string DownloadUrl;      // zip 下载地址
            public string Notes;             // 可选：更新说明
            public bool HasUpdate;           // 远端比本地新
            public string Message;           // 失败原因（给用户看的大白话）
        }

        /// <summary>检查有没有新版本。**永不抛异常、永不阻塞 UI** —— 失败就返回 Ok=false，
        /// 调用方只需要「有新版本就提示，其他都静默」。⚠ 更新检查失败绝不该影响桌宠本身。</summary>
        public static async Task<CheckResult> CheckAsync(string feedUrl = null, int timeoutMs = 15000)
        {
            var r = new CheckResult();
            try
            {
                string url = string.IsNullOrEmpty(feedUrl) ? DefaultFeedUrl : feedUrl;
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
                    http.DefaultRequestHeaders.Add("User-Agent", "AzhuPet/" + CurrentVersion);
                    string json = await http.GetStringAsync(url).ConfigureAwait(false);
                    ParseFeed(json, r);          // ParseFeed 自己负责把自己判成功/失败
                }
                // ⚠ 这里**不**无条件 r.Ok = true —— 解析失败（缺 version 字段）时必须保持 false，
                //   否则「feed 写坏了」会被当成「检查成功、只是没更新」，静默吞掉。
                r.HasUpdate = r.Ok && CompareVersions(r.RemoteVersion, CurrentVersion) > 0;
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.HasUpdate = false;
                r.Message = DescribeNetworkError(ex);
            }
            return r;
        }

        /// <summary>解析 version.json。格式（故意极简，键名与 pack-release.cmd 产出一致）：
        /// { "version": "0.2.0", "url": "https://.../AzhuPet-v0.2.0-win-x64.zip", "notes": "..." }
        ///
        /// ⚠ 本方法**自己负责设置 r.Ok**（成功 true、失败 false），调用方不要在外面再覆盖。
        ///   第一版这里是「只置 false、从不在成功时置 true」，由 CheckAsync 事后统一补 true ——
        ///   结果 `ParseFeed` 单独用时，一个完全正常的 feed 也会返回 Ok=false（判据当场抓到）。
        ///   一个「解析成功了却说自己没成功」的方法是个坏 API，所以把成功判定收回到这里。</summary>
        public static void ParseFeed(string json, CheckResult r)
        {
            r.RemoteVersion = JsonStr(json, "version");
            r.DownloadUrl = JsonStr(json, "url");
            r.Notes = JsonStr(json, "notes");
            if (string.IsNullOrEmpty(r.RemoteVersion))
            {
                r.Ok = false;
                r.Message = "更新信息里没有 version 字段";
            }
            // url 缺失时不能算「有更新」—— 提示了却下不了，比不提示更糟
            else if (string.IsNullOrEmpty(r.DownloadUrl))
            {
                r.Ok = false;
                r.Message = "更新信息里没有 url 字段";
            }
            else
            {
                r.Ok = true;
                r.Message = null;
            }
        }

        /// <summary>极简取字符串字段（不复用 PetConfig 的 Raw —— 那是给固定格式配置文件用的，
        /// 这里是任意 JSON，键顺序不定）。找不到返回 null。</summary>
        private static string JsonStr(string s, string key)
        {
            if (string.IsNullOrEmpty(s)) return null;
            int i = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int c = s.IndexOf(':', i);
            if (c < 0) return null;
            int q1 = s.IndexOf('"', c + 1);
            if (q1 < 0) return null;
            var sb = new StringBuilder();
            for (int k = q1 + 1; k < s.Length; k++)
            {
                char ch = s[k];
                if (ch == '\\' && k + 1 < s.Length)
                {
                    char n = s[++k];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'u':
                            if (k + 4 < s.Length)
                            {
                                int cp;
                                if (int.TryParse(s.Substring(k + 1, 4),
                                        System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out cp))
                                { sb.Append((char)cp); k += 4; }
                            }
                            break;
                        default: sb.Append(n); break;
                    }
                    continue;
                }
                if (ch == '"') break;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string DescribeNetworkError(Exception ex)
        {
            return DescribeNetworkErrorPublic(ex);
        }

        /// <summary>给诊断入口用的公开版本（DescribeNetworkError 本身是 private）。</summary>
        public static string DescribeNetworkErrorPublic(Exception ex)
        {
            // 网络问题对用户来说只有两种有意义：连不上 / 超时。其余归「暂时无法检查」。
            if (ex is TaskCanceledException || ex is OperationCanceledException) return "连接超时";

            // ⚠⚠ 代理是我们真实踩过的坑（2026-09-21）：环境变量里的 *_PROXY 指向一个
            //   **已经关掉的进程留下的死端口**，HttpClient 照着走 ⇒ 502 / 连不上，
            //   而用户只看到「检查更新失败」，完全不知道该去查代理。
            //   所以这里把「系统当前用的代理」原样报出来 —— 用户一眼就知道去哪儿改。
            var e = ex;
            while (e != null)
            {
                if (e is System.Net.Http.HttpRequestException)
                {
                    string via = CurrentProxyDescription();
                    return via == null
                        ? "网络不可达（可能未联网）"
                        : "网络不可达 —— 系统代理是 " + via + "，若它没在运行请关掉代理再试";
                }
                e = e.InnerException;
            }
            return "暂时无法检查更新";
        }

        /// <summary>
        /// 报出「本进程当前会走哪个代理」—— 诊断用，绝不抛异常。
        /// 返回 null 表示判定为直连。返回形如 "http://127.0.0.1:61827（走环境变量）"。
        /// </summary>
        public static string CurrentProxyDescription()
        {
            try
            {
                // 顺序与 .NET 实际取值一致：环境变量优先于 IE/系统设置
                foreach (string name in new string[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
                {
                    string v = Environment.GetEnvironmentVariable(name);
                    if (!string.IsNullOrWhiteSpace(v))
                        return v.Trim() + "（走环境变量 " + name + "）";
                }
                var wi = System.Net.WebRequest.GetSystemWebProxy();
                if (wi != null)
                {
                    var u = wi.GetProxy(new Uri("https://raw.githubusercontent.com/"));
                    if (u != null && u.Host != "raw.githubusercontent.com")
                        return u.ToString() + "（走系统设置）";
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- ② 下载

        /// <summary>下载 zip → 解压到 staging → 校验 → 落 pending 标记。返回是否成功。
        /// ⚠ 全流程在 staging 里进行，**下载/解压失败绝不碰任何现有文件** —— 更新失败最坏的结果
        ///   只能是「没更成」，不能是「程序坏了」。这是与配置文件那次事故的根本区别。
        /// ⚠ **不在这里替换**，只准备。真正替换在下次启动（ApplyPending）。</summary>
        public static async Task<bool> DownloadAsync(CheckResult plan, Action<int> onProgress = null,
                                                     int timeoutMs = 120000)
        {
            if (plan == null || !plan.HasUpdate || string.IsNullOrEmpty(plan.DownloadUrl)) return false;

            DownloadError = null;   // 清掉上一次的残留，别把旧错误报成这次的

            // 用一个全新的临时目录做暂存，避免上次的残留混进来
            string tmp = StageDir + ".tmp-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                Directory.CreateDirectory(tmp);
                string zipPath = Path.Combine(tmp, "update.zip");

                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
                    http.DefaultRequestHeaders.Add("User-Agent", "AzhuPet/" + CurrentVersion);
                    using (var resp = await http.GetAsync(plan.DownloadUrl,
                               HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        long total = resp.Content.Headers.ContentLength ?? -1;
                        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                        {
                            byte[] buf = new byte[81920];
                            long got = 0;
                            int n;
                            while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                            {
                                await dst.WriteAsync(buf, 0, n).ConfigureAwait(false);
                                got += n;
                                if (onProgress != null && total > 0)
                                    onProgress((int)(got * 100 / total));
                            }
                        }
                    }
                }

                // 解压到 payload 子目录
                string payloadDir = Path.Combine(tmp, "payload");
                Directory.CreateDirectory(payloadDir);
                ZipFile.ExtractToDirectory(zipPath, payloadDir);

                // ==== 校验：核心文件必须存在且非空；模型若带则必须完整 ====
                // 核心（pet.exe/persona.md）缺一 ⇒ 整包作废（装上去反而不能用）
                // 模型：包里带了就必须非空；没带则容忍（= 兼容 v0.1.0 及更早的旧包，
                //   它们没有模型，若不兼容则那些版本永远更新不上来）
                foreach (string name in PayloadNames)
                {
                    string f = Path.Combine(payloadDir, name);
                    bool exists = File.Exists(f);
                    if (!exists)
                    {
                        if (IsCorePayload(name)) { Cleanup(tmp); return false; }
                        continue;   // 非核心缺失：容忍
                    }
                    if (new FileInfo(f).Length == 0) { Cleanup(tmp); return false; }
                }

                // 原子换位：先把旧的 staging 挪走，再把新的放到位
                string finalDir = StageDir;
                string oldDir = StageDir + ".old";
                if (Directory.Exists(oldDir)) { try { Directory.Delete(oldDir, true); } catch { } }
                if (Directory.Exists(finalDir))
                {
                    try { Directory.Move(finalDir, oldDir); } catch { Cleanup(tmp); return false; }
                }
                try { Directory.Move(tmp, finalDir); }
                catch
                {
                    // 放不进去就把旧的请回来，别把用户的更新现场搞丢
                    if (Directory.Exists(oldDir) && !Directory.Exists(finalDir))
                    { try { Directory.Move(oldDir, finalDir); } catch { } }
                    Cleanup(tmp);
                    return false;
                }
                try { if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true); } catch { }

                // 落 pending 标记：写明「要换成哪个版本」
                File.WriteAllText(PendingFlag,
                    "{\r\n  \"version\": \"" + Esc(plan.RemoteVersion) + "\",\r\n" +
                    "  \"preparedAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\"\r\n}\r\n",
                    new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                Cleanup(tmp);
                DownloadError = DescribeNetworkError(ex);
                return false;
            }
        }

        /// <summary>最后一次 DownloadAsync 失败的原因（成功时为 null）。
        /// ⚠ 光返回 false 的话，调用方只能显示「下载失败」，用户不知道是断网、代理还是别的事；
        ///   而我们确实踩过代理的坑（见 DescribeNetworkError）。所以把原因留在外面。</summary>
        public static string DownloadError { get; private set; }

        private static void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        // ---------------------------------------------------------------- ③ 启动期替换

        /// <summary>有没有已经下载好、等下次启动替换的版本。没准备好返回 null。</summary>
        public static string PendingVersion()
        {
            try
            {
                if (!File.Exists(PendingFlag)) return null;
                string json = File.ReadAllText(PendingFlag, Encoding.UTF8);
                string v = JsonStr(json, "version");
                // 标记在、但 payload 不全 ⇒ 视为无效（比如用户手工删了文件）
                if (string.IsNullOrEmpty(v) || !StagingLooksValid()) return null;
                // 已经比当前旧（比如用户手工装了更新的版本）⇒ 不该再替换
                if (CompareVersions(v, CurrentVersion) <= 0) return null;
                return v;
            }
            catch { return null; }
        }

        private static bool StagingLooksValid()
        {
            try
            {
                foreach (string name in PayloadNames)
                {
                    string f = Path.Combine(StageDir, "payload", name.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(f))
                    {
                        // 与下载期的口径一致：核心缺一即无效；非核心缺失只是容忍
                        if (IsCorePayload(name)) return false;
                        continue;
                    }
                    if (new FileInfo(f).Length == 0) return false;
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>把 staging 里的文件换到正在运行的自己身上。**在启动最早期调用**（任何窗口创建之前）。
        ///
        /// 核心技巧（已实测）：**正在运行的程序可以重命名自己的 exe**。
        ///   ① 把现役 pet.exe 改名成 pet.exe.old
        ///   ② 把新的 pet.exe 拷/移进来
        ///   ③ 同理处理 persona.md
        ///   ④ 删掉 pending 标记
        /// 这样当前进程继续用旧文件句柄跑完这一轮，**下次启动**就是新版 —— 用户感觉「重启一下，变新了」。
        ///
        /// 返回值：是否真的替换了（供自检与日志用）。
        /// ⚠ 任何一步失败都**保持原样**、不清 pending（下次启动再试），绝不让程序变得跑不起来。</summary>
        public static bool ApplyPending(out string detail)
        {
            detail = "";
            string pending = PendingVersion();
            if (string.IsNullOrEmpty(pending)) { detail = "没有待替换的版本"; return false; }

            string me = Environment.ProcessPath;
            if (string.IsNullOrEmpty(me) || !File.Exists(me))
            {
                // 单文件发布时 ProcessPath 就是 pet.exe；取不到就退到 exe 目录猜
                string dir0 = AppDomain.CurrentDomain.BaseDirectory;
                string guess = Path.Combine(dir0, "pet.exe");
                if (File.Exists(guess)) me = guess;
                else { detail = "定位不到自身的 exe"; return false; }
            }
            string appDir = Path.GetDirectoryName(me);
            string payloadDir = Path.Combine(StageDir, "payload");

            var done = new List<string>();
            try
            {
                foreach (string name in PayloadNames)
                {
                    // ⚠ 名字可能带子目录（"model/chibi_maid_pet.glb"），必须换成分隔符并按需建目录
                    string rel = name.Replace('/', Path.DirectorySeparatorChar);
                    string src = Path.Combine(payloadDir, rel);
                    string dst = Path.Combine(appDir, rel);
                    string old = dst + ".old";

                    if (!File.Exists(src))
                    {
                        // 非核心缺失：跳过（把旧的留在原地，比删掉好）
                        if (!IsCorePayload(name)) { detail = "（" + name + " 不在包里，保留原文件）"; continue; }
                        detail = "暂存里缺 " + name; Rollback(appDir, done); return false;
                    }

                    // 目标可能在新目录（首次带模型发布时 model/ 还不存在）
                    string dstDir = Path.GetDirectoryName(dst);
                    try { if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir)) Directory.CreateDirectory(dstDir); }
                    catch (Exception ex) { detail = "无法创建目录 " + dstDir + "：" + ex.GetType().Name; Rollback(appDir, done); return false; }

                    // 清掉上一次留下的 .old（可能还占着，删不掉就留着，不影响）
                    try { if (File.Exists(old)) File.Delete(old); } catch { }

                    // ① 现役文件让位（**正在运行的 exe 也能被改名**，这是实测结论）
                    if (File.Exists(dst))
                    {
                        try { File.Move(dst, old); }
                        catch (Exception ex) { detail = "无法让位 " + name + "：" + ex.GetType().Name; Rollback(appDir, done); return false; }
                    }

                    // ② 新文件就位
                    try { File.Move(src, dst); }
                    catch (Exception ex)
                    {
                        // 放不进去就把旧的请回来
                        try { if (File.Exists(old) && !File.Exists(dst)) File.Move(old, dst); } catch { }
                        detail = "无法就位 " + name + "：" + ex.GetType().Name;
                        Rollback(appDir, done);
                        return false;
                    }
                    done.Add(rel);
                }

                // ④ 成功：清掉标记与暂存
                try { File.Delete(PendingFlag); } catch { }
                try { Cleanup(StageDir); } catch { }
                detail = "已替换为 " + pending;
                return true;
            }
            catch (Exception ex)
            {
                detail = "替换失败：" + ex.GetType().Name;
                Rollback(appDir, done);
                return false;
            }
        }

        /// <summary>把已经换过的文件还原回去（用 .old）。尽力而为，失败也不抛。</summary>
        private static void Rollback(string appDir, List<string> done)
        {
            foreach (string name in done)
            {
                try
                {
                    string dst = Path.Combine(appDir, name);
                    string old = dst + ".old";
                    if (File.Exists(old))
                    {
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(old, dst);
                    }
                }
                catch { }
            }
        }

        /// <summary>清掉上次替换成功后留下的 .old（启动稳定后调用；删不掉说明还被占着，下次再说）。</summary>
        public static void CleanupOldFiles()
        {
            try
            {
                string me = Environment.ProcessPath;
                if (string.IsNullOrEmpty(me)) return;
                string appDir = Path.GetDirectoryName(me);
                foreach (string name in PayloadNames)
                {
                    string old = Path.Combine(appDir, name + ".old");
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                }
            }
            catch { }
        }

        private static string Esc(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            return v.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
