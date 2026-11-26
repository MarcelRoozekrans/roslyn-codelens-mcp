using System;

namespace NetStdLib
{
    public sealed class NetStdLibResource : IDisposable
    {
        public void Dispose() { }
    }

    public static class NetStdLibUsages
    {
        [Obsolete("Use something else.")]
        public static void Legacy() { }

        public static void Write() { Console.WriteLine("NetStdLib"); }

        public static AppDomain Domain() { return AppDomain.CurrentDomain; }

        public static void Subscribe() { AppDomain.CurrentDomain.ProcessExit += OnExit; }

        public static void Fail() { throw new InvalidOperationException("NetStdLib"); }

        public static void Guard()
        {
            try { Fail(); }
            catch (InvalidOperationException) { }
        }

        public static int WordCount(this string text) { return text.Split(' ').Length; }

        private static void OnExit(object sender, EventArgs e) { }
    }
}