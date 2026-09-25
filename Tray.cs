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
        // ⚠ 2026-09-20 主面板引入后，托盘只留**高频 / 单手**的那些项。
        //   原先散在这里的配置勾选（说话／读屏／模型）已并入「设置…」——
        //   同一件事有两个入口，就是本项目那条老毛病（同一份数据两个落点）的温床。
        private readonly ToolStripMenuItem _miShow, _miLock;
        private IntPtr _iconHandle = IntPtr.Zero;

        public PetMenu(PetWindow w, bool wantTray)
        {
            _w = w;

            _miShow = new ToolStripMenuItem("隐藏阿助", null, (s, e) => ToggleShow()) { CheckOnClick = false };
            _miLock = new ToolStripMenuItem("鼠标穿透（锁住）", null, (s, e) => SetLocked(!Locked)) { CheckOnClick = false };

            var size = new ToolStripMenuItem("大小");
            size.DropDownItems.Add(new ToolStripMenuItem("小", null, (s, e) => _w.SetSize(0)));
            size.DropDownItems.Add(new ToolStripMenuItem("中", null, (s, e) => _w.SetSize(1)));
            size.DropDownItems.Add(new ToolStripMenuItem("大", null, (s, e) => _w.SetSize(2)));

            _menu.Items.Add(_miShow);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miLock);
            _menu.Items.Add(size);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("回到右下角", null, (s, e) => _w.GoHome()));
            _menu.Items.Add(new ToolStripMenuItem("打个盹", null, (s, e) => _w.NapNow()));
            _menu.Items.Add(new ToolStripMenuItem("跳一下", null, (s, e) => _w.Pose.TriggerJump()));
            _menu.Items.Add(new ToolStripMenuItem("状态…", null, (s, e) => _w.ShowBubble()));
            // 本地读屏幕文字（D 档）。原文不出本机 —— 它显示的是**本机识别器**读到的字。
            _menu.Items.Add(new ToolStripMenuItem("她看见了什么…", null, (s, e) => _w.ShowScreenRead()));
            _menu.Items.Add(new ToolStripMenuItem("和桌宠说话…", null, (s, e) => OpenChat()));
            _menu.Items.Add(new ToolStripMenuItem("让她说一句", null, (s, e) => _w.ForceSpeak()));
            _menu.Items.Add(new ToolStripMenuItem("余额…", null, (s, e) =>
                _w.Dispatcher.InvokeAsync(OpenBalanceSettings)));
            // ⚠ 一切**配置**收在这一个入口（说话／模型通道／读屏／产出／外观），
            //   见 SettingsWindow.cs 文件头三条纪律。
            _menu.Items.Add(new ToolStripMenuItem("设置…", null, (s, e) =>
                _w.Dispatcher.InvokeAsync(OpenSettings)));
            _menu.Items.Add(new ToolStripSeparator());
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
                    + "现在会用免费模板台词。右键托盘 →「设置…」→「模型通道」可接任何 OpenAI 兼容服务"
                    + "（DeepSeek／硅基流动／本地 ollama…）；只想用模板台词的话，把那一项关掉即可。";
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
            _miLock.Checked = Locked;
            // ⚠ 配置类勾选项（置顶／调光／自启／说话／读屏／模型）已并入「设置…」面板 ⇒ 这里不再同步它们。
            //   托盘只剩「显示/穿透/大小」这些高频动作 —— 每个入口都写一套 Sync，正是分叉的来源。
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
        {            // 桌宠通常置顶；设置窗若是普通窗口，会被宠物盖住右下角按钮。
            // ⚠⚠ 借走置顶这件事必须**成对**，而且归还时的值一律**取配置**（`PetWindow.ResumeTopmost`），
            //   绝不写回「借走前的值」—— 面板里的「保存」可能刚把配置改成新值，写回旧值会把它
            //   抹掉，而配置里明明写着 true（2026-09-25 真故障：她自认置顶、系统不认，
            //   实测 `GWL_EXSTYLE=0x08080080` 里没有 0x8）。详见 TopmostGuard 顶部注释。
            // ⚠ 归还**不能**靠 `using`：`panel.Show()` 是**非模态**的、立刻返回，
            //   那样刚打开就归还了，面板又会被她盖住 ⇒ 必须挂到 `Closed` 上。
            // ⚠ 借出凭据用**计数**而不是布尔：面板连开两次时，谁先关谁后关是不确定的，
            //   布尔会被先关的那个提前归还（旧写法就是这么把置顶永久弄丢的）。
            IDisposable hold = _w.SuspendTopmost();
            BalanceSettingsWindow panel = null;
            try
            {
                panel = new BalanceSettingsWindow(_w.ReloadBalanceSources);
                panel.Closed += (s, e) => hold.Dispose();
                panel.Show();
                panel.Activate();
            }
            catch
            {
                // ⚠ 窗没开成也必须归还：否则置顶永远回不来，现象是「她再也压不住窗口了」
                //   而看不出任何报错。归还凭据是幂等的（内部置空后重复 Dispose 无副作用）。
                hold.Dispose();
                throw;
            }
        }

        /// <summary>设置主面板 —— 一切配置的唯一入口（说话／模型通道／读屏／产出／外观）。</summary>
        private void OpenSettings()
        {
            // 桌宠通常置顶；设置窗若是普通窗口会被宠物盖住右下角按钮（同余额窗的处理）。
            // ⚠ 模态窗（`ShowDialog()` 阻塞到关闭）才可以用 `using` —— 它确实覆盖了整段"面板开着"。
            // ⚠ `SettingsWindow.Save()` 会调 `_w.ApplyConfig()`，而那一下在借出期间**只记配置、
            //   不抢置顶**；真正拨回置顶发生在下面归还的那一刻，且取的是**配置**。
            //   （旧写法 `_w.Topmost = wasTopmost` 会把刚保存的新值当场抹掉 ⇒ 勾了「始终置顶」不生效。）
            using (_w.SuspendTopmost())
            using (var dlg = new SettingsWindow(_w))
                dlg.ShowDialog();
        }

        private void About()
        {            var m = _w.R; 
            MessageBox.Show(
                "阿助桌宠 · 自建轻量壳 v1\r\n\r\n"
                + "渲染：" + m.Stats + "\r\n"
                + "窗口：" + _w.ActualWidth.ToString("0") + "×" + _w.ActualHeight.ToString("0") + " DIP，DPI "
                + (_w.DipScale * 100).ToString("0") + "%\r\n"
                + "已渲染帧：" + _w.RenderedFrames + "，平均每帧 " + AvgMs().ToString("0.##") + " ms\r\n"
                // ⚠ 置顶那一格状态的**现场记录**：正常情况下它必须一直是 0。
                //   真值（扩展样式位）被外部抹掉时，自愈会补回并把次数 +1 —— 有数字就说明出过事。
                //   判「她有没有被压下去过」看这里，不要凭感觉（详见 TopmostGuard.cs）。
                + "置顶：" + (_w.Cfg.Topmost ? "开" : "关")
                + "，被抹掉后补回 " + _w.TopmostHeals + " 次\r\n\r\n"
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
