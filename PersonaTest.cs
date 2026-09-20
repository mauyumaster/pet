// --personatest：离线验人格前缀（不联网、不读凭据、不构建界面）。
//
// 为什么必须离线也能验：**只靠真接口验的判据没有资格失败** ——
// 「她说话像不像她」是主观的，但「她被告知了什么」是能算的。能算的部分全算掉：
// 文件有没有读到、正文干不干净、注入是不是必然发生、开场白合不合规。
//
// 负对照：`--personatest --persona <一个含禁词的文件>` 必须 exit 1。
// 跑不出红，说明这条判据没在测任何东西。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzhuPet
{
    internal static class PersonaTest
    {
        // 机制词：属于「关于她的规格」，不该进她的自我认知（人格页 §7：只给存在层）。
        // 她一旦读到自己的规格，就不再是从里面活着，而是从外面看着自己。
        private static readonly string[] Mechanism =
        {
            "模型", "网关", "端口", "闸门", "判据", "token", "Token", "额度",
            "API", "api", "JSON", ".cs", ".exe", ".dll", "llm_utils", "DeepSeek", "deepseek",
            "system prompt", "prompt", "缓存", "抑制", "网线", "接口",
        };

        // 禁词：会把她变成客服的称呼（人格页 §4／§5）。
        // ⚠ 包括「不要叫用户」这种写法本身 —— 模型有把定义原样复述的稳定倾向，
        //   写进提示它就一定会说出口。所以禁词只允许存在于**判据**里，不允许存在于**提示**里。
        // ⚠ 这份表是**共用**的（--watchtest 用它验她真说出口的话）—— 共用是为了避免两份禁词表各自演化，
        //   那正是「同一份数据两个落点」。
        internal static readonly string[] Forbidden =
        {
            "主人", "老板", "用户", "亲爱的", "宝子", "本鲸",
        };

        public static int Run(Cli o)
        {
            Persona.Reload(o.PersonaPath);

            var checks = new List<object>();
            bool ok = true;

            void Check(string name, bool pass, string detail)
            {
                if (!pass) ok = false;
                checks.Add(new Dictionary<string, object>
                {
                    ["name"] = name, ["ok"] = pass, ["detail"] = detail ?? "",
                });
            }

            string text = Persona.Text ?? "";
            string greeting = Persona.Greeting ?? "";

            // ⚠ 禁词／机制词判据扫的是「persona.md 原文剥掉注释」，不是 Persona.Text。
            // 退回骨架时 Persona.Text 是骨架副本，拿它当扫描对象等于在检查副本、永远全绿
            // —— 那正是「总数变小 ＋ 全绿」的假通过。
            string scanned = text;
            string scanSource = "Persona.Text（退回骨架）";
            int rawChars = -1, afterFmChars = -1, afterCommentChars = -1;
            if (Persona.SourcePath != null)
            {
                try
                {
                    string raw = File.ReadAllText(Persona.SourcePath, Encoding.UTF8);
                    raw = raw.Replace("\r\n", "\n").Replace("\r", "\n");   // 与 Persona.Reload 同口径，否则诊断数字不可比
                    string noFm = Persona.StripFrontmatter(raw);
                    string noCm = Persona.StripComments(noFm);
                    rawChars = raw.Length;
                    afterFmChars = noFm.Length;
                    afterCommentChars = noCm.Length;
                    if (raw.Length > 0 && raw[0] == '\uFEFF')
                        Check("noBom", false, "persona.md 以 BOM 开头 —— 会被当成她的第一句话的第一个字（.NET 的 \\s 不匹配 U+FEFF）");
                    scanned = Regex.Replace(noCm, @"\n{3,}", "\n\n").Trim();
                    scanSource = "persona.md 原文（已剥 frontmatter ＋ 注释）";
                }
                catch (Exception ex) { Check("readRaw", false, "读原文失败：" + ex.Message); }
            }

            // ---- 1. 读到了没有 ----
            Check("found", Persona.SourcePath != null, Persona.SourcePath ?? "未找到 persona.md");
            Check("notFallback", !Persona.UsedFallback,
                Persona.UsedFallback ? "退回了内置骨架：" + Persona.Problem : "用的是库里的真值");

            // ---- 2. 正文 ----
            Check("length", text.Length >= 200 && text.Length <= 3000,
                text.Length + " 字（合理区间 200–3000）");

            var mech = Hits(scanned, Mechanism);
            Check("noMechanism", mech.Count == 0,
                mech.Count == 0 ? "扫描 " + scanSource + "：无机制词" : "出现机制词：" + string.Join("／", mech));

            var forb = Hits(scanned, Forbidden);
            Check("noForbidden", forb.Count == 0,
                forb.Count == 0 ? "扫描 " + scanSource + "：无禁词" : "出现禁词：" + string.Join("／", forb));

            // ---- 3. 开场白 ----
            Check("greeting", greeting.Trim().Length > 0 && greeting.Trim().Length <= 30,
                greeting.Trim().Length + " 字：「" + Clamp(greeting.Trim(), 40) + "」");
            var gForb = Hits(greeting, Forbidden);
            Check("greetingNoForbidden", gForb.Count == 0,
                gForb.Count == 0 ? "开场白无禁词" : "开场白出现禁词：" + string.Join("／", gForb));

            // ---- 4. 注入：纯函数，离线逼红 ----
            var bare = new List<object> { Persona.Message("user", "在吗") };
            var withSys = Persona.WithSystem(bare);
            string firstRole = FirstRole(withSys);
            Check("injectInserts", withSys.Count == bare.Count + 1 && firstRole == "system",
                "无 system → 条数 " + bare.Count + "→" + withSys.Count + "，首条 role=" + firstRole);
            Check("injectContent", FirstSystemText(withSys) == text,
                "注入的正文与 Persona.Text 逐字节一致：" + (FirstSystemText(withSys) == text));
            Check("injectNoMutate", bare.Count == 1 && FirstRole(bare) == "user",
                "原列表未被就地修改");

            var already = new List<object> { Persona.Message("system", "临时口吻"), Persona.Message("user", "在吗") };
            var kept = Persona.WithSystem(already);
            Check("injectNoDuplicate", kept.Count == 2 && FirstSystemText(kept) == "临时口吻",
                "已有 system → 条数保持 " + kept.Count + "，且不覆盖显式传入的那条");

            // ---- 5. 落盘 + 自验 ----
            var report = new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["sourcePath"] = Persona.SourcePath,
                ["usedFallback"] = Persona.UsedFallback,
                ["problem"] = Persona.Problem,
                ["chars"] = text.Length,
                ["rawChars"] = rawChars,
                ["afterFrontmatterChars"] = afterFmChars,
                ["afterCommentChars"] = afterCommentChars,
                ["scannedChars"] = scanned.Length,
                ["scanSource"] = scanSource,
                ["greeting"] = greeting,
                ["greetingChars"] = greeting.Trim().Length,
                ["mechanismHits"] = mech,
                ["forbiddenHits"] = forb,
                ["checks"] = checks,
            };

            string outPath = Path.Combine(Path.GetTempPath(), "azhu_personatest.json");
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(outPath, json, Encoding.UTF8);
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(outPath, Encoding.UTF8))) { }
            }
            catch (Exception ex)
            {
                ok = false;
                Console.WriteLine("写出的 JSON 自己解析不了：" + ex.Message);
            }

            Console.WriteLine("personatest " + (ok ? "OK" : "FAIL") + " —— " + checks.Count + " 项检查，报告 " + outPath);
            foreach (object c in checks)
            {
                var d = (Dictionary<string, object>)c;
                if (!(bool)d["ok"]) Console.WriteLine("  [x] " + d["name"] + "：" + d["detail"]);
            }
            // 读不到结论就不许报成功（「总数变小 ＋ 全绿」是最危险的假通过）。
            if (text.Length == 0) { Console.WriteLine("  [x] 正文为空 —— 读不到内容，判为 FAIL"); ok = false; }
            return ok ? 0 : 1;
        }

        private static List<string> Hits(string s, string[] words)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (string w in words)
                if (s.IndexOf(w, StringComparison.Ordinal) >= 0) list.Add(w);
            return list;
        }

        private static string FirstRole(IReadOnlyList<object> msgs)
        {
            var d = msgs != null && msgs.Count > 0 ? msgs[0] as IDictionary<string, object> : null;
            object r;
            return d != null && d.TryGetValue("role", out r) ? (r as string) : null;
        }

        private static string FirstSystemText(IReadOnlyList<object> msgs)
        {
            foreach (object m in msgs)
            {
                var d = m as IDictionary<string, object>;
                object role;
                if (d == null || !d.TryGetValue("role", out role) || (role as string) != "system") continue;
                object content;
                if (!d.TryGetValue("content", out content)) return null;
                var arr = content as object[];
                if (arr == null || arr.Length == 0) return null;
                var part = arr[0] as IDictionary<string, object>;
                object t;
                return part != null && part.TryGetValue("text", out t) ? (t as string) : null;
            }
            return null;
        }

        private static string Clamp(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }
    }
}
