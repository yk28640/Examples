using System;
using System.ServiceProcess;
using Microsoft.Owin.Hosting;

namespace WindowsServiceV14
{
    /// <summary>将 OWIN HTTP 宿主的启动和释放绑定到 Windows 服务生命周期。</summary>
    public partial class Service1 : ServiceBase
    {
        private IDisposable _webApp;

        internal string ListenUrl { get; private set; }

        /// <summary>初始化服务组件；实际 HTTP 监听由 OnStart 启动。</summary>
        public Service1()
        {
            InitializeComponent();
        }

        /// <summary>读取服务端监听配置，并使用 Startup 构建 OWIN 管线。</summary>
        protected override void OnStart(string[] args)
        {
            var listenUrl = TagProjectConfiguration.LoadListenUrl();
            _webApp = WebApp.Start<Startup>(listenUrl);
            ListenUrl = listenUrl;
        }

        /// <summary>释放 HTTP 宿主，同时触发管线注册的比较任务停止回调。</summary>
        protected override void OnStop()
        {
            _webApp?.Dispose();
            _webApp = null;
        }

#if DEBUG
        /// <summary>供调试入口复用服务启动逻辑，不经过 Windows 服务控制管理器。</summary>
        internal void StartInteractive()
        {
            OnStart(new string[0]);
        }

        /// <summary>供调试入口复用服务停止逻辑。</summary>
        internal void StopInteractive()
        {
            OnStop();
        }
#endif
    }
}
