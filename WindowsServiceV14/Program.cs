using System;
using System.ServiceProcess;

namespace WindowsServiceV14
{
    internal static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// </summary>
        static void Main()
        {
#if DEBUG
            using (var service = new Service1())
            {
                service.StartInteractive();
                try
                {
                    Console.WriteLine($"服务已启动，访问 {service.ListenUrl}test。按 Enter 停止。");
                    System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
                }
                finally
                {
                    service.StopInteractive();
                }
            }
#else
            ServiceBase.Run(new Service1());
#endif
        }
    }
}
