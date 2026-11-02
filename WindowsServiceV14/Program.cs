using System;
using System.ServiceProcess;

namespace WindowsServiceV14
{
    /// <summary>按编译配置选择控制台调试入口或 Windows 服务入口。</summary>
    internal static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        static void Main()
        {
#if DEBUG
            // Debug 直接调用服务启停，便于在 Visual Studio 中调试同一套托管逻辑。
            using (var service = new Service1())
            {
                service.StartInteractive();
                try
                {
                    Console.WriteLine($"服务已启动，访问 {service.ListenUrl}test。按 Enter 停止。");
                    // 当前实现通过无限休眠保持宿主运行，不在此读取键盘输入。
                    System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                }
                finally
                {
                    service.StopInteractive();
                }
            }
#else
            // Release 由 Windows 服务控制管理器调用 OnStart 和 OnStop。
            ServiceBase.Run(new Service1());
#endif
        }
    }
}
