// 余额配置离线回归测试：只写系统临时目录，不联网、不碰真实配置和凭据。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AzhuPet
{
    internal static class BalanceConfigTest
    {
        public static int Run()
        {
            int pass = 0, fail = 0;
            string old = Environment.GetEnvironmentVariable("AZHU_SECRET_DIR");
            string dir = Path.Combine(Path.GetTempPath(), "AzhuPet-BalanceConfigTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AZHU_SECRET_DIR", dir);
            try
            {
                var source = new BalanceSource { Name = "示例余额", Url = "https://example.com/balance", Method = "GET", PathExpr = "data.remain", Unit = "积分", Enabled = true };
                Check(BalanceSources.Validate(source).Count == 0, "合法配置通过校验", ref pass, ref fail);
                BalanceSources.Save(new List<BalanceSource> { source }, BalanceSources.ConfigPath());
                Check(File.Exists(BalanceSources.ConfigPath()), "首次保存产生 balances.json", ref pass, ref fail);

                List<BalanceSource> loaded; string error;
                Check(BalanceSources.TryLoad(out loaded, out error) && loaded.Count == 1 && loaded[0].Name == "示例余额", "保存后可无损读回", ref pass, ref fail);

                source.Name = "修改后";
                BalanceSources.Save(new List<BalanceSource> { source }, BalanceSources.ConfigPath());
                Check(File.Exists(BalanceSources.ConfigPath() + ".bak"), "覆盖保存前生成 .bak", ref pass, ref fail);
                string backup = File.ReadAllText(BalanceSources.ConfigPath() + ".bak");
                using (var doc = JsonDocument.Parse(backup))
                    Check(doc.RootElement[0].GetProperty("Name").GetString() == "示例余额", "备份保留上一个版本", ref pass, ref fail);

                File.WriteAllText(BalanceSources.ConfigPath(), "{ broken json");
                Check(!BalanceSources.TryLoad(out loaded, out error) && !string.IsNullOrEmpty(error), "坏 JSON 显式报错", ref pass, ref fail);
                Check(File.ReadAllText(BalanceSources.ConfigPath()) == "{ broken json", "加载失败不覆盖原文件", ref pass, ref fail);

                BalanceSources.SaveSecret("custom-test.secret.txt", "authorization: test-only");
                Check(File.Exists(BalanceSources.ResolveSecret("custom-test.secret.txt")), "凭据只写首选本机目录", ref pass, ref fail);
                Check(BalanceSources.HasSensitiveInlineHeaders(new BalanceSource { HeadersText = "cookie: should-move" }), "识别误放在普通配置中的敏感头", ref pass, ref fail);
                Check(!BalanceSources.HasSensitiveInlineHeaders(new BalanceSource { HeadersText = "accept: application/json" }), "普通请求头不误报", ref pass, ref fail);

                // ---- HTTP 失败文案：401 必须能自证「为什么」----
                // ⚠ 2026-09-25：此前所有失败分支都只输出状态码（气泡上就是光秃秃的「401」），
                //   于是「凭据过期」「缺请求头」「网关拦截」长得一模一样 —— 区分它们的唯一线索
                //   在响应体里，而它当场被丢掉了。文案已抽成纯函数 StatusProbe.DescribeHttpFailure，
                //   这里不联网就能把这四条钉住（含「无响应体」「超长」「含换行」三种退化形状）。
                Check(StatusProbe.DescribeHttpFailure(401, @" {""code"":1001,""msg"":""unauthorized""}").Contains("unauthorized"),
                    "401 文案带出响应体线索（可区分过期/拦截）", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(401, null) == "401",
                    "无响应体时退化为纯状态码（不出现空括号）", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(403, "a\r\nb").IndexOf('\n') < 0,
                    "响应体换行被压平（气泡只显示一行）", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(500, new string('x', 5000)).Length < 200,
                    "超长响应体被截断，不灌爆气泡", ref pass, ref fail);
                // 负对照（判据纪律 7：先证明它能失败）：旧行为只回状态码 ⇒ 带响应体时**必须**不再退化为纯「401」。
                Check(StatusProbe.DescribeHttpFailure(401, @" {""msg"":""unauthorized""}") != "401",
                    "负对照：带响应体时不再退化为纯「401」（旧行为在此判红）", ref pass, ref fail);

                var report = new StatusReport();
                StatusProbe.ApplyCustomResults(report, new Action<StatusReport>[]
                {
                    r => r.DynamicRows.Add(new BalanceCell { Name = "随想余额", Ok = true, Value = 8.57 })
                });
                Check(report.DynamicRows.Count == 1 && report.DynamicRows[0].Name == "随想余额" && report.DynamicRows[0].Ok,
                    "自定义余额异步结果写入气泡报告", ref pass, ref fail);
                string reportJson = StatusProbe.ToJson(report, null);
                using (var doc = JsonDocument.Parse(reportJson))
                    Check(doc.RootElement.GetProperty("dynamic_balances")[0].GetProperty("name").GetString() == "随想余额",
                        "无头报告包含动态余额", ref pass, ref fail);
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine("[FAIL] 未处理异常：" + ex.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable("AZHU_SECRET_DIR", old);
                try
                {
                    string full = Path.GetFullPath(dir);
                    if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(full).StartsWith("AzhuPet-BalanceConfigTest-", StringComparison.Ordinal))
                        Directory.Delete(full, true);
                }
                catch { }
            }
            Console.WriteLine("余额配置测试：PASS " + pass + " / FAIL " + fail);
            return fail == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string name, ref int pass, ref int fail)
        {
            if (ok) { pass++; Console.WriteLine("[PASS] " + name); }
            else { fail++; Console.WriteLine("[FAIL] " + name); }
        }
    }
}
