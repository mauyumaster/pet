// --calibertest：**离线**验「积分口径」这件事本身。
//
// 为什么单独有一个：
//   --statustest 只能拿真接口验，而真接口不会因为我们想验就变形 ——
//   「口径这条判据究竟有没有资格变红」，在联网测试里根本逼不出来
//   （本项目判据纪律 7：先证明它**能**失败，再拿它下结论）。
// 做法：口径逻辑已抽成纯函数 StatusProbe.ParseWorkbuddyJson()，这里用**合成响应**造形状：
//   混合桶 / 只剩别的桶 / 返回不完整 / 网关回话 / 少字段 / 非法 JSON / 脏条目 / 字段类型漂移。
// 全程不联网、不读凭据 ⇒ 可离线跑、可重复、结论确定。
//
// ⚠ 本文件刻意只用逐字字符串（@"..."），全篇不出现反斜杠 ——
//   本项目多次栽在转义上（批处理里 \r\n 被折叠、《批处理行尾＝可执行性》）。
//
// ★ 战果（2026-09-18 首跑就抓到）：用例 ⑦ 让「JsonElement.TryGetInt32/TryGetDouble 对非 Number
//   元素**抛异常**（不是返回 false）」这个旧 bug 当场现形 —— 真接口一直给 Number，联网测试永远照不出来。
//   它的后果不是「少算」而是**整条读数丢失**（c.Remain 还没赋值就跳 catch）。同类隐患共四处（code /
//   CapacityType / TotalDosage / TotalCount），已一并换成宽容取值器。
using System;
using System.Globalization;
using System.Text;

namespace AzhuPet
{
    internal static class CaliberTest
    {
        private static int _pass, _fail;

        // ---- 合成额度条目（逐字字符串，键名与真实响应一致）----
        private const string T1_1000 = @"{""CapacityType"":1,""Status"":0,""CapacityRemain"":1000,""CapacitySize"":1000,""PackageName"":""a""}";
        private const string T1_55   = @"{""CapacityType"":1,""Status"":0,""CapacityRemain"":55,""CapacitySize"":110,""PackageName"":""b""}";
        private const string T1_EXP  = @"{""CapacityType"":1,""Status"":3,""CapacityRemain"":0,""CapacitySize"":900,""PackageName"":""expired""}";
        private const string T4_500  = @"{""CapacityType"":4,""Status"":0,""CapacityRemain"":500,""CapacitySize"":500,""PackageName"":""CodeBuddy""}";
        private const string T1_10   = @"{""CapacityType"":1,""Status"":0,""CapacityRemain"":10,""CapacitySize"":10}";
        private const string T1_STR  = @"{""CapacityType"":1,""Status"":0,""CapacityRemain"":""20"",""CapacitySize"":20}";  // 数字以字符串给
        private const string T1_NOREM= @"{""CapacityType"":1,""Status"":0,""CapacitySize"":100}";                          // 缺 CapacityRemain
        private const string T_BADT  = @"{""CapacityType"":""x"",""Status"":0,""CapacityRemain"":77}";                     // 类型字段非数字

        // 字段类型整体漂移（键名、层级同真，但所有数字都变成了字符串）——
        // 这是「接口悄悄改类型」的最小复现，旧写法在这里会**整条读数丢失**而不是少算。
        private const string ALL_STRINGS =
            @"{""code"":""0"",""data"":{""Response"":{""Data"":{""TotalCount"":""3"",""TotalDosage"":""1555"",""Accounts"":["
            + @"{""CapacityType"":""1"",""Status"":""0"",""CapacityRemain"":""1055"",""CapacitySize"":""1110""},"
            + @"{""CapacityType"":""1"",""Status"":""3"",""CapacityRemain"":""0"",""CapacitySize"":""900""},"
            + @"{""CapacityType"":""4"",""Status"":""0"",""CapacityRemain"":""500"",""CapacitySize"":""500""}]}}}}";

        public static int Run(Cli o)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }
            Console.WriteLine("---- 积分口径测试（离线；合成响应，不联网、不读凭据）----");
            Console.WriteLine("主口径常量：MainCreditType = " + StatusProbe.MainCreditType);
            Console.WriteLine();

            var mixed = F1_Mixed();
            F2_OnlyOtherBucket();
            F3_IncompleteReturn();
            F4_GatewayCode();
            F5_NoAccounts();
            F6_BadJson();
            F7_DirtyItems();
            F8_AllStrings();
            HarnessSelfCheck(mixed);

