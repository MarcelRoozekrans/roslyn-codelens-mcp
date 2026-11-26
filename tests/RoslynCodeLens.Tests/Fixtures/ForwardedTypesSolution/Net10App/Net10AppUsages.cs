using System;

namespace Net10App
{
    public sealed class Net10AppResource : IDisposable
    {
        public void Dispose() { }
    }

    public static class Net10AppUsages
    {
        [Obsolete("Use something else.")]
        public static void Legacy() { }

        public static void Write() { Console.WriteLine("Net10App"); }

        public static AppDomain Domain() { return AppDomain.CurrentDomain; }

        public static void Subscribe() { AppDomain.CurrentDomain.ProcessExit += OnExit; }

        public static void Fail() { throw new InvalidOperationException("Net10App"); }

        public static void Guard()
        {
            try { Fail(); }
            catch (InvalidOperationException) { }
        }

        public static int WordCount(this string text) { return text.Split(' ').Length; }

        private static void OnExit(object sender, EventArgs e) { }
    }
}