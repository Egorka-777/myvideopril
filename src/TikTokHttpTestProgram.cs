using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("VideoBatch TikTok HTTP Test")]
[assembly: System.Reflection.AssemblyVersion("0.1.0.0")]

namespace VideoBatch.TikTokHttpTest {
    static class TikTokHttpTestProgram {
        [STAThread]
        static void Main() {
            bool first;
            using (var mutex = new Mutex(true, "Local\\VideoBatchDesktop.TikTokHttpTest.SingleInstance", out first)) {
                if (!first) {
                    MessageBox.Show("TikTok HTTP тест уже открыт.", "VideoBatch TikTok HTTP Test", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new TikTokHttpTestForm()); }
                catch (Exception error) {
                    try {
                        Directory.CreateDirectory(TikTokHttpTestPaths.TestRoot);
                        File.AppendAllText(TikTokHttpTestPaths.ErrorLog, DateTime.Now.ToString("O") + Environment.NewLine + error + Environment.NewLine);
                    } catch { }
                    MessageBox.Show(error.Message, "VideoBatch TikTok HTTP Test", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
