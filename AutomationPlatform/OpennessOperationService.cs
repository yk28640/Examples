using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace AutomationPlatform;

public sealed record OpennessOperationResult(bool Success, string Message);

public sealed class OpennessOperationService(
    IOptions<DiscoveryOptions> options,
    IWebHostEnvironment environment,
    ILogger<OpennessOperationService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task<OpennessOperationResult> CompareAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.TiaProjectPath))
            throw new InvalidOperationException("请在 Discovery 配置中指定 TiaProjectPath。");
        var projectPath = ResolvePath(settings.TiaProjectPath);
        if (!File.Exists(projectPath))
            throw new FileNotFoundException("TIA 工程文件不存在，请检查 Discovery 配置。", projectPath);
        if (string.IsNullOrWhiteSpace(settings.TiaDeviceName))
            throw new InvalidOperationException("请在 Discovery 配置中指定 TiaDeviceName。");

        return RunAsync("compare", [projectPath, settings.TiaDeviceName],
            TimeSpan.FromSeconds(settings.CompareTimeoutSeconds), cancellationToken);
    }

    public Task<OpennessOperationResult> UploadAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.UploadDirectory))
            throw new InvalidOperationException("请在 Discovery 配置中指定 UploadDirectory。");
        var directory = ResolvePath(settings.UploadDirectory);
        Directory.CreateDirectory(directory);
        var projectName = $"UploadedStation_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}";
        return RunAsync("upload", [directory, projectName],
            TimeSpan.FromSeconds(settings.UploadTimeoutSeconds), cancellationToken);
    }

    private async Task<OpennessOperationResult> RunAsync(string command, string[] arguments,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("操作超时必须大于零。");
        if (!await gate.WaitAsync(0, cancellationToken))
            throw new InvalidOperationException("其他会话正在执行工程操作，请稍后重试。");

        string? operationDirectory = null;
        try
        {
            var executable = FindExecutable();
            if (string.IsNullOrWhiteSpace(options.Value.ResultDirectory))
                throw new InvalidOperationException("请配置结果目录 ResultDirectory。");
            operationDirectory = Path.Combine(ResolvePath(options.Value.ResultDirectory), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(operationDirectory);
            var resultPath = Path.Combine(operationDirectory, "result.txt");
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(command);
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add(resultPath);

            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 OpennessServices。");
            logger.LogInformation("Openness {Command} 已启动，进程 {ProcessId}", command, process.Id);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutSource.Token);
            var outputTask = ReadOutputAsync(process.StandardOutput);
            var errorTask = ReadOutputAsync(process.StandardError);
            try
            {
                await process.WaitForExitAsync(linkedSource.Token);
                await Task.WhenAll(outputTask, errorTask).WaitAsync(linkedSource.Token);
                var standardError = await errorTask;
                if (!string.IsNullOrWhiteSpace(standardError))
                    logger.LogWarning("Openness {Command} 标准错误：{Error}", command, standardError);
                logger.LogInformation("Openness {Command} 结束，退出码 {ExitCode}", command, process.ExitCode);

                if (!File.Exists(resultPath))
                    throw new InvalidOperationException($"OpennessServices 已退出（退出码 {process.ExitCode}），但未生成结果文件。\n{standardError}");

                var content = await File.ReadAllTextAsync(resultPath, linkedSource.Token);
                var lines = content.Split('\n');
                if (lines.Length < 4 || !lines[0].StartsWith("Time=", StringComparison.Ordinal) ||
                    !lines[1].StartsWith("Success=", StringComparison.Ordinal) ||
                    !bool.TryParse(lines[1]["Success=".Length..].Trim(), out var succeeded) ||
                    !lines[2].StartsWith("State=", StringComparison.Ordinal) ||
                    lines[3].TrimEnd('\r') != "Details=")
                    throw new InvalidOperationException("OpennessServices 结果文件格式无效。");

                var success = process.ExitCode == 0 && succeeded;
                var message = success ? content : $"操作失败（退出码 {process.ExitCode}）。\n{content}";
                if (!success && !string.IsNullOrWhiteSpace(standardError))
                    message += "\n" + standardError;
                return new OpennessOperationResult(success, message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException($"工程操作超过 {timeout.TotalSeconds:0} 秒，已请求停止本次进程。");
            }
            finally
            {
                await StopProcessAsync(process);
                try
                {
                    await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Openness 输出流清理未正常完成");
                }
            }
        }
        finally
        {
            if (operationDirectory != null)
            {
                try
                {
                    Directory.Delete(operationDirectory, recursive: true);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "无法清理本次操作结果目录 {Directory}", operationDirectory);
                }
            }
            gate.Release();
        }
    }

    private async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanupSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanupSource.Token);
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "无法确认本次 Openness 进程已停止");
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader)
    {
        const int limit = 16384;
        var buffer = new char[4096];
        var output = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            output.Append(buffer, 0, count);
            if (output.Length > limit)
                output.Remove(0, output.Length - limit);
        }
        return output.ToString();
    }

    private string ResolvePath(string path) => Path.GetFullPath(path, environment.ContentRootPath);

    private string FindExecutable()
    {
        if (!string.IsNullOrWhiteSpace(options.Value.ExecutablePath))
        {
            var configured = ResolvePath(options.Value.ExecutablePath);
            return File.Exists(configured) ? configured
                : throw new FileNotFoundException("配置的 OpennessServices 程序不存在。", configured);
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name == "Release" ? "Release" : "Debug";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "OpennessServices", "OpennessServices.exe"),
            Path.Combine(AppContext.BaseDirectory, "OpennessServices.exe"),
            ResolvePath(Path.Combine("..", "OpennessServices", "bin", configuration, "OpennessServices.exe"))
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("未找到 OpennessServices.exe，请生成该项目或配置 ExecutablePath。");
    }
}
