// 设置主面板（2026-09-20 重做版式）—— 所有需要配置的东西收在一处。
//
// 为什么要它：此前配置散在四处 —— 托盘勾选项（说话／读屏／模型）／台词模型设置窗（openai 三字段）／
// 余额配置窗（来源与凭据）／config.json 手改（库路径、总结间隔）。用户要的是「打开一个窗，配完所有」。
//
// ---------------------------------------------------------------------------------------
// 版式（第二轮返工，2026-09-20）—— 旧版是「一个长表单从头堆到尾」，被用户判为「太简陋、全挤在一起」。
// 现版式按 `ardot-ui-design`（web-app 指南）重构，逐条对应：
//
//   ① Purpose First / Dominant Region  —— 一栏一个主题，右侧是唯一主区域。
//   ② Spatial Logic: 两个结构区         —— 左侧**分类栏**（去哪一栏）+ 右侧**内容区**（这一栏干什么）。
//                                          WinForms 没有路由，所以每栏是一个独立 Panel，切栏目＝显隐 Panel。
//   ③ Progressive Disclosure            —— 高级项折进「高级」折叠区，首屏只留你一定会决定的。
//   ④ Recognition over Recall           —— 左栏常驻，当前栏有强调竖条；副标题写清这一栏管什么。
//   ⑤ Structural Consistency            —— 区间距 24 / 卡内 16 / 卡间 12 —— 全部取自 SettingsTheme，
//                                          控件级间距也走同一套（Groups，不再是随手写的 7px）。
//   ⑥ System Status Visibility          —— 底部状态条常驻显示「已保存 / 有未保存修改 / 保存失败」，
//                                          不再「点了没反应」。
//   ⑦ Action Hierarchy                  —— 整窗只有**一个**主按钮（保存并生效）；取消/恢复默认是次按钮。
//
// ⚠ 三条纪律（改本文件前先读）：
// 1. **下发一律走 `PetWindow.ApplyConfig()`**（唯一入口）；面板里不许手写 `OcrEye.SendText = ...`。
//    判据 `configApplyIsSingleEntry` 会扫这个文件，违反即红。
// 2. **每个开关要能回答「它在哪个档位下完全没作用」**（本仓纪律）：灰掉／加注说明，
//    不留给用户「勾了没反应」的坑。见 `RefreshEnabledState()`。
// 3. **凭据不回显**：key 类字段留空即保留（沿用 BalanceSettingsWindow 的先例）。
//
// 视觉层（颜色/字体/间距/自绘控件）全在 `SettingsTheme.cs`，本文件只声明「这是什么控件」。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AzhuPet
{
    /// <summary>
    /// 设置面板对它「寄主」的全部要求。
    ///
    /// 为什么要这个接口（而不是直接收一个 <see cref="PetWindow"/>）：
    /// ① **可测**：`--settingstest` 要能离线构造面板量版式，不该为了量一下控件高度
    ///    先加载 GLB 模型、起 WPF 渲染器 —— 那是「为了验证 A 先把 B 整条链拉起来」，
    ///    一旦 B 挂了 A 的判据也跟着红，分不清是谁的错。
    /// ② **职责**：面板只做「读配置 → 改配置 → 通知生效」，它不需要知道寄主是桌宠还是别的什么壳。
    /// ③ 依赖面从「整个 PetWindow 的公开成员」缩到**这 4 个**，将来寄主换了，面板一字不改。
    /// </summary>
    internal interface ISettingsHost
    {
        PetConfig Cfg { get; }
        /// <summary>把配置下发到运行中的各个开关（唯一入口，见 PetWindow.ApplyConfig）。</summary>
        void ApplyConfig();
        /// <summary>换尺寸（几何要跟着重算，不是改个数字就完事）。</summary>
        void SetSize(int idx);
        /// <summary>余额来源被改动后重载。</summary>
        void ReloadBalanceSources();
    }

    internal sealed class SettingsWindow : Form
    {
        private readonly ISettingsHost _w;

        // ---- 控件引用（保存时统一回读）----
        private CheckBox _cSpeech, _cLlm, _cRoast, _cSummary, _cOcr, _cOcrSend, _cEye;
        private NumericUpDown _nInterval;
        private TextBox _tVault, _tBase, _tModel, _tKey;
        private CheckBox _cTopmost, _cNight, _cAutostart;
        private ComboBox _cSize;
        private Toggle _tSpeech, _tLlm, _tRoast, _tSummary, _tOcr, _tOcrSend, _tEye, _tTopmost, _tNight, _tAutostart;

        // ---- 版式骨架 ----
        private Panel _nav, _content;
        private readonly List<NavItem> _navItems = new List<NavItem>();
        private readonly List<Panel> _pages = new List<Panel>();
        private readonly List<FlowLayoutPanel> _flows = new List<FlowLayoutPanel>();
        private readonly List<Panel> _heads = new List<Panel>();
        private int _current = -1;
        private Label _statusText;
        private Button _saveBtn;
        private bool _dirty;

        private const int NavWidth = 210;   // 196 会让「读屏范围与隐私边界」这种副标题差几个像素被裁掉
        private const int PadX = 24;          // 内容区左右留白
        private const int PadY = 20;          // 内容区上下留白
        private const int CardPad = 16;       // 卡片内部留白

        public SettingsWindow(ISettingsHost w)
        {
            _w = w;
            SettingsTheme.InitFonts();
            var pal = SettingsTheme.Pal;

            Text = "阿助设置";
            // ⚠⚠ 这一行不是可选项（第一版就栽在这里）：
            //   WinForms 的 AutoScaleMode 默认是 **Font** —— 设完 Font 再设 ClientSize 时，
            //   窗体尺寸会按「当前字体 / 设计期字体」的比例被**偷偷缩放**。
            //   本窗用 9.5f 的雅黑，与默认 8.25f 不同 ⇒ 880×620 被缩成 601×451，
            //   整条 Dock 链跟着错位、绘制区域对不上，屏幕上一片红色叉。
            //   Dpi 模式才是这个窗真正想要的语义（跟随屏幕缩放，不跟随字体度量）。
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = pal.Window;
            ForeColor = pal.Text;
            Font = SettingsTheme.Body;
            ClientSize = new Size(880, 620);
            MinimumSize = new Size(760, 540);
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            // ------------------------------------------------------------------
            // 底部操作条（先 Dock 底，再 Dock 左，最后填中间 —— 这样不会被挤小）
            // ------------------------------------------------------------------
            var footer = BuildFooter();
            Controls.Add(footer);

            // 左侧分类栏
            _nav = new Panel { Dock = DockStyle.Left, Width = NavWidth, BackColor = pal.Sidebar };
            _nav.Paint += (s, e) =>
            {
                using (var p = new Pen(pal.Line))
                    e.Graphics.DrawLine(p, _nav.Width - 1, 0, _nav.Width - 1, _nav.Height);
            };
            Controls.Add(_nav);
            BuildNav();

            // 右侧内容区
            _content = new Panel { Dock = DockStyle.Fill, BackColor = pal.Window, AutoScroll = true };
            Controls.Add(_content);
            BuildPages();

            // ⚠⚠ Dock 的 z-order 顺序是这里唯一容易搞错的地方（真实事故：截图上左栏整条不见了）。
            //   WinForms 按 **z-order 从后往前**依次切分剩余空间：
            //     · `Dock=Fill` 必须**最后**处理 —— 它吃掉剩下的全部。
            //     · `Dock=Bottom` / `Dock=Left` 必须在它**之前**处理，否则拿不到空间。
            //   而 `Controls.Add()` 的先后与 z-order **相反**：后 Add 的 z-order 更靠前
            //   ⇒ 后 Add 的反而**更晚**被 Dock 处理。
            //   实测（/tmp 最小复现，三种组合都量过）：
            //     Add(footer, nav, content) 且不调 z 序
            //       ⇒ content 拿到 880×620（全屏）、nav 只有 196 但被压在下面看不见 —— **就是这次的 bug**
            //     Add(footer, nav, content) 后 `content.BringToFront()`
            //       ⇒ content 684×560、nav 196 宽可见 —— **正确**
            //   ⚠ 注意只需 content.BringToFront()；**不要**顺手把 nav/footer 也 BringToFront，
            //     那会把 content 又压回去，等于没修（我第一版就是这么"修"的）。
            _content.BringToFront();

            ShowPage(0);
            CheckChanged(null, EventArgs.Empty);

            // ⚠ 窗体可缩放 ⇒ 宽度链必须在每次 Resize 后重排一次。
            //   不重排的后果（第一版实测）：卡片仍是初始宽度，右半边整片空白 + 错位红叉。
            _content.Resize += (s, e) => Relayout();
            Resize += (s, e) => Relayout();

            // ⚠ 脏标记：任何控件变动都让状态条说实话（System Status Visibility）。
            WireDirty();

            // 首帧后再排一次：构造期 ClientSize 可能还没跟上 DPI 缩放
            Shown += (s, e) => Relayout();
        }

        // ==================================================================================
        // 左栏：分类
        // ==================================================================================
        private void BuildNav()
        {
            var pal = SettingsTheme.Pal;

            var brand = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Color.Transparent };
            brand.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(pal.Accent))
                    g.FillEllipse(b, 20, 22, 12, 12);          // 一个小圆点当 logo（不引资源）
                using (var b = new SolidBrush(pal.Text))
                    g.DrawString("阿助", SettingsTheme.BodyBold, b, new PointF(42, 19));
                using (var b = new SolidBrush(pal.Faint))
                    g.DrawString("桌面伙伴 · 设置", SettingsTheme.Small, b, new PointF(42, 38));
            };
            _nav.Controls.Add(brand);

            var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(8, 4, 8, 8) };
            _nav.Controls.Add(host);

            // 顺序＝用户第一次打开会依次想的顺序：先「她怎么说话」，再「她靠什么说话」…
            AddNav(host, "◍", "说话与吐槽", "她会主动开口吗");
            AddNav(host, "⌁", "模型通道", "台词与小结算哪儿");
            AddNav(host, "◉", "她能看见什么", "读屏范围与隐私");
            AddNav(host, "▤", "每小时小结", "她替你写的日记");
            AddNav(host, "◧", "外观与启动", "尺寸、置顶、开机");
            AddNav(host, "ⓘ", "关于与位置", "配置在哪、怎么自检");
        }

        private void AddNav(Panel host, string glyph, string title, string sub)
        {
            var it = new NavItem
            {
                Glyph = glyph, Title = title, Subtitle = sub,
                Dock = DockStyle.Top, Height = 56,
            };
            int idx = _navItems.Count;
            it.Click += (s, e) => ShowPage(idx);
            foreach (Control c in it.Controls) c.Click += (s, e) => ShowPage(idx);
            _navItems.Add(it);
            // Dock 顺序：后加的在上，所以按倒序插入才能保持声明的顺序
            host.Controls.Add(it);
            host.Controls.SetChildIndex(it, 0);
        }

        private void ShowPage(int idx)
        {
            if (idx < 0 || idx >= _pages.Count) return;
            _current = idx;
            for (int i = 0; i < _pages.Count; i++)
            {
                _pages[i].Visible = (i == idx);
                _navItems[i].Active = (i == idx);
            }
            _content.AutoScrollPosition = new Point(0, 0);
        }

        // ==================================================================================
        // 右栏：一栏一个 Panel（内含若干卡片）
        // ==================================================================================
        private void BuildPages()
        {
BuildPageSpeech();BuildPageModel();BuildPagePrivacy();BuildPageSummary();BuildPageAppearance();BuildPageAbout();        }

        /// <summary>开一页并返回「卡片流」宿主。
        /// ⚠ 版式要点（第一版就在这里翻过车）：
        ///   • 页面 = AutoScroll 的 Panel；里面放**一个** Dock=Top 的 FlowLayoutPanel，
        ///     它自己 AutoSize —— 这样滚动条由页面给，内容高度由卡片流真实撑开。
        ///   • **不给 page 设 Dock=Fill 之外的花样**，也不给 flow 设固定高度：
        ///     上一版把 page 与 flow 都设成 Dock 链，结果 flow 只报到 451px 高，卡片全被裁掉，
        ///     只剩一堆错位红叉。
        ///   • 宽度在 <see cref="Relayout"/> 里统一刷新（窗体可缩放 ⇒ 不能只在构造时算一次）。</summary>
        private FlowLayoutPanel BeginPage(string title, string desc)
        {
            var page = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.Transparent,
                Visible = false,
                AutoScroll = true,
            };
            var flow = new FlowLayoutPanel
            {
                Location = new Point(0, 0),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(PadX, PadY, PadX, PadY),
            };
            page.Controls.Add(flow);

            // 页眉高度按字体实际高度算（H1 + 间距 + Body + 余量），不写死 64 ——
            // 写死就等着 15pt 标题把副标题压住（第一版截图上的叠字）。
            var head = new SizePanel(title, desc)
            {
                Height = SettingsTheme.H1.Height + 4 + SettingsTheme.Body.Height + 12,
                Margin = new Padding(0, 0, 0, SettingsTheme.GapSm),
            };
            flow.Controls.Add(head);
            _heads.Add(head);

            _content.Controls.Add(page);
            _pages.Add(page);
            _flows.Add(flow);
            return flow;
        }

        /// <summary>页眉：大标题 + 一句「这一栏管什么」。自绘（高度算得准）。</summary>
        private sealed class SizePanel : Panel
        {
            private readonly string _title, _desc;
            public SizePanel(string title, string desc)
            {
                _title = title; _desc = desc;
                // ⚠ 同 Toggle：自绘面板放在自绘父容器里，Transparent 取不到底色 ⇒ 给实色。
                BackColor = SettingsTheme.Pal.Window;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                         | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var pal = SettingsTheme.Pal;
                try
                {
                    e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    if (Width > 60)
                    {
                        // ⚠⚠ 行距按**字体实际高度**算，不写死 30 ——
                        //   真实事故：第一版 H1 画在 y=2、副标题画在 y=32，
                        //   而 H1 是 15pt（96 DPI 下约 26px 高），两行就叠在一起了。
                        //   截图上就是「标题和副标题糊成一团」。
                        int y = 2;
                        using (var b = new SolidBrush(pal.Text))
                            e.Graphics.DrawString(_title, SettingsTheme.H1, b, new PointF(0, y));
                        y += SettingsTheme.H1.Height + 4;
                        if (Height >= y + SettingsTheme.Body.Height)
                            using (var b = new SolidBrush(pal.Muted))
                                e.Graphics.DrawString(_desc, SettingsTheme.Body, b, new PointF(0, y));
                    }
                }
                catch { }
                // ⚠ 同样不调 base.OnPaint —— 本控件声明了 UserPaint（自己画），
                //   调 base 只会多走一遍框架流程、并可能触发别人的 Paint 订阅把画面盖掉。
            }
        }

        private int ContentWidth()
        {
            // ⚠ 卡片宽必须按**宿主卡片流的内部宽度**算，不能自己再算一遍窗体宽度 ——
            //   自己算的那份会和实际宿主差几像素（padding / 滚动条 / 边框），
            //   差出来的那几像素就是「最右边一列永远看不全」。
            //   ⚠⚠ 这条是 `--settingstest` 的 cardFitsFlow 量出来的：第一版卡片 812 > 宿主 804。
            var flow = _flows.Count > 0 ? _flows[0] : null;
            int avail;
            if (flow != null && flow.ClientSize.Width > 0)
                avail = flow.ClientSize.Width - flow.Padding.Horizontal;
            else
                avail = ClientSize.Width - NavWidth - PadX * 2 - 24;
            return Math.Max(360, avail);
        }

        /// <summary>按当前窗体宽度重排所有页面的宽度链（窗体可缩放 ⇒ 每次 Resize 都要走一遍）。
        /// ⚠ 顺序要紧：**先让页面/卡片流自己排好**（它们的宽度来自 Dock 链），
        ///   再按卡片流的真实客户区宽去定卡片宽。反过来做 ⇒ 拿的是上一帧的宽度。</summary>
        private void Relayout()
        {
            foreach (var page in _pages) page.PerformLayout();
            foreach (var flow in _flows) flow.PerformLayout();

            int w = ContentWidth();
            foreach (var flow in _flows)
            {
                // ⚠ AutoSize 的 FlowLayoutPanel **不会**跟着父容器变宽（它按内容自定尺寸），
                //   所以这里显式把宿主拉满 —— 否则卡片流永远停在首次布局的宽度上，
                //   表现就是「窗口拉大了，右边一大片空白」。
                if (flow.Parent != null && flow.Parent.ClientSize.Width > 0)
                {
                    int target = flow.Parent.ClientSize.Width;
                    if (flow.Width != target) flow.Width = target;
                    int avail = target - flow.Padding.Horizontal;
                    if (avail > 200) w = Math.Max(360, avail);
                }
                foreach (Control c in flow.Controls)
                {
                    c.Width = w;
                    if (c is Card) RelayoutCard((Card)c);
                }
            }
            foreach (var h in _heads) h.Width = w;
        }

        /// <summary>造一张卡片：标题 + 可选副标题 + 若干行。返回卡片，调用方继续往里 AddRow。
        /// 卡片高在 <see cref="FinishCard"/> 里按实际行数算 —— 手工写死高度正是旧版错位的来源。</summary>
        private Card BeginCard(FlowLayoutPanel host, string title, string sub = null)
        {
            var card = new Card { Width = ContentWidth(), BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, SettingsTheme.GapMd), Title = title ?? "", Sub = sub ?? "" };
            card.Tag = new CardCtx { Title = title, Sub = sub, Rows = new List<Control>(), Pads = new List<int>() };
            host.Controls.Add(card);
            return card;
        }

        private sealed class CardCtx
        {
            public string Title, Sub;
            public List<Control> Rows;
            public List<int> Pads;
            public int TitleH;
        }

        /// <summary>往卡片里加一行。padLeft 用于「子项缩进」（隶属于上一个开关的行，如输入框）。</summary>
        private void AddRow(Card card, Control row, int padLeft = 0)
        {
            var ctx = (CardCtx)card.Tag;
            ctx.Rows.Add(row);
            ctx.Pads.Add(padLeft);
            card.Controls.Add(row);
        }

        /// <summary>结算卡片：算标题高度、摆放行、定高。**可在 Resize 时重复调用**（幂等），
        /// 所以它只依赖 <see cref="CardCtx"/> 里的数据与卡片当前宽度，不累积任何状态。</summary>
        private void FinishCard(Card card)
        {
            var ctx = (CardCtx)card.Tag;
            // 标题高度：标题 22 + 副标题 18（副标题要换行时按实际行数加）
            int th = 0;
            if (!string.IsNullOrEmpty(ctx.Title)) th += 24;
            if (!string.IsNullOrEmpty(ctx.Sub)) th += 20;
            ctx.TitleH = th;
            RelayoutCard(card);
        }

        /// <summary>按卡片当前宽度重排行并重算高度。</summary>
        private void RelayoutCard(Card card)
        {
            var ctx = card.Tag as CardCtx;
            if (ctx == null) return;

            int inner = Math.Max(80, card.Width - CardPad * 2);
            int y = CardPad + (ctx.TitleH > 0 ? ctx.TitleH + SettingsTheme.GapSm : 0);

            for (int i = 0; i < ctx.Rows.Count; i++)
            {
                var r = ctx.Rows[i];
                int pad = ctx.Pads[i];
                r.Left = CardPad + pad;
                r.Width = Math.Max(60, inner - pad * 2);
                r.Top = y;
                // ⚠ 说明段（Note/NoteBox）是「自己量好尺寸」的：宽度取卡片的可用宽度，
                //   高度按换行后的真实行数。必须在**卡片有宽度之后**量，所以摆在这一步。
                if (r is Label lbl && !lbl.AutoSize) FitTextLabel(lbl, card.Width - CardPad * 2 - pad);
                int h = RowHeight(r);
                r.Height = Math.Max(h, r.Height > 0 ? Math.Min(h, r.Height) : h);
                if (r is Panel p) p.PerformLayout();
                y += h + (i == ctx.Rows.Count - 1 ? 0 : SettingsTheme.GapSm);
            }
            card.Height = Math.Max(64, y + CardPad);
        }

        private static int RowHeight(Control c)
        {
            if (c is Toggle) return 24;
            if (c is Label)
            {
                var l = (Label)c;
                // ⚠ AutoSize 的标签用 PreferredSize 拿换行高度；但说明段（Note/NoteBox）
                //   是 **AutoSize=false** 的 —— 它的 Height 在 FitTextLabel 里已经量好了，
                //   直接采信，**不要**再去问框架（那条路正是会间歇性抛 GDI+ 异常的路）。
                int need = l.AutoSize ? l.PreferredHeight : l.Height;
                return Math.Max(16, need);
            }
            return Math.Max(24, c.Height);
        }

        // ==================================================================================
        // 第 1 栏：说话与吐槽
        // ==================================================================================
        private void BuildPageSpeech()
        {
            var flow = BeginPage("说话与吐槽", "控制她什么时候、以什么方式开口。");

            var c1 = BeginCard(flow, "开口",
                "关掉「她会自己说话」＝她只在你打字时回答，下面两项都会停。");
            var _trow1 = ToggleRow("她会自己说话", "不依赖你的输入，主动冒话", out _tSpeech, out _cSpeech, _w.Cfg.SpeechOn);
            AddRow(c1, _trow1);
            var _trow2 = ToggleRow("台词用模型生成", "关＝免费模板句（零成本、逐字节可预测）；开＝走「模型通道」", out _tLlm, out _cLlm, _w.Cfg.SpeechLlm);
            AddRow(c1, _trow2);
            var _trow3 = ToggleRow("时不时吐槽一句", "同一应用里换了页面／文档就评一句；长时间安静则冒一句保底", out _tRoast, out _cRoast, _w.Cfg.RoastOn);
            AddRow(c1, _trow3);
            FinishCard(c1);

            var c2 = BeginCard(flow, "触发节奏",
                "这三条是「她多久冒一次话」的口径，暂时锁在默认值。");
            AddRow(c2, Note("• 停留多久才算「她看到了」：8 秒\n"
                          + "• 两次吐槽之间的最短间隔：200 秒\n"
                          + "• 每天最多主动说：200 句\n\n"
                          + "改动它们要三个数字配套调（例如冷却必须大于闸门冷却），单独放开容易让她刷屏 —— "
                          + "需要时告诉我，我一起调。", SettingsTheme.Pal.Muted));
            FinishCard(c2);
        }

        // ==================================================================================
        // 第 2 栏：模型通道
        // ==================================================================================
        private void BuildPageModel()
        {
            var flow = BeginPage("模型通道", "台词与每小时小结都走这里选定的通道。");

            var c1 = BeginCard(flow, "OpenAI 兼容端点",
                "任何 OpenAI 兼容服务都能接：DeepSeek／硅基流动／OpenRouter／本地 ollama。");
            _tBase = TextRow(c1, "接口地址", _w.Cfg.OpenAiBase, "https://api.deepseek.com");
            _tModel = TextRow(c1, "模型名", _w.Cfg.OpenAiModel, "deepseek-chat");
            _tKey = TextRow(c1, "API key", _w.Cfg.DeepSeekKey, "sk-…（留空＝保留已存的 key）", secret: true);

            var hint = NoteBox("填写规则：\n"
                + "• 「接口地址」不写 /chat/completions，只写它前面的部分（代码会自己拼）。\n"
                + "• 地址与 key **两项都有**才走这条通道，否则自动回落到 Trae 通道。\n"
                + "• key 只存在本机（" + PetConfig.Dir + "），不会进 Obsidian 库、不会同步。\n\n"
                + "常见填法：\n"
                + "    DeepSeek → https://api.deepseek.com ＋ deepseek-chat\n"
                + "    硅基流动 → https://api.siliconflow.cn/v1 ＋ deepseek-ai/DeepSeek-V3\n"
                + "    本地 ollama → http://localhost:11434/v1 ＋ qwen2.5", SettingsTheme.Pal.Faint);
            AddRow(c1, hint);
            FinishCard(c1);

            var c2 = BeginCard(flow, "Trae 通道（内置，无需配置）",
                "不填上面任何一项时，她走这条通道。");
            AddRow(c2, Note("• 复用本机 Trae 客户端的登录态，不额外要 key。\n"
                          + "• 只在装了 Trae 的机器上可用 —— 换机器时它会自动失效，那时请改用上面的兼容端点。",
                          SettingsTheme.Pal.Muted));
            FinishCard(c2);
        }

        // ==================================================================================
        // 第 3 栏：她能看见什么（隐私边界）
        // ==================================================================================
        private void BuildPagePrivacy()
        {
            var flow = BeginPage("她能看见什么", "四档感知，从「只看应用名」到「把画面发出去」。本机／外发在这里分家。");

            var c1 = BeginCard(flow, "A 档 · 进程名（始终开启，不出本机）",
                "她只知道当前前台程序叫什么。");
            AddRow(c1, Note("例：msedge、Code、notepad。这一档不含窗口标题、不含任何文字内容。", SettingsTheme.Pal.Muted));
            FinishCard(c1);

            var c2 = BeginCard(flow, "D 档 · 屏幕文字（本机识别）",
                "用 Windows 自带识别器读屏幕上的字。全程本机 —— 不出电脑、不花钱。");
            var _trow4 = ToggleRow("允许她在本机读屏幕上的字", "仅本机识别，不发出去", out _tOcr, out _cOcr, _w.Cfg.OcrOn);
            AddRow(c2, _trow4);
            var _trow5 = ToggleRow("把读到的字放进提示（会发出去）",
                    "⚠ 这是唯一会把屏幕内容送出这台电脑的开关。默认关。",
                    out _tOcrSend, out _cOcrSend, _w.Cfg.OcrSendText, warn: true);
            AddRow(c2, _trow5);
            AddRow(c2, NoteBox("两个开关各管一件事，分开是有意的：\n"
                + "• 只开上面那个 → 她读得到字，但只用来在本机判断你切到什么页面（不外发）。\n"
                + "• 两个都开 → 读到的原文会进提示词发给模型，她才能**针对内容**吐槽。\n\n"
                + "关闭下面那个以后，「迟早吐槽一句」仍会触发，但她吐槽不到内容 —— 只能对着应用名和待的时长说。",
                SettingsTheme.Pal.Muted));
            FinishCard(c2);

            var c3 = BeginCard(flow, "L4 档 · 像素级看屏幕（未实现）",
                "会把画面本身发出去，权限性质与上面几档不同。");
            var _trow6 = ToggleRow("像素级看屏幕", "当前版本未接线，勾了也没有作用", out _tEye, out _cEye, _w.Cfg.EyeOn, warn: true);
            AddRow(c3, _trow6);
            FinishCard(c3);
        }

        // ==================================================================================
        // 第 4 栏：每小时小结
        // ==================================================================================
        private void BuildPageSummary()
        {
            var flow = BeginPage("每小时小结", "她替你记的日记：这一段时间你在哪些应用上花了多久。");

            var c1 = BeginCard(flow, "开关与间隔");
            var _trow7 = ToggleRow("每满一段时间写一篇小结", "由模型把「应用 → 时长」写成一段话", out _tSummary, out _cSummary, _w.Cfg.SummaryOn);
            AddRow(c1, _trow7);
            var row = new Panel { Height = 32, BackColor = SettingsTheme.Pal.Card };
            row.Controls.Add(new Label { Text = "间隔（分钟）", AutoSize = true, ForeColor = SettingsTheme.Pal.Text, Location = new Point(0, 8) });
            _nInterval = new NumericUpDown
            {
                Minimum = 10, Maximum = 600, Increment = 10,
                Location = new Point(140, 5), Width = 90,
                BackColor = SettingsTheme.Pal.Field, ForeColor = SettingsTheme.Pal.Text, BorderStyle = BorderStyle.FixedSingle,
                Value = (decimal)Math.Max(10, Math.Min(600, _w.Cfg.SummaryIntervalMin)),
            };
            row.Controls.Add(_nInterval);
            row.Controls.Add(new Label { Text = "默认 60（每小时一次）", AutoSize = true, ForeColor = SettingsTheme.Pal.Faint, Location = new Point(240, 8) });
            AddRow(c1, row);
            FinishCard(c1);

            var c2 = BeginCard(flow, "写到哪里");
            _tVault = TextRow(c2, "库路径", _w.Cfg.VaultPath, @"D:\Obsidian_SecondBrain\SecondBrain");
            AddRow(c2, NoteBox("小结写进「<库路径>\\40 Projects\\阿助（桌宠）\\她写的\\日期.md」\n"
                + "按天一个文件、按小时分节 —— 你直接打开那个文件就能读。\n\n"
                + "素材只有**应用名与时长**，不含窗口标题、不含屏幕文字 —— 即使「把读到的字发出去」开着，"
                + "小结也不会把屏幕原文写进日记。", SettingsTheme.Pal.Muted));
            FinishCard(c2);
        }

        // ==================================================================================
        // 第 5 栏：外观与启动
        // ==================================================================================
        private void BuildPageAppearance()
        {
            var flow = BeginPage("外观与启动", "她在桌面上什么样、开机要不要自己起来。");

            var c1 = BeginCard(flow, "尺寸");
            var row = new Panel { Height = 32, BackColor = SettingsTheme.Pal.Card };
            row.Controls.Add(new Label { Text = "显示尺寸", AutoSize = true, ForeColor = SettingsTheme.Pal.Text, Location = new Point(0, 8) });
            _cSize = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(140, 5), Width = 140,
                BackColor = SettingsTheme.Pal.Field, ForeColor = SettingsTheme.Pal.Text, FlatStyle = FlatStyle.Flat,
            };
            _cSize.Items.AddRange(new object[] { "小（176×220）", "中（240×300）", "大（320×400）" });
            _cSize.SelectedIndex = Math.Max(0, Math.Min(2, _w.Cfg.SizeIndex));
            row.Controls.Add(_cSize);
            AddRow(c1, row);
            FinishCard(c1);

            var c2 = BeginCard(flow, "窗口行为");
            var _trow8 = ToggleRow("始终置顶", "压在所有窗口上面；不想被她挡到时关掉", out _tTopmost, out _cTopmost, _w.Cfg.Topmost);
            AddRow(c2, _trow8);
            var _trow9 = ToggleRow("夜间调光", "跟随系统夜间模式，把她的亮度压下来", out _tNight, out _cNight, _w.Cfg.NightDim);
            AddRow(c2, _trow9);
            var _trow10 = ToggleRow("开机自启", "开机后自动出现在桌面右下角", out _tAutostart, out _cAutostart, PetConfig.AutostartOn());
            AddRow(c2, _trow10);
            FinishCard(c2);
        }

        // ==================================================================================
        // 第 6 栏：关于与位置
        // ==================================================================================
        // ==================================================================================
        // 版本与更新（「关于」栏第一张卡）
        // ==================================================================================
        // 交互口径（用户 2026-09-21 拍板）：**能查、能下、但不悄悄换** ——
        //   ① 打开面板时只显示当前版本，**不自动联网**（免得一开设置就卡一下）
        //   ② 用户点「检查更新」才去拉 version.json
        //   ③ 有新版：显示版本号 + 说明，给一个「下载并重启更新」按钮
        //   ④ 点了之后：下载 → 落 pending → 提示重启；**重启才真正替换**
        // 为什么不做成全静默自动更新：更新是**唯一会改程序自身**的动作，
        //   一旦出错后果比「配置写坏」严重得多（程序直接没了）。所以第一版把
        //   「要不要换」这个决定留给人 —— 基础设施全做了，风险动作有人确认。

        private Label _updStatus;
        private Panel _updBtnRow;
        private Button _updCheckBtn, _updApplyBtn;

        private void BuildUpdateCard(FlowLayoutPanel flow)
        {
            var pal = SettingsTheme.Pal;
            var card = BeginCard(flow, "版本与更新", "当前装的是哪一版、有没有新的。");

            // ---- 第一行：当前版本 + 仓库链接 ----
            AddRow(card, NoteBox("当前版本：" + Updater.CurrentVersion
                + "\n项目主页：" + Updater.RepoUrl, pal.Muted));

            // ---- 第二行：状态文字（检查/下载的结果都显示在这里）----
            _updStatus = new Label
            {
                Text = "点下面的按钮看看有没有新版本。", AutoSize = false, Height = 34,
                ForeColor = pal.Faint, Font = SettingsTheme.Small,
                BackColor = Color.Transparent,
            };
            AddRow(card, _updStatus);

            // ---- 第三行：按钮 ----
            _updBtnRow = new Panel { Height = 34, BackColor = pal.Card };
            _updCheckBtn = SmallButton("检查更新", 96);
            _updCheckBtn.Click += (s, e) => StartCheckUpdate();
            _updBtnRow.Controls.Add(_updCheckBtn);

            _updApplyBtn = SmallButton("下载并更新", 116);
            _updApplyBtn.Left = 104;
            _updApplyBtn.Visible = false;                  // 只有发现有新版才出现
            _updApplyBtn.Click += (s, e) => OnApplyButton();
            _updBtnRow.Controls.Add(_updApplyBtn);

            AddRow(card, _updBtnRow);
            FinishCard(card);

            // ⚠ 打开面板时**不自动检查** —— 一次网络请求会让「打开设置」慢一下，
            //   而用户打开设置多半是想改别的。真要自动检查应该后台做、结果落盘，
            //   那是下一版的事；现在保持「点一下才查」这种可预期的行为。
        }

        /// <summary>统一的小按钮样式（与「打开余额配置」那一颗同款）。</summary>
        private Button SmallButton(string text, int width)
        {
            var b = new Button
            {
                Text = text, Location = new Point(0, 2), Width = width, Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = SettingsTheme.Pal.Field,
                ForeColor = SettingsTheme.Pal.Text,
            };
            b.FlatAppearance.BorderColor = SettingsTheme.Pal.Line;
            return b;
        }

        // ---- 状态小工具：所有更新动作都在 UI 线程上改这两处，不在别处直接动控件 ----

        private void SetUpdStatus(string text, bool bad = false, bool good = false)
        {
            if (_updStatus == null) return;
            var pal = SettingsTheme.Pal;
            _updStatus.Text = text;
            _updStatus.ForeColor = bad ? pal.Warn : (good ? pal.Text : pal.Faint);
        }

        private void SetUpdBusy(bool busy, string what)
        {
            if (_updCheckBtn != null) _updCheckBtn.Enabled = !busy;
            if (_updApplyBtn != null) _updApplyBtn.Enabled = !busy;
            if (busy) SetUpdStatus(what);
        }

        /// <summary>检查更新。⚠ **必须异步** —— 同步发 HTTP 会把整个面板冻住几秒，
        /// 那正是我们刚花大力气修掉的症状，不能自己再造一个。</summary>
        private void StartCheckUpdate()
        {
            SetUpdBusy(true, "正在检查…");
            if (_updApplyBtn != null) _updApplyBtn.Visible = false;

            System.Threading.Tasks.Task.Run(async () =>
            {
                var r = await Updater.CheckAsync().ConfigureAwait(false);
                RunOnUi(() => ReportCheckResult(r));
            });
        }

        private void ReportCheckResult(Updater.CheckResult r)
        {
            SetUpdBusy(false, null);
            if (!r.Ok)
            {
                // ⚠ 检查失败是**正常情况**（离线、代理、raw 被墙都可能），
                //   措辞要让人放心，别写得像程序坏了。
                SetUpdStatus("没查到更新信息：" + (r.Message ?? "暂时无法检查") + "。（不影响使用）", bad: false);
                return;
            }
            if (!r.HasUpdate)
            {
                SetUpdStatus("已经是最新版本（" + r.RemoteVersion + "）。", good: true);
                return;
            }
            SetUpdStatus("发现新版本 " + r.RemoteVersion + "（当前 " + Updater.CurrentVersion + "）。"
                + (string.IsNullOrEmpty(r.Notes) ? "" : "\n" + r.Notes), good: true);
            if (_updApplyBtn != null)
            {
                _updApplyBtn.Text = "下载 v" + r.RemoteVersion;
                _updApplyBtn.Visible = true;
            }
            _pendingPlan = r;
            _updApplyStage = 1;                      // 待下载
        }

        private Updater.CheckResult _pendingPlan;    // 上次检查发现的可用更新
        private int _updApplyStage;                  // 1 = 待下载，2 = 已下载待重启

        /// <summary>那颗「下载并更新」按钮的分流：同一颗按钮在两个阶段做两件事。
        /// ⚠ 用显式阶段标志而不是反复 += / -= 处理器 —— WinForms 里漏 -= 的症状是
        ///   「点一下触发两次」，而它只在真机上出现，判据很难覆盖。</summary>
        private void OnApplyButton()
        {
            if (_updApplyStage == 2) DoRestartToUpdate();
            else StartDownloadUpdate();
        }

        /// <summary>下载更新包并落 pending。**下载完不替换** —— 只告诉用户「重启生效」。</summary>
        private void StartDownloadUpdate()
        {
            var plan = _pendingPlan;
            if (plan == null) { SetUpdStatus("请先点「检查更新」。"); return; }

            SetUpdBusy(true, "正在下载…");
            if (_updApplyBtn != null) _updApplyBtn.Visible = false;

            System.Threading.Tasks.Task.Run(async () =>
            {
                bool ok = await Updater.DownloadAsync(plan, pct =>
                {
                    RunOnUi(() => SetUpdStatus("正在下载… " + pct + "%"));
                }).ConfigureAwait(false);
                RunOnUi(() => ReportDownloadResult(ok, plan));
            });
        }

        private void ReportDownloadResult(bool ok, Updater.CheckResult plan)
        {
            SetUpdBusy(false, null);
            if (!ok)
            {
                SetUpdStatus("下载失败。可能是网络问题，也可能是新版本的包还没传完 —— 过会儿再试。"
                    + "（你的程序没被改动）", bad: true);
                return;
            }
            // 下载成功：给「立即生效」的路子
            SetUpdStatus("v" + plan.RemoteVersion + " 已下载好，重启后生效。", good: true);
            if (_updApplyBtn != null)
            {
                _updApplyBtn.Text = "立即重启更新";
                _updApplyBtn.Visible = true;
                // 按钮同一个，但此刻要做的事变成了「替换 + 重启」——
                // 用一次性订阅换掉原来的「下载」处理器，避免两个 handler 同时挂着导致点一下下载两次。
                // ⚠ WinForms 没有「移除全部 Click 处理器」的公开 API ⇒ 用一个标志位分流，
                //   比反复 += / -= 更不容易漏（漏了的症状是「点一下触发两次」）。
                _updApplyStage = 2;
            }
        }

        /// <summary>「立即重启更新」：先兑现替换，再重启自己。
        /// ⚠ 替换后**当前进程仍在跑旧代码**（旧文件句柄），所以必须重启才真的切过去。</summary>
        private void DoRestartToUpdate()
        {
            try
            {
                string detail;
                bool ok = Updater.ApplyPending(out detail);
                if (!ok)
                {
                    MessageBox.Show("更新没有完成：" + detail + "\n\n你的程序仍在正常版本上。",
                        "阿助桌宠", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                // 重启：把自己再拉起来（新文件已在原位），然后退出当前进程
                string me = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(me))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = me,
                        UseShellExecute = true,
                    });
                }
                Close();
                System.Windows.Forms.Application.Exit();
            }
            catch (Exception ex)
            {
                MessageBox.Show("更新时出错：" + ex.Message + "\n\n你的程序应该仍然可用。",
                    "阿助桌宠", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>把一段代码丢回 UI 线程执行。⚠ WinForms 控件只能在建它的线程上碰 ——
        /// 后台线程直接改 _updStatus.Text 会抛跨线程异常（而且往往只在用户真点时炸）。</summary>
        private void RunOnUi(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { }
        }

        private void BuildPageAbout()
        {
            var flow = BeginPage("关于与位置", "出问题时你会想知道的两件事：东西在哪、怎么自证。");

            BuildUpdateCard(flow);

            var c1 = BeginCard(flow, "本机位置");
            AddRow(c1, NoteBox("配置与内部状态：\n" + PetConfig.Dir + "\n\n"
                + "她写的日记：\n" + SafeJoin(_w.Cfg.VaultPath, @"40 Projects\阿助（桌宠）\她写的") + "\n\n"
                + "⚠ 配置目录在 %LOCALAPPDATA%，**不在**你的 Obsidian 库里 —— 所以凭据不会被同步到云上。",
                SettingsTheme.Pal.Muted));
            FinishCard(c1);

            var c2 = BeginCard(flow, "余额来源");
            AddRow(c2, Note("Trae / WorkBuddy 积分与自定义余额接口是另一套较复杂的界面（多来源列表 + 凭据隔离），"
                          + "单独放在这个窗里。", SettingsTheme.Pal.Muted));
            var btnRow = new Panel { Height = 34, BackColor = SettingsTheme.Pal.Card };
            var bal = new Button
            {
                Text = "打开余额配置…", Location = new Point(0, 2), Width = 150, Height = 30,
                FlatStyle = FlatStyle.Flat, BackColor = SettingsTheme.Pal.Field, ForeColor = SettingsTheme.Pal.Text,
            };
            bal.FlatAppearance.BorderColor = SettingsTheme.Pal.Line;
            bal.Click += (s, e) => OpenBalance();
            btnRow.Controls.Add(bal);
            AddRow(c2, btnRow);
            FinishCard(c2);

            var c3 = BeginCard(flow, "自检");
            AddRow(c3, NoteBox("她每一环都能离线自证 —— 怀疑哪一环坏了，就跑对应的那一条（在 exe 所在目录）：\n\n"
                + "    pet.exe --watchtest      感知／决策／记忆\n"
                + "    pet.exe --speaktest      表达环接线（含通道路由）\n"
                + "    pet.exe --summarytest    每小时小结聚合\n"
                + "    pet.exe --ocrtest        本机读字\n"
                + "    pet.exe --personatest    人格前缀\n"
                + "    pet.exe --fstest         全屏隐退判定\n\n"
                + "全部通过会打印 N/N。这些都不联网、不花钱（--llmtest／--statustest 例外）。",
                SettingsTheme.Pal.Faint));
            FinishCard(c3);
        }

        // ==================================================================================
        // 行构件
        // ==================================================================================

        /// <summary>「标题 + 副标题」在左、拨动开关在右的一整行。开关与标签各占一位，
        /// 标签不许压在开关下面 —— 旧版把说明文字塞进 CheckBox.Text 里换行，正是「挤」的来源。
        /// <paramref name="carrier"/> 就是那个 <see cref="Toggle"/>（它是 CheckBox 的子类），
        /// out 出来是为了让字段声明读起来仍是「一组开关」。</summary>
        private Panel ToggleRow(string title, string sub, out Toggle toggle, out CheckBox carrier, bool value, bool warn = false)
        {
            var pal = SettingsTheme.Pal;
            var p = new Panel { Height = 46, BackColor = SettingsTheme.Pal.Card };
            var t = new Toggle { Checked = value, Top = 11 };
            t.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var lbl = new Label
            {
                Text = title, AutoSize = true, ForeColor = warn ? pal.Warn : pal.Text,
                Font = SettingsTheme.BodyBold, Location = new Point(0, 5),
            };
            var sub2 = new Label
            {
                Text = sub, AutoSize = false, ForeColor = warn ? pal.Warn : pal.Faint,
                Font = SettingsTheme.Small, Location = new Point(0, 24), Height = 17,
            };
            p.Controls.Add(lbl);
            p.Controls.Add(sub2);
            p.Controls.Add(t);
            p.Resize += (s, e) =>
            {
                t.Left = p.Width - t.Width;
                sub2.Width = Math.Max(60, p.Width - t.Width - 12);
            };
            toggle = t;
            carrier = t;      // 同一个对象，不是第二份真值
            return p;
        }

        /// <summary>带标题的文本输入行（标题在上、输入框在下，占满整行宽）。</summary>
        private TextBox TextRow(Card card, string label, string value, string placeholder, bool secret = false)
        {
            var pal = SettingsTheme.Pal;
            var p = new Panel { Height = 56, BackColor = SettingsTheme.Pal.Card };
            p.Controls.Add(new Label
            {
                Text = label, AutoSize = true, ForeColor = pal.Text,
                Font = SettingsTheme.BodyBold, Location = new Point(0, 0),
            });
            var t = new TextBox
            {
                Top = 24, Left = 0, Height = 26,
                BackColor = pal.Field, ForeColor = pal.Text, BorderStyle = BorderStyle.FixedSingle,
                UseSystemPasswordChar = secret,
            };
            t.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            t.Tag = placeholder;
            p.Resize += (s, e) => t.Width = p.Width;

            // ⚠ 凭据类不回显：已存 key 时不填文本，用占位提示「留空＝保留」。
            bool hasStored = secret && !string.IsNullOrEmpty(value);
            if (!hasStored && !string.IsNullOrEmpty(value)) t.Text = value;
            if (string.IsNullOrEmpty(t.Text))
            {
                t.Text = placeholder;
                t.ForeColor = pal.Faint;
                t.GotFocus += (s, e) => { if (t.Text == (string)t.Tag) { t.Text = ""; t.ForeColor = pal.Text; } };
                t.LostFocus += (s, e) => { if (t.Text.Length == 0) { t.Text = (string)t.Tag; t.ForeColor = pal.Faint; } };
            }
            p.Controls.Add(t);
            AddRow(card, p);
            return t;
        }

        /// <summary>普通说明段（可换行，高度自适应 —— 不再写死 44 导致截字）。
        ///
        /// ⚠⚠ **为什么不用 `AutoSize = true`**（真实事故，2026-09-20，查了很久）：
        ///   原先写的是 `AutoSize = true` + `MaximumSize = (宽, 0)`，靠 `Label.AdjustSize()`
        ///   在挂上父容器（`OnParentChanged`）时自己量文本。这条路径**会间歇性地**抛
        ///     `ExternalException: A generic error occurred in GDI+.`
        ///   （栈：`Label.OnParentChanged → AdjustSize → GetPreferredSizeCore → MeasureString`）
        ///   在 `--settings` 里它表现为**启动即崩**（`RunSettings` 里先起了 WPF 渲染器与
        ///   PetWindow，之后才构造面板）；在 `--settingstest` 里却一直是绿的 —— 因为它只
        ///   构造一次面板，撞不上那个窗口。
        ///
        ///   已排除的解释（都实测过，别再往回猜）：
        ///     ✗ GDI 句柄配额：异常时全进程只有 **38** 个 GDI 对象（上限约 10000）。
        ///     ✗ 特定文本/字符：把真实文本、⚠、`%LOCALAPPDATA%`、全角括号、`——` 逐个喂进去，全过。
        ///     ✗ `MaximumSize` 取值：0 / 1 / 10 / 700 / int.MaxValue / 负数，全过。
        ///     ✗ 派生字体 `new Font(Body, Bold)`：连造 5 次全过。
        ///     ✗ 纯 WPF Window 先 Show/Hide：过。
        ///     ✗ 重复运行计数：**间歇性**，同一条命令有时过有时不过（约 1/10 失败）。
        ///
        ///   ⇒ 结论：这是 `Label.AutoSize` 在「同线程已有 WPF 渲染器」时对 GDI+ 文本度量的
        ///     一种脆弱依赖，成因在框架内部，不在我们的文本或字体里。**判据是「别再走这条路径」**：
        ///     我们自己拿 `Graphics.MeasureString` 量一次、给死尺寸，`AutoSize = false`。
        ///     量不准也不会有异常 —— 最坏是少一行字，而不是整个窗口起不来。</summary>
        private Label Note(string text, Color color)
        {
            return MakeTextLabel(text, color, SettingsTheme.Small);
        }

        /// <summary>造一个「自己量好尺寸」的说明段：绕开 <see cref="Note"/> 里说明的那条脆弱路径。
        /// 宽度按卡片可用宽度封顶，高度按换行后的真实行数给 —— 显式尺寸，不靠框架自适应。</summary>
        private static Label MakeTextLabel(string text, Color color, Font font)
        {
            var l = new Label
            {
                Text = text ?? "",
                AutoSize = false,
                ForeColor = color,
                Font = font,
                BackColor = Color.Transparent,
            };
            return l;
        }

        /// <summary>把说明段的宽高定下来（在它在卡片里、卡片有宽度之后调用）。
        /// 量文本这一步包了 try —— 量不出来（返回空）时退化成「一行高」，绝不让异常冒出去。</summary>
        private static void FitTextLabel(Label l, int width)
        {
            if (width < 40) width = 40;                 // 卡片还没排过版时不写死一个可疑的小值
            SizeF need;
            try
            {
                using (var bmp = new Bitmap(1, 1))
                using (var g = Graphics.FromImage(bmp))
                {
                    // ⚠ 用 TextRenderer（GDI，非 GDI+）—— 它对「同线程有 WPF 渲染器」不敏感，
                    //   正是上面那条脆弱路径换掉之后要用的东西。
                    // ⚠⚠ 这一步曾是「打开设置卡 16.5 秒」的量测点（2026-09-20 事故）：
                    //   病灶不在 MeasureText 本身，而在喂给它的字符串有 2.68 亿字符。
                    //   ⇒ 纪律：**量文本的地方要假设文本可能很长**，谁生产这个字符串谁负责消毒
                    //     （已落在 PetConfig.IsPoisonedPath + Load 自愈里）。
                    need = TextRenderer.MeasureText(g, l.Text, l.Font,
                        new Size(width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
            }
            catch
            {
                need = new SizeF(width, l.Font.Height);
            }
            l.Width = Math.Min(width, Math.Max(20, (int)Math.Ceiling(need.Width)));
            l.Height = Math.Max(l.Font.Height, (int)Math.Ceiling(need.Height));
        }

        /// <summary>等宽字体的说明段（填法示例、路径、命令行 —— 对齐才好读）。</summary>
        private Label NoteBox(string text, Color color)
        {
            return MakeTextLabel(text, color, SettingsTheme.Mono);
        }

        // ==================================================================================
        // 底部操作条
        // ==================================================================================
        private Panel BuildFooter()
        {
            var pal = SettingsTheme.Pal;
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = pal.Window };
            footer.Paint += (s, e) =>
            {
                using (var p = new Pen(pal.Line))
                    e.Graphics.DrawLine(p, 0, 0, footer.Width, 0);
            };

            _statusText = new Label
            {
                AutoSize = true, ForeColor = pal.Muted, Font = SettingsTheme.Small,
                Location = new Point(PadX, 22),
            };
            footer.Controls.Add(_statusText);

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true, WrapContents = false, Padding = new Padding(0, 12, PadX, 0),
            };
            _saveBtn = MakeButton("保存并生效", 118, pal.Accent, Color.White, primary: true);
            _saveBtn.Click += (s, e) => Save();
            var cancel = MakeButton("取消", 76, pal.Field, pal.Muted, border: true);
            cancel.Click += (s, e) => Close();
            var def = MakeButton("恢复默认…", 96, pal.Field, pal.Muted, border: true);
            def.Click += (s, e) => RestoreDefaults();
            flow.Controls.Add(_saveBtn);
            flow.Controls.Add(cancel);
            flow.Controls.Add(def);
            footer.Controls.Add(flow);
            return footer;
        }

        private static Button MakeButton(string text, int width, Color back, Color fore, bool primary = false, bool border = false)
        {
            var b = new Button
            {
                Text = text, Width = width, Height = 32, BackColor = back, ForeColor = fore,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(SettingsTheme.GapSm, 0, 0, 0),
                Font = primary ? SettingsTheme.BodyBold : SettingsTheme.Body,
            };
            b.FlatAppearance.BorderSize = border ? 1 : 0;
            if (border) b.FlatAppearance.BorderColor = SettingsTheme.Pal.Line;
            return b;
        }

        // ==================================================================================
        // 联动：开关之间的「谁在什么档位下没作用」（纪律 2）
        // ==================================================================================
        private void WireDirty()
        {
            Action<Control> walk = null;
            walk = (c) =>
            {
                foreach (Control k in c.Controls)
                {
                    if (k is Toggle || k is TextBox || k is NumericUpDown || k is ComboBox)
                    {
                        var ctl = k;
                        if (ctl is Toggle tg) tg.CheckedChanged += CheckChanged;
                        else if (ctl is TextBox tb) tb.TextChanged += CheckChanged;
                        else if (ctl is NumericUpDown nu) nu.ValueChanged += CheckChanged;
                        else if (ctl is ComboBox cb) cb.SelectedIndexChanged += CheckChanged;
                    }
                    if (k.HasChildren) walk(k);
                }
            };
            walk(this);
        }

        private void CheckChanged(object sender, EventArgs e)
        {
            var pal = SettingsTheme.Pal;

            // ---- 联动灰化：每个开关都要能回答「它在哪个档位下完全没作用」----
            bool speech = _tSpeech != null && _tSpeech.Checked;
            SetEnabled(_tLlm, speech, pal);
            SetEnabled(_tRoast, speech, pal);

            bool summary = _tSummary != null && _tSummary.Checked;
            if (_nInterval != null) { _nInterval.Enabled = summary; _nInterval.BackColor = summary ? pal.Field : pal.Divider; }

            bool ocr = _tOcr != null && _tOcr.Checked;
            SetEnabled(_tOcrSend, ocr, pal);

            // ---- 状态条说实话 ----
            if (sender != null && _statusText != null)
            {
                _dirty = true;
                _statusText.Text = "● 有未保存的修改";
                _statusText.ForeColor = pal.Warn;
            }
        }

        private static void SetEnabled(Control c, bool on, SettingsPalette pal)
        {
            if (c == null) return;
            c.Enabled = on;
            // 灰化的**视觉**也要给到：WinForms 的 Enabled=false 只把子控件调灰，
            // 自绘控件不吃这一套 ⇒ 手动改色（否则「灰掉」在拨动开关上看不出来）。
            if (c is Toggle) ((Toggle)c).Enabled = on;
            foreach (Control k in c.Controls)
                if (k is Label) k.ForeColor = on ? pal.Faint : pal.Divider;
        }

        // ==================================================================================
        // 读写
        // ==================================================================================

        /// <summary>占位文本不算值 —— 与 placeholder 相同的输入按「空」处理。</summary>
        private static string Clean(TextBox t)
        {
            if (t == null) return "";
            string v = t.Text.Trim();
            return v == (string)t.Tag ? "" : v;
        }

        private void OpenBalance()
        {
            var panel = new BalanceSettingsWindow(_w.ReloadBalanceSources);
            panel.Show();
        }

        private void RestoreDefaults()
        {
            var pal = SettingsTheme.Pal;
            if (MessageBox.Show(this,
                "把这一窗里的开关恢复成默认值？\n\n"
                + "• 不会动「模型通道」里的接口地址与 key\n"
                + "• 不会动库路径\n"
                + "• 不会动余额来源配置\n\n"
                + "改完还要点「保存并生效」才落盘。",
                "恢复默认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            var d = new PetConfig();
            _tSpeech.Checked = d.SpeechOn;
            _tLlm.Checked = d.SpeechLlm;
            _tRoast.Checked = d.RoastOn;
            _tSummary.Checked = d.SummaryOn;
            _nInterval.Value = (decimal)d.SummaryIntervalMin;
            _tOcr.Checked = d.OcrOn;
            _tOcrSend.Checked = d.OcrSendText;
            _tEye.Checked = d.EyeOn;
            _tTopmost.Checked = d.Topmost;
            _tNight.Checked = d.NightDim;
            _cSize.SelectedIndex = d.SizeIndex;
            _statusText.Text = "● 已恢复默认（尚未保存）";
            _statusText.ForeColor = pal.Warn;
        }

        private void Save()
        {
            var c = _w.Cfg;
            var pal = SettingsTheme.Pal;

            c.SpeechOn = _tSpeech.Checked;
            c.SpeechLlm = _tLlm.Checked;
            c.RoastOn = _tRoast.Checked;
            c.OcrOn = _tOcr.Checked;
            c.OcrSendText = _tOcrSend.Checked;
            c.EyeOn = _tEye.Checked;
            c.SummaryOn = _tSummary.Checked;
            c.SummaryIntervalMin = (double)_nInterval.Value;
            string vault = Clean(_tVault);
            if (vault.Length > 0) c.VaultPath = vault;      // 空＝保留原值（别把库路径清成空串）
            c.OpenAiBase = Clean(_tBase);
            c.OpenAiModel = Clean(_tModel);
            string key = Clean(_tKey);
            if (key.Length > 0) c.DeepSeekKey = key;        // 空＝保留已存 key（凭据不回显）
            c.Topmost = _tTopmost.Checked;
            c.NightDim = _tNight.Checked;
            c.SizeIndex = _cSize.SelectedIndex;
            c.Save();

            // ⚠ 下发走唯一入口：托盘与面板共用同一份同步逻辑，杜绝分叉。
            _w.ApplyConfig();
            _w.SetSize(_cSize.SelectedIndex);

            bool wantAuto = _tAutostart.Checked;
            if (wantAuto != PetConfig.AutostartOn()) PetConfig.SetAutostart(wantAuto, Environment.ProcessPath);

            _dirty = false;
            _statusText.Text = "✓ 已保存并生效";
            _statusText.ForeColor = pal.Good;
            _saveBtn.Text = "已保存";
            var t = new Timer { Interval = 1400 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); if (!IsDisposed) _saveBtn.Text = "保存并生效"; };
            t.Start();

            DialogResult = DialogResult.OK;
        }

        private static string SafeJoin(string a, string b)
        {
            try { return System.IO.Path.Combine(a ?? "", b); }
            catch { return (a ?? "") + "\\" + b; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_dirty && DialogResult != DialogResult.OK)
            {
                if (MessageBox.Show(this, "还有没保存的修改，关闭就丢了。确定关闭吗？",
                    "阿助设置", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    e.Cancel = true;
            }
            base.OnFormClosing(e);
        }
    }
}
