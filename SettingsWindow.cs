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
using System.IO;
using System.Linq;
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
        private NumericUpDown _nCooldown, _nIdle, _nDaily;    // 说话频率三旋钮（第 1 栏）
        private TextBox _tVault, _tBase, _tModel, _tKey;
        // 备用通道（可选，2026-09-29 加）：主通道被限流时改走它。
        private TextBox _tBase2, _tModel2, _tKey2;
        private CheckBox _cTopmost, _cNight, _cAutostart;
        private ComboBox _cSize;
        private Toggle _tSpeech, _tLlm, _tRoast, _tSummary, _tOcr, _tOcrSend, _tEye, _tTopmost, _tNight, _tAutostart;

        // ---- 第 6 栏「余额与凭据」（2026-10-01 由独立的 WPF 余额窗合并进来）----
        // ⚠⚠ 这一栏的数据**不走**本窗的 `Save()` / `_dirty` 那一套，这是**有意的**：
        //   余额源写 `balances.json`、有自己的字段校验与「敏感头必须进凭据框」检查、
        //   还有自己的重载回调（`_w.ReloadBalanceSources`）。把它塞进 PetConfig 的保存流程
        //   ＝ 同一件事两个落点（本仓老毛病）。所以它在栏内自带「保存余额配置」按钮，
        //   底部那条全局的「保存并生效」**不管它**，界面上也照实写清楚。
        private ListBox _balList;
        private Label _balStatus, _balTraeState, _balWbState, _balTraeSub, _balWbSub;
        private Button _balTestBtn, _balSaveBtn;
        private Toggle _balAuto;
        private Panel _balTraeRow, _balWbRow;
        /// <summary>一个窗只留一个 ToolTip 实例（每次 new 一份会攒着不放，见它自身的 Dispose 语义）。</summary>
        private readonly ToolTip _balTip = new ToolTip();
        private List<BalanceSource> _balItems;
        private bool _balLoadOk, _balDirty;
        private string _balLoadError;
        private Dictionary<string, BalanceCell> _balTested;

        /// <summary>这一栏在左栏里的标题 —— 「关于与位置」里那颗跳转按钮靠它找到目标栏，
        /// 不写死下标（下标正是会漂的那个量）。</summary>
        internal const string BalancePageTitle = "余额与凭据";

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

        /// <summary>左栏两个容器的 Name。**判据靠它认出谁是谁** —— 版式判据要断言
        /// 「品牌区在栏目列表之上」，用名字取比「猜类型／写死坐标」稳（坐标正是会漂的那个量）。</summary>
        internal const string NavBrandName = "navBrand";
        internal const string NavListName = "navList";

        /// <param name="initialPage">打开后停在哪一栏（按**标题**匹配，见 <see cref="ShowPageByTitle"/>）。
        /// 传 null ＝ 第 1 栏。`--balance-settings` / `pet-settings.exe` 用它直达余额那一栏。</param>
        public SettingsWindow(ISettingsHost w, string initialPage = null)
        {
            _w = w;
            SettingsTheme.InitFonts();
            var pal = SettingsTheme.Pal;

            // 余额源在这里**一次性**读进来（不是每栏都读）：`_balLoadOk=false` 时那一栏会
            // 明确拒绝保存，而不是把读不出来的原配置覆盖掉 —— 与旧余额窗同一条纪律。
            {
                List<BalanceSource> loaded;
                string loadError;
                _balLoadOk = BalanceSources.TryLoad(out loaded, out loadError);
                _balItems = loaded ?? new List<BalanceSource>();
                _balLoadError = loadError;
            }

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
            // ⚠ 定位那一栏的调用必须排在 `ShowPage(0)` **之后**：`ShowPageByTitle` 找不到目标时
            //   什么都不做，于是自然回落到第 1 栏 —— 不会出现「整窗空白」那种最难查的状态。
            if (initialPage != null) ShowPageByTitle(initialPage);
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

            // ⚠ Name 不是装饰：`--settingstest` 的 `navBrandAboveList` 靠它认出「品牌区」与
            //   「栏目列表」，不靠猜类型、也不靠写死坐标 —— 否则判据自己会和版式一起漂。
            var brand = new Panel { Name = NavBrandName, Dock = DockStyle.Top, Height = 78, BackColor = Color.Transparent };
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

            var host = new Panel { Name = NavListName, Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(8, 4, 8, 8) };
            _nav.Controls.Add(host);

            // ⚠⚠ **`Dock=Fill` 必须最后被处理**，否则它把左栏**整个高度**吃光，
            //   而 `Dock=Top` 的品牌区只能叠在顶上 ⇒ 第 1 个栏目被**整块盖住**、第 2 个被压掉大半。
            //   2026-09-29 用户报「设置面板里没有『说话与吐槽』这一栏」，就是这么来的
            //   （那一栏一直存在、也一直被选中，只是被品牌区盖住了看不见）。
            //   实测：不调它时 brand 与 host 都报 y=0..78 / y=0..620，重叠 210×78px。
            //   ⚠ 这与本文件窗体级那条 `_content.BringToFront()` 是**同一个机制**，
            //     别只改一处 —— 少一处的症状就是「某个区域整块不见了，而所有判据全绿」。
            host.BringToFront();

            // 顺序＝用户第一次打开会依次想的顺序：先「她怎么说话」，再「她靠什么说话」…
            AddNav(host, "◍", "说话与吐槽", "她会主动开口吗");
            AddNav(host, "⌁", "模型通道", "台词与小结算哪儿");
            AddNav(host, "◉", "她能看见什么", "读屏范围与隐私");
            AddNav(host, "▤", "每小时小结", "她替你写的日记");
            AddNav(host, "◧", "外观与启动", "尺寸、置顶、开机");
            // 顺序：余额排在「关于」前面 —— 「关于与位置」永远是最后一栏（它是只读的兜底页）。
            // ⚠ 副标题控制在 ~7 个字以内：左栏 210px 宽，减掉图标位与内边距只剩 ~120px，
            //   超过就会被 `NavItem` 硬裁（没有省略号，看起来像渲染坏了）。判据测不到这一条。
            AddNav(host, "◎", BalancePageTitle, "积分与凭据在哪");
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
            BuildPageSpeech(); BuildPageModel(); BuildPagePrivacy(); BuildPageSummary();
            BuildPageAppearance(); BuildPageBalance(); BuildPageAbout();
        }

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

        /// <summary>
        /// 造一行「标签 ＋ 数字框 ＋ 单位（默认值提示）」。三行频率旋钮共用。
        /// ⚠ 抽出来不是为了省几行 —— 是**三个 x 坐标只写一遍**：手写三份各偏几像素，
        ///   正是旧版错位的来源（本仓老毛病「同一份数据两个落点」的排版版）。
        /// </summary>
        private Panel NumRow(string label, double val, double min, double max, double step,
                             string unit, string hint, out NumericUpDown box)
        {
            var pal = SettingsTheme.Pal;
            box = new NumericUpDown
            {
                Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step,
                BackColor = pal.Field, ForeColor = pal.Text, BorderStyle = BorderStyle.FixedSingle,
                Value = (decimal)Math.Max(min, Math.Min(max, val)),
            };
            return FieldRow(label, box, 140, 74,
                unit + (string.IsNullOrEmpty(hint) ? "" : "（" + hint + "）"));
        }

        /// <summary>
        /// 造一行「标签 ＋ 输入控件 ＋ 可选尾注」。**三个输入行共用**（开口间隔／安静保底／每天上限，
        /// 另外第 4、5 栏的两行也走它）。
        ///
        /// ⚠⚠ **高度与输入框的 x 都不许写死**（2026-09-29 用户报「文字溢出遮挡其他元素或被遮挡」）：
        ///   字体是**按 DPI 缩放**的 —— 本机 150% 缩放下 9.5pt 的实测行高是 **30px**，
        ///   而这一带原来是按 96dpi 写的死数字（面板高 32、标签 y=8、输入框 x=140）：
        ///     · 标签（y 8..38，高 30）比面板（32）还高 ⇒ 下沿被截；
        ///     · 「安静时多久冒一句」实测 **163px** 宽，直接压在 x=140 的输入框上（实测重叠 23×29px）。
        ///   ⇒ 行高取 `max(标签行高, 控件高) + GapSm`、两者垂直居中；
        ///     输入框的 x 由**标签实测宽**推出：`max(minFieldX, 标签宽 + GapMd)`。
        ///   ⚠ 有 `minFieldX` 兜底是为了三行**对齐**（长短不一的标签不会让输入框参差不齐）。
        /// </summary>
        private Panel FieldRow(string label, Control field, int minFieldX, int fieldWidth, string tail)
        {
            var pal = SettingsTheme.Pal;
            var lbl = new Label { Text = label, AutoSize = true, ForeColor = pal.Text, Font = SettingsTheme.Body };
            int lh = SettingsTheme.LineH(SettingsTheme.Body);   // 真实行高（不是 Font.Height，见 SettingsTheme.LineH）
            int fh = Math.Max(lh, field.Height);          // 控件高还没被框架定下来时，至少不矮于标签
            int h = fh + SettingsTheme.GapSm;

            var row = new Panel { Height = h, BackColor = pal.Card };
            lbl.Location = new Point(0, (h - lh) / 2);
            row.Controls.Add(lbl);

            int fx = Math.Max(minFieldX, lbl.PreferredWidth + SettingsTheme.GapMd);
            field.Location = new Point(fx, Math.Max(0, (h - fh) / 2));
            field.Width = fieldWidth;
            row.Controls.Add(field);

            if (!string.IsNullOrEmpty(tail))
                row.Controls.Add(new Label
                {
                    Text = tail, AutoSize = true, ForeColor = pal.Faint, Font = SettingsTheme.Body,
                    Location = new Point(fx + fieldWidth + SettingsTheme.GapSm, (h - lh) / 2),
                });
            return row;
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
                if (r is Label lbl && !lbl.AutoSize) SettingsTheme.FitLabel(lbl, card.Width - CardPad * 2 - pad);
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

            // ---- 触发节奏（2026-09-29 由只读说明改成可调）----
            // ⚠ 用户拍板「十分钟一句」＋「频率在设置里可调」。三个数字之间的**不变式**
            //   （保底 ≥ 冷却、吐槽冷却 > 冷却）由 `SpeechFreq.Apply` 一处钳制搞定 ——
            //   面板这边**不做任何校验**，否则就是第二个口径（本仓老毛病「同一份数据两个落点」）。
            var c2 = BeginCard(flow, "触发节奏",
                "她多久说一句。改完点「保存并生效」立刻起作用，不用重启。");
            AddRow(c2, NumRow("两次开口至少隔", _w.Cfg.SpeechCooldownMin, 1, 240, 1,
                "分钟", "默认 10", out _nCooldown));
            AddRow(c2, NumRow("安静时多久冒一句", _w.Cfg.RoastIdleMin, 1, 240, 1,
                "分钟", "默认 10", out _nIdle));
            AddRow(c2, NumRow("每天最多说", _w.Cfg.SpeechDailyCap, 1, 2000, 10,
                "句", "默认 200", out _nDaily));
            AddRow(c2, Note("• 第一条是**硬闸门**：两次开口之间的最小间隔，与她有没有话要说无关。\n"
                          + "• 第二条只在**完全安静**时用得上（标题没变、你也没换应用）—— 免得她像死掉了。\n"
                          + "• 「换了页面就吐槽一句」的间隔会自动跟着第一条走（必须比它长，否则触发会被闸门\n"
                          + "   一次次拦下，把她的观察记录灌爆）。\n"
                          + "• 不受影响的口径：停留多久才算「她看到了」仍是自适应的 4–8 秒；手动「让她说一句」\n"
                          + "   永远能开口，不看这些数。", SettingsTheme.Pal.Muted));
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

            // ---- 备用通道（可选，2026-09-29 加）----
            // 为什么要有这一栏：免费档（如 glm-4.7-flash）在平台算力吃紧时会返回
            // HTTP 429 / 业务码 1305「该模型当前访问量过大」—— 用户实测撞到过，聊天窗里只显示一句
            // 429。官方处置就是「稍后再试」，代码已会自己重试几次；仍然不通时才改走这里，
            // 这样她不会因为你选了免费档就整段答不上话。
            var cFb = BeginCard(flow, "备用通道（可选）",
                "主通道被限流时自动改走这里。地址与 key 都填才生效，不填＝行为与以前完全一致。");
            _tBase2 = TextRow(cFb, "接口地址", _w.Cfg.FallbackBase, "https://api.deepseek.com");
            _tModel2 = TextRow(cFb, "模型名", _w.Cfg.FallbackModel, "deepseek-chat");
            _tKey2 = TextRow(cFb, "API key", _w.Cfg.FallbackKey, "sk-…（留空＝保留已存的 key）", secret: true);
            AddRow(cFb, NoteBox("什么时候会用到它：\n"
                + "• 免费档被挤爆时服务端返回 429。上面那条通道会自动重试几次（1.2 秒起步、逐次翻倍）。\n"
                + "• 仍然不通，才改走这里 —— 于是她不会因为你选了免费档就整段答不上话。\n"
                + "• 只在**限流**时回落。key 错／地址错回落毫无意义（那只会把真正的原因盖住），所以不会走它。\n\n"
                + "建议填一条付费通道（如 DeepSeek）。完全不想花钱的话，这一栏留空即可。",
                SettingsTheme.Pal.Faint));
            FinishCard(cFb);

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
            // ⚠ 走 FieldRow 而不是就地手写：「标签 + 数字框 + 尾注」这一行的度量只许有一处
            //   （手写第二份 = 第二个会漂的口径，本仓老毛病）。
            _nInterval = new NumericUpDown
            {
                Minimum = 10, Maximum = 600, Increment = 10,
                BackColor = SettingsTheme.Pal.Field, ForeColor = SettingsTheme.Pal.Text, BorderStyle = BorderStyle.FixedSingle,
                Value = (decimal)Math.Max(10, Math.Min(600, _w.Cfg.SummaryIntervalMin)),
            };
            AddRow(c1, FieldRow("间隔（分钟）", _nInterval, 140, 90, "默认 60（每小时一次）"));
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
            _cSize = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = SettingsTheme.Pal.Field, ForeColor = SettingsTheme.Pal.Text, FlatStyle = FlatStyle.Flat,
            };
            _cSize.Items.AddRange(new object[] { "小（176×220）", "中（240×300）", "大（320×400）" });
            _cSize.SelectedIndex = Math.Max(0, Math.Min(2, _w.Cfg.SizeIndex));
            AddRow(c1, FieldRow("显示尺寸", _cSize, 140, 140, null));
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

        /// <summary>统一的小按钮样式（更新卡片、余额那一栏的工具条与行内按钮都用它）。
        /// `primary` 那个变体是余额栏要的：同一行里「主路」（安装扩展／浏览器登录）与
        /// 「备选」（手工粘贴）必须一眼分得出来 —— 否则终端用户会把备选当主路走。</summary>
        private Button SmallButton(string text, int width, bool primary = false)
        {
            var pal = SettingsTheme.Pal;
            var b = new Button
            {
                Text = text, Location = new Point(0, 2), Width = width, Height = 30,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? pal.Accent : pal.Field,
                ForeColor = primary ? Color.White : pal.Text,
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            if (!primary) b.FlatAppearance.BorderColor = pal.Line;
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

        // ==================================================================================
        // 第 6 栏：余额与凭据（原独立的 WPF 余额窗，2026-10-01 合并进来）
        // ==================================================================================
        // 为什么合并：托盘里「余额…」与「设置…」两个窗口装的是同一类东西（配置），
        //   而旧余额窗是**写死深色**的 WPF 窗、本窗跟着系统深浅走 —— 浅色系统下两个窗的观感是断的。
        //   合并后只剩一个入口、一套样式、一条滚动方式。
        //
        // ⚠ 三条与其它栏**不同**的纪律（改这一栏前先读）：
        // 1. **保存是栏内独立的**（写 balances.json，不进 PetConfig）——见字段区注释。
        //    底部那条全局的「保存并生效」不管它，界面上也照实写清楚，免得用户以为存过了。
        // 2. 自动补投那个勾选框**即时落盘**（勾完下一拍自检就按新值走），
        //    所以它被 `WireDirty()` 显式排除 —— 它是"已生效"，不是"待保存"。
        // 3. 凭据一律不回显；粘贴框留空＝保留原值（沿用旧余额窗与第 2 栏的先例）。
        private void BuildPageBalance()
        {
            var pal = SettingsTheme.Pal;
            var flow = BeginPage(BalancePageTitle,
                "状态气泡里的数字从哪来。凭据只存在本机（%LOCALAPPDATA%），不会进 Obsidian 库。");

            // ---------------- 卡片 1：内置平台 ----------------
            var c1 = BeginCard(flow, "内置平台",
                _balLoadOk ? "两个开箱即用的来源；其它平台用下面的「自定义接口」加。"
                           : "⚠ 自定义接口的配置文件读不出来，这一栏不会把它覆盖掉。");

            AddRow(c1, Note("凭据过期时优先用右边的按钮重新取一次；「手工粘贴」是备选（终端用户可能没装 WebView2 运行时）。"
                          + "旧内容不回显，粘贴框留空即保留原值。", pal.Muted));

            _balTraeRow = BuiltinRow("Trae 积分",
                "读取可用积分（总额 − 已用）｜ 令牌 14 天到期，靠同步扩展自动续期",
                out _balTraeState, out _balTraeSub,
                "安装同步扩展", () => InstallTraeExt(),
                "手工粘贴", () => OpenBalanceCredential("Trae", StatusProbe.TraeSecretFile, "authorization"));
            AddRow(c1, _balTraeRow);
            // ⚠ Trae **没有**「浏览器登录获取」这条路，理由两条，都不是猜的：
            //   ① 它的凭据是 **IDE 现签的** `Cloud-IDE-JWT`，而桌宠那一发只发 cookie
            //      （`BrowserReading.BuildFetchScript` 原文：`fetch(url,{credentials:'include'})`，
            //      一个自定义头都不设）；发那个请求的页面是 `vscode-file://` 自定义协议，
            //      实测 `document.cookie` 长度 0 —— 连 cookie 罐都不存在。
            //   ② 实测那个接口**只认 `authorization` 一个头**（29 个头删到 1 个照样读到真数），
            //      所以「复现 29 个签名头」这个曾经以为的难点其实不存在。
            //   ⇒ 正确入口是「装一个扩展让 Trae 自己把令牌交出来」（见 TraeExtInstaller.cs）。

            _balWbRow = BuiltinRow("WorkBuddy 积分",
                "读取界面同口径的 type=1 可用额度",
                out _balWbState, out _balWbSub,
                "浏览器登录获取", () => OpenBrowserLogin(),
                "手工粘贴", () => OpenBalanceCredential("WorkBuddy", StatusProbe.WorkBuddySecretFile, "cookie", "x-user-id"));
            AddRow(c1, _balWbRow);

            // ---- Trae 重启后自动补投（2026-10-01 用户拍板；契约见 TraeExtAuto.cs）----
            // ⚠ 它放在余额这一栏而不是「外观与启动」：整个 Trae 令牌的故事（凭据 + 装扩展）都在这张页上。
            var autoRow = ToggleRow("Trae 重启后自动补投同步扩展",
                "Trae 启动时不加载用户扩展 ⇒ 每次重启后自动续期都会失效。"
                + "勾着：桌宠每两分钟自检，发现「Trae 在跑 且 本会话还没跑过」就补投一份新副本；"
                + "关掉：重启后要你自己点一次「安装同步扩展」。⚠ 只在「已经装过一次」时才动 Trae 的目录。",
                out _balAuto, out _, _w.Cfg == null || _w.Cfg.TraeExtAuto);
            _balAuto.CheckedChanged += (s, e) => BalSetAuto(_balAuto.Checked);
            AddRow(c1, autoRow);
            FinishCard(c1);

            // ---------------- 卡片 2：自定义接口 ----------------
            var c2 = BeginCard(flow, "自定义接口",
                "一个来源对应状态气泡里的一行。双击列表项可编辑。");

            AddRow(c2, Note("字段路径支持三种写法：普通路径（data.remain）· 数组求和（accounts[type=1].remain）· "
                          + "相减（sub:data.total;data.used）。", pal.Muted));

            var bar = new Panel { Height = 36, BackColor = pal.Card };
            {
                int x = 0;
                Button Bar(string text, bool primary, Action act)
                {
                    var b = SmallButtonAt(text, x, primary);
                    b.Click += (s, e) => act();
                    x = b.Right + SettingsTheme.GapXs;
                    bar.Controls.Add(b);
                    return b;
                }
                Bar("＋ 新增", true, () => BalAddEdit(null));
                Bar("编辑", false, () => BalAddEdit(BalSelected()));
                Bar("复制", false, () => BalDuplicate());
                Bar("启用 / 停用", false, () => BalToggle());
                Bar("删除", false, () => BalDelete());
            }
            AddRow(c2, bar);

            int itemH = SettingsTheme.LineH(SettingsTheme.BodyBold) + SettingsTheme.LineH(SettingsTheme.Small)
                        + SettingsTheme.GapXs * 2;
            _balList = new ListBox
            {
                // ⚠ 高度按「几行」算，不写死像素 —— 150% 缩放下字体大一圈，写死的那个数会少显示一行。
                Height = itemH * 7,
                ItemHeight = itemH,
                IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed,
                BackColor = pal.Field, ForeColor = pal.Text, BorderStyle = BorderStyle.FixedSingle,
            };
            _balList.DrawItem += BalListDrawItem;
            _balList.MouseDoubleClick += (s, e) => { var sel = BalSelected(); if (sel != null) BalAddEdit(sel); };
            _balList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) BalDelete(); };
            AddRow(c2, _balList);

            var act = new Panel { Height = 36, BackColor = pal.Card };
            _balTestBtn = SmallButtonAt("测试全部", 0);
            _balTestBtn.Click += (s, e) => BalTestAll();
            act.Controls.Add(_balTestBtn);
            _balSaveBtn = SmallButtonAt("保存余额配置", _balTestBtn.Right + SettingsTheme.GapSm, true);
            _balSaveBtn.Click += (s, e) => BalSave();
            act.Controls.Add(_balSaveBtn);
            AddRow(c2, act);

            _balStatus = Note(_balLoadError != null ? "⚠ " + _balLoadError
                : "改完先「测试全部」再「保存余额配置」——它是独立保存的，底部那条「保存并生效」不管这一栏。",
                _balLoadError != null ? pal.Bad : pal.Faint);
            AddRow(c2, _balStatus);
            FinishCard(c2);
            _balSaveBtn.Enabled = _balLoadOk;

            // ---------------- 卡片 3：凭据放在哪 ----------------
            var c3 = BeginCard(flow, "凭据放在哪");
            AddRow(c3, NoteBox("凭据目录：" + BalanceSources.StorageDir() + "\n"
                + "来源列表：" + BalanceSources.ConfigPath() + "\n\n"
                + "⚠ 这两个位置都在 %LOCALAPPDATA%，**不在**你的 Obsidian 库里 —— 所以不会被同步到云上。\n"
                + "   cookie / authorization 这类敏感头只会写进独立凭据文件，不进 balances.json。",
                pal.Faint));
            FinishCard(c3);

            RefreshBalanceBuiltins();
            RefreshBalanceList();
        }


        /// <summary>内置平台那一行的内部构件（宽度链重排要用到，见 <see cref="LayoutBuiltinRow"/>）。</summary>
        private sealed class BuiltinRowCtx
        {
            public Label Sub, State;
            public Button Primary, Secondary;
        }

        /// <summary>「名称＋副标题」在左、状态在中右、最右两颗按钮的一整行 —— 内置平台那两个来源共用。
        /// ⚠ 宽度链只在这个 Panel 的 Resize 里算（并且在 <see cref="RefreshBalanceBuiltins"/>
        ///   改了文字之后再调一次）—— 按钮宽度跟字体（DPI）走，构造期拿到的可能不是最终值；
        ///   只算一次的话窄窗下状态文字会压到名称上，而 `noSiblingOverlap` 会把它抓出来。</summary>
        private Panel BuiltinRow(string name, string sub, out Label state, out Label subLabel,
                                 string primaryText, Action primary,
                                 string secondaryText, Action secondary)
        {
            var pal = SettingsTheme.Pal;
            int h1 = SettingsTheme.LineH(SettingsTheme.BodyBold);
            int h2 = SettingsTheme.LineH(SettingsTheme.Small);
            var p = new Panel { Height = h1 + h2 + SettingsTheme.GapSm, BackColor = pal.Card };

            var title = new Label
            {
                Text = name, AutoSize = true, ForeColor = pal.Text, Font = SettingsTheme.BodyBold,
                Location = new Point(0, 0),
            };
            subLabel = new Label
            {
                Text = sub, AutoSize = false, ForeColor = pal.Faint, Font = SettingsTheme.Small,
                Left = 0, Top = h1 + SettingsTheme.GapXs, Height = h2,
            };
            state = new Label
            {
                Text = "…", AutoSize = false, ForeColor = pal.Muted, Font = SettingsTheme.Small,
                Height = h2, TextAlign = ContentAlignment.MiddleRight,
            };
            var b1 = SmallButtonAt(primaryText, 0, true);
            b1.Click += (s, e) => primary();
            var b2 = SmallButtonAt(secondaryText, 0);
            b2.Click += (s, e) => secondary();

            p.Controls.Add(title); p.Controls.Add(subLabel); p.Controls.Add(state);
            p.Controls.Add(b1); p.Controls.Add(b2);
            p.Tag = new BuiltinRowCtx { Sub = subLabel, State = state, Primary = b1, Secondary = b2 };
            p.Resize += (s, e) => LayoutBuiltinRow(p);
            LayoutBuiltinRow(p);
            return p;
        }

        private void LayoutBuiltinRow(Panel p)
        {
            var ctx = p.Tag as BuiltinRowCtx;
            if (ctx == null) return;
            int h2 = SettingsTheme.LineH(SettingsTheme.Small);
            int h1 = SettingsTheme.LineH(SettingsTheme.BodyBold);

            // 从右往左排：次按钮 → 主按钮 → 状态文字 → （剩下的才是名称/副标题的）。
            int right = p.Width;
            ctx.Secondary.Left = Math.Max(0, right - ctx.Secondary.Width);
            right = ctx.Secondary.Left - SettingsTheme.GapSm;
            ctx.Primary.Left = Math.Max(0, right - ctx.Primary.Width);
            right = ctx.Primary.Left - SettingsTheme.GapMd;

            // ⚠⚠ 状态文字右对齐到按钮左边，但它**占掉的横向空间**是它自己那段文字 ——
            //   副标题的可用宽度必须按「到状态文字的**视觉**左边界」算，**不能**按 `leftLimit`
            //   （那只是名称的右边界）。第一版就是拿 `leftLimit` 当右边界，副标题被挤成一条
            //   5 个字一行的竖排窄条：整行被撑到 8 行高，还把下一行（WorkBuddy）挤出了卡片 ——
            //   而 `--settingstest` 当时是**全绿**的（行只是变高，既没重叠也没被裁）。
            //   ⇒ 判据测不到「丑」，这一条只能靠真截图发现（`--screencap` 就是为此加的）。
            int nameW = 0;
            foreach (Control k in p.Controls)
                if (k is Label l && !ReferenceEquals(l, ctx.State) && !ReferenceEquals(l, ctx.Sub))
                    nameW = Math.Max(nameW, l.Width);
            int leftLimit = Math.Min(right, nameW + SettingsTheme.GapMd);
            // ⚠⚠ 盒子要比「紧量出来的字宽」再宽 `GapSm`：`MeasureW` 走 `NoPadding | SingleLine`
            //   （量的是字身），而 Label 渲染时两侧各留几像素 ⇒ 盒子**正好等于**字宽时
            //   「● 已配置」会**折成两行**（截图里成了「● 已配／置」）。
            //   这正是「判据测不到丑」的又一例：折行既不重叠也不裁字，`--settingstest` 全绿。
            int stateTextW = SettingsTheme.MeasureW(ctx.State.Text, SettingsTheme.Small) + SettingsTheme.GapSm;
            int stateVisualLeft = Math.Max(leftLimit, right - stateTextW);

            // ⚠⚠ 状态标签是 `TextAlign=MiddleRight` ⇒ **盒子也收到 `[stateVisualLeft, right]`**，
            //   不能图省事写成 `Left=leftLimit, Width=right-leftLimit`。两种写法**画出来一模一样**
            //   （文字都右对齐到 `right`），但矩形不同：后者那条「铺满但不画东西」的空档横跨在副标题
            //   上方 ⇒ `noSiblingOverlap` 量到 120×27 的重叠判红（2026-10-01 实测）。
            //   ⇒ 这条也是那条判据的边界：它量**矩形**、不量像素，「空盒子」照样算压住。
            ctx.State.Left = stateVisualLeft;
            ctx.State.Width = Math.Max(40, right - stateVisualLeft);
            if (ctx.State.Right > right) ctx.State.Left = Math.Max(0, right - ctx.State.Width);
            ctx.State.Height = Math.Max(h2, SettingsTheme.TextBlockH(ctx.State.Text, SettingsTheme.Small, ctx.State.Width));

            // ⚠ 副标题右边界**由状态盒子的实际左沿反推**（不再重算一遍 `stateVisualLeft`）——
            //   这样「两盒不相交（副标题.Right ≤ 状态.Left − GapMd）」是**结构保证**的，
            //   不依赖上面那条 `Right > right` 的 clamp 走不走。（去掉原来的 80px 下限：它只是
            //   防退化的写法，却会让极窄窗下副标题反过来压住状态。）
            int avail = Math.Max(0, ctx.State.Left - SettingsTheme.GapMd);
            if (ctx.Sub.Width != avail) ctx.Sub.Width = avail;
            // ⚠ 副标题按**实际可用宽度**量换行高度（与 ToggleRow 同一条纪律）：
            //   窗口拉到最窄时最长的说明会换行，高度写死一行 ⇒ 第二行被裁（`textNotClipped` 会判红）。
            ctx.Sub.Height = Math.Max(h2, SettingsTheme.TextBlockH(ctx.Sub.Text, SettingsTheme.Small, avail));
            ctx.Sub.Top = h1 + SettingsTheme.GapXs;

            // 行高取「名称＋副标题」那一列与状态文字**两者较高**的那个。
            int need = Math.Max(h1 + SettingsTheme.GapXs + ctx.Sub.Height, ctx.State.Height) + SettingsTheme.GapSm;
            if (p.Height != need) p.Height = need;          // ⚠ 判等再写，避免 Resize 自递归

            ctx.State.Top = Math.Max(0, (p.Height - ctx.State.Height) / 2);
            ctx.Primary.Top = Math.Max(0, (p.Height - ctx.Primary.Height) / 2);
            ctx.Secondary.Top = ctx.Primary.Top;
        }

        /// <summary>一行内的小按钮，宽度按文字量算（不写死 —— 见 <see cref="SettingsTheme.MeasureW"/> 的理由）。</summary>
        private Button SmallButtonAt(string text, int left, bool primary = false)
        {
            var b = SmallButton(text, Math.Max(64, SettingsTheme.MeasureW(text, SettingsTheme.Body) + 24), primary);
            b.Left = left;
            return b;
        }

        /// <summary>刷新两个内置平台的卡片状态。四档色的口径与旧余额窗一致（理由见那里的长注释）：
        /// ① 凭据在 且 续期已装且**本会话已生效** = 绿（真的不用管了）
        /// ② 凭据在 但 续期没装 / Trae 重启后还没补投 = 琥珀（现在读数正常，将来会坏 —— 最该被看见的那格）
        /// ③ 没凭据 = 灰
        /// ⚠ 关键是第 ② 档：**「磁盘上装得好好的」≠「现在真的在生效」**。Trae 启动时不加载用户扩展，
        ///   所以每次 Trae 重启后磁盘上的副本都是哑的；若还显示"已启用"，用户会一直不去看它，
        ///   直到 14 天后 401 才发现。判据是扩展自己那份日志的 mtime 有没有晚于 Trae 的启动时刻。</summary>
        private void RefreshBalanceBuiltins()
        {
            if (_balTraeState == null) return;
            var pal = SettingsTheme.Pal;
            var slots = StatusProbe.SecretSlots();
            var ext = TraeExtInstaller.Inspect();
            bool? live = TraeExtInstaller.SessionLive();
            bool needRefill = ext.State == TraeExtInstaller.State.Installed && live == false;
            bool auto = _w.Cfg != null && _w.Cfg.TraeExtAuto;
            // 副标题 ＝ 固定说明 ＋ 一句**短**后缀（只有异常档才加）。
            // ⚠⚠ 后缀必须短：这一行的宽度被「状态文字 + 两颗按钮」压着，长句会把副标题挤成
            //   每行几个字的竖排窄条（第一版把整句 `ShortLabel` 拼上来，实测就是这样）。
            //   完整解释在 ToolTip 里（下面 `ext.Detail` 那一段），不占这一行。
            string suffix = "";
            if (slots[0].exists)
            {
                if (ext.State == TraeExtInstaller.State.NoSource) suffix = "　·　桌宠缺扩展文件";
                else if (ext.State == TraeExtInstaller.State.NoTrae) suffix = "　·　未发现 Trae";
                else if (ext.State == TraeExtInstaller.State.NotInstalled) suffix = "　·　续期扩展未装";
                else if (ext.State == TraeExtInstaller.State.Outdated) suffix = "　·　扩展待更新";
                else if (needRefill) suffix = auto ? "　·　重启过，等自动补投" : "　·　重启过，需点安装";
            }
            _balTraeSub.Text = TraeSubBase + suffix;
            _balTraeState.Text = slots[0].exists ? "● 已配置" : "○ 未配置";
            _balTraeState.ForeColor = !slots[0].exists ? pal.Muted
                : (ext.State == TraeExtInstaller.State.Installed && !needRefill ? pal.Good : pal.Warn);
            _balTraeState.Tag = slots[0].path + "\n" + ext.Detail
                + (ext.State == TraeExtInstaller.State.Installed
                   ? "\n本会话：" + (live == true ? "已生效" : live == false ? "还没跑过" : "判不了（Trae 可能没在运行）")
                     + "\n自动补投：" + (_w.Cfg == null ? "（这个窗口没接配置）" : _w.Cfg.TraeExtAuto ? "开" : "关")
                   : "")
                + "\n最近一次自检：" + (TraeExtAuto.LastNote ?? "（还没跑过）");
            var tip = _balTip;
            tip.SetToolTip(_balTraeState, (string)_balTraeState.Tag);
            tip.SetToolTip(_balTraeSub, (string)_balTraeState.Tag);

            _balWbState.Text = slots[1].exists ? "● 已配置" : "○ 未配置";
            _balWbState.ForeColor = slots[1].exists ? pal.Good : pal.Muted;
            tip.SetToolTip(_balWbState, slots[1].path);
            tip.SetToolTip(_balWbSub, slots[1].path);

            LayoutBuiltinRow(_balTraeRow);
            LayoutBuiltinRow(_balWbRow);
            // ⚠ 状态文字的长短会改变副标题的可用宽度 ⇒ 行高可能变 ⇒ **卡片高度必须跟着重算**。
            //   只调 `LayoutBuiltinRow` 不够：它改的是行自己的 Height，而卡片高度是 `RelayoutCard`
            //   算出来的、只在窗口 Resize 时跑一次（"状态文字变了但卡片还是旧高度"⇒ 最后一行
            //   被排到卡片外，看起来像"这行没了"）。
            var card = _balTraeRow.Parent as Card;
            if (card != null) RelayoutCard(card);

        }

        /// <summary>Trae 那张卡副标题的固定部分（短后缀在 <see cref="RefreshBalanceBuiltins"/> 里拼）。</summary>
        private const string TraeSubBase = "读取可用积分（总额 − 已用）｜ 14 天到期，靠扩展自动续期";

        private void RefreshBalanceList()
        {
            if (_balList == null) return;
            int old = _balList.SelectedIndex;
            _balList.BeginUpdate();
            _balList.Items.Clear();
            if (_balItems.Count == 0)
                _balList.Items.Add("还没有自定义接口。Trae / WorkBuddy 已在上方单独配置。");
            else
                foreach (var s in _balItems) _balList.Items.Add(s);
            _balList.EndUpdate();
            if (old >= 0 && old < _balList.Items.Count) _balList.SelectedIndex = old;
            else if (_balItems.Count > 0) _balList.SelectedIndex = 0;
        }

        /// <summary>列表自绘。⚠ 两条硬约束：
        /// ① **退化矩形直接返回** —— GDI+ 在宽或高 ≤ 0 的矩形上抛 `ArgumentException`，
        ///    那就是屏幕上红叉的根因（本项目已经在版式判据里栽过一次）。
        /// ② 一律走 `TextRenderer`（GDI），与文件里其它量/画文字的地方同一条口径。</summary>
        private void BalListDrawItem(object sender, DrawItemEventArgs e)
        {
            var pal = SettingsTheme.Pal;
            var b = e.Bounds;
            if (e.Index < 0 || e.Index >= _balList.Items.Count) return;
            if (b.Width <= 0 || b.Height <= 0) return;

            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var br = new SolidBrush(sel ? pal.AccentSoft : pal.Field))
                e.Graphics.FillRectangle(br, b);

            BalanceSource src = e.Index < _balItems.Count ? _balItems[e.Index] : null;
            if (src == null)
            {
                TextRenderer.DrawText(e.Graphics, "还没有自定义接口。Trae / WorkBuddy 已在上方单独配置。",
                    SettingsTheme.Small, new Rectangle(b.Left + SettingsTheme.GapSm, b.Top, Math.Max(1, b.Width - SettingsTheme.GapSm * 2), b.Height),
                    pal.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                return;
            }

            int h1 = SettingsTheme.LineH(SettingsTheme.BodyBold);
            // 状态徽章（右侧）先算宽度，免得第一行压到它上面。
            string badge;
            Color bc;
            BalanceCell cell;
            if (_balTested != null && _balTested.TryGetValue(src.Name, out cell))
            {
                badge = cell.Ok ? "✓ " + cell.Value.ToString("0.##") + (string.IsNullOrEmpty(cell.Unit) ? "" : " " + cell.Unit)
                                : "✕ " + cell.Error;
                bc = cell.Ok ? pal.Good : pal.Bad;
            }
            else
            {
                bool secretOk = string.IsNullOrWhiteSpace(src.SecretFile) || File.Exists(BalanceSources.ResolveSecret(src.SecretFile));
                bool hasSecretRef = !string.IsNullOrWhiteSpace(src.SecretFile);
                badge = !secretOk ? "凭据缺失"
                    : hasSecretRef ? (src.Enabled ? "凭据已保存 · 待测试" : "凭据已保存 · 已停用")
                    : (src.Enabled ? "待测试" : "已停用");
                bc = secretOk ? pal.Muted : pal.Bad;
            }
            int bw = SettingsTheme.MeasureW(badge, SettingsTheme.Small) + SettingsTheme.GapSm;

            int textW = Math.Max(40, b.Width - SettingsTheme.GapSm * 2 - bw - SettingsTheme.GapSm);
            TextRenderer.DrawText(e.Graphics,
                (src.Enabled ? "" : "[停用] ") + (string.IsNullOrEmpty(src.Name) ? "未命名来源" : src.Name),
                SettingsTheme.BodyBold,
                new Rectangle(b.Left + SettingsTheme.GapSm, b.Top + SettingsTheme.GapXs, textW, h1),
                src.Enabled ? pal.Text : pal.Muted,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            string host = "URL 未填写"; Uri u;
            if (Uri.TryCreate(src.Url, UriKind.Absolute, out u)) host = u.Host;
            TextRenderer.DrawText(e.Graphics,
                (src.Method ?? "GET") + "  ·  " + host + "  ·  " + (src.PathExpr ?? ""),
                SettingsTheme.Small,
                new Rectangle(b.Left + SettingsTheme.GapSm, b.Top + SettingsTheme.GapXs + h1,
                              textW, Math.Max(1, b.Height - SettingsTheme.GapXs - h1)),
                pal.Faint,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            TextRenderer.DrawText(e.Graphics, badge, SettingsTheme.Small,
                new Rectangle(b.Right - bw, b.Top, bw, b.Height), bc,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private BalanceSource BalSelected()
        {
            int i = _balList == null ? -1 : _balList.SelectedIndex;
            return (i >= 0 && i < _balItems.Count) ? _balItems[i] : null;
        }

        private void BalSetStatus(string text, Color color)
        {
            if (_balStatus == null) return;
            _balStatus.Text = text;
            _balStatus.ForeColor = color;
            // ⚠⚠ 状态文字**会变长**（测试结果里带着服务端返回的错误原文）⇒ 必须重算高度、
            //   并让卡片跟着长。不重算的话长出来的那部分被裁——那正是 `textNotClipped`
            //   这条判据存在的理由（它只在构造期量一次，抓不到"运行后才变长"的这种情况）。
            int w = _balStatus.Width;
            if (w > 0)
            {
                _balStatus.Height = SettingsTheme.TextBlockH(text ?? "", _balStatus.Font, w);
                var card = _balStatus.Parent as Card;
                if (card != null) RelayoutCard(card);
            }
        }

        /// <summary>切换「Trae 重启后自动补投」。**立刻落盘**，且写进桌宠**正在用的那份** `Cfg`
        /// ⇒ 不必重启桌宠，下一拍自检就按新值走。
        /// ⚠ 关掉时要把「会怎样」说清楚：它等于回到手点按钮，而不是"功能没了"（按钮一直在）。</summary>
        private void BalSetAuto(bool on)
        {
            if (_w.Cfg == null) { BalSetStatus("这个窗口没有接上桌宠配置，改不了这一项。", SettingsTheme.Pal.Warn); return; }
            _w.Cfg.TraeExtAuto = on;
            _w.Cfg.Save();
            BalSetStatus(on
                ? "已开启：Trae 每次重启后，桌宠会自动补投一份新副本（不必你再点按钮）。"
                : "已关闭：Trae 重启后需要你点一次「安装同步扩展」才会恢复自动续期。",
                SettingsTheme.Pal.Muted);
            RefreshBalanceBuiltins();
        }

        /// <summary>把「Trae 令牌同步扩展」投放到 Trae 的扩展目录（契约见 trae-ext/README.md）。
        /// ⚠ 往**别的应用**的目录里写东西必须由用户点一下 —— 不静默发生。</summary>
        private void InstallTraeExt()
        {
            var pal = SettingsTheme.Pal;
            var st = TraeExtInstaller.Inspect();
            if (st.State == TraeExtInstaller.State.NoSource)
            { BalSetStatus("桌宠缺少扩展文件：" + st.Detail, pal.Bad); return; }
            if (!st.HasTrae)
            { BalSetStatus("没找到 Trae 的扩展目录 —— 先启动一次 Trae 并登录，再点这里。", pal.Bad); return; }
            // ⚠ 这里**故意没有**「已经是最新就不装」的短路 —— 每次投放都是一份**新目录名**的副本，
            //   而"投放一个新目录"正是让 Trae 当场加载它的唯一办法（Trae SOLO CN 启动时不扫用户扩展目录）。
            //   那个短路曾把"重启后已失效"的扩展显示成"已启用"，用户点了没反应还以为一切正常。
            string ask = (st.State == TraeExtInstaller.State.Outdated
                    ? "Trae 里已有 " + TraeExtInstaller.OurFolders(st.ExtDir).Count + " 份同步扩展副本，"
                      + "这次会再投放一份新的（" + TraeExtInstaller.Version + " 版）。\n\n"
                    : "将把「Trae 令牌同步扩展」装进：\n" + st.ExtDir + "\n\n")
                + "它做且只做一件事：**Trae 运行中**被加载时，把当前令牌写进阿助的凭据文件，"
                + "让积分在 14 天到期后自动续上，不必再手工粘贴。\n"
                + "不抓包、不联网、不读进程内存；只改 authorization 一行，改前留 .bak-autosync。\n\n"
                + "若凭据文件本来不存在，会先准备一份骨架（那一行仍由扩展来填）。\n\n"
                + TraeExtInstaller.RestartHint + "\n\n确定继续吗？";
            if (MessageBox.Show(this, ask, "安装 Trae 同步扩展", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            string credPath, credNote;
            TraeExtInstaller.EnsureCredentialSkeleton(out credPath, out credNote);

            string detail;
            string err = TraeExtInstaller.Install(out detail);
            if (err != null) BalSetStatus(err, pal.Bad);
            else BalSetStatus(credNote + " " + detail, pal.Good);
            RefreshBalanceBuiltins();
        }

        private void BalAddEdit(BalanceSource src)
        {
            if (src == null && _balList != null && _balList.SelectedIndex < 0 && _balItems.Count > 0)
            { BalSetStatus("先在上面选一个来源，再点「编辑」。", SettingsTheme.Pal.Muted); return; }

            using (var dlg = new SourceEditorForm(src))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (src == null) _balItems.Add(dlg.Result);
                else _balItems[_balItems.IndexOf(src)] = dlg.Result;
                _balDirty = true;
                if (dlg.CredentialSaved)
                    BalSetStatus("凭据已安全保存；还要点「保存余额配置」提交来源设置。", SettingsTheme.Pal.Good);
                else
                    BalSetStatus("有未保存的修改 —— 点「保存余额配置」落盘。", SettingsTheme.Pal.Muted);
            }
            RefreshBalanceList();
        }

        private void BalDuplicate()
        {
            var s = BalSelected(); if (s == null) return;
            var c = CloneBalance(s); c.Name += "（副本）"; c.Enabled = false;
            _balItems.Add(c); _balDirty = true;
            BalSetStatus("已复制一份（默认停用）—— 点「保存余额配置」落盘。", SettingsTheme.Pal.Muted);
            RefreshBalanceList();
        }

        private void BalToggle()
        {
            var s = BalSelected(); if (s == null) return;
            s.Enabled = !s.Enabled; _balDirty = true;
            BalSetStatus("有未保存的修改 —— 点「保存余额配置」落盘。", SettingsTheme.Pal.Muted);
            RefreshBalanceList();
        }

        private void BalDelete()
        {
            var s = BalSelected(); if (s == null) return;
            if (MessageBox.Show(this, "从列表中移除“" + s.Name + "”？\n凭据文件会保留，防止误删。",
                "删除余额源", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _balItems.Remove(s); _balDirty = true;
            BalSetStatus("有未保存的修改 —— 点「保存余额配置」落盘。", SettingsTheme.Pal.Muted);
            RefreshBalanceList();
        }

        private bool BalSave()
        {
            var pal = SettingsTheme.Pal;
            if (!_balLoadOk)
            {
                MessageBox.Show(this, "原配置读取失败。这一栏不会覆盖它。", "无法保存", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            var errors = _balItems.Where(x => x.Enabled).SelectMany(x => BalanceSources.Validate(x).Select(e => x.Name + "：" + e)).ToList();
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", errors), "请检查配置", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (_balItems.Any(BalanceSources.HasSensitiveInlineHeaders))
            {
                MessageBox.Show(this, "检测到 cookie / authorization 仍写在普通请求头中。请编辑该来源，把敏感头移到「凭据」框。",
                    "凭据未隔离", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            try
            {
                BalanceSources.Save(_balItems, BalanceSources.ConfigPath());
                _w.ReloadBalanceSources();
                _balDirty = false;
                BalSetStatus("已保存；下次打开状态气泡立即使用新配置。", pal.Good);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "余额配置", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private async void BalTestAll()
        {
            var pal = SettingsTheme.Pal;
            var errors = _balItems.Where(x => x.Enabled).SelectMany(x => BalanceSources.Validate(x).Select(e => x.Name + "：" + e)).ToList();
            if (errors.Count > 0)
            {
                MessageBox.Show(this, string.Join("\n", errors), "无法测试", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _balTestBtn.Enabled = false; _balTestBtn.Text = "正在测试…";
            BalSetStatus("正在请求已启用的余额接口。", pal.Muted);
            try
            {
                var probe = new StatusProbe { CustomSources = _balItems.Where(x => x.Enabled).Select(CloneBalance).ToList() };
                // force=true：面板上「测试全部」是用户主动要一次真结果，穿透浏览器通道的节流。
                var r = await probe.CheckAsync(true);
                var slots = StatusProbe.SecretSlots();
                string traeTxt = r.TraeOk ? "Trae ✓ " + Math.Round(r.TraeAvailable).ToString("0")
                    : (string.IsNullOrEmpty(r.TraeError) ? "Trae ○ 未配置" : "Trae ✕ " + r.TraeError);
                string wbTxt = r.WorkbuddyOk ? "WorkBuddy ✓ " + Math.Round(r.WorkbuddyRemain).ToString("0")
                    : (string.IsNullOrEmpty(r.WorkbuddyError) ? "WorkBuddy ○ 未配置" : "WorkBuddy ✕ " + r.WorkbuddyError);

                _balTested = r.DynamicRows.GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.Last());
                RefreshBalanceList();
                bool ok = (r.TraeOk || !slots[0].exists) && (r.WorkbuddyOk || !slots[1].exists) && r.DynamicRows.All(x => x.Ok);
                BalSetStatus(traeTxt + "　｜　" + wbTxt + "　——　"
                    + (ok ? "已配置的来源均可用。" : "有来源需要处理（见上方每行右侧的标记）。"),
                    ok ? pal.Good : pal.Bad);
            }
            catch (Exception ex) { BalSetStatus("测试失败：" + ex.Message, pal.Bad); }
            finally { _balTestBtn.Text = "测试全部"; _balTestBtn.Enabled = true; }
        }

        private void OpenBalanceCredential(string platform, string file, params string[] required)
        {
            using (var dlg = new CredentialEditorForm(platform, file, required))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                RefreshBalanceBuiltins();
                _w.ReloadBalanceSources();
                BalSetStatus(platform + " 凭据已更新，可点「测试全部」验证。", SettingsTheme.Pal.Good);
            }
        }

        /// <summary>浏览器登录取凭据（WorkBuddy 专用）。成功时窗口内部就带了一次真实回测，
        /// 所以这里只负责刷新卡片与气泡 —— 气泡的余额有 60 秒缓存，不主动丢弃的话最长一分钟仍显示旧的 401。
        /// ⚠⚠ 这个窗口仍是 **WPF** 的（它托管 WebView2，重写风险与收益不成比例）。
        ///   在 WinForms 里开它必须做两件事，少一件就会出怪事：
        ///   ① 用 `WindowInteropHelper.Owner` 把 owner 设成**本 WinForms 窗体**（WPF 的 `Owner`
        ///      属性只收 WPF Window，直接赋 `this` 编译不过）；
        ///   ② 自己 `EnableWindow(false)` —— WPF 的模态性只对 WPF 那一层有效，
        ///      不自己禁用宿主的话，登录窗开着时设置面板仍可点。</summary>
        private void OpenBrowserLogin()
        {
            var dlg = new CredentialBrowserWindow(
                "WorkBuddy 积分",
                StatusProbe.WorkBuddySecretFile,
                CredentialCapture.WorkbuddyCapturePattern,
                CredentialCapture.WorkbuddyLoginUrl,
                CredentialCapture.WorkbuddyBalanceUrl,
                _w.ReloadBalanceSources);
            new System.Windows.Interop.WindowInteropHelper(dlg).Owner = Handle;
            bool wasEnabled = Native.DisableOwner(Handle);
            try { dlg.ShowDialog(); }
            finally { Native.RestoreOwner(Handle, wasEnabled); }
            RefreshBalanceBuiltins();
            if (dlg.Saved) BalSetStatus("WorkBuddy 凭据已从浏览器获取；可点「测试全部」复核。", SettingsTheme.Pal.Good);
        }

        private static BalanceSource CloneBalance(BalanceSource s) => new BalanceSource
        {
            Name = s.Name, Url = s.Url, Method = s.Method, PathExpr = s.PathExpr, Unit = s.Unit,
            Enabled = s.Enabled, SecretFile = s.SecretFile, HeadersText = s.HeadersText, Body = s.Body,
        };

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
            AddRow(c2, Note("Trae / WorkBuddy 积分与自定义余额接口都在「" + BalancePageTitle + "」那一栏 —— "
                          + "凭据隔离、测试与保存也在那儿。", SettingsTheme.Pal.Muted));
            var btnRow = new Panel { Height = 34, BackColor = SettingsTheme.Pal.Card };
            var bal = SmallButtonAt("去「" + BalancePageTitle + "」", 0);
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
                + "    pet.exe --fstest         全屏隐退判定\n"
                + "    pet.exe --webtest        内嵌浏览器依赖链（WebView2 装了没）\n"
                + "    pet.exe --traeexttest   随包分发的 Trae 同步扩展（清单／骨架／落点；有 node 时真跑扩展本体）\n\n"
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
            // ⚠⚠ 行高与两个标签的 y 一律**按字体真实度量**算，不许写死 —— 见 FieldRow 的长注释。
            //   写死 96dpi 的那一套（面板高 46、标题 y=5、副标题 y=24）在 150% 缩放下会让
            //   标题（5..35，真实高 30）与副标题（24..41）叠 **11px**，屏幕上就是两行字糊在一起。
            int titleH = SettingsTheme.LineH(SettingsTheme.BodyBold);
            int subH = SettingsTheme.LineH(SettingsTheme.Small);
            int top = SettingsTheme.GapXs;
            int subTop = top + titleH + SettingsTheme.GapXs;      // 两行之间留一格气
            int rowH = subTop + subH + SettingsTheme.GapXs;
            var p = new Panel { Height = rowH, BackColor = SettingsTheme.Pal.Card };
            var t = new Toggle { Checked = value, Top = Math.Max(0, (rowH - 24) / 2) };   // 24 = Toggle 构造时定死的 Size.Height
            t.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var lbl = new Label
            {
                Text = title, AutoSize = true, ForeColor = warn ? pal.Warn : pal.Text,
                Font = SettingsTheme.BodyBold, Location = new Point(0, top),
            };
            var sub2 = new Label
            {
                Text = sub, AutoSize = false, ForeColor = warn ? pal.Warn : pal.Faint,
                Font = SettingsTheme.Small, Location = new Point(0, subTop), Height = subH,
            };
            p.Controls.Add(lbl);
            p.Controls.Add(sub2);
            p.Controls.Add(t);
            p.Resize += (s, e) =>
            {
                t.Left = p.Width - t.Width;
                sub2.Width = Math.Max(60, p.Width - t.Width - 12);
                // ⚠ 副标题按**实际可用宽度**量换行高度：窗口拉到最窄时最长的说明会换行，
                //   高度写死一行 ⇒ 第二行被裁（「文字被遮挡」的另一半）。
                //   ⚠ 顺序是有讲究的：`RelayoutCard` 先设 `r.Width` 再读 `RowHeight(r)`，
                //     所以在这里撑开的高度**会被卡片读到**、行距跟着长，不用回调它。
                int need = Math.Max(subH, SettingsTheme.TextBlockH(sub2.Text, SettingsTheme.Small, sub2.Width));
                sub2.Height = need;
                int wantH = subTop + need + SettingsTheme.GapXs;
                if (p.Height != wantH) p.Height = wantH;      // ⚠ 判等再写，避免 Resize 自递归
                // ⚠ 开关垂直居中也放在这里（而不是只在构造时算一次）：控件高跟 DPI 走，
                //   构造期拿到的可能还不是最终值。
                t.Top = Math.Max(0, (p.Height - t.Height) / 2);
            };
            toggle = t;
            carrier = t;      // 同一个对象，不是第二份真值
            return p;
        }

        /// <summary>带标题的文本输入行（标题在上、输入框在下，占满整行宽）。</summary>
        private TextBox TextRow(Card card, string label, string value, string placeholder, bool secret = false)
        {
            var pal = SettingsTheme.Pal;
            // ⚠⚠ 标签与输入框的垂直间距同样按字体度量算：写死 `Top = 24` 时，
            //   标签的真实高是 30（150% 缩放）⇒ 两者叠 6px（实测 85×6px）。
            int lh = SettingsTheme.LineH(SettingsTheme.BodyBold);
            int boxTop = lh + SettingsTheme.GapXs;
            var p = new Panel { Height = boxTop + 26 + SettingsTheme.GapXs, BackColor = SettingsTheme.Pal.Card };
            p.Controls.Add(new Label
            {
                Text = label, AutoSize = true, ForeColor = pal.Text,
                Font = SettingsTheme.BodyBold, Location = new Point(0, 0),
            });
            var t = new TextBox
            {
                Top = boxTop, Left = 0, Height = 26,
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

        /// <summary>普通说明段（可换行，尺寸自己量 —— 见 <see cref="SettingsTheme.TextLabel"/>）。
        ///
        /// ⚠⚠ **为什么不用 `AutoSize = true`**（真实事故，2026-09-20，查了很久；完整实测记录
        ///   连同已排除的解释一起搬到了 <see cref="SettingsTheme.TextLabel"/> 的注释里，
        ///   改字号/改量法之前先读那里）：那条路（`Label.AdjustSize` → GDI+ `MeasureString`）
        ///   会**间歇性**抛 `ExternalException: A generic error occurred in GDI+.`，
        ///   在 `--settings` 里表现为**启动即崩**，而 `--settingstest` 一直全绿。
        ///   结论：成因在框架内部 ⇒ 纪律是「别再走这条路径」，自己量一次、给死尺寸。
        ///
        /// ⚠ 2026-10-01：量文字与造文字盒的**实现**搬去了 <see cref="SettingsTheme"/> ——
        ///   因为「余额与凭据」那两个弹窗（`BalanceEditorForms.cs`）也要用同一套，
        ///   在本文件里再留一份就是**第二个会漂的口径**（本仓老毛病）。这里只留转发。</summary>
        private static Label Note(string text, Color color)
        {
            return SettingsTheme.TextLabel(text, color, SettingsTheme.Small);
        }

        /// <summary>等宽字体的说明段（填法示例、路径、命令行 —— 对齐才好读）。</summary>
        private static Label NoteBox(string text, Color color)
        {
            return SettingsTheme.TextLabel(text, color, SettingsTheme.Mono);
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
                    // ⚠⚠ 「余额与凭据」那一栏的自动补投勾选框**不算脏**：它是即时落盘的
                    //   （勾完下一拍自检就按新值走，见 BalSetAuto）。把它接进来会让底部状态条
                    //   一直显示「有未保存的修改」，而用户点「保存并生效」也存不了它 —— 那句
                    //   提示就成了假话（本仓最忌讳的那种：状态条不说实话）。
                    if (ReferenceEquals(k, _balAuto)) { /* 既不接处理器，也不往下走 */ }
                    else if (k is Toggle || k is TextBox || k is NumericUpDown || k is ComboBox)
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
            // 频率三旋钮同理：她都不主动说话了，「多久说一句」就完全没作用 ⇒ 一起灰掉。
            SetNumEnabled(_nCooldown, speech, pal);
            SetNumEnabled(_nIdle, speech, pal);
            SetNumEnabled(_nDaily, speech, pal);

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

        /// <summary>数字框的灰化：`Enabled=false` 管用，但它的底色不吃 Enabled
        /// （与自绘 Toggle 同族的问题）⇒ 手动换色，否则「灰掉」在数字框上看不出来。</summary>
        private static void SetNumEnabled(NumericUpDown n, bool on, SettingsPalette pal)
        {
            if (n == null) return;
            n.Enabled = on;
            n.BackColor = on ? pal.Field : pal.Divider;
            n.ForeColor = on ? pal.Text : pal.Faint;
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
            // 2026-10-01 起余额不再是独立窗口，而是本窗的一栏 ⇒ 这里只负责「带用户过去」。
            ShowPageByTitle(BalancePageTitle);
        }

        /// <summary>切到某一栏（按**标题**找，不按下标）。
        /// ⚠ 不按下标的理由：栏数会随版本增删（本轮整合刚从 6 栏变成 7 栏），下标就是会漂的那个量；
        ///   而「按下标找页」正是本仓那一族假绿的老病灶。</summary>
        private void ShowPageByTitle(string title)
        {
            for (int i = 0; i < _navItems.Count && i < _pages.Count; i++)
                if (_navItems[i].Title == title) { ShowPage(i); return; }
        }

        /// <summary>给判据用（**只读**）：余额栏「自动补投」勾选框现在的状态。
        /// 存在只为一件事 —— 断言它的**初值真的绑到了配置上**。「控件写了但没绑 / 绑反了」
        /// 肉眼看不出来，而它恰好是"用户勾了没生效"那类故障的源头。</summary>
        internal bool BalanceAutoDeployChecked { get { return _balAuto != null && _balAuto.Checked; } }

        /// <summary>给判据用（**只读**）：勾选框是否可操作（没有配置可写时应当置灰，而不是假装能改）。</summary>
        internal bool BalanceAutoDeployEnabled { get { return _balAuto != null && _balAuto.Enabled; } }

        /// <summary>给判据用（**只读**）：余额列表里的行数。空列表会有一个占位行 ⇒ 断言时要知道这件事。</summary>
        internal int BalanceRowCount { get { return _balList == null ? -1 : _balList.Items.Count; } }

        /// <summary>给判据用：这个窗口里有没有叫这个名字的栏目。
        /// ⚠ 它测的是「结构还在不在」——本轮把余额从独立窗口并进来，最容易出的错不是画错，
        ///   而是**整合没做成**（栏没加上／被后来的人删了），那种情况下所有版式判据照样全绿。</summary>
        internal bool HasPage(string title)
        {
            foreach (var it in _navItems) if (it.Title == title) return true;
            return false;
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
            _nCooldown.Value = (decimal)d.SpeechCooldownMin;
            _nIdle.Value = (decimal)d.RoastIdleMin;
            _nDaily.Value = (decimal)d.SpeechDailyCap;
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
            // 说话频率（分钟）。⚠ 这里**不做任何钳制** —— 不变式由 SpeechFreq.Apply 一处保证
            //   （面板若也钳一次，就是第二个口径；将来改了一边忘了另一边，症状只是「她变奇怪」）。
            c.SpeechCooldownMin = (double)_nCooldown.Value;
            c.RoastIdleMin = (double)_nIdle.Value;
            c.SpeechDailyCap = (int)_nDaily.Value;
            string vault = Clean(_tVault);
            if (vault.Length > 0) c.VaultPath = vault;      // 空＝保留原值（别把库路径清成空串）
            c.OpenAiBase = Clean(_tBase);
            c.OpenAiModel = Clean(_tModel);
            string key = Clean(_tKey);
            if (key.Length > 0) c.DeepSeekKey = key;        // 空＝保留已存 key（凭据不回显）
            c.FallbackBase = Clean(_tBase2);
            c.FallbackModel = Clean(_tModel2);
            string key2 = Clean(_tKey2);
            if (key2.Length > 0) c.FallbackKey = key2;      // 与上面同一条口径：空＝保留已存 key
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

            // ⚠⚠ 余额那一栏是**独立保存**的（写 balances.json，不走这颗按钮）。用户按了这颗
            //   「保存并生效」而那边还有没落盘的改动时，必须**当场说清楚并把他送过去**：
            //   直接关窗会让那些改动静默消失，而状态条还写着「✓ 已保存并生效」——
            //   那是本仓最忌讳的一句假话。
            //   这里 return（不设 DialogResult）⇒ 模态窗不关，用户能接着去存。
            if (_balDirty)
            {
                bool go = MessageBox.Show(this,
                    "设置已保存。\n\n但「" + BalancePageTitle + "」那一栏还有没保存的改动 —— "
                    + "它写的是另一份文件，这颗按钮管不了它。\n\n现在带你过去存一下？",
                    "余额配置还没保存", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
                if (go)
                {
                    ShowPageByTitle(BalancePageTitle);
                    BalSetStatus("⚠ 上面那些改动还没落盘 —— 点「保存余额配置」。", pal.Warn);
                    return;
                }
            }

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
            // ⚠ 余额那一栏有**它自己那条**保存（写 balances.json，不走底部那颗按钮）⇒ 它的脏
            //   必须单独问一次，否则关窗时那些改动会静默消失（旧余额窗就是为这件事挂了 Closing）。
            if (!e.Cancel && _balDirty)
            {
                if (MessageBox.Show(this, "「" + BalancePageTitle + "」里还有没保存的改动，关闭就丢了。确定关闭吗？",
                    "阿助设置", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    e.Cancel = true;
            }
            base.OnFormClosing(e);
        }
    }
}
