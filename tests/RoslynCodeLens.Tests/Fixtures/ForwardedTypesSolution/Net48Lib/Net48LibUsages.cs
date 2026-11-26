using System;

namespace Net48Lib
{
    public sealed class Net48LibResource : IDisposable
    {
        public void Dispose() { }
    }

    public static class Net48LibUsages
    {
        [Obsolete("Use something else.")]
        public static void Legacy() { }

        public static void Write() { Console.WriteLine("Net48Lib"); }

        public static AppDomain Domain() { return AppDomain.CurrentDomain; }

        public static void Subscribe() { AppDomain.CurrentDomain.ProcessExit += OnExit; }

        public static void Fail() { throw new InvalidOperationException("Net48Lib"); }

        public static void Guard()
        {
            try { Fail(); }
            catch (InvalidOperationException) { }
        }

        public static int WordCount(this string text) { return text.Split(' ').Length; }

        // String.Copy(string) is not [Obsolete] on net48, unlike on net10.0.
        public static string Clone(string text) { return string.Copy(text); }

        // SHA1Managed is not [Obsolete] on net48, unlike on net10.0.
        public static object Hasher() { return new System.Security.Cryptography.SHA1Managed(); }

        // AppDomain.GetCurrentThreadId() is [Obsolete] on net48 and net10.0, with different messages.
        public static int ThreadId() { return AppDomain.GetCurrentThreadId(); }

        private static void OnExit(object sender, EventArgs e) { }
    }
}