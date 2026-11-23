using System.Buffers.Binary;
using System.Net;
using Siemens.Automation.AutomationTool.API;

namespace SimaticSat.ReadOnly;

public sealed class SatClient
{
    private static readonly object SdkLock = new();
    private readonly string _adapter;
    private readonly SatLanguage _language;

    public SatClient(string adapter, SatLanguage language = SatLanguage.English)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter);
        _ = ToLanguage(language);
        _adapter = adapter;
        _language = language;
    }

    public static SatRead<IReadOnlyList<string>> ListAdapters(SatLanguage language = SatLanguage.English)
    {
        var sdkLanguage = ToLanguage(language);
        lock (SdkLock)
        {
            var result = new Network().QueryNetworkInterfaceCards(out var adapters);
            return new(RequireSuccess(result, adapters, "QueryNetworkInterfaceCards", sdkLanguage)
                .AsReadOnly(), Warnings(result, sdkLanguage));
        }
    }

    public SatRead<SatScan> Scan(string? filter = null)
    {
        if (filter is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(filter);

        lock (SdkLock)
        {
            var warnings = new List<SatMessage>();
            var network = SelectNetwork(warnings);
            var result = filter is null
                ? network.ScanNetworkDevices(out var devices)
                : network.ScanNetworkDevices(out devices, filter, NetworkScanType.Layer2Local);
            var snapshots = new List<SatDevice>();
            if (devices is not null)
            {
                foreach (IProfinetDevice device in devices)
                    snapshots.Add(Snapshot(device));
            }
            return new(new(result.Succeeded, snapshots.AsReadOnly(), ScanEvents(result)), warnings.AsReadOnly());
        }
    }

    public SatRead<SatDeviceDetails> ReadDevice(string ipv4, string subnetMask, string? password = null,
        string? userName = null)
        => ReadTarget(ipv4, subnetMask, password, userName, (device, _) =>
        {
            var modules = new List<SatModule>();
            if (device.Modules is not null)
            {
                foreach (IModule module in device.Modules)
                    modules.Add(new(module.Slot, module.Name, module.ArticleNumber,
                        module.FirmwareVersion, module.SerialNumber));
            }
            return new SatDeviceDetails(Snapshot(device), (device as ICPU)?.OperatingMode.ToString(),
                modules.AsReadOnly());
        });

    public SatRead<IReadOnlyList<SatDiagnostic>> ReadDiagnostics(string ipv4, string subnetMask,
        string? password = null, string? userName = null)
        => ReadTarget<IReadOnlyList<SatDiagnostic>>(ipv4, subnetMask, password, userName, (device, warnings) =>
        {
            if (device is not ICPU && device is not ICPUClassic)
                throw new NotSupportedException("Diagnostic buffer reads require a supported Siemens CPU.");
            if (device is IDiagnosticBuffer { DiagBufferAllowed: false })
                throw new InvalidOperationException("The SDK reports that diagnostic buffer access is not allowed.");

            var wasSelected = device.Selected;
            device.Selected = true;
            try
            {
                var language = ToLanguage(_language);
                var result = device is ICPU cpu
                    ? cpu.GetDiagnosticsBuffer(out var entries, language)
                    : ((ICPUClassic)device).GetDiagnosticsBuffer(out entries, language);
                Check(result, "GetDiagnosticsBuffer", language, warnings);
                return Array.AsReadOnly(entries.Select(entry => new SatDiagnostic(entry.TimeStamp, entry.State,
                    entry.Description1, entry.Description2)).ToArray());
            }
            finally
            {
                device.Selected = wasSelected;
            }
        });

    private SatRead<T> ReadTarget<T>(string ipv4, string subnetMask, string? password, string? userName,
        Func<IProfinetDevice, List<SatMessage>, T> read)
    {
        var address = ParseIpv4(ipv4, nameof(ipv4));
        var mask = ParseIpv4(subnetMask, nameof(subnetMask));
        var hostBits = ~mask;
        if (mask == 0 || (hostBits & unchecked(hostBits + 1)) != 0)
            throw new ArgumentException("Expected a nonzero contiguous IPv4 subnet mask.", nameof(subnetMask));
        if (address == 0 || (address >> 24) >= 224)
            throw new ArgumentException("Expected a unicast device address.", nameof(ipv4));
        if (password is not null)
            ArgumentException.ThrowIfNullOrEmpty(password);
        if (userName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(userName);
            if (password is null)
                throw new ArgumentException("A local project user also requires a password.", nameof(password));
        }

        lock (SdkLock)
        {
            var language = ToLanguage(_language);
            var warnings = new List<SatMessage>();
            var network = SelectNetwork(warnings);
            var devices = Network.GetEmptyCollection();
            var lookup = devices.InsertDeviceByIP(ip: address, routerIP: 0, subnet: mask,
                password: null, theDevice: out var device);
            var events = ScanEvents(lookup);
            if (!lookup.Succeeded || device is null || device.IP != address || device.DuplicateIP)
            {
                GC.KeepAlive(network);
                throw new SatLookupException(ipv4, events);
            }

            AuthorizationData? authorization = null;
            try
            {
                if (device is ICPU cpu)
                {
                    if (cpu.TLSTrustRequired)
                        throw new InvalidOperationException("The CPU requires TLS certificate approval. This wrapper does not approve certificates.");
                    if (cpu.Protected)
                    {
                        if (password is null)
                            throw new InvalidOperationException("This protected CPU requires authorized read credentials.");
                        authorization = new(userName is null ? ConnectionType.AccessLevel : ConnectionType.LocalProjectUser,
                            userName ?? "", password);
                        Check(cpu.SetPassword(authorization), "SetPassword", language, warnings);
                    }
                    else if (password is not null)
                    {
                        throw new ArgumentException("Do not supply credentials for an unprotected CPU.", nameof(password));
                    }
                }
                else if (password is not null)
                {
                    throw new NotSupportedException("This wrapper only supplies credentials to protected ICPU devices.");
                }

                Check(device.RefreshStatus(), "RefreshStatus", language, warnings);
                return new(read(device, warnings), warnings.AsReadOnly()) { LookupEvents = events };
            }
            finally
            {
                authorization?.Clear();
                GC.KeepAlive(network);
            }
        }
    }

    private Network SelectNetwork(List<SatMessage> warnings)
    {
        var network = new Network();
        Check(network.SetCurrentNetworkInterface(_adapter), "SetCurrentNetworkInterface",
            ToLanguage(_language), warnings);
        return network;
    }

    private static void Check(Result result, string operation, Language language, List<SatMessage> warnings)
    {
        warnings.AddRange(Warnings(result, language));
        if (result.Failed)
            throw new SatSdkException(operation, new(result.Error.ToString(), result.GetErrorDescription(language)),
                warnings.AsReadOnly());
    }

    private static T RequireSuccess<T>(Result result, T data, string operation, Language language)
    {
        if (result.Failed)
            throw new SatSdkException(operation, new(result.Error.ToString(), result.GetErrorDescription(language)),
                Warnings(result, language));
        return data;
    }

    private static IReadOnlyList<SatMessage> Warnings(Result result, Language language)
    {
        if (!result.HasWarnings)
            return Array.Empty<SatMessage>();
        var descriptions = result.GetWarningDescription(language);
        return Array.AsReadOnly(result.Warnings.Select((code, index) =>
            new SatMessage(code.ToString(), descriptions.ElementAtOrDefault(index) ?? "")).ToArray());
    }

    private static IReadOnlyList<SatScanEvent> ScanEvents(IScanErrorCollection result)
    {
        var events = new List<SatScanEvent>();
        foreach (IScanErrorEvent evt in result)
            events.Add(new(evt.TimeStamp, evt.Type.ToString(), evt.Code.ToString(), evt.IP, evt.MAC, evt.Name));
        return events.AsReadOnly();
    }

    private static SatDevice Snapshot(IProfinetDevice device)
        => new(device.IPString, device.MACString, device.ProfinetName, device.Name, device.FamilyName,
            device.ArticleNumber, device.FirmwareVersion, device.SerialNumber, device.DeviceFound,
            device.Initialized, device.DuplicateIP, device.Failsafe);

    private static Language ToLanguage(SatLanguage language) => language switch
    {
        SatLanguage.English => Language.English,
        SatLanguage.Chinese => Language.Chinese,
        _ => throw new ArgumentOutOfRangeException(nameof(language))
    };

    private static uint ParseIpv4(string text, string parameter)
    {
        if (!IPAddress.TryParse(text, out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || address.ToString() != text)
            throw new ArgumentException("Expected an IPv4 address in dotted-decimal notation.", parameter);
        return BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
    }
}
