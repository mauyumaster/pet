// 入口。三种跑法：
//   pet.exe                 —— 正常桌宠
//   pet.exe --selftest      —— 自检：几何／姿态幅度／逐像素命中／全屏隐退／截图，写 JSON
//   pet.exe --probe <file>  —— 探针（被 --clicktest 拉起，用来验证「点击真的穿到下层窗口了」）
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;

namespace AzhuPet
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Cli o = Cli.Parse(args);

            // ==== 自更新：先「兑现」上次下载好的新版本，再干别的 ====
            // ⚠⚠ 必须是**第一件事**（在任何窗口、任何文件读取之前）：
            //   替换会改掉 pet.exe / persona.md 这两个文件本身，趁没人碰过它们时做最干净。
            // ⚠ 自检模式（--xxxtest）**不执行**替换 —— 判据必须是无副作用的，
            //   跑一次测试就把用户的程序换了，那是灾难。
            // ⚠ --applyupdate 是给「用户点了「立即重启更新」」用的显式入口：
            //   那种情况下要报清楚结果（更换了什么、失败原因）。
            if (o.ApplyUpdate) return RunApplyUpdate();
            // --version：**必须最先处理且只打印版本号这一行**。
            // ⚠⚠ 用 `Write` + 显式 "\n"，**不用 `WriteLine`** ——
            //   WriteLine 在 Windows 上输出 CRLF，而 pack-release.cmd 用 `for /f` 抓它。
            //   cmd 的 `for /f` 对行尾 CR 的处理依上下文而变，一旦 CR 被带进变量，
            //   生成的文件名就变成 `AzhuPet-v0.1.0␍-win-x64.zip` —— **看起来几乎一样**，
            //   但 zip 名、version.json 里的 url、以及 git 提交的文件名全都会带上这个隐形字符。
            //   这类「看不见的字符导致的失配」在本项目出现过多次，所以从源头掐掉：
            //   只输出 LF，让 for /f 无论怎么处理都拿到干净的一行。
            // 另外：**只准打这一行**，多打一行会被一起抓进变量。
            if (o.Version)
            {
                var so = Console.OpenStandardOutput();
                byte[] bytes = Encoding.ASCII.GetBytes(Updater.CurrentVersion + "\n");
                so.Write(bytes, 0, bytes.Length);
                so.Flush();
                return 0;
            }
            if (!o.AnyTest()) ApplyPendingQuietly();

            // 模型覆盖必须在任何 ChatAsync 之前生效（四条通路共用这一个开关）。
            if (!string.IsNullOrEmpty(o.LlmModel)) TraeChat.ModelOverride = o.LlmModel;
            // 「关思考」开关同理：必须在任何 ChatAsync 之前定下来。
            // ⚠ `--old-openai-body` 是**负对照**：回到 2026-09-29 之前的请求体（不发 thinking），
            //   好让 --speaktest 里那条判据能红。
            TraeChat.SendThinkingDisabled = !o.OldOpenAiBody;
            // 限流重试同理（2026-09-29 加）：`--no-rate-retry` 是**负对照** —— 关掉重试就是用户那天
            // 撞上 1305 时的原始行为（一次都不重试，直接把 429 甩到聊天窗里）。判据必须因此变红。
            if (o.NoRateRetry) TraeChat.RateLimitRetries = 0;
            // 多行台词（2026-09-29 用户拍板「多行输出也可以」）：`--no-multiline` 是**负对照** ——
            //   回到「含换行即整句丢弃」的旧行为，好让 --speaktest 里那条判据能红。
            //   与上面两条同理：必须在任何 SayAsync 之前定下来。
            if (o.NoMultiline) LlmSpeaker.AllowMultiline = false;
            // 说话频率的两条钳制（2026-09-29）：`--no-freq-clamp` 是**负对照** —— 跳过
            //   「保底间隔 ≥ 开口冷却」与冷却下限，把配置里的分钟数原样当秒数用，
            //   好让 --speaktest 里那几条频率判据能红。
            SpeechFreq.NoClamp = o.NoFreqClamp;
            // ⚠⚠ 判据模式**不许写用户的真配置** —— 见 PetConfig.RealWriteAllowed 的事故记录
            //   （跑一次 --lifttest 就把 config.json 覆盖成空的，用户的 GLM key 丢了）。
            //   放行的三类：本来就操作用户配置的入口（--fixconfig／--settings），以及 --llmtest
            //   （它的 --openai-* 参数按设计会落盘）；`--allow-config-write` 是负对照。
            PetConfig.RealWriteAllowed = !o.AnyTest()
                || o.AllowConfigWrite || o.FixConfig || o.Settings || o.BalanceSettings || o.LlmTest;
            // 通道路由也必须在这里接上 —— 见 WireOpenAi 的注释：原先只挂在 RunNormal 里，
            // 于是 `--llmtest` 测的是 Trae 通道（「测错对象的绿」）。
            WireOpenAi(o, null);
            WpfPetRenderer.MatMode = o.Mat == "flat" ? 1 : o.Mat == "emissive" ? 2 : o.Mat == "uv" ? 3 : 0;
            Glb.VFlip = o.VFlip;
            PetWindow.ForceHitThrough = o.ForceThrough;
            PetWindow.MotionTest_OldGate = o.OldFrameGate;   // 负对照：见 PetWindow.OnRender 的注释
            // 多 agent 任务计时：判据模式下关掉。理由不是「省事」而是**判据的输入必须自己钉死** ——
            // 跑判据的人往往正开着 TRAE/WorkBuddy，只要那边有任务在跑，判据窗口里就会凭空多出常驻读数行，
            // 把气泡流的宽／高判据整个带偏。
            AgentTaskTimer.Enabled = !o.AnyTest();
            // apphost 的副本命名为 pet-settings.exe 时可无参数直达设置面板，便于桌面快捷方式与 UI 验收。
            // ⚠⚠ 必须带 `!o.Settings && !o.BalanceSettings` 守卫 —— 否则 `pet.exe --settings` 会被这一条
            //   **抢走**，落到另一条分支上，命令行开关形同虚设。
            //   判据 `settingstest` 只测 SettingsWindow 这个类，测不到「入口被抢走」，
            //   所以这个顺序问题只能靠视觉验收发现 —— 第一轮就是这么栽的。
            // ⚠ 2026-10-01 起余额不再是独立窗口：这条兜底也直接开设置面板（并停在余额那一栏）。
            if (o.Settings == false && o.BalanceSettings == false
                && (System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "")
                .IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0)
                return RunSettings(o, SettingsWindow.BalancePageTitle);
            if (o.ProbeFile != null) return Probe.Run(o);
            if (o.ScreenCapPath != null) return Shot(o.ScreenCapPath, o.ScreenCapCrop);
            if (o.PixDir != null) return PixelReport.Run(o);
            if (o.SelfTest) return SelfTest.Run(o);
            if (o.ClickTest) return ClickTest.Run(o);
            if (o.DragTest) return DragTest.Run(o);
            if (o.SpinTest) return SpinTest.Run(o);
            if (o.Chat != null) return ChatOneShot(o.Chat);
            if (o.PersonaTest) return PersonaTest.Run(o);
            if (o.WatchTest) return WatchTest.Run(o);
            if (o.WatchProbe) return WatchTest.ProbeRun(o);
            if (o.ShellProbe) return WatchTest.ShellProbeRun(o);
            if (o.FsTest) return FsTest.Run(o);
            if (o.FsProbe) return FsTest.ProbeRun(o);
            if (o.SpeakTest) return SpeakTest.Run(o);
            if (o.LlmTest) return SpeakTest.LlmRun(o);
            if (o.SpeakProbe) return SpeakTest.ProbeRun(o);
            if (o.SpeakVis) return SpeakTest.VisRun(o);
            if (o.EyeTest) return EyeTest.Run(o);
            if (o.OcrTest) return OcrTest.Run(o);
            if (o.OcrProbe) return OcrTest.ProbeRun(o);
            if (o.OcrVis) return OcrTest.VisRun(o);
            if (o.StatusTest) return StatusTest.Run(o);
            if (o.CaliberTest) return CaliberTest.Run(o);
            if (o.TraeExtTest) return TraeExtTest.Run(o);
            if (o.TraeExtInstall) return TraeExtInstaller.InstallRun();
            if (o.TraeExtAutoDiag) return TraeExtAuto.DiagRun();
            if (o.BubbleTest) return BubbleTest.Run(o);
            if (o.BalanceConfigTest) return BalanceConfigTest.Run();
            if (o.WebTest) return WebTest.Run();
            // --hookscript：把真正注入页面的那段 JS 原样打出来。
            // ⚠ 它存在的理由：钩子脚本有语法错时，AddScriptToExecuteOnDocumentCreatedAsync **不报错**
            //   （那一步只是「注册脚本」），于是钩子静默失效 —— 现象与「没抄到请求」一模一样，
            //   而这条路径偏偏只能靠「登一次看看」来发现。所以脚本得能被单独拿去做语法+行为检查：
            //   tools/hook_check.js（node，造一个最小浏览器环境真跑一遍）。排障时也能直接看它。
            if (o.HookScript) return RunHookScriptDump();
            if (o.ConfigTest) return ConfigTest.Run(o);
            if (o.FixConfig) return ConfigFix.Run(o);
            if (o.UpdateTest) return UpdateTest.Run(o);
            if (o.UpdateDiag) return UpdateTest.DiagRun(o);
            if (o.SummaryTest) return SummaryTest.Run(o);
            if (o.SummaryNow) return SummaryTest.NowRun(o);      // 真调模型一次，总结「现在往前 60 分钟」
            if (!string.IsNullOrEmpty(o.InstallBalanceTemplate)) return BalanceTemplateInstaller.Run(o.InstallBalanceTemplate);
            if (o.BalanceSettings) return RunSettings(o, SettingsWindow.BalancePageTitle);
            if (o.SettingsTest) return SettingsTest.Run(o);
            if (o.TopmostTest) return TopmostTest.Run();
            if (o.MotionTest) return MotionTest.Run(o);
            if (o.LiftTest) return LiftTest.Run(o);
            if (o.AgentTimerTest) return AgentTimerTest.Run(o);
            if (o.Settings) return RunSettings(o);
            return RunNormal(o);
        }

        /// <summary>--hookscript：原样输出注入页面的脚本（纯 ASCII）——**两段**，中间用
        /// <see cref="ScriptSectionMark"/> 分隔：①钩子脚本 ②页面内取数脚本（BuildFetchScript 的样品）。
        /// ⚠ 用 Write 不用 WriteLine，且**不多打任何一行说明文字** —— 输出会被直接喂给 node 当脚本跑，
        ///   多一行提示文字就会变成「语法错误」，把真问题淹掉。
        /// ⚠ 取数脚本也要能被真跑一遍，理由与钩子同源：它是**浏览器**执行的东西，C# 编译器看不见它。
        ///   它在这条路上比钩子更要紧 —— 钩子坏了只是抄不到，取数脚本坏了是「一个字都没发出去」。
        ///   两段共用一个旗标（而不是各开一个），是为了不动 pack-release.cmd 里那条已经验过的管道。</summary>
        private static int RunHookScriptDump()
        {
            Console.Write(CredentialCapture.BuildHookScript(CredentialCapture.WorkbuddyCapturePattern));
            Console.Write(ScriptSectionMark);
            Console.Write(CredentialCapture.BuildFetchScript(
                "https://example.com/api/meter?x=1", "POST", FetchScriptSampleBody, "__azhuCheck"));
            return 0;
        }

        /// <summary>两段脚本之间的分隔行（JS 注释写法，被人眼看到时也是可读的）。</summary>
        public const string ScriptSectionMark = "/*__AZHU_SECTION__*/\n";

        /// <summary>给取数脚本用的样品 body：**刻意带上一个内层引号**，这样「C# → JS 注入」
        /// 这一跳有没有把引号弄坏，能被 node 那边逐字符断言出来（不用中文：默认 JSON 编码器会把
        /// 非 ASCII 写成 \uXXXX，拿它验转义只会得到一条永远红的判据）。</summary>
        public const string FetchScriptSampleBody = "{\"q\":\"a\\\"b\",\"n\":1}";

        /// <summary>正常启动时的静默兑现：如果上次下好了新版本，在这里换掉。**不弹任何东西。**
        /// 失败就当作没发生 —— 下次启动再试。用户不该因为更新失败而看到错误对话框弹在桌宠上。</summary>
        private static void ApplyPendingQuietly()
        {
            try
            {
                if (Updater.PendingVersion() == null) return;
                string detail;
                Updater.ApplyPending(out detail);
                Updater.CleanupOldFiles();     // 顺手清掉上次替换留下的 .old（这次删不掉就留着）
            }
            catch { }
        }

        /// <summary>--applyupdate：显式兑现一次待替换版本，并把结果打到控制台。
        /// 给「设置面板点了立即更新」和排障用。返回值：0 = 换了，1 = 没得换/换失败。</summary>
        private static int RunApplyUpdate()
        {
            string pending = Updater.PendingVersion();
            if (pending == null)
            {
                Console.WriteLine("没有待替换的版本。当前版本 " + Updater.CurrentVersion);
                return 1;
            }
            Console.WriteLine("当前版本 " + Updater.CurrentVersion + " → 待替换 " + pending);
            string detail;
            bool ok = Updater.ApplyPending(out detail);
            Console.WriteLine((ok ? "✅ " : "❌ ") + detail);
            if (ok)
            {
                Console.WriteLine("下次启动即为新版本。");
                Updater.CleanupOldFiles();
            }
            return ok ? 0 : 1;
        }

        private static int ChatOneShot(string text)
        {
            // 人格前缀由 TraeChat 在发送边界注入（Persona.WithSystem），所以这条自检路径
            // 天然带人格 —— 这正是把它做成机制而不是叮嘱的好处：调用方不会忘。
            var msg = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } },
                },
            };
            var r = TraeChat.ChatAsync(msg).GetAwaiter().GetResult();
            string breadcrumb = Path.Combine(Path.GetTempPath(), "trae_chat_selftest.txt");
            File.WriteAllText(breadcrumb, (r.Ok ? "OK:" : "ERR:") + r.Text);
            return r.Ok ? 0 : 1;
        }

        /// <summary>--settings：独立打开设置主面板。
        /// 为什么要它：设置面板平时只能从托盘进 —— 而托盘菜单在截屏／自动化里够不着，
        /// 「面板长什么样」就变成只能靠人眼验收的东西。这条入口把它变成可复现的一步。
        /// ⚠ 面板需要一个 PetWindow 才能读写 Cfg 并调 ApplyConfig；这里按 RunNormal 的同一方式
        ///   造一个**不显示的**宿主窗口（同一个渲染器类型、同一份 PetConfig 真值，不产生第二份配置）。
        /// ⚠ `page`：打开后直接停在某一栏（按**标题**，不是下标 —— 见 ShowPageByTitle）。
        ///   `--balance-settings` 与 `pet-settings.exe` 都靠它直达余额那一栏；
        ///   2026-10-01 之前它们是「另开一个 WPF 余额窗」，现在只是同一条路的两个入口。</summary>
        /// <summary>--screencap &lt;路径&gt;：把整个虚拟屏抓成 PNG。
        ///
        /// 为什么要有它：设置面板／气泡这类东西**只有人眼能验收** —— `--settings` 解决了
        ///   "怎么把面板调出来"，这一条解决"怎么把它拍下来"。两件配齐，UI 改动才有一条
        ///   可复现的证据链，而不是只能靠"我刚打开看了一眼，没问题"。
        /// ⚠ 名字不是 `--shot`：那个已被 `--selftest` 占用（它抓的是**桌宠窗口**那一块，
        ///   见 SelfTest 里 `--shot-crop` 的用法）。两个是不同的东西，名字也必须不同。
        /// ⚠ 典型用法是**两个进程**：先 `pet.exe --balance-settings` 把面板起起来，
        ///   隔几秒再 `pet.exe --screencap out.png` —— 拍到的就是真实那个面板。
        /// ⚠ 抓的是整屏（含其它窗口），所以拍之前先把目标置于最前。
        ///   要「只画某个窗口、天然不含遮挡物」该用 `PrintWindow`（见 ScreenEye.cs），那是另一件事：
        ///   它画不到某些合成方式渲染的窗口，而 CopyFromScreen 画得到、但会连遮挡一起拍。
        /// ⚠ 用 `VirtualScreen` 而不是 `PrimaryScreen`：面板被拖到副屏时主屏截图是**空白**的，
        ///   而那种失败看起来像"窗口没起来"，会把人带偏。
        /// ⚠ `crop`（`--screencap-crop x,y,w,h`）：一张 2560×1440 的整屏图缩到能看的尺寸之后，
        ///   面板里那几行字就看不清了 —— 而"看不清"正是我加这个开关要解决的问题。裁剪矩形会先
        ///   与位图求交（越界不抛，只是变小）；坐标是**虚拟屏左上角为原点**。</summary>
        private static int Shot(string path, int[] crop)
        {
            try
            {
                var b = System.Windows.Forms.SystemInformation.VirtualScreen;
                using (var bmp = new System.Drawing.Bitmap(b.Width, b.Height))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(b.Left, b.Top, 0, 0, new System.Drawing.Size(b.Width, b.Height));

                    if (crop != null && crop.Length == 4)
                    {
                        var r = new System.Drawing.Rectangle(crop[0], crop[1], crop[2], crop[3]);
                        r.Intersect(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height));
                        if (r.Width > 0 && r.Height > 0)
                        {
                            using (var c = bmp.Clone(r, bmp.PixelFormat))
                                c.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                            Console.WriteLine("screencap -> " + path + "（裁 " + r.X + "," + r.Y + " " + r.Width + "×" + r.Height + "）");
                            return 0;
                        }
                        Console.WriteLine("screencap：裁剪矩形与屏幕不相交（" + crop[0] + "," + crop[1] + " " + crop[2] + "×" + crop[3]
                            + "），按整屏保存");
                    }
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine("screencap -> " + path + "（" + b.Width + "×" + b.Height + "）");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("screencap 失败：" + ex.Message);
                return 1;
            }
        }

        private static int RunSettings(Cli o, string page = null)
        {
            string model = Cli.ResolveModel(o.ModelPath);
            if (model == null) { MessageBox.Show("找不到 model/chibi_maid_pet.glb", "阿助桌宠"); return 2; }
            GlbModel gm;
            try { gm = Glb.Load(model); }
            catch (Exception ex) { MessageBox.Show("读取模型失败：" + ex.Message, "阿助桌宠"); return 3; }

            // ⚠⚠ 这里**不能**用 WPF 的 `app.Run()` 去驱动一个 WinForms 窗体（真实事故，2026-09-20）：
            //   设置面板（SettingsWindow）是 **WinForms Form**，而 PetWindow 是 WPF Window。
            //   若用 `new System.Windows.Application` + `app.Run()`，跑的是 WPF 的 Dispatcher 循环，
            //   WinForms 的 `Form.Show()` 没有消息泵去驱动它 ——
            //   现象极具迷惑性：**进程活着、内存涨到 250MB、不报任何错，但一个窗口都不出现**，
            //   然后 `app.Run()` 静默返回、进程退出（退出码 0）。
            //   ⇒ 判据是「窗口/进程都对了」这种假绿：它从不抛异常，只是什么都不做。
            //   正确做法：WinForms 窗体就用 **WinForms 的消息循环**（`System.Windows.Forms.Application.Run`）。
            //   不需要 WPF Application 实例 —— WpfPetRenderer 只是被 PetWindow 持有着，
            //   面板只用到它的 `Cfg` 与 `ApplyConfig()`，不依赖 Dispatcher 在转。
            // ⚠ 这一条**没有**运行中的桌宠，所以那边「自动补投」的勾选框写的是磁盘上的那份配置
            //   （`PetConfig.Load()` 这一份）。正在跑的桌宠要等下次启动才读到新值 ——
            //   从托盘打开的那条路（传的是**它在用的** `Cfg`）才是当场生效的。
            var r = new WpfPetRenderer(gm);
            var host = new PetWindow(r, PetConfig.Load());
            host.Show();
            host.Hide();                    // 只要它的 Cfg 与 ApplyConfig，不要它出现在桌面上
            GC.KeepAlive(r);

            var w = new SettingsWindow(host, page);
            System.Windows.Forms.Application.Run(w);
            return 0;
        }

        private static int RunNormal(Cli o)
        {
            bool created;
            using (var mtx = new Mutex(true, "AzhuPet.SingleInstance.v1", out created))
            {
                if (!created) return 0;                 // 已经在跑了，静默退出

                string model = Cli.ResolveModel(o.ModelPath);
                if (model == null)
                {
                    MessageBox.Show("找不到 model/chibi_maid_pet.glb\r\n可以用 --model <路径> 指定。", "阿助桌宠");
                    return 2;
                }

                var cfg = PetConfig.Load();
                cfg.SizeIndex = o.SizeIndex;
                // 自启自愈：只在注册表里那个旧目标**已不存在**时才纠正（理由见 PetConfig.HealAutostartIfOn）。
                // 放在窗口出现之前 —— 它只碰注册表，且内部吞掉全部异常，失败不影响启动。
                PetConfig.HealAutostartIfOn();
                // D 档（文字档）两个开关落到进程内的静态位上（与 ScreenEye.Enabled 同一种接法）。
                OcrEye.Enabled = cfg.OcrOn;
                OcrEye.SendText = cfg.OcrSendText;
                // ⚠ 只影响本次运行、不落盘 —— 用来当场试「带上屏幕上的字她会不会说得更像回事」。
                //   要长期打开请用托盘那个勾选项（它改的是 cfg 并 Save()）。
                if (o.SpeakScreen) OcrEye.SendText = true;
                // 吐槽通道与自适应阈值：负对照开关在这里落到静态位（判据打进 --speaktest/--watchtest）。
                WatchLoop.RoastOn = cfg.RoastOn && !o.NoRoast;
                Watcher.AdaptiveOn = !o.NoAdaptive;
                // DeepSeek key：命令行 > 环境变量 > 已存配置；命令行/env 给的新值会落盘
                string key = !string.IsNullOrEmpty(o.DeepSeekKey)
                    ? o.DeepSeekKey
                    : Environment.GetEnvironmentVariable("AZHU_DEEPSEEK_KEY");
                if (!string.IsNullOrEmpty(key) && key != cfg.DeepSeekKey)
                {
                    cfg.DeepSeekKey = key;
                    cfg.Save();
                }

                // 自定义余额接口：命令行 > 环境变量 > 已存配置；新值会落盘
                bool balChanged = false;
                balChanged |= SetCfg(cfg, v => cfg.BalanceUrl = v, o.BalanceUrl, "AZHU_BALANCE_URL");
                balChanged |= SetCfg(cfg, v => cfg.BalanceToken = v, o.BalanceToken, "AZHU_BALANCE_TOKEN");
                balChanged |= SetCfg(cfg, v => cfg.BalanceHeader = v, o.BalanceHeader, "AZHU_BALANCE_HEADER");
                balChanged |= SetCfg(cfg, v => cfg.BalanceKey = v, o.BalanceKey, "AZHU_BALANCE_KEY");
                balChanged |= SetCfg(cfg, v => cfg.BalanceUnit = v, o.BalanceUnit, "AZHU_BALANCE_UNIT");
                // OpenAI 兼容通道 + 通道路由接线：**只有一处**（见 WireOpenAi 的注释）。
                // 早段已经用 null 调过一次（为了让 --llmtest 也接上）；这里带上真正的 cfg 再走一遍，
                // 好让下面 new PetWindow(..., cfg) 拿到的实例与落盘后的状态一致。
                WireOpenAi(o, cfg);

                GlbModel gm;
                try { gm = Glb.Load(model); }
                catch (Exception ex) { MessageBox.Show("读取模型失败：" + ex.Message, "阿助桌宠"); return 3; }

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var r = new WpfPetRenderer(gm);
                var w = new PetWindow(r, cfg);
                if (!o.NoTray) w.Menu = new PetMenu(w, true);
                w.Closing += (s, e) => { if (w.Menu != null) w.Menu.Dispose(); };
                w.Show();
                GC.KeepAlive(r);
                return app.Run();
            }
        }

        /// <summary>取「命令行 > 环境变量」的值写入配置。**有值就返回 true**（＝该落盘）——
        /// ⚠ 注意它并不比较「与现配置是否不同」（注释以前这么写过，与实现不符，已改正）：
        ///   也就是说命令行/环境变量一旦给了值，每次启动都会重写一次 config.json。代价可忽略，保留现行为。</summary>
        private static bool SetCfg(PetConfig cfg, Action<string> set, string cliValue, string envName)
        {
            string v = !string.IsNullOrEmpty(cliValue) ? cliValue : Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrEmpty(v)) return false;
            set(v);
            return true;
        }

        /// <summary>接上「和桌宠说话」的通道路由：命令行 &gt; 环境变量 &gt; 已存配置（新值落盘）。
        /// ⚠⚠ 抽成一个方法、并且**在 Program.Main 的早段无差别调用**，理由是本轮挖出来的真 bug：
        ///   原先这段只写在 <c>RunNormal</c> 里（第 292 行），而 <c>--llmtest</c> 在 Switch 更早处就 return 了
        ///   ⇒ 那时 <c>TraeChat.OpenAiSource</c> 仍是 null，**--llmtest 测的是 Trae 中转通道**。
        ///   后果极坏：用户按提示填好 GLM/DeepSeek 的 base+key，跑 --llmtest 得到一个绿色，
        ///   而那条绿跟新通道毫无关系 —— 「测错对象的绿」（2026-09-29）。
        ///   接线必须**只有一处**，运行期与判据同源。</summary>
        private static void WireOpenAi(Cli o, PetConfig cfg)
        {
            cfg = cfg ?? PetConfig.Load();
            bool changed = false;
            // key 走 DeepSeekKey 字段（见 PetConfig.OpenAiBase 的注释）
            changed |= SetCfg(cfg, v => cfg.OpenAiBase = v, o.OpenAiBase, "AZHU_OPENAI_BASE");
            changed |= SetCfg(cfg, v => cfg.OpenAiModel = v, o.OpenAiModel, "AZHU_OPENAI_MODEL");
            changed |= SetCfg(cfg, v => cfg.DeepSeekKey = v, o.DeepSeekKey, "AZHU_DEEPSEEK_KEY");
            // 备用通道（可选）：仅当用户填了 base+key 才生效；不填 = 行为与新增前完全一致。
            changed |= SetCfg(cfg, v => cfg.FallbackBase = v, o.FallbackBase, "AZHU_FALLBACK_BASE");
            changed |= SetCfg(cfg, v => cfg.FallbackModel = v, o.FallbackModel, "AZHU_FALLBACK_MODEL");
            changed |= SetCfg(cfg, v => cfg.FallbackKey = v, o.FallbackKey, "AZHU_FALLBACK_KEY");
            if (changed) cfg.Save();

            // 闭包每次现读配置 —— 配置窗／config.json 改完**即时生效**，不用重启。
            // （config.json 只有几 KB，消息频率又是分钟级；正确性优先于这点 IO。）
            TraeChat.OpenAiSource = () =>
            {
                var c = PetConfig.Load();
                return Tuple.Create(c.OpenAiBase, c.DeepSeekKey, c.OpenAiModel);
            };
            // 备用通道用**同一个**「现读配置」模式：主通道被限流时才会被读到，空值即不用。
            TraeChat.FallbackSource = () =>
            {
                var c = PetConfig.Load();
                return Tuple.Create(c.FallbackBase, c.FallbackKey, c.FallbackModel);
            };
        }
    }
}
