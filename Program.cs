// 入口。三种跑法：
//   pet.exe                 —— 正常桌宠
//   pet.exe --selftest      —— 自检：几何／姿态幅度／逐像素命中／全屏隐退／截图，写 JSON
//   pet.exe --probe <file>  —— 探针（被 --clicktest 拉起，用来验证「点击真的穿到下层窗口了」）
using System;
using System.Collections.Generic;
using System.IO;
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
            // 模型覆盖必须在任何 ChatAsync 之前生效（四条通路共用这一个开关）。
            if (!string.IsNullOrEmpty(o.LlmModel)) TraeChat.ModelOverride = o.LlmModel;
            WpfPetRenderer.MatMode = o.Mat == "flat" ? 1 : o.Mat == "emissive" ? 2 : o.Mat == "uv" ? 3 : 0;
            Glb.VFlip = o.VFlip;
            PetWindow.ForceHitThrough = o.ForceThrough;
            // apphost 的副本命名为 pet-settings.exe 时可无参数直达配置中心，便于桌面快捷方式与 UI 验收。
            if ((System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "")
                .IndexOf("settings", StringComparison.OrdinalIgnoreCase) >= 0)
                return RunBalanceSettings();
            if (o.ProbeFile != null) return Probe.Run(o);
            if (o.PixDir != null) return PixelReport.Run(o);
            if (o.SelfTest) return SelfTest.Run(o);
            if (o.ClickTest) return ClickTest.Run(o);
            if (o.DragTest) return DragTest.Run(o);
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
            if (o.BubbleTest) return BubbleTest.Run(o);
            if (o.BalanceConfigTest) return BalanceConfigTest.Run();
            if (o.SummaryTest) return SummaryTest.Run(o);
            if (o.SummaryNow) return SummaryTest.NowRun(o);      // 真调模型一次，总结「现在往前 60 分钟」
            if (!string.IsNullOrEmpty(o.InstallBalanceTemplate)) return BalanceTemplateInstaller.Run(o.InstallBalanceTemplate);
            if (o.BalanceSettings) return RunBalanceSettings();
            return RunNormal(o);
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

        private static int RunBalanceSettings()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            // 独立配置进程可能与正在置顶的桌宠同时存在；保持在它上面，避免操作按钮被挡住。
            var w = new BalanceSettingsWindow(null) { Topmost = true };
            app.MainWindow = w;
            w.Show();
            return app.Run();
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
                // OpenAI 兼容通道：命令行 > 环境变量 > 已存配置（key 走 DeepSeekKey 字段，见 PetConfig 注释）
                bool oaChanged = false;
                oaChanged |= SetCfg(cfg, v => cfg.OpenAiBase = v, o.OpenAiBase, "AZHU_OPENAI_BASE");
                oaChanged |= SetCfg(cfg, v => cfg.OpenAiModel = v, o.OpenAiModel, "AZHU_OPENAI_MODEL");
                oaChanged |= SetCfg(cfg, v => cfg.DeepSeekKey = v, o.DeepSeekKey, "AZHU_DEEPSEEK_KEY");
                if (oaChanged) cfg.Save();

                // 通道路由接线：闭包每次现读配置 —— 配置窗／config.json 改完**即时生效**，不用重启。
                // （config.json 只有几 KB，消息频率又是分钟级；正确性优先于这点 IO。）
                TraeChat.OpenAiSource = () =>
                {
                    var c = PetConfig.Load();
                    return Tuple.Create(c.OpenAiBase, c.DeepSeekKey, c.OpenAiModel);
                };

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

        /// <summary>取「命令行 > 环境变量」的值写入配置；有值且与现配置不同则返回 true（该落盘）。</summary>
        private static bool SetCfg(PetConfig cfg, Action<string> set, string cliValue, string envName)
        {
            string v = !string.IsNullOrEmpty(cliValue) ? cliValue : Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrEmpty(v)) return false;
            set(v);
            return true;
        }
    }
}
