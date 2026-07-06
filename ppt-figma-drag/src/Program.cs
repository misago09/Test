using System;
using System.Threading;
using System.Windows.Forms;

namespace PptFigmaDrag
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\PptFigmaDrag.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("PPT Figma Drag가 이미 실행 중입니다.\n트레이(작업 표시줄 오른쪽 아래) 아이콘을 확인하세요.",
                        "PPT Figma Drag", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Event log is ALWAYS on (fresh file per session): every debugging
                // round so far was slowed by "was logging even enabled / which
                // build ran?" - this removes both questions.
                DiagLog.Enabled = true;
                DiagLog.Log("APP", "start");

                // A silent crash looks like "nothing happened"; always say something.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                {
                    MessageBox.Show("오류가 발생했습니다:\r\n" + e.Exception, "PPT Figma Drag",
                        MessageBoxButtons.OK, MessageBoxIcon.Error,
                        MessageBoxDefaultButton.Button1, MessageBoxOptions.ServiceNotification);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    MessageBox.Show("치명적 오류로 종료됩니다:\r\n" + e.ExceptionObject, "PPT Figma Drag",
                        MessageBoxButtons.OK, MessageBoxIcon.Error,
                        MessageBoxDefaultButton.Button1, MessageBoxOptions.ServiceNotification);
                };

                Application.Run(new TrayContext());
                GC.KeepAlive(mutex);
            }
        }
    }
}
