namespace WinMeters
{
    internal static class Log
    {
        public static void D(string message) => System.Diagnostics.Debug.WriteLine(message);
        public static void E(string message) => System.Diagnostics.Debug.WriteLine(message);
    }
}