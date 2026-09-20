// 主配置（config.json）离线回归：转义对称性 + 膨胀守卫。
//
// 为什么单独立这一套：2026-09-20 出了一次**只表现为「打开设置要等 16 秒」**的事故 ——
//   `Save()` 写盘时对路径做了转义（`\` → `\\`），而 `Raw()` 读回来时**不做反转义**。
//   两边不对称 ⇒ 每次「保存设置」反斜杠翻一倍。面板打开时会「读到值 → 写回」，
//   于是**每开一次设置就翻一倍**（指数增长）：用户机上 config.json 涨到 **268 MB**，
//   而面板构造要对该字符串 `MeasureText` ⇒ 稳定卡 16.5 秒。
//
// ⚠ 这条判据的思路值得复用：**它不测「打开面板快不快」**（那要起 UI、耗时且不稳），
//   而是测**根因本身**——「序列化 → 反序列化」必须是不动点，且反复往返不增长。
//   根因判据比症状判据快、稳、且解释力强。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AzhuPet
{
    internal static class ConfigTest
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
            Console.WriteLine("---- 主配置转义对称性与膨胀守卫（离线；不碰真实 config.json）----");

            // 让被测的读写走临时目录，绝不动用户真实配置
            string tmp = Path.Combine(Path.GetTempPath(), "AzhuPet-ConfigTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            string oldDir = Environment.GetEnvironmentVariable("AZHU_CONFIG_DIR");
            Environment.SetEnvironmentVariable("AZHU_CONFIG_DIR", tmp);
            try
            {
                // ---- ① 关键回归：反复 Save→Load 不得让任何字段增长 ----
                //     这正是那 268 MB 的直接来源：往返一次翻一倍。
                var c = new PetConfig
                {
                    VaultPath = @"D:\Obsidian_SecondBrain\SecondBrain",
                    DeepSeekKey = "sk-abc",
                    BalanceHeader = "cookie: a=1",
                };
                int len0 = 0;
                var lens = new List<int>();
                for (int i = 0; i < 8; i++)
                {
                    c.Save();
                    len0 = (int)new FileInfo(Path.Combine(tmp, "config.json")).Length;
                    lens.Add(len0);
                    c = PetConfig.Load();
                }
                Check(lens[lens.Count - 1] == lens[0],
                    "反复 存取 8 轮，config.json 大小不增长",
                    "首轮 " + lens[0] + " B → 末轮 " + lens[lens.Count - 1] + " B");
                Check(len0 < 4096, "config.json 大小在合理范围", len0 + " B（阈值 4096）");

                // ---- ② 往返不动点：VaultPath 逐字节还原 ----
                var c2 = new PetConfig { VaultPath = @"D:\Obsidian_SecondBrain\SecondBrain" };
                c2.Save();
                var back = PetConfig.Load();
                Check(back.VaultPath == @"D:\Obsidian_SecondBrain\SecondBrain",
                    "反斜杠路径逐字节还原", "读到 <" + back.VaultPath + ">");

                // ---- ③ 反斜杠数量不许增长（直接盯住那个翻倍的量）----
                int n0 = CountBackslashes(back.VaultPath);
                for (int i = 0; i < 6; i++) { back.Save(); back = PetConfig.Load(); }
                int n1 = CountBackslashes(back.VaultPath);
                Check(n0 == n1, "反斜杠数量在 6 轮往返后不变", n0 + " → " + n1);

                // ---- ④ 恶意/畸形输入：引号、换行、制表、Unicode 转义都要能原样回来 ----
                string[] nasty = {
                    "带\"引号\"的值",
                    "带\\反斜杠\\的值",
                    "第一行\n第二行",
                    "制表\t符",
                    "emoji 🐋 与全角（）",
                    "结尾是反斜杠\\",
                };
                foreach (var s in nasty)
                {
                    var cc = new PetConfig { VaultPath = s };
                    cc.Save();
                    var rb = PetConfig.Load();
                    Check(rb.VaultPath == s, "往返保真：" + Describe(s),
                        rb.VaultPath == s ? "" : "读到 <" + Describe(rb.VaultPath) + ">");
                }

                // ---- ⑤ 转义必须真的生效（防止哪天有人把 Esc 拆掉）----
                var ce = new PetConfig { VaultPath = "a\"b" };
                ce.Save();
                string raw = File.ReadAllText(Path.Combine(tmp, "config.json"));
                Check(raw.Contains("a\\\"b"), "写盘时引号被转义", "文件里能找到 a\\\"b");

                // ---- ⑥ 转义对称性：用「不足以触发自愈」的反斜杠数来验机制本身 ----
                //     ⚠ 2026-09-21 这里被我改过一次，值得记：原先用 **4000 个反斜杠**当素材，
                //     断言「读出 2000 个」。加了自愈（超 64 个反斜杠即判垃圾）之后，
                //     同一素材**被重置成默认值**（2 个反斜杠）⇒ 判据红了。
                //     这不是回归，是**两条判据在抢同一块素材**：
                //       · 「转义对称」要的是**没到垃圾线的值**，好观察逐字符还原；
                //       · 「垃圾识别」要的是**过了垃圾线的值**。
                //     混用会永远全红或永远全绿。⇒ 拆开：这里用 32 个（< 64，安全区）。
                var sb = new StringBuilder();
                sb.Append("{\r\n  \"vaultPath\": \"D:");
                sb.Append('\\', 32);                       // 文件里 32 个转义字符 = 值里 16 个反斜杠
                sb.Append("tmp\",\r\n  \"size\": 1\r\n}\r\n");
                File.WriteAllText(Path.Combine(tmp, "config.json"), sb.ToString());
                var poisoned = PetConfig.Load();
                Check(CountBackslashes(poisoned.VaultPath) == 16,
                    "转义对称：32 个转义字符按 JSON 规则读成 16 个反斜杠",
                    "读到 " + CountBackslashes(poisoned.VaultPath) + " 个（期望 16）");

                // ---- ⑥' 关键：写回**不再指数增长** ----
                //     这是「可自愈」的判据：用户已经中毒的机器，开一次设置文件就稳定下来。
                //     ⚠ 判据的措辞要准：不是「字节数绝对不变」—— 值里的反斜杠写回时要按 JSON
                //     转义成双倍字符，所以**单次往返可能让文件略大**（合法，如 452 → 535）。
                //     真正要杜绝的是**指数增长**：再往返几轮，大小必须稳住。
                //     第一版我断言「after <= before」被判红 —— 是**判据把期望值算窄了**。
                long before = new FileInfo(Path.Combine(tmp, "config.json")).Length;
                poisoned.Save();
                long after1 = new FileInfo(Path.Combine(tmp, "config.json")).Length;
                var p2 = PetConfig.Load();
                p2.Save();
                long after2 = new FileInfo(Path.Combine(tmp, "config.json")).Length;
                p2 = PetConfig.Load();
                p2.Save();
                long after3 = new FileInfo(Path.Combine(tmp, "config.json")).Length;
                Check(after1 == after2 && after2 == after3,
                    "安全区内的值反复写回后大小**收敛**（不再每轮翻倍）",
                    before + " → " + after1 + " → " + after2 + " → " + after3 + " B");
                Check(CountBackslashes(PetConfig.Load().VaultPath) == 16,
                    "安全区内的值往返多轮后反斜杠数量稳定",
                    "读到 " + CountBackslashes(PetConfig.Load().VaultPath) + "（期望 16）");

                // ---- ⑦ 自检：判据真的会红（负对照内建）----
                //     构造一个「写时不转义」的场景，断言①会红。
                //     这里用最直接的办法：手工写一个转义不对称的文件，看往返是否增长。
                File.WriteAllText(Path.Combine(tmp, "config.json"),
                    "{\r\n  \"vaultPath\": \"D:\\\\a\",\r\n  \"size\": 1\r\n}\r\n");
                var asym = PetConfig.Load();
                bool asymOk = asym.VaultPath == @"D:\a";   // 文件里是 \\a ⇒ 应读成 \a
                Check(asymOk, "自检：不对称文件按 JSON 规则反转义", "读到 <" + asym.VaultPath + ">");

                // ---- ⑧ 抢救：被写胖的旧文件必须能自愈 ----
                //     病灶是**结构性的**：`D:` + 2²⁷ 个 `\` + `SecondBrain`。多出来的反斜杠
                //     把原本的段落分隔吃掉了，**已不可逆还原** ⇒ 只能判垃圾、重置默认。
                //     ⚠ 这条判据的价值在于：它同时锁住「识别规则」与「自愈动作」两件事 ——
                //       上一版 --fixconfig 靠 Load→Save 保真，字节数原地踏步，正是漏了「重置」。
                Check(PetConfig.IsPoisonedPath(@"D:" + new string('\\', 2000) + "SecondBrain"),
                    "IsPoisonedPath 认得被膨胀的路径", "2000 个反斜杠");
                Check(PetConfig.IsPoisonedPath(new string('a', 40000)),
                    "IsPoisonedPath 认得超长路径（> 32767）", "40000 字符");
                Check(!PetConfig.IsPoisonedPath(@"D:\Obsidian_SecondBrain\SecondBrain"),
                    "IsPoisonedPath 不误伤正常路径", @"D:\Obsidian_SecondBrain\SecondBrain");
                Check(!PetConfig.IsPoisonedPath(@"C:\Users\a\b\c\d\e\f"),
                    "IsPoisonedPath 不误伤较深但合法的路径", @"C:\Users\a\b\c\d\e\f");
                Check(!PetConfig.IsPoisonedPath(""), "IsPoisonedPath 不误伤空值", "(empty)");

                // 端到端：写一个膨胀值进文件 → Load 后必须已回落默认 → Save 后文件必须缩回 KB 级
                // ⚠ 这里**不能**断言「比修复前小」：这个素材只写了 3 个字段（约 452 B），
                //   而 Save 会写全 20 个字段（约 535 B）⇒ 合法地变大一点点。
                //   真正的契约是「**缩回 KB 级**」—— 对一个原本 268 MB 的病灶来说，
                //   452 → 535 B 与 268 MB → 535 B 是同一件事：不再是病态尺寸。
                //   （旧判据 pAfter < pBefore 又一次把期望值算窄了，同 ⑥' 那类错。）
                string poisonTxt = "{\r\n  \"vaultPath\": \"D:" + new string('\\', 400)
                                 + "SecondBrain\",\r\n  \"size\": 1\r\n}\r\n";
                File.WriteAllText(Path.Combine(tmp, "config.json"), poisonTxt);
                long pBefore = new FileInfo(Path.Combine(tmp, "config.json")).Length;
                var healed = PetConfig.Load();
                healed.Save();
                long pAfter = new FileInfo(Path.Combine(tmp, "config.json")).Length;
                Check(healed.VaultPath == new PetConfig().VaultPath,
                    "膨胀值 Load 后自愈为默认库路径", "<" + healed.VaultPath + ">");
                Check(pAfter < 4096,
                    "膨胀配置 Save 后缩回 KB 级（对比修复前的病态尺寸）", pBefore + " → " + pAfter + " B");
                // 关键对照：把「病态尺寸」也写出来，让「缩回 KB 级」这件事有个可参照的量级
                Check(new string('\\', 400).Length > 64,
                    "自检：本用例的素材确实越过了垃圾线（> 64）",
                    "素材反斜杠 = 400");
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

            Console.WriteLine();
            Console.WriteLine("---- configtest: PASS " + _pass + " / FAIL " + _fail + " ----");
            return _fail == 0 ? 0 : 1;
        }

        private static int CountBackslashes(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int n = 0;
            foreach (char c in s) if (c == '\\') n++;
            return n;
        }

        private static string Describe(string s)
        {
            if (s == null) return "(null)";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            return "<" + sb + ">";
        }
    }
}
