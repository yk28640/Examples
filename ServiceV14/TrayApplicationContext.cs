using System;
using System.Configuration;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace ServiceV14
{
    /// <summary>不创建主窗口，通过通知区域菜单管理后台宿主。</summary>
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly BackendHost host;
        private bool disposed;

        internal TrayApplicationContext(BackendHost host)
        {
            this.host = host;
            try
            {
                menu.Items.Add(new ToolStripMenuItem("状态：正在运行") { Enabled = false });
                menu.Items.Add(new ToolStripMenuItem(host.ListenUrl) { Enabled = false });
                menu.Items.Add(new ToolStripSeparator());
                bool swaggerEnabled;
                var documentation = new ToolStripMenuItem("打开 API 文档", null,
                    (sender, args) => Open(host.ListenUrl + "swagger"));
                documentation.Enabled = bool.TryParse(ConfigurationManager.AppSettings["SwaggerEnabled"], out swaggerEnabled)
                    && swaggerEnabled;
                menu.Items.Add(documentation);
                menu.Items.Add("打开配置目录", null, (sender, args) => Open(AppDomain.CurrentDomain.BaseDirectory));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出", null, (sender, args) => ExitThread());
                trayIcon.Icon = SystemIcons.Application;
                trayIcon.Text = "ServiceV14 - 正在运行";
                trayIcon.ContextMenuStrip = menu;
                trayIcon.DoubleClick += ShowStatus;
                trayIcon.Visible = true;
                trayIcon.ShowBalloonTip(3000, "ServiceV14 已启动", host.ListenUrl, ToolTipIcon.Info);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void ShowStatus(object sender, EventArgs args)
        {
            MessageBox.Show("后台正在运行。\r\n监听地址：" + host.ListenUrl
                + "\r\n右键托盘图标可打开文档、配置目录或退出。", "ServiceV14",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Open(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Trace.TraceError("ServiceV14 打开目标失败: {0}", ex);
                MessageBox.Show("无法打开：" + target + "\r\n" + ex.Message, "ServiceV14",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void ExitThreadCore()
        {
            trayIcon.Visible = false;
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                trayIcon.Visible = false;
                trayIcon.Dispose();
                menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
