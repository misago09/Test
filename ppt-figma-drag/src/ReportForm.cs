using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PptFigmaDrag
{
    // Selectable always-on-top report window. A MessageBox can't have its text
    // selected (and with ServiceNotification even Ctrl+C copy doesn't work), and
    // one-shot Clipboard writes fail transiently when another process holds the
    // clipboard - this form gives the user three reliable ways to get the text.
    internal sealed class ReportForm : Form
    {
        private readonly TextBox _text;
        private readonly Button _copy;

        public ReportForm(string report, string savedPath)
        {
            Text = "PPT Figma Drag 진단 결과";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(780, 660);
            TopMost = true;
            MinimizeBox = false;

            _text = new TextBox();
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.ScrollBars = ScrollBars.Both;
            _text.WordWrap = false;
            _text.Dock = DockStyle.Fill;
            _text.Font = new Font(FontFamily.GenericMonospace, 9f);
            _text.Text = report;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Height = 42;
            buttons.Padding = new Padding(6);

            Button close = new Button();
            close.Text = "닫기";
            close.Width = 80;
            close.Click += OnClose;

            _copy = new Button();
            _copy.Text = "전체 복사";
            _copy.Width = 150;
            _copy.Click += OnCopy;

            Button openDir = new Button();
            openDir.Text = "저장 폴더 열기";
            openDir.Width = 120;
            openDir.Tag = savedPath;
            openDir.Click += OnOpenDir;

            buttons.Controls.Add(close);
            buttons.Controls.Add(_copy);
            buttons.Controls.Add(openDir);

            Controls.Add(_text);
            Controls.Add(buttons);

            Shown += OnShown;
        }

        private void OnShown(object sender, EventArgs e)
        {
            _text.SelectionStart = 0;
            _text.SelectionLength = 0;
            if (TryCopy(_text.Text))
                _copy.Text = "복사됨 ✓ (붙여넣기 하세요)";
        }

        private void OnCopy(object sender, EventArgs e)
        {
            _copy.Text = TryCopy(_text.Text) ? "복사됨 ✓ (붙여넣기 하세요)" : "실패 - 텍스트를 직접 드래그하세요";
        }

        private void OnClose(object sender, EventArgs e)
        {
            Close();
        }

        private void OnOpenDir(object sender, EventArgs e)
        {
            try
            {
                string path = (string)((Button)sender).Tag;
                if (!string.IsNullOrEmpty(path))
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch
            {
            }
        }

        private static bool TryCopy(string text)
        {
            try
            {
                // Built-in retry: the clipboard is frequently locked for a moment
                // by clipboard managers or the app we just automated.
                Clipboard.SetDataObject(text, true, 10, 120);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
