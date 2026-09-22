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
        /// ⚠ public：设置面板要显示它（「配置在哪」是用户第一个会问的问题）。
        /// ⚠ 支持 `AZHU_CONFIG_DIR` 覆盖：判据要能把它指到临时目录去，
        ///   否则测一次就把用户的真配置写坏了（本项目的判据纪律：离线、不碰真数据）。</summary>
        public static string Dir
        {
            get
            {
                string ov = Environment.GetEnvironmentVariable("AZHU_CONFIG_DIR");
                if (!string.IsNullOrEmpty(ov)) return ov;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzhuPet");
            }
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
                // ⚠ 自愈：历史版本写坏的值（反斜杠膨胀）已不可还原 ⇒ 判为垃圾、回落默认。
                //   放在 Load 里而不是只在 --fixconfig 里，是为了「打开面板」这条路也自愈：
                //   否则用户被污染的配置要等到他恰好想起来跑修复工具才不再卡。
                if (IsPoisonedPath(c.VaultPath)) c.VaultPath = new PetConfig().VaultPath;
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
                sb.Append("  \"vaultPath\": \"").Append(Esc(VaultPath)).Append("\",\r\n");
                sb.Append("  \"deepSeekKey\": \"").Append(Esc(DeepSeekKey)).Append("\",\r\n");
                sb.Append("  \"openAiBase\": \"").Append(Esc(OpenAiBase)).Append("\",\r\n");
                sb.Append("  \"openAiModel\": \"").Append(Esc(OpenAiModel)).Append("\",\r\n");
                sb.Append("  \"balanceUrl\": \"").Append(Esc(BalanceUrl)).Append("\",\r\n");
                sb.Append("  \"balanceToken\": \"").Append(Esc(BalanceToken)).Append("\",\r\n");
                sb.Append("  \"balanceHeader\": \"").Append(Esc(BalanceHeader)).Append("\",\r\n");
                sb.Append("  \"balanceKey\": \"").Append(Esc(BalanceKey)).Append("\",\r\n");
                sb.Append("  \"balanceUnit\": \"").Append(Esc(BalanceUnit)).Append("\",\r\n");
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
        /// <summary>极简 JSON 取值。
        ///
        /// ⚠⚠ 2026-09-20 真实事故：这里以前只 `Trim()`、**不做反转义**，而 <see cref="Save"/> 写盘时
        ///   对 vaultPath 做了 `Replace("\\", "\\\\")`。两边不对称 ⇒ 每次「保存设置」
        ///   反斜杠**翻一倍**：`D:\Obsidian` → `D:\\Obsidian` → `D:\\\\Obsidian` → …
        ///   面板打开时会「读到值 → 写回」，所以**每开一次就翻一倍**（指数增长）。
        ///   实测用户机上 config.json 涨到 **268 MB**（正常约 1 KB），
        ///   而面板构造要对这段文本 `MeasureText` ⇒ **稳定卡 16.5 秒**，
        ///   表现出来的症状只是「打开设置要等很久」——根因是一处转义不对称。
        ///   ⇒ 纪律：**凡「写时转义」的地方，读时必须有对应的反转义**，
        ///     而且两者要成对出现在同一处可对照的代码里，否则迟早分叉。
        /// </summary>
        private static string Raw(string s, string key)
        {
            int i = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int c = s.IndexOf(':', i);
            if (c < 0) return null;
            int e = c + 1;
            while (e < s.Length && (s[e] == ' ' || s[e] == '\t')) e++;

            // 带引号的值：扫到配对的收尾引号，同时做反转义（与 Save 的转义成对）。
            if (e < s.Length && s[e] == '"')
            {
                var sb = new StringBuilder();
                for (int k = e + 1; k < s.Length; k++)
                {
                    char ch = s[k];
                    if (ch == '\\' && k + 1 < s.Length)
                    {
                        char n = s[++k];
                        switch (n)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u':
                                // \uXXXX —— 解不出就放 U+FFFD，别静默吞掉字符
                                if (k + 4 < s.Length)
                                {
                                    int cp;
                                    if (int.TryParse(s.Substring(k + 1, 4),
                                            System.Globalization.NumberStyles.HexNumber,
                                            System.Globalization.CultureInfo.InvariantCulture, out cp))
                                    { sb.Append((char)cp); k += 4; }
                                    else sb.Append('\uFFFD');
                                }
                                else sb.Append('\uFFFD');
                                break;
                            default: sb.Append(n); break;   // 未知转义：原样保留第二个字符
                        }
                        continue;
                    }
                    if (ch == '"') break;                   // 值到此结束
                    sb.Append(ch);
                }
                return sb.ToString();
            }

            // 裸值（数字／true／false／null）：扫到分隔符
            int end = e;
            while (end < s.Length && s[end] != ',' && s[end] != '\r' && s[end] != '\n' && s[end] != '}') end++;
            return s.Substring(e, end - e).Trim();
        }

        /// <summary>识别并清掉「被转义膨胀写坏」的路径值。
        ///
        /// ⚠⚠ 2026-09-20 事故的**抢救**这一步：转义不对称会把 `D:\Obsidian_SecondBrain\SecondBrain`
        ///   写成 `D:` + 2²⁷ 个反斜杠 + `SecondBrain`（实测 134,217,728 个 `\`）。
        ///   关键认识：**这个值已经不可逆还原了** —— 多出来的反斜杠把原本的段落分隔
        ///   （`\Obsidian_SecondBrain\`）吃掉了，任何「反转义 N 次」的尝试都是猜。
        ///   ⇒ 唯一正确做法是**判定它是垃圾、重置为默认值**，而不是保真读回来
        ///   （保真读回来正是 `--fixconfig` 第一次没生效的原因：读回 1.34 亿个 `\`，
        ///   写回时再转义成 2.68 亿 —— 字节数原地踏步，看着像「修复失败」）。
        ///
        /// 判据用**结构性**条件，不用魔数：反斜杠数量超过任何合法路径可能有的量级，
        /// 或长度超过 NTFS 长路径上限，就判为垃圾。正常 Windows 路径反斜杠不会过百。
        /// </summary>
        public static bool IsPoisonedPath(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            if (v.Length > 32767) return true;              // 超 NTFS 长路径上限
            int bs = 0;
            for (int i = 0; i < v.Length; i++)
                if (v[i] == '\\' && ++bs > 64) return true; // 合法路径不会有 64 个以上分隔符
            return false;
        }

        /// <summary>JSON 字符串值转义（与 <see cref="Raw"/> 的反转义成对）。
        /// ⚠ 以前只有 vaultPath 走了转义，其他字段（含 API key）**直接裸拼** ——
        ///   key 里只要有一个引号或反斜杠就会把整个 config.json 写坏，而且下次读不回来。
        ///   统一走这里，两边才算对称。</summary>
        private static string Esc(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            var sb = new StringBuilder(v.Length + 8);
            foreach (char ch in v)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
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

        /// <summary>
        /// 自启自愈：**仅当注册表里记的那个 exe 文件已经不存在时**，把它改成当前这个 exe。
        ///
        /// ⚠ 为什么条件卡得这么死（只在旧目标不存在时才动）：
        ///   她有两种运行形态 —— 开发树的 `bin\Release\...` 与固定安装目录（如 D:\AzhuPet）。
        ///   两者都能启动、都能开自启。若无条件「跟随当前进程」，两个形态就会互相抢那个
        ///   注册表值：谁后启动谁写赢，自启目标在两者之间来回漂移，而用户完全看不见
        ///   （症状是「开机后起来的怎么是旧版本」）。
        ///   只在旧目标**真的没了**（被搬走、被删、被 dotnet clean 清掉）时才纠正 ——
        ///   这既是「自愈」的本意，也不会让两个形态打架。
        ///
        /// 背景：这条自愈是为「给她一个固定安装位置」配套的。此前自启写的是构建产物路径
        ///   （…\bin\Release\net9.0-windows10.0.19041.0\pet.exe），一次 dotnet clean 或
        ///   一次 TFM 变更就静默失效，只在注册表里留一个指向不存在文件的死值。
        /// </summary>
        public static void HealAutostartIfOn()
        {
            try
            {
                string me = Environment.ProcessPath;
                if (string.IsNullOrEmpty(me)) return;

                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    string cur = k.GetValue(RunName) as string;
                    string target = AutostartHealTarget(cur, me, File.Exists);
                    if (target == null) return;                 // 不动（绝大多数情况走这里）
                    k.SetValue(RunName, target);                // 旧目标没了 ⇒ 纠正
                }
            }
            catch { }
        }

        /// <summary>
        /// 纯函数：算出注册表**应该**被改成什么值；返回 null = 不要动。
        ///
        /// ⚠ 抽成纯函数是为了它能被**判据逼红**。靠真注册表验的判据有两个毛病：
        ///   ① 危险 —— 会真去改用户的开机自启；
        ///   ② 覆盖不全 —— 「旧目标还在所以不该动」这类**正面**场景没法构造。
        ///   exists 由调用方传进来（而不是内部直接调 File.Exists），就是为了判据能造任意组合。
        /// </summary>
        public static string AutostartHealTarget(string currentValue, string me, Func<string, bool> exists)
        {
            if (string.IsNullOrEmpty(me)) return null;              // 不知道自己是谁 ⇒ 别乱写
            string want = "\"" + me + "\"";
            if (string.IsNullOrEmpty(currentValue)) return null;    // 没开自启 ⇒ 不擅自开
            if (currentValue == want) return null;                  // 已经对

            string old = currentValue.Trim().Trim('"');
            if (string.IsNullOrEmpty(old)) return want;             // 值是空的 ⇒ 直接纠正
            if (exists != null && exists(old)) return null;         // 旧目标还在 ⇒ 不抢（见 HealAutostartIfOn）
            return want;                                            // 旧目标没了 ⇒ 纠正
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
        public bool SpinTest;                   // --spintest：离线验「拎起旋转」（固定角加速度／角速度上限／左右半屏方向）
        public bool StatusTest;                 // --statustest：无头跑状态探针（网络/VPN/积分），写 JSON
        public bool CaliberTest;                // --calibertest：离线验积分口径（合成响应，不联网/不读凭据）
        public bool BubbleTest;                 // --bubbletest：像素验「气泡浮在宠物之外、不遮模型」
        public bool BalanceSettings;            // --balance-settings：独立打开余额配置中心（无需先找到托盘菜单）
        public bool Settings;                   // --settings：独立打开设置主面板（便于 UI 验收与截图）
        public bool SettingsTest;               // --settingstest：离线验设置面板版式（裁剪／重叠／跟随缩放）
        public bool NoLayout;                   // --no-layout：负对照 —— 跳过重排，版式判据必须变红
        public bool BalanceConfigTest;          // --balanceconfigtest：离线验保存/备份/坏 JSON/凭据隔离
        public bool ConfigTest;                 // --configtest：离线验主配置「写/读转义对称 + 不膨胀」
        public bool FixConfig;                  // --fixconfig：把被「转义不对称」写胖的 config.json 修回来
        public bool UpdateTest;                 // --updatetest：离线验自更新（版本比较／feed 解析／staging 校验／替换）
        public bool ApplyUpdate;                // --applyupdate：显式兑现待替换版本（用户点「立即更新」走这条）
        public bool Version;                    // --version：只打印版本号（pack-release.cmd 靠它取名）
        public bool UpdateDiag;                 // --updatediag：联网诊断更新链路（代理／可达性／版本），自助排查用
        public string FeedUrl;                  // --feed <url>：覆盖更新源（诊断／自测用；不写就用 DefaultFeedUrl）
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

        /// <summary>这一轮跑的是不是「自检／工具模式」（而非正常当桌宠跑）。
        ///
        /// ⚠⚠ 存在的理由：自更新要在启动时**替换 exe 本身**。但判据必须无副作用 ——
        ///   跑一次 `--configtest` 就把用户的程序换掉是灾难。所以「要不要兑现待替换版本」
        ///   得先问一句「这次是自检吗」。
        /// ⚠ 维护性警告：**新增任何 --xxxtest 开关，都要同步加进这里**。
        ///   漏加的症状很隐蔽：新判据跑起来会顺手把用户的桌宠更新了。
        ///   之所以不做成「名字里带 test 就算」的自动判断 —— 那样 `--no-xxx` 负对照开关、
        ///   `--fixconfig` 这类**真会改文件**的工具模式分不清，宁可显式列举。
        ///   （自检 UpdateTest 里有一条判据专门守这里，见 UpdateTest.cs。）</summary>
        public bool AnyTest()
        {
            return SelfTest || ClickTest || DragTest || SpinTest || PersonaTest
                || WatchTest || WatchProbe || ShellProbe
                || FsTest || FsProbe
                || SpeakTest || SpeakProbe || SpeakVis || LlmTest
                || EyeTest || OcrTest || OcrProbe || OcrVis
                || StatusTest || CaliberTest || BubbleTest
                || BalanceConfigTest || ConfigTest || FixConfig || UpdateTest
                || UpdateDiag
                || SettingsTest || SummaryTest || SummaryNow
                || BalanceSettings || Settings
                || PixDir != null || ProbeFile != null
                || Chat != null
                || FeedUrl != null
                || !string.IsNullOrEmpty(InstallBalanceTemplate);
        }

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
                    case "--spintest": c.SpinTest = true; break;
                    case "--statustest": c.StatusTest = true; break;
                    case "--calibertest": c.CaliberTest = true; break;
                    case "--bubbletest": c.BubbleTest = true; break;
                    case "--balance-settings": c.BalanceSettings = true; break;
                    case "--settings": c.Settings = true; break;
                    case "--settingstest": c.SettingsTest = true; break;
                    case "--no-layout": c.NoLayout = true; break;
                    case "--balanceconfigtest": c.BalanceConfigTest = true; break;
                    case "--configtest": c.ConfigTest = true; break;
                    case "--fixconfig": c.FixConfig = true; break;
                    case "--updatetest": c.UpdateTest = true; break;
                    case "--applyupdate": c.ApplyUpdate = true; break;
                    case "--version": c.Version = true; break;
                    case "--updatediag": c.UpdateDiag = true; break;
                    case "--feed": c.FeedUrl = Nxt(a, ref i); break;
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

        /// <summary>找 model/chibi_maid_pet.glb。优先级：
        ///   ① --model 显式路径
        ///   ② **exe 同目录**的 model/（发布包的布局：解压出来 pet.exe 与 model/ 并排）
        ///   ③ 从 exe 目录 / 当前工作目录**往上**找 model/（开发布局：pet/bin/.../ 往上到工程根的 model/）
        ///
        /// ⚠ ② 是 2026-09-21 补的，起因是一次真实的翻车：发布包原来只含 pet.exe + persona.md，
        ///   模型 13MB 留在工程根没进包 ⇒ 用户解压双击，弹「找不到 model/chibi_maid_pet.glb」。
        ///   而旧逻辑只会往上找（为开发方便写的），**发布包解压后往上什么也找不到**。
        ///   两条路都要留：开发时用 ③，发布后用 ②。</summary>
        public static string ResolveModel(string explicitPath)
        {
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;

            // ② exe 同目录（发布布局）—— 明说「就在我旁边」，不往上爬
            try
            {
                string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "model", "chibi_maid_pet.glb");
                if (File.Exists(beside)) return beside;
            }
            catch { }

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

        /// <summary>模型文件在发布包里的相对路径（pack-release.cmd 与判据共用同一份口径）。</summary>
        public const string ModelRelPath = "model/chibi_maid_pet.glb";
    }
}
