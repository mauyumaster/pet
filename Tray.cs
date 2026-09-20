// 托盘与右键菜单 —— 桌宠唯一的「控制面板」。
// 图标在运行时画，不依赖 .ico 资源文件（少一个二进制资产、少一处路径坑）。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AzhuPet
{
    internal sealed class PetMenu : IDisposable
    {
        public bool Locked;                       // 全窗鼠标穿透（锁住，谁也点不到）

        private readonly PetWindow _w;
        private readonly NotifyIcon _ni;
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly ToolStripMenuItem _miShow, _miTop, _miLock, _miNight, _miAuto;
        private ToolStripMenuItem _miSpeech, _miLlm, _miScreen;
        private IntPtr _iconHandle = IntPtr.Zero;

        public PetMenu(PetWindow w, bool wantTray)
        {
            _w = w;

            _miShow = new ToolStripMenuItem("隐藏阿助", null, (s, e) => ToggleShow()) { CheckOnClick = false };
            _miTop = new ToolStripMenuItem("始终置顶", null, (s, e) => { _w.Topmost = !_w.Topmost; _w.Cfg.Topmost = _w.Topmost; _w.Cfg.Save(); }) { CheckOnClick = false };
            _miLock = new ToolStripMenuItem("鼠标穿透（锁住）", null, (s, e) => SetLocked(!Locked)) { CheckOnClick = false };
            _miNight = new ToolStripMenuItem("夜间调光", null, (s, e) => { _w.Cfg.NightDim = !_w.Cfg.NightDim; _w.Cfg.Save(); }) { CheckOnClick = false };
            _miAuto = new ToolStripMenuItem("开机自启", null, (s, e) => ToggleAutostart()) { CheckOnClick = false };
            _miSpeech = new ToolStripMenuItem("她会自己说话", null,
                (s, e) => { _w.Cfg.SpeechOn = !_w.Cfg.SpeechOn; _w.Cfg.Save(); }) { CheckOnClick = false };
            _miLlm = new ToolStripMenuItem("台词用模型生成", null,
                (s, e) => { _w.Cfg.SpeechLlm = !_w.Cfg.SpeechLlm; _w.Cfg.Save(); _w.RebuildSpeaker(); }) { CheckOnClick = false };
            // ⚠⚠ 这一项是**跨线开关**：勾上之后，屏幕上的文字会被放进发给模型的提示（出本机）。
            //   它默认关（与 EyeOn 同一种理由：不可逆的事不能靠口头承诺）。
            //   ⚠ 勾选必须**同时**改进程内静态位 —— 只改配置的话要重启才生效，
            //     而「勾了没反应」正是本仓那条老毛病（功能可用性不能寄生在别的东西上）。
            //   ⚠ 不在这里弹气泡：勾选状态本身在菜单里看得见，而「会／不会发出去」这句
            //     写在「她看见了什么…」那个气泡里（那里才是你核对后果的地方）。
            _miScreen = new ToolStripMenuItem("说话时带上屏幕上的字", null,
                (s, e) =>
                {
                    _w.Cfg.OcrSendText = !_w.Cfg.OcrSendText;
                    OcrEye.SendText = _w.Cfg.OcrSendText;
                    _w.Cfg.Save();
                }) { CheckOnClick = false };

            var size = new ToolStripMenuItem("大小");
            size.DropDownItems.Add(new ToolStripMenuItem("小", null, (s, e) => _w.SetSize(0)));
            size.DropDownItems.Add(new ToolStripMenuItem("中", null, (s, e) => _w.SetSize(1)));
            size.DropDownItems.Add(new ToolStripMenuItem("大", null, (s, e) => _w.SetSize(2)));

            _menu.Items.Add(_miShow);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miTop);
            _menu.Items.Add(_miLock);
            _menu.Items.Add(size);
            _menu.Items.Add(_miNight);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("回到右下角", null, (s, e) => _w.GoHome()));
            _menu.Items.Add(new ToolStripMenuItem("打个盹", null, (s, e) => _w.NapNow()));
            _menu.Items.Add(new ToolStripMenuItem("跳一下", null, (s, e) => _w.Pose.TriggerJump()));
            _menu.Items.Add(new ToolStripMenuItem("状态…", null, (s, e) => _w.ShowBubble()));
            // 本地读屏幕文字（D 档）。原文不出本机 —— 它显示的是**本机识别器**读到的字。
            _menu.Items.Add(new ToolStripMenuItem("她看见了什么…", null, (s, e) => _w.ShowScreenRead()));
            _menu.Items.Add(new ToolStripMenuItem("和桌宠说话…", null, (s, e) => OpenChat()));
            _menu.Items.Add(new ToolStripMenuItem("让她说一句", null, (s, e) => _w.ForceSpeak()));
            _menu.Items.Add(_miSpeech);
            _menu.Items.Add(_miLlm);
            _menu.Items.Add(new ToolStripMenuItem("台词模型设置…", null, (s, e) =>
                _w.Dispatcher.InvokeAsync(() =>
                {
                    using (var dlg = new ModelSettingsWindow(_w)) dlg.ShowDialog();
                    _w.RebuildSpeaker();     // 配完即时生效（通道在 ChatAsync 入口路由，但改了开关状态仍要重建说话人）
                })));
            _menu.Items.Add(_miScreen);
            _menu.Items.Add(new ToolStripMenuItem("余额配置…", null, (s, e) =>
                _w.Dispatcher.InvokeAsync(OpenBalanceSettings)));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miAuto);
            _menu.Items.Add(new ToolStripMenuItem("关于…", null, (s, e) => About()));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => Shutdown()));
            _menu.Opening += (s, e) => Sync();

            if (wantTray)
            {
                _ni = new NotifyIcon();
                _ni.Icon = MakeIcon(out _iconHandle);
                _ni.Text = "阿助桌宠";
                _ni.Visible = true;
                _ni.ContextMenuStrip = _menu;
                _ni.DoubleClick += (s, e) => ToggleShow();
                _ni.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowAt(Cursor.Position); };
                SetupNudge();
            }
        }

        // ---- 首启引导（2026-09-20，发布降门槛）----
        // 模型台词开着、但哪条通道都没配 → balloon 指路**一次**。
        // 静默失败是本仓老毛病（「点了没反应」）—— 与其让她第一次说话就报错，不如启动时就还清。
        // ⚠ 引导失败（balloon 抛异常等）不该拖垮启动：整段 try 吞掉。
        private void SetupNudge()
        {
            try
            {
                var cfg = PetConfig.Load();
                if (!cfg.SpeechLlm) return;    // 模板台词不花钱也不需要凭据，不打扰
                bool trae = System.IO.File.Exists(TraeChat.SecretPath());
                bool oa = TraeChat.ShouldUseOpenAi(
                    Tuple.Create(cfg.OpenAiBase, cfg.DeepSeekKey, cfg.OpenAiModel));
                if (trae || oa) return;
                _ni.BalloonTipTitle = "阿助：台词模型还没配";
                _ni.BalloonTipText = "「台词用模型生成」开着，但 Trae 凭据和 OpenAI 兼容端点都没配，"
                    + "现在会用免费模板台词。右键托盘 →「台词模型设置…」可接任何 OpenAI 兼容服务"
                    + "（DeepSeek／硅基流动／本地 ollama…）；只想用模板台词的话，取消勾选即可。";
                _ni.BalloonTipIcon = ToolTipIcon.Info;
                _ni.ShowBalloonTip(9000);
            }
            catch { }
        }

        public void ShowAt(System.Drawing.Point p)
        {
            Sync();
            _menu.Show(p);
        }

        private void Sync()
        {
            _miShow.Text = _w.ManualHidden ? "显示阿助" : "隐藏阿助";
            _miTop.Checked = _w.Topmost;
            _miLock.Checked = Locked;
            _miNight.Checked = _w.Cfg.NightDim;
            _miAuto.Checked = PetConfig.AutostartOn();
            _miSpeech.Checked = _w.Cfg.SpeechOn;
            _miLlm.Checked = _w.Cfg.SpeechLlm;
            _miScreen.Checked = _w.Cfg.OcrSendText;
            // ⚠ 本机读字关着的时候，这一项**必须灰掉** —— 否则你会勾上一个永远不会生效的开关
            //   （闸的第一道就是 OcrOn，勾了也只是看着开了）。
            _miScreen.Enabled = _w.Cfg.OcrOn;
            // ⚠⚠ 「勾了却什么都不会变」＝静默无效，本仓明令要避免。
            //   而这里正好有一个真实的空档：默认说话人是**模板**（StubSpeaker），
            //   它根本不读屏幕文字 ⇒ 只勾这一项不会有任何效果，且完全看不出来。
            //   所以把话写在标签上，不靠用户自己领悟。
            _miScreen.Text = _w.Cfg.OcrSendText && !_w.Cfg.SpeechLlm
                ? "说话时带上屏幕上的字（需先开上一项「台词用模型生成」）"
                : "说话时带上屏幕上的字";
        }

        private void ToggleShow()
        {
            _w.ManualHidden = !_w.ManualHidden;
            if (!_w.ManualHidden) _w.Wake();
        }

        private void SetLocked(bool on)
        {
            Locked = on;
            // 双保险：① 本进程的 NCHITTEST 直接返回穿透 ② 系统级 WS_EX_TRANSPARENT
            IntPtr h = _w.Handle;
            if (h != IntPtr.Zero)
            {
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                ex = on ? (ex | Native.WS_EX_TRANSPARENT) : (ex & ~Native.WS_EX_TRANSPARENT);
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex);
            }
        }

        private static ChatWindow _chat;

        private void OpenChat()
        {
            if (_chat == null || !_chat.IsLoaded)
            {
                _chat = new ChatWindow();
                _chat.Closed += (s, e) => _chat = null;
                _chat.Show();
            }
            else
            {
                _chat.Activate();
            }
        }

        private void ToggleAutostart()
        {
            bool on = PetConfig.AutostartOn();
            // ⚠ 单文件发布（PublishSingleFile）下 Assembly.Location 返回空串（IL3000），
            //   自启就会指向空路径。Environment.ProcessPath 在两种形态下都返回真 exe 路径。
            string exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) exe = System.Reflection.Assembly.GetEntryAssembly().Location;
            string exePath = exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? exe.Substring(0, exe.Length - 4) + ".exe" : exe;
            bool ok = PetConfig.SetAutostart(!on, exePath);
            if (!ok) MessageBox.Show("写入开机自启失败（注册表被拒）", "阿助桌宠");
        }

        private void OpenBalanceSettings()
        {
            // 桌宠通常置顶；设置窗若是普通窗口，会被宠物盖住右下角按钮。
            // 打开工作区期间临时放下桌宠，关闭后恢复用户原来的置顶选择。
            bool wasTopmost = _w.Topmost;
            _w.Topmost = false;
            var panel = new BalanceSettingsWindow(_w.ReloadBalanceSources);
            panel.Closed += (s, e) => _w.Topmost = wasTopmost;
            panel.Show();
            panel.Activate();
        }

        private void About()
        {
            var m = _w.R; 
            MessageBox.Show(
                "阿助桌宠 · 自建轻量壳 v1\r\n\r\n"
                + "渲染：" + m.Stats + "\r\n"
                + "窗口：" + _w.ActualWidth.ToString("0") + "×" + _w.ActualHeight.ToString("0") + " DIP，DPI "
                + (_w.DipScale * 100).ToString("0") + "%\r\n"
                + "已渲染帧：" + _w.RenderedFrames + "，平均每帧 " + AvgMs().ToString("0.##") + " ms\r\n\r\n"
                + "左键拖动可甩出，松手会自己落地；右键出这个菜单。",
                "关于阿助桌宠");
        }

        private double AvgMs() { return _w.RenderedFrames > 0 ? _w.SumFrameMs / _w.RenderedFrames : 0; }

        private void Shutdown()
        {
            Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private static Icon MakeIcon(out IntPtr handle)
        {
            var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var b = new SolidBrush(Color.FromArgb(255, 74, 96, 152)))
                    g.FillEllipse(b, 1, 1, 30, 30);
                using (var b = new SolidBrush(Color.FromArgb(255, 245, 250, 255)))
                {
                    g.FillEllipse(b, 9, 11, 6, 7);
                    g.FillEllipse(b, 17, 11, 6, 7);
                }
                using (var p = new Pen(Color.FromArgb(255, 255, 210, 120), 2f))
                    g.DrawArc(p, 11, 19, 10, 8, 20, 140);
            }
            handle = bmp.GetHicon();
            Icon ic = Icon.FromHandle(handle).Clone() as Icon;
            return ic;
        }

        public void Dispose()
        {
            try { if (_ni != null) { _ni.Visible = false; _ni.Dispose(); } } catch { }
            try { if (_iconHandle != IntPtr.Zero) Native.DestroyIcon(_iconHandle); } catch { }
        }
    }
}
