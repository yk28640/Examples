using System;
using System.ServiceProcess;
using Microsoft.Owin.Hosting;

namespace WindowsServiceV14
{
    public partial class Service1 : ServiceBase
    {
        private IDisposable _webApp;

        internal string ListenUrl { get; private set; }

        public Service1()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            var listenUrl = TagProjectConfiguration.LoadListenUrl();
            _webApp = WebApp.Start<Startup>(listenUrl);
            ListenUrl = listenUrl;
        }

        protected override void OnStop()
        {
            _webApp?.Dispose();
            _webApp = null;
        }

#if DEBUG
        internal void StartInteractive()
        {
            OnStart(new string[0]);
        }

        internal void StopInteractive()
        {
            OnStop();
        }
#endif
    }
}
