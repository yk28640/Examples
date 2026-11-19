using System;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Security;
using System.Threading;
using System.Web.Http;

namespace ServiceV14
{
    public sealed class EventLogController : ApiController
    {
        [HttpGet]
        [Route("GetEventLog")]
        public IHttpActionResult GetEventLog(CancellationToken cancellationToken,
            int maximumEntries = EventLogService.DefaultEntries)
        {
            if (!ModelState.IsValid || maximumEntries < 1 || maximumEntries > EventLogService.MaximumEntries)
                return BadRequest("maximumEntries 必须为 1 到 5000 的整数。");

            try
            {
                return Json(new EventLogService().ReadOperationLogs(maximumEntries, cancellationToken));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException)
            {
                Trace.TraceWarning("读取 TIA Portal 操作日志权限不足: {0}", ex);
                return Content(HttpStatusCode.Forbidden, "没有读取 TIA Portal 操作日志的权限。");
            }
            catch (Exception ex) when (ex is EventLogException || ex is PlatformNotSupportedException)
            {
                Trace.TraceWarning("无法读取 TIA Portal 操作日志: {0}", ex);
                return Content(HttpStatusCode.ServiceUnavailable,
                    "无法读取 TIA Portal 操作日志，请确认日志通道在本机可用。");
            }
        }
    }
}
