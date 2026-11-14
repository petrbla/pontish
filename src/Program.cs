using System;
using System.IO;
using System.Windows.Forms;

namespace PontishApp
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LogCrash("ThreadException", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash("UnhandledException", e.ExceptionObject as Exception);

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                LogCrash("Main", ex);
            }
        }

        private static void LogCrash(string kind, Exception ex)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pontish");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:O}] {kind}:\n{ex}\n");
            }
            catch { }
        }
    }
}
