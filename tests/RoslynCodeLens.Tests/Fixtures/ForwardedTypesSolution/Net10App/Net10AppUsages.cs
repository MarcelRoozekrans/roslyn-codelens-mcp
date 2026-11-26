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

        // String.Contains(string, StringComparison) does not exist on net48 and netstandard2.0.
        public static bool Mentions(string text) { return text.Contains("Net10App", StringComparison.Ordinal); }

        // String.Copy(string) is [Obsolete] on net10.0, but not on net48 and netstandard2.0.
        public static string Clone(string text) { return string.Copy(text); }

        // SHA1Managed is [Obsolete] on net10.0, but not on net48.
        public static object Hasher() { return new System.Security.Cryptography.SHA1Managed(); }

        private static void OnExit(object sender, EventArgs e) { }
    }
}