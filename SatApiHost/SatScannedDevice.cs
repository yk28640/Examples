using Siemens.Automation.AutomationTool.API;

internal sealed record SatScannedDevice(string IpAddress, string MacAddress, string ProfinetName,
    string Description, string ArticleNumber, bool IsCpu, string? OperatingMode,
    SatCpuMemoryUsage? MemoryUsage, IReadOnlyList<string> Warnings);

internal sealed record SatCpuMemoryUsage(ulong LoadMemorySize, ulong LoadMemoryUsedSize,
    ulong WorkMemorySize, ulong WorkMemoryUsedSize, ulong CodeWorkMemorySize, ulong CodeWorkMemoryUsedSize,
    ulong DataWorkMemorySize, ulong DataWorkMemoryUsedSize, ulong RetainMemorySize, ulong RetainMemoryUsedSize);

internal static partial class SatAdapters
{
    private static SatScannedDevice ReadDevice(IProfinetDevice device, ILogger logger)
    {
        var ip = device.IPString;
        var warnings = new List<string>();
        string? operatingMode = null;
        SatCpuMemoryUsage? memoryUsage = null;

        if (device is ICPU cpu && TryRefresh(device, ip, warnings, logger))
        {
            try
            {
                operatingMode = cpu.OperatingMode.ToString();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Cannot read SAT CPU operating mode for {IpAddress}.", ip);
                warnings.Add("CPU operating mode is unavailable.");
            }

            try
            {
                var memory = cpu.MemoryUsage;
                if (memory is null)
                    warnings.Add("CPU memory usage is unavailable.");
                else
                    memoryUsage = new(memory.LoadMemorySize, memory.LoadMemoryUsedSize,
                        memory.WorkMemorySize, memory.WorkMemoryUsedSize,
                        memory.CodeWorkMemorySize, memory.CodeWorkMemoryUsedSize,
                        memory.DataWorkMemorySize, memory.DataWorkMemoryUsedSize,
                        memory.RetainMemorySize, memory.RetainMemoryUsedSize);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Cannot read SAT CPU memory usage for {IpAddress}.", ip);
                warnings.Add("CPU memory usage is unavailable.");
            }
        }

        return new(ip, device.MACString, device.ProfinetName, device.Description, device.ArticleNumber,
            device is ICPU, operatingMode, memoryUsage, warnings);
    }

    private static bool TryRefresh(IProfinetDevice device, string ip, List<string> warnings, ILogger logger)
    {
        try
        {
            var status = device.RefreshStatus();
            if (status.HasWarnings)
                warnings.AddRange(status.GetWarningDescription(Language.English));
            if (status.Succeeded)
                return true;

            logger.LogWarning("SAT CPU refresh failed for {IpAddress}: {Code}", ip, status.Error);
            warnings.Add($"CPU status refresh failed ({status.Error}); live values are unavailable.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "SAT CPU refresh failed for {IpAddress}.", ip);
            warnings.Add("CPU status refresh failed; live values are unavailable.");
        }

        return false;
    }
}
