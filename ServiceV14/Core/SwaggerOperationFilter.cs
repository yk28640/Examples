using System.Collections.Generic;
using System.Web.Http;
using System.Web.Http.Description;
using Swashbuckle.Swagger;

namespace ServiceV14
{
    /// <summary>补充 Web API 无法从通用响应类型推断出的文档模型，不执行任何业务接口。</summary>
    public sealed class SwaggerOperationFilter : IOperationFilter
    {
        /// <summary>按现有特性路由描述响应；增加或调整接口时需同步维护这里的元数据。</summary>
        public void Apply(Operation operation, SchemaRegistry registry, ApiDescription apiDescription)
        {
            var responses = new Dictionary<string, Response>();
            switch (apiDescription.RelativePath.Split('?')[0])
            {
                case "test":
                    operation.summary = "检查 HTTP 服务";
                    operation.produces = new List<string> { "text/plain" };
                    responses.Add("200", Reply("服务正常，返回 Helloworld。", new Schema { type = "string" }));
                    break;
                case "GetEventLog":
                    operation.summary = "读取本机 TIA Portal 操作日志";
                    operation.description = "读取 SiemensAG-Automation-TIAPortal/Operational，最新优先；不打开 TIA 工程。";
                    operation.produces = new List<string> { "application/json" };
                    if (operation.parameters != null)
                        foreach (var parameter in operation.parameters)
                            if (parameter.name == "maximumEntries")
                            {
                                parameter.description = "读取条数，1 到 5000，默认 100。";
                                parameter.@default = EventLogService.DefaultEntries;
                                parameter.minimum = 1;
                                parameter.maximum = EventLogService.MaximumEntries;
                            }
                    responses.Add("200", Reply("日志数组；无记录时为空数组。", registry.GetOrRegister(typeof(List<OperationLogEntry>))));
                    BadRequest(responses, registry, "maximumEntries 必须为 1 到 5000 的整数。");
                    responses.Add("403", Reply("没有读取本机 TIA Portal 操作日志的权限。"));
                    responses.Add("503", Reply("本机日志通道不存在或事件日志不可用。"));
                    break;
                case "getTags":
                    operation.summary = "读取 PLC 变量表标签";
                    operation.description = "首次请求打开配置工程读取标签，后续复用内存缓存；首次读取可能耗时。";
                    responses.Add("200", Reply("标签列表。", registry.GetOrRegister(typeof(List<MainController.TagInfo>))));
                    Unavailable(responses);
                    break;
                case "compareOfflinePlcs":
                    operation.summary = "比较最新的两个离线工程";
                    operation.description = "按配置目录中的修改时间选择两个 .ap14 工程。会打开工程进行比较，可能耗时并产生导出文件。";
                    // 此接口返回匿名对象，在文档层描述结构，避免为文档改动运行时契约。
                    responses.Add("200", Reply("工程信息及 PLC 比较结果。", new Schema
                    {
                        type = "object",
                        properties = new Dictionary<string, Schema>
                        {
                            { "LatestProject", new Schema { type = "string" } },
                            { "LatestModifiedUtc", new Schema { type = "string", format = "date-time" } },
                            { "PreviousProject", new Schema { type = "string" } },
                            { "PreviousModifiedUtc", new Schema { type = "string", format = "date-time" } },
                            { "Comparisons", registry.GetOrRegister(typeof(List<MainController.PlcComparison>)) }
                        }
                    }));
                    BadRequest(responses, registry, "比较目录或工程输入无效。");
                    responses.Add("409", Reply("PLC 无法可靠配对。"));
                    Unavailable(responses);
                    break;
                case "GenerateChangesHistory":
                    operation.summary = "获取工程更改历史";
                    operation.description = "复用 ChangesHistory.json 和内存缓存；需要生成的历史可能执行耗时的比较及导出。加载文档不会调用本接口。";
                    responses.Add("200", Reply("工程更改历史。", registry.GetOrRegister(typeof(MainController.ChangesHistoryDocument))));
                    BadRequest(responses, registry, "历史目录或输入无效。");
                    responses.Add("409", Reply("PLC 配对失败或生成期间工程发生变化。"));
                    Unavailable(responses);
                    break;
                case "api/block-comparisons":
                    operation.summary = "创建程序块比较任务";
                    operation.description = "请求体 ExportId 为历史结果中的有效导出标识（32 位十六进制）。必须有 Content-Length 且不超过 1024 字节。使用已发布的 XML 创建或复用任务，不隐式重新导出。";
                    operation.consumes = new List<string> { "application/json" };
                    responses.Add("202", Reply("已接受任务，使用 Id 查询状态。", registry.GetOrRegister(typeof(BlockComparisonTask))));
                    BadRequest(responses, registry, "缺少或无效的 ExportId。");
                    responses.Add("409", Reply("工程版本或 XML 失效，或服务正在停止。"));
                    responses.Add("413", Reply("缺少有效 Content-Length 或长度超过 1024 字节（纯文本响应）。"));
                    responses.Add("429", Reply("比较队列或报告存储已达上限。"));
                    Unavailable(responses);
                    break;
                case "api/block-comparisons/{taskId}":
                    operation.summary = "查询比较任务";
                    DescribeTaskId(operation);
                    responses.Add("200", Reply("任务状态：Queued、Running、Succeeded 或 Failed。", registry.GetOrRegister(typeof(BlockComparisonTask))));
                    BadRequest(responses, registry, "任务标识无效。");
                    responses.Add("404", Reply("任务不存在或已过期。"));
                    Unavailable(responses);
                    break;
                case "api/block-comparisons/{taskId}/report":
                    operation.summary = "获取比较 PDF 报告";
                    DescribeTaskId(operation);
                    // 成功时直接返回 PDF，错误时仍由 Web API 格式化为 JSON 或 XML。
                    operation.produces = new List<string> { "application/pdf", "application/json", "application/xml" };
                    responses.Add("200", Reply("PDF 文件，以 inline 方式返回，文件名为 comparison-{taskId}.pdf。", new Schema { type = "file" }));
                    BadRequest(responses, registry, "任务标识无效。");
                    responses.Add("404", Reply("报告尚未生成、已过期或被移除。"));
                    Unavailable(responses);
                    break;
                default:
                    return;
            }
            operation.responses = responses;
        }

        /// <summary>补充路由参数用途，保留 Web API 自动生成的参数类型与必填约束。</summary>
        private static void DescribeTaskId(Operation operation)
        {
            if (operation.parameters == null) return;
            foreach (var parameter in operation.parameters)
                if (parameter.name == "taskId")
                    parameter.description = "创建任务返回的 Id（32 位十六进制标识）。";
        }

        /// <summary>BadRequest 使用 HttpError 模型，与直接返回错误字符串的响应区分。</summary>
        private static void BadRequest(Dictionary<string, Response> responses, SchemaRegistry registry, string description)
        {
            responses.Add("400", Reply(description, registry.GetOrRegister(typeof(HttpError))));
        }

        /// <summary>为业务服务异常添加统一的 HTTP 503 文档响应。</summary>
        private static void Unavailable(Dictionary<string, Response> responses)
        {
            responses.Add("503", Reply("服务不可用，请检查配置和服务端日志。"));
        }

        /// <summary>构造响应元数据，未指定模型时按错误提示字符串描述。</summary>
        private static Response Reply(string description, Schema schema = null)
        {
            return new Response { description = description, schema = schema ?? new Schema { type = "string" } };
        }
    }
}
