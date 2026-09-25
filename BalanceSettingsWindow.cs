// 余额配置中心：内置平台凭据 + 自定义余额源统一入口。
// 普通配置写 balances.json；cookie/token 只写 LocalAppData 下独立凭据文件，永不回显。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AzhuPet
{
    internal sealed class BalanceSettingsWindow : Window
    {
        private static readonly Brush Bg = B(24, 26, 36), Panel = B(32, 35, 47), Panel2 = B(39, 43, 57);
        private static readonly Brush Muted = B(166, 174, 194), Accent = B(88, 132, 235);
        private static readonly Brush Good = B(82, 196, 132), Bad = B(238, 111, 111), Border = B(61, 66, 84);

        private readonly Action _reload;
        private readonly List<BalanceSource> _items;
        private readonly ListBox _list;
        private readonly TextBlock _traeState, _workbuddyState, _notice;
        private readonly Button _saveButton, _testButton;
        private bool _dirty, _loadOk;

        public BalanceSettingsWindow(Action reload)
        {
            _reload = reload;
            List<BalanceSource> loaded;
            string loadError;
            _loadOk = BalanceSources.TryLoad(out loaded, out loadError);
            _items = loaded;

            Title = "余额配置";
            Width = 760; Height = 650; MinWidth = 680; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true; // 设置窗口是一个完整工作区；从托盘打开后也应能在任务栏找回
            Background = Bg; Foreground = Brushes.White;
            FontFamily = new FontFamily("Microsoft YaHei");

            var root = new Grid { Margin = new Thickness(22, 18, 22, 16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var title = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            title.Children.Add(new TextBlock { Text = "余额配置", FontSize = 23, FontWeight = FontWeights.SemiBold });
            title.Children.Add(new TextBlock
            {
                Text = "内置平台只需更新凭据；其它平台作为自定义接口添加。敏感信息不会写进 balances.json。",
                Foreground = Muted, FontSize = 12.5, Margin = new Thickness(0, 5, 0, 0)
            });
            Grid.SetRow(title, 0); root.Children.Add(title);

            var builtins = new StackPanel();
            builtins.Children.Add(SectionTitle("内置平台", "凭据过期时：点「浏览器登录获取」重新登一次即可；也可手工粘贴。旧内容不回显。"));
            _traeState = new TextBlock();
            _workbuddyState = new TextBlock();
            builtins.Children.Add(BuiltinCard("Trae 积分", "读取可用积分（总额 − 已用）", _traeState,
                () => OpenCredential("Trae", StatusProbe.TraeSecretFile, "authorization")));
            // ⚠ Trae 没有浏览器通道：它的凭据是 **IDE 的**（29 个头，含 X-Medusa / X-Neptune / x-lscbd-*
            //   等客户端签名头），网页登录拿不到同一份 —— 给它一个「浏览器登录」按钮只会让用户白试一趟。
            builtins.Children.Add(BuiltinCard("WorkBuddy 积分", "读取界面同口径的 type=1 可用额度", _workbuddyState,
                () => OpenCredential("WorkBuddy", StatusProbe.WorkBuddySecretFile, "cookie", "x-user-id"),
                () => OpenBrowserLogin()));
            Grid.SetRow(builtins, 1); root.Children.Add(builtins);

            var customHead = new Grid { Margin = new Thickness(0, 16, 0, 8) };
            customHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            customHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            customHead.Children.Add(SectionTitle("自定义接口", "一个来源对应气泡中的一行。双击可编辑。"));
            var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
            tools.Children.Add(SmallButton("＋ 新增", (s, e) => AddEdit(null), true));
            tools.Children.Add(SmallButton("编辑", (s, e) => AddEdit(Selected())));
            tools.Children.Add(SmallButton("复制", (s, e) => Duplicate()));
            tools.Children.Add(SmallButton("启用 / 停用", (s, e) => Toggle()));
            tools.Children.Add(SmallButton("删除", (s, e) => Delete()));
            Grid.SetColumn(tools, 1); customHead.Children.Add(tools);
            Grid.SetRow(customHead, 2); root.Children.Add(customHead);

            _list = new ListBox
            {
                Background = Panel, BorderBrush = Border, BorderThickness = new Thickness(1),
                Padding = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            _list.MouseDoubleClick += (s, e) => { if (Selected() != null) AddEdit(Selected()); };
            _list.KeyDown += (s, e) => { if (e.Key == Key.Delete) Delete(); };
            Grid.SetRow(_list, 3); root.Children.Add(_list);

            var footer = new Grid { Margin = new Thickness(0, 13, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var meta = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _notice = new TextBlock { Foreground = loadError == null ? Muted : Bad, FontSize = 12, Text = loadError ?? "修改后先测试，再保存。" };
            var path = new TextBlock { Foreground = B(120, 128, 148), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                Text = "本机配置：" + BalanceSources.ConfigPath(), ToolTip = BalanceSources.ConfigPath() };
            meta.Children.Add(_notice); meta.Children.Add(path); footer.Children.Add(meta);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            _testButton = ActionButton("测试全部", async (s, e) => await TestAll());
            _saveButton = ActionButton("保存配置", (s, e) => Save(), true);
            actions.Children.Add(_testButton); actions.Children.Add(_saveButton);
            actions.Children.Add(ActionButton("完成", (s, e) => { if (Save()) { _dirty = false; Close(); } }));
            Grid.SetColumn(actions, 1); footer.Children.Add(actions);
            Grid.SetRow(footer, 4); root.Children.Add(footer);
            Content = root;

            _saveButton.IsEnabled = _loadOk;
            RefreshBuiltins(); RefreshList();
            Closing += (s, e) =>
            {
                if (!_dirty) return;
                if (MessageBox.Show(this, "还有未保存的自定义余额源修改，确定关闭吗？", "余额配置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    e.Cancel = true;
            };
        }

        private static Brush B(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
        private FrameworkElement SectionTitle(string title, string sub)
        {
            var p = new StackPanel();
            p.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
            p.Children.Add(new TextBlock { Text = sub, Foreground = Muted, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 8) });
            return p;
        }

        private Border BuiltinCard(string name, string sub, TextBlock state, Action edit, Action browser = null)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Margin = new Thickness(12, 9, 8, 9) };
            text.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.Medium });
            text.Children.Add(new TextBlock { Text = sub, Foreground = Muted, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) });
            g.Children.Add(text);
            state.Margin = new Thickness(8); state.VerticalAlignment = VerticalAlignment.Center;
            state.FontSize = 12; state.FontWeight = FontWeights.SemiBold;
            Grid.SetColumn(state, 1); g.Children.Add(state);
            if (browser != null)
            {
                // 有浏览器通道时，它才是「主路」（不用理解请求头、不用开 DevTools）；
                // 手工粘贴退成备选 —— 但它必须留着：终端用户可能没装 WebView2 运行时。
                var bb = SmallButton("浏览器登录获取", (s, e) => browser(), true);
                bb.VerticalAlignment = VerticalAlignment.Center; bb.Margin = new Thickness(4, 0, 0, 0);
                Grid.SetColumn(bb, 2); g.Children.Add(bb);
                var eb = SmallButton("手工粘贴", (s, e) => edit(), false);
                eb.VerticalAlignment = VerticalAlignment.Center; eb.Margin = new Thickness(4, 0, 10, 0);
                Grid.SetColumn(eb, 3); g.Children.Add(eb);
            }
            else
            {
                var b = SmallButton("更新凭据", (s, e) => edit(), true);
                b.VerticalAlignment = VerticalAlignment.Center; b.Margin = new Thickness(4, 0, 10, 0);
                Grid.SetColumn(b, 2); g.Children.Add(b);
            }
            return new Border { Background = Panel, BorderBrush = Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = g, Margin = new Thickness(0, 0, 0, 7) };
        }

        private Button SmallButton(string text, RoutedEventHandler click, bool primary = false)
        {
            var b = new Button
            {
                Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(9, 4, 9, 4),
                Background = primary ? Accent : Panel2, Foreground = Brushes.White, BorderBrush = Border,
                BorderThickness = primary ? new Thickness(0) : new Thickness(1), Cursor = Cursors.Hand
            };
            b.Click += click; return b;
        }
        private Button ActionButton(string text, RoutedEventHandler click, bool primary = false) { var b = SmallButton(text, click, primary); b.Padding = new Thickness(15, 7, 15, 7); return b; }
        private BalanceSource Selected() => (_list.SelectedItem as ListBoxItem)?.DataContext as BalanceSource;

        private void RefreshBuiltins()
        {
            var slots = StatusProbe.SecretSlots();
            SetState(_traeState, slots[0].exists ? "● 已配置" : "○ 未配置", slots[0].exists);
            SetState(_workbuddyState, slots[1].exists ? "● 已配置" : "○ 未配置", slots[1].exists);
            _traeState.ToolTip = slots[0].path; _workbuddyState.ToolTip = slots[1].path;
        }
        private static void SetState(TextBlock t, string text, bool ok) { t.Text = text; t.Foreground = ok ? Good : Muted; }

        private void RefreshList(Dictionary<string, BalanceCell> tested = null)
        {
            int old = _list.SelectedIndex;
            _list.Items.Clear();
            foreach (var s in _items)
            {
                var row = new Grid { Margin = new Thickness(8, 7, 8, 7) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var left = new StackPanel();
                left.Children.Add(new TextBlock { Text = (s.Enabled ? "" : "[停用] ") + (string.IsNullOrEmpty(s.Name) ? "未命名来源" : s.Name), FontSize = 13.5, FontWeight = FontWeights.Medium, Foreground = s.Enabled ? Brushes.White : Muted });
                string host = "URL 未填写"; Uri u; if (Uri.TryCreate(s.Url, UriKind.Absolute, out u)) host = u.Host;
                left.Children.Add(new TextBlock { Text = (s.Method ?? "GET") + "  ·  " + host + "  ·  " + (s.PathExpr ?? ""), Foreground = Muted, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(left);
                var badge = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, Margin = new Thickness(12, 0, 0, 0) };
                BalanceCell cell;
                if (tested != null && tested.TryGetValue(s.Name, out cell))
                {
                    badge.Text = cell.Ok ? "✓ " + cell.Value.ToString("0.##") + (string.IsNullOrEmpty(cell.Unit) ? "" : " " + cell.Unit) : "✕ " + cell.Error;
                    badge.Foreground = cell.Ok ? Good : Bad;
                }
                else
                {
                    bool secretOk = string.IsNullOrWhiteSpace(s.SecretFile) || File.Exists(BalanceSources.ResolveSecret(s.SecretFile));
                    bool hasSecretRef = !string.IsNullOrWhiteSpace(s.SecretFile);
                    badge.Text = !secretOk ? "凭据缺失"
                        : hasSecretRef ? (s.Enabled ? "凭据已保存 · 待测试" : "凭据已保存 · 已停用")
                        : (s.Enabled ? "待测试" : "已停用");
                    badge.Foreground = secretOk ? Muted : Bad;
                }
                Grid.SetColumn(badge, 1); row.Children.Add(badge);
                _list.Items.Add(new ListBoxItem { Content = row, DataContext = s, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            if (_items.Count == 0)
                _list.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { Text = "还没有自定义接口。Trae / WorkBuddy 已在上方单独配置。", Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12, 26, 12, 26) } });
            if (old >= 0 && old < _list.Items.Count) _list.SelectedIndex = old;
        }

        private void AddEdit(BalanceSource src)
        {
            var dlg = new SourceEditor(src == null ? null : Clone(src)) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            if (src == null) _items.Add(dlg.Result); else _items[_items.IndexOf(src)] = dlg.Result;
            _dirty = true;
            if (dlg.CredentialSaved)
            {
                _notice.Text = "凭据已安全保存；还需点击主面板“保存配置”提交来源设置。";
                _notice.Foreground = Good;
            }
            else Dirty();
            RefreshList();
        }
        private void Duplicate() { var s = Selected(); if (s == null) return; var c = Clone(s); c.Name += "（副本）"; c.Enabled = false; _items.Add(c); Dirty(); RefreshList(); }
        private void Toggle() { var s = Selected(); if (s == null) return; s.Enabled = !s.Enabled; Dirty(); RefreshList(); }
        private void Dirty() { _dirty = true; _notice.Text = "有未保存的修改"; _notice.Foreground = Muted; }
        private void Delete()
        {
            var s = Selected(); if (s == null) return;
            if (MessageBox.Show(this, "从列表中移除“" + s.Name + "”？\n凭据文件会保留，防止误删。", "删除余额源", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _items.Remove(s); Dirty(); RefreshList();
        }

        private bool Save()
        {
            if (!_loadOk) { MessageBox.Show(this, "原配置读取失败。面板不会覆盖它。", "无法保存", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
            var errors = _items.Where(x => x.Enabled).SelectMany(x => BalanceSources.Validate(x).Select(e => x.Name + "：" + e)).ToList();
            if (errors.Count > 0) { MessageBox.Show(this, string.Join("\n", errors), "请检查配置", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            if (_items.Any(BalanceSources.HasSensitiveInlineHeaders))
            { MessageBox.Show(this, "检测到 cookie / authorization 仍写在普通请求头中。请编辑该来源，把敏感头移到“凭据”框。", "凭据未隔离", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
            try
            {
                BalanceSources.Save(_items, BalanceSources.ConfigPath()); _reload?.Invoke(); _dirty = false;
                _notice.Text = "已保存；下次打开状态气泡立即使用新配置。"; _notice.Foreground = Good; return true;
            }
            catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "余额配置", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
        }

        private async Task TestAll()
        {
            var errors = _items.Where(x => x.Enabled).SelectMany(x => BalanceSources.Validate(x).Select(e => x.Name + "：" + e)).ToList();
            if (errors.Count > 0) { MessageBox.Show(this, string.Join("\n", errors), "无法测试", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            _testButton.IsEnabled = false; _testButton.Content = "正在测试…"; _notice.Text = "正在请求已启用的余额接口"; _notice.Foreground = Muted;
            try
            {
                var probe = new StatusProbe { CustomSources = _items.Where(x => x.Enabled).Select(Clone).ToList() };
                var r = await probe.CheckAsync();
                SetState(_traeState, r.TraeOk ? "✓ " + Math.Round(r.TraeAvailable).ToString("0") : (string.IsNullOrEmpty(r.TraeError) ? "○ 未配置" : "✕ " + r.TraeError), r.TraeOk);
                SetState(_workbuddyState, r.WorkbuddyOk ? "✓ " + Math.Round(r.WorkbuddyRemain).ToString("0") : (string.IsNullOrEmpty(r.WorkbuddyError) ? "○ 未配置" : "✕ " + r.WorkbuddyError), r.WorkbuddyOk);
                RefreshList(r.DynamicRows.GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.Last()));
                var slots = StatusProbe.SecretSlots();
                bool ok = (r.TraeOk || !slots[0].exists) && (r.WorkbuddyOk || !slots[1].exists) && r.DynamicRows.All(x => x.Ok);
                _notice.Text = ok ? "测试完成：已配置来源均可用" : "测试完成：有来源需要处理"; _notice.Foreground = ok ? Good : Bad;
            }
            catch (Exception ex) { _notice.Text = "测试失败：" + ex.Message; _notice.Foreground = Bad; }
            finally { _testButton.Content = "测试全部"; _testButton.IsEnabled = true; }
        }

        private void OpenCredential(string platform, string file, params string[] required)
        {
            var dlg = new CredentialEditor(platform, file, required) { Owner = this };
            if (dlg.ShowDialog() == true) { RefreshBuiltins(); _reload?.Invoke(); _notice.Text = platform + " 凭据已更新，可点“测试全部”验证。"; _notice.Foreground = Good; }
        }
        /// <summary>浏览器登录取凭据（WorkBuddy 专用）。成功时窗口内部就带了一次真实回测，
        /// 所以这里只负责刷新卡片与气泡 —— 气泡的余额有 60 秒缓存，不主动丢弃的话最长一分钟仍显示旧的 401。</summary>
        private void OpenBrowserLogin()
        {
            var dlg = new CredentialBrowserWindow(
                "WorkBuddy 积分",
                StatusProbe.WorkBuddySecretFile,
                CredentialCapture.WorkbuddyCapturePattern,
                CredentialCapture.WorkbuddyLoginUrl,
                CredentialCapture.WorkbuddyBalanceUrl,
                _reload) { Owner = this };
            dlg.ShowDialog();
            RefreshBuiltins();
            if (dlg.Saved) { _notice.Text = "WorkBuddy 凭据已从浏览器获取；可点「测试全部」复核。"; _notice.Foreground = Good; }
        }
        private static BalanceSource Clone(BalanceSource s) => new BalanceSource { Name = s.Name, Url = s.Url, Method = s.Method, PathExpr = s.PathExpr, Unit = s.Unit, Enabled = s.Enabled, SecretFile = s.SecretFile, HeadersText = s.HeadersText, Body = s.Body };
    }

    internal sealed class CredentialEditor : Window
    {
        private readonly string _file;
        private readonly string[] _required;
        private readonly TextBox _input;
        public CredentialEditor(string platform, string file, params string[] required)
        {
            _file = file; _required = required ?? new string[0];
            Title = "更新 " + platform + " 凭据"; Width = 610; Height = 455;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 36)); Foreground = Brushes.White; FontFamily = new FontFamily("Microsoft YaHei");
            var root = new Grid { Margin = new Thickness(20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var info = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            info.Children.Add(new TextBlock { Text = "粘贴完整请求", FontSize = 19, FontWeight = FontWeights.SemiBold });
            info.Children.Add(new TextBlock { Text = "格式：第一行接口 URL，后续每行一个“请求头: 值”。也可直接粘贴现有凭据文件全文。\n已有内容不会显示；留空不会覆盖。保存位置仅限本机 LocalAppData。", Foreground = new SolidColorBrush(Color.FromRgb(166, 174, 194)), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
            root.Children.Add(info);
            _input = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(8), Background = new SolidColorBrush(Color.FromRgb(32, 35, 47)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(61, 66, 84)) };
            Grid.SetRow(_input, 1); root.Children.Add(_input);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var save = new Button { Content = "安全保存", Padding = new Thickness(16, 7, 16, 7), Background = new SolidColorBrush(Color.FromRgb(88, 132, 235)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            var cancel = new Button { Content = "取消", Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            save.IsDefault = true;
            save.Click += Save; cancel.Click += (s, e) => DialogResult = false; buttons.Children.Add(save); buttons.Children.Add(cancel); Grid.SetRow(buttons, 2); root.Children.Add(buttons); Content = root;
        }
        private void Save(object sender, RoutedEventArgs e)
        {
            string text = _input.Text.Trim(); if (text.Length == 0) { MessageBox.Show(this, "没有输入内容，原凭据未改动。", "更新凭据"); return; }
            string first = text.Split('\n', '\r').FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim(); Uri u;
            if (!Uri.TryCreate(first, UriKind.Absolute, out u) || (u.Scheme != "https" && u.Scheme != "http")) { MessageBox.Show(this, "第一行应为完整的 http/https 接口 URL。", "格式不正确", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in text.Split('\n', '\r')) { int i = line.IndexOf(':'); if (i > 0) names.Add(line.Substring(0, i).Trim()); }
            var missing = _required.Where(x => !names.Contains(x)).ToList();
            if (missing.Count > 0) { MessageBox.Show(this, "缺少必要请求头：" + string.Join("、", missing), "格式不完整", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            try { BalanceSources.SaveSecret(_file, text); _input.Clear(); DialogResult = true; }
            catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "更新凭据", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    internal sealed class SourceEditor : Window
    {
        private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(166, 174, 194));
        private readonly TextBox _name, _url, _path, _unit, _secretInput, _headers, _body;
        private readonly ComboBox _method;
        private readonly CheckBox _enabled;
        private readonly TextBlock _testState;
        private readonly string _oldSecret;
        public BalanceSource Result { get; private set; }
        public bool CredentialSaved { get; private set; }

        public SourceEditor(BalanceSource src)
        {
            src = src ?? new BalanceSource { Method = "GET", Enabled = true }; _oldSecret = src.SecretFile ?? "";
            Title = string.IsNullOrEmpty(src.Name) ? "新增自定义余额源" : "编辑 " + src.Name;
            Width = 650; Height = 700; MinHeight = 610; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 36)); Foreground = Brushes.White; FontFamily = new FontFamily("Microsoft YaHei");
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var root = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };
            root.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(src.Name) ? "添加自定义接口" : "编辑自定义接口", FontSize = 20, FontWeight = FontWeights.SemiBold });
            root.Children.Add(new TextBlock { Text = "告诉桌宠：请求哪里、如何鉴权、从响应 JSON 的哪里取数字。", Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 4, 0, 12) });
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(135) });
            var n = Field("显示名称", src.Name); _name = n.box; row.Children.Add(n.host);
            var m = new StackPanel { Margin = new Thickness(12, 0, 0, 0) }; m.Children.Add(Label("请求方法"));
            _method = new ComboBox { ItemsSource = new[] { "GET", "POST" }, SelectedItem = (src.Method ?? "GET").ToUpperInvariant(), Height = 29 }; m.Children.Add(_method); Grid.SetColumn(m, 1); row.Children.Add(m); root.Children.Add(row);
            _url = AddField(root, "接口 URL", src.Url, "https://api.example.com/user/balance");
            _path = AddField(root, "余额字段路径", src.PathExpr, "例如 total_balance 或 data.account.remain");
            root.Children.Add(new TextBlock { Text = "数组求和：accounts[type=1].remain    ·    相减：sub:data.total;data.used", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
            _unit = AddField(root, "显示单位（可选）", src.Unit, "积分 / 次 / ¥");
            root.Children.Add(Label("凭据（可选）"));
            root.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(_oldSecret) ? "尚未关联凭据；粘贴 authorization / cookie 等敏感请求头。" : "已安全关联 " + Path.GetFileName(_oldSecret) + "；下方留空即保留，旧内容不会回显。", Foreground = Muted, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 4) });
            _secretInput = new TextBox { Height = 68, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(7), Background = new SolidColorBrush(Color.FromRgb(32, 35, 47)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(61, 66, 84)), ToolTip = "每行一个敏感请求头，例如 authorization: Bearer …" }; root.Children.Add(_secretInput);
            var advanced = new Expander { Header = "高级选项：非敏感请求头与请求体", Foreground = Brushes.White, Margin = new Thickness(0, 12, 0, 0) };
            var adv = new StackPanel { Margin = new Thickness(0, 6, 0, 0) }; _headers = AddField(adv, "普通请求头（每行 name: value）", src.HeadersText, "content-type: application/json", 70); _body = AddField(adv, "POST 请求体", src.Body, "{}", 70); advanced.Content = adv; root.Children.Add(advanced);
            _enabled = new CheckBox { Content = "启用并显示在状态气泡中", IsChecked = src.Enabled, Margin = new Thickness(0, 14, 0, 0), Foreground = Brushes.White }; root.Children.Add(_enabled);
            _testState = new TextBlock { Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap }; root.Children.Add(_testState);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var test = Btn("测试连接", null); test.Click += async (s, e) => await Test(test);
            var apply = Btn("保存并返回", Save, true); apply.IsDefault = true;
            var cancel = Btn("取消", (s, e) => DialogResult = false); cancel.IsCancel = true;
            buttons.Children.Add(test); buttons.Children.Add(apply); buttons.Children.Add(cancel); root.Children.Add(buttons);
            scroll.Content = root; Content = scroll;
        }

        private static TextBlock Label(string text) => new TextBlock { Text = text, Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 8, 0, 3) };
        private (StackPanel host, TextBox box) Field(string label, string value, string hint = null, double height = double.NaN)
        {
            var p = new StackPanel(); p.Children.Add(Label(label));
            var b = new TextBox
            {
                Text = value ?? "", Height = height, Padding = new Thickness(6, 4, 6, 4), ToolTip = hint,
                AcceptsReturn = !double.IsNaN(height),
                VerticalScrollBarVisibility = !double.IsNaN(height) ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                Background = new SolidColorBrush(Color.FromRgb(32, 35, 47)), Foreground = Brushes.White,
                CaretBrush = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(61, 66, 84))
            };
            p.Children.Add(b); return (p, b);
        }
        private TextBox AddField(StackPanel root, string label, string value, string hint, double height = double.NaN) { var f = Field(label, value, hint, height); root.Children.Add(f.host); return f.box; }
        private Button Btn(string text, RoutedEventHandler click, bool primary = false)
        {
            var b = new Button { Content = text, Padding = new Thickness(15, 7, 15, 7), Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.White, Background = new SolidColorBrush(primary ? Color.FromRgb(88, 132, 235) : Color.FromRgb(39, 43, 57)), BorderThickness = primary ? new Thickness(0) : new Thickness(1) };
            if (click != null) b.Click += click; return b;
        }
        private BalanceSource Build(bool forTest)
        {
            string secret = _oldSecret, typed = _secretInput.Text.Trim(), headers = _headers.Text.Trim();
            if (forTest && typed.Length > 0) { headers += (headers.Length > 0 ? "\n" : "") + typed; secret = ""; }
            return new BalanceSource { Name = _name.Text.Trim(), Url = _url.Text.Trim(), Method = (_method.SelectedItem as string) ?? "GET", PathExpr = _path.Text.Trim(), Unit = _unit.Text.Trim(), Enabled = _enabled.IsChecked == true, SecretFile = secret, HeadersText = headers, Body = _body.Text.Trim() };
        }
        private async Task Test(Button button)
        {
            var s = Build(true); var errors = BalanceSources.Validate(s);
            if (errors.Count > 0) { ShowTest(string.Join("；", errors), false); return; }
            button.IsEnabled = false; button.Content = "正在测试…"; _testState.Text = "正在请求接口"; _testState.Foreground = Muted;
            try { var c = await new StatusProbe().TestSourceAsync(s); ShowTest(c.Ok ? "✓ 成功读到 " + c.Value.ToString("0.##") + (string.IsNullOrEmpty(c.Unit) ? "" : " " + c.Unit) : "✕ " + c.Error, c.Ok); }
            catch (Exception ex) { ShowTest("✕ " + ex.Message, false); }
            finally { button.Content = "测试连接"; button.IsEnabled = true; }
        }
        private void ShowTest(string s, bool ok) { _testState.Text = s; _testState.Foreground = new SolidColorBrush(ok ? Color.FromRgb(82, 196, 132) : Color.FromRgb(238, 111, 111)); }
        private void Save(object sender, RoutedEventArgs e)
        {
            var s = Build(false); string pending = _secretInput.Text.Trim(); if (pending.Length > 0) s.SecretFile = "";
            var errors = BalanceSources.Validate(s); if (errors.Count > 0) { MessageBox.Show(this, string.Join("\n", errors), "请检查配置", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (BalanceSources.HasSensitiveInlineHeaders(s)) { MessageBox.Show(this, "普通请求头中包含 cookie / authorization。请把它移动到上方“凭据”框。", "凭据未隔离", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (pending.Length > 0 && !pending.Split('\n', '\r').Any(line => line.IndexOf(':') > 0))
            {
                MessageBox.Show(this, "凭据必须是完整的“请求头名称: 值”，例如：\nauthorization: Bearer <Token>", "凭据格式不完整", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                if (pending.Length > 0)
                {
                    string file = string.IsNullOrWhiteSpace(_oldSecret) ? "custom-" + Guid.NewGuid().ToString("N") + ".secret.txt" : Path.GetFileName(_oldSecret);
                    BalanceSources.SaveSecret(file, pending); s.SecretFile = file; _secretInput.Clear(); CredentialSaved = true;
                }
                else s.SecretFile = _oldSecret;
                Result = s; DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, "凭据保存失败：" + ex.Message, "自定义余额源", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }
}