            Console.WriteLine();
            Console.WriteLine("---- PASS " + _pass + " / FAIL " + _fail + " ----");
            return _fail == 0 ? 0 : 1;
        }

        // ① 混合桶：与 2026-09-18 的真实响应同形。
        //    真值：type=1 余 1000+55+0 = 1055；另有一笔 type=4 的 500；接口全桶合计 1555。
        private static StatusProbe.WbCaliber F1_Mixed()
        {
            Console.WriteLine("[用例 ①] 混合桶（与真实响应同形）");
            var c = StatusProbe.ParseWorkbuddyJson(
                Envelope(T1_1000 + "," + T1_55 + "," + T1_EXP + "," + T4_500, 4, 1555));
            CheckTrue("口径成立", c.Ok);
            Check("主口径余(type=1)", c.Remain, 1055);
            Check("非主口径余", c.OtherRemain, 500);
            Check("全桶合计(TotalDosage)", c.AllBucketsRemain, 1555);
            Check("参与求和的条数(type=1)", c.Type1Count, 3);
            Check("返回条数", c.AccountCount, 4);
            Check("TotalCount", c.TotalCount, 4);
            // 这条是本轮的教训：两个数**本来就不该相等**，混为一谈就会得错误结论。
            CheckTrue("主口径 ≠ 全桶合计（不许混为一谈）", Math.Abs(c.Remain - c.AllBucketsRemain) > 1e-9);
            Console.WriteLine();
            return c;
        }

        // ② 只剩别的桶 ⇒ 口径失效，必须报错，**绝不许当 0 报**（当 0 就是「总数变小 ＋ 全绿」的假通过）。
        private static void F2_OnlyOtherBucket()
        {
            Console.WriteLine("[用例 ②] 只有非主口径桶 ⇒ 口径失效");
            var c = StatusProbe.ParseWorkbuddyJson(Envelope(T4_500, 1, 500));
            CheckFalse("口径不成立", c.Ok);
            CheckContains("原因指名 type", c.Why, "type=" + StatusProbe.MainCreditType);
            Console.WriteLine();
        }

        // ③ 接口自称 20 条却只回 2 条 ⇒ 解析层必须如实透出两侧数字，让消费者能判「求和不完整」。
        private static void F3_IncompleteReturn()
        {
            Console.WriteLine("[用例 ③] 返回不完整（TotalCount=20，只回 2 条）");
            var c = StatusProbe.ParseWorkbuddyJson(Envelope(T1_10 + "," + T4_500, 20, 510));
            CheckTrue("口径成立（单条都合法）", c.Ok);
            Check("TotalCount 如实透出", c.TotalCount, 20);
            Check("返回条数如实透出", c.AccountCount, 2);
            CheckTrue("两侧不等 ⇒ 消费者可判 FAIL", c.TotalCount != c.AccountCount);
            Console.WriteLine();
        }

        // ④ 网关/风控回话：data 整个不在，只剩 code+msg。要报得出原因，而不是「字段缺失(一串 JSON)」。
        private static void F4_GatewayCode()
        {
            Console.WriteLine("[用例 ④] 网关回话 code≠0");
            var c = StatusProbe.ParseWorkbuddyJson(@"{""code"":1001,""msg"":""unauthorized""}");
            CheckFalse("口径不成立", c.Ok);
            CheckContains("原因含 code=1001", c.Why, "code=1001");
            Console.WriteLine();
        }

        // ⑤ 结构不完整（有 Data 但没有 Accounts）。
        private static void F5_NoAccounts()
        {
            Console.WriteLine("[用例 ⑤] 响应里没有 Accounts");
            var c = StatusProbe.ParseWorkbuddyJson(@"{""code"":0,""data"":{""Response"":{""Data"":{""TotalCount"":3}}}}");
            CheckFalse("口径不成立", c.Ok);
            CheckContains("原因指名 Accounts", c.Why, "没有 Accounts");
            Console.WriteLine();
        }

        // ⑥ 非法 JSON：不许抛出来炸掉调用方（气泡是 async void，抛了就崩桌宠），要变成 Why。
        private static void F6_BadJson()
        {
            Console.WriteLine("[用例 ⑥] 非法 JSON");
            var c = StatusProbe.ParseWorkbuddyJson(@"{""code"":0,");
            CheckFalse("口径不成立", c.Ok);
            CheckContains("原因以「解析失败」开头", c.Why, "解析失败");
            Console.WriteLine();
        }

        // ⑦ 脏条目：缺字段／类型怪 ⇒ 跳过；数字以字符串给 ⇒ 计入（NumOf 的既有兼容）。
        //    关键是**跳过 ≠ 当 0 计入**：若把脏条目当 0 计入，条数会虚高而金额偏低。
        //    ⚠ 本用例首跑时抓到旧代码的真 bug：CapacityType 给字符串会让 TryGetInt32 抛异常 ⇒ 整条读数丢失。
        private static void F7_DirtyItems()
        {
            Console.WriteLine("[用例 ⑦] 脏条目（缺字段 / 类型怪 / 数字以字符串给）");
            var c = StatusProbe.ParseWorkbuddyJson(
                Envelope(T1_10 + "," + T1_STR + "," + T1_NOREM + "," + T_BADT, 4, 30));
            if (!c.Ok) Console.WriteLine("  (Why=" + (c.Why ?? "(null)") + ")");
            CheckTrue("口径成立（脏条目不炸）", c.Ok);
            Check("只累计可解析的（10 + \"20\"）", c.Remain, 30);
            Check("参与求和条数", c.Type1Count, 2);
            Check("返回条数（含脏条目）", c.AccountCount, 4);
            CheckTrue("参与求和 < 返回条数 ⇒ 脏条目被跳过而非当 0", c.Type1Count < c.AccountCount);
            Console.WriteLine();
        }

        // ⑧ 字段类型整体漂移：所有数字都变成字符串。旧代码在这里**整条读数丢失**（不是少算）。
        private static void F8_AllStrings()
        {
            Console.WriteLine("[用例 ⑧] 字段类型漂移（数字全以字符串给）");
            var c = StatusProbe.ParseWorkbuddyJson(ALL_STRINGS);
            if (!c.Ok) Console.WriteLine("  (Why=" + (c.Why ?? "(null)") + ")");
            CheckTrue("口径成立（不因类型漂移而丢读数）", c.Ok);
            Check("主口径余", c.Remain, 1055);
            Check("非主口径余", c.OtherRemain, 500);
            Check("全桶合计", c.AllBucketsRemain, 1555);
            Check("TotalCount", c.TotalCount, 3);
            Check("参与求和条数", c.Type1Count, 2);
            Console.WriteLine();
        }

        // ⑨ 自检：**断言器**与**漂移哨兵**各自有没有资格失败。
        //    报不出来 ⇒ 上面所有 PASS 都不算数（本项目判据纪律 7 的元级应用）。
        //    - 断言器用纯比较函数 Eq() 验，避免在正常输出里混进一行看起来像失败的 FAIL 行；
        //    - 漂移哨兵用不一致的三个数喂它，看它会不会报（只写在 --statustest 里就永远逼不出来）。
        private static void HarnessSelfCheck(StatusProbe.WbCaliber mixed)
        {
            Console.WriteLine("[用例 ⑨] 自检：断言器 + 漂移哨兵有没有资格失败");
            CheckTrue("断言器：相同值判为相等（1055 vs 1055）", Eq(mixed.Remain, 1055));
            CheckFalse("断言器：不同值判为不等（1055 vs 999）", Eq(mixed.Remain, 999));
            CheckTrue("断言器有资格失败 ⇒ 本测试能变红",
                !Eq(mixed.Remain, 999) && Eq(mixed.Remain, 1055));

            CheckFalse("哨兵：三个数自洽（1055+500=1555）→ 不报",
                StatusProbe.CaliberDrift(1055, 500, 1555));
            CheckTrue("哨兵：三个数不自洽（1055+500≠9999）→ 必须报（负对照）",
                StatusProbe.CaliberDrift(1055, 500, 9999));
            CheckFalse("哨兵：差 0.5（正好等于绝对阈值）→ 不报，避浮点噪声",
                StatusProbe.CaliberDrift(1055, 500, 1555.5));
            CheckTrue("哨兵：差 1.1（超过绝对阈值）→ 报",
                StatusProbe.CaliberDrift(1055, 500, 1556.1));
            CheckFalse("哨兵：任一侧取不到（NaN）≠ 漂移，不许误报",
                StatusProbe.CaliberDrift(1055, double.NaN, 1555));
            Console.WriteLine();
        }

        // ---- 合成响应外壳（键名、层级与真实响应一致）----
        private static string Envelope(string accounts, int totalCount, double totalDosage)
        {
            return @"{""code"":0,""data"":{""Response"":{""Data"":{""TotalCount"":" + totalCount
                 + @",""TotalDosage"":" + totalDosage.ToString("0.##", CultureInfo.InvariantCulture)
                 + @",""Accounts"":[" + accounts + @"]}}}}";
        }

        // ---- 断言器（PASS/FAIL 计数；不抛异常，全部跑完再汇总）----
        // 相等判定抽成纯函数 Eq()，好让用例 ⑨ 能直接验「断言器有没有资格失败」。
        private static bool Eq(double a, double b) => Math.Abs(a - b) < 1e-9;
        private static void Check(string label, double got, double want)
        {
            if (Eq(got, want)) { _pass++; Console.WriteLine("  [PASS] " + label + " = " + Show(got)); }
            else { _fail++; Console.WriteLine("  [FAIL] " + label + "：期望 " + Show(want) + "，实得 " + Show(got)); }
        }
        private static void CheckTrue(string label, bool got) { if (got) { _pass++; Console.WriteLine("  [PASS] " + label); } else { _fail++; Console.WriteLine("  [FAIL] " + label); } }
        private static void CheckFalse(string label, bool got) { CheckTrue(label, !got); }
        private static void CheckContains(string label, string got, string needle)
        {
            bool ok = got != null && got.IndexOf(needle, StringComparison.Ordinal) >= 0;
            if (ok) { _pass++; Console.WriteLine("  [PASS] " + label + " = " + got); }
            else { _fail++; Console.WriteLine("  [FAIL] " + label + "：期望含「" + needle + "」，实得 " + (got ?? "(null)")); }
        }
        private static string Show(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
