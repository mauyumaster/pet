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
        public const string Version = "0.1.3";

        /// <summary>本次投放要用的目录名 —— **每一次都不同**（&lt;PubId&gt;-&lt;版本&gt;-&lt;UTC 时间戳&gt;）。
        /// ⚠⚠ 为什么不能固定成一个名字（2026-10-01 六次现场实验定案，推翻 2026-09-30 的判断）：
        ///   ① **Trae SOLO CN 启动时根本不加载用户扩展目录** —— 它是 `solo-lite` 构建
        ///      （`product.json`: `packageType=SOLO_CN` / `runMode=solo-lite`）。代码里
        ///      `ExtensionsScannerService.scanUserExtensions()` 与 `scanAllUserExtensions()`
        ///      第一句都是 `if (Vs(this.w)) return trace("Skipping ... in solo-lite mode"), []`，
        ///      构造函数还把缓存扫描器直接 `.disable()`。⇒ 重启 Trae 之后，
        ///      `~/.trae-cn/extensions/` 里的东西**永远不会被加载**，`extensions.json` 写得再对也没用。
        ///   ② 唯一的入口是 `LocalExtensionsWatcher`：它监听扩展目录，**只对"新增一个它没见过的目录"
        ///      作出反应**（内部有个只增不减的已见集合），当场扫描并 `addExtensionsToProfile` ⇒ 激活。
        ///      **文件改动不算、已见过的路径移出再移回也不算。**
        ///   ③ **同一个 id 在一个 Trae 会话里只会激活一次**：之后再出现（哪怕换个目录名）只登记、
        ///      不激活 —— 那是 VSCode 对"已加载扩展"的正常行为，要下一次启动才会重新加载。
        ///   ⇒ 合起来只有一句话：**要让扩展在某次 Trae 会话里跑起来，就必须在那个会话里投放一个新的目录名。**
        ///     固定名字在重启之后永远不会再触发 watcher —— 那正是 2026-09-30 把它卡死一整轮的真因
        ///     （当时误判成"同一批次里同 id 两个变更互相抵消"，其实那几次全都发生在"该 id 已激活过"的会话里）。
        ///   ⇒ 代价：扩展目录里会积累多份副本（每份约 14 KB）。**故意不清理** —— 删除只会往 Trae 的
        ///     `.obsolete` 里写字（把那个目录名标记成"已卸载"），**换不回激活**，却多一个未知状态。</summary>
        public static string FolderNameAt(DateTime utc)
            => PubId + "-" + Version + "-" + utc.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>本次投放要用的目录名（调用两次可能相同 —— 同一秒内连点两次时要自己让开，见 `Install`）。</summary>
        public static string NewFolderName() => FolderNameAt(DateTime.UtcNow);

        /// <summary>这个名字是不是「本扩展的副本」。⚠ 认它只有一个用处：`Inspect` 数自家副本、
        ///   以及判据能离线证伪。**必须把 `-diag` 这类变体排除在外**（开发期遗留的另一个 id，
        ///   它写同一份 TEMP 日志，混进来会让状态显示成"已装旧版本"而永远好不了）。</summary>
        public static bool IsOurFolderName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (string.Equals(name, PubId, StringComparison.OrdinalIgnoreCase)) return true;   // 2026-09-30 用过的固定名
            return System.Text.RegularExpressions.Regex.IsMatch(name,
                "^" + System.Text.RegularExpressions.Regex.Escape(PubId) + @"-\d+\.\d+\.\d+-[0-9A-Za-z]+$");
        }

        /// <summary>诊断日志的文件名 —— **必须与 `trae-ext/extension.js` 里的 `os.tmpdir()` 那份同名**。
        /// 它是「扩展到底有没有真的跑起来」唯一的外部证据：Trae 自己的日志只会说"登记了"，
        /// 而登记 ≠ 激活（2026-10-01 实测：`Added extensions to default profile` 出现过多次，扩展一行日志都没打）。</summary>
        public const string TmpLogName = "azhupet_trae_sync.log";

        public static string TmpLogPath() => Path.Combine(Path.GetTempPath(), TmpLogName);

        /// <summary>随包分发的扩展源码目录名（**与 pet.csproj / pack-release.cmd / azhupet.iss 三处同名**）。
        /// ⚠ 改名要四处一起改：那三处按名字打包含，本文件按名字找源。</summary>
        public const string SourceDirName = "trae-ext";
        public const string JsFile = "extension.js";
        public const string ManifestFile = "package.json";

        /// <summary>凭据骨架里的那一行 URL —— **桌宠负责「请求长什么样」的唯一落点**。
        /// ⚠ 扩展刻意不知道这个 URL：它只改 `authorization` 一行，不创建文件。
        ///   这样「格式」只有一处真值，不会出现 C# 与 JS 各写一份 URL 的局面。</summary>
        public const string EntitlementUrl = "https://api.trae.cn/trae/api/v2/pay/user_current_entitlement_list";

        /// <summary>投放到 Trae 之后，用户还要知道的一件事。
        /// ⚠ 三条都实测过（2026-10-01）：
        ///   ① Trae **正在运行**时投放 ⇒ 扩展当场加载并同步一次；
        ///   ② **重启 Trae 后它不会再回来**（Trae SOLO CN 启动时不扫用户扩展目录，见 `FolderNameAt`）；
        ///   ③ 那就再点一次本按钮 —— 每次投放都是**新目录名**，watcher 才认。
        ///   ⚠ 只有"该 id 在**当前这个** Trae 会话里已经加载过一次"这一种情形点了也没用，
        ///     得等下次 Trae 启动；这条写进 `Install` 的返回文案里，别让用户对着按钮反复点。</summary>
        public const string RestartHint = "Trae 运行中投放会当场生效；Trae 重启后它不会自己回来（Trae SOLO CN 启动时不加载用户扩展），再点一次即可。";

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

            // ⚠ 状态是**按内容**判的，不是按目录名 —— 目录名每次投放都变（见 FolderNameAt）。
            //   先找「与源逐字节一致」的那一份：只要有一份，就说明"已经装过这个版本"。
            var ours = OurFolders(st.ExtDir);
            foreach (var d in ours)
                if (SameFile(Path.Combine(SourceDir(), JsFile), Path.Combine(d, JsFile))
                    && SameFile(Path.Combine(SourceDir(), ManifestFile), Path.Combine(d, ManifestFile)))
                {
                    st.State = State.Installed;
                    st.Detail = "已投放且与当前版本一致：" + d + "（共 " + ours.Count + " 份副本）";
                    return st;
                }

            if (ours.Count > 0)
            {
                st.State = State.Outdated;
                st.Detail = "有 " + ours.Count + " 份本扩展副本，但没有一份与当前桌宠带的 " + Version
                          + " 逐字节一致（最近的是 " + Path.GetFileName(ours[0]) + "）；点按钮会再投放一份新的";
                return st;
            }
            st.State = State.NotInstalled;
            st.Detail = "未安装；点按钮会把 " + Version + " 版投放到 " + st.ExtDir;
            return st;
        }

        /// <summary>列出本扩展在扩展目录里的**所有副本**（纯查盘，按写入时间倒序，最新的在前）。
        /// ⚠ 会有多份是**设计使然**（见 `FolderNameAt`），不是垃圾：只有"新目录"才能触发 Trae 的 watcher。</summary>
        public static List<string> OurFolders(string extDir)
        {
            var list = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(extDir) || !Directory.Exists(extDir)) return list;
                foreach (var d in Directory.GetDirectories(extDir, PubId + "*"))
                    if (IsOurFolderName(Path.GetFileName(d))) list.Add(d);
            }
            catch { /* 权限/竞态：当没有 */ }
            list.Sort((a, b) =>
            {
                try { return File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)); }
                catch { return 0; }
            });
            return list;
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

        /// <summary>投放步骤的**顺序**（抽成纯函数，只为让判据能钉住不变式）：
        /// ① **先取一个新目录名**（名字必须是扩展目录里还没有的）；② 拷两件；③ 逐字节验；④ 等它**真的跑起来**。
        /// ⚠⚠ 而且**整个过程不许有任何删除动作** —— 2026-10-01 实测：删掉旧副本之后再投一个新目录，
        ///   新目录照样只登记不激活（删旧换不回任何东西），却会往 Trae 的 `.obsolete` 里写字、
        ///   把那个目录名标记成"已卸载"，白多一个未知状态。判据里那两条"删除类动词"黑名单替这条站岗。</summary>
        public static string[] DeployPlan()
            => new[] { "pick-new-name", "copy-new", "verify", "wait-activated" };

        /// <summary>把扩展投放到 Trae —— **每次调用都投一份新目录名的副本**（唯二能让 Trae 当场加载它的条件
        /// 就是"Trae 在跑" + "目录名是新的"，见 `FolderNameAt`）。所以这里**没有"已经是最新就不用装"的短路**：
        /// 那个短路在 2026-10-01 之前一直把"重启后失效"的扩展显示成"已启用"，用户点了也没反应。
        /// 返回 null = 成功；否则是给用户看的失败原因。</summary>
        public static string Install(out string detail)
        {
            detail = null;
            var st = Inspect();
            if (st.State == State.NoSource) return "桌宠缺少扩展文件：" + st.Detail;
            if (!st.HasTrae) return "没有找到 Trae 的扩展目录 —— 请先启动一次 Trae 并登录，然后再试。";

            // 投放前后各看一次诊断日志的时间戳：变新 = 扩展**真的跑起来了**（登记 ≠ 激活）
            DateTime? logBefore = LogTime();

            try
            {
                // ① 取一个新目录名 —— 「新」才有 watcher 事件。同一秒里连点两次就等一下换个戳。
                string folder = NewFolderName();
                string dst = Path.Combine(st.ExtDir, folder);
                if (Directory.Exists(dst))
                {
                    System.Threading.Thread.Sleep(1100);
                    folder = NewFolderName();
                    dst = Path.Combine(st.ExtDir, folder);
                }
                if (Directory.Exists(dst)) return "投放失败：目录名撞车（" + dst + " 已存在），请再点一次。";

                // ② 拷两件 —— ⚠ 全程**不做任何删除**（理由见 DeployPlan）
                Directory.CreateDirectory(dst);
                File.Copy(Path.Combine(SourceDir(), JsFile), Path.Combine(dst, JsFile), true);
                File.Copy(Path.Combine(SourceDir(), ManifestFile), Path.Combine(dst, ManifestFile), true);

                // ③ 生成即验（本项目纪律：产出物必须当场读回来证明它是对的）
                if (!SameFile(Path.Combine(SourceDir(), JsFile), Path.Combine(dst, JsFile))
                    || !SameFile(Path.Combine(SourceDir(), ManifestFile), Path.Combine(dst, ManifestFile)))
                {
                    // ⚠ 只回滚**本次刚建的这个空壳**：里面除了刚拷坏的文件什么都没有，删掉无害。
                    try { Directory.Delete(dst, true); } catch { }
                    return "投放后校验失败（拷贝出来的文件与源不一致），已回滚。";
                }

                // ④ 等它真的跑起来 —— 看扩展自己那份诊断日志有没有变新
                bool ran = WaitActivated(logBefore, 8000);
                detail = "已投放到 " + dst
                       + (ran
                          ? "，Trae 已当场加载并完成一次同步。"
                          : "，但 Trae 没有当场加载它。两种常见原因（都不是失败）：Trae 没在运行；"
                          + "或本扩展在**这个** Trae 会话里已经加载过一次了（同一个 id 一个会话只加载一次）—— 下次启动 Trae 后再点一次即可。");
                return null;
            }
            catch (Exception ex)
            {
                return "投放失败：" + ex.Message;
            }
        }

        /// <summary>扩展自己那份诊断日志（`%TEMP%\azhupet_trae_sync.log`）的最后写入时间；不存在 = null。
        /// ⚠ 为什么不用 Trae 的 `extensions.json` 当证据：那只说明"**登记**了"。
        ///   2026-10-01 实测到好几次 `Added extensions to default profile from external source` 写着我们，
        ///   而扩展一行日志都没打（登记 ≠ 激活）。要证明"真的跑了"，只能看它自己写的东西。</summary>
        private static DateTime? LogTime()
        {
            try
            {
                string f = TmpLogPath();
                return File.Exists(f) ? File.GetLastWriteTimeUtc(f) : (DateTime?)null;
            }
            catch { return null; }
        }

        /// <summary>等诊断日志变新。**超时不算失败**，只改 `Install` 的文案。
        /// ⚠ 判据是「比投放前更新」，不是「最近写过」—— 后者会把上一次投放留下的旧日志当成这次的成绩。</summary>
        private static bool WaitActivated(DateTime? before, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var t = LogTime();
                if (t.HasValue && (!before.HasValue || t.Value > before.Value)) return true;
                System.Threading.Thread.Sleep(250);
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

            string credPath, credNote;
            bool created = EnsureCredentialSkeleton(out credPath, out credNote);
            Console.WriteLine("凭据文件      : " + credPath + " —— " + credNote);

            // ⚠ 这里**故意没有**「已经是最新就不装」的短路：每次投放都是一份新目录名，
            //   而"投放一个新目录"正是让 Trae 当场加载它的唯一办法（见 FolderNameAt）。
            string detail;
            string err = Install(out detail);
            Console.WriteLine(err == null ? "投放          : 成功 —— " + detail : "投放          : 失败 —— " + err);
            if (err != null) return 1;

            var after = Inspect();
            Console.WriteLine("投放后状态    : " + after.State + " · " + after.Detail);
            if (after.State != State.Installed)
            { Console.WriteLine("[FAIL] 投放后没有达到「已存在与源逐字节一致的副本」"); return 1; }
            Console.WriteLine(RestartHint);
            Console.WriteLine("[OK] " + (created ? "已创建凭据骨架；" : "凭据文件本来就存在，未改动；") + "本次副本见上方路径。");
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
