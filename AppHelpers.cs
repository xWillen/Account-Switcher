using System;
using System.IO;

namespace WillenSwitcher
{
    public static class AppPaths
    {
        public static string LogFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
            "WillenSwitcher", 
            "error.log"
        );
    }

    public static class AppLog
    {
        public static void Write(Exception ex)
        {
            try
            {
                string dir = Path.GetDirectoryName(AppPaths.LogFile)!;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n-----------------------------------\n";
                File.AppendAllText(AppPaths.LogFile, logContent);
            }
            catch
            {
                
            }
        }
    }
}