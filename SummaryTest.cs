// --summarytest：离线验小时总结的四块 —— 到点判定（Due）／聚合（Aggregate）／
// 落盘内容（BuildSection／AppendToFile）／负对照。不联网、不读真记忆、不碰真库。
//
// 负对照：`--summarytest --no-summary` —— 模拟 PetWindow 的触发条件 `SummaryOn && Due(...)`：
//   ① 先证「Due 单独为 true」（目标行为真的会发生）② 再证「开关关掉后组合为 false」。
//   顺序反了就是那种「exit 1 但目标判据一次都没跑」的假验证（纪律页第 25 条）。
//
// --summary-now（NowRun）：真调一次模型，总结「现在往前 60 分钟」，落真库 —— 验收用，不是判据。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AzhuPet
{
    internal static class SummaryTest
    {
        public static int Run(Cli o)
        {
            bool negative = o.NoSummary;
            var checks = new List<object>();
            bool ok = true;
            void Check(string name, bool pass, string detail)
            {
                if (!pass) ok = false;
                checks.Add(new Dictionary<string, object> { ["name"] = name, ["ok"] = pass, ["detail"] = detail ?? "" });
            }

            // ================= 负对照：只跑那一条 =================
            if (negative)
            {
                var now = new DateTime(2026, 9, 20, 14, 0, 0);
                var last = now.AddMinutes(-60);
                bool dueAlone = HourlySummary.Due(now, last, 60);                 // ① 目标行为真的会发生
                bool combined = false && dueAlone;                                // ② SummaryOn=false 的组合（离线把关死的开关硬编码）
                Check("negControl_summary", dueAlone && !combined,
                    "Due 单独=" + dueAlone + "，关掉 SummaryOn 后组合=" + combined
                    + "（期望：Due 为 true 且组合为 false —— 两步都成立才算这条负对照有效）");
                return Report(ok, checks, "summarytest_neg");
            }

            // ================= A. 到点判定 =================
            var t0 = new DateTime(2026, 9, 20, 14, 0, 0);
            Check("dueWhenNeverSummarized", HourlySummary.Due(t0, DateTime.MinValue, 60),
                "从没总结过（lastAt=MinValue）→ 期望 true（启动后第一次总是要做的）");
            Check("notDueWithinInterval", !HourlySummary.Due(t0, t0.AddMinutes(-59.9), 60),
                "距上次 59.9 分钟 → 期望 false");
            Check("dueAtInterval", HourlySummary.Due(t0, t0.AddMinutes(-60), 60),
                "满 60 分钟 → 期望 true（开机后每满一小时，不对齐整点）");

            // ================= B. 聚合 =================
            // 合成行序列（见 HourlySummary.cs 文件头的三类特殊行）。⚠ proc 用**映射表内的真实进程名**——
            //   用 A/B/C 这类合成名的话 Human() 落回原名，「prompt 用人话名」那条判据永远分不清
            //   是翻译了还是没翻译（判别力归零）。窗口外那行不在统计里，名字随意。
            //   行 5 带 "title"：数据带毒，输出（prompt／section）必须干净 —— promptHasNoTitle 靠它有判别力。
            //   08:00 窗口外（忽略）／09:10 msedge（首行，丢归属）／09:30 Code（msedge +1200s）
            //   09:50 manual notepad（跳过，不推进指针）／09:55 WINWORD（Code +600s）／09:58 roast WINWORD（不归属）
            var rows = new List<Dictionary<string, JsonElement>>
            {
                Row("{\"at\":\"2026-09-20 08:00:00\",\"proc\":\"Z\",\"prevDwell\":999}"),
                Row("{\"at\":\"2026-09-20 09:10:00\",\"proc\":\"msedge\",\"prevDwell\":100}"),
                Row("{\"at\":\"2026-09-20 09:30:00\",\"proc\":\"Code\",\"prevDwell\":1200,\"title\":\"绝密合同草稿v3\"}"),
                Row("{\"at\":\"2026-09-20 09:50:00\",\"proc\":\"notepad\",\"prevDwell\":0,\"manual\":true}"),
                Row("{\"at\":\"2026-09-20 09:55:00\",\"proc\":\"WINWORD\",\"prevDwell\":600}"),
                Row("{\"at\":\"2026-09-20 09:58:00\",\"proc\":\"WINWORD\",\"prevDwell\":1800,\"reason\":\"roast-title\"}"),
            };
            var from = new DateTime(2026, 9, 20, 9, 0, 0);
            var to = new DateTime(2026, 9, 20, 10, 0, 0);
            var st = HourlySummary.Aggregate(rows, from, to);
            Check("aggregateSwitchCount", st.Switches == 2,
                "切换次数=" + st.Switches + "（期望 2：A→B、B→C；manual 与 roast 行不算切换）");
            Check("aggregateDwellAttribution",
                st.SecByProc.TryGetValue("msedge", out var aSec) && aSec == 1200
                && st.SecByProc.TryGetValue("Code", out var bSec) && bSec == 600
                && !st.SecByProc.ContainsKey("WINWORD"),
                "msedge=" + (st.SecByProc.TryGetValue("msedge", out var a2) ? a2 : -1) + "s（期望 1200）、"
                + "Code=" + (st.SecByProc.TryGetValue("Code", out var b2) ? b2 : -1) + "s（期望 600）、"
                + "WINWORD " + (st.SecByProc.ContainsKey("WINWORD") ? "被错误计入（roast 行的 1800s 不得归属）" : "未计入（正确）"));
            Check("aggregateIgnoresOutsideWindow", st.Lines == 5,
                "窗口内行数=" + st.Lines + "（期望 5：08:00 那行在窗口外）");

            // ================= C. prompt 与落盘内容 =================
            string prompt = HourlySummary.BuildPrompt(st, from, to);
            Check("promptCarriesSwitches", prompt.IndexOf("切换应用 2 次", StringComparison.Ordinal) >= 0,
                "prompt 里找得到「切换应用 2 次」");
            Check("promptHidesRawProc", prompt.IndexOf("浏览器", StringComparison.Ordinal) >= 0
                && prompt.IndexOf("msedge", StringComparison.Ordinal) < 0,
                "prompt 里出现的是「浏览器」（Human(msedge)）而不是进程名 msedge —— "
                + "合成行特意用映射表内的真实进程名，否则这条永远分不清翻译没翻译");
            // ⚠ 不能断言「不含『标题』二字」——BuildPrompt 的提示语自己写了「不要标题」，会撞词（假红）。
            //   改为：合成行里塞了一个假标题（绝密合同草稿v3），断言 prompt 不含它。
            //   判别力：若未来有人把 title 采进 Stats／打进 prompt，这条立刻红 —— 数据带毒，输出必须干净。
            Check("promptHasNoTitle", prompt.IndexOf("绝密合同草稿", StringComparison.Ordinal) < 0,
                "合成数据带着窗口标题「绝密合同草稿v3」，prompt 必须不含它 —— "
                + "总结链路的素材只有「应用 → 时长」，标题根本进不了 Stats（隐私判据）");

            string secOk = HourlySummary.BuildSection("这一小时他主要在浏览器里查资料，中间切去编辑器写了几笔。", null, st, from, to);
            Check("sectionCarriesSummary", secOk.IndexOf("## 09:00–10:00", StringComparison.Ordinal) == 0 + secOk.IndexOf("##", StringComparison.Ordinal)
                && secOk.IndexOf("这一小时", StringComparison.Ordinal) > 0,
                "成功节带「## 09:00–10:00」标题与总结正文");
            Check("sectionCarriesDataFootnote", secOk.IndexOf("切换 2 次", StringComparison.Ordinal) > 0,
                "成功节带数据脚注（切换 N 次＋时长 top 列表）");
            string secFail = HourlySummary.BuildSection(null, "模型没回：超时", st, from, to);
            Check("failureIsReadable", secFail.IndexOf("生成失败", StringComparison.Ordinal) > 0
                && secFail.IndexOf("模型没回：超时", StringComparison.Ordinal) > 0,
                "失败节必须留可读原因 —— 静默空节会让你分不清「她没写」和「她写了但丢了」");

            // ================= D. 落盘 =================
            string tmp = Path.Combine(Path.GetTempPath(), "azhu_summarytest_" + Guid.NewGuid().ToString("N") + ".md");
            var day = new DateTime(2026, 9, 20, 0, 0, 0);
            string e1 = HourlySummary.AppendToFile(tmp, HourlySummary.BuildSection("第一条。", null, st, from, to), day);
            string e2 = HourlySummary.AppendToFile(tmp, HourlySummary.BuildSection("第二条。", null, st, from, to), day);
            string head = e1 == null && e2 == null ? File.ReadAllText(tmp, Encoding.UTF8) : null;
            Check("fileCreatedWithHeader", head != null
                && head.IndexOf("# 她写的 · 2026-09-20", StringComparison.Ordinal) >= 0,
                "新文件带「# 她写的 · 日期」头部");
            Check("fileAppends", head != null && head.IndexOf("第一条。", StringComparison.Ordinal) > 0
                && head.IndexOf("第二条。", StringComparison.Ordinal) > head.IndexOf("第一条。", StringComparison.Ordinal),
                "第二节追加在第一节之后（追加不是覆盖）");
            try { File.Delete(tmp); } catch { }

            Report(ok, checks, null);
            return ok ? 0 : 1;
        }

        /// <summary>--summary-now：真调一次模型（不是判据）。读真 memory.jsonl，总结「现在往前 60 分钟」。</summary>
        public static int NowRun(Cli o)
        {
            var to = DateTime.Now;
            var from = to.AddMinutes(-60);
            var rows = Memory.ReadAll(out int bad, out string err);
            Console.WriteLine("记忆文件：" + Memory.FilePath() + "（坏行 " + bad + (err == null ? "" : "；" + err) + "）");
            Console.WriteLine("总结窗口：" + from.ToString("HH:mm") + " – " + to.ToString("HH:mm"));
            string result = HourlySummary.RunAsync(PetConfig.Load().VaultPath, rows, from, to).GetAwaiter().GetResult();
            if (result != null) { Console.WriteLine("FAIL —— " + result); return 1; }
            string path = HourlySummary.FilePath(PetConfig.Load().VaultPath, to);
            Console.WriteLine("OK —— 已写入 " + path);
            Console.WriteLine(File.ReadAllText(path, Encoding.UTF8));
            return 0;
        }

        private static Dictionary<string, JsonElement> Row(string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var d = new Dictionary<string, JsonElement>();
                foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
                return d;
            }
        }

        private static int Report(bool ok, List<object> checks, string tag)
        {
            var report = new Dictionary<string, object> { ["ok"] = ok, ["checks"] = checks };
            string outPath = Path.Combine(Path.GetTempPath(),
                "azhu_summarytest" + (tag == null ? "" : "_" + tag) + ".json");
            try
            {
                File.WriteAllText(outPath,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
            }
            catch (Exception ex) { ok = false; Console.WriteLine("写报告失败：" + ex.Message); }
            Console.WriteLine("summarytest " + (ok ? "OK" : "FAIL") + " —— " + checks.Count + " 项检查，报告 " + outPath);
            foreach (object c in checks)
            {
                var d = (Dictionary<string, object>)c;
                if (!(bool)d["ok"]) Console.WriteLine("  [x] " + d["name"] + "：" + d["detail"]);
            }
            return ok ? 0 : 1;
        }
    }
}
