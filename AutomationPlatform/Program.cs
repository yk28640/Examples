using System.Diagnostics.Eventing.Reader;
using AutomationPlatform.Components;
using MudBlazor.Services;

namespace AutomationPlatform
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add MudBlazor services
            builder.Services.AddMudServices();
            builder.Services.AddSingleton<EventLogService>();
            builder.Services.AddSingleton<ProjectDirectoryService>();
            builder.Services.AddOptions<DiscoveryOptions>()
                .Bind(builder.Configuration.GetSection(DiscoveryOptions.SectionName))
                .Validate(settings => settings.CompareTimeoutSeconds is > 0 and <= 86400 &&
                    settings.UploadTimeoutSeconds is > 0 and <= 86400,
                    "Discovery 操作超时必须为 1 到 86400 秒。")
                .Validate(settings => !string.IsNullOrWhiteSpace(settings.ResultDirectory),
                    "Discovery 结果目录不能为空。")
                .Validate(settings => !string.IsNullOrWhiteSpace(settings.ScanHistoryDatabasePath) &&
                    Path.IsPathFullyQualified(settings.ScanHistoryDatabasePath),
                    "Discovery 扫描历史数据库路径必须为绝对路径。")
                .Validate(settings => settings.ScanHistoryMaxMegabytes > 0,
                    "Discovery 扫描历史数据库大小限制必须大于零。")
                .ValidateOnStart();
            builder.Services.AddSingleton<DiscoveryHistoryService>();
            builder.Services.AddSingleton<DiscoveryScanService>();
            builder.Services.AddSingleton<OpennessOperationService>();
            builder.Services.AddCors(options => options.AddPolicy("ReadOnlyApi", policy =>
                policy.AllowAnyOrigin().WithMethods("GET").AllowAnyHeader()));

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }
            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

            app.UseRouting();
            app.UseCors();
            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapDiscoveryHistoryApi().RequireCors("ReadOnlyApi");
            app.MapGet("/api/event-logs/operational", async (EventLogService eventLogs,
                ILogger<Program> logger, CancellationToken cancellationToken) =>
            {
                try
                {
                    var entries = await Task.Run(() => eventLogs.ReadOperationLogs(100, cancellationToken),
                        cancellationToken);
                    return Results.Ok(entries);
                }
                catch (UnauthorizedAccessException exception)
                {
                    logger.LogWarning(exception, "读取 TIA Portal 操作日志权限不足");
                    return Results.Problem("没有读取 TIA Portal 操作日志的权限。", statusCode: 403);
                }
                catch (Exception exception) when (exception is EventLogException or PlatformNotSupportedException)
                {
                    logger.LogWarning(exception, "无法读取 TIA Portal 操作日志");
                    return Results.Problem("无法读取 TIA Portal 操作日志，请确认日志通道在本机可用。", statusCode: 503);
                }
            }).RequireCors("ReadOnlyApi");
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
