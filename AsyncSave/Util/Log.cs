using BepInEx.Logging;

namespace AsyncSave.Util
{
    internal static class Log
    {
        internal static ManualLogSource Source;

        public static void Info(string msg) => Source?.LogInfo(msg);
        public static void Warn(string msg) => Source?.LogWarning(msg);
        public static void Error(string msg) => Source?.LogError(msg);
        public static void Debug(string msg) => Source?.LogDebug(msg);
    }
}
