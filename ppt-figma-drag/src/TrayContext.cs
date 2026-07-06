using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PptFigmaDrag
{
    internal sealed class TrayContext : ApplicationContext
    {
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string RunValueName = "PptFigmaDrag";

        private readonly NotifyIcon _notifyIcon;
        private readonly DragWorker _worker;
        private readonly ViewportMonitor _monitor;
        private readonly GestureEngine _engine;
        private readonly MouseHook _hook;
        private readonly ToolStripMenuItem _enabledItem;
        private readonly ToolStripMenuItem _panZoomItem;
        private readonly ToolStripMenuItem _logItem;
        private readonly ToolStripMenuItem _diagItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly Timer _diagTimer;
        private readonly Icon _iconOn;
        private readonly Icon _iconOff;
        private volatile bool _diagRunning;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        public TrayContext()
        {
            _worker = new DragWorker();
            _monitor = new ViewportMonitor();
            _engine = new GestureEngine(_monitor);
            _hook = new MouseHook(_worker, _engine);
            _hook.Install();

            _iconOn = CreateIcon(true);
            _iconOff = CreateIcon(false);

            _enabledItem = new ToolStripMenuItem("걸침 선택 (드래그에 닿은 도형까지 선택)");
            _enabledItem.Checked = true;
            _enabledItem.CheckOnClick = true;
            _enabledItem.CheckedChanged += OnEnabledChanged;

            _panZoomItem = new ToolStripMenuItem("피그마식 이동/확대 (가운데 드래그 · 휠 · Ctrl+휠)");
            _panZoomItem.Checked = true;
            _panZoomItem.CheckOnClick = true;
            _panZoomItem.CheckedChanged += OnPanZoomChanged;

            _logItem = new ToolStripMenuItem("뷰포트 진단 로그 기록");
            _logItem.Checked = false;
            _logItem.CheckOnClick = true;
            _logItem.CheckedChanged += OnLogChanged;

            _diagItem = new ToolStripMenuItem("🔍 진단 실행 (3초 후 커서 위치 검사)");
            _diagItem.Click += OnRunDiagnostics;

            _diagTimer = new Timer();
            _diagTimer.Interval = 3000;
            _diagTimer.Tick += OnDiagTimerTick;

            _autoStartItem = new ToolStripMenuItem("Windows 시작 시 자동 실행");
            _autoStartItem.Checked = IsAutoStartRegistered();
            _autoStartItem.CheckOnClick = true;
            _autoStartItem.CheckedChanged += OnAutoStartChanged;

            ToolStripMenuItem exitItem = new ToolStripMenuItem("종료");
            exitItem.Click += OnExit;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(_enabledItem);
            menu.Items.Add(_panZoomItem);
            menu.Items.Add(_logItem);
            menu.Items.Add(_diagItem);
            menu.Items.Add(_autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = _iconOn;
            _notifyIcon.Text = "PPT Figma Drag - 걸치기만 해도 선택";
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += OnDoubleClick;
            _notifyIcon.Visible = true;
        }

        private void OnEnabledChanged(object sender, EventArgs e)
        {
            _hook.Enabled = _enabledItem.Checked;
            if (!_enabledItem.Checked)
                _worker.PostReleaseCom(); // don't keep PowerPoint pinned while off
            UpdateIcon();
        }

        private void OnPanZoomChanged(object sender, EventArgs e)
        {
            _hook.PanZoomEnabled = _panZoomItem.Checked;
            UpdateIcon();
        }

        private void UpdateIcon()
        {
            bool anyOn = _enabledItem.Checked || _panZoomItem.Checked;
            _notifyIcon.Icon = anyOn ? _iconOn : _iconOff;
        }

        private void OnLogChanged(object sender, EventArgs e)
        {
            _monitor.LoggingEnabled = _logItem.Checked;
            if (_logItem.Checked)
            {
                _notifyIcon.BalloonTipTitle = "PPT Figma Drag";
                _notifyIcon.BalloonTipText = "뷰포트 로그: " + _monitor.LogPath;
                _notifyIcon.ShowBalloonTip(4000);
            }
        }

        private void OnDoubleClick(object sender, EventArgs e)
        {
            _enabledItem.Checked = !_enabledItem.Checked;
        }

        // Give the user 3 seconds to move the cursor onto the slide (clicking the
        // menu moves the pointer to the tray) before sampling the window there.
        private void OnRunDiagnostics(object sender, EventArgs e)
        {
            _notifyIcon.BalloonTipTitle = "PPT Figma Drag 진단";
            _notifyIcon.BalloonTipText = "3초 안에 마우스를 PowerPoint 슬라이드 위로 옮겨 두세요.";
            _notifyIcon.ShowBalloonTip(2500);
            _diagTimer.Stop();
            _diagTimer.Start();
        }

        // The measurement takes seconds and must NOT run on this thread: it is
        // the thread the WH_MOUSE_LL hook lives on, and a blocked hook thread
        // stutters the system pointer until Windows silently removes the hook.
        private void OnDiagTimerTick(object sender, EventArgs e)
        {
            _diagTimer.Stop();
            if (_diagRunning)
                return;
            _diagRunning = true;
            System.Threading.Thread worker = new System.Threading.Thread(RunDiagnosticsWorker);
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.IsBackground = true;
            worker.Name = "PptFigmaDrag.Diag";
            worker.Start();
        }

        private void RunDiagnosticsWorker()
        {
            string report;
            try
            {
                report = Diagnostics.RunWithTimeout(_engine, _hook, 45000);
            }
            catch (Exception ex)
            {
                report = "진단 중 오류: " + ex;
            }
            finally
            {
                _diagRunning = false;
            }

            // A selectable, always-on-top window instead of a MessageBox: its text
            // can be selected/copied directly, plus a retrying copy button and the
            // saved-report folder, so the result can always be shared.
            using (ReportForm form = new ReportForm(report, Diagnostics.ReportPath))
            {
                form.ShowDialog();
            }
        }

        private void OnAutoStartChanged(object sender, EventArgs e)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (_autoStartItem.Checked)
                        key.SetValue(RunValueName, "\"" + Application.ExecutablePath + "\"");
                    else if (key.GetValue(RunValueName) != null)
                        key.DeleteValue(RunValueName);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("자동 실행 설정을 변경하지 못했습니다.\n" + ex.Message,
                    "PPT Figma Drag", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static bool IsAutoStartRegistered()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    return key != null && key.GetValue(RunValueName) != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private void OnExit(object sender, EventArgs e)
        {
            _notifyIcon.Visible = false;
            _diagTimer.Stop();
            _diagTimer.Dispose();
            _hook.Dispose();
            _engine.Dispose();
            _monitor.Dispose();
            _worker.Dispose();
            ExitThread();
        }

        // Dashed marquee square with a filled block half in / half out of it -
        // drawn at runtime so the repo needs no binary icon asset.
        private static Icon CreateIcon(bool enabled)
        {
            Color color = enabled ? Color.FromArgb(0, 122, 255) : Color.FromArgb(140, 140, 140);
            using (Bitmap bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    using (Pen pen = new Pen(color, 1f))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        g.DrawRectangle(pen, 0, 0, 12, 12);
                    }
                    using (SolidBrush brush = new SolidBrush(color))
                    {
                        g.FillRectangle(brush, 8, 8, 7, 7);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }
    }
}
