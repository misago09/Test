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
                Application.Run(new TrayContext());
                GC.KeepAlive(mutex);
            }
        }
    }
}
