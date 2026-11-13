using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace PontishLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool isCs = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("cs", StringComparison.OrdinalIgnoreCase);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string scriptPath = Path.Combine(baseDir, "pontish.ps1");

            if (!File.Exists(scriptPath))
            {
                MessageBox.Show(
                    (isCs ? "Soubor pontish.ps1 nebyl nalezen ve slozce:\n" : "File pontish.ps1 was not found in:\n") + baseDir,
                    "Pontish",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                return;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + scriptPath + "\"",
                    WorkingDirectory = baseDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    (isCs ? "Nepodarilo se spustit Pontish:\n" : "Failed to start Pontish:\n") + ex.Message,
                    "Pontish",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
    }
}
