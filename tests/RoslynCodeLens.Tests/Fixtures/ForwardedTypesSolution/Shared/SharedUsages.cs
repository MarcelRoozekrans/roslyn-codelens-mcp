using System;

namespace SharedSource
{
    // Compiled into Net10App and Net48Lib from this one file, so both compilations bind this location to
    // their own framework instance of AppDomain.GetCurrentThreadId(), each carrying a different [Obsolete] message.
    public static class SharedUsages
    {
        public static int ThreadId() { return AppDomain.GetCurrentThreadId(); }

        // Displayed as Handle(nint) in Net10App, whose runtime has a numeric IntPtr, and as Handle(System.IntPtr) in Net48Lib.
        public static bool Handle(IntPtr handle) { return handle != IntPtr.Zero; }
    }
}
