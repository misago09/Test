using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PptFigmaDrag
{
    // One-shot self-diagnosis, shown to the user as a copyable message box. Its
    // whole purpose is to reveal, on the user's real machine, which of the two
    // unverified assumptions behind pan/zoom is failing:
    //   1. that the slide canvas window class is "mdiClass", and
    //   2. that PowerPoint's edit canvas reacts to injected two-finger touch.
    internal static class Diagnostics
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT p);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private const int SM_REMOTESESSION = 0x1000;
        private const uint GA_ROOT = 2;
        private const string PptFrameClass = "PPTFrameClass";
        private const string SlideCanvasClass = "mdiClass";

        private static string ClassOf(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return "(null)";
            StringBuilder sb = new StringBuilder(256);
            if (GetClassName(hwnd, sb, sb.Capacity) == 0)
                return "(unknown)";
            return sb.ToString();
        }

        private static string ErrorName(int err)
        {
            switch (err)
            {
                case 0: return "(성공/미기록)";
                case 5: return "ERROR_ACCESS_DENIED";
                case 50: return "ERROR_NOT_SUPPORTED";
                case 87: return "ERROR_INVALID_PARAMETER";
                case 1359: return "ERROR_INTERNAL_ERROR";
                case 0x578: return "ERROR_INVALID_WINDOW_HANDLE";
                default: return "";
            }
        }

        private static string ExplainError(int err, bool remote)
        {
            switch (err)
            {
                case 5:
                    return "권한 문제입니다. PowerPoint가 '관리자 권한'으로 실행 중이면,\r\n" +
                           "   PptFigmaDrag.exe도 우클릭 → 관리자 권한으로 실행하세요 (둘의 권한을 맞춰야 함).";
                case 50:
                    return remote
                        ? "이 RDP/원격 세션은 터치 주입을 지원하지 않습니다.\r\n" +
                          "   → 터치 대신 COM 기반 방식으로 재구현이 필요합니다(알려주시면 진행)."
                        : "이 환경/SKU가 터치 주입을 지원하지 않습니다(가상머신·일부 정책).\r\n" +
                          "   → 터치 대신 COM 기반 방식으로 재구현이 필요합니다(알려주시면 진행).";
                case 87:
                    return "좌표 또는 주입 구조체 문제입니다. 개발자에게 이 코드를 알려주시면\r\n" +
                           "   해당 부분을 정확히 수정하겠습니다.";
                default:
                    return remote
                        ? "원격 세션일 가능성이 큽니다. 이 코드를 개발자에게 알려주세요."
                        : "이 코드를 개발자에게 알려주시면 원인을 특정해 수정하겠습니다.";
            }
        }

        public static string Run(GestureEngine engine)
        {
            StringBuilder r = new StringBuilder();
            r.Append("=== PPT Figma Drag 진단 ===\r\n");
            r.Append("OS 버전: ").Append(Environment.OSVersion.Version.ToString())
             .Append(Environment.Is64BitProcess ? "  (64-bit 프로세스)" : "  (32-bit 프로세스)")
             .Append("\r\n");
            bool remote = GetSystemMetrics(SM_REMOTESESSION) != 0;
            r.Append("실행 환경: ")
             .Append(remote ? "원격 데스크톱/RDP 세션 ⚠ (터치 주입이 막히는 대표 환경)" : "로컬 콘솔")
             .Append("\r\n");
            r.Append("터치 주입 초기화(Ready): ")
             .Append(engine.Ready ? "성공 ✓" : "실패 ✗  ← 이 PC/세션에서 InjectTouchInput 사용 불가")
             .Append("\r\n\r\n");

            POINT p;
            GetCursorPos(out p);
            r.Append("마우스 위치: (").Append(p.X).Append(", ").Append(p.Y).Append(")\r\n");

            IntPtr leaf = WindowFromPoint(p);
            IntPtr root = GetAncestor(leaf, GA_ROOT);
            string rootClass = ClassOf(root);
            bool rootIsPpt = string.Equals(rootClass, PptFrameClass, StringComparison.OrdinalIgnoreCase);

            r.Append("커서 아래 창 클래스 계층 (아래→위):\r\n");
            IntPtr cur = leaf;
            bool foundCanvas = false;
            for (int i = 0; i < 12 && cur != IntPtr.Zero; i++)
            {
                string cn = ClassOf(cur);
                bool isCanvas = string.Equals(cn, SlideCanvasClass, StringComparison.OrdinalIgnoreCase);
                if (isCanvas)
                    foundCanvas = true;
                r.Append("   ").Append(i == 0 ? "[커서] " : "  ↑   ").Append(cn);
                if (isCanvas)
                    r.Append("   ← 캔버스로 인식");
                r.Append("\r\n");
                cur = GetParent(cur);
            }
            r.Append("최상위 창: ").Append(rootClass)
             .Append(rootIsPpt ? "  ✓ PowerPoint" : "  ✗ PowerPoint 아님").Append("\r\n\r\n");

            bool canvasDetected = foundCanvas && rootIsPpt;
            r.Append("→ 이 위치에서 팬/줌 동작 조건(캔버스 인식): ")
             .Append(canvasDetected ? "충족 ✓" : "불충족 ✗").Append("\r\n");
            if (!canvasDetected)
            {
                if (!rootIsPpt)
                    r.Append("   마우스가 PowerPoint 편집창 위에 있지 않습니다.\r\n" +
                             "   슬라이드 중앙에 커서를 두고 다시 실행하세요.\r\n");
                else if (!foundCanvas)
                    r.Append("   PowerPoint는 맞지만 캔버스 클래스 'mdiClass'를 찾지 못했습니다.\r\n" +
                             "   → 위 계층에 보이는 실제 클래스명을 개발자에게 알려주세요.\r\n");
            }
            r.Append("\r\n");

            // Live injection test. Focus PowerPoint first so the synthetic touch
            // is routed to it rather than whatever else holds the foreground.
            if (rootIsPpt)
                SetForegroundWindow(root);
            int test = engine.RunSelfTest(p.X, p.Y);
            r.Append("실시간 터치 주입 테스트: ");
            if (test < 0)
                r.Append("건너뜀 (터치 주입 초기화 실패)\r\n");
            else if (test == 0)
            {
                int err = engine.LastInjectError;
                r.Append("InjectTouchInput 호출 실패 ✗   (GetLastError = ").Append(err)
                 .Append(" ").Append(ErrorName(err)).Append(")\r\n");
                r.Append("   ").Append(ExplainError(err, remote)).Append("\r\n");
            }
            else
                r.Append("InjectTouchInput 호출 성공 ✓\r\n" +
                         "   방금 슬라이드가 커서 기준으로 확대됐나요?\r\n" +
                         "   • 확대됨 → 주입은 정상. 남은 건 게이팅/좌표 문제입니다.\r\n" +
                         "   • 그대로 → PowerPoint가 합성 터치를 제스처로 받지 않습니다(핵심 원인).\r\n");

            r.Append("\r\n(이 창에서 Ctrl+C를 누르면 전체 내용이 복사됩니다.)");
            return r.ToString();
        }
    }
}
