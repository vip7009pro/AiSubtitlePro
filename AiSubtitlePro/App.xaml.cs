using System.Configuration;
using System.Data;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace AiSubtitlePro
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            var nativeDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Native", "win-x64");
            if (Directory.Exists(nativeDir))
            {
                SetDllDirectory(nativeDir);
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!currentPath.StartsWith(nativeDir + ";", StringComparison.OrdinalIgnoreCase))
                {
                    Environment.SetEnvironmentVariable("PATH", nativeDir + ";" + currentPath);
                }
            }

            base.OnStartup(e);
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }

}
