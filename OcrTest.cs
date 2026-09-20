// --ocrtest：验「她能不能在本机读屏幕上的字」。
//
// 为什么这一条必须离线可验、且必须自己**造**输入：
//   判据的输入不许沿用外部可变状态（本仓旧坑：--speakvis 去读 config.json，
//   于是同一份判据的绿/红取决于托盘里那个勾当时勾没勾）。所以这里的图是**画出来的**，
//   文本是**写死的**，跟屏幕上此刻有什么完全无关。
//
// 判据的分工（哪一条负责什么，必须分得清）：
//   合成图 → 验**识别能力**（本机识别器读不读得出中文）＋ **归一化**（读出来的东西能不能直接用）
//   纯色图 → 验**不许瞎编**（一个字都没有的图必须报「没有字」，而不是编出一句）
//   纯函数 → 验归一化本身（不必开引擎也能跑，喂合成字符串逼红）
//   敏感表 → 验「哪些窗口一律不读」，且**必须同时验一个普通窗口不算敏感**
//           （只验命中不验不命中 ⇒ 把一切都判敏感也能全绿，那条判据就没资格失败）
//   真截图那一半由 `--ocrprobe` 补（拿真前台窗口的真像素验），合成图替代不了它。
//
// ⚠ 「可看绝不留档」在这条链上有一个**明写的例外**：`--ocrprobe` 会把真读到的文字
//   写进 %TEMP%\azhu_ocrprobe.json。理由没有别的 —— 验证这一步的全部意义就是
//   **让你亲眼看一遍她读到了什么**，不落下来你就只能听我说。它只在**你手动跑那一条**时发生，
//   生产路径（托盘入口、将来的提示注入）一个字节都不落盘（`nothingWrittenToDisk` 那条判据管的是它）。
//
// 负对照：`--ocrtest --no-ocr-normalize`
//   关掉归一化（直接用引擎原文）后，`tidyCjkSpaces` 必须**具备变红的条件** ——
//   中文 OCR 输出是「桌 宠 能 看 见」，关掉归一化就会重新出现「汉字 空格 汉字」。
//   负对照只跑这一条，理由见本仓纪律：判据串在一起跑时，**别的判据会先把它拦成红**，
//   于是你拿到一个 exit 1，却不知道红的到底是不是目标那一条。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AzhuPet
{
    internal static class OcrTest
    {
        /// <summary>合成图上画的内容。**写死的常量** —— 判据不许依赖屏幕此刻有什么。</summary>
        private const string ExpectZh = "桌宠能看见这行字吗";
        private const string ExpectTail = "ABC 2026";

        public static int Run(Cli o)
        {
            bool negative = o.NoOcrNormalize;
            OcrEye.RawTextNoNormalize = negative;
            // ⚠ 两条负对照各管一件事，必须分别跑：`--no-ocr-normalize` 管归一化，
            //   `--no-lastforeign` 管「前台是她自己时该读哪个窗口」。合成一个开关 = 说不清是哪条红。
            Watcher.AllowLastForeign = !o.NoLastForeign;

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
            // 一份「读到了内容」的结果。**自己造**，不借用上面真跑出来的那个 ——
            // 否则引擎一旦出问题，H 组会跟着一起红，红的原因就分不清是排版还是识别。
            OcrEye.Result OkRes()
            {
                return new OcrEye.Result
                {
                    Ok = true, Chars = ExpectZh.Length, Lines = 1, Ms = 40,
                    Text = ExpectZh, Why = "读到内容",
                };
            }

            // ================= 负对照：只跑与目标判据有关的那两条 =================
            // ⚠ 为什么只跑这几条：判据串在一起跑时，**别的判据会先把目标判据拦成红**，
            //   于是你拿到一个 exit 1，却不知道红的到底是不是它。
            if (negative)
            {
                var s0 = SynthText(ExpectZh + " " + ExpectTail);
                var r0 = s0 == null ? null : OcrEye.Read(s0);
                bool ranOk = r0 != null && r0.Ok;
                bool rawHasPair = ranOk && OcrEye.HasSpaceBetweenCjk(r0.Text);
                // ↓↓ 这一行就是正常跑里 `tidyCjkSpaces` 的**判据体本身**，不是它的近似。
                //    负对照的正确形状是「把目标判据原样跑一遍，看它会不会红」，
                //    而不是「另写一条判据去猜它为什么会红」。
                bool targetWouldPass = ranOk && !OcrEye.HasSpaceBetweenCjk(r0.Text);

                Check("negControl_recognized", ranOk,
                    ranOk ? "负对照下识别本身仍然成功（「" + Clip(r0.Text) + "」）—— 否则后面两条没有意义"
                          : "连识别都没跑起来（" + (r0 == null ? "合成图失败" : r0.Why) + "）—— 负对照无效");
                Check("negControl_rawHasCjkSpaces", rawHasPair,
                    rawHasPair ? "关掉归一化后引擎原文里确实有「汉字 空格 汉字」（「" + Clip(r0.Text) + "」）"
                               : "关掉归一化后仍然没有字间空格 —— 那 tidyCjkSpaces 的绿跟归一化无关");
                // ⚠ 这一条是**本次负对照的结论本身**：目标判据现在会红吗。
                //   绿的写法是 `!targetWouldPass` —— 读作「它确实会红」，所以本跑报 OK。
                Check("negControl_targetGoesRed", !targetWouldPass,
                    targetWouldPass
                        ? "关掉归一化后 tidyCjkSpaces 仍然会绿 ⇒ 那条判据没资格失败，它的绿没有信息量 ❌"
                        : "关掉归一化后 tidyCjkSpaces 会红（" + (rawHasPair ? "字间空格仍在" : "文本形态不符")
                          + "）⇒ 它确实是在测归一化 ✅");
                return Report(ok, checks, negative, null, "normalize");
            }

            // ================= 负对照 ②：`--no-lastforeign` —— 只跑目标那一条 =================
            // 正常跑里「前台是她自己 ⇒ 回退到上一条真实读数」那条判据**必须靠回退才成立**。
            // 关掉回退后，它选回的应当正是「她自己」⇒ 目标判据会红。
            if (o.NoLastForeign)
            {
                var me0 = new Fg { Hwnd = new IntPtr(3), Proc = Watcher.SelfName(), Title = "阿助桌宠" };
                var edge0 = new Fg { Hwnd = new IntPtr(1), Proc = "msedge", Title = "某个网页" };
                var got0 = Watcher.ChooseReadTarget(me0, edge0, Watcher.SelfName(), false);
                bool pickedLast = ReferenceEquals(got0, edge0);
                Check("negControl_targetGoesRed", !pickedLast,
                    pickedLast
                        ? "关掉回退后仍然选中了上一条读数 ⇒ pickFallsBackWhenForegroundIsSelf 的绿跟回退无关 ❌"
                        : "关掉回退后它选回了「" + (got0 == null ? "(空)" : got0.Proc) + "」＝她自己"
                          + " ⇒ pickFallsBackWhenForegroundIsSelf 确实是靠回退通过的 ✅");
                // ⚠ 走负对照的报告文件 —— 以前这里传的是 `negative`（＝false），
                //   于是负对照会**覆盖掉正常跑的报告**，而这正好违反上面 Report 里
                //   「负对照与正常判据不许共用一个报告文件」那条注释（同一份数据两个落点）。
                return Report(ok, checks, true, null, "lastforeign");
            }

            // ================= 负对照 ③：`--no-ocr-gate` —— 只跑「门」那几条 =================
            // ⚠⚠ 这一条要拆穿一种很具体的假通过：J 组里那几条「必须不发」，
            //   有可能只是因为**当时屏幕上没文字**才绿的。跳过门之后，判据在
            //   「允许外发 + 普通窗口 + 本机读开着」的情况下**必须放弃拒** ⇒
            //   如果那几条照样绿，说明它们测的不是门。
            // ⚠ 三个负对照互斥，同时给时只有最先命中的那个会跑（就是上面那两个分支）。
            if (o.NoOcrGate)
            {
                OcrEye.GateOff = true;
                string selfN = Watcher.SelfName();
                bool g1 = !OcrEye.ShouldSendText(true, false, selfN, "msedge", "某个网页");    // 默认关
                bool g2 = !OcrEye.ShouldSendText(true, true, selfN, "WeChat", "和某某的聊天"); // 敏感窗口
                bool g3 = !OcrEye.ShouldSendText(false, true, selfN, "msedge", "某个网页");    // 本机读关着

                // 先证明负对照本身有效：门确实被跳过了（全合法的目标仍放行 ⇒ 不是坏在别处）。
                Check("negControl_gateOffReallyOn",
                    OcrEye.ShouldSendText(true, true, selfN, "msedge", "某个网页"),
                    "跳过门后，全合法的目标仍然放行 —— 说明下面的「会红」不是坏在别处");
                Check("negControl_targetGoesRed", !g1 && !g2 && !g3,
                    (!g1 && !g2 && !g3)
                        ? "跳过门后三条目标判据**同时变红**："
                          + "gateRefusesByDefault ✓／gateRefusesSensitiveProc ✓／gateRefusesWhenLocalReadOff ✓"
                          + " ⇒ 它们测的确实是那道门 ✅"
                        : "跳过门后仍有判据保持绿（"
                          + (g1 ? "gateRefusesByDefault 仍绿；" : "")
                          + (g2 ? "gateRefusesSensitiveProc 仍绿；" : "")
                          + (g3 ? "gateRefusesWhenLocalReadOff 仍绿；" : "")
                          + "）⇒ 那条判据的绿跟门无关，它没资格失败 ❌");
                OcrEye.GateOff = false;
                return Report(ok, checks, true, null, "gate");
            }

            // ================= A. 引擎（拿不到必须报出来，不许静默跳过） =================
            Check("engineAvailable", OcrEye.Available(),
                OcrEye.Available()
                    ? "本机识别器可用，语言 " + OcrEye.LangTag + "（本地能力：不联网、不要模型、不出本机）"
                    : "本机识别器不可用：" + OcrEye.Why);

            // ================= B. 归一化：纯函数，先离线钉死 =================
            // ⚠ 这一组**不依赖引擎**，所以就算引擎挂了，它们也能告诉你代码逻辑对不对。
            // ⚠ 这三条要能互相区分 —— 一条坏掉时，detail 里那句原话直接告诉你是哪一环：
            //   tidyCollapsesWhitespace 只压空白（纯拉丁，汉字规则插不上手）
            //   tidyRemovesCjkSpaces    只去汉字间空格
            //   tidyCollapsesThenJoins  两环都过；**首跑就是它红的** —— 我的期望值写成了
            //                           「你好世界 第二行」，可「界 第」正是汉字间空格，该被去掉。
            //                           红的是判据的期望值，不是代码（这正是「先跑一遍再看结论」的用处）。
            Check("tidyCollapsesWhitespace", OcrEye.Tidy("  hi \t there  ") == "hi there",
                "多空白／制表符 → 「" + OcrEye.Tidy("  hi \t there  ") + "」");
            Check("tidyRemovesCjkSpaces", OcrEye.Tidy("桌 宠 能 看 见") == "桌宠能看见",
                "字间空格 → 「" + OcrEye.Tidy("桌 宠 能 看 见") + "」");
            Check("tidyCollapsesThenJoins", OcrEye.Tidy("  你好   世界  ") == "你好世界",
                "先压空白、再并汉字 → 「" + OcrEye.Tidy("  你好   世界  ") + "」（期望「你好世界」）");
            // ⚠ 正对照的另一半：拉丁词之间的空格**必须留下** —— 去掉它会把 "hello world" 粘死。
            Check("tidyKeepsLatinSpaces", OcrEye.Tidy("hello   world") == "hello world",
                "拉丁词间空格 → 「" + OcrEye.Tidy("hello   world") + "」（不许被当成汉字间空格一起删掉）");
            Check("tidyKeepsMixedBoundary", OcrEye.Tidy("看见了 hello 世界") == "看见了 hello 世界",
                "中英混排 → 「" + OcrEye.Tidy("看见了 hello 世界") + "」（中英之间的空格要留）");
            Check("tidyHandlesNull", OcrEye.Tidy(null) == "" && OcrEye.Tidy("") == "",
                "null / 空串 → 空串（不抛异常）");
            Check("compactRemovesAllWhitespace", OcrEye.Compact("桌 宠\t能\n看见") == "桌宠能看见",
                "判据形态：去掉**所有**空白 → 「" + OcrEye.Compact("桌 宠\t能\n看见") + "」");
            Check("hasSpaceBetweenCjkDetects", OcrEye.HasSpaceBetweenCjk("桌 宠")
                && !OcrEye.HasSpaceBetweenCjk("桌宠") && !OcrEye.HasSpaceBetweenCjk("hello world"),
                "「桌 宠」判为有 → 有；「桌宠」判为无 → 无；「hello world」判为无 → 无"
                + "（这条是 tidyCjkSpaces 的探测器的自检 —— 探测器本身坏了，判据的绿就是假的）");

            // ================= C. 敏感表：命中 ＋ **不命中**，两半都要 =================
            Check("sensitiveProcHit", OcrEye.IsSensitive("WeChat", "和某某的聊天"),
                "进程 WeChat → 敏感 ✅（聊天窗一律不读）");
            Check("sensitiveTitleHit", OcrEye.IsSensitive("chrome", "登录 - 某银行"),
                "标题含「登录」→ 敏感 ✅");
            Check("sensitiveProcCaseInsensitive", OcrEye.IsSensitive("KeePassXC", "vault"),
                "进程名大小写不敏感 → 敏感 ✅");
            // ⚠ 正对照：**不许把一切都判成敏感** —— 否则等于「永远不读」，而判据照样全绿。
            Check("normalWindowNotSensitive", !OcrEye.IsSensitive("Code", "Brain.cs"),
                "普通编辑器窗口 → 非敏感 " + (!OcrEye.IsSensitive("Code", "Brain.cs") ? "✅" : "❌（过滤过宽 = 等于不读）"));

            // ================= D. 识别：合成图（正对照） =================
            var shot = SynthText(ExpectZh + " " + ExpectTail);
            Check("synthShotHasPixels", shot != null && shot.Pixels != null && shot.PixelW > 0,
                shot == null ? "合成失败" : "白底黑字 " + shot.PixelW + "×" + shot.PixelH + "，BGRA " + (shot.Pixels.Length / 1024) + " KB");

            OcrEye.Result zh = null;
            if (shot != null)
            {
                zh = OcrEye.Read(shot);
                Console.WriteLine("  合成图识别：Ok=" + zh.Ok + " 「" + Clip(zh.Text) + "」 " + zh.Describe());

                // 识别本身：比对必须用 Compact —— ⚠ 中文字间空格会让朴素 IndexOf 把
                // 「读出来了」判成「没读出来」，本仓为此误判过一次能力。
                bool got = zh.Ok && OcrEye.Compact(zh.Text).IndexOf(OcrEye.Compact(ExpectZh), StringComparison.Ordinal) >= 0;
                Check("readsSyntheticZh", got,
                    got ? "读出了合成图里的中文「" + ExpectZh + "」 ✅（本机离线、不联网、不要模型）"
                        : "读不出 44 号中文字：Ok=" + zh.Ok + " text=「" + Clip(zh.Text) + "」 why=" + zh.Why);
                Check("readsSyntheticTail", zh.Ok && OcrEye.Compact(zh.Text).IndexOf(OcrEye.Compact(ExpectTail), StringComparison.Ordinal) >= 0,
                    zh.Ok && OcrEye.Compact(zh.Text).IndexOf(OcrEye.Compact(ExpectTail), StringComparison.Ordinal) >= 0
                        ? "拉丁＋数字「" + ExpectTail + "」也读出来了"
                        : "拉丁／数字没读出来（text=「" + Clip(zh.Text) + "」）");

                // ⚠ 目标判据：读出来的文本**可以直接用**（没有字间空格）。
                //   它的负对照就是 --no-ocr-normalize。
                Check("tidyCjkSpaces", !OcrEye.HasSpaceBetweenCjk(zh.Text),
                    !OcrEye.HasSpaceBetweenCjk(zh.Text)
                        ? "归一化后文本无「汉字 空格 汉字」⇒ 可以直接说出口（「" + Clip(zh.Text) + "」）"
                        : "文本里仍有字间空格：「" + Clip(zh.Text) + "」（这条正是被 --no-ocr-normalize 逼红的那一条）");
            }

            // ================= E. 纯色图：一个字都没有时**不许瞎编** =================
            var blank = SynthSolid(Colors.White, 800, 300);
            if (blank == null) Check("blankShotMade", false, "合成纯色图失败");
            else
            {
                var rb = OcrEye.Read(blank);
                // ⚠ 「读不到字」与「读错了」是两件事：前者 Ok=true / Any=false，
                //   后者是 Ok=false（引擎坏、超限、被关掉）。判据要的是**前者**。
                Check("blankYieldsNoText", rb.Ok && !rb.Any,
                    "纯白无字图 → Ok=" + rb.Ok + "，字数=" + rb.Chars + "，why=" + rb.Why
                    + (rb.Ok && !rb.Any ? " ✅（正确地报『这一屏没有文字』，而不是编一句）"
                                        : "（应当 Ok=true 且 字数=0）"));
            }

            // ================= F. 不落盘 =================
            string tmp = Path.GetTempPath();
            int before = CountImages(tmp);
            if (shot != null) OcrEye.Read(shot);
            if (blank != null) OcrEye.Read(blank);
            int after = CountImages(tmp);
            Check("nothingWrittenToDisk", after <= before,
                "OCR 前后临时目录里的图片文件数：" + before + " → " + after
                + (after <= before ? " ✅（截图与文本只在内存里活一次）" : " ❌ 落盘了 " + (after - before) + " 个文件"));

            // ================= G. 截「**指定**窗口」的守卫（靶子自己钉死，不沿用当前前台） =================
            // ⚠⚠ 为什么改成「拿句柄截」：托盘菜单属于 pet.exe，你一点托盘前台就变成她自己的窗口，
            //   「截当前前台」于是必然截到她自己（用户实拍：「她只看见了自己」）。
            //   修法是把**那个窗口**先定下来，再按句柄截 —— 于是这三条必须成对出现：
            //     正对照：合法的窗口 + 对的进程名 → **必须截到**
            //     负对照：进程名对不上 → **必须拒**
            //   只验后一条的话，`return null;` 就能全绿（本仓纪律：判据要能区分「对了」和「没做」）。
            IntPtr target = Native.FindWindow("Shell_TrayWnd", null);   // 任务栏：一个确定的、非自己的窗口
            Check("pinnedTargetFound", target != IntPtr.Zero,
                target != IntPtr.Zero
                    ? "找到任务栏窗口当靶子（句柄非零 ⇒ 下面两条才跑得起来）"
                    : "找不到 Shell_TrayWnd —— 下面两条没有靶子，这次没跑起来（不许当成通过）");

            var good = ScreenEye.CaptureWindow(target, "explorer", true, 0);
            Check("captureWindowPinnedTarget", good != null && good.Pixels != null && good.PixelW > 0,
                good == null
                    ? "按句柄截任务栏失败了（正对照不成立 ⇒ 下面那条「必须拒」没有信息量）"
                    : "按句柄截「" + good.Proc + "」→ " + good.PixelW + "×" + good.PixelH + " 带像素 ["
                      + good.How + "] ✅（这条保证下一句的『拒』不是靠永远返回 null 换来的）");

            var wrongProc = ScreenEye.CaptureWindow(target, "___definitely_not_a_real_process___", true, 0);
            Check("expectProcMismatchRefusesCapture", wrongProc == null,
                wrongProc == null
                    ? "句柄是它、进程名不是它 → 拒绝截图 ✅（观察与截图之间前台变了时，她不会拿到一张对不上的图）"
                    : "居然截到了 " + wrongProc.Proc + " —— 这条守卫没生效");

            var refused = OcrEye.ReadForeground("___definitely_not_a_real_process___");
            Check("ocrRefusesWhenProcMismatch", !refused.Ok && !string.IsNullOrEmpty(refused.Why),
                refused.Ok
                    ? "仍然读到了内容：「" + Clip(refused.Text) + "」 ❌"
                    : "→ 拒绝读：" + refused.Why + " ✅（拒绝必须留可读原因，不许静默变成『没有字』）");

            // ================= H. 「读的是谁」：前台是她自己时必须换一个窗口 =================
            // ⚠⚠ 这一组的来历（2026-09-20 用户实拍「她只看见了自己」）：
            //   托盘菜单属于 pet.exe，菜单一关前台就落回她自己的窗口 ⇒ 按「当前前台」读，
            //   结果必然是「看见了：pet · 没读」。修法 ＝ 回退到「上一条真实读数」。
            // ⚠ 真机条件（你去点托盘）没法在无人时复现，所以这一组用的是**合成读数**：
            //   它验的是选择逻辑本身。真条件的端到端由 `--ocrvis` 补（那一条会**自己造**条件）。
            // ⚠ 负对照是 `--no-lastforeign`：关掉回退后，本组第 2 条必须红。
            string self = Watcher.SelfName();
            var fgSelf = new Fg { Hwnd = new IntPtr(3), Proc = self, Title = "阿助桌宠" };
            var fgEdge = new Fg { Hwnd = new IntPtr(1), Proc = "msedge", Title = "某个网页 - 浏览器" };
            var fgCode = new Fg { Hwnd = new IntPtr(2), Proc = "Code", Title = "Brain.cs" };
            var fgDesk = new Fg { Hwnd = new IntPtr(4), Proc = "explorer", Title = "", Cls = "Progman", Shell = true };

            Check("pickPrefersCurrentWhenForeign",
                ReferenceEquals(Watcher.ChooseReadTarget(fgCode, fgEdge, self, true), fgCode),
                "当前前台是普通应用、上一条是别的应用 → 选**当前**（你在哪，她看哪）");
            // ↓↓ 这一条就是本案的判据本体。
            Check("pickFallsBackWhenForegroundIsSelf",
                ReferenceEquals(Watcher.ChooseReadTarget(fgSelf, fgEdge, self, true), fgEdge),
                "前台＝她自己（" + self + "）→ 选**上一条真实读数**（msedge）"
                + " —— 这一条红过：托盘点一下她就只看得见自己");
            Check("pickSkipsShellWindow",
                ReferenceEquals(Watcher.ChooseReadTarget(fgDesk, fgEdge, self, true), fgEdge),
                "前台是桌面／任务栏（Progman）→ 也不算「正在用的窗口」，照样回退");
            Check("pickNoLastReturnsCurrentSoDownstreamCanExplain",
                ReferenceEquals(Watcher.ChooseReadTarget(fgSelf, null, self, true), fgSelf),
                "没有上一条读数时把「当前」原样交出（而不是返回 null）—— 这样下游能报出"
                + "「我现在只看得见自己」，而不是含混的一句『没读到』");
            Check("pickNilCurrentFallsBack",
                ReferenceEquals(Watcher.ChooseReadTarget(null, fgEdge, self, true), fgEdge),
                "当前前台读不到（正在切换／锁屏）→ 回退到上一条");
            Check("pickDoesNotInventATarget",
                ReferenceEquals(Watcher.ChooseReadTarget(fgSelf, fgSelf, self, true), fgSelf),
                "当前与前一条都只是她自己 ⇒ 原样交出「她自己」，**不许凭空编一个窗口**"
                + "（那样她会说出一个你根本没开过的应用）");

            // ================= I. 气泡文本（纯函数，运行时判据看的是同一个函数） =================
            var r1 = OkRes(); r1.Proc = "msedge"; r1.Title = "某个网页 - 浏览器";
            string t1 = PetWindow.FormatScreenRead(r1);
            Check("formatShowsText", t1.IndexOf(ExpectZh, StringComparison.Ordinal) >= 0,
                "读到内容 → 气泡里出现原文：「" + Clip(t1, 70) + "」");
            Check("formatShowsWhoWasRead", t1.IndexOf("看见了", StringComparison.Ordinal) >= 0
                                            && t1.IndexOf("浏览器", StringComparison.Ordinal) >= 0,
                "第一行写明**读的是谁** → 「" + Clip(t1, 40) + "」");
            string t2 = PetWindow.FormatScreenRead(new OcrEye.Result
            { Ok = true, Chars = 0, Lines = 0, Ms = 30, Text = "", Proc = "chrome", Title = "看图" });
            Check("formatDistinguishesEmptyFromFail", t2.IndexOf("没有字", StringComparison.Ordinal) >= 0,
                "读到 0 字 → 「" + Clip(t2, 70) + "」（必须与『读失败』长得不一样）");
            string t3 = PetWindow.FormatScreenRead(new OcrEye.Result
            { Ok = false, Why = "这个窗口不读（敏感：WeChat）", Proc = "WeChat", Title = "和某某的聊天" });
            Check("formatSaysWhyWhenRefused", t3.IndexOf("WeChat", StringComparison.Ordinal) >= 0
                                             && t3.IndexOf("不读", StringComparison.Ordinal) >= 0,
                "被拒时气泡写出原因 → 「" + Clip(t3, 70) + "」（要求带出**是哪个窗口**被拒；静默空白是本仓老毛病）");
            var r4 = new OcrEye.Result
            { Ok = true, Chars = 500, Lines = 5, Ms = 50, Text = new string('字', 500), Proc = "Code", Title = "x" };
            string t4 = PetWindow.FormatScreenRead(r4);
            Check("formatCapsLength", t4.Length < 600 && t4.IndexOf("共 500 字", StringComparison.Ordinal) >= 0,
                "原文 500 字 → 气泡文本共 " + t4.Length + " 字，且写明「共 500 字」（不许把整屏文字塞进气泡）");

            // ⚠ 回退来的读数**必须在气泡上标出来**，而且不能把「几秒前」写成「现在」——
            //   否则你会以为她读错了窗口（她读的是你上一个真实在用的那个，不是此刻最前面那个）。
            var r5 = new OcrEye.Result
            { Ok = true, Chars = 9, Lines = 1, Ms = 40, Text = ExpectZh, Proc = "msedge", Title = "某个网页", FromLast = true, AgeSec = 12.0 };
            string t5 = PetWindow.FormatScreenRead(r5);
            Check("formatMarksTheFallbackAge", t5.IndexOf("12 秒前", StringComparison.Ordinal) >= 0,
                "回退来的读数 → 「" + Clip(t5, 60) + "」（必须写出「N 秒前那个窗口」）");
            var r6 = new OcrEye.Result
            { Ok = true, Chars = 9, Lines = 1, Ms = 40, Text = ExpectZh, Proc = "msedge", Title = "某个网页", Caveat = "这扇窗当时不在最前面，读到的可能被挡住" };
            string t6 = PetWindow.FormatScreenRead(r6);
            Check("formatShowsOcclusionCaveat", t6.IndexOf("可能被挡住", StringComparison.Ordinal) >= 0,
                "屏幕拷贝来的图 → 气泡带出「可能被挡住」：「" + Clip(t6, 80) + "」"
                + "（截「上一个窗口」时它通常不在前台 ⇒ 这条会是常客，不许静默）");

            // 关掉开关时也必须拒绝 —— 两个开关各管一件事，这条验的是 OcrOn。
            bool wasOn = OcrEye.Enabled;
            OcrEye.Enabled = false;
            var off = OcrEye.ReadForeground(null);
            OcrEye.Enabled = wasOn;
            Check("disabledRefuses", !off.Ok && off.Why != null && off.Why.IndexOf("关", StringComparison.Ordinal) >= 0,
                "OcrOn=false 时 → " + off.Why + " ✅");

            // ================= J. 「屏幕上的字」要不要**发出去**（隐私判据） =================
            // ⚠⚠ 这一组管的是**跨线**那一步：屏幕内容离开本机。它一旦发生就不可撤回
            //   （字里可能有聊天记录、订单号、别人发来的原文）。
            // ⚠ 每条输入都自己钉死（纯函数）—— 不借用屏幕上此刻有什么，
            //   否则同一份判据的绿/红取决于你今天开着哪个窗口。
            // ⚠ 必须有**正对照**（gateOpensWhenAllGood）：只验「必须拒」的话，
            //   一个恒返回 false 的实现能让这一组全绿 —— 那些判据就没资格失败。
            string selfJ = Watcher.SelfName();

            Check("gateRefusesWhenLocalReadOff",
                !OcrEye.ShouldSendText(false, true, selfJ, "msedge", "某个网页"),
                "本机读屏幕文字关着 → 不许发（第一道闸）");
            Check("gateRefusesByDefault",
                !OcrEye.ShouldSendText(true, false, selfJ, "msedge", "某个网页"),
                "「说话时带上屏幕上的字」没开 → 不许发。⚠ 这条是隐私底线：默认必须是关的");
            Check("gateRefusesSelf",
                !OcrEye.ShouldSendText(true, true, selfJ, selfJ, "阿助桌宠"),
                "读的是她自己（" + selfJ + "）→ 不许发（否则她会把自己的测试文字当屏幕内容说出来）");
            Check("gateRefusesSensitiveProc",
                !OcrEye.ShouldSendText(true, true, selfJ, "WeChat", "和某某的聊天"),
                "目标进程属敏感类（WeChat）→ 不许发");
            Check("gateRefusesSensitiveTitle",
                !OcrEye.ShouldSendText(true, true, selfJ, "chrome", "登录 - 某网银"),
                "进程普通但标题命中敏感词（登录／网银）→ 不许发（标题也是内容）");
            Check("gateRefusesUnknownTarget",
                !OcrEye.ShouldSendText(true, true, selfJ, null, null),
                "不知道读的是哪个窗口 → 不许发（宁可不发，也不发一个来源不明的）");
            // ↓↓ 正对照。它保证上面六条的「拒」不是靠一个恒 false 的实现换来的。
            Check("gateOpensWhenAllGood",
                OcrEye.ShouldSendText(true, true, selfJ, "msedge", "某个网页"),
                "三个条件都满足（本机读开、允许外发、普通窗口）→ **必须放行**"
                + "（否则「拒」可以由恒 false 换来，上面六条就没有信息量）");

            // ⚠ 门关了但**说不出为什么** ＝ 静默跳过。四个关门档位逐个核对原因非空。
            bool whyOk =
                OcrEye.SendWhyText(false, true, selfJ, "msedge", "某个网页").Length > 0 &&
                OcrEye.SendWhyText(true, false, selfJ, "msedge", "某个网页").Length > 0 &&
                OcrEye.SendWhyText(true, true, selfJ, selfJ, "阿助桌宠").Length > 0 &&
                OcrEye.SendWhyText(true, true, selfJ, "WeChat", "x").Length > 0 &&
                OcrEye.SendWhyText(true, true, selfJ, null, null).Length > 0 &&
                OcrEye.SendWhyText(true, true, selfJ, "msedge", "某个网页").Length == 0;
            Check("sendWhyNotEmptyWhenRefused", whyOk,
                whyOk ? "每种「不发」都带得出可读原因（门开时原因为空）："
                        + OcrEye.SendWhyText(true, false, selfJ, "msedge", "某个网页")
                      : "有档位关了门却说不出为什么 —— 那就是静默跳过");

            // ---- 发出去的那段：必须**原样**是读到的原文（她的话才可能被指认回屏幕内容） ----
            const string Screen = "桌宠能看见这一行吗 ABC 2026";
            bool wasSend = OcrEye.SendText, wasEnable = OcrEye.Enabled;
            OcrEye.Enabled = true;

            OcrEye.SendText = true;
            string sent = OcrEye.TextForSpeakingFrom(
                () => new OcrEye.Result { Ok = true, Chars = Screen.Length, Lines = 1, Text = Screen, Why = "读到内容" },
                "msedge", "某个网页");
            Check("sendDeliversReadTextVerbatim",
                OcrEye.Compact(sent) == OcrEye.Compact(Screen) && sent.Length > 0,
                "门开着 → 发出去的是「" + Clip(sent, 60) + "」。⚠ 必须与读到的原文**逐字相同**"
                + "（改写／翻译／摘要都会让她说出屏幕上没有的东西）");

            OcrEye.SendText = false;
            string held = OcrEye.TextForSpeakingFrom(
                () => new OcrEye.Result { Ok = true, Chars = Screen.Length, Lines = 1, Text = Screen, Why = "读到内容" },
                "msedge", "某个网页");
            Check("sendEmptyWhenGateClosed", held.Length == 0,
                held.Length == 0
                    ? "门关着 → 一个字都不发，**即使读到了内容**（✓ 这条是「关掉就必须不发」的判据本体）"
                    : "门关着却仍然发出了「" + Clip(held, 40) + "」 ❌");

            OcrEye.SendText = true;
            string failed = OcrEye.TextForSpeakingFrom(
                () => new OcrEye.Result { Ok = false, Why = "这个窗口不读（敏感：WeChat）" }, "WeChat", "和某某的聊天");
            string empty = OcrEye.TextForSpeakingFrom(
                () => new OcrEye.Result { Ok = true, Chars = 0, Text = "", Why = "这一屏没有文字" }, "msedge", "看图");
            Check("sendEmptyWhenNothingRead", failed.Length == 0 && empty.Length == 0,
                "读失败／读到 0 字 → 提示里**不出现这一段**（不是写一句「没读到」——"
                + " 那种话会被模型照着说，正如它照着念秒数）");

            string longText = new string('字', OcrEye.PromptCap + 120);
            string capped = OcrEye.TextForSpeakingFrom(
                () => new OcrEye.Result { Ok = true, Chars = longText.Length, Lines = 1, Text = longText, Why = "读到内容" },
                "msedge", "某个网页");
            Check("sendCapHasEllipsis",
                OcrEye.Compact(capped).Length <= OcrEye.PromptCap + 1 && capped.EndsWith("…", StringComparison.Ordinal),
                "原文 " + longText.Length + " 字 → 发出去 " + capped.Length + " 字并以省略号结尾"
                + "（不写省略号的话，下游会把「半句话」当成全部）");

            string multiline = OcrEye.TextForSpeakingFrom(
                () => new OcrEye.Result { Ok = true, Chars = 9, Lines = 3, Text = "第一行\r\n第二行\r\n第三行", Why = "读到内容" },
                "msedge", "某个网页");
            Check("sendCollapsesToSingleLine", multiline.IndexOf('\n') < 0 && multiline.Length > 0,
                "多行原文 → 「" + Clip(multiline, 60) + "」（提示里必须是一行，否则模型会把换行当成「可以分行回话」）");

            OcrEye.SendText = wasSend;
            OcrEye.Enabled = wasEnable;

            return Report(ok, checks, negative, zh == null ? null : zh.Ms);
        }

        /// <summary>
        /// --ocrprobe：**真读一次当前前台窗口**，把真像素读到的真文字写进报告。
        /// ⚠ 合成图证明的是「识别能力存在」；这一条证明的是「接到真屏幕上没有断」——
        ///   两件事，合成图替代不了后一件（本仓那条「接线 vs 逻辑」）。
        /// </summary>
        public static int ProbeRun(Cli o)
        {
            var rows = new List<object>();
            bool ok = true;
            void Check(string name, bool pass, string detail)
            {
                if (!pass) ok = false;
                rows.Add(new Dictionary<string, object>
                {
                    ["name"] = name, ["ok"] = pass, ["detail"] = detail ?? "",
                });
            }

            Console.WriteLine("== 真读「她该看的那个窗口」 ==");
            Console.WriteLine("（把光标点到你想让她读的那个窗口上，再跑这一条）");

            // ⚠ 读的是**她该看的那个窗口**，不一定等于「此刻最前面那个」—— 前台是她自己时，
            //   她会回退到上一条真实读数（见 Watcher.ChooseReadTarget）。
            var t = Watcher.ReadTarget();
            string proc = t == null ? null : t.Proc;
            string title = t == null ? null : t.Title;
            bool isForground = t != null && Native.GetForegroundWindow() == t.Hwnd;
            Console.WriteLine("  要读：proc=" + (proc ?? "(读不到)") + "  title=" + (title ?? "(读不到)")
                + "  " + (isForground ? "（就是当前前台）" : "（不在前台 ⇒ 回退来的，或你刚点过托盘）"));

            bool sens = !string.IsNullOrEmpty(proc) && OcrEye.IsSensitive(proc, title);
            Check("probe.targetIsNotSelf", t == null || proc != Watcher.SelfName(),
                t == null ? "没有可读的窗口（" + "前台读不到，也没有更早的记录" + "）"
                          : (proc == Watcher.SelfName()
                              ? "要读的是她自己（" + proc + "）❌—— 说明既没有前台、也没有上一条读数"
                              : "要读的不是她自己（" + proc + "）✅"));
            Check("probe.notSensitive", !sens,
                sens ? "前台是敏感窗口（" + proc + "）⇒ 按设计不读，这是**正确行为**，不是故障" : "前台非敏感，可以读");

            if (!sens)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = OcrEye.ReadForeground(proc);
                sw.Stop();

                Check("probe.readOk", r.Ok,
                    r.Ok ? "读成功：" + r.Describe() : "读失败：" + r.Why
                    + (r.Ok ? "" : "（前台是她自己／已换走／没截到 —— 都可能，看上面的 proc）"));

                if (r.Ok)
                {
                    Check("probe.hasText", r.Any,
                        r.Any ? "真屏幕上读到 " + r.Chars + " 字（见下面 text 字段，**这是她的原话来源**）"
                              : "这个窗口一个字都没读到（看图／空白文档／纯图形界面 —— 也正常，但要你亲眼确认一次）");
                    Check("probe.totalUnder3s", sw.ElapsedMilliseconds < 3000,
                        "端到端（截图＋识别）" + sw.ElapsedMilliseconds + " ms"
                        + (sw.ElapsedMilliseconds < 3000 ? " ✅" : " ❌ 反常，像在等外部服务"));
                }

                if (r.Ok && r.Any)
                {
                    Console.WriteLine();
                    Console.WriteLine("---- 她读到的原文（截断 300 字）----");
                    Console.WriteLine(Clip(r.Text, 300));
                    Console.WriteLine("---- 完 ----");
                }

                Write(new Dictionary<string, object>
                {
                    ["ok"] = ok,
                    ["proc"] = proc, ["title"] = title, ["sensitive"] = sens,
                    ["readOk"] = r.Ok, ["chars"] = r.Chars, ["lines"] = r.Lines,
                    ["ms"] = r.Ms, ["lang"] = r.Lang, ["why"] = r.Why,
                    ["text"] = r.Text,
                    ["checks"] = rows,
                }, "azhu_ocrprobe.json");
            }
            else
            {
                Write(new Dictionary<string, object>
                {
                    ["ok"] = ok, ["proc"] = proc, ["title"] = title, ["sensitive"] = true, ["checks"] = rows,
                }, "azhu_ocrprobe.json");
            }

            foreach (object c in rows)
            {
                var d = (Dictionary<string, object>)c;
                Console.WriteLine("  " + ((bool)d["ok"] ? "[v] " : "[x] ") + d["name"] + "：" + d["detail"]);
            }
            if (rows.Count == 0) { Console.WriteLine("  [x] 一项都没跑 —— 算 FAIL"); ok = false; }
            Console.WriteLine("ocrprobe " + (ok ? "OK" : "FAIL") + " —— 报告 "
                + Path.Combine(Path.GetTempPath(), "azhu_ocrprobe.json"));
            return ok ? 0 : 1;
        }

        /// <summary>
        /// --ocrvis：**真窗口端到端** —— 走托盘那一项的完整路径（PetWindow.ShowScreenRead → 气泡）。
        ///
        /// ⚠ 为什么需要它：上面那 26 条判据全是「逻辑对」，它们**证明不了接上了**。
        ///   本仓为此付过两次代价：三环 16/16 全绿而她一言不发；`--watchtest` 全绿而
        ///   `SlowTick()` 里零调用点。UI 接缝（方法 → 气泡）只能靠真窗口验。
        ///
        /// ⚠ 那条**有资格变红**的判据是 `vis.bubbleNotStillLoading`：
        ///   如果 ShowScreenRead 半路把异常吞了、或者 await 之后的 SetBubbleText 没跑到，
        ///   气泡就会停在「正在读屏幕上写的字…」—— 现象只是「她没反应」，
        ///   与「功能坏了」无法区分。这一条专门抓它。
        ///
        /// ⚠⚠ 2026-09-20 加的第二组（用户实拍「她只看见了自己」之后）：
        ///   上面这些判据**全绿**，而功能是坏的 —— 因为「看见了」这三个字在她只看得见自己时
        ///   也照样出现（`vis.reportedWhatSheSaw` 只查了前缀）。这是本仓的老毛病又犯了一次：
        ///   **判据没把「现象的必要条件」覆盖全**。所以现在多了两条：
        ///     · `vis.selfForegroundConditionBuilt` —— 条件得**真造出来**（把前台抢到她自己身上，
        ///        这就是你点托盘那一刻的真实情形），造不出来必须报红，不许当成通过；
        ///     · `vis.readTargetIsNotSelf` —— 读数里「读的是谁」不许是她自己。
        ///   负对照 `--ocrvis --no-lastforeign` 必须让第二条红。
        /// </summary>
        public static int VisRun(Cli o)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            string model = Cli.ResolveModel(o.ModelPath);
            if (model == null) { Console.WriteLine("[ocrvis] 找不到模型（用 --model 指定）"); return 2; }
            GlbModel gm;
            try { gm = Glb.Load(model); }
            catch (Exception ex) { Console.WriteLine("[ocrvis] 读模型失败：" + ex.Message); return 3; }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var cfg = PetConfig.Load();
            cfg.SizeIndex = o.SizeIndex;
            cfg.NightDim = false;                 // 调光持续改 Opacity，与本次无关
            cfg.SpeechOn = false;                 // ⚠ 关掉自发采样：要的是「我点了一下」的反应，不是环境噪音
            // ⚠⚠ 显式固定，不沿用 config.json —— 否则这条判据的绿取决于你托盘里勾了什么。
            OcrEye.Enabled = true;
            OcrEye.RawTextNoNormalize = false;
            Watcher.AllowLastForeign = !o.NoLastForeign;

            var r = new WpfPetRenderer(gm);
            var w = new PetWindow(r, cfg) { ShowInTaskbar = false };
            w.SelfTestMode = true;
            // ⚠ 在窗口抢走前台**之前**先记一眼「你上一个真实在用的窗口」（生产里这是 SlowTick
            //   每 1 秒做一次的事）。不补这一眼，运行时就没有可回退的对象，下面那条判据会红得
            //   没有信息量 —— 那时候你分不清是「回退逻辑坏了」还是「本来就没有别的窗口」。
            Watcher.RememberForeground();
            w.Show();   // ⚠⚠ 少了这一句，窗口永远不 Loaded ⇒ OnLoaded 不跑 ⇒ **_brain 恒为 null**，
                        //   而气泡是**独立浮窗**（自己 Show 自己），所以它照样显示结果 ——
                        //   现象是「一切都对，只有占用状态是『无 brain』」。首跑就是这么红的。
            bool visible = false; string text = null, kind = null, proc0 = null, ownerBefore = null;
            bool forced = false; string closedReason = null, targetProc = null;
            string lastForeignAtClick = null;

            var tl = new DispatcherTimer(DispatcherPriority.Send);
            tl.Interval = TimeSpan.FromMilliseconds(40);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int phase = 0;
            double due = 0;
            IntPtr selfHwnd = IntPtr.Zero;

            // ⚠ 为什么要「抢前台」：我们要验的情形是**你点托盘那一刻** —— 前台是她自己。
            //   那条路没法在无人时复现，只能自己造。造法两步，第二步是 Windows 上的经典补法：
            //   把本线程的输入队列挂到当前前台线程上，就能被允许改前台（挂上后立刻摘掉）。
            void TakeForeground(IntPtr hwnd)
            {
                try { Native.SetForegroundWindow(hwnd); } catch { }
                if (Native.GetForegroundWindow() == hwnd) return;
                try
                {
                    IntPtr fg = Native.GetForegroundWindow();
                    int pid; uint fgThread = (uint)Native.GetWindowThreadProcessId(fg, out pid);
                    uint mine = Native.GetCurrentThreadId();
                    if (fgThread != 0 && fgThread != mine)
                    {
                        Native.AttachThreadInput(mine, fgThread, true);
                        try { Native.SetForegroundWindow(hwnd); } finally { Native.AttachThreadInput(mine, fgThread, false); }
                    }
                }
                catch { }
            }

            tl.Tick += (s, e) =>
            {
                try
                {
                    double t = clock.Elapsed.TotalMilliseconds;
                    if (t < due) return;
                    switch (phase)
                    {
                        case 0:
                            w.Pose.Freeze = true; phase = 1; due = t + 900;   // 等它摆稳
                            break;
                        case 1:
                            // 先让 1 秒一次的感知循环记下「你上一个真实在用的窗口」（这一步在真实使用里
                            // 由 PetWindow 自己每秒做），再把前台抢到她自己身上。
                            selfHwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                            Watcher.RememberForeground();
                            TakeForeground(selfHwnd);
                            phase = 2; due = t + 700;                         // 等前台真的换过来
                            break;
                        case 2:
                            var pr = Watcher.Probe();                          // ⚠ 此时前台应当是她自己
                            proc0 = pr == null ? null : pr.Item1;
                            lastForeignAtClick = Watcher.LastForeign == null ? null : Watcher.LastForeign.Proc;
                            forced = selfHwnd != IntPtr.Zero && Native.GetForegroundWindow() == selfHwnd;
                            ownerBefore = w.BubbleOwner;                       // 点之前她那一侧是什么状态（诊断用）
                            w.ShowScreenRead();                                // ← 托盘那一项点下去的**同一个方法**
                            phase = 3; due = t + 5000;                         // OCR 200–400 ms，给足余量
                            break;
                        default:
                            visible = w.BubbleVisible;
                            text = w.BubbleText;
                            kind = w.BubbleOwner;    // ⚠ 用 BubbleOwner 不用 BubbleKind：后者在气泡不可见时也返回 null，
                                                     //   会把「brain 是 null」和「气泡没显示」混成同一个读数
                            targetProc = w.LastScreenRead == null ? null : w.LastScreenRead.Proc;
                            closedReason = w.LastScreenRead == null ? "(没有读数)" : w.LastScreenRead.Why;
                            tl.Stop();
                            app.Shutdown();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[ocrvis] 测试自身异常：" + ex);
                    tl.Stop();
                    app.Shutdown();
                }
            };
            tl.Start();
            app.Run();

            var checks = new List<object>();
            bool ok = true;
            void Check(string name, bool pass, string detail)
            {
                if (!pass) ok = false;
                checks.Add(new Dictionary<string, object> { ["name"] = name, ["ok"] = pass, ["detail"] = detail ?? "" });
            }

            Check("vis.bubbleVisible", visible, visible ? "气泡确实弹出来了" : "气泡没出来（双击/托盘那条路断了）");
            Check("vis.bubbleKindIsStatus", kind == "status",
                "气泡种类 = " + (kind ?? "(无)")
                + (kind == "status" ? " ✅（读数是**状态**，不是她的台词）" : "（应当是 status；speech 会让读数被她正在说的话顶掉）"));
            Check("vis.bubbleNotStillLoading",
                !string.IsNullOrEmpty(text) && text.IndexOf("正在读屏幕上写的字", StringComparison.Ordinal) < 0,
                string.IsNullOrEmpty(text) ? "气泡文本是空的 ❌"
                    : (text.IndexOf("正在读屏幕上写的字", StringComparison.Ordinal) >= 0
                        ? "气泡停在「正在读屏幕上写的字…」—— OCR 的结果**再也没回来**（await 之后的 SetBubbleText 没跑到，或异常被吞）❌"
                        : "气泡已经换成了结果，不是加载中的那一句 ✅"));
            Check("vis.reportedWhatSheSaw", !string.IsNullOrEmpty(text) && text.IndexOf("看见了", StringComparison.Ordinal) >= 0,
                "气泡开头是「看见了：…」→「" + Clip(text, 90) + "」（前台 proc=" + (proc0 ?? "?") + "）");

            // ⚠⚠ 下面两条是 2026-09-20 补的 —— 因为上面四条**全绿而功能是坏的**：
            //   「看见了：pet … 没读」也含「看见了」三个字，前缀判据对它毫无办法。
            //   这就是本仓那条「判据必须覆盖现象的全部必要条件」。
            Check("vis.selfForegroundConditionBuilt", forced,
                forced
                    ? "已把前台抢到她自己的窗口上（proc0=" + (proc0 ?? "?") + "）—— 这就是你点托盘那一刻的真实情形，条件成立 ✅"
                    : "条件没造出来：前台仍不是她的窗口（proc0=" + (proc0 ?? "?") + "）。"
                      + "**这条判据这次没跑起来，不许当成通过** —— 现象是「前台是她自己时读谁」，"
                      + "没建立条件就无从验。报告里带了上一次真实读数（" + (lastForeignAtClick ?? "无") + "）供归因");

            bool targetIsSelf = string.IsNullOrEmpty(targetProc)
                                || string.Equals(targetProc, Watcher.SelfName(), StringComparison.OrdinalIgnoreCase);
            Check("vis.readTargetIsNotSelf", !targetIsSelf,
                targetIsSelf
                    ? "她读的是**她自己**（proc=" + (targetProc ?? "(没读数)") + "）—— 正是用户实拍的那个 bug："
                      + "「" + Clip(text, 70) + "」❌（负对照 --no-lastforeign 下这一条本来就会红；"
                      + "正常跑里红说明回退没生效）"
                    : "读的是「" + targetProc + "」，不是她自己 ✅（读数来源=" + (closedReason ?? "?") + "）");

            // 顺带把「她也可能只是没读到字」与「她读错了对象」分开：后者才是本案。
            Check("vis.bubbleDoesNotSaySelfOnly",
                string.IsNullOrEmpty(text) || text.IndexOf("只看得见自己", StringComparison.Ordinal) < 0,
                string.IsNullOrEmpty(text)
                    ? "气泡是空的（前面那条会红）"
                    : (text.IndexOf("只看得见自己", StringComparison.Ordinal) >= 0
                        ? "气泡里写着「我现在只看得见自己」❌ —— 这正是用户看到的那一屏「她只看见了自己」"
                        : "气泡没有出现「只看得见自己」的说法 ✅"));

            // ⚠ 负对照的跑法与其他几条**一致**：红是预期的，本跑报 OK 并打印出**目标判据的名字**。
            //   （「exit 1 ≠ 我那条判据红了」—— 所以必须点名，而不是只看退出码。）
            if (o.NoLastForeign)
            {
                Check("negControl_targetGoesRed", targetIsSelf,
                    targetIsSelf
                        ? "关掉回退后 vis.readTargetIsNotSelf 会红：她读的是她自己 —— 气泡「" + Clip(text, 60) + "」✅"
                          + "（因此上面那两条红是**预期**的，不是回归）"
                        : "关掉回退后 vis.readTargetIsNotSelf 仍然绿 ⇒ 那条判据没资格失败，它的绿没有信息量 ❌");
                ok = targetIsSelf;
            }

            Console.WriteLine();
            Console.WriteLine("---- 气泡里实际显示的（这就是你点托盘会看到的那一屏）----");
            Console.WriteLine(text ?? "(空)");
            Console.WriteLine("---- 完 ----");

            // ⚠ 红项要能被归因：把她的初始化轨迹一起落进报告。
            //   「气泡里有字、但占用状态是『无 brain』」这一对读数只有在能看到 TraceLog 时才好判。
            var trace = new List<string>();
            for (int i = 0; i < w.TraceN && i < w.TraceLog.Length; i++) trace.Add(w.TraceLog[i]);

            Write(new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["foregroundProcWhenClicked"] = proc0,
                ["selfForegroundForced"] = forced,
                ["lastForeignAtClick"] = lastForeignAtClick,
                ["readTargetProc"] = targetProc,
                ["readWhy"] = closedReason,
                ["noLastForeign"] = o.NoLastForeign,
                ["bubbleOwnerBeforeClick"] = ownerBefore,
                ["bubbleVisible"] = visible, ["bubbleKind"] = kind, ["bubbleText"] = text,
                ["traceN"] = w.TraceN, ["trace"] = trace,
                ["isLoaded"] = w.IsLoaded, ["renderedFrames"] = w.RenderedFrames,
                ["checks"] = checks,
            }, "azhu_ocrvis.json");

            Console.WriteLine("  （点之前占用=" + (ownerBefore ?? "?") + "，初始化轨迹 " + w.TraceN + " 条，"
                + "IsLoaded=" + w.IsLoaded + "，已渲染帧=" + w.RenderedFrames + "）");
            Console.WriteLine("  （前台造条件=" + (forced ? "成功" : "失败")
                + "，点之前最后见到的外来窗口=" + (lastForeignAtClick ?? "无")
                + "，读数来自=" + (targetProc ?? "无") + "）");
            foreach (string s in trace) Console.WriteLine("    · " + s);
            foreach (object c in checks)
            {
                var d = (Dictionary<string, object>)c;
                Console.WriteLine("  " + ((bool)d["ok"] ? "[v] " : "[x] ") + d["name"] + "：" + d["detail"]);
            }
            if (checks.Count == 0) { Console.WriteLine("  [x] 一项都没跑 —— 算 FAIL"); ok = false; }
            Console.WriteLine("ocrvis " + (ok ? "OK" : "FAIL")
                + (o.NoLastForeign ? "（负对照：只验「关掉回退后 readTargetIsNotSelf 会不会红」）" : "")
                + " —— 报告 " + Path.Combine(Path.GetTempPath(), "azhu_ocrvis.json"));
            return ok ? 0 : 1;
        }

        // ================================================================ 合成图

        /// <summary>
        /// 画一张白底黑字的图并**取出原始像素**。
        /// ⚠ 这里刻意**不编 JPEG** —— 与生产的 OCR 通路一致（那条路不吃有损压缩）。
        /// ⚠ 像素格式也必须与生产一致（Pbgra32／4 字节每像素），否则测的是另一条路。
        /// </summary>
        private static ScreenEye.Shot SynthText(string text)
        {
            try
            {
                const int W = 900, H = 220;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
                    var ft = new FormattedText(text, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"),
                        FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 44, Brushes.Black, 1.0);
                    dc.DrawText(ft, new Point(30, 70));
                }
                return Snapshot(dv, W, H, text);
            }
            catch { return null; }
        }

        /// <summary>纯色图（没有字）。用来验「读不到字」与「读错了」是两回事。</summary>
        private static ScreenEye.Shot SynthSolid(Color c, int w, int h)
        {
            try
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(0, 0, w, h));
                return Snapshot(dv, w, h, "纯色");
            }
            catch { return null; }
        }

        private static ScreenEye.Shot Snapshot(DrawingVisual dv, int w, int h, string title)
        {
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            int stride = w * 4;
            var px = new byte[stride * h];
            rtb.CopyPixels(px, stride, 0);
            return new ScreenEye.Shot
            {
                Pixels = px, PixelW = w, PixelH = h,
                SrcW = w, SrcH = h, OutW = w, OutH = h,
                Proc = "合成", Title = title, How = "RenderTargetBitmap",
            };
        }

        // ================================================================ 杂项

        /// <summary>临时目录里的图片文件数（验「不落盘」）。只数直接子项，够用且不会走很远。</summary>
        private static int CountImages(string dir)
        {
            int n = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                {
                    string e = Path.GetExtension(f).ToLowerInvariant();
                    if (e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".gif") n++;
                }
            }
            catch { }
            return n;
        }

        private static string Clip(string s, int n = 60)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }

        private static void Write(Dictionary<string, object> report, string file)
        {
            string outPath = Path.Combine(Path.GetTempPath(), file);
            try
            {
                File.WriteAllText(outPath,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                using (var doc = JsonDocument.Parse(File.ReadAllText(outPath, Encoding.UTF8))) { }
            }
            catch (Exception ex) { Console.WriteLine("写出的 JSON 自己解析不了：" + ex.Message); }
        }

        private static int Report(bool ok, List<object> checks, bool negative, int? ms, string negTag = null)
        {
            // ⚠ 负对照与正常判据**不许共用一个报告文件**：谁后有谁覆盖，
            //   而负对照的证据正是那几条判定值。
            // ⚠ 三个负对照（归一化／回退／门）也**各自一个文件** —— 共用一个的话，
            //   连着跑三条时只有最后一条的证据留下来，而「哪一条红过」恰恰是唯一要看的东西。
            string file = negative
                ? "azhu_ocrtest_neg" + (string.IsNullOrEmpty(negTag) ? "" : "_" + negTag) + ".json"
                : "azhu_ocrtest.json";
            Write(new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["normalizeOff"] = OcrEye.RawTextNoNormalize,
                ["lang"] = OcrEye.LangTag,
                ["readMs"] = ms.HasValue ? (object)ms.Value : null,
                ["checks"] = checks,
            }, file);

            string outPath = Path.Combine(Path.GetTempPath(), file);
            Console.WriteLine("ocrtest " + (ok ? "OK" : "FAIL") + " —— " + checks.Count + " 项检查，报告 " + outPath
                + (negative ? "（负对照）" : ""));
            // ⚠ 红项必须报出名字 —— 只给一个 exit 1，分不清是不是目标判据红的。
            foreach (object c in checks)
            {
                var d = (Dictionary<string, object>)c;
                if (!(bool)d["ok"]) Console.WriteLine("  [x] " + d["name"] + "：" + d["detail"]);
            }
            if (checks.Count == 0) { Console.WriteLine("  [x] 一项都没跑 —— 算 FAIL"); ok = false; }
            return ok ? 0 : 1;
        }
    }
}
