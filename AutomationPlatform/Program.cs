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
                .ValidateOnStart();
            builder.Services.AddSingleton<DiscoveryScanService>();
            builder.Services.AddSingleton<OpennessOperationService>();

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

            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
