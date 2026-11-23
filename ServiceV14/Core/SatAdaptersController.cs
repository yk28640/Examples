using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;

namespace ServiceV14
{
    public sealed class SatAdaptersController : ApiController
    {
        [HttpGet]
        [Route("api/sat/adapters")]
        public Task<HttpResponseMessage> Get(CancellationToken cancellationToken)
        {
            if (Configuration.Properties.TryGetValue(SatHostProcess.ConfigurationKey, out var value)
                && value is SatHostProcess host)
                return host.GetAdaptersAsync(cancellationToken);

            return Task.FromResult(SatHostProcess.Error(HttpStatusCode.ServiceUnavailable, "SAT 宿主不可用。"));
        }

        [HttpPost]
        [Route("api/sat/scan")]
        public Task<HttpResponseMessage> Scan(CancellationToken cancellationToken)
        {
            string adapter;
            try
            {
                adapter = TagProjectConfiguration.LoadSatScanAdapter();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
            {
                Trace.TraceWarning("无法读取 SAT 扫描配置：{0}", ex.Message);
                return Task.FromResult(SatHostProcess.Error(HttpStatusCode.Conflict, "请检查部署目录的 tagsconfig.json。"));
            }

            if (string.IsNullOrWhiteSpace(adapter))
                return Task.FromResult(SatHostProcess.Error(HttpStatusCode.Conflict,
                    "请通过 GET /api/sat/adapters 查询网卡，并配置 tagsconfig.json 中的 sat.scanAdapter。"));

            if (Configuration.Properties.TryGetValue(SatHostProcess.ConfigurationKey, out var value)
                && value is SatHostProcess host)
                return host.ScanAsync(adapter, cancellationToken);

            return Task.FromResult(SatHostProcess.Error(HttpStatusCode.ServiceUnavailable, "SAT 宿主不可用。"));
        }
    }
}
