using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("VideoBatch HTTP Test")]
[assembly: AssemblyDescription("Изолированный одноканальный тест загрузки YouTube через Dolphin")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]

namespace VideoBatch.HttpTest {
    static class HttpTestProgram {
        [STAThread]
        static void Main() {
            bool first;
            using(var mutex=new Mutex(true,"Local\\VideoBatchDesktop.HttpTest.SingleInstance",out first)) {
                if(!first) {
                    MessageBox.Show("Тестовый загрузчик уже открыт.","VideoBatch HTTP Test",MessageBoxButtons.OK,MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { Application.Run(new HttpTestForm()); }
                catch(Exception error) {
                    try {
                        Directory.CreateDirectory(HttpTestPaths.TestRoot);
                        File.AppendAllText(HttpTestPaths.ErrorLog,DateTime.Now.ToString("O")+Environment.NewLine+error+Environment.NewLine);
                    } catch {}
                    MessageBox.Show(error.Message,"VideoBatch HTTP Test",MessageBoxButtons.OK,MessageBoxIcon.Error);
                }
            }
        }
    }
}
