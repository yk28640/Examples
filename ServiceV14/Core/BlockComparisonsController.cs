using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web.Http;

namespace ServiceV14
{
    /// <summary>提供异步程序块比较任务的创建、状态查询及 PDF 报告读取接口。</summary>
    public sealed class BlockComparisonsController : ApiController
    {
        /// <summary>仅接收历史结果中的导出标识，不接受客户端指定 XML 路径。</summary>
        public sealed class CreateRequest
        {
            public string ExportId { get; set; }
        }

        /// <summary>校验已发布导出后创建或复用任务，以 HTTP 202 返回任务快照。</summary>
        [HttpPost]
        [Route("api/block-comparisons")]
        public IHttpActionResult Create(CreateRequest request)
        {
            if (!ModelState.IsValid || request == null || !BlockExportStore.IsId(request.ExportId))
                return BadRequest("必须提供有效的导出标识 ExportId。");
            try
            {
                // 失效导出只返回冲突，不在任务请求内重新打开工程或导出 XML。
                var manifest = MainController.GetPublishedExport(request.ExportId);
                if (manifest == null)
                    return Content(HttpStatusCode.Conflict, "工程版本或 XML 已失效。工程变更请刷新历史；XML 缺失需管理员恢复文件或显式重建缓存，普通刷新不会重新导出。");
                return Content(HttpStatusCode.Accepted, BlockComparisonService.Current.Create(manifest));
            }
            catch (ComparisonCapacityException ex) { return Content((HttpStatusCode)429, ex.Message); }
            catch (InvalidOperationException ex) { return Content(HttpStatusCode.Conflict, ex.Message); }
            catch (Exception ex) { return Unavailable(ex); }
        }

        /// <summary>查询任务快照；任务不存在或过期时返回 HTTP 404。</summary>
        [HttpGet]
        [Route("api/block-comparisons/{taskId}")]
        public IHttpActionResult Status(string taskId)
        {
            if (!BlockExportStore.IsId(taskId)) return BadRequest("任务标识无效。");
            try
            {
                var task = BlockComparisonService.Current.Get(taskId);
                if (task == null) return Content(HttpStatusCode.NotFound, "任务不存在或已过期，请重新比较。");
                return Ok(task);
            }
            catch (Exception ex) { return Unavailable(ex); }
        }

        /// <summary>以浏览器内联方式返回有效 PDF；不可用时返回 HTTP 404。</summary>
        [HttpGet]
        [Route("api/block-comparisons/{taskId}/report")]
        public IHttpActionResult Report(string taskId)
        {
            if (!BlockExportStore.IsId(taskId)) return BadRequest("任务标识无效。");
            try
            {
                var stream = BlockComparisonService.Current.OpenReport(taskId);
                if (stream == null) return Content(HttpStatusCode.NotFound, "报告尚未生成、已过期或被移除，请重新比较。");
                // 将文件流所有权交给 StreamContent，在响应释放时一并关闭。
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream)
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                response.Content.Headers.ContentLength = stream.Length;
                response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
                {
                    FileName = "comparison-" + taskId + ".pdf"
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                response.Headers.TryAddWithoutValidation("X-Content-Type-Options", "nosniff");
                return ResponseMessage(response);
            }
            catch (Exception ex) { return Unavailable(ex); }
        }

        /// <summary>服务端记录完整异常，对客户端统一返回不含内部细节的错误提示。</summary>
        private IHttpActionResult Unavailable(Exception exception)
        {
            System.Diagnostics.Trace.TraceError("程序块比较 API 失败: {0}", exception);
            return Content(HttpStatusCode.ServiceUnavailable, "比较服务不可用，请检查配置和服务端日志。");
        }
    }
}
