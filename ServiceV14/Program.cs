using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace ServiceV14
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            try
            {
                using (var mutex = new Mutex(false, @"Local\ServiceV14.Tray"))
                {
                    bool acquired;
                    try { acquired = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                    {
                        MessageBox.Show("ServiceV14 已经运行，请在右下角通知区域查看图标。", "ServiceV14",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    try
                    {
                        using (var host = new BackendHost())
                        using (var context = new TrayApplicationContext(host))
                            Application.Run(context);
                    }
                    finally
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError("ServiceV14 运行失败: {0}", ex);
                MessageBox.Show("ServiceV14 启动或运行失败：\r\n" + ex.GetBaseException().Message
                    + "\r\n\r\n请检查程序目录中的 tagsconfig.json、HTTP URL ACL 权限及监听地址是否被原服务占用。",
                    "ServiceV14", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
