// 配置与命令行 —— 桌面宠物也是个常驻程序，设置必须落盘、必须能自启。
using System;
using System.IO;
using System.Text;

namespace AzhuPet
{
    internal sealed class PetConfig
    {
        public int SizeIndex = 1;        // 0 小 / 1 中 / 2 大
        public bool Topmost = true;
        public bool NightDim = true;
        public bool ShowTray = true;
        // ---- 观察型的两道总开关（与「同一份数据两个落点」同族：只留这一处真值）----
        public bool SpeechOn = true;      // 她会**自己**开口（关了＝只在你打字时说话）
        public bool SpeechLlm = false;    // 台词由真模型生成（默认 false：模板台词零成本、逐字节可预测）
        // ⚠⚠ L4（像素级）：她能不能**看屏幕**。默认 false 不是保守，是这一档的**性质不同** ——
        //   A 档（进程名）与 B 档（元素树）都不出本机；开了它，**屏幕内容会发出去**。
        //   不可逆的事不能靠口头承诺 ⇒ 落成一个必须手动打开的开关。
        public bool EyeOn = false;
        // ⚠⚠ D 档（文字档）的**两个**开关，各管一件事，不许合并成一个：
        //   OcrOn       —— 允许她在**本机**读屏幕上的字（默认开：不出本机、不要模型、不要钱）。
        //   OcrSendText —— 允许把读到的**原文**放进发给模型的提示（**默认关**：这一步把屏幕内容送出去）。
        //   合并的后果是「想让本地判断生效」不得不连外发一起打开 —— 那就不是开关了。
        public bool OcrOn = true;
        public bool OcrSendText = false;
        // ---- 2026-09-20 用户拍板的三件事（「时不时吐槽」＋「每小时总结」）----
        // ⚠ RoastOn 是**吐槽总开关**。它在哪个档位下没作用：SpeechOn 关时整个自发说话都停
        //   （含吐槽）；OcrSendText 关时她吐槽不到**内容**，只能对着应用名与待的时长说。
        public bool RoastOn = true;
        // 小时总结：按「开机后每满 60 分钟」触发（用户拍板：不对齐全点），真模型生成，
        // 写进 Obsidian 库 `40 Projects/阿助（桌宠）/她写的/YYYY-MM-DD.md`（按天一个文件、按小时分节）。
        public bool SummaryOn = true;
        public double SummaryIntervalMin = 60;
        // ⚠ 库路径写进**本机配置**（%LOCALAPPDATA%，不进同步目录）：她的落点是「给你看的产出」，
        //   走 `她写的/` 那个刻意分开的落点 —— 与内部状态（memory.jsonl）不同族。
        public string VaultPath = @"D:\Obsidian_SecondBrain\SecondBrain";
        public string DeepSeekKey = "";             // DeepSeek API key（仅本地，不入库）
        // ---- OpenAI 兼容通道（2026-09-20，为发布降门槛新增）----
        // base 形如 https://api.deepseek.com（不带 /chat/completions，代码里拼）；model 形如 deepseek-chat。
        // ⚠ key 复用 DeepSeekKey 字段（同一个东西，不造第二份真值）——旧配置里的 key 直接可用。
        // base 与 key **都有**才路由到兼容端点，否则回落 Trae 通道（原有行为一字不动）。
        public string OpenAiBase = "";
        public string OpenAiModel = "";
        public string BalanceUrl = "";              // 自定义余额接口（URL）；空 = 走 DeepSeek balance
        public string BalanceToken = "";            // 可选 Bearer token
        public string BalanceHeader = "";           // 可选自定义 header（如 "cookie: xxx"，含冒号整段）
        public string BalanceKey = "total_balance"; // JSON 数值字段名，取余额
        public string BalanceUnit = "";             // 显示单位（自定义接口用；空则 DeepSeek 走 ¥/token）
        public double X = double.NaN, Y = double.NaN;   // 记住位置（物理像素）

