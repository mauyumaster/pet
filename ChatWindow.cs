// 「和桌宠说话」聊天窗：聊天气泡 + 多轮历史，回车发送。
// 消息发往 TraeChat(明文 llm_utils_chat 兼容通道)，不扣积分。
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AzhuPet
{
    internal sealed class ChatWindow : Window
    {
        private readonly List<Dictionary<string, object>> _history = new List<Dictionary<string, object>>();
        private readonly StackPanel _msgPanel;
        private readonly TextBox _input;
        private readonly Button _send;
        private readonly ScrollViewer _scroller;
        private bool _busy;

        public ChatWindow()
        {
            Title = "和桌宠说话";
            Width = 380; Height = 520;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            FontSize = 13;

            _msgPanel = new StackPanel { Margin = new Thickness(10) };
            _scroller = new ScrollViewer { Content = _msgPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            _send = new Button { Content = "发送", Width = 56, Height = 30, IsEnabled = false, Margin = new Thickness(6, 0, 0, 0) };
            _input = new TextBox
            {
                AcceptsReturn = false,
                Height = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _input.KeyDown += OnKeyDown;
            var inputBar = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(_send, Dock.Right);
            inputBar.Children.Add(_send);
            inputBar.Children.Add(_input);

            var root = new DockPanel { Margin = new Thickness(10) };
            DockPanel.SetDock(inputBar, Dock.Bottom);
            root.Children.Add(inputBar);
            root.Children.Add(_scroller);
            Content = root;

            _send.Click += (s, e) => Send();
            _input.TextChanged += (s, e) => _send.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(_input.Text);
            _input.Focus();

            // 首句来自 persona.md 的 [[greeting]]，不再硬编码寒暄 ——
            // 那三条随机问候（"哈啰～"）按人格页 §4 属于禁用的卖萌腔，且与人格无关。
            // 每次开窗重读一次，所以你改完 persona.md 立刻能看到效果，不必重启进程。
            Persona.Reload(null);
            AddBubble("assistant", Persona.Greeting);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !_busy && !string.IsNullOrWhiteSpace(_input.Text))
            {
                e.Handled = true;
                Send();
            }
        }

        private async void Send()
        {
            if (_busy) return;
            string text = _input.Text.Trim();
            if (text.Length == 0) return;

            _input.Clear();
            _busy = true;
            _input.IsEnabled = false;
            _send.IsEnabled = false;

            AddBubble("user", text);
            _history.Add(Msg("user", text));

            AddBubble("assistant", "正在思考…");
            var (ok, reply) = await TraeChat.ChatAsync(_history);
            PopLastBubble();

            if (ok && reply.Trim().Length > 0)
            {
                AddBubble("assistant", reply.Trim());
                _history.Add(Msg("assistant", reply.Trim()));
            }
            else
            {
                AddBubble("assistant", "（没回复成功）" + (string.IsNullOrEmpty(reply) ? "" : "\n" + reply));
            }

            _busy = false;
            _input.IsEnabled = true;
            _send.IsEnabled = !string.IsNullOrWhiteSpace(_input.Text);
            _input.Focus();
        }

        private static Dictionary<string, object> Msg(string role, string text)
        {
            return new Dictionary<string, object>
            {
                ["role"] = role,
                ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = text } },
            };
        }

        private void PopLastBubble()
        {
            if (_msgPanel.Children.Count > 0) _msgPanel.Children.RemoveAt(_msgPanel.Children.Count - 1);
        }

        private void AddBubble(string role, string text, string kind = null)
        {
            bool mine = role == "user";
            bool isErr = kind == "err";
            var tb = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 250,
                LineHeight = 20,
                Foreground = mine ? Brushes.White : isErr ? new SolidColorBrush(Color.FromRgb(155, 66, 66)) : Brushes.Black,
            };
            tb.Inlines.AddRange(BuildInlines(text));
            var border = new Border
            {
                Background = new SolidColorBrush(mine ? Color.FromRgb(74, 96, 152) : isErr ? Color.FromRgb(250, 233, 233) : Color.FromRgb(240, 242, 245)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                Child = tb,
                HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 270,
                Margin = new Thickness(0, 4, 0, 4),
            };
            _msgPanel.Children.Add(border);
            _scroller.ScrollToEnd();
            Dispatcher.BeginInvoke(new Action(() => _scroller.ScrollToEnd()), DispatcherPriority.Background);
        }

        private static readonly Regex _md = new Regex(@"(\*\*[^*\n]+\*\*|\*[^*\n]+\*|`[^`\n]+`)", RegexOptions.Compiled);

        /// <summary>轻量 markdown 行内渲染：**粗体**、*斜体*、`代码`，其余原样（\n 由 Run 保留为换行）。</summary>
        private static IEnumerable<Inline> BuildInlines(string text)
        {
            var list = new List<Inline>();
            if (string.IsNullOrEmpty(text)) return list;
            int idx = 0;
            foreach (Match m in _md.Matches(text))
            {
                if (m.Index > idx) list.Add(new Run(text.Substring(idx, m.Index - idx)));
                string tok = m.Value;
                if (tok.Length >= 4 && tok.StartsWith("**", StringComparison.Ordinal))
                {
                    list.Add(new Run(tok.Substring(2, tok.Length - 4)) { FontWeight = FontWeights.Bold });
                }
                else if (tok.Length >= 2 && tok[0] == '`')
                {
                    list.Add(new Run(tok.Substring(1, tok.Length - 2))
                    {
                        FontFamily = new FontFamily("Consolas"),
                        Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)),
                    });
                }
                else
                {
                    list.Add(new Run(tok.Substring(1, tok.Length - 2)) { FontStyle = FontStyles.Italic });
                }
                idx = m.Index + m.Length;
            }
            if (idx < text.Length) list.Add(new Run(text.Substring(idx)));
            return list;
        }
    }
}