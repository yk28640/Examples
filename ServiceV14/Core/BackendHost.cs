using System;
using System.Threading;
using Microsoft.Owin.Hosting;

namespace ServiceV14
{
    /// <summary>托盘程序自己的 HTTP 宿主；启动时不执行工程操作。</summary>
    public sealed class BackendHost : IDisposable
    {
        private readonly SatHostProcess satHost = new SatHostProcess();
        private IDisposable webApp;

        public string ListenUrl { get; }

        public BackendHost()
        {
            ListenUrl = TagProjectConfiguration.LoadListenUrl();
            webApp = WebApp.Start(ListenUrl, new Startup(satHost).Configuration);
            satHost.StartInBackground();
        }

        /// <summary>释放监听并触发 Startup 注册的比较任务停止回调。</summary>
        public void Dispose()
        {
            try
            {
                Interlocked.Exchange(ref webApp, null)?.Dispose();
            }
            finally
            {
                satHost.Dispose();
            }
        }
    }
}
