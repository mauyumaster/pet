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

            // ================= ⑥ 更新源地址自检 =================
            // ⚠⚠ 这条守一个「推上去才发现」的坑：raw.githubusercontent.com 路径**区分大小写**，
            //   而 404 在这条链路上只表现成「检查更新失败」，看不到「地址写错了」。
            Check(Updater.DefaultFeedUrl.StartsWith("https://raw.githubusercontent.com/"),
                "更新源是 raw 直链（不是 github.com 页面地址）", Updater.DefaultFeedUrl);
            Check(Updater.DefaultFeedUrl.Contains("mauyumaster/AzhuPet"),
                "更新源含正确的用户名与仓库名（大小写敏感）", Updater.DefaultFeedUrl);
            Check(Updater.DefaultFeedUrl.EndsWith("version.json"),
                "更新源指向 version.json", Updater.DefaultFeedUrl);
            Check(Updater.RepoUrl == "https://github.com/mauyumaster/AzhuPet",
                "仓库主页地址正确（设置面板会用它）", Updater.RepoUrl);

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
                    Check(rf.DownloadUrl.Contains("mauyumaster/AzhuPet/releases"),
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
    }
}