        // 三档尺寸。宽高比固定 0.8：再窄就会因为「横向留白不足」把角色缩得比预期小。
        public static readonly double[][] Sizes =
        {
            new double[] { 176, 220 },
            new double[] { 240, 300 },
            new double[] { 320, 400 },
        };
        public double WDip { get { return Sizes[SizeIndex][0]; } }
        public double HDip { get { return Sizes[SizeIndex][1]; } }

        /// <summary>配置与内部状态的落点（%LOCALAPPDATA%\AzhuPet）。
        /// ⚠ public：设置面板要显示它（「配置在哪」是用户第一个会问的问题）。</summary>
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzhuPet"); }
        }
        private static string FilePath { get { return Path.Combine(Dir, "config.json"); } }

        public static PetConfig Load()
        {
            var c = new PetConfig();
            try
            {
                if (!File.Exists(FilePath)) return c;
                string s = File.ReadAllText(FilePath, Encoding.UTF8);
                c.SizeIndex = (int)Num(s, "size", c.SizeIndex);
                if (c.SizeIndex < 0 || c.SizeIndex > 2) c.SizeIndex = 1;
                c.Topmost = Bool(s, "topmost", c.Topmost);
                c.NightDim = Bool(s, "nightDim", c.NightDim);
                c.ShowTray = Bool(s, "showTray", c.ShowTray);
                c.SpeechOn = Bool(s, "speechOn", c.SpeechOn);
                c.SpeechLlm = Bool(s, "speechLlm", c.SpeechLlm);
                c.EyeOn = Bool(s, "eyeOn", c.EyeOn);
                c.OcrOn = Bool(s, "ocrOn", c.OcrOn);
                c.OcrSendText = Bool(s, "ocrSendText", c.OcrSendText);
                c.RoastOn = Bool(s, "roastOn", c.RoastOn);
                c.SummaryOn = Bool(s, "summaryOn", c.SummaryOn);
                c.SummaryIntervalMin = Num(s, "summaryIntervalMin", c.SummaryIntervalMin);
                c.VaultPath = Raw(s, "vaultPath")?.Trim('"') ?? c.VaultPath;
                c.DeepSeekKey = Raw(s, "deepSeekKey")?.Trim('"') ?? "";
                c.OpenAiBase = Raw(s, "openAiBase")?.Trim('"') ?? "";
                c.OpenAiModel = Raw(s, "openAiModel")?.Trim('"') ?? "";
                c.BalanceUrl = Raw(s, "balanceUrl")?.Trim('"') ?? "";
                c.BalanceToken = Raw(s, "balanceToken")?.Trim('"') ?? "";
                c.BalanceHeader = Raw(s, "balanceHeader")?.Trim('"') ?? "";
                c.BalanceKey = Raw(s, "balanceKey")?.Trim('"') ?? "total_balance";
                c.BalanceUnit = Raw(s, "balanceUnit")?.Trim('"') ?? "";
                c.X = Num(s, "x", double.NaN);
                c.Y = Num(s, "y", double.NaN);
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var sb = new StringBuilder();
                sb.Append("{\r\n");
                sb.Append("  \"size\": ").Append(SizeIndex).Append(",\r\n");
                sb.Append("  \"topmost\": ").Append(Topmost ? "true" : "false").Append(",\r\n");
                sb.Append("  \"nightDim\": ").Append(NightDim ? "true" : "false").Append(",\r\n");
                sb.Append("  \"showTray\": ").Append(ShowTray ? "true" : "false").Append(",\r\n");
                sb.Append("  \"speechOn\": ").Append(SpeechOn ? "true" : "false").Append(",\r\n");
                sb.Append("  \"speechLlm\": ").Append(SpeechLlm ? "true" : "false").Append(",\r\n");
                sb.Append("  \"eyeOn\": ").Append(EyeOn ? "true" : "false").Append(",\r\n");
                sb.Append("  \"ocrOn\": ").Append(OcrOn ? "true" : "false").Append(",\r\n");
                sb.Append("  \"ocrSendText\": ").Append(OcrSendText ? "true" : "false").Append(",\r\n");
                sb.Append("  \"roastOn\": ").Append(RoastOn ? "true" : "false").Append(",\r\n");
                sb.Append("  \"summaryOn\": ").Append(SummaryOn ? "true" : "false").Append(",\r\n");
                sb.Append("  \"summaryIntervalMin\": ").Append(SummaryIntervalMin.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(",\r\n");
                sb.Append("  \"vaultPath\": \"").Append(VaultPath.Replace("\\", "\\\\")).Append("\",\r\n");
                sb.Append("  \"deepSeekKey\": \"").Append(DeepSeekKey).Append("\",\r\n");
                sb.Append("  \"openAiBase\": \"").Append(OpenAiBase).Append("\",\r\n");
                sb.Append("  \"openAiModel\": \"").Append(OpenAiModel).Append("\",\r\n");
                sb.Append("  \"balanceUrl\": \"").Append(BalanceUrl).Append("\",\r\n");
                sb.Append("  \"balanceToken\": \"").Append(BalanceToken).Append("\",\r\n");
                sb.Append("  \"balanceHeader\": \"").Append(BalanceHeader).Append("\",\r\n");
                sb.Append("  \"balanceKey\": \"").Append(BalanceKey).Append("\",\r\n");
                sb.Append("  \"balanceUnit\": \"").Append(BalanceUnit).Append("\",\r\n");
                sb.Append("  \"x\": ").Append(double.IsNaN(X) ? "null" : ((long)X).ToString()).Append(",\r\n");
                sb.Append("  \"y\": ").Append(double.IsNaN(Y) ? "null" : ((long)Y).ToString()).Append("\r\n");
                sb.Append("}\r\n");
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // ---- 极简 JSON 取值（避免为一个配置文件引入序列化依赖/反射）----
        private static double Num(string s, string key, double dflt)
        {
            string v = Raw(s, key);
            if (v == null || v == "null") return dflt;
            double d;
            return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : dflt;
        }
        private static bool Bool(string s, string key, bool dflt)
        {
            string v = Raw(s, key);
            if (v == null) return dflt;
            return v == "true";
        }
        private static string Raw(string s, string key)
        {
            int i = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int c = s.IndexOf(':', i);
            if (c < 0) return null;
            int e = c + 1;
            while (e < s.Length && (s[e] == ' ' || s[e] == '\t')) e++;
            int end = e;
            while (end < s.Length && s[end] != ',' && s[end] != '\r' && s[end] != '\n' && s[end] != '}') end++;
            return s.Substring(e, end - e).Trim();
        }

        // ---- 开机自启：写 HKCU 的 Run 键（不用 reg.exe —— 老式外部工具在本机被拦过）----
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "AzhuPet";

        public static bool AutostartOn()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(RunName) != null;
            }
            catch { return false; }
        }

        public static bool SetAutostart(bool on, string exePath)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return false;
                    if (on) k.SetValue(RunName, "\"" + exePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
                return true;
            }
            catch { return false; }
        }
    }

    internal sealed class Cli
    {
        public string ModelPath;
        public string OutFile;
        public string ShotFile;
        public string ShotCrop;
        public double Seconds = 7;
        public bool SelfTest;
        public double DozeAfter = 45;
        public double SleepAfter = 180;
        public double ForcedIdle = -1;      // >=0 则强制空闲秒数（自检打盹用）
        public bool ClickTest;
        public bool DragTest;
        public bool StatusTest;                 // --statustest：无头跑状态探针（网络/VPN/积分），写 JSON
        public bool CaliberTest;                // --calibertest：离线验积分口径（合成响应，不联网/不读凭据）
        public bool BubbleTest;                 // --bubbletest：像素验「气泡浮在宠物之外、不遮模型」
        public bool BalanceSettings;            // --balance-settings：独立打开余额配置中心（无需先找到托盘菜单）
        public bool Settings;                   // --settings：独立打开设置主面板（便于 UI 验收与截图）
        public bool SettingsTest;               // --settingstest：离线验设置面板版式（裁剪／重叠／跟随缩放）
        public bool NoLayout;                   // --no-layout：负对照 —— 跳过重排，版式判据必须变红
        public bool BalanceConfigTest;          // --balanceconfigtest：离线验保存/备份/坏 JSON/凭据隔离
        public string InstallBalanceTemplate;   // --install-balance-template sui-xiang：只装结构，不装凭据
        public bool ForceBubbleOnPet;           // --force-bubble-on-pet：负对照（故意压在模型上，该判据必须变红）
        public string DeepSeekKey;              // 命令行传入的 DeepSeek key（会写入配置）
        public string OpenAiBase;               // 命令行传入的兼容端点 base（会写入配置）
        public string OpenAiModel;              // 命令行传入的兼容端点 model（会写入配置）
        public string LlmModel;                 // --llm-model <id>：覆盖 Trae 通道的模型 id（空 = DeepSeek-V4-Flash）
                                                // ⚠ 不能叫 --model：那个早被 3D 模型文件路径占用了（ModelPath）
        public string Chat;                     // --chat <文本>：无头自检 TraeChat(明文 llm_utils_chat)，结果写 %TEMP%
        public bool PersonaTest;                // --personatest：离线验人格前缀（读得到吗／干不干净／注入是否必然发生）
        public bool WatchTest;                  // --watchtest：离线验 P0 三环（感知／决策／记忆），不读真窗口、不联网
        public bool WatchProbe;                 // --watchprobe：真读前台窗口（唯一只能真验的一环），每 3 秒打一行
        public bool ShellProbe;                 // --shellprobe：枚举真窗口，把「谁算外壳」在这台机器上量出来
        public bool NoDedupe;                   // --no-dedupe：负对照 —— 关掉感知去重，--watchtest 的判据必须变红
        public bool SpeakTest;                  // --speaktest：离线验**表达环接线**（喂合成序列，她到底会不会吐话）
        public bool SpeakProbe;                 // --speakprobe：真读前台跑一段，打印她观察到什么、说了什么
        public bool NoWire;                     // --no-wire：负对照 —— 换哑说话人，--speaktest 的接线判据必须变红
        public bool NoSpeak;                    // --no-speak：启动后**不自发说话**（安静调试用）
        public bool FsTest;                     // --fstest：离线验「全屏隐退」判定（点桌面不该让她隐退）
        public bool FsProbe;                    // --fsprobe：真读 shell 窗口，用真数据验那条判据
        public bool NoShellGuard;               // --no-shellguard：负对照 —— 关掉 shell 排除，--fstest 的判据必须变红
        public bool SpeakVis;                   // --speakvis：真窗口端到端验「PetWindow → 气泡」这一段
        public bool SpeakLlm;                   // --speak-llm：自发说话走真模型（默认模板 stub：零成本、可预测）
        public bool LlmTest;                    // --llmtest：**真调**模型若干次，打印喂进去的 prompt 与她真说的话
        public bool EyeTest;                    // --eyetest：离线验退化图判定 ＋ 截图不落盘（不联网；加 --eye-send 才真发）
        public bool EyeSend;                    // --eye-send：真把截图发给模型 —— 验「通道到底收不收图」
        public bool EyeSynth;                   // --eye-synth：**对照实验** —— 发一张自己合成的大字图，排除「屏幕太复杂」
        public bool EyeSolid;                   // --eye-solid：发**纯色块**问颜色 —— 拆开「像素没到」与「识别能力弱」
        public int EyeFmt = 1;                  // --eye-fmt N：图片消息格式（1=Anthropic image/source；2=OpenAI image_url；3=image_url 字符串）
        public int EyeMax;                      // --eye-max N：截图缩到多宽（0=用默认 1280）
        public int EyeQ;                        // --eye-q N：JPEG 质量（0=用默认 70）
        public bool Eye;                        // --eye：本次运行强制开「她能看屏幕」（不改配置）
        public bool NoEye;                      // --no-eye：本次运行强制关（负对照）
        public bool OcrTest;                    // --ocrtest：离线验本地读字（合成图／纯函数／敏感表／不落盘）
        public bool NoOcrNormalize;             // --no-ocr-normalize：负对照 —— 关掉归一化，tidyCjkSpaces 必须能变红
        /// <summary>--no-lastforeign：负对照 —— 只信「当前前台」，不许回退到上一条真实读数。
        /// ⚠ 它专治本案：前台是她自己时，判据必须回报「她只看见了自己」而不是照绿。</summary>
        public bool NoLastForeign;
        /// <summary>--no-ocr-gate：负对照 —— 跳过 `OcrEye.ShouldSendText` 那道门，直接发。
        /// ⚠ 它专治一种假通过：「关掉时必须不发」那条判据可能只是因为**当时屏幕上没文字**才绿的。
        ///   跳过门后，判据必须在「文字明明有」的情况下仍报出「不许发」被违反 ⇒ 才证明它测的是门。</summary>
        public bool NoOcrGate;
        /// <summary>--speak-foreground：负对照 —— 手动触发（「让她说一句」）回到**旧行为**：
        /// 用「那一瞬间的当前前台」当观察对象，并且带上 `WatchLoop.LastDwell`。
        /// ⚠ 它专治本案（2026-09-20 实拍「6秒，够你在里面找到想要的？」）：前台是她的托盘菜单时，
        ///   旧行为会回退到**很久以前**的一条观察（explorer／任务栏）＋一个**冻结的**秒数。
        ///   跳过修复后，`speakNowUsesReadTarget` 必须变红，而且**要点名是哪一条**。</summary>
        public bool SpeakForeground;
        /// <summary>--speak-screen：本次运行强制开「说话时带上屏幕上的字」（不改配置）。
        /// ⚠ 与托盘那个勾选项同效：托盘改的是 `Cfg.OcrSendText`（落盘），这个只影响本次进程。</summary>
        public bool SpeakScreen;
        /// <summary>--speak-manual：让 `--llmtest` 用**手动触发**那套提示（`Observation.Manual`）。
        /// ⚠ 它是为了能人工对照：同一个应用、同一个秒数，手动那条**不该**提「刚换了窗口」与秒数。
        ///   只影响探针，不改运行时行为。</summary>
        public bool SpeakManual;
        /// <summary>--screen-text &lt;文本&gt;：给 `--llmtest` 灌一段**合成**屏幕文字。
        /// ⚠ 有它才能离线对照「她的话有没有用上屏幕内容」—— 否则那条只能靠真屏幕上此刻写着什么，
        ///   而那是可变外部状态（本仓明令：判据的输入不许沿用外部可变状态）。</summary>
        public string ScreenText;
        /// <summary>--no-roast：负对照 —— 关掉吐槽通道，roast 组判据必须点名变红。</summary>
        public bool NoRoast;
        /// <summary>--no-adaptive：负对照 —— 关掉自适应停留阈值（恒用基线 8 秒），adaptive 组判据必须红。</summary>
        public bool NoAdaptive;
        /// <summary>--summary-now：立刻生成「上一个整段时段」的工作总结（不等满 60 分钟），验收用。</summary>
        public bool SummaryNow;
        /// <summary>--no-summary：负对照 —— 关掉小时总结，summary 组判据必须红。</summary>
        public bool NoSummary;
        /// <summary>--summarytest：离线验小时总结（Due／聚合／prompt／落盘格式），不联网不读真记忆。</summary>
        public bool SummaryTest;
        public bool OcrProbe;                    // --ocrprobe：真读一次当前前台窗口，把真读到的文字写进报告
        public bool OcrVis;                      // --ocrvis：真窗口端到端验「托盘那一项 → 气泡」（UI 接缝）
        public int Times;                       // --times N：--llmtest 调几次（默认 4）
        public string PersonaPath;              // --persona <path>：指定 persona.md；负对照时指向一个坏文件，判据必须变红
        public string BalanceUrl, BalanceToken, BalanceHeader, BalanceKey, BalanceUnit;
        public bool ForceThrough;
        public string ProbeFile;
        public int[] ProbeRect;
        public string Size = "M";
        public int[] At;
        public bool NoTray;
        public string Mat = "tex";
        public string PixDir;                   // 非空 = 像素量测模式：分层连拍 + 做差统计，写 JSON
        public int[] Amb, Key;                  // 0..255 光照覆盖（标定用）
        public int[] Backdrop;                  // 垫在桌宠下面的背板色（像素量测用）
        public bool Freeze;
        public bool VFlip;                      // 把 UV 的 V 翻成 1-v（默认否，见 Glb.cs 文件头）

        public static Cli Parse(string[] a)
        {
            var c = new Cli();
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i];
                switch (k)
                {
                    case "--selftest": c.SelfTest = true; break;
                    case "--clicktest": c.ClickTest = true; break;
                    case "--dragtest": c.DragTest = true; break;
                    case "--statustest": c.StatusTest = true; break;
                    case "--calibertest": c.CaliberTest = true; break;
                    case "--bubbletest": c.BubbleTest = true; break;
                    case "--balance-settings": c.BalanceSettings = true; break;
                    case "--settings": c.Settings = true; break;
                    case "--settingstest": c.SettingsTest = true; break;
                    case "--no-layout": c.NoLayout = true; break;
                    case "--balanceconfigtest": c.BalanceConfigTest = true; break;
                    case "--install-balance-template": c.InstallBalanceTemplate = Nxt(a, ref i); break;
                    case "--force-bubble-on-pet": c.ForceBubbleOnPet = true; break;
                    case "--force-through": c.ForceThrough = true; break;
                    case "--deepseek-key": c.DeepSeekKey = Nxt(a, ref i); break;
                    // OpenAI 兼容通道：--openai-base https://api.deepseek.com --openai-model deepseek-chat --openai-key sk-...
                    case "--openai-base": c.OpenAiBase = Nxt(a, ref i); break;
                    case "--openai-model": c.OpenAiModel = Nxt(a, ref i); break;
                    case "--openai-key": c.DeepSeekKey = Nxt(a, ref i); break;   // key 同一字段（注释见 PetConfig.OpenAiBase）
                    case "--chat": c.Chat = Nxt(a, ref i); break;
                    case "--personatest": c.PersonaTest = true; break;
                    case "--watchtest": c.WatchTest = true; break;
                    case "--watchprobe": c.WatchProbe = true; break;
                    case "--shellprobe": c.ShellProbe = true; break;
                    case "--no-dedupe": c.NoDedupe = true; break;
                    case "--speaktest": c.SpeakTest = true; break;
                    case "--speakprobe": c.SpeakProbe = true; break;
                    case "--no-wire": c.NoWire = true; break;
                    case "--no-speak": c.NoSpeak = true; break;
                    case "--fstest": c.FsTest = true; break;
                    case "--fsprobe": c.FsProbe = true; break;
                    case "--no-shellguard": c.NoShellGuard = true; break;
                    case "--speakvis": c.SpeakVis = true; break;
                    case "--speak-llm": c.SpeakLlm = true; break;
                    case "--llmtest": c.LlmTest = true; break;
                    case "--eyetest": c.EyeTest = true; break;
                    case "--eye-send": c.EyeSend = true; break;
                    case "--eye-synth": c.EyeSynth = true; break;
                    case "--eye-solid": c.EyeSolid = true; break;
                    case "--eye-fmt": c.EyeFmt = (int)Dbl(Nxt(a, ref i), c.EyeFmt); break;
                    case "--eye-max": c.EyeMax = (int)Dbl(Nxt(a, ref i), 0); break;
                    case "--eye-q": c.EyeQ = (int)Dbl(Nxt(a, ref i), 0); break;
                    case "--eye": c.Eye = true; break;
                    case "--no-eye": c.NoEye = true; break;
                    case "--ocrtest": c.OcrTest = true; break;
                    case "--no-ocr-normalize": c.NoOcrNormalize = true; break;
                    case "--no-lastforeign": c.NoLastForeign = true; break;
                    case "--no-ocr-gate": c.NoOcrGate = true; break;
                    case "--speak-screen": c.SpeakScreen = true; break;
                    case "--speak-foreground": c.SpeakForeground = true; break;
                    case "--speak-manual": c.SpeakManual = true; break;
                    case "--screen-text": c.ScreenText = Nxt(a, ref i); break;
                    case "--no-roast": c.NoRoast = true; break;
                    case "--no-adaptive": c.NoAdaptive = true; break;
                    case "--summary-now": c.SummaryNow = true; break;
                    case "--no-summary": c.NoSummary = true; break;
                    case "--summarytest": c.SummaryTest = true; break;
                    case "--ocrprobe": c.OcrProbe = true; break;
                    case "--ocrvis": c.OcrVis = true; break;
                    case "--times": c.Times = (int)Dbl(Nxt(a, ref i), c.Times); break;
                    case "--persona": c.PersonaPath = Nxt(a, ref i); break;
                    case "--balance-url": c.BalanceUrl = Nxt(a, ref i); break;
                    case "--balance-token": c.BalanceToken = Nxt(a, ref i); break;
                    case "--balance-header": c.BalanceHeader = Nxt(a, ref i); break;
                    case "--balance-key": c.BalanceKey = Nxt(a, ref i); break;
                    case "--balance-unit": c.BalanceUnit = Nxt(a, ref i); break;
                    case "--no-tray": c.NoTray = true; break;
                    case "--model": c.ModelPath = Nxt(a, ref i); break;
                    case "--llm-model": c.LlmModel = Nxt(a, ref i); break;
                    case "--out": c.OutFile = Nxt(a, ref i); break;
                    case "--shot": c.ShotFile = Nxt(a, ref i); break;
                    case "--shot-crop": c.ShotCrop = Nxt(a, ref i); break;
                    case "--seconds": c.Seconds = Dbl(Nxt(a, ref i), c.Seconds); break;
                    case "--doze-after": c.DozeAfter = Dbl(Nxt(a, ref i), c.DozeAfter); break;
                    case "--sleep-after": c.SleepAfter = Dbl(Nxt(a, ref i), c.SleepAfter); break;
                    case "--forced-idle": c.ForcedIdle = Dbl(Nxt(a, ref i), -1); break;
                    case "--probe": c.ProbeFile = Nxt(a, ref i); break;
                    case "--probe-rect": c.ProbeRect = Ints(Nxt(a, ref i)); break;
                    case "--size": c.Size = Nxt(a, ref i); break;
                    case "--at": c.At = Ints(Nxt(a, ref i)); break;
                    case "--mat": c.Mat = Nxt(a, ref i); break;
                    case "--pix": c.PixDir = Nxt(a, ref i); break;
                    case "--amb": c.Amb = Ints(Nxt(a, ref i)); break;
                    case "--key": c.Key = Ints(Nxt(a, ref i)); break;
                    case "--backdrop": c.Backdrop = Ints(Nxt(a, ref i)); break;
                    case "--freeze": c.Freeze = true; break;
                    case "--vflip": c.VFlip = true; break;
                }
            }
            return c;
        }
        private static string Nxt(string[] a, ref int i) { return i + 1 < a.Length ? a[++i] : null; }
        private static double Dbl(string s, double d) { double x; return double.TryParse(s, out x) ? x : d; }
        private static int[] Ints(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] p = s.Split(',');
            var r = new int[p.Length];
            for (int i = 0; i < p.Length; i++) int.TryParse(p[i].Trim(), out r[i]);
            return r;
        }

        public int SizeIndex { get { return Size == "S" ? 0 : Size == "L" ? 2 : 1; } }

        /// <summary>从 exe 所在目录往上找 model/chibi_maid_pet.glb（工程目录布局）。</summary>
        public static string ResolveModel(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;
            // 两个搜索起点：exe 所在目录（正常启动）＋ 当前工作目录（从源码树/临时构建目录跑测试时）。
            // ⚠ 少了第二个，从 D:/_petbuild 跑 --bubbletest / --pix 就得每次手写 --model 全路径
            //   —— 而手写全路径这件事本身就会在不经意间指向上一版的模型文件。
            foreach (string start in new[] { AppDomain.CurrentDomain.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                string d = start;
                for (int i = 0; i < 7 && d != null; i++)
                {
                    string p = Path.Combine(d, "model", "chibi_maid_pet.glb");
                    if (File.Exists(p)) return p;
                    d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar));
                }
            }
            return null;
        }
    }
}
