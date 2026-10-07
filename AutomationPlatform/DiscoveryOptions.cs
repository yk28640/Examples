namespace AutomationPlatform;

public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    public string ScanFilter { get; set; } = "192.168.98.[0-255]";
    public string TiaProjectPath { get; set; } = string.Empty;
    public string TiaDeviceName { get; set; } = string.Empty;
    public string UploadDirectory { get; set; } = @"E:\TMP";
    public string ExecutablePath { get; set; } = string.Empty;
    public string ResultDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "AutomationPlatform", "OpennessResults");
    public int CompareTimeoutSeconds { get; set; } = 120;
    public int UploadTimeoutSeconds { get; set; } = 600;
}

public sealed record DiscoveredDevice(string IpAddress, string MacAddress, string ProfinetName,
    string Description, string ArticleNumber, string? OperatingMode, string? StatusError);

public sealed record DiscoveryScanResult(IReadOnlyList<DiscoveredDevice> Devices, string? Warning);
