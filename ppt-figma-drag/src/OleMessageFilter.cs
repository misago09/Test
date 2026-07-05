using System;
using System.Runtime.InteropServices;

namespace PptFigmaDrag
{
    [ComImport]
    [Guid("00000016-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig]
        int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

        [PreserveSig]
        int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

        [PreserveSig]
        int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    // PowerPoint's STA rejects incoming COM calls with SERVERCALL_RETRYLATER while
    // its UI thread runs modal loops - including the marquee mouse-tracking loop
    // this program rides on. Without a message filter those rejections surface as
    // RPC_E_CALL_REJECTED and silently abort the drag; with it the calls retry
    // for a bounded time.
    internal sealed class OleMessageFilter : IOleMessageFilter
    {
        private const int ServerCallIsHandled = 0;
        private const int ServerCallRetryLater = 2;
        private const int PendingMsgWaitDefProcess = 2;
        private const int RetryDelayMs = 150;
        private const int MaxRetriesPerBurst = 20; // ~3s worst case

        [DllImport("ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        [ThreadStatic]
        private static OleMessageFilter _registered;

        private int _retriesLeft = MaxRetriesPerBurst;

        // Must be called on the (STA) thread whose outgoing calls should retry.
        public static void Register()
        {
            OleMessageFilter filter = new OleMessageFilter();
            IOleMessageFilter previous;
            if (CoRegisterMessageFilter(filter, out previous) >= 0)
                _registered = filter;
        }

        // Call at the start of each unit of work so one slow burst cannot starve
        // the next one of its retry budget.
        public static void ResetRetryBudget()
        {
            OleMessageFilter filter = _registered;
            if (filter != null)
                filter._retriesLeft = MaxRetriesPerBurst;
        }

        int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo)
        {
            return ServerCallIsHandled;
        }

        int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            if (dwRejectType == ServerCallRetryLater && _retriesLeft > 0)
            {
                _retriesLeft--;
                return RetryDelayMs; // values >= 100 mean "retry after this many ms"
            }
            return -1; // give up; the caller sees RPC_E_CALL_REJECTED
        }

        int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType)
        {
            return PendingMsgWaitDefProcess;
        }
    }
}
