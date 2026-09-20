// 台词模型设置（2026-09-20，发布降门槛）—— 一个精简窗：任何 OpenAI 兼容服务都能接。
//
// ⚠ 为什么不复用 BalanceSettingsWindow：那是「余额来源」的窗（多来源列表），
//   而这里只有三个字段（base／model／key），造第二份复杂 UI = 两个落点迟早不一致。
// ⚠ key 落 config.json（%LOCALAPPDATA%\AzhuPet\，不在同步目录）—— 与 DeepSeekKey 同一先例。
//   明文落盘是已知取舍：本地单用户程序，构架闸已保证它绝不进仓库／bin。
using System;
using System.Drawing;
using System.Windows.Forms;

namespace AzhuPet
{
    internal sealed class ModelSettingsWindow : Form
    {
        private readonly PetWindow _w;
        private readonly TextBox _base, _model, _key;

        public ModelSettingsWindow(PetWindow w)
        {
            _w = w;
            Text = "台词模型设置（OpenAI 兼容）";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 250);
            BackColor = Color.FromArgb(24, 27, 38);
            Font = new Font("Microsoft YaHei UI", 9.5f);

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14) };
            Controls.Add(root);

            var intro = new Label
            {
                Dock = DockStyle.Top, Height = 58, ForeColor = Muted(),
                Text = "填「接口地址 + 模型名 + API key」三项即可接任何 OpenAI 兼容服务。\n" +
                       "例：DeepSeek 填 https://api.deepseek.com ／ deepseek-chat ／ 你的 sk-…\n" +
                       "本地 ollama 填 http://localhost:11434 ／ qwen2.5 ／ ollama。留空 = 用 Trae 通道。",
            };
            root.Controls.Add(intro);

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, AutoSize = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(grid);
            grid.BringToFront();

            _base = AddRow(grid, 0, "接口地址", w.Cfg.OpenAiBase, "（如 https://api.deepseek.com）");
            _model = AddRow(grid, 1, "模型名", w.Cfg.OpenAiModel, "（如 deepseek-chat）");
            _key = AddRow(grid, 2, "API key", w.Cfg.DeepSeekKey, "（粘贴 sk-…）", multiline: true);
            _key.TextChanged += (s, e) => UpdateKeyPlaceholder();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 6, 0, 0) };
            var save = new Button { Text = "保存", Width = 88, BackColor = Color.FromArgb(61, 66, 84), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            save.FlatAppearance.BorderSize = 0;
            save.Click += (s, e) => Save();
            var clear = new Button { Text = "清空（回到 Trae 通道）", Width = 160, BackColor = Color.FromArgb(32, 35, 47), ForeColor = Muted(), FlatStyle = FlatStyle.Flat };
            clear.FlatAppearance.BorderSize = 0;
            clear.Click += (s, e) => { _base.Text = _model.Text = _key.Text = ""; Save(); };
            buttons.Controls.Add(save);
            buttons.Controls.Add(clear);
            root.Controls.Add(buttons);
            buttons.BringToFront();

            AcceptButton = save;
        }

        private static Color Muted() { return Color.FromArgb(150, 155, 175); }

        private static TextBox AddRow(TableLayoutPanel grid, int row, string label, string value, string placeholder, bool multiline = false)
        {
            var l = new Label { Text = label, ForeColor = Color.White, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 4, 0) };
            var t = new TextBox
            {
                Dock = DockStyle.Fill, Text = value ?? "",
                BackColor = Color.FromArgb(32, 35, 47), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(4, 6, 0, 2),
                Multiline = multiline, Height = multiline ? 52 : 26,
            };
            t.Tag = placeholder;
            t.GotFocus += (s, e) => { if (t.Text == (string)t.Tag) { t.Text = ""; t.ForeColor = Color.White; } };
            t.LostFocus += (s, e) => { if (t.Text.Length == 0) { t.Text = (string)t.Tag; t.ForeColor = Color.FromArgb(110, 115, 135); } };
            if (t.Text.Length == 0) { t.Text = placeholder; t.ForeColor = Color.FromArgb(110, 115, 135); }   // 占位样式
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, multiline ? 58 : 36));
            grid.Controls.Add(l, 0, row);
            grid.Controls.Add(t, 1, row);
            return t;
        }

        private void UpdateKeyPlaceholder()
        {
            // key 框是唯一有真实内容的敏感框：占位态不算「填过」——保存时按颜色区分会脆，直接按文本判。
        }

        private void Save()
        {
            // ⚠ 占位文本不算值：与 placeholder 相同的输入按「空」处理（否则示例 URL 会被当成真配置存下来）。
            string Clean(TextBox t) { return t.Text.Trim(); }
            _w.Cfg.OpenAiBase = Clean(_base) == (string)_base.Tag ? "" : Clean(_base);
            _w.Cfg.OpenAiModel = Clean(_model) == (string)_model.Tag ? "" : Clean(_model);
            _w.Cfg.DeepSeekKey = Clean(_key) == (string)_key.Tag ? "" : Clean(_key);
            _w.Cfg.Save();
            DialogResult = System.Windows.Forms.DialogResult.OK;
            Close();
        }
    }
}
