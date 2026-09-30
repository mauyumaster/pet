// 把「Trae 令牌同步扩展」投放到 Trae 的扩展目录 —— 让**别人的桌宠**也能取到自己 Trae 余额。
//
// 背景（2026-09-30 定案）：Trae 的积分请求由主进程原生 ttnet 发出，三路抓包全抓不到；
// 它的凭据是桌面 IDE 现签的 `Cloud-IDE-JWT`（14 天到期），浏览器里根本不存在这一份。
// 实测确认唯一可行的通用路径是：一个约 200 行的 Trae 扩展，在 activate 时调用 Trae
// **自己的**内部命令 `icube.cloudide.getByteCloudToken`，把令牌写进阿助的凭据文件。
// 扩展源码随桌宠分发（trae-ext/），本文件负责投放与状态。
//
// ⚠ 三条纪律：
//   ① **不擅自投放** —— 往别的应用的目录里写东西必须由用户点一下（卡片上的按钮）。
//   ② **落点自适应，不硬编码** —— 扩展目录 = %USERPROFILE%\<product.json 的 dataFolderName>\extensions。
//      本机是 `~/.trae-cn/extensions`，但那是**读出来的**，不是写死的。
//   ③ **投完要验证** —— 拷贝之后再逐字节比一次，否则「装了但没生效」会静默。
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AzhuPet
{
    internal static class TraeExtInstaller
    {
        /// <summary>扩展标识（= package.json 里的 publisher.name）。</summary>
        public const string PubId = "azhupet.trae-token-sync";
        /// <summary>扩展版本。改扩展代码时要**一起改这里**（和 `trae-ext/package.json` 的 version）。</summary>
        public const string Version = "0.1.2";
        /// <summary>扩展在 Trae 扩展目录里的文件夹名 —— **固定，不带版本号**。
        /// ⚠⚠ 这里**故意违反** VSCode 的 `&lt;publisher&gt;.&lt;name&gt;-&lt;version&gt;` 惯例，因为带版本号的名字
        ///   会制造「同一个 id 同时存在两个目录」的瞬间，而**那一瞬间正是 Trae 不激活的原因**：
        ///   Trae 的扩展目录 watcher 把短时间内的变更合并成一批，**同一批里同一个 id 出现多次变更
        ///   （新建新目录 + 扫到/删掉旧目录）时，它只把扩展加进 profile、不执行 `activate`**
        ///   —— 2026-09-30 实测：一批里 3 次变更（建 `-0.1.1` / 扫 `-0.1.0-b` / 删 `-0.1.0`）
        ///   ⇒ 日志只有 `Added extensions to default profile from external source`，
        ///   整个会话搜不到 `_doActivateExtension`；而**只新建一个目录**时 30 ms 内就激活。
        ///   固定名字 ⇒ 升级变成**就地覆盖**，永远只有一个目录、批次里只有文件变更 ⇒ 不再有这个问题。
        ///   代价：升级不在运行中的会话里生效（VSCode 对同 id 换版本本就要重启），
        ///   下次启动 Trae 时加载新代码 —— 已写进 `RestartHint`。</summary>
        public const string FolderName = PubId;

        /// <summary>随包分发的扩展源码目录名（**与 pet.csproj / pack-release.cmd / azhupet.iss 三处同名**）。
        /// ⚠ 改名要四处一起改：那三处按名字打包含，本文件按名字找源。</summary>
        public const string SourceDirName = "trae-ext";
        public const string JsFile = "extension.js";
        public const string ManifestFile = "package.json";

        /// <summary>凭据骨架里的那一行 URL —— **桌宠负责「请求长什么样」的唯一落点**。
        /// ⚠ 扩展刻意不知道这个 URL：它只改 `authorization` 一行，不创建文件。
        ///   这样「格式」只有一处真值，不会出现 C# 与 JS 各写一份 URL 的局面。</summary>
        public const string EntitlementUrl = "https://api.trae.cn/trae/api/v2/pay/user_current_entitlement_list";

        /// <summary>投放到 Trae 之后，用户还要做的一件事。
        /// ⚠ 两种情形不一样，**都实测过**：**全新安装**（该批次里只有这一个变更）时，Trae 运行中
        ///   几十毫秒内就加载并执行；**同 id 的就地升级**不会在运行中的会话里生效，下次启动才换装。
        ///   ⚠ 第二条**不是**"因为同一个 id" —— 而是 VSCode 本就不对同 id 的版本变更重新 `activate`。
        ///   真正的病（同一批次里同 id 出现两个变更 ⇒ 只登记、永不激活、重启也救不回来）已经用
        ///   **固定目录名 + 投放里不删东西**治掉了，见 `FolderName` 的注释。
        ///   ⇒ 用一句「会自动加载」概括两种情形是错的。</summary>
        public const string RestartHint = "首次安装时若 Trae 正在运行，扩展会当场加载；之后升级是就地覆盖，下次启动 Trae 时生效。";

        // ============================ 状态 ============================

        public enum State
        {
            NoTrae,         // 找不到 Trae 的扩展目录
            NoSource,       // 桌宠旁边没有 trae-ext（没随包分发）
            NotInstalled,   // Trae 在，但扩展没装
            Installed,      // 装了且与源一致
            Outdated,       // 装了但与源不一致（换了桌宠版本，或扩展被改过）
        }

        public sealed class ExtStatus
        {
            public State State;
            public string ExtDir;        // Trae 的 extensions 目录；找不到时 null
            public string SourceDir;     // 桌宠旁边的 trae-ext
            public string Detail;        // 一句话解释（进界面与日志）
            public bool HasTrae => !string.IsNullOrEmpty(ExtDir);
        }

        public static string SourceDir() => Path.Combine(AppContext.BaseDirectory, SourceDirName);

        /// <summary>源文件是否齐备（两个文件都在）。缺哪个就说哪个 —— 「装了但没生效」最难查。</summary>
        public static bool SourceReady(out string why)
        {
            string d = SourceDir();
            if (!File.Exists(Path.Combine(d, JsFile))) { why = "缺少 " + Path.Combine(d, JsFile); return false; }
            if (!File.Exists(Path.Combine(d, ManifestFile))) { why = "缺少 " + Path.Combine(d, ManifestFile); return false; }
            why = null; return true;
        }

        // ---- 落点解析：拆成「纯函数挑候选」+「薄薄一层查盘」，这样能离线证伪 ----

        /// <summary>Trae 数据目录的候选清单（**纯函数**，可离线喂合成 home/appdata）。
        /// 顺序 = 可信度：先按已存在的目录（枚举 `%USERPROFILE%\.trae*` 与 `%APPDATA%\TRAE*`），
        /// 再列两个常见默认名兜底。
        /// ⚠ 为什么不硬编码 `.trae-cn`：那是从本机 product.json 读出来的**这一次**的事实；
        ///   换个 Trae 版本/渠道就可能变（`.trae` / 国际版另有其名）。候选 + 枚举两层足够稳。</summary>
        public static List<string> DataDirCandidates(string home, string appData)
        {
            var list = new List<string>();
            AddEnumerated(list, home, ".trae*");
            AddEnumerated(list, home, "trae*");
            if (!string.IsNullOrEmpty(appData)) AddEnumerated(list, appData, "TRAE*");
            foreach (var p in new[]
            {
                Path.Combine(home, ".trae-cn"),
                Path.Combine(home, ".trae"),
                string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "TRAE SOLO CN"),
            })
                if (!string.IsNullOrEmpty(p) && !list.Contains(p)) list.Add(p);
            return list;
        }

        private static void AddEnumerated(List<string> list, string root, string pattern)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
                foreach (var d in Directory.GetDirectories(root, pattern))
                    if (!list.Contains(d)) list.Add(d);
            }
            catch { /* 权限/竞态都当「这个候选没有」 */ }
        }

        /// <summary>扩展目录 = 第一个存在的候选 + \extensions。找不到返回 null（**纯函数**，exists 由调用方注入）。</summary>
        public static string PickExtensionsDir(IEnumerable<string> candidates, Func<string, bool> exists)
        {
            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                if (exists(c)) return Path.Combine(c, "extensions");
            }
            return null;
        }

        /// <summary>真实查盘。</summary>
        public static string ResolveExtensionsDir()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string appData = Environment.GetEnvironmentVariable("APPDATA");
            return PickExtensionsDir(DataDirCandidates(home, appData), Directory.Exists);
        }

        // ============================ 查状态 ============================

        public static ExtStatus Inspect()
        {
            var st = new ExtStatus { SourceDir = SourceDir(), ExtDir = ResolveExtensionsDir() };
            string why;
            if (!SourceReady(out why)) { st.State = State.NoSource; st.Detail = why; return st; }
            if (!st.HasTrae) { st.State = State.NoTrae; st.Detail = "没有找到 Trae 的数据目录（未安装或未登录过）"; return st; }

            string dst = Path.Combine(st.ExtDir, FolderName);
            bool js = SameFile(Path.Combine(SourceDir(), JsFile), Path.Combine(dst, JsFile));
            bool mf = SameFile(Path.Combine(SourceDir(), ManifestFile), Path.Combine(dst, ManifestFile));
            if (js && mf) { st.State = State.Installed; st.Detail = "已投放且与当前版本一致：" + dst; return st; }

            // ⚠ 先看**有没有别的版本**，再看这个版本在不在 —— 顺序不能反，否则升级时
            //   （手里装着 0.0.1、包里是 0.1.0）界面会显示成「未安装」，而其实她正在用着旧的那份：
            //   用户看到「未安装」就不会去点更新，功能停在旧的上面而没人知道。
            string older = null;
            try
            {
                foreach (var d in Directory.GetDirectories(st.ExtDir, PubId + "-*"))
                    if (!string.Equals(Path.GetFileName(d), FolderName, StringComparison.OrdinalIgnoreCase)) { older = d; break; }
            }
            catch { }

            bool any = File.Exists(Path.Combine(dst, JsFile)) || File.Exists(Path.Combine(dst, ManifestFile));
            if (any || older != null)
            {
                st.State = State.Outdated;
                st.Detail = older != null
                    ? "已装旧版本 " + Path.GetFileName(older) + "，当前桌宠带的是 " + Version + "；点按钮即替换"
                    : "已装但不是当前版本（" + (js ? "" : JsFile + " ") + (mf ? "" : ManifestFile + " 不一致）");
                return st;
            }
            st.State = State.NotInstalled; st.Detail = "未安装；将投放到 " + dst;
            return st;
        }

        /// <summary>给界面用的一句话（短）。</summary>
        public static string ShortLabel(ExtStatus st)
        {
            switch (st.State)
            {
                case State.Installed: return "自动续期已启用";
                case State.Outdated: return "扩展待更新";
                case State.NotInstalled: return "扩展未安装";
                case State.NoTrae: return "未发现 Trae";
                default: return "桌宠缺少扩展文件";
            }
        }

        // ============================ 投放 ============================

        /// <summary>投放步骤的**顺序**。抽成纯函数只为让判据能钉住两条不变式：
        /// ① 先拷、后校验、最后等绑定；② **整个投放过程没有任何删除动作**。
        /// ⚠⚠ ②才是关键：2026-09-30 让扩展在 Trae 里卡了一整轮的两个病因，都是「删除」引起的 ——
        ///   - 先删旧目录、紧接着拷新目录（相隔几毫秒）⇒ id 被绑到已删除的旧路径上，永不 activate；
        ///   - 拷完再删 ⇒ 建/删落进同一批 ⇒ Trae 只登记、不激活。
        ///   ⇒ 结论不是「把顺序排对」，而是**别在投放里删东西**：目录名固定（见 `FolderName`），
        ///     升级就地覆盖，于是根本没有需要删的旧目录。判据里那条 `!Contains("remove-old")`
        ///     就是替这条结论站岗的 —— 谁再把删除加回来，判据当场变红。</summary>
        public static string[] DeployPlan()
            => new[] { "copy-new", "verify", "wait-bind" };

        /// <summary>把扩展投放到 Trae。
        /// 返回 null = 成功；否则是给用户看的失败原因。</summary>
        public static string Install(out string detail)
        {
            detail = null;
            var st = Inspect();
            if (st.State == State.NoSource) return "桌宠缺少扩展文件：" + st.Detail;
            if (!st.HasTrae) return "没有找到 Trae 的扩展目录 —— 请先启动一次 Trae 并登录，然后再试。";
            if (st.State == State.Installed) { detail = st.Detail; return null; }

            try
            {
                string dst = Path.Combine(st.ExtDir, FolderName);
                bool fresh = !Directory.Exists(dst);   // 目录是这次新建的吗？只有新建才谈得上「当场加载」

                // ① 拷 / 就地覆盖 —— ⚠ 这一步**不做任何删除**（理由见 DeployPlan）
                Directory.CreateDirectory(dst);
                File.Copy(Path.Combine(SourceDir(), JsFile), Path.Combine(dst, JsFile), true);
                File.Copy(Path.Combine(SourceDir(), ManifestFile), Path.Combine(dst, ManifestFile), true);

                // ② 生成即验（本项目纪律：产出物必须当场读回来证明它是对的）
                if (!SameFile(Path.Combine(SourceDir(), JsFile), Path.Combine(dst, JsFile))
                    || !SameFile(Path.Combine(SourceDir(), ManifestFile), Path.Combine(dst, ManifestFile)))
                {
                    // ⚠ 只在**这次新建**的目录上回滚。已存在的目录里可能躺着一份能用的旧副本，
                    //   把它删掉会让用户从「旧版可用」掉到「完全没有」—— 宁可留着并如实报错。
                    if (fresh) { try { Directory.Delete(dst, true); } catch { } }
                    return "投放后校验失败（拷贝出来的文件与源不一致）" + (fresh ? "，已回滚。" : "；未动原有副本。");
                }

                // ③ 只有新建目录才等绑定 —— 覆盖升级时目录名早在注册表里，Trae 本就不会重新 activate
                bool bound = fresh && WaitBound(st.ExtDir, FolderName, 6000);

                detail = "已投放到 " + dst
                       + (fresh
                          ? (bound ? "（Trae 已当场加载）" : "（Trae 未响应，下次启动 Trae 时生效）")
                          : "（就地覆盖；下次启动 Trae 时生效）");
                return null;
            }
            catch (Exception ex)
            {
                return "投放失败：" + ex.Message;
            }
        }

        /// <summary>等 Trae 把 `extensions.json` 里的 relativeLocation 指到目标目录。
        /// ⚠ 只在 Trae 正在运行时才有意义；**超时不算失败**（首次安装时 Trae 常常没在跑，
        ///   那种情况下扩展会在下次启动 Trae 时被加载）。
        /// ⚠ 用字符串匹配而不是 JSON 解析：这里的值就是目录名、形状稳定；为一个「只想看一眼」
        ///   的动作引入解析依赖不划算。Trae 正在写这个文件时会抛 IO 异常，重试即可。</summary>
        private static bool WaitBound(string extDir, string folder, int timeoutMs)
        {
            string file = Path.Combine(extDir, "extensions.json");
            string[] needles =
            {
                "\"relativeLocation\":\"" + folder + "\"",
                "\"relativeLocation\": \"" + folder + "\"",
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        string t = File.ReadAllText(file, Encoding.UTF8);
                        foreach (var n in needles) if (t.Contains(n)) return true;
                    }
                }
                catch { }
                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        /// <summary>按需写出凭据**骨架**（只在文件不存在时创建）。
        /// 骨架 = URL + `---headers---` + `---body---`，没有 authorization —— 那一行由扩展填。
        /// 为什么由桌宠写而不是扩展写：骨架里的 URL/body 是**请求长什么样**这个事实，
        /// 属于桌宠；扩展凭空造一份就是给同一份数据加第二个口径。
        /// ⚠ 只在用户明确点「安装同步扩展」时调用：凭空多出一个 Trae 凭据文件，会让
        ///   BalanceAsync 从「回落自定义接口」改走 Trae 那一路，那是行为变化，不该静默发生。</summary>
        public static bool EnsureCredentialSkeleton(out string path, out string note)
        {
            path = Path.Combine(StatusProbe.SecretDir(), StatusProbe.TraeSecretFile);
            if (File.Exists(path)) { note = "凭据文件已存在，未改动"; return false; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string text = EntitlementUrl + "\n---headers---\n---body---";
                File.WriteAllText(path, text, new UTF8Encoding(false));
                note = "已创建凭据骨架（还差 authorization，扩展会自动填）";
                return true;
            }
            catch (Exception ex) { note = "创建凭据骨架失败：" + ex.Message; return false; }
        }

        /// <summary>骨架是否**真的能被自己的解析器读懂** —— 判据用（离线可跑）。
        /// ⚠ 这条断言的价值：骨架格式若与 ParseRawSecretText 对不上，症状是
        ///   「文件明明写出来了，桌宠却说凭据无 URL」，而两边各自的代码都自洽。</summary>
        public static string SkeletonText() => EntitlementUrl + "\n---headers---\n---body---";

        // ============================ 小工具 ============================

        /// <summary>命令行入口（`--traeextinstall`）：走**与卡片按钮完全相同**的代码路径。
        /// ⚠ 它存在的理由：按钮要人去点，而「点了会怎样」不能靠读代码推断 —— 本项目纪律是
        ///   交付前必须有针对该能力的判据。这个开关让「投放」这件事能被真跑、被复现、被 diff，
        ///   也给了终端用户一条无界面时的补救路径。
        /// ⚠ 它会往 **Trae 的目录**里写东西 ⇒ 必须是用户显式敲出来的，不许被别的流程顺带调用。</summary>
        public static int InstallRun()
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Console.WriteLine("---- 投放 Trae 令牌同步扩展 ----");
            var st = Inspect();
            Console.WriteLine("源目录        : " + st.SourceDir);
            Console.WriteLine("Trae 扩展目录 : " + (st.HasTrae ? st.ExtDir : "(没找到)"));
            Console.WriteLine("投放前状态    : " + st.State + " · " + st.Detail);

            string srcWhy;
            if (!SourceReady(out srcWhy)) { Console.WriteLine("[FAIL] 桌宠缺少扩展文件：" + srcWhy); return 1; }
            if (!st.HasTrae) { Console.WriteLine("[FAIL] 没找到 Trae 的扩展目录 —— 先启动一次 Trae 并登录，再来。"); return 1; }
            if (st.State == State.Installed) { Console.WriteLine("[OK] 已经是最新，无需重装。"); return 0; }

            string credPath, credNote;
            bool created = EnsureCredentialSkeleton(out credPath, out credNote);
            Console.WriteLine("凭据文件      : " + credPath + " —— " + credNote);

            string detail;
            string err = Install(out detail);
            Console.WriteLine(err == null ? "投放          : 成功 —— " + detail : "投放          : 失败 —— " + err);

            var after = Inspect();
            Console.WriteLine("投放后状态    : " + after.State + " · " + after.Detail);
            if (err != null || after.State != State.Installed)
            { Console.WriteLine("[FAIL] 投放后没有达到「已安装且与源逐字节一致」"); return 1; }
            Console.WriteLine(RestartHint);
            Console.WriteLine("[OK] 已投放到 " + Path.Combine(after.ExtDir, FolderName)
                              + (created ? "（并创建了凭据骨架）" : "（凭据文件本来就存在，未改动）"));
            return 0;
        }

        private static bool SameFile(string a, string b)
        {
            try
            {
                if (!File.Exists(a) || !File.Exists(b)) return false;
                return Hash(a) == Hash(b);
            }
            catch { return false; }
        }

        private static string Hash(string p)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(p))
                return Convert.ToBase64String(sha.ComputeHash(fs));
        }
    }
}
