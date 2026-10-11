using System.Globalization;
using System.Text.Json;

namespace AutomationPlatform;

public sealed record StationUploadResult(string Station, string? IpAddress, string State, string Message);

public sealed record StationBatchUploadStatus(bool IsRunning, string? Trigger, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, IReadOnlyList<StationUploadResult> Results, string? Error);

public sealed class StationBatchUploadService(
    ProjectDirectoryService directories,
    OpennessOperationService operations,
    IHostApplicationLifetime lifetime,
    ILogger<StationBatchUploadService> logger) : BackgroundService
{
    private const string ScheduleFileName = "upload-schedule.json";
    private readonly object syncRoot = new();
    private TimeOnly? scheduledTime;
    private DateTime? nextRunLocal;
    private string? scheduleError;
    private StationBatchUploadStatus status = new(false, null, null, null, [], null);

    public event Action? Changed;

    public string? DailyTime
    {
        get { lock (syncRoot) return scheduledTime?.ToString("HH:mm", CultureInfo.InvariantCulture); }
    }

    public string? ScheduleError
    {
        get { lock (syncRoot) return scheduleError; }
    }

    public StationBatchUploadStatus Status
    {
        get { lock (syncRoot) return status; }
    }

    public void SaveDailyTime(string? time)
    {
        TimeOnly? parsed = null;
        if (!string.IsNullOrWhiteSpace(time))
        {
            if (!TryParseTime(time, out var value))
                throw new ArgumentException("请输入有效的时间（如 20:56 或 08:56 PM）。", nameof(time));
            parsed = value;
        }

        directories.Load();
        var filePath = Path.Combine(directories.RootPath, ScheduleFileName);
        var temporaryPath = Path.Combine(directories.RootPath, $"{ScheduleFileName}.{Guid.NewGuid():N}.tmp");
        lock (syncRoot)
        {
            try
            {
                EnsureRegularFile(filePath);
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new ScheduleConfiguration
                {
                    DailyTime = parsed?.ToString("HH:mm", CultureInfo.InvariantCulture)
                }));
                File.Move(temporaryPath, filePath, overwrite: true);
                scheduledTime = parsed;
                nextRunLocal = parsed.HasValue ? NextOccurrence(DateTime.Now, parsed.Value) : null;
                scheduleError = null;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
                    NotifyChanged();
        }

        public void DisableDailyTime() => SaveDailyTime(null);

        public bool TryStartManual() => TryStart("手动");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            LoadDailyTime();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                bool due;
                lock (syncRoot)
                {
                    var now = DateTime.Now;
                    due = scheduledTime.HasValue && nextRunLocal.HasValue && now >= nextRunLocal.Value;
                    if (due)
                        nextRunLocal = NextOccurrence(now, scheduledTime!.Value);
                }

                if (due && !TryStart("定时"))
                    logger.LogWarning("定时工站上传被跳过：另一批上传正在执行");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void LoadDailyTime()
    {
        var filePath = Path.Combine(directories.RootPath, ScheduleFileName);
        if (!File.Exists(filePath))
            return;

        try
        {
            directories.Load();
            EnsureRegularFile(filePath);
            var config = JsonSerializer.Deserialize<ScheduleConfiguration>(File.ReadAllText(filePath))
                ?? throw new InvalidDataException("上传定时配置为空。");
            TimeOnly? parsed = null;
            if (!string.IsNullOrWhiteSpace(config.DailyTime))
            {
                if (!TryParseTime(config.DailyTime, out var value))
                    throw new InvalidDataException("上传定时配置的时间格式无效。");
                parsed = value;
            }
            lock (syncRoot)
            {
                scheduledTime = parsed;
                nextRunLocal = parsed.HasValue ? NextOccurrence(DateTime.Now, parsed.Value) : null;
                scheduleError = null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            logger.LogError(exception, "读取工站上传时间配置失败");
            lock (syncRoot) scheduleError = "读取每日上传时间失败，请检查 AP 根目录中的 upload-schedule.json。";
        }
        NotifyChanged();
    }

    private bool TryStart(string trigger)
    {
        lock (syncRoot)
        {
            if (status.IsRunning || lifetime.ApplicationStopping.IsCancellationRequested)
                return false;
            status = new StationBatchUploadStatus(true, trigger, DateTimeOffset.Now, null, [], null);
        }
        NotifyChanged();
        _ = RunBatchAsync(lifetime.ApplicationStopping);
        return true;
    }

    private async Task RunBatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var stations = await Task.Run(() => directories.Load().Children.SelectMany(line => line.Children).ToArray(),
                cancellationToken);
            if (stations.Length == 0)
                SetError("AP 目录中没有可上传的 ST 工站。");

            foreach (var station in stations)
            {
                 cancellationToken.ThrowIfCancellationRequested();
                var ip = station.StationIpAddress;
                if (ip is null)
                {
                    AddResult(new StationUploadResult(station.RelativePath, null, "跳过",
                        station.StationIpError ?? "工站尚未配置 IP。"));
                    continue;
                }

                try
                {
                    var stationPath = directories.GetStationPath(station.RelativePath);
                    var result = await operations.UploadAsync(ip, stationPath, cancellationToken);
                    AddResult(new StationUploadResult(station.RelativePath, ip,
                        result.Success ? "成功" : "失败", result.Message));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "工站 {Station} ({IpAddress}) 上传失败", station.RelativePath, ip);
                    AddResult(new StationUploadResult(station.RelativePath, ip, "失败", exception.Message));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetError("应用正在停止，批量上传已取消。");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "无法完成工站批量上传");
            SetError($"无法读取工站列表：{exception.Message}");
        }
        finally
        {
            lock (syncRoot) status = status with { IsRunning = false, FinishedAt = DateTimeOffset.Now };
            NotifyChanged();
        }
    }

    private void AddResult(StationUploadResult result)
    {
        lock (syncRoot) status = status with { Results = [.. status.Results, result] };
        NotifyChanged();
    }

    private void SetError(string message)
    {
        lock (syncRoot) status = status with { Error = message };
        NotifyChanged();
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "工站上传状态通知失败");
        }
    }

    private static DateTime NextOccurrence(DateTime now, TimeOnly time)
    {
        var next = now.Date.Add(time.ToTimeSpan());
        return next > now ? next : next.AddDays(1);
    }

    private static bool TryParseTime(string input, out TimeOnly result)
    {
        var trimmed = input.Trim();
        string[] formats = [
            "HH:mm",
            "H:mm",
            "HH:mm:ss",
            "H:mm:ss",
            "hh:mm tt",
            "h:mm tt",
            "hh:mm:ss tt",
            "h:mm:ss tt"
        ];

        if (TimeOnly.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result))
            return true;

        if (TimeOnly.TryParseExact(trimmed, formats, CultureInfo.CurrentCulture, DateTimeStyles.None, out result))
            return true;

        if (TimeOnly.TryParse(trimmed, CultureInfo.InvariantCulture, out result))
            return true;

        return TimeOnly.TryParse(trimmed, CultureInfo.CurrentCulture, out result);
    }

    private static void EnsureRegularFile(string filePath)
    {
        if (File.Exists(filePath) && (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("上传定时配置文件不能是符号链接。");
    }

    private sealed class ScheduleConfiguration
    {
        public string? DailyTime { get; set; }
    }
}
