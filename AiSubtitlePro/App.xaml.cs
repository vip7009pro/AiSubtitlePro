using System.Configuration;
using System.Data;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace AiSubtitlePro
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static readonly object _logLock = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            RegisterGlobalExceptionHandlers();

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

        private static void RegisterGlobalExceptionHandlers()
        {
            try
            {
                AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                {
                    try
                    {
                        WriteCrashLog("AppDomain.CurrentDomain.UnhandledException", args.ExceptionObject as Exception);
                    }
                    catch { }
                };

                TaskScheduler.UnobservedTaskException += (_, args) =>
                {
                    try
                    {
                        WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);
                    }
                    catch { }
                };

                if (Current != null)
                {
                    Current.DispatcherUnhandledException += (_, args) =>
                    {
                        try
                        {
                            WriteCrashLog("Application.DispatcherUnhandledException", args.Exception);
                        }
                        catch { }

                        // Let the app crash normally after logging (safer than trying to continue in unknown state)
                        args.Handled = false;
                    };
                }

                AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                {
                    try
                    {
                        WriteCrashLog("ProcessExit", null);
                    }
                    catch { }
                };
            }
            catch
            {
            }
        }

        private static void WriteCrashLog(string source, Exception? ex)
        {
            lock (_logLock)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AiSubtitlePro",
                    "logs");
                Directory.CreateDirectory(dir);

                var path = Path.Combine(dir, "crash.log");
                using var sw = new StreamWriter(path, append: true);
                sw.WriteLine("====================================================");
                sw.WriteLine($"UTC: {DateTime.UtcNow:O}");
                sw.WriteLine($"Source: {source}");
                sw.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
                sw.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");

                if (ex != null)
                {
                    sw.WriteLine("Exception:");
                    sw.WriteLine(ex.ToString());
                }
            }
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);
    }

}
