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

        private static void OnExit(object sender, EventArgs e) { }
    }
}