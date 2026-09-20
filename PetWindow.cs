// 壳 —— 窗口与交互。这一层不认识 3D，只认识「窗口 + 屏幕 + 系统事件」。
//
// v1 的七件（对应 自建轻量壳交互清单.md 第 7 节）全部在这里：
//   ① 逐像素命中＋其余穿透（WM_NCHITTEST）  ② 拖拽＋抛物落地＋落地压扁
//   ③ 鼠标跟随转头                          ④ 托盘／右键菜单＋置顶＋自启
//   ⑤ 空闲打盹（GetLastInputInfo）          ⑥ 全屏应用时自动隐退
//   ⑦ 按时段自动调光
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AzhuPet
{
    internal sealed class PetWindow : Window
    {
        public readonly IPetRenderer R;
        public readonly PoseEngine Pose = new PoseEngine();
        public readonly PetConfig Cfg;
        public PetMenu Menu;

        // ---- 自检可读的量（全部是算术，不靠人眼）----
        public int RenderedFrames;
        public double SumFrameMs, MaxFrameMs;
        public double HideK, NightK;
        public bool HitThrough = true;      // 上一次 NCHITTEST 的结论
        public int HitThroughChecks, HitThroughPass;
        public bool SelfTestMode;
        /// <summary>仅测试用：让 WM_NCHITTEST 恒判透明。用来做**负对照** —— 验「测试自己有没有资格失败」。
        /// 一个从没红过的测试，其绿是没有信息量的。</summary>
        public static bool ForceHitThrough;
        /// <summary>仅测试用：把气泡**强行摆在宠物身上** —— 验「气泡不遮挡模型」这条判据有没有资格变红。
        /// 一个从没红过的测试，其绿是没有信息量的。</summary>
        public static bool ForceBubbleOnPet;
        /// <summary>负对照：把所有说话人换成哑的（--no-wire）。
        /// 打在**行为是否存在**上，而不是「参数是否为 0」—— 见 --speakvis／--speaktest 的负对照。</summary>
        public static bool ForceSilent;
        public string[] TraceLog = new string[64];
        public int TraceN;
        public int GateCalls, GatePass;

        // ---- 拖拽诊断：把「拖不动」拆成可分辨的几环 ----
        //   NCHITTEST 没给 HTCLIENT ⇒ 卡在命中；给了但 DownCount=0 ⇒ 卡在输入送达；
        //   Down>0 而窗口没位移 ⇒ 卡在 OnMove/坐标。
        public int DownCount, MoveCount, UpCount;
        public int DownRejectedHit, DownRejectedLock;
        public bool Dragging { get { return _dragging; } }

        // ---- 测试可读：气泡几何（**物理像素**，与截图坐标系一致）----
        public bool BubbleVisible
        {
            get { return _bubbleWin != null && _bubbleWin.Visibility == Visibility.Visible; }
        }

        /// <summary>气泡流当前"最新的那一条"在说什么。给 --speakvis 判据用（判「她的话」而不是「状态文案」）。</summary>
        public string BubbleText { get { return _feed == null ? null : _feed.LastText; } }

        /// <summary>"speech"（她说话）／"status"（查余额）／null（没显示）。
        /// ⚠ 这正是「一个窗口两个用途」必须能被外部区分的那一笔。</summary>
        public string BubbleKind
        {
            get
            {
                if (_brain == null || _bubbleWin == null || _bubbleWin.Visibility != Visibility.Visible) return null;
                if (_brain.Bubble.Kind == BubbleArbiter.Speech) return "speech";
                if (_brain.Bubble.Kind == BubbleArbiter.Status) return "status";
                return null;
            }
        }
        /// <summary>
        /// 气泡现在被谁占着：`none` / `status` / `speech` / `(无 brain)`。
        /// ⚠ 与 `BubbleKind` 的分工：`BubbleKind` 只在气泡**可见**时才回答（它答的是
        ///   「你眼前这个气泡是什么」）；这一条答的是**占用状态本身**，气泡关着也答。
        ///   判据要判「接缝」时必须用这一条 —— 用 `BubbleKind` 会把「_brain 是 null」
        ///   和「气泡还没显示」混成同一个 null（本仓那条「同一个词指多个对象」）。
        /// </summary>
        public string BubbleOwner
        {
            get
            {
                if (_brain == null) return "(无 brain)";
                if (_brain.Bubble.Kind == BubbleArbiter.Status) return "status";
                if (_brain.Bubble.Kind == BubbleArbiter.Speech) return "speech";
                return "none";
            }
        }

        /// <summary>[left, top, w, h]（物理像素）。未显示或坐标未落定时返回全 0。</summary>
        public int[] BubbleRectPx()
        {
            if (_bubbleWin == null) return new int[4];
            double l = _bubbleWin.Left, t = _bubbleWin.Top;
            double bw = _bubbleWin.ActualWidth, bh = _bubbleWin.ActualHeight;
            if (double.IsNaN(l) || double.IsNaN(t) || bw < 1 || bh < 1) return new int[4];
            return new[]
            {
                (int)Math.Round(l * DipScale), (int)Math.Round(t * DipScale),
                (int)Math.Round(bw * DipScale), (int)Math.Round(bh * DipScale)
            };
        }
        public bool Airborne { get { return _airborne; } }
        public string LastHitKind = "(未查询)";
        // 最近若干次 NCHITTEST 的原始输入与判定 —— 用来回答「点击到底被算到了哪里」
        public readonly string[] HitLog = new string[12];
        public int HitLogN;

        public readonly System.Collections.Generic.List<string> RateLog = new System.Collections.Generic.List<string>();
        private double _bucketT, _bCalls, _bFrames;
        public double ForcedIdle = -1;
        public bool ManualHidden;           // 用户在菜单里手动隐藏（与「全屏隐退」共用淡出通道）

        public IntPtr Handle { get; private set; }
        public double DipScale { get; private set; }

        private const double Fps = 60;   // 合成渲染上限（桌宠常驻：帧率越高越费电，按需取舍）
        private const double Gravity = 2600;      // 物理像素/秒²
        private const double Restitution = 0.42;

        private DispatcherTimer _slow;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastT;

        private bool _dragging;
        private Native.POINT _dragOrigin;
        private double _dragLeft0, _dragTop0, _lastX, _lastY, _lastMoveT;
        private double _vx, _vy;
        private bool _airborne;
        private double _hideTarget;
        private bool _hidden;

        private readonly StatusProbe _status = new StatusProbe();   // 连通性＋余额
        private DateTime _balanceConfigStamp;                       // 外部配置窗口保存后也能自动感知
        private Grid _bubbleHost;                                   // 窗口内容：只放渲染宿主（气泡已移出窗口，见 BuildBubble）
        private Window _bubbleWin;                                  // 气泡 = **独立浮窗**（窗口内没有不遮住模型的位置）
        private BubbleFeed _feed;                                   // 气泡流（多气泡同屏，见 BubbleFeed.cs）
        private bool _doubleTap;                                    // 本次左键按下是双击 → 弹气泡而非拖拽
        private long _lastDownT = long.MinValue;                    // 手动双击识别：上次按下毫秒时间戳
        private System.Windows.Point _lastDownP;                    // 手动双击识别：上次按下位置
        private Brain _brain;                                       // 表达环接线：感知 → 闸门 → 决策 → 气泡
        /// <summary>上次小时总结的时刻（初始＝启动时刻 ⇒ 「开机后每满 60 分钟」，用户拍板不对齐整点）。</summary>
        private DateTime _lastSummaryAt = DateTime.Now;
        private bool _summaryRunning;                               // 模型在途：防止重叠触发
        private long _lastSenseMs = -1000;                          // 采样节流（Probe 要开进程句柄，1 秒一次足够）

        public PetWindow(IPetRenderer r, PetConfig cfg)
        {
            R = r; Cfg = cfg;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = cfg.Topmost;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SnapsToDevicePixels = true;
            Title = "阿助桌宠";
            Width = cfg.WDip; Height = cfg.HDip;
            R.Resize(cfg.WDip, cfg.HDip);
            _bubbleHost = new Grid();
            _bubbleHost.Children.Add(r.Host);
            Content = _bubbleHost;
            _bubbleWin = BuildBubble();
            // 宠物一动就重新摆气泡（拖拽 / 抛物 / 换挡 / 贴边都覆盖，不用各处手动调）
            LocationChanged += (s2, e2) => UpdateBubblePos();
            Pose.DozeAfter = 45;

            Loaded += OnLoaded;
            SizeChanged += (s, e) => { if (ActualWidth > 1) R.Resize(ActualWidth, ActualHeight); };
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonUp += OnRight;
            Closing += (s, e) => { SavePos(); if (_bubbleWin != null) _bubbleWin.Close(); };
        }

        /// <summary>设置面板保存后重载并清缓存；下一次气泡必须使用新配置。</summary>
        public void ReloadBalanceSources()
        {
            _status.CustomSources = BalanceSources.Load();
            _balanceConfigStamp = BalanceSources.ConfigWriteUtc();
            _status.ClearCache();
        }

        private void ReloadBalanceSourcesIfChanged()
        {
            DateTime stamp = BalanceSources.ConfigWriteUtc();
            if (stamp != _balanceConfigStamp) ReloadBalanceSources();
        }

        public async Task<StatusReport> TestBalancesAsync()
        {
            ReloadBalanceSources();
            return await _status.CheckAsync();
        }

        private void Trace_(string s)
        {
            if (TraceN < TraceLog.Length) TraceLog[TraceN++] = s;
        }

        // ------------------------------------------------------------------ 初始化
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            DipScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            IntPtr h = new WindowInteropHelper(this).Handle;
            Handle = h;
            var src = HwndSource.FromHwnd(h);
            if (src != null) src.AddHook(WndProc);
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex);

            PlaceInitial();
            _status.ApiKey = Cfg.DeepSeekKey;   // 连通性任何 key 都测；余额需要 DeepSeek key
            _status.BalanceUrl = Cfg.BalanceUrl;   // 有自定义余额接口则优先于 DeepSeek
            _status.BalanceToken = Cfg.BalanceToken;
            _status.BalanceHeader = Cfg.BalanceHeader;
            _status.BalanceKey = Cfg.BalanceKey;
            _status.BalanceUnit = Cfg.BalanceUnit;
            ReloadBalanceSources();                           // 模板化自定义余额源(balances.json)
            StartBrain();                                     // ⚠ 表达环：不在这里接，前四环就是悬空代码（判据会全绿而她一言不发）
            StartLoop();
            _slow = new DispatcherTimer(DispatcherPriority.Background);
            _slow.Interval = TimeSpan.FromMilliseconds(120);
            _slow.Tick += (s2, e2) => SlowTick();
            _slow.Start();
            Trace_("dpi=" + DipScale.ToString("0.###") + " feetDip=" + R.FeetYDip.ToString("0.#"));
        }

        private void PlaceInitial()
        {
            var wa = WorkArea();
            double wPx = ActualWidth * DipScale, hPx = ActualHeight * DipScale;
            double x, y;
            if (!double.IsNaN(Cfg.X) && !double.IsNaN(Cfg.Y))
            {
                x = Cfg.X; y = Cfg.Y;
            }
            else
            {
                x = wa.Right - wPx - 40 * DipScale;
                y = wa.Bottom - R.FeetYDip * DipScale;      // 脚正好踩在工作区底边
            }
            x = Math.Min(Math.Max(x, wa.Left), Math.Max(wa.Left, wa.Right - wPx));
            y = Math.Min(Math.Max(y, wa.Top), Math.Max(wa.Top, wa.Bottom - hPx));
            Left = x / DipScale; Top = y / DipScale;
            Trace_("place.wa=" + wa.Left + "," + wa.Top + " " + wa.Right + "," + wa.Bottom
                + " xy=" + x.ToString("0.#") + "," + y.ToString("0.#"));
        }

        private Native.RECT WorkArea()
        {
            // ⚠ 不能用「窗口中心 → WindowFromPoint」推显示器：窗口位置还没落定时（Left 为 NaN）
            //   这个点会落到屏幕外，WindowFromPoint 返回 NULL，退化出一条假的工作区。
            //   改成直接问「本窗口所在显示器」，位置未定时退回光标所在显示器。
            Native.RECT wa;
            if (Native.WindowWorkArea(Handle, out wa)) return wa;
            Native.POINT c;
            if (Native.GetCursorPos(out c)) return Native.WorkAreaAt(c.X, c.Y);
            wa = new Native.RECT();
            wa.Right = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            wa.Bottom = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            return wa;
        }

        private void StartLoop() { CompositionTarget.Rendering += OnRender; }
        public void StopLoop() { CompositionTarget.Rendering -= OnRender; }

        // ------------------------------------------------------------------ 主循环
        private void OnRender(object sender, EventArgs e)
        {
            double t = _clock.Elapsed.TotalSeconds;
            GateCalls++;
            // 每秒记一格「合成器频率 vs 实际渲染频率」——桌宠常驻，这两条曲线就是耗电的全部由来
            if (t - _bucketT >= 1.0)
            {
                double span = t - _bucketT;
                RateLog.Add("t=" + t.ToString("0.0") + ": comp=" + ((GateCalls - _bCalls) / span).ToString("0.0")
                    + "Hz rendered=" + ((RenderedFrames - _bFrames) / span).ToString("0.0")
                    + "Hz hidden=" + (_hidden ? 1 : 0));
                _bucketT = t; _bCalls = GateCalls; _bFrames = RenderedFrames;
            }
            double dt = t - _lastT;
            if (dt < 1.0 / Fps - 0.0015) return;         // 桌宠常驻，主动限帧省电
            GatePass++;
            _lastT = t;
            if (dt > 0.5) dt = 1.0 / Fps;                // 系统卡顿/休眠恢复时别积分一大步

            // ⚠ 淡入淡出必须在「隐藏」判断**之前**跑：否则一旦隐退就再也不会恢复
            //   （本机实测踩到：全屏窗口关掉后阿助永远不回来了）。这是隐退功能的命门。
            StepFade(dt);
            // 气泡流推进：与渲染同拍（不生独立计时器，省一个常驻开销）
            if (_feed != null) { _feed.Step(t, dt); SyncBubbleWindow(); }
            if (_hidden) return;

            var sw = Stopwatch.StartNew();
            R.ApplyPose(Pose.Step(dt));
            StepMotion(dt);
            RenderedFrames++;
            double ms = sw.Elapsed.TotalMilliseconds;
            SumFrameMs += ms;
            if (ms > MaxFrameMs) MaxFrameMs = ms;
        }

        // ------------------------------------------------------------------ 命中穿透
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_NCHITTEST)
            {
                long v = l.ToInt64();
                int sx = (short)(v & 0xFFFF);
                int sy = (short)(((v >> 16) & 0xFFFF));
                handled = true;
                if (ForceHitThrough) { LastHitKind = "forced-transparent"; return new IntPtr(Native.HTTRANSPARENT); }
                if (Menu != null && Menu.Locked) { LastHitKind = "locked"; return new IntPtr(Native.HTTRANSPARENT); }
                if (_hidden) { LastHitKind = "hidden"; return new IntPtr(Native.HTTRANSPARENT); }
                // ⚠⚠ 这里**不能**除以 DipScale。
                //   WM_NCHITTEST 的 lParam 是**物理像素**，而 WPF 的 `PointFromScreen` 期望的
                //   也正是物理像素 —— 实测 `PointToScreen(DIP)` 返回的就是物理像素（dip(160,180)
                //   → screen(1140,650)，窗口左边界 900 + 160×1.5），roundtrip 自洽。
                //   曾经写成 `sx / DipScale`：在 100% 缩放的显示器上**歪打正着**，一到 150%
                //   就每点必错 —— 算出的客户区坐标偏到窗口外（实测点模型得到 -395），
                //   恒判透明 ⇒ 点模型也穿透 ⇒ 拖不动、点不跳、右键菜单弹不出来。
                var p = PointFromScreen(new Point(sx, sy));
                bool on = R.HitTest(p);
                LastHitKind = (on ? "client" : "transparent") + " " + sx + "," + sy
                            + " -> " + (int)p.X + "," + (int)p.Y;
                if (SelfTestMode) { HitLog[HitLogN % HitLog.Length] = LastHitKind; HitLogN++; }
                HitThrough = !on;
                HitThroughChecks++;
                if (HitThrough) HitThroughPass++;
                return new IntPtr(on ? Native.HTCLIENT : Native.HTTRANSPARENT);
            }
            return IntPtr.Zero;
        }

        // ------------------------------------------------------------------ 拖拽 / 抛物
        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            DownCount++;
            // 手动双击：220ms 内、落点相距 < 12px 判为双击（比 WPF ClickCount 可靠，
            // 因为第一次点击会进入拖拽并重排窗口，可能打断系统的双击计数）
            var pt = e.GetPosition(this);
            long now = Environment.TickCount64;
            if (now - _lastDownT < 220 && Math.Abs(pt.X - _lastDownP.X) < 12 && Math.Abs(pt.Y - _lastDownP.Y) < 12)
            {
                _doubleTap = true;
                _lastDownT = -999;                    // 吞掉本次，防三连击再触发
                return;
            }
            _lastDownT = now; _lastDownP = pt;
            _doubleTap = false;
            if (Menu != null && Menu.Locked) { DownRejectedLock++; return; }
            if (!R.HitTest(e.GetPosition(this))) { DownRejectedHit++; return; }
            _dragging = true;
            _airborne = false;
            Pose.Dragging = true; Pose.Airborne = false; Pose.SpinDrive = 0;   // 新一次拖拽从停转开始
            _vx = _vy = 0;
            Native.GetCursorPos(out _dragOrigin);
            _dragLeft0 = Left * DipScale;
            _dragTop0 = Top * DipScale;
            _lastX = _dragLeft0; _lastY = _dragTop0;
            _lastMoveT = _clock.Elapsed.TotalSeconds;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            MoveCount++;
            if (!_dragging) return;
            Native.POINT c; Native.GetCursorPos(out c);
            double nx = _dragLeft0 + (c.X - _dragOrigin.X);
            double ny = _dragTop0 + (c.Y - _dragOrigin.Y);
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastMoveT;
            if (dt > 0.004)
            {
                double ivx = (nx - _lastX) / dt, ivy = (ny - _lastY) / dt;
                _vx = _vx * 0.55 + ivx * 0.45;          // 平滑：不然后几个字节的抖动会决定抛多远
                _vy = _vy * 0.55 + ivy * 0.45;
                _lastX = nx; _lastY = ny; _lastMoveT = now;
            }
            // 左右拖拽：把横向速度喂给引擎，它按速度给角速度、随时间衰减
            Pose.SpinDrive = _vx;
            Left = nx / DipScale; Top = ny / DipScale;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            UpCount++;
            if (_doubleTap) { _doubleTap = false; ShowBubble(); e.Handled = true; return; }   // 双击 → 状态气泡
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            Cursor = null;
            double dist = Math.Abs(Left * DipScale - _dragLeft0) + Math.Abs(Top * DipScale - _dragTop0);
            if (dist < 4) { Pose.Dragging = false; Pose.Airborne = false; Pose.SpinDrive = 0; Pose.TriggerJump(); return; }   // 没动 = 点了一下 → 跳
            _vx = Clamp(_vx, -3500, 3500);
            _vy = Clamp(_vy, -3500, 3500);
            _airborne = true;
            Pose.Dragging = false; Pose.Airborne = true;    // 自由落体：角速度沿用拖拽末刻值，随衰减继续翻滚
        }

        private void OnRight(object sender, MouseButtonEventArgs e)
        {
            if (Menu != null) Menu.ShowAt(System.Windows.Forms.Cursor.Position);
            e.Handled = true;
        }

        // ------------------------------------------------------------------ 气泡流（独立浮窗：双击 / 托盘「状态…」/ 发言共用）
        //
        // 为什么不用窗口内覆盖层（v1 的做法）：
        //   宠物窗口只有 176×220 / 240×300 / 320×400 DIP 三档，而模型占视野高度 ≈ 77% 且纵向居中
        //   ⇒ 窗口顶部只剩约 11.5% 的空当。多行气泡塞不进去，只能压在模型头上（实测正好盖住脸）。
        // 所以正解是**换落点**：气泡浮窗贴在宠物窗口**之外**，零遮挡，宽高也不受窗口约束。
        // 2026-09-20 起内容从"单块文本"换成 **BubbleFeed 气泡流**：发言与状态读数同屏共存。
        private Window BuildBubble()
        {
            _feed = new BubbleFeed();
            _feed.Changed += OnFeedChanged;
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,          // 别把焦点从用户正在敲的窗口抢走
                ResizeMode = ResizeMode.NoResize,
                Topmost = Cfg.Topmost,
                SnapsToDevicePixels = true,
                Content = _feed,
                Width = 1, Height = 1,
                Visibility = Visibility.Collapsed,
                Title = "阿助桌宠·气泡",
            };
            w.SourceInitialized += (s2, e2) =>
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                // 工具窗（不进 Alt+Tab）＋ 不激活 ＋ 点击穿透。
                // ⚠ 气泡是**另一个顶层窗口**，靠 IsHitTestVisible 管不住（那只管同一窗口内部的命中）
                //   —— 不设 WS_EX_TRANSPARENT 的话，它会把底下的鼠标整块吃掉。
                ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex);
            };
            return w;
        }

        /// <summary>气泡流结构变化（新增／变空）→ 显窗、重定位、变空时收起并释放占用。</summary>
        private void OnFeedChanged()
        {
            if (_feed == null) return;
            if (_feed.AnyLive)
            {
                _bubbleWin.Topmost = Topmost;
                _bubbleWin.Visibility = Visibility.Visible;
                SyncBubbleWindow();
            }
            else if (_bubbleWin != null && _bubbleWin.Visibility == Visibility.Visible)
            {
                _bubbleWin.Visibility = Visibility.Collapsed;
                if (_brain != null) _brain.Bubble.Clear();   // ⚠ 全部收起后必须释放占用，否则她再也说不了话
            }
        }

        /// <summary>每帧把气泡流推进的结果落到浮窗上：量窗尺寸、摆位。宠物移动也经此重定位。</summary>
        private void SyncBubbleWindow()
        {
            if (_bubbleWin == null || _feed == null || !_feed.AnyLive) return;
            if (_feed.TotalWidth < 1 || _feed.TotalHeight < 1) _feed.Step(_clock.Elapsed.TotalSeconds, 0.016);
            _bubbleWin.Width = _feed.TotalWidth + 2;
            _bubbleWin.Height = _feed.TotalHeight + 2;
            UpdateBubblePos();
        }

        public async void ShowBubble()
        {
            _brain?.Bubble.Note(BubbleArbiter.Status);
            // 配置中心可用独立进程打开；不能只靠同进程回调，否则保存成功后气泡仍拿启动时旧列表。
            ReloadBalanceSourcesIfChanged();
            double now = _clock.Elapsed.TotalSeconds;
            _feed.Push(FeedKind.Status, "正在检测网络…", 2.5, 0, now);

            // 复用缓存窗口内的探测结果，避免频繁打余额接口（时限走 StatusProbe.CacheSeconds，别在这里写死）
            StatusReport rep;
            if (_status.FresherThan(StatusProbe.CacheSeconds))
            {
                PushStatusRows(_status.GetCached());
                return;
            }
            var cached = _status.GetCached();
            // 先展示上次结果（有任何一路有值就先用着），后台再刷
            if (cached != null && (cached.Reachable || cached.NoKey || cached.BalanceOk
                                   || cached.TraeOk || cached.WorkbuddyOk))
                PushStatusRows(cached);
            try
            {
                var fresh = await Task.Run(() => _status.CheckAsync());
                PushStatusRows(fresh);
            }
            catch
            {
                // 兜底：任何探测异常都不允许崩掉 async void 的桌宠
                _feed.Push(FeedKind.Error, "检测出错", 4, 0, now);
            }
        }

        /// <summary>把状态读数拆成逐条气泡，依次浮现（网络 / 每路积分 / 每条动态源）。</summary>
        private void PushStatusRows(StatusReport rep)
        {
            double now = _clock.Elapsed.TotalSeconds;
            var rows = StatusProbe.FormatRows(rep);
            for (int i = 0; i < rows.Count; i++)
                _feed.Push(FeedKind.Status, rows[i], 6, i * 0.45, now);   // 错峰 → 「逐条浮现」
        }

        /// <summary>
        /// 托盘「她看见了什么…」：**在本机**读一遍「她该看的那个窗口」上写着的字，把结果放进状态气泡。
        ///
        /// ⚠ 四个刻意的选择：
        ///   ① 走**状态**气泡，不是台词 —— 这是你要看的读数，不是她说的话。
        ///      顺带也避开了「她正在说一句、你的读数把它顶掉」（状态可以打断台词，反之不行）。
        ///   ② 读 **expectProc=null**（不预设是哪个应用）：这是你手动问她「看这个」，
        ///      不存在「她刚说的是 A」这个前提，所以没有「对不上」可言。自动路径仍严格要求一致。
        ///   ③ 原文直接显示 —— 它本来就没出过本机，藏起来就没意义了。
        ///   ④ **不再自己问一次前台**：读的是谁由 `OcrEye.ReadForeground` 从同一个窗口读数里带回来
        ///      （`r.Proc` / `r.Title`）。以前这里 Probe 一次、它内部再 Probe 一次 ＝ 同一份数据两个落点，
        ///      前台一变，标题栏和正文就会各说一个应用 —— 而且看不出来。
        ///
        /// ⚠ **不许 .Result**：读一次屏是 200–400 ms，卡在渲染线程上桌宠会当场僵住。
        /// </summary>
        public async void ShowScreenRead()
        {
            if (_brain != null) _brain.Bubble.Note(BubbleArbiter.Status);
            double now0 = _clock.Elapsed.TotalSeconds;
            _feed.Push(FeedKind.Status, "正在读屏幕上写的字…", 4, 0, now0);

            OcrEye.Result r;
            try { r = await Task.Run(() => OcrEye.ReadForeground(null)); }
            catch (Exception ex)
            {
                // async void 里漏出去的异常会带走整个桌宠 —— 这里必须自己兜住。
                _feed.Push(FeedKind.Error, "读屏幕出错：" + ex.Message, 8, 0.25, _clock.Elapsed.TotalSeconds);
                return;
            }
            LastScreenRead = r;      // ⚠ 给判据一个**精确**的读数：气泡文本要解析「读的是谁」，容易看走眼
            _feed.Push(FeedKind.Status, FormatScreenRead(r), 8, 0.25, _clock.Elapsed.TotalSeconds);
        }

        /// <summary>最近一次读屏的结构化结果（内存里，和气泡显示的是同一个对象）。
        /// ⚠ 给判据用：`--ocrvis` 要断言「读的不是她自己」，解析气泡文本太脆。</summary>
        public OcrEye.Result LastScreenRead;

        /// <summary>
        /// **纯函数**：把一次读屏的结果排成气泡文本。
        /// 抽出来是为了能离线断言三件事：**读不到时必须说出为什么**（不许显示一片空白）、
        /// **原文不会长到把气泡撑爆**、以及**读的是谁要写在最上面**。
        /// 运行时它和判据看的是同一个函数。
        /// </summary>
        public static string FormatScreenRead(OcrEye.Result r)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("看见了：").Append(StubSpeaker.Human(r == null ? null : r.Proc));
            if (r != null && !string.IsNullOrEmpty(r.Title)) sb.Append("　").Append(Clip1(r.Title, 26));
            // ⚠ 回退来的读数必须**标出来**：它不是「你现在最前面的那个窗口」，
            //   而是「你上一次真正在用的那个」（你点托盘时前台已经变成她自己了）。
            //   不标的话，你会以为她读错窗口了。
            if (r != null && r.FromLast && r.AgeSec >= 3)
                sb.Append("（").Append((int)r.AgeSec).Append(" 秒前那个窗口）");

            if (r == null) { sb.Append("\r\n（没有结果）"); return sb.ToString(); }
            if (!r.Ok) { sb.Append("\r\n没读：").Append(r.Why); return sb.ToString(); }
            if (!r.Any)
            {
                // ⚠ 「一个字都没有」和「读失败」必须长得不一样 —— 否则你分不清是功能坏了
                //   还是这个窗口本来就没有字（看图、空白页、游戏画面）。
                sb.Append("\r\n这一屏没有字（不是出错，用时 ").Append(r.Ms).Append(" ms）");
                if (!string.IsNullOrEmpty(r.Caveat)) sb.Append("\r\n（").Append(r.Caveat).Append("）");
                return sb.ToString();
            }

            sb.Append("\r\n约 ").Append(r.Chars).Append(" 字 · ").Append(r.Lines).Append(" 行 · ")
              .Append(r.Ms).Append(" ms（本机识别）\r\n");
            const int cap = 200;
            sb.Append(r.Text.Length > cap ? r.Text.Substring(0, cap) + "…（共 " + r.Chars + " 字）" : r.Text);
            // ⚠⚠ 原文与「会不会发出去」必须**同屏**出现：你看到字的那一刻，就该看到它的去向。
            //   分开两处显示 ＝ 两边迟早各说一套（本仓：同一份数据两个落点）。
            sb.Append("\r\n").Append(OcrEye.SendNote(r.WillSend, r.SendWhy));
            if (!string.IsNullOrEmpty(r.Caveat)) sb.Append("\r\n（").Append(r.Caveat).Append("）");
            return sb.ToString();
        }

        private static string Clip1(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }

        /// <summary>把气泡摆在宠物窗口**之外**：优先上方，上方不够放下方，都不够就夹进工作区。
        /// 宠物一动就会重新走这里（构造函数挂了 LocationChanged），拖拽 / 抛物 / 换挡都会跟上。</summary>
        private void UpdateBubblePos()
        {
            if (_bubbleWin == null || _bubbleWin.Visibility != Visibility.Visible) return;
            _bubbleWin.UpdateLayout();
            if (ForceBubbleOnPet)   // 负对照专用：故意压在宠物正中（正常路径绝不会走到这里）
            {
                _bubbleWin.Left = Left + (ActualWidth - _bubbleWin.ActualWidth) / 2;
                _bubbleWin.Top = Top + (ActualHeight - _bubbleWin.ActualHeight) / 2;
                return;
            }
            double bwDip = _bubbleWin.ActualWidth, bhDip = _bubbleWin.ActualHeight;
            // 首次显示的那一帧布局可能还没落定 ⇒ 用估算值摆个大概，下一帧（SetBubbleText/AutoClose 路径）会摆正
            if (bwDip < 1 || bhDip < 1) { bwDip = 320; bhDip = 54; }

            var wa = WorkArea();                        // 物理像素
            const double Gap = 6, Edge = 4;             // DIP
            double bwPx = bwDip * DipScale, bhPx = bhDip * DipScale;
            double leftPx = (Left + ActualWidth / 2) * DipScale - bwPx / 2;   // 与宠物水平居中
            double topPx = Top * DipScale - bhPx - Gap * DipScale;            // 优先贴在窗口上边缘之外

            if (topPx < wa.Top + Edge * DipScale)                             // 上方装不下（宠物贴着屏幕顶）
                topPx = (Top + ActualHeight) * DipScale + Gap * DipScale;     // 改放窗口下边缘之外
            if (topPx + bhPx > wa.Bottom - Edge * DipScale)                   // 下方也越界 ⇒ 夹进工作区
                topPx = Math.Max(wa.Top + Edge * DipScale, wa.Bottom - bhPx - Edge * DipScale);
            leftPx = Math.Min(Math.Max(leftPx, wa.Left + Edge * DipScale),
                              Math.Max(wa.Left + Edge * DipScale, wa.Right - bwPx - Edge * DipScale));

            _bubbleWin.Left = leftPx / DipScale;        // ⚠ Window.Left/Top 的单位是 DIP，wa 是物理像素
            _bubbleWin.Top = topPx / DipScale;
        }

        // 气泡文本的渲染已上移到 StatusProbe.Format —— 让「气泡显示什么」与
        // 「--statustest 断言什么」共用同一个实现，免得两套口径各自演化。

        private void StepMotion(double dt)
        {
            if (_dragging || !_airborne) return;
            var wa = WorkArea();
            double wPx = ActualWidth * DipScale, hPx = ActualHeight * DipScale;
            double feetPx = R.FeetYDip * DipScale;
            double x = Left * DipScale, y = Top * DipScale;

            _vy += Gravity * dt;
            x += _vx * dt;
            y += _vy * dt;

            bool onFloor = false;
            double floorTop = wa.Bottom - feetPx;
            if (y >= floorTop)
            {
                y = floorTop;
                if (_vy > 90)
                {
                    Pose.TriggerBounce(Math.Min(1.0, _vy / 1600.0));
                    _vy = -_vy * Restitution;
                }
                else { _vy = 0; onFloor = true; }
            }
            if (y < wa.Top) { y = wa.Top; if (_vy < 0) _vy = -_vy * Restitution; }
            if (x < wa.Left) { x = wa.Left; _vx = -_vx * 0.5; }
            if (x > wa.Right - wPx) { x = wa.Right - wPx; _vx = -_vx * 0.5; }

            _vx *= Math.Pow(onFloor ? 0.02 : 0.55, dt);    // 地面摩擦 / 空气阻力

            // 自由落体：按当前横向速度持续给角速度（随空气阻力与自身衰减逐渐停转）
            Pose.Airborne = true;
            Pose.SpinDrive = _vx;

            if (onFloor && Math.Abs(_vx) < 60)
            {
                _vx = 0;
                // 停稳后贴边停靠（只在不靠近时轻微吸一下，不做强制吸附）
                if (x - wa.Left < 44 * DipScale) x = wa.Left + 8 * DipScale;
                else if (wa.Right - (x + wPx) < 44 * DipScale) x = wa.Right - wPx - 8 * DipScale;
                _airborne = false;
                Pose.Airborne = false; Pose.SpinDrive = 0;   // 落定 → 回跟随，贝塞尔曲线缓慢回正面向用户
                Cfg.X = x; Cfg.Y = y;                       // 记住落点
            }
            Left = x / DipScale; Top = y / DipScale;
        }

        // ------------------------------------------------------------------ 慢速轮询：打盹 / 隐退 / 调光
        private void SlowTick()
        {
            Pose.IdleSeconds = (SelfTestMode && ForcedIdle >= 0) ? ForcedIdle : Native.IdleSeconds();

            // 光标跟随：离得越近转得越多；靠近则呼吸加快
            Native.POINT c;
            if (Native.GetCursorPos(out c))
            {
                var wa = WorkArea();
                double cx = (Left + ActualWidth / 2) * DipScale;
                double cy = (Top + R.FeetYDip * 0.8) * DipScale;
                double dx = c.X - cx, dy = c.Y - cy;
                double d = Math.Sqrt(dx * dx + dy * dy);
                Pose.Near = d < 260 * DipScale;
                if (_dragging)
                {
                    Pose.Dragging = true;                       // 拖拽：翻滚由 OnMove 每帧写 SpinRate（∝横向速度）
                }
                else
                {
                    Pose.Dragging = false;
                    // 跟随：只按水平偏移小幅转头（限定 FollowMax）；光标回到水平中线 → 缓慢回正面向用户
                    Pose.CursorYaw = Pose.FollowMax * Clamp(dx / (400 * DipScale), -1, 1);
                }
            }

            // 全屏应用时自动隐退（不然打游戏、放片时它一直挡在前面）
            bool fs = Native.ForegroundIsFullscreen(Handle);
            _hideTarget = (fs || ManualHidden) ? 1.0 : 0.0;

            // 按时段调光：22:30 之后渐入暖色低透明，06:00 之后恢复
            double nk = 0;
            if (Cfg.NightDim)
            {
                DateTime now = DateTime.Now;
                double h = now.Hour + now.Minute / 60.0;
                if (h >= 23.0 || h < 6.0) nk = 1;
                else if (h >= 21.0) nk = (h - 21.0) / 2.0;
                else if (h < 7.5) nk = 1 - (h - 6.0) / 1.5;
            }
            NightK = nk;
            R.SetNight(nk);

            // ---- 表达环采样（P0 第四环）----
            // ⚠ 放在最后：它只读前台，与姿态／调光互不相干；万一它抛异常也不该影响上面那些。
            //   节流到 1 秒 —— Probe() 每次都要开进程句柄，120ms 一次是白烧电。
            //   「停留 8 秒才算一次观察」这件事由 Watcher 内部管，这里的节流只影响时间精度（±1 秒）。
            long ms = _clock.ElapsedMilliseconds;
            // ⚠ 抑制闸的第一道（P2 的其余几道还没做，但这道零成本且显然正确）：
            //   全屏／手动隐藏时她**不该**开口 —— 你在打游戏、开会、放片，冒一句话出来是骚扰。
            //   注意用的是 _hideTarget 而不是 HideK：淡出要 0.35 秒，不该让她在这期间抢一句。
            if (ms - _lastSenseMs >= 1000)
            {
                _lastSenseMs = ms;

                // ⚠⚠ 这一句**必须在 SpeechOn 之外**。它只做一件事：记下「你上一个真实在用的窗口」。
                //   寄生在自发说话开关底下的话，你把自发说话关掉之后，托盘那项「她看见了什么…」
                //   就会永远说「还没见过别的窗口」—— 一个功能的可用性被另一个开关的副作用决定，
                //   而现象只是「点了没反应」。（本仓那条：凡有开关能改行为的功能，两个档位都要跑。）
                try { Watcher.RememberForeground(); }
                catch (Exception ex) { Trace_("sense: " + ex.Message); }

                if (Cfg.SpeechOn && _brain != null && _hideTarget < 0.5)
                {
                    try { _brain.Tick(); }
                    catch (Exception ex) { Trace_("brain: " + ex.Message); }
                }

                // ---- 小时总结（用户 2026-09-20 拍板：真模型 / 落库 / 开机后每满 60 分钟）----
                // ⚠ 不该被 SpeechOn 拦：总结是**写给你看的落库产出**，不是自发说话 ——
                //   把它寄生在「她会自己说话」底下，关掉那个开关总结就静默消失
                //   （同「功能可用性寄生在另一个开关的副作用上」那条教训）。
                // ⚠ 异步、不 await（这里是 DispatcherTimer）；在途时跳过检查，避免重叠。
                if (Cfg.SummaryOn && !_summaryRunning
                    && HourlySummary.Due(DateTime.Now, _lastSummaryAt, Cfg.SummaryIntervalMin))
                {
                    _lastSummaryAt = DateTime.Now;
                    _summaryRunning = true;
                    var from = _lastSummaryAt.AddMinutes(-Cfg.SummaryIntervalMin);
                    var to = _lastSummaryAt;
                    string vault = Cfg.VaultPath;
                    Trace_("summary: 触发 " + from.ToString("HH:mm") + "–" + to.ToString("HH:mm"));
                    HourlySummary.RunAsync(vault, Memory.ReadAll(out _, out _), from, to)
                        .ContinueWith(t =>
                        {
                            _summaryRunning = false;
                            if (t.Status == TaskStatus.RanToCompletion)
                                Trace_(t.Result == null ? "summary: 落盘成功" : "summary: " + t.Result);
                            else
                                Trace_("summary: 异常 " + t.Exception?.GetBaseException().Message);
                        });
                }
            }
        }

        // ------------------------------------------------------------------ 表达环：她开口

        /// <summary>
        /// 建接线。⚠ 说话人是**配置项**，不是写死的常量：
        ///   模板（stub）零成本、逐字节可预测，用来验**时机**；
        ///   真模型（trae）用来验**话**。两者必须能互换 —— 否则你分不清
        ///   「她今天说得无聊」是话的问题还是时机的问题（这正是当初先做 stub 的理由）。
        /// </summary>
        private void StartBrain()
        {
            ISpeaker speaker = ForceSilent ? (ISpeaker)new SilentSpeaker()
                             : Cfg.SpeechLlm ? (ISpeaker)new LlmSpeaker() : new StubSpeaker();
            _brain = new Brain(Watcher.Probe, () => DateTime.Now, speaker, OnVerdict, Trace_);
            _brain.WireRoast();          // 吐槽通道：LastSpoke 接到 Gate（不接 ⇒ roast 永不触发，判据红）
        }

        /// <summary>换说话人（托盘切「台词用模型生成」）。
        /// ⚠ 只换 Speaker，**不重建整个 Brain** —— 重建会把闸门计数和气泡占用一起清掉
        ///   （同「同一份数据两个落点」的变体：状态被复制成两份）。</summary>
        public void RebuildSpeaker()
        {
            if (_brain == null) return;
            _brain.Speaker = ForceSilent ? (ISpeaker)new SilentSpeaker()
                           : Cfg.SpeechLlm ? (ISpeaker)new LlmSpeaker() : new StubSpeaker();
        }

        /// <summary>
        /// **配置下发唯一入口**（2026-09-20，主面板引入）。
        /// 把「改配置 → 让改动即时生效」的全部动作收在一处：托盘勾选项、主设置面板、
        /// 将来任何配置入口都调它 —— 否则每个入口各写一套同步，迟早分叉
        /// （本仓老毛病「同一份数据两个落点」；旧版托盘里就这么写过一次 `OcrEye.SendText = ...`）。
        ///
        /// 覆盖的静态位：`OcrEye.Enabled`／`OcrEye.SendText`（D 档）、`WatchLoop.RoastOn`／
        /// `Watcher.AdaptiveOn`（自动感知）、窗口置顶、说话人。
        /// ⚠ 它**不** Save —— 落盘是调用方的事（有些入口改的是内存态）。
        /// </summary>
        public void ApplyConfig()
        {
            OcrEye.Enabled = Cfg.OcrOn;
            OcrEye.SendText = Cfg.OcrSendText;
            WatchLoop.RoastOn = Cfg.RoastOn;
            Watcher.AdaptiveOn = true;
            Topmost = Cfg.Topmost;
            RebuildSpeaker();
        }

        /// <summary>⚠ 这一回调**可能在后台线程上**被调（真 LLM 是异步的）⇒ 一律切回 UI 线程再碰控件。</summary>
        private void OnVerdict(Verdict v, Observation obs, DateTime at)
        {
            if (v == null || !v.Speak) return;
            Dispatcher.BeginInvoke(new Action(() => SayLine(v.Text)));
        }

        /// <summary>她开口 —— 走**气泡流**（那是她的嘴），而不是另开一个聊天窗。
        /// 与状态读数同屏共存（2026-09-20 用户拍板：不再单槽互斥、不再 AllowSpeech 让路）。</summary>
        public void SayLine(string text)
        {
            if (string.IsNullOrEmpty(text) || _brain == null) return;
            _brain.Bubble.Note(BubbleArbiter.Speech);
            ReloadBalanceSourcesIfChanged();
            // 她开口也跳一下（2026-09-20）：雀跃感跟台词走。被拖住／腾空时跳到一半很怪，跳过。
            if (!Pose.Dragging && !Pose.Airborne) Pose.TriggerJump();
            _bubbleWin.Topmost = Topmost;
            _bubbleWin.Visibility = Visibility.Visible;
            _feed.Push(FeedKind.Speech, text, 9, 0, _clock.Elapsed.TotalSeconds);   // 台词比状态多停一会儿
        }

        /// <summary>托盘「让她说一句」：立刻开口，不等停留阈值、不受闸门拦（是你点的，不是她自作主张）。</summary>
        public void ForceSpeak()
        {
            if (_brain == null) return;
            try { _brain.SpeakNow(); }
            catch (Exception ex) { Trace_("speaknow: " + ex.Message); }
        }

        // 视觉区分已下沉到 BubbleFeed：发言＝暖象牙、状态＝冷浅蓝、出错＝浅红（不再有单块的 _bubbleFace 换色）。
        private void StepFade(double dt)
        {
            double k = 1 - Math.Pow(0.002, dt / 0.35);
            HideK += (_hideTarget - HideK) * Math.Min(1, k);
            if (_hideTarget > 0.5 && HideK > 0.995)
            {
                HideK = 1;
                if (!_hidden)
                {
                    _hidden = true; Visibility = Visibility.Hidden;
                    // 全屏应用里桌宠隐退了，气泡不能还挂在人家画面上（气泡是独立窗口，不会跟着隐藏）
                    if (_bubbleWin != null) _bubbleWin.Visibility = Visibility.Collapsed;
                    if (_brain != null) _brain.Bubble.Clear();   // 同 AutoClose：收起必须同时释放占用
                }
            }
            else if (_hideTarget < 0.5 && _hidden && HideK < 0.995)
            {
                _hidden = false;
                Visibility = Visibility.Visible;
            }
            double newOp = (1 - HideK) * (1 - 0.22 * NightK);
            if (Math.Abs(newOp - Opacity) > 0.002) Opacity = newOp;
        }

        // ------------------------------------------------------------------ 外部命令
        public void SetSize(int idx)
        {
            Cfg.SizeIndex = idx;
            double cx = Left + ActualWidth / 2, cy = Top + ActualHeight / 2;
            Width = Cfg.WDip; Height = Cfg.HDip;
            R.Resize(Cfg.WDip, Cfg.HDip);
            Left = cx - Width / 2; Top = cy - Height / 2;
            PlaceFeetOnFloor();
            Cfg.Save();
        }

        public void PlaceFeetOnFloor()
        {
            var wa = WorkArea();
            Top = (wa.Bottom - R.FeetYDip * DipScale) / DipScale;
            Left = Math.Min(Math.Max(Left, wa.Left / DipScale), (wa.Right - ActualWidth * DipScale) / DipScale);
            Cfg.X = Left * DipScale; Cfg.Y = Top * DipScale;
        }

        public void GoHome()
        {
            var wa = WorkArea();
            Left = (wa.Right - ActualWidth * DipScale - 40 * DipScale) / DipScale;
            PlaceFeetOnFloor();
            Cfg.Save();
        }

        public void NapNow() { Pose.ResetExtremes(); ForcedIdle = Pose.SleepAfter + 1; }

        public void Wake() { ForcedIdle = -1; Pose.DozeKReset(); }

        public void SavePos()
        {
            Cfg.X = Left * DipScale; Cfg.Y = Top * DipScale;
            Cfg.Save();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            DipScale = newDpi.DpiScaleX;
        }

        private static double Clamp(double v, double a, double b) { return v < a ? a : v > b ? b : v; }
    }
}
