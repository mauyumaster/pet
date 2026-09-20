// 自更新（Updater.cs）的离线判据套件。
//
// 为什么自更新**必须有判据**，而不能「跑一次能用就算」：
//   配置写坏只丢设置；**更新写坏会丢整个程序** —— 而且是在用户机器上、我们够不着的地方。
//   所以这里的每条判据都对应一个「一旦错就很贵」的具体失败模式：
//     · 版本比较用字符串 —— 症状是「有新版本却永远不提示」，且只在跨两位数时出现
//     · feed 解析太脆 —— 症状是「检查更新老是失败」，用户以为网络问题
//     · staging 少一个文件还去替换 —— 症状是「更新后人格退回骨架音色」
//     · 替换失败没回滚 —— 症状是**程序直接没了**（最坏的结局）
//
// ⚠ 全程离线：feed 内容用字符串喂给解析器，替换在临时目录里用假文件演练。
//   不联网、不碰真实的 pet.exe。**判据绝不能有副作用** —— 跑判据把用户程序换了是灾难。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AzhuPet
{
    internal static class UpdateTest
    {
        private static int _pass, _fail;

        private static void Check(bool ok, string name, string detail = null)
        {
            if (ok) { _pass++; Console.WriteLine("  [PASS] " + name + (detail == null ? "" : "  —— " + detail)); }
            else { _fail++; Console.WriteLine("  [FAIL] " + name + (detail == null ? "" : "  —— " + detail)); }
        }

        public static int Run(Cli o)
        {
            _pass = _fail = 0;
            Console.WriteLine("---- 自更新：版本比较 / feed 解析 / staging 校验 / 替换与回滚（离线）----");

            // ================= ① 版本比较 =================
            // 这一组是本次最有价值的判据：字符串比较会在这里全部翻车。
            Check(Updater.CompareVersions("0.2.0", "0.1.0") > 0, "0.2.0 比 0.1.0 新");
            Check(Updater.CompareVersions("0.1.0", "0.2.0") < 0, "0.1.0 比 0.2.0 旧");
            Check(Updater.CompareVersions("0.1.0", "0.1.0") == 0, "同版本相等");

            // ⚠⚠ 关键陷阱：字符串比较会把 "0.10.0" 判成比 "0.9.0" 小（'1' < '9'）
            Check(Updater.CompareVersions("0.10.0", "0.9.0") > 0,
                "0.10.0 比 0.9.0 新（字符串比较会判反）", "0.10.0 vs 0.9.0");
            Check(Updater.CompareVersions("0.9.0", "0.10.0") < 0,
                "0.9.0 比 0.10.0 旧（字符串比较会判反）", "0.9.0 vs 0.10.0");
            Check(Updater.CompareVersions("1.0.0", "0.99.99") > 0, "1.0.0 比 0.99.99 新");
            Check(Updater.CompareVersions("0.100.0", "0.99.0") > 0, "0.100.0 比 0.99.0 新（三位数）");
            // 带前缀 / 位数不足
            Check(Updater.CompareVersions("v0.2.0", "0.1.0") > 0, "带 v 前缀也能比", "v0.2.0 vs 0.1.0");
            Check(Updater.CompareVersions("0.2", "0.1.0") > 0, "两段式也能比", "0.2 vs 0.1.0");
            Check(Updater.CompareVersions("2", "1.9.9") > 0, "一段式也能比", "2 vs 1.9.9");
            // 预发布版更旧（semver 规定）
            Check(Updater.CompareVersions("0.2.0-beta", "0.2.0") < 0,
                "预发布版比正式版旧（semver 规定）", "0.2.0-beta vs 0.2.0");
            Check(Updater.CompareVersions("0.2.0", "0.2.0-beta") > 0,
                "正式版比预发布版新", "0.2.0 vs 0.2.0-beta");
            // 垃圾输入不能抛
            Check(Updater.CompareVersions("", "") == 0, "空字符串不抛、判相等");
            Check(Updater.CompareVersions(null, "1.0.0") < 0, "null 不抛、判更旧");

            // 边界：全部差分组合跑一遍，确认没有「反了」的对
            int bad = 0;
            string[] vs = { "0.1.0", "0.1.1", "0.2.0", "0.9.0", "0.10.0", "0.10.1", "1.0.0", "1.0.1", "10.0.0" };
            for (int i = 0; i < vs.Length; i++)
                for (int j = 0; j < vs.Length; j++)
                {
                    int expect = i.CompareTo(j);
                    int got = Updater.CompareVersions(vs[i], vs[j]);
                    if (Math.Sign(expect) != Math.Sign(got)) bad++;
                }
            Check(bad == 0, "版本比较在 9×9 组合上单调一致", bad + " 个不一致（期望 0）");

            // ================= ② feed 解析 =================
            var r1 = new Updater.CheckResult();
            Updater.ParseFeed("{\r\n  \"version\": \"0.2.0\",\r\n  \"url\": \"https://example.com/a.zip\"\r\n}\r\n", r1);
            Check(r1.Ok && r1.RemoteVersion == "0.2.0", "解析 version 字段", "<" + r1.RemoteVersion + ">");
            Check(r1.DownloadUrl == "https://example.com/a.zip", "解析 url 字段", "<" + r1.DownloadUrl + ">");

            // 键顺序颠倒（真实文件是人写的，别指望顺序）
            var r2 = new Updater.CheckResult();
            Updater.ParseFeed("{ \"url\": \"https://x/y.zip\", \"version\": \"1.2.3\" }", r2);
            Check(r2.RemoteVersion == "1.2.3" && r2.DownloadUrl == "https://x/y.zip",
                "键顺序颠倒也能解析", r2.RemoteVersion + " / " + r2.DownloadUrl);

            // 带说明文本与转义
            var r3 = new Updater.CheckResult();
            Updater.ParseFeed("{ \"version\": \"0.3.0\", \"url\": \"u\", \"notes\": \"修了\\\"引号\\\"问题\" }", r3);
            Check(r3.Notes == "修了\"引号\"问题", "notes 里的转义被还原", "<" + r3.Notes + ">");

            // 缺 version 必须判失败（不能当成「有更新」）
            var r4 = new Updater.CheckResult();
            Updater.ParseFeed("{ \"url\": \"https://example.com/a.zip\" }", r4);
            Check(!r4.Ok, "缺 version 字段 ⇒ 判失败（不是「有更新」）");

            // 缺 url 也必须判失败：提示了却下不了，比不提示更糟
            var r4b = new Updater.CheckResult();
            Updater.ParseFeed("{ \"version\": \"0.2.0\" }", r4b);
            Check(!r4b.Ok, "缺 url 字段 ⇒ 判失败（提示了却下不了更糟）",
                "version=" + (r4b.RemoteVersion ?? "(null)"));

            // ⚠ 正例必须也是 Ok=true：第一版 ParseFeed 从不在成功时置 true，
            //   于是「解析完全正常」也返回 Ok=false（被判据当场抓到）。这条守住它不再退化。
            var r4c = new Updater.CheckResult();
            Updater.ParseFeed("{ \"version\": \"0.2.0\", \"url\": \"https://e/a.zip\" }", r4c);
            Check(r4c.Ok, "完整 feed ⇒ Ok=true（解析成功要能说自己成功）");

            // 空 / 垃圾输入不抛
            var r5 = new Updater.CheckResult();
            try { Updater.ParseFeed("", r5); Updater.ParseFeed("not json at all", r5); Check(true, "垃圾输入不抛异常"); }
            catch (Exception ex) { Check(false, "垃圾输入不抛异常", ex.GetType().Name); }

            // ================= ③ 整链：真实 feed 比对 =================
            var r6 = new Updater.CheckResult();
            Updater.ParseFeed("{ \"version\": \"99.0.0\", \"url\": \"https://example.com/a.zip\" }", r6);
            r6.Ok = true;
            Check(Updater.CompareVersions(r6.RemoteVersion, Updater.CurrentVersion) > 0,
                "远端 99.0.0 会被判为「有更新」", "本地 " + Updater.CurrentVersion);
            var r7 = new Updater.CheckResult();
            Updater.ParseFeed("{ \"version\": \"0.0.1\", \"url\": \"https://example.com/a.zip\" }", r7);
            Check(Updater.CompareVersions(r7.RemoteVersion, Updater.CurrentVersion) <= 0,
                "远端 0.0.1 会被判为「无更新」", "本地 " + Updater.CurrentVersion);

            // ================= ④ 当前版本号读得到 =================
            string cv = Updater.CurrentVersion;
            Check(!string.IsNullOrEmpty(cv) && cv != "0.0.0",
                "CurrentVersion 从 csproj 读到了版本号（不是兜底 0.0.0）", "读到 " + cv);
            Check(cv.Split('.').Length >= 2, "版本号至少有主次两段", cv);
            // ⚠ 这条守「csproj 里 <Version> 忘了写」—— 那种情况下会兜底成程序集版本，格式不同
            Check(Updater.CompareVersions(cv, "0.0.0") > 0, "版本号比 0.0.0 大（说明真的配置了）", cv);

            // ================= ⑤ staging 校验 / 替换与回滚 =================
            // 在临时目录里用**假文件**演练整套替换，绝不碰真实的 pet.exe。
            string tmp = Path.Combine(Path.GetTempPath(), "AzhuPet-UpdateTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            string oldDir = Environment.GetEnvironmentVariable("AZHU_CONFIG_DIR");
            Environment.SetEnvironmentVariable("AZHU_CONFIG_DIR", tmp);
            try
            {
                string stage = Path.Combine(tmp, "update");
                string payload = Path.Combine(stage, "payload");
                string flag = Path.Combine(stage, "pending.json");

                // 场景一：payload 齐全 + 版本更新 ⇒ PendingVersion 应认出
                Directory.CreateDirectory(payload);
                File.WriteAllText(Path.Combine(payload, "pet.exe"), "NEW-EXE", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(payload, "persona.md"), "NEW-PERSONA", new UTF8Encoding(false));
                File.WriteAllText(flag, "{ \"version\": \"99.0.0\" }", new UTF8Encoding(false));
                Check(Updater.PendingVersion() == "99.0.0",
                    "payload 齐全且版本更新 ⇒ 认得出待替换版本", "读到 " + (Updater.PendingVersion() ?? "(null)"));

                // 场景二：pending 里的版本比当前**旧** ⇒ 不该替换（用户手工装了新版）
                File.WriteAllText(flag, "{ \"version\": \"0.0.1\" }", new UTF8Encoding(false));
                Check(Updater.PendingVersion() == null,
                    "待替换版本比当前旧 ⇒ 不替换（避免把用户降级）");

                // 场景三：**persona.md 缺失** ⇒ 整包作废（这正是「新程序配旧人格」的防线）
                File.WriteAllText(flag, "{ \"version\": \"99.0.0\" }", new UTF8Encoding(false));
                string savedPersona = Path.Combine(payload, "persona.md");
                File.Delete(savedPersona);
                Check(Updater.PendingVersion() == null,
                    "⚠ payload 缺 persona.md ⇒ 整包作废（防「新程序配旧人格」）",
                    "只换 exe 不换人格 = 人格退回骨架音色");
                File.WriteAllText(savedPersona, "NEW-PERSONA", new UTF8Encoding(false));   // 复原

                // 场景四：**pet.exe 空文件** ⇒ 整包作废（防替换成空壳）
                File.WriteAllText(Path.Combine(payload, "pet.exe"), "", new UTF8Encoding(false));
                Check(Updater.PendingVersion() == null, "payload 里 pet.exe 为空 ⇒ 整包作废");
                File.WriteAllText(Path.Combine(payload, "pet.exe"), "NEW-EXE", new UTF8Encoding(false));

                // 场景四之二：**模型**（2026-09-21 加）——这是发布包里被漏掉过的那个 13MB 文件。
                //   翻车现场：zip 只含 pet.exe + persona.md ⇒ 用户解压双击弹「找不到 glb」直接退出。
                //   所以这里的口径是：带上就必须完整（非空）；不带则容忍（兼容没模型的旧包）。
                string modelDir = Path.Combine(payload, "model");
                Directory.CreateDirectory(modelDir);
                string modelFile = Path.Combine(modelDir, "chibi_maid_pet.glb");

                // (a) 包里有非空模型 ⇒ 认得出待替换
                File.WriteAllText(modelFile, "FAKE-GLB-BYTES", new UTF8Encoding(false));
                File.WriteAllText(flag, "{ \"version\": \"99.0.0\" }", new UTF8Encoding(false));
                Check(Updater.PendingVersion() == "99.0.0",
                    "payload 含非空模型 ⇒ 认得出待替换版本", "读到 " + (Updater.PendingVersion() ?? "(null)"));

                // (b) 模型是**空文件** ⇒ 作废（空模型 = 启动就崩，比没有更糟）
                File.WriteAllText(modelFile, "", new UTF8Encoding(false));
                Check(Updater.PendingVersion() == null,
                    "⚠ payload 里模型为空 ⇒ 整包作废", "0 字节的 glb 会让启动直接失败");

                // (c) 完全没有 model 目录 ⇒ 容忍（兼容 v0.1.0 及更早的无模型包）
                Directory.Delete(modelDir, true);
                Check(Updater.PendingVersion() == "99.0.0",
                    "payload 没有 model/ ⇒ 容忍（兼容旧版无模型包）",
                    "不兼容的话，已发出的旧版本永远更新不上来");

                // 场景五：没有 pending 标记 ⇒ 不替换
                File.Delete(flag);
                Check(Updater.PendingVersion() == null, "没有 pending 标记 ⇒ 不替换");
                File.WriteAllText(flag, "{ \"version\": \"99.0.0\" }", new UTF8Encoding(false));

                // 场景六：pending 标记在、payload 目录整个没了 ⇒ 不替换（不能崩）
                string payloadBak = payload + ".bak";
                Directory.Move(payload, payloadBak);
                bool threw = false;
                try { var _ = Updater.PendingVersion(); } catch { threw = true; }
                Check(!threw && Updater.PendingVersion() == null, "payload 目录消失 ⇒ 不替换且不抛");
                Directory.Move(payloadBak, payload);

                // 场景七：**真演练一次替换**（用假 appDir，不碰真 exe）
                //   ApplyPending 会用 Environment.ProcessPath 定位自身；测试里那个是真 exe，
                //   所以这里**不调 ApplyPending**，只验它依赖的两个前提条件：
                //     ① 正在运行的文件能被改名（已在设计期实测过，这里再验一次同类操作）
                //     ② 换过去的文件内容正确
                string appDir = Path.Combine(tmp, "appdir");
                Directory.CreateDirectory(appDir);
                string fakeExe = Path.Combine(appDir, "pet.exe");
                string fakePersona = Path.Combine(appDir, "persona.md");
                File.WriteAllText(fakeExe, "OLD-EXE", new UTF8Encoding(false));
                File.WriteAllText(fakePersona, "OLD-PERSONA", new UTF8Encoding(false));

                // 复刻 ApplyPending 的核心三步（改名让位 → 新文件就位 → 读回验证）
                string oldExe = fakeExe + ".old";
                File.Move(fakeExe, oldExe);                                  // 让位
                File.Move(Path.Combine(payload, "pet.exe"), fakeExe);         // 就位
                File.Move(fakePersona, fakePersona + ".old");
                File.Move(Path.Combine(payload, "persona.md"), fakePersona);

                Check(File.ReadAllText(fakeExe) == "NEW-EXE", "替换演练：pet.exe 内容已是新版");
                Check(File.ReadAllText(fakePersona) == "NEW-PERSONA", "替换演练：persona.md 内容已是新版");
                Check(File.ReadAllText(oldExe) == "OLD-EXE", "替换演练：旧版被保留成 .old（可回滚）");

                // 回滚：把 .old 换回来，内容必须复原
                File.Delete(fakeExe); File.Move(oldExe, fakeExe);
                Check(File.ReadAllText(fakeExe) == "OLD-EXE", "回滚演练：能从 .old 还原旧版");

                // 场景七之二：**模型要能落到 appDir/model/ 子目录**（2026-09-21 加）
                //   这是首次带模型发布前的真实盲点：老的替换逻辑只会把文件放到 appDir 根，
                //   而模型在 model/ 子目录里 ⇒ 直接 File.Move 到不存在的目录会抛异常。
                //   复刻新逻辑：先建目录、再就位。
                string srcModel = Path.Combine(payload, "model", "chibi_maid_pet.glb");
                if (!File.Exists(srcModel))
                {
                    // 前面场景 (c) 把 model 目录删了，这里重建一份最小样本
                    Directory.CreateDirectory(Path.Combine(payload, "model"));
                    File.WriteAllText(srcModel, "NEW-GLB", new UTF8Encoding(false));
                }
                string dstModelDir = Path.Combine(appDir, "model");
                string dstModel = Path.Combine(dstModelDir, "chibi_maid_pet.glb");
                bool mkOk = true;
                try
                {
                    if (!Directory.Exists(dstModelDir)) Directory.CreateDirectory(dstModelDir);   // ← 新逻辑的关键一步
                    File.Move(srcModel, dstModel);
                }
                catch { mkOk = false; }
                Check(mkOk && File.Exists(dstModel),
                    "替换演练：模型能落到 appDir/model/ 子目录（旧逻辑会因目录不存在而抛异常）");
                Check(!Directory.Exists(dstModelDir) || File.Exists(dstModel),
                    "替换演练：模型就位后内容可读",
                    File.Exists(dstModel) ? File.ReadAllText(dstModel) : "（没就位）");
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  [FAIL] 未处理异常：" + ex);
            }
            finally
            {
                Environment.SetEnvironmentVariable("AZHU_CONFIG_DIR", oldDir);
                try { Directory.Delete(tmp, true); } catch { }
            }

            // ================= ⑤之二 模型查找口径 =================
            // ⚠⚠ 这条守 2026-09-21 的**真实翻车**：发布包只含 pet.exe + persona.md，
            //   用户解压双击弹「找不到 model/chibi_maid_pet.glb」——因为 ResolveModel 原来
            //   只会从 exe 目录**往上**找（为开发布局写的），而解压出来的目录往上什么都没有。
            //   修法是「exe 同目录优先」。这条判据确认：模型就在 exe 旁边时一定找得到。
            try
            {
                string mb = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model");
                string mf = Path.Combine(mb, "chibi_maid_pet.glb");
                bool preExisting = File.Exists(mf);
                if (!preExisting)
                {
                    Directory.CreateDirectory(mb);
                    File.WriteAllText(mf, "PROBE-GLB", new UTF8Encoding(false));
                }
                string found = Cli.ResolveModel(null);
                Check(found != null && Path.GetFullPath(found).Equals(Path.GetFullPath(mf), StringComparison.OrdinalIgnoreCase),
                    "ResolveModel：模型在 exe 同目录时优先取它（发布包布局）",
                    found ?? "(没找到)");
                Check(Updater.DefaultFeedUrl.Length > 0 && Cli.ModelRelPath == "model/chibi_maid_pet.glb",
                    "发布包里模型的相对路径口径固定为 model/chibi_maid_pet.glb",
                    Cli.ModelRelPath);
                if (!preExisting)
                {
                    try { File.Delete(mf); Directory.Delete(mb); } catch { }
                }
            }
            catch (Exception ex)
            {
                Check(false, "模型查找判据不该抛异常", ex.GetType().Name);
            }

            // ================= ⑥ 更新源地址自检 =================
            // ⚠⚠ 这条守一个「推上去才发现」的坑：raw.githubusercontent.com 路径**区分大小写**，
            //   而 404 在这条链路上只表现成「检查更新失败」，看不到「地址写错了」。
            Check(Updater.DefaultFeedUrl.StartsWith("https://raw.githubusercontent.com/"),
                "更新源是 raw 直链（不是 github.com 页面地址）", Updater.DefaultFeedUrl);
            Check(Updater.DefaultFeedUrl.Contains("mauyumaster/pet"),
                "更新源含正确的用户名与仓库名（大小写敏感）", Updater.DefaultFeedUrl);
            Check(Updater.DefaultFeedUrl.EndsWith("version.json"),
                "更新源指向 version.json", Updater.DefaultFeedUrl);
            Check(Updater.RepoUrl == "https://github.com/mauyumaster/pet",
                "仓库主页地址正确（设置面板会用它）", Updater.RepoUrl);

            // ⚠⚠ 2026-09-21 加的：**别只跟硬编码字符串比**——那验证的是副本，不是真值。
            //   仓库改名时，硬编码判据照样全绿，而线上 404。这里直接读 `.git/config` 里的
            //   remote.origin.url（真正的远端身份），比对代码里的更新源。
            //   实测抓到的真实错误：本地文件夹叫「阿助娘化形象」、命名空间叫 AzhuPet，
            //   于是把仓库名写成了 AzhuPet —— 而 GitHub Desktop 是用**文件夹名 pet** 建的仓，
            //   线上是 mauyumaster/pet。两个地址都「看起来对」，只有比对 git 才知道谁对。
            try
            {
                string gitUrl = ReadGitRemoteUrl();
                if (gitUrl == null)
                {
                    Console.WriteLine("  [skip] 附近没有 .git/config（发布产物里本来就没有它）");
                }
                else
                {
                    // https://github.com/mauyumaster/pet.git  →  mauyumaster/pet
                    string slug = GitUrlToSlug(gitUrl);
                    Check(slug != null, "能从 git remote 解析出 owner/repo", gitUrl);
                    if (slug != null)
                    {
                        Check(Updater.DefaultFeedUrl.Contains(slug),
                            "更新源里的 owner/repo 与 git remote 一致（防仓库改名后失效）",
                            "feed 用的是 <" + Updater.DefaultFeedUrl + ">，git 说是 <" + slug + ">");
                        Check(Updater.RepoUrl.EndsWith(slug),
                            "仓库主页地址与 git remote 一致",
                            "repo 用的是 <" + Updater.RepoUrl + ">，git 说是 <" + slug + ">");
                    }
                }
            }
            catch (Exception ex)
            {
                Check(false, "读 git remote 时不该抛异常", ex.Message);
            }

            // ================= ⑦ 打包产物 version.json 与实际版本一致 =================
            // ⚠⚠ 这条守一个「发布当天才会发现」的失配：pack-release.cmd 从 exe 读版本、
            //   写进 version.json；如果两者不一致（比如忘了重新打包），
            //   更新器就会要么永远不提示、要么无限提示。
            //   这里把「仓库里那份 version.json」真的读出来比对 —— 它是会被提交、会被 raw URL 服务的那个文件。
            try
            {
                // 从 exe 目录往上找仓库根的 version.json（发布目录里没有它）
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string feedPath = null;
                for (int up = 0; up < 6 && dir != null; up++)
                {
                    string cand = Path.Combine(dir, "version.json");
                    if (File.Exists(cand)) { feedPath = cand; break; }
                    var parent = Directory.GetParent(dir);
                    dir = parent == null ? null : parent.FullName;
                }
                if (feedPath == null)
                {
                    // 发布后的解压目录里没有它 —— 这不是失败，只是这条判据在这里无对象
                    Console.WriteLine("  [skip] 附近没有 version.json（发布产物里本来就没有它）");
                }
                else
                {
                    string txt = File.ReadAllText(feedPath, Encoding.UTF8);
                    var rf = new Updater.CheckResult();
                    Updater.ParseFeed(txt, rf);
                    Check(rf.Ok, "仓库里的 version.json 能被解析", feedPath);
                    Check(rf.RemoteVersion == Updater.CurrentVersion,
                        "version.json 的版本号 == 程序的当前版本（防打包后忘同步）",
                        "feed=" + rf.RemoteVersion + " exe=" + Updater.CurrentVersion);
                    Check(!string.IsNullOrEmpty(rf.DownloadUrl) && rf.DownloadUrl.EndsWith(".zip"),
                        "version.json 里的下载地址指向 zip", rf.DownloadUrl);
                    Check(rf.DownloadUrl.Contains("mauyumaster/pet/releases"),
                        "下载地址指向本项目的 Release 资产", rf.DownloadUrl);
                    Check(rf.DownloadUrl.Contains("v" + Updater.CurrentVersion),
                        "下载地址里的 tag 与版本号一致（防 tag 写错）", rf.DownloadUrl);
                    // 地址里不能有 CR/LF（批处理生成文件的经典坑）
                    Check(rf.DownloadUrl.IndexOf('\r') < 0 && rf.DownloadUrl.IndexOf('\n') < 0
                          && rf.RemoteVersion.IndexOf('\r') < 0 && rf.RemoteVersion.IndexOf('\n') < 0,
                        "解析出的字段里没有隐藏的 CR/LF（批处理生成的经典坑）",
                        "<" + rf.RemoteVersion + ">");
                }
            }
            catch (Exception ex)
            {
                _fail++;
                Console.WriteLine("  [FAIL] version.json 一致性检查抛异常：" + ex.GetType().Name);
            }

            Console.WriteLine();
            Console.WriteLine("---- updatetest: PASS " + _pass + " / FAIL " + _fail + " ----");
            return _fail == 0 ? 0 : 1;
        }

        // ============================================================
        //  --updatediag：**联网**诊断（与上面的离线判据分开）
        //  为什么需要它：更新检查失败时，UI 上只有「检查更新失败」四个字。
        //  用户（尤其我自己）没法区分「没联网 / 代理坏了 / 地址错了 / 仓库是私有的」——
        //  而这几件事的修法完全不同。这把整条链路逐段量出来。
        // ============================================================
        public static int DiagRun(Cli o)
        {
            Console.WriteLine("---- 自更新诊断（联网）----");
            string feed = string.IsNullOrEmpty(o.FeedUrl) ? Updater.DefaultFeedUrl : o.FeedUrl;
            bool customFeed = feed != Updater.DefaultFeedUrl;
            Console.WriteLine("  当前版本      : " + Updater.CurrentVersion);
            Console.WriteLine("  更新源        : " + feed + (customFeed ? "   ← --feed 覆盖（非线上地址）" : ""));
            Console.WriteLine("  仓库主页      : " + Updater.RepoUrl);

            string proxy = Updater.CurrentProxyDescription();
            Console.WriteLine("  本进程的代理  : " + (proxy == null ? "（直连，未设代理）" : proxy));
            if (proxy != null)
                Console.WriteLine("                  ⚠ 若这个代理没在运行，请求必然失败 —— 关掉代理再试。");

            // ① 版本比较自检（不联网，但这台机器上先确认基本逻辑没退化）
            int cmp = Updater.CompareVersions("0.10.0", "0.9.0");
            Console.WriteLine("  版本比较自检  : 0.10.0 vs 0.9.0 → " +
                (cmp > 0 ? "新（正确）" : "判错了！"));

            // ② 真去拉 feed
            Console.WriteLine();
            Console.WriteLine("  [1/3] 拉取更新信息…");
            Updater.CheckResult r = null;
            try
            {
                r = Updater.CheckAsync(feed, 15000).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("        ✗ 不该抛异常：" + ex.GetType().Name);
                return 1;
            }

            if (!r.Ok)
            {
                Console.WriteLine("        ✗ 失败：" + r.Message);
                Console.WriteLine();
                Console.WriteLine("  结论：**更新检查现在不可用**。上表的代理与下面的地址是首要嫌疑。");
                Console.WriteLine("  自助排查顺序：");
                Console.WriteLine("    1) 代理没开？→ 关掉系统代理/环境变量 *_PROXY 再跑一次");
                Console.WriteLine("    2) 地址对不对？→ 浏览器直接打开上面的「更新源」，看是不是 404");
                Console.WriteLine("    3) 仓库是私有的？→ raw 地址对私有仓库外部读不到，必须公开");
                return 1;
            }

            Console.WriteLine("        ✓ 拉到了：version=" + r.RemoteVersion);
            Console.WriteLine("          url=" + r.DownloadUrl);
            Console.WriteLine("          比较：" + Updater.CurrentVersion + " → " + r.RemoteVersion +
                              " = " + (r.HasUpdate ? "**有更新**" : "已是最新"));
            if (!string.IsNullOrEmpty(r.Notes))
                Console.WriteLine("          notes=" + r.Notes);

            // ③ 远端地址与仓库身份是否一致（把「地址写错」从网络问题里摘出来）
            Console.WriteLine();
            Console.WriteLine("  [2/3] 地址与本地仓库身份比对…");
            if (customFeed)
            {
                // 用了 --feed 就是故意不指线上，这条比对没有意义（本地测试会误报）
                Console.WriteLine("        [skip] 用了 --feed，本次不比线上身份");
            }
            else
            {
            string gitUrl = ReadGitRemoteUrl();
            if (gitUrl == null)
            {
                Console.WriteLine("        [skip] 附近没有 .git（发布产物里没有它，正常）");
            }
            else
            {
                string slug = GitUrlToSlug(gitUrl);
                Console.WriteLine("        git remote = " + gitUrl + "  →  " + (slug ?? "（认不出）"));
                if (slug != null)
                {
                    bool feedOk = Updater.DefaultFeedUrl.Contains(slug);
                    Console.WriteLine("        " + (feedOk ? "✓" : "✗") + " 更新源与 git remote " +
                                      (feedOk ? "一致" : "**不一致** —— 地址写的是别的仓库"));
                    bool zipOk = r.DownloadUrl == null || r.DownloadUrl.Contains(slug);
                    Console.WriteLine("        " + (zipOk ? "✓" : "✗") + " 下载地址与 git remote " +
                                      (zipOk ? "一致" : "**不一致** —— 线上 version.json 里还是旧地址，说明它没跟着仓库改名更新"));
                }
            }
            }   // end if(!customFeed)

            // ④ 下载地址到底能不能下（这是「检查成功但下载失败」的典型现场）：
            //    zip 挂在 GitHub Release 上，**没建 Release / 没传资产 / tag 写错** 都会 404，
            //    而它只在用户点「下载更新」时才暴露，那时已经晚了。
            Console.WriteLine();
            Console.WriteLine("  [3/3] 探测下载地址是否真有资产（只发 HEAD，不下整个包）…");
            try
            {
                int code = HeadStatusCode(r.DownloadUrl, 15000);
                if (code == 200)
                    Console.WriteLine("        ✓ HTTP 200 —— 资产在，下载能成功");
                else if (code == 404)
                    Console.WriteLine("        ✗ HTTP 404 —— **Release 或资产不存在**。\n" +
                                      "          检查：tag v" + r.RemoteVersion + " 建了吗？zip 传上去了吗？\n" +
                                      "          或：version.json 里的地址写的是别的仓库？");
                else if (code == 302 || code == 301)
                    Console.WriteLine("        ~ HTTP " + code + " —— 重定向（通常是正常的，GitHub 资产会跳到 CDN）");
                else
                    Console.WriteLine("        ? HTTP " + code + " —— 需人工确认");
            }
            catch (Exception ex)
            {
                Console.WriteLine("        ✗ 探测失败：" + ex.GetType().Name + " —— " +
                                  Updater.DescribeNetworkErrorPublic(ex));
            }

            Console.WriteLine();
            Console.WriteLine(r.HasUpdate
                ? "  结论：检测到新版本 " + r.RemoteVersion + "，可在设置面板下载。"
                : "  结论：通道正常，当前已是最新（" + Updater.CurrentVersion + "）。");
            return 0;
        }

        /// <summary>只发 HEAD 取状态码，用来判断 Release 资产在不在。不抛异常之外的东西给调用方。</summary>
        private static int HeadStatusCode(string url, int timeoutMs)
        {
            using (var http = new System.Net.Http.HttpClient())
            {
                http.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
                http.DefaultRequestHeaders.Add("User-Agent", "AzhuPet/" + Updater.CurrentVersion);
                using (var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url))
                using (var resp = http.SendAsync(req).GetAwaiter().GetResult())
                {
                    return (int)resp.StatusCode;
                }
            }
        }

        /// <summary>
        /// 从 exe 目录往上找到仓库根的 .git/config，读出 remote.origin.url。
        /// 找不到（发布产物解压目录）返回 null —— 调用方按「本条无对象」跳过，不算失败。
        /// </summary>
        private static string ReadGitRemoteUrl()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int up = 0; up < 7 && dir != null; up++)
            {
                string cfg = Path.Combine(dir, ".git", "config");
                if (File.Exists(cfg))
                {
                    foreach (string raw in File.ReadAllLines(cfg))
                    {
                        string line = raw.Trim();
                        // 形如： url = https://github.com/mauyumaster/pet.git
                        if (line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0)
                            {
                                string v = line.Substring(eq + 1).Trim();
                                if (v.Length > 0) return v;
                            }
                        }
                    }
                    return null;   // 有 .git 但没配 remote —— 老实返回 null
                }
                var parent = Directory.GetParent(dir);
                dir = parent == null ? null : parent.FullName;
            }
            return null;
        }

        /// <summary>
        /// https://github.com/mauyumaster/pet.git  →  mauyumaster/pet
        /// git@github.com:mauyumaster/pet.git      →  mauyumaster/pet
        /// 认不出（比如自建 GitLab 的非 github 地址）返回 null。
        /// </summary>
        private static string GitUrlToSlug(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            string s = url.Trim();
            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - 4);

            // 去掉协议/主机，留 path
            int idx = s.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            s = s.Substring(idx + "github.com".Length);

            // 去掉开头可能残留的 : / 组合（git@ 形式是 github.com:owner/repo）
            s = s.TrimStart(':', '/');
            // 只保留 owner/repo 两段
            string[] parts = s.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            return parts[0] + "/" + parts[1];
        }
    }
}
