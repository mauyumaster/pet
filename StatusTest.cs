// --statustest：无头跑一遍「状态探针」（网络 / VPN / Trae 积分 / WorkBuddy 积分），
// 打印文本 ＋ 可选写 JSON，供脚本 diff / 断言。
//
// 为什么需要它：积分只有双击气泡才看得到，而「人眼看一眼觉得对」不算判据 ——
// 本项目纪律是「交付前必须有针对该能力的测试」（MEMORY.md 判据纪律 13）。
// 有了它，「积分到底读没读到、读成多少」就从一个目视动作变成一个可以被 diff 的文件。
using System;
using System.IO;
using System.Text;

namespace AzhuPet
{
    internal static class StatusTest
    {
        public static int Run(Cli o)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }   // 否则 ✅/❌ 与中文在 cmd 里全是乱码
            var cfg = PetConfig.Load();
            var probe = new StatusProbe
            {
                ApiKey = cfg.DeepSeekKey,
                BalanceUrl = cfg.BalanceUrl,
                BalanceToken = cfg.BalanceToken,
                BalanceHeader = cfg.BalanceHeader,
                BalanceKey = cfg.BalanceKey,
                BalanceUnit = cfg.BalanceUnit,
                CustomSources = BalanceSources.Load(),
            };

            StatusReport rep = null;
            Exception err = null;
            try { rep = probe.CheckAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { err = ex; }
            if (rep == null) rep = new StatusReport();

            Console.WriteLine("---- 状态探针（无头）----");
            Console.WriteLine(StatusProbe.Format(rep));
            if (err != null) Console.WriteLine("探针异常：" + err.Message);

            var slots = StatusProbe.SecretSlots();
            Console.WriteLine("---- 凭据文件（只报路径与有无，不报密文）----");
            foreach (var (path, exists) in slots)
                Console.WriteLine((exists ? "  [有] " : "  [无] ") + path);

            Console.WriteLine("读到任一份额/积分：" + ((rep.TraeOk || rep.WorkbuddyOk || rep.BalanceOk) ? "是" : "否"));
            Console.WriteLine("Trae 可用=" + Show(rep.TraeAvailable)
                + "  WorkBuddy 可用=" + Show(rep.WorkbuddyRemain)
                + "  剩余 token=" + rep.RemainingTokens);
            Console.WriteLine("WorkBuddy 口径：主口径 type=" + StatusProbe.MainCreditType + " 命中 "
                + rep.WorkbuddyType1Count + " 条 / 返回 " + rep.WorkbuddyAccountCount + " 条（TotalCount="
                + (rep.WorkbuddyTotalCount < 0 ? "?" : rep.WorkbuddyTotalCount.ToString()) + "）");
            Console.WriteLine("  主口径余=" + Show(rep.WorkbuddyRemain)
                + "；非主口径余=" + Show(rep.WorkbuddyOtherRemain)
                + "；全桶合计(接口 TotalDosage，非消耗量)=" + Show(rep.WorkbuddyAllBucketsRemain));
            Console.WriteLine("  （主口径已与 WorkBuddy 界面同刻对照：2026-09-18 界面 1056 / 接口 1055）");

            // 负对照：**配了凭据却读不到** = 真失败，必须 exit 1（否则这个测试没有资格变红）
            int rc = 0;

            // ⚠ 口径漂移哨兵：主口径 + 非主口径 应当恒等于接口的全桶合计。
            //   一旦不等 ⇒ 要么接口加了新桶、要么 TotalDosage 换了语义 —— 两种情况都会让「主口径」这个
            //   结论失效，而它正是气泡上那个数字的唯一来源。阈值取绝对值 0.5（避开浮点噪声，
            //   本项目判据纪律 3：判据要带绝对阈值）。
            if (rep.WorkbuddyOk
                && StatusProbe.CaliberDrift(rep.WorkbuddyRemain, rep.WorkbuddyOtherRemain, rep.WorkbuddyAllBucketsRemain))
            {
                Console.WriteLine("[FAIL] 口径漂移：主口径 " + Show(rep.WorkbuddyRemain) + " + 非主口径 "
                    + Show(rep.WorkbuddyOtherRemain) + " ≠ 全桶合计 " + Show(rep.WorkbuddyAllBucketsRemain)
                    + " ⇒ type 分桶或字段语义已变，须重新对照 WorkBuddy 界面" );
                rc = 1;
            }
            if (slots[0].exists && !rep.TraeOk) { Console.WriteLine("[FAIL] Trae 凭据在，但积分没读到：" + (rep.TraeError ?? "(无原因)")); rc = 1; }
            if (slots[1].exists && !rep.WorkbuddyOk) { Console.WriteLine("[FAIL] WorkBuddy 凭据在，但积分没读到：" + (rep.WorkbuddyError ?? "(无原因)")); rc = 1; }
            foreach (var cell in rep.DynamicRows)
                if (!cell.Ok) { Console.WriteLine("[FAIL] " + cell.Name + " 配置已启用，但余额没读到：" + cell.Error); rc = 1; }

            // ⚠ 凭据「落在哪儿」本身就是判据：工程目录和 bin 都在坚果云同步范围内。
            //   上一轮的事故正是 bin 里躺着一份 balance_secret.txt —— 它让 Trae 看起来完全正常，
            //   把「凭据位置已经乱了」这件事整个盖住。这里把它变成一条会红的断言。
            string wantDir = Path.GetFullPath(StatusProbe.SecretDir()).TrimEnd('\\');
            for (int i = 0; i < slots.Length; i++)
            {
                if (!slots[i].exists) continue;
                string got = Path.GetDirectoryName(Path.GetFullPath(slots[i].path));
                if (!string.Equals(got, wantDir, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[FAIL] 凭据不在首选目录（可能在云同步范围内）：" + slots[i].path
                        + " —— 该放 " + wantDir);
                    rc = 1;
                }
            }

            // ⚠ 凭据「只配到一半」= 本次事故的准确形状：bin 里躺着一份 balance_secret.txt 而没有
            //   workbuddy_secret.txt ⇒ Trae 一切正常、WorkBuddy 那一行凭空消失，看上去像「积分接口坏了」。
            //   把「两路都在」变成一条会红的断言，就不必再指望有人恰好发现「少了一行」。
            //   （只用一路凭据的安装：设 AZHU_ALLOW_SINGLE_SECRET=1 可把这条降级成提示。）
            if (slots[0].exists != slots[1].exists)
            {
                string half = "凭据只配到一半（" + (slots[0].exists ? "Trae 在" : "Trae 缺")
                    + "、" + (slots[1].exists ? "WorkBuddy 在" : "WorkBuddy 缺")
                    + "）⇒ 缺的那一路积分会整行不显示";
                if (Environment.GetEnvironmentVariable("AZHU_ALLOW_SINGLE_SECRET") == "1")
                    Console.WriteLine("[提示] " + half + "（已按 AZHU_ALLOW_SINGLE_SECRET=1 放行）");
                else { Console.WriteLine("[FAIL] " + half); rc = 1; }
            }

            // ⚠「总数变小 ＋ 全绿」是本项目最危险的假通过（判据纪律 7）：
            //   接口自称 TotalCount，只回来一部分的话求和就是错的，而每一行看上去都正常。
            if (rep.WorkbuddyOk && rep.WorkbuddyTotalCount >= 0
                && rep.WorkbuddyTotalCount != rep.WorkbuddyAccountCount)
            {
                Console.WriteLine("[FAIL] WorkBuddy 只返回 " + rep.WorkbuddyAccountCount + " 条，接口自称 TotalCount="
                    + rep.WorkbuddyTotalCount + " ⇒ 求和不完整");
                rc = 1;
            }

            if (!string.IsNullOrEmpty(o.OutFile))
            {
                File.WriteAllText(o.OutFile, StatusProbe.ToJson(rep, err), new UTF8Encoding(false));
                Console.WriteLine("已写出：" + o.OutFile);
                // 生成即验：本项目两次产出过非法 JSON ⇒ 写完必须真的解析一遍
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(o.OutFile)))
                        Console.WriteLine("JSON 校验：通过（根节点 " + doc.RootElement.ValueKind + "）");
                }
                catch (Exception e2)
                {
                    Console.WriteLine("JSON 校验：失败 " + e2.Message);
                    return 1;
                }
            }
            return rc;
        }

        private static string Show(double v) =>
            double.IsNaN(v) ? "(未取到)" : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }
}
