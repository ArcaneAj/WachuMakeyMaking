namespace WachuMakeyMaking.Utils
{
    public static class Logging
    {
        public static void Log(this string val)
        {
            Plugin.Log.Info(val);
        }
        public static void Log(this int val)
        {
            Plugin.Log.Info($"{val}");
        }
        public static void Log(this uint val)
        {
            Plugin.Log.Info($"{val}");
        }
        public static void Log(this float val)
        {
            Plugin.Log.Info($"{val}");
        }
        public static void Log(this bool val)
        {
            Plugin.Log.Info($"{val}");
        }
    }
}

//  System.Reflection.MethodBase.GetCurrentMethod()?.Name.Log();
