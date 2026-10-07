using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace AutomationPlatform.Components.Pages;

public partial class Discovery
{
    [Inject] private DiscoveryScanService ScanService { get; set; } = null!;
    [Inject] private OpennessOperationService OperationService { get; set; } = null!;
    [Inject] private IOptions<DiscoveryOptions> Options { get; set; } = null!;
    [Inject] private ILogger<Discovery> Logger { get; set; } = null!;

    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operationCancellation;
    private bool disposed;
    private string? activeOperation;
    private string? errorMessage;
    private IReadOnlyList<string>? networkInterfaces;
    private string? selectedItem;
    private string? scanErrorMessage;
    private IReadOnlyList<DiscoveredDevice>? scannedDevices;
    private string? compareErrorMessage;
    private string? compareResultMessage;
    private string? uploadErrorMessage;
    private string? uploadResultMessage;

    private DiscoveryOptions Settings => Options.Value;
    private bool IsBusy => disposed || activeOperation != null;
    private bool isLoading => activeOperation == "interfaces";
    private bool isScanning => activeOperation == "scan";
    private bool isComparing => activeOperation == "compare";
    private bool isUploading => activeOperation == "upload";

    private Task LoadNetworkInterfaces() => RunUiOperationAsync("interfaces", () =>
    {
        errorMessage = null;
        networkInterfaces = null;
        selectedItem = null;
        scannedDevices = null;
        scanErrorMessage = null;
    }, async token =>
    {
        var interfaces = await ScanService.GetNetworkInterfacesAsync(token);
        if (!disposed)
            networkInterfaces = interfaces;
    }, message => errorMessage = message);

    private Task OnItemClick(string item) => RunUiOperationAsync("scan", () =>
    {
        selectedItem = item;
        scannedDevices = null;
        scanErrorMessage = null;
    }, async token =>
    {
        var result = await ScanService.ScanAsync(item, Settings.ScanFilter, token);
        if (!disposed)
        {
            scannedDevices = result.Devices;
            scanErrorMessage = result.Warning;
        }
    }, message => scanErrorMessage = message);

    private Task CompareDeviceAsync() => RunUiOperationAsync("compare", () =>
    {
        compareErrorMessage = null;
        compareResultMessage = null;
    }, async token =>
    {
        var result = await OperationService.CompareAsync(token);
        if (!disposed)
        {
            if (result.Success)
                compareResultMessage = result.Message;
            else
                compareErrorMessage = result.Message;
        }
    }, message => compareErrorMessage = message);

    private Task UploadDeviceAsync() => RunUiOperationAsync("upload", () =>
    {
        uploadErrorMessage = null;
        uploadResultMessage = null;
    }, async token =>
    {
        var result = await OperationService.UploadAsync(token);
        if (!disposed)
        {
            if (result.Success)
                uploadResultMessage = result.Message;
            else
                uploadErrorMessage = result.Message;
        }
    }, message => uploadErrorMessage = message);

    private async Task RunUiOperationAsync(string operation, Action reset,
        Func<CancellationToken, Task> run, Action<string> showError)
    {
        if (IsBusy)
            return;

        activeOperation = operation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operationCancellation = cancellation;
        try
        {
            reset();
            await run(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!disposed)
                showError("本次操作已取消。");
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Discovery {Operation} 操作失败", operation);
            if (!disposed)
                showError(exception.Message);
        }
        finally
        {
            operationCancellation = null;
            activeOperation = null;
        }
    }

    private void CancelOperation() => operationCancellation?.Cancel();

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
