// 余额相关的两个编辑对话框（WinForms 版）—— 2026-10-01 由 BalanceSettingsWindow.cs 里的
// WPF 版 `CredentialEditor` / `SourceEditor` 改写而来。
//
// 为什么改写而不是把 WPF 对话框留着：「余额与凭据」已经是设置面板里的一栏（WinForms + SettingsTheme），
//   而这两个弹窗是用户**最常遇到的两屏**（配凭据、配自定义源）。留成写死深色的 WPF 窗，
//   浅色系统下从浅色面板点出来会突然跳成一块深色 —— 观感是断的。
//   ⚠ 只有 WebView2 那个「浏览器登录获取」窗**仍然**是 WPF（`CredentialBrowserWindow`）：
//     它托管 WebView2 且带一整条抓包逻辑，重写的风险与收益不成比例；那一处用
//     `WindowInteropHelper` 挂 owner + 手工 `EnableWindow` 处理模态（见 SettingsWindow.OpenBrowserLogin）。
//
// ⚠ 三条纪律与设置面板一致：
// 1. **颜色/间距只从 SettingsTheme 取**，本文件不许出现 `Color.FromArgb(…)` 字面量。
// 2. **凭据不回显**：已关联凭据时只显示文件名，粘贴框留空＝保留原值。
// 3. **坐标一律按字体度量算**（`LineH` / `FitLabel`），不写死像素 —— 本机 150% 缩放下
//    写死的行高会撞字（设置主面板为这件事返工过一轮）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AzhuPet
{
    /// <summary>「手工粘贴凭据」对话框：第一行 URL，后续每行一个 `请求头: 值`。
    /// 旧内容不回显（留空即保留原值）—— 与设置面板里 key 类字段同一条口径。</summary>
    internal sealed class CredentialEditorForm : Form
    {
        private readonly string _file;
        private readonly string[] _required;
        private readonly TextBox _input;
        private readonly Label _hint, _title;
        private readonly Panel _foot;
        private readonly Button _ok, _cancel;

        private const int PadX = 20, PadY = 16, FootH = 44;

        public CredentialEditorForm(string platform, string file, params string[] required)
        {
            _file = file;
            _required = required ?? new string[0];
            SettingsTheme.InitFonts();
            var pal = SettingsTheme.Pal;

            Text = "更新 " + platform + " 凭据";
            // ⚠ AutoScaleMode 必须是 Dpi 而不是默认的 Font —— 设完 Font 再设 ClientSize 时，
            //   窗体尺寸会按「当前字体／设计期字体」的比例被偷偷缩放（设置主面板第一版就栽在这）。
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = pal.Window;
            ForeColor = pal.Text;
            Font = SettingsTheme.Body;
            ClientSize = new Size(680, 500);
            MinimumSize = new Size(560, 400);
            DoubleBuffered = true;

            _title = new Label
            {
                Text = "粘贴完整请求", Font = SettingsTheme.H2, ForeColor = pal.Text, AutoSize = true,
            };
            _hint = SettingsTheme.TextLabel(
                "格式：第一行接口 URL，后续每行一个「请求头: 值」。也可直接粘贴现有凭据文件全文。\n"
                + "已有内容不会显示；留空不会覆盖。保存位置仅限本机 LocalAppData。\n"
                + "需要的最小集合：" + (_required.Length == 0 ? "（无）" : string.Join("、", _required)),
                pal.Muted, SettingsTheme.Small);
            _input = new TextBox
            {
                Multiline = true, AcceptsReturn = true, AcceptsTab = true, WordWrap = false,
                ScrollBars = ScrollBars.Both, Font = SettingsTheme.Mono,
                BackColor = pal.Field, ForeColor = pal.Text, BorderStyle = BorderStyle.FixedSingle,
            };

            _foot = new Panel { BackColor = pal.Window };
            _ok = SettingsTheme.FormButton("安全保存", true);
            _ok.Click += Save;
            _cancel = SettingsTheme.FormButton("取消", false);
            _cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            _foot.Controls.Add(_ok);
            _foot.Controls.Add(_cancel);
            AcceptButton = _ok;
            CancelButton = _cancel;

            Controls.Add(_input);
            Controls.Add(_foot);
            Controls.Add(_hint);
            Controls.Add(_title);
            // ⚠ 文本框最后 BringToFront：它没有 Dock，与其它三个是**重叠**关系，
            //   靠 z-order 决定谁在上面（顺序错了会被压住看不见，而所有判据都不会红）。
            _input.BringToFront();

            Resize += (s, e) => Relayout();
            Relayout();
        }

        private void Relayout()
        {
            int w = Math.Max(160, ClientSize.Width - PadX * 2);

            _title.Left = PadX;
            _title.Top = PadY;
            _hint.Left = PadX;
            _hint.Top = _title.Bottom + SettingsTheme.GapSm;
            SettingsTheme.FitLabel(_hint, w);

            _foot.Left = PadX;
            _foot.Width = w;
            _foot.Height = FootH;
            _foot.Top = Math.Max(0, ClientSize.Height - PadY - FootH);
            _ok.Left = Math.Max(0, _foot.Width - _ok.Width);
            _cancel.Left = Math.Max(0, _ok.Left - SettingsTheme.GapSm - _cancel.Width);

            int top = _hint.Bottom + SettingsTheme.GapMd;
            _input.Left = PadX;
            _input.Top = top;
            _input.Width = w;
            _input.Height = Math.Max(60, _foot.Top - SettingsTheme.GapMd - top);
        }

        private void Save(object sender, EventArgs e)
        {
            string text = _input.Text.Trim();
            if (text.Length == 0)
            {
                MessageBox.Show(this, "没有输入内容，原凭据未改动。", "更新凭据",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string first = text.Split('\n', '\r').FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
            Uri u;
            if (!Uri.TryCreate(first, UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http"))
            {
                MessageBox.Show(this, "第一行应为完整的 http/https 接口 URL。", "格式不正确",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Split('\n', '\r'))
            {
                int i = line.IndexOf(':');
                if (i > 0) names.Add(line.Substring(0, i).Trim());
            }
            var missing = _required.Where(x => !names.Contains(x)).ToList();
            if (missing.Count > 0)
            {
                MessageBox.Show(this, "缺少必要请求头：" + string.Join("、", missing), "格式不完整",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                BalanceSources.SaveSecret(_file, text);
                _input.Clear();
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "更新凭据",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    /// <summary>添加／编辑一个「自定义余额源」。
    ///
    /// ⚠ 它只管**收集字段**：校验与落盘都交回设置面板（那里是唯一的保存落点）——
    ///   两个地方各校验一次就是两个会漂的口径。
    /// ⚠ 凭据那一格是**唯一的**敏感信息入口：普通请求头里若还写着 cookie / authorization，
    ///   保存时会被顶回来（`HasSensitiveInlineHeaders`）。
    /// ⚠ 字段顺序由 `BuildRows()` **一处**声明，布局按它走 —— 不再有"声明顺序"与"摆放顺序"两份。</summary>
    internal sealed class SourceEditorForm : Form
    {
        private sealed class Row
        {
            public Label Label;
            public Control Box;
            public Func<bool> Visible = () => true;
        }

        private readonly List<Row> _rows = new List<Row>();

        private readonly TextBox _name, _url, _path, _unit, _secretInput, _headers, _body;
        private readonly ComboBox _method;
        private readonly CheckBox _enabled, _advToggle;
        private readonly Label _secretNote, _testState, _head;
        private readonly Panel _scrollHost, _canvas, _foot;
        private readonly Button _test, _apply, _cancel;
        private readonly string _oldSecret;

        public BalanceSource Result { get; private set; }
        public bool CredentialSaved { get; private set; }

        private const int Pad = 20, FootH = 46;

        public SourceEditorForm(BalanceSource src)
        {
            src = src ?? new BalanceSource { Method = "GET", Enabled = true };
            _oldSecret = src.SecretFile ?? "";
            SettingsTheme.InitFonts();
            var pal = SettingsTheme.Pal;

            Text = string.IsNullOrEmpty(src.Name) ? "新增自定义余额源" : "编辑 " + src.Name;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = pal.Window;
            ForeColor = pal.Text;
            Font = SettingsTheme.Body;
            ClientSize = new Size(700, 720);
            MinimumSize = new Size(560, 520);
            DoubleBuffered = true;

            _name = MakeBox(src.Name);
            _url = MakeBox(src.Url);
            _path = MakeBox(src.PathExpr);
            _unit = MakeBox(src.Unit);
            _secretInput = MakeBox("", true);
            _headers = MakeBox(src.HeadersText, true);
            _body = MakeBox(src.Body, true);

            _method = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = pal.Field, ForeColor = pal.Text,
            };
            _method.Items.AddRange(new object[] { "GET", "POST" });
            _method.SelectedItem = (src.Method ?? "GET").ToUpperInvariant() == "POST" ? "POST" : "GET";

            _enabled = new CheckBox
            {
                Text = "启用并显示在状态气泡中", Checked = src.Enabled, AutoSize = true,
                ForeColor = pal.Text, Font = SettingsTheme.Body,
            };
            _advToggle = new CheckBox
            {
                Text = "高级选项：非敏感请求头与请求体", AutoSize = true,
                ForeColor = pal.Muted, Font = SettingsTheme.Body,
                Checked = !string.IsNullOrWhiteSpace(src.HeadersText) || !string.IsNullOrWhiteSpace(src.Body),
            };

            _head = SettingsTheme.TextLabel(
                "告诉桌宠：请求哪里、如何鉴权、从响应 JSON 的哪里取数字。\n"
                + "数组求和：accounts[type=1].remain　·　相减：sub:data.total;data.used",
                pal.Muted, SettingsTheme.Small);
            _secretNote = SettingsTheme.TextLabel(
                string.IsNullOrWhiteSpace(_oldSecret) ? "尚未关联凭据；粘贴 authorization / cookie 等敏感请求头。"
                    : "已安全关联 " + Path.GetFileName(_oldSecret) + "；下方留空即保留，旧内容不会回显。",
                pal.Muted, SettingsTheme.Small);
            _testState = SettingsTheme.TextLabel("", pal.Faint, SettingsTheme.Small);

            _canvas = new Panel { BackColor = pal.Window };
            _scrollHost = new Panel { BackColor = pal.Window, AutoScroll = true };
            _scrollHost.Controls.Add(_canvas);

            _foot = new Panel { BackColor = pal.Window };
            _apply = SettingsTheme.FormButton("保存并返回", true);
            _apply.Click += Save;
            _cancel = SettingsTheme.FormButton("取消", false);
            _cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
            _test = SettingsTheme.FormButton("测试连接", false);
            _test.Click += async (s, e) => await Test();
            _foot.Controls.Add(_test);
            _foot.Controls.Add(_apply);
            _foot.Controls.Add(_cancel);
            AcceptButton = _apply;
            CancelButton = _cancel;

            BuildRows();
            _canvas.Controls.Add(_head);
            _canvas.Controls.Add(_secretNote);
            _canvas.Controls.Add(_advToggle);
            _canvas.Controls.Add(_enabled);
            _canvas.Controls.Add(_testState);

            Controls.Add(_scrollHost);
            Controls.Add(_foot);
            _scrollHost.BringToFront();

            Resize += (s, e) => Relayout();
            _advToggle.CheckedChanged += (s, e) => Relayout();
            Relayout();
        }

        /// <summary>**唯一**的字段顺序声明：从上到下就是列表顺序。
        /// `visible` 为 null ＝ 总是可见；传一个判据表示「跟着高级选项折叠」。</summary>
        private void BuildRows()
        {
            AddRow("显示名称", _name);
            AddRow("请求方法", _method);
            AddRow("接口 URL", _url);
            AddRow("余额字段路径", _path);
            AddRow("显示单位（可选）", _unit);
            AddRow("凭据（可选）", _secretInput);
            AddRow("普通请求头（每行 name: value）", _headers, () => _advToggle.Checked);
            AddRow("POST 请求体", _body, () => _advToggle.Checked);
        }

        private void AddRow(string labelText, Control box, Func<bool> visible = null)
        {
            var lbl = new Label
            {
                Text = labelText, AutoSize = true,
                ForeColor = SettingsTheme.Pal.Text, Font = SettingsTheme.Body,
            };
            var row = new Row { Label = lbl, Box = box };
            if (visible != null) row.Visible = visible;
            _rows.Add(row);
            _canvas.Controls.Add(lbl);
            _canvas.Controls.Add(box);
        }

        private static TextBox MakeBox(string value, bool multiline = false) => new TextBox
        {
            Text = value ?? "",
            Multiline = multiline,
            Height = multiline ? 76 : 26,
            AcceptsReturn = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            BackColor = SettingsTheme.Pal.Field, ForeColor = SettingsTheme.Pal.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = multiline ? SettingsTheme.Mono : SettingsTheme.Body,
        };

        private void Relayout()
        {
            int wrap = Math.Max(120, _scrollHost.ClientSize.Width - Pad * 2);

            _head.Left = Pad;
            _head.Top = Pad;
            SettingsTheme.FitLabel(_head, wrap);
            int y = _head.Bottom + SettingsTheme.GapMd;

            foreach (var row in _rows)
            {
                bool vis = row.Visible();
                row.Label.Visible = vis;
                row.Box.Visible = vis;
                // ⚠ 隐藏的字段**不占位置、也不推进 y** —— 否则折叠起来底下会留一大片空白，
                //   而滚动条的高度是照它算的（"折叠了反而更长"）。
                if (!vis) continue;

                row.Label.Left = Pad;
                row.Label.Top = y;
                row.Box.Left = Pad;
                row.Box.Top = row.Label.Bottom + SettingsTheme.GapXs;
                row.Box.Width = wrap;
                y = row.Box.Bottom + SettingsTheme.GapMd;

                // 凭据那格下面挂说明，再下面是「高级选项」开关（它管着后面两个字段的显隐）。
                if (ReferenceEquals(row.Box, _secretInput))
                {
                    _secretNote.Left = Pad;
                    _secretNote.Top = y;
                    SettingsTheme.FitLabel(_secretNote, wrap);
                    y = _secretNote.Bottom + SettingsTheme.GapMd;
                    _advToggle.Left = Pad;
                    _advToggle.Top = y;
                    y = _advToggle.Bottom + SettingsTheme.GapMd;
                }
            }

            _enabled.Left = Pad;
            _enabled.Top = y;
            y = _enabled.Bottom + SettingsTheme.GapMd;
            _testState.Left = Pad;
            _testState.Top = y;
            SettingsTheme.FitLabel(_testState, wrap);
            y = _testState.Bottom + Pad;

            _canvas.Left = 0;
            _canvas.Top = 0;
            _canvas.Width = _scrollHost.ClientSize.Width;
            _canvas.Height = Math.Max(y, _scrollHost.ClientSize.Height);

            _foot.Left = 0;
            _foot.Width = ClientSize.Width;
            _foot.Height = FootH;
            _foot.Top = Math.Max(0, ClientSize.Height - FootH);
            _apply.Left = Math.Max(0, ClientSize.Width - Pad - _apply.Width);
            _cancel.Left = Math.Max(0, _apply.Left - SettingsTheme.GapSm - _cancel.Width);
            _test.Left = Math.Max(Pad, _cancel.Left - SettingsTheme.GapMd - _test.Width);

            _scrollHost.Left = 0;
            _scrollHost.Top = 0;
            _scrollHost.Width = ClientSize.Width;
            _scrollHost.Height = Math.Max(80, _foot.Top);
        }

        private BalanceSource Build(bool forTest)
        {
            string secret = _oldSecret, typed = _secretInput.Text.Trim(), headers = _headers.Text.Trim();
            // ⚠ 测试时把「刚粘进来、还没落盘」的凭据临时并进普通请求头 —— 否则用户点「测试连接」
            //   会拿到一个必错的 401（凭据还没存），而那是**测试方式的错**，不是配置的错。
            if (forTest && typed.Length > 0) { headers += (headers.Length > 0 ? "\n" : "") + typed; secret = ""; }
            return new BalanceSource
            {
                Name = _name.Text.Trim(), Url = _url.Text.Trim(),
                Method = (_method.SelectedItem as string) ?? "GET",
                PathExpr = _path.Text.Trim(), Unit = _unit.Text.Trim(),
                Enabled = _enabled.Checked, SecretFile = secret,
                HeadersText = headers, Body = _body.Text.Trim(),
            };
        }

        private async Task Test()
        {
            var s = Build(true);
            var errors = BalanceSources.Validate(s);
            if (errors.Count > 0) { ShowTest(string.Join("；", errors), false); return; }
            _test.Enabled = false; _test.Text = "正在测试…";
            ShowTest("正在请求接口", null);
            try
            {
                var c = await new StatusProbe().TestSourceAsync(s);
                ShowTest(c.Ok ? "✓ 成功读到 " + c.Value.ToString("0.##") + (string.IsNullOrEmpty(c.Unit) ? "" : " " + c.Unit)
                              : "✕ " + c.Error, c.Ok);
            }
            catch (Exception ex) { ShowTest("✕ " + ex.Message, false); }
            finally { _test.Text = "测试连接"; _test.Enabled = true; }
        }

        private void ShowTest(string text, bool? ok)
        {
            var pal = SettingsTheme.Pal;
            _testState.Text = text;
            _testState.ForeColor = ok == true ? pal.Good : ok == false ? pal.Bad : pal.Faint;
            SettingsTheme.FitLabel(_testState, Math.Max(80, _scrollHost.ClientSize.Width - Pad * 2));
        }

        private void Save(object sender, EventArgs e)
        {
            var s = Build(false);
            string pending = _secretInput.Text.Trim();
            if (pending.Length > 0) s.SecretFile = "";
            var errors = BalanceSources.Validate(s);
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", errors), "请检查配置",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (BalanceSources.HasSensitiveInlineHeaders(s))
            {
                MessageBox.Show(this, "普通请求头中包含 cookie / authorization。请把它移动到上方「凭据」框。",
                    "凭据未隔离", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (pending.Length > 0 && !pending.Split('\n', '\r').Any(line => line.IndexOf(':') > 0))
            {
                MessageBox.Show(this, "凭据必须是完整的「请求头名称: 值」，例如：\nauthorization: Bearer <Token>",
                    "凭据格式不完整", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            try
            {
                if (pending.Length > 0)
                {
                    string file = string.IsNullOrWhiteSpace(_oldSecret)
                        ? "custom-" + Guid.NewGuid().ToString("N") + ".secret.txt"
                        : Path.GetFileName(_oldSecret);
                    BalanceSources.SaveSecret(file, pending);
                    s.SecretFile = file;
                    _secretInput.Clear();
                    CredentialSaved = true;
                }
                else s.SecretFile = _oldSecret;
                Result = s;
                DialogResult = DialogResult.OK;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "凭据保存失败：" + ex.Message, "自定义余额源",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
