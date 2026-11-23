namespace SimaticSat.ReadOnly;

public enum SatLanguage { English, Chinese }

public sealed record SatMessage(string Code, string Description);
public sealed record SatRead<T>(T Data, IReadOnlyList<SatMessage> Warnings)
{
    public IReadOnlyList<SatScanEvent> LookupEvents { get; init; } = Array.Empty<SatScanEvent>();
}
public sealed record SatScanEvent(DateTime Timestamp, string Type, string Code,
    string IpAddress, string MacAddress, string Name);
public sealed record SatScan(bool Succeeded, IReadOnlyList<SatDevice> Devices,
    IReadOnlyList<SatScanEvent> Events);
public sealed record SatDevice(string IpAddress, string MacAddress, string ProfinetName,
    string Name, string Family, string ArticleNumber, string FirmwareVersion,
    string SerialNumber, bool Found, bool Initialized, bool DuplicateIp, bool FailSafe);
public sealed record SatModule(uint Slot, string Name, string ArticleNumber,
    string FirmwareVersion, string SerialNumber);
public sealed record SatDeviceDetails(SatDevice Device, string? OperatingState,
    IReadOnlyList<SatModule> Modules);
public sealed record SatDiagnostic(DateTime Timestamp, byte State, string Summary, string Details);

public sealed class SatSdkException : Exception
{
    internal SatSdkException(string operation, SatMessage error, IReadOnlyList<SatMessage> warnings)
        : base($"{operation}: {error.Code}: {error.Description}")
    {
        Operation = operation;
        Error = error;
        Warnings = warnings;
    }

    public string Operation { get; }
    public SatMessage Error { get; }
    public IReadOnlyList<SatMessage> Warnings { get; }
}

public sealed class SatLookupException : Exception
{
    internal SatLookupException(string ip, IReadOnlyList<SatScanEvent> events)
        : base($"InsertDeviceByIP did not identify one usable device at {ip}. See Events.")
        => Events = events;

    public IReadOnlyList<SatScanEvent> Events { get; }
}
