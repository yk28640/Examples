using Siemens.Automation.AutomationTool.API;

namespace AutomationPlatform;

public sealed class DiscoveryScanService(ILogger<DiscoveryScanService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<string>> GetNetworkInterfacesAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run<IReadOnlyList<string>>(() =>
            {
                var network = new Network();
                try
                {
                    var result = network.QueryNetworkInterfaceCards(out List<string> interfaces);
                    if (result.Failed)
                        throw new InvalidOperationException($"无法枚举网卡：{result}");

                    return interfaces?.ToArray() ?? [];
                }
                finally
                {
                    ((object)network as IDisposable)?.Dispose();
                }
            }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<DiscoveryScanResult> ScanAsync(string networkInterface, string filter,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkInterface);
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                var network = new Network();
                try
                {
                    var selection = network.SetCurrentNetworkInterface(networkInterface);
                    if (selection.Failed)
                        throw new InvalidOperationException($"无法选择网卡：{selection}");

                    var errors = network.ScanNetworkDevices(out IProfinetDeviceCollection devices,
                        string.IsNullOrWhiteSpace(filter) ? null : filter);
                    var snapshots = new List<DiscoveredDevice>();
                    try
                    {
                        if (devices != null)
                        {
                            foreach (IProfinetDevice device in devices)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                string? operatingMode = null;
                                string? statusError = null;
                                if (device is ICPU cpu)
                                {
                                    try
                                    {
                                        var status = device.RefreshStatus();
                                        if (status.Succeeded)
                                            operatingMode = cpu.OperatingMode.ToString();
                                        else
                                            statusError = status.ToString();
                                    }
                                    catch (Exception exception)
                                    {
                                        logger.LogWarning(exception, "设备 {IpAddress} 状态刷新失败", device.IPString);
                                        statusError = exception.Message;
                                    }
                                }

                                snapshots.Add(new DiscoveredDevice(device.IPString, device.MACString,
                                    device.ProfinetName, device.Description, device.ArticleNumber,
                                    operatingMode, statusError));
                            }
                        }
                    }
                    finally
                    {
                        ((object?)devices as IDisposable)?.Dispose();
                    }

                    return new DiscoveryScanResult(snapshots, errors.Failed ? errors.ToString() : null);
                }
                finally
                {
                    ((object)network as IDisposable)?.Dispose();
                }
            }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
