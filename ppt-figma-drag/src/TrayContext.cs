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
        private readonly MouseHook _hook;
        private readonly ToolStripMenuItem _enabledItem;
        private readonly ToolStripMenuItem _autoStartItem;
        private readonly Icon _iconOn;
        private readonly Icon _iconOff;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);

        public TrayContext()
        {
            _worker = new DragWorker();
            _hook = new MouseHook(_worker);
            _hook.Install();

            _iconOn = CreateIcon(true);
            _iconOff = CreateIcon(false);

            _enabledItem = new ToolStripMenuItem("사용 (드래그에 걸친 도형까지 선택)");
            _enabledItem.Checked = true;
            _enabledItem.CheckOnClick = true;
            _enabledItem.CheckedChanged += OnEnabledChanged;

            _autoStartItem = new ToolStripMenuItem("Windows 시작 시 자동 실행");
            _autoStartItem.Checked = IsAutoStartRegistered();
            _autoStartItem.CheckOnClick = true;
            _autoStartItem.CheckedChanged += OnAutoStartChanged;

            ToolStripMenuItem exitItem = new ToolStripMenuItem("종료");
            exitItem.Click += OnExit;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(_enabledItem);
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
            bool on = _enabledItem.Checked;
            _hook.Enabled = on;
            _notifyIcon.Icon = on ? _iconOn : _iconOff;
        }

        private void OnDoubleClick(object sender, EventArgs e)
        {
            _enabledItem.Checked = !_enabledItem.Checked;
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
            _hook.Dispose();
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
