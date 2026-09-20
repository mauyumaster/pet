// 设置主面板（2026-09-20）—— 所有需要配置的东西收在一处。
//
// 为什么要它：此前配置散在四处 —— 托盘勾选项（说话／读屏／模型）／台词模型设置窗（openai 三字段）／
// 余额配置窗（来源与凭据）／config.json 手改（库路径、总结间隔）。用户要的是「打开一个窗，配完所有」。
//
// ⚠ 三条纪律（写在本文件头，改它之前先读）：
// 1. **托盘快速项保留**（高频单手操作），面板负责细节与一次性配置 —— 两边都写同一份 Cfg，
//    且**下发一律走 `PetWindow.ApplyConfig()`**（唯一入口）；面板里不许再手写 `OcrEye.SendText = ...`。
// 2. **每个开关要能回答「它在哪个档位下完全没作用」**（本仓纪律）：灰掉／加注说明，
//    不留给用户「勾了没反应」的坑。
// 3. **凭据不回显**：key 类字段留空即保留（沿用 BalanceSettingsWindow 的先例）。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace AzhuPet
{
    internal sealed class SettingsWindow : Form
    {
        private readonly PetWindow _w;

        // 控件引用（保存时统一回读）
        private CheckBox _cSpeech, _cLlm, _cRoast, _cSummary, _cOcr, _cOcrSend, _cEye;
        private NumericUpDown _nInterval;
        private TextBox _tVault, _tBase, _tModel, _tKey;
        private CheckBox _cTopmost, _cNight, _cAutostart;

        private static readonly Color Bg = Color.FromArgb(24, 27, 38);
        private static readonly Color Field = Color.FromArgb(32, 35, 47);
        private static readonly Color Line = Color.FromArgb(61, 66, 84);
        private static readonly Color Muted = Color.FromArgb(150, 155, 175);
        private static readonly Color Warn = Color.FromArgb(232, 162, 96);

        public SettingsWindow(PetWindow w)
        {
            _w = w;
            Text = "阿助设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 620);
            BackColor = Bg;
            Font = new Font("Microsoft YaHei UI", 9.5f);

            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14, 12, 14, 8) };
            Controls.Add(body);

            // ---- 按钮条（Dock 先加，后加的在上面）----
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = Bg, Padding = new Padding(14, 8, 14, 10) };
            var save = MakeButton("保存并生效", 120, Line, Color.White);
            save.Click += (s, e) => Save();
            var cancel = MakeButton("取消", 80, Field, Muted);
            cancel.Click += (s, e) => Close();
            var bal = MakeButton("余额来源…", 110, Field, Muted);
            bal.Click += (s, e) => OpenBalance();
            var flow = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            flow.Controls.Add(save);
            flow.Controls.Add(cancel);
            flow.Controls.Add(bal);
            bar.Controls.Add(flow);
            Controls.Add(bar);
            bar.BringToFront();

            var stack = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Width = 520 };
            body.Controls.Add(stack);
            int W = 505;

            // ================= 一、她怎么说话 =================
            stack.Controls.Add(Section("她怎么说话"));
            _cSpeech = AddCheck(stack, W, "她会自己说话",
                "关掉＝她只在你打字时回答，不主动开口。⚠ 关掉时下面两项吐槽／总结都停。",
                w.Cfg.SpeechOn);
            _cLlm = AddCheck(stack, W, "台词用模型生成",
                "关＝免费模板句（零成本、逐字节可预测）；开＝走下面「模型通道」。",
                w.Cfg.SpeechLlm);
            _cRoast = AddCheck(stack, W, "时不时吐槽一句",
                "同一应用里换了页面／文档就评一句，长时间安静则冒一句保底。⚠ 只在她自己说话开着时有效。",
                w.Cfg.RoastOn);

            stack.Controls.Add(Section("模型通道（台词与总结都用它）"));
            _tBase = AddText(stack, W, "接口地址", w.Cfg.OpenAiBase, "（留空＝走 Trae 通道；如 https://api.deepseek.com）");
            _tModel = AddText(stack, W, "模型名", w.Cfg.OpenAiModel, "（如 deepseek-chat）");
            _tKey = AddText(stack, W, "API key", w.Cfg.DeepSeekKey, "（留空＝保留已存的 key）", secret: true);
            stack.Controls.Add(Note(stack, W,
                "任何 OpenAI 兼容服务都能接：DeepSeek／硅基流动／OpenRouter／本地 ollama。\n"
                + "两项都填才走它，否则回落 Trae。凭据只存本机（%LOCALAPPDATA%\\AzhuPet）。", Muted));

            // ================= 二、她看得到什么 =================
            stack.Controls.Add(Section("她看得到什么（隐私边界）"));
            _cOcr = AddCheck(stack, W, "允许她在本机读屏幕上的字",
                "用 Windows 自带识别器，全程本机 —— 不出电脑、不花钱。",
                w.Cfg.OcrOn);
            _cOcrSend = AddCheck(stack, W, "把读到的字放进提示（**会发出去**）",
                "⚠ 这是唯一会把屏幕内容送出去的开关。默认关。只读不外发时上面那项单独开即可。",
                w.Cfg.OcrSendText, warn: true);
            _cEye = AddCheck(stack, W, "像素级看屏幕（L4，未实现）",
                "A／B／D 档都不出本机；这一档会把画面本身发出去。当前版本未接，勾了也没有作用。",
                w.Cfg.EyeOn, warn: true);

            // ================= 三、她的产出 =================
            stack.Controls.Add(Section("她的产出（每小时小结）"));
            _cSummary = AddCheck(stack, W, "每小时写一篇小结",
                "开机后每满一段时长，用模型把这一段的「应用 → 时长」写成小结，存成 Markdown 日记。",
                w.Cfg.SummaryOn);
            stack.Controls.Add(Row(stack, W, "间隔（分钟）", out _nInterval));
            _nInterval.Minimum = 10; _nInterval.Maximum = 600; _nInterval.Increment = 10;
            _nInterval.Value = (decimal)Math.Max(10, Math.Min(600, w.Cfg.SummaryIntervalMin));
            _tVault = AddText(stack, W, "日记落点（库路径）", w.Cfg.VaultPath,
                @"（如 D:\Obsidian_SecondBrain\SecondBrain）");
            stack.Controls.Add(Note(stack, W,
                "小结写进「<库路径>\\40 Projects\\阿助（桌宠）\\她写的\\日期.md」，按天一个文件、按小时分节。\n"
                + "素材只有应用名与时长 —— 不含窗口标题、不含屏幕文字。", Muted));

            // ================= 四、外观与启动 =================
            stack.Controls.Add(Section("外观与启动"));
            _cTopmost = AddCheck(stack, W, "始终置顶", null, w.Cfg.Topmost);
            _cNight = AddCheck(stack, W, "夜间调光", null, w.Cfg.NightDim);
            _cAutostart = AddCheck(stack, W, "开机自启", null, PetConfig.AutostartOn());

            // ================= 五、只读信息 =================
            stack.Controls.Add(Section("位置"));
            stack.Controls.Add(Note(stack, W,
                "配置与内部状态：" + PetConfig.Dir + "\n"
                + "判据自检：pet.exe --watchtest --speaktest --summarytest --ocrtest …", Muted));
            stack.Controls.Add(Note(stack, W,
                "⚠ 长期没有动过的默认值（停留 8 秒／吐槽冷却 200 秒／日限额 200 句）暂未开放调参 —— "
                + "它们要配套调整才安全，需要时告诉我。", Warn));
        }

        // ---------- 小工具 ----------

        private static Button MakeButton(string text, int width, Color back, Color fore)
        {
            var b = new Button { Text = text, Width = width, Height = 30, BackColor = back, ForeColor = fore, FlatStyle = FlatStyle.Flat };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private static Label Section(string text)
        {
            return new Label
            {
                Text = text, AutoSize = true, ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold), Margin = new Padding(2, 14, 0, 6),
            };
        }

        private static Label Note(FlowLayoutPanel host, int width, string text, Color color)
        {
            return new Label { Text = text, Width = width, AutoSize = false, Height = 44, ForeColor = color, Margin = new Padding(6, 2, 0, 2) };
        }

        private static CheckBox AddCheck(FlowLayoutPanel host, int width, string label, string tip, bool value, bool warn = false)
        {
            var c = new CheckBox
            {
                Text = label, Width = width, Checked = value, ForeColor = warn ? Warn : Color.White,
                AutoSize = false, Height = tip == null ? 24 : 42, Margin = new Padding(2, 2, 0, 0),
            };
            if (tip != null) c.Text += "\n" + tip;   // 两行：标签 + 灰色说明（WinForms 同色，够用）
            host.Controls.Add(c);
            return c;
        }

        private static TextBox AddText(FlowLayoutPanel host, int width, string label, string value, string placeholder, bool secret = false)
        {
            host.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Color.White, Margin = new Padding(2, 8, 0, 0) });
            var t = new TextBox
            {
                Width = width, Height = 26, BackColor = Field, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
                UseSystemPasswordChar = secret, Margin = new Padding(2, 2, 0, 2),
            };
            t.Tag = placeholder;
            // ⚠ 凭据类不回显：已存 key 时不填文本，用占位提示「留空＝保留」。
            bool hasStored = secret && !string.IsNullOrEmpty(value);
            if (!hasStored && !string.IsNullOrEmpty(value)) t.Text = value;
            if (string.IsNullOrEmpty(t.Text))
            {
                t.Text = placeholder;
                t.ForeColor = Color.FromArgb(110, 115, 135);
                t.GotFocus += (s, e) => { if (t.Text == (string)t.Tag) { t.Text = ""; t.ForeColor = Color.White; } };
                t.LostFocus += (s, e) => { if (t.Text.Length == 0) { t.Text = (string)t.Tag; t.ForeColor = Color.FromArgb(110, 115, 135); } };
            }
            host.Controls.Add(t);
            return t;
        }

        private static Control Row(FlowLayoutPanel host, int width, string label, out NumericUpDown num)
        {
            var p = new Panel { Width = width, Height = 32, Margin = new Padding(2, 4, 0, 2) };
            p.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Color.White, Location = new Point(2, 6) });
            num = new NumericUpDown
            {
                Location = new Point(120, 4), Width = 90, BackColor = Field, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            };
            p.Controls.Add(num);
            return p;
        }

        /// <summary>占位文本不算值 —— 与 placeholder 相同的输入按「空」处理（同 ModelSettingsWindow 的判法）。</summary>
        private static string Clean(TextBox t)
        {
            string v = t.Text.Trim();
            return v == (string)t.Tag ? "" : v;
        }

        private void OpenBalance()
        {
            // 余额窗保持独立：它是「多来源列表 + 凭据」的复杂 UI（同「一个页面一个职责」）。
            var panel = new BalanceSettingsWindow(_w.ReloadBalanceSources);
            panel.Show();
        }

        private void Save()
        {
            var c = _w.Cfg;
            c.SpeechOn = _cSpeech.Checked;
            c.SpeechLlm = _cLlm.Checked;
            c.RoastOn = _cRoast.Checked;
            c.OcrOn = _cOcr.Checked;
            c.OcrSendText = _cOcrSend.Checked;
            c.EyeOn = _cEye.Checked;
            c.SummaryOn = _cSummary.Checked;
            c.SummaryIntervalMin = (double)_nInterval.Value;
            string vault = Clean(_tVault);
            if (vault.Length > 0) c.VaultPath = vault;      // 空＝保留原值（别把库路径清成空串）
            c.OpenAiBase = Clean(_tBase);
            c.OpenAiModel = Clean(_tModel);
            string key = Clean(_tKey);
            if (key.Length > 0) c.DeepSeekKey = key;        // 空＝保留已存 key（凭据不回显）
            c.Topmost = _cTopmost.Checked;
            c.NightDim = _cNight.Checked;
            c.Save();

            // ⚠ 下发走唯一入口：托盘与面板共用同一份同步逻辑，杜绝分叉。
            _w.ApplyConfig();
            bool wantAuto = _cAutostart.Checked;
            if (wantAuto != PetConfig.AutostartOn()) PetConfig.SetAutostart(wantAuto, Environment.ProcessPath);

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
