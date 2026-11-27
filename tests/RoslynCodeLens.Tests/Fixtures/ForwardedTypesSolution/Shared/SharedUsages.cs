using System;

namespace SharedSource
{
    // Compiled into Net10App and Net48Lib from this one file, so both compilations bind this location to
    // their own framework instance of AppDomain.GetCurrentThreadId(), each carrying a different [Obsolete] message.
    public static class SharedUsages
    {
        public static int ThreadId() { return AppDomain.GetCurrentThreadId(); }
    }
}
