using System.Runtime.CompilerServices;
using Siemens.Automation.AutomationTool.API;
using IResult = Microsoft.AspNetCore.Http.IResult;

internal sealed record SatScanRequest(string? Adapter);

internal static partial class SatAdapters
{
    internal static IResult Scan(string? adapter, ILogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(adapter))
            return AdapterUnavailable();

        if (!Monitor.TryEnter(SdkLock))
            return Results.Problem(title: "SAT is busy.", statusCode: StatusCodes.Status429TooManyRequests);

        try
        {
            return ScanDevices(adapter, logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "SAT device scan failed.");
            return Results.Problem(title: "SAT device scan is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            Monitor.Exit(SdkLock);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IResult ScanDevices(string adapter, ILogger logger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var network = new Network();
        try
        {
            var query = network.QueryNetworkInterfaceCards(out var adapters);
            if (query.Failed)
                throw new InvalidOperationException($"QueryNetworkInterfaceCards failed: {query.Error}");
            if (adapters is null || !adapters.Contains(adapter, StringComparer.Ordinal))
                return AdapterUnavailable();

            var selection = network.SetCurrentNetworkInterface(adapter);
            if (selection.Failed)
            {
                logger.LogWarning("SAT adapter selection failed: {Code}", selection.Error);
                return AdapterUnavailable();
            }

            cancellationToken.ThrowIfCancellationRequested();
            var events = network.ScanNetworkDevices(out var devices);
            try
            {
                var snapshots = new List<SatScannedDevice>();
                if (devices is not null)
                {
                    foreach (IProfinetDevice device in devices)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        snapshots.Add(ReadDevice(device, logger));
                    }
                }

                return Results.Ok(new
                {
                    adapter,
                    succeeded = events.Succeeded,
                    warning = events.Failed ? "SAT reported scan errors; results may be incomplete." : null,
                    devices = snapshots
                });
            }
            finally
            {
                ((object?)devices as IDisposable)?.Dispose();
            }
        }
        finally
        {
            ((object)network as IDisposable)?.Dispose();
        }
    }

    private static IResult AdapterUnavailable() => Results.Problem(
        title: "Select an available adapter from GET /api/sat/adapters and update sat.scanAdapter.",
        statusCode: StatusCodes.Status409Conflict);
}
