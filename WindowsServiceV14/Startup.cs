using System.Configuration;
using System.Web.Http;
using Microsoft.Owin.Cors;
using Owin;
using Swashbuckle.Application;

namespace WindowsServiceV14
{
    /// <summary>构建 OWIN 自托管管线，注册 API、可选文档及比较请求限制。</summary>
    public class Startup
    {
        /// <summary>配置宿主生命周期回调和中间件；此过程不打开 TIA 工程。</summary>
        public void Configuration(IAppBuilder appBuilder)
        {
            BlockComparisonService.StartHost();
            // 宿主释放时取消比较任务；StartHost 本身仅重置生命周期标志。
            object stopping;
            if (appBuilder.Properties.TryGetValue("host.OnAppDisposing", out stopping)
                && stopping is System.Threading.CancellationToken)
                ((System.Threading.CancellationToken)stopping).Register(BlockComparisonService.StopHost);
            var config = new HttpConfiguration();
            config.MapHttpAttributeRoutes();
            // 开关仅在启动时读取；缺失或无效值保持关闭，不单独暴露文档入口。
            bool swaggerEnabled;
            if (bool.TryParse(ConfigurationManager.AppSettings["SwaggerEnabled"], out swaggerEnabled)
                && swaggerEnabled)
            {
                config.EnableSwagger(options =>
                {
                    options.SingleApiVersion("v1", "WindowsServiceV14 API")
                        .Description("TIA Portal V14 SP1 服务接口。文档加载不执行工程操作；Try it out 会真实调用接口，读取标签、比较工程及生成历史可能耗时并产生文件，请谨慎执行。");
                    options.OperationFilter<SwaggerOperationFilter>();
                }).EnableSwaggerUi();
            }
            // 当前允许任意来源的浏览器跨域请求；CORS 不承担身份认证或网络访问控制。
            appBuilder.UseCors(CorsOptions.AllowAll);
            appBuilder.Use(async (context, next) =>
            {
                if (context.Request.Path.Value.StartsWith("/api/block-comparisons", System.StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Headers.Set("Cache-Control", "no-store");
                    // 在 Web API 反序列化前拒绝缺失长度或过大的比较任务请求。
                    long length;
                    if (context.Request.Method == "POST"
                        && (!long.TryParse(context.Request.Headers.Get("Content-Length"), out length)
                            || length < 0 || length > 1024))
                    {
                        context.Response.StatusCode = 413;
                        await context.Response.WriteAsync("比较请求必须是长度不超过 1024 字节的 JSON。");
                        return;
                    }
                }
                await next();
            });
            // Web API 置于自定义中间件之后，使请求限制能够先行生效。
            appBuilder.UseWebApi(config);
        }
    }
}
