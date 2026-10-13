using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AutomationPlatform;

public static class TagSourceApi
{
    public const string HttpClientName = "TagSources";

    public static RouteGroupBuilder MapTagSourceApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/tag-sources");
        group.MapGet("", (IOptions<TagSourceOptions> options) =>
            Results.Ok(options.Value.Sources.Where(source => source.Enabled)
                .Select(source => new TagSourceSummary(source.Id, source.Name))));
        group.MapGet("/{sourceId}/tags", ReadTagsAsync);
        return group;
    }

    private static async Task<IResult> ReadTagsAsync(string sourceId, IOptions<TagSourceOptions> options,
        IHttpClientFactory clients, ILogger<TagSourceOptions> logger, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var source = settings.Sources.Find(source => source.Enabled &&
            string.Equals(source.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (source is null)
        {
            logger.LogWarning("PLC 变量来源不存在或已禁用：{SourceId}", sourceId);
            return Results.Problem("设备来源不存在或已禁用，请重新加载来源列表。", statusCode: 404);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        try
        {
            using var client = clients.CreateClient(HttpClientName);
            var address = new Uri(new Uri(source.BaseUrl.TrimEnd('/') + "/"), "getTags");
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("读取 PLC 变量来源 {SourceId} 失败，上游 HTTP {StatusCode}",
                    source.Id, (int)response.StatusCode);
                return Results.Problem($"设备来源“{source.Name}”返回 HTTP {(int)response.StatusCode}。",
                    statusCode: 502);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var tags = await JsonSerializer.DeserializeAsync<List<PlcTag>>(stream,
                cancellationToken: timeout.Token);
            if (tags is null || tags.Any(tag => tag is null ||
                tag.Device is null || tag.Group is null || tag.Table is null ||
                tag.Name is null || tag.DataType is null || tag.Address is null))
                throw new JsonException("变量数组及六个字符串字段不能为空值。");

            return Results.Ok(new TagSourceSnapshot(source.Id, source.Name, DateTimeOffset.UtcNow, tags));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("读取 PLC 变量来源 {SourceId} 超时（{TimeoutSeconds} 秒）",
                source.Id, settings.RequestTimeoutSeconds);
            return Results.Problem($"读取设备来源“{source.Name}”超时（{settings.RequestTimeoutSeconds} 秒）。",
                statusCode: 504);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "无法连接 PLC 变量来源 {SourceId}", source.Id);
            return Results.Problem($"无法连接设备来源“{source.Name}”，请检查网络及变量服务。",
                statusCode: 502);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "PLC 变量来源 {SourceId} 返回的数据格式异常", source.Id);
            return Results.Problem($"设备来源“{source.Name}”数据格式异常，预期返回包含 Device、Group、Table、Name、DataType、Address 字符串字段的变量数组。",
                statusCode: 502);
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "读取 PLC 变量来源 {SourceId} 的响应失败", source.Id);
            return Results.Problem($"读取设备来源“{source.Name}”的响应失败，请重试。", statusCode: 502);
        }
    }
}
