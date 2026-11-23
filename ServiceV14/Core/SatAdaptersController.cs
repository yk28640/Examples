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
    }
}
