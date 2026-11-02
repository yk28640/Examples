using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CSharp;
using Microsoft.Owin.Hosting;
using Newtonsoft.Json;
using WindowsServiceV14;

internal static class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Type Controller = typeof(MainController);
    private static readonly Type Store = Controller.Assembly.GetType("WindowsServiceV14.BlockExportStore");
    private static readonly Type Runner = Controller.Assembly.GetType("WindowsServiceV14.BlockComparisonRunner");
    private static readonly Type Service = Controller.Assembly.GetType("WindowsServiceV14.BlockComparisonService");
    private static string folder;
    private static int comparisons;

    private static object Call(Type type, object target, string method, params object[] args)
    {
        try { return type.GetMethod(method, target == null ? Static : Instance).Invoke(target, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Console.WriteLine("PASS: " + message); return; }
        throw new Exception("FAIL: " + message);
    }

    private static MainController.HistoryProjectFile Version(FileInfo file)
    {
        return new MainController.HistoryProjectFile { Path = file.FullName, Length = file.Length, ModifiedUtc = file.LastWriteTimeUtc };
    }

    private static List<MainController.PlcComparison> MockCompare(FileInfo latest, FileInfo previous)
    {
        comparisons++;
        var nodes = new List<MainController.ComparisonNode>();
        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(folder, ".block-comparisons", "exports", id);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "previous.xml"), "slow");
            File.WriteAllText(Path.Combine(directory, "latest.xml"), "fixture");
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonConvert.SerializeObject(new
            {
                Id = id, Device = "PLC_1", BlockName = "分组/FB_" + i,
                PreviousProject = Version(previous), LatestProject = Version(latest),
                PreviousXml = "previous.xml", LatestXml = "latest.xml"
            }));
            nodes.Add(new MainController.ComparisonNode
            {
                ObjectType = "Block", ComparisonResult = "Different", ExportId = id,
                LeftName = "分组/FB_" + i, RightName = "分组/FB_" + i,
                LatestModifiedDate = latest.LastWriteTimeUtc, PreviousModifiedDate = previous.LastWriteTimeUtc,
                Elements = new List<MainController.ComparisonNode>()
            });
        }
        return new List<MainController.PlcComparison> { new MainController.PlcComparison
        {
            Device = "PLC_1", Result = new MainController.ComparisonNode { Elements = nodes, ComparisonResult = "Different" }
        } };
    }

    private static MainController.ChangesHistoryDocument History()
    {
        return (MainController.ChangesHistoryDocument)Call(Controller, null, "GetChangesHistory", folder,
            new Func<FileInfo, FileInfo, List<MainController.PlcComparison>>(MockCompare));
    }

    private static void ResetHistory()
    {
        Controller.GetField("cachedHistory", Static).SetValue(null, null);
        Controller.GetField("cachedHistoryPath", Static).SetValue(null, null);
    }

    private static BlockComparisonTask Wait(object service, string id, string state)
    {
        for (var i = 0; i < 150; i++)
        {
            var task = (BlockComparisonTask)Call(Service, service, "Get", id);
            if (task.State == state) return task;
            if (task.State == "Failed") throw new Exception(task.Message);
            Thread.Sleep(100);
        }
        throw new Exception("Timed out waiting for " + state);
    }

    public static int Main()
    {
        folder = Path.Combine(Path.GetTempPath(), "块比较 validation " + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tagsconfig.json");
        var original = File.Exists(config) ? File.ReadAllBytes(config) : null;
        Directory.CreateDirectory(folder);
        try
        {
            var executable = Path.Combine(folder, "ACTCLI.exe");
            using (var compiler = new CSharpCodeProvider())
            {
                var parameters = new CompilerParameters(new[] { "System.dll" }, executable) { GenerateExecutable = true };
                var result = compiler.CompileAssemblyFromSource(parameters,
                    "using System; using System.IO; using System.Threading; class Fake { static int Main(string[] a) { var mode = File.ReadAllText(a[0]); if(mode == \"hang\") Thread.Sleep(20000); if(mode == \"slow\") Thread.Sleep(2000); if(mode == \"fail\") return 2; File.WriteAllText(a[3], \"%PDF-1.4\\nfixture\\n%%EOF\"); return 0; } }");
                Check(!result.Errors.HasErrors, "compile isolated fake ACTCLI (never runs Siemens tool)");
            }
            File.WriteAllText(config, JsonConvert.SerializeObject(new
            {
                comparasionFolder = folder,
                blockComparison = new { ExecutablePath = executable, TimeoutSeconds = 5, MaxConcurrency = 1, QueueCapacity = 1 }
            }));
            var previousPath = Path.Combine(folder, "previous.ap14");
            var latestPath = Path.Combine(folder, "latest.ap14");
            File.WriteAllText(previousPath, "fixture");
            File.WriteAllText(latestPath, "fixture");
            File.SetLastWriteTimeUtc(previousPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(latestPath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            foreach (var status in new[] { "Different", "Added", "Removed", "Identical" })
                foreach (var type in new[] { "Block", "TechnologicalObject" })
                    Check((bool)Call(Controller, null, "IsModifiedBlock", new MainController.ComparisonNode
                    {
                        ObjectType = type, ComparisonResult = status, LeftName = "FB", RightName = "FB"
                    }) == (status == "Different" && type == "Block"), "export filter " + type + "/" + status);
            Check(!(bool)Call(Store, null, "IsId", "../secret"), "path traversal rejected");
            foreach (var argument in new[] { "", "中文 path/file.xml", "a\"b", @"C:\trailing\", "a\\\\\"b" })
            {
                var quoted = (string)Call(Runner, null, "Quote", argument);
                int count;
                var pointer = CommandLineToArgvW("fixture.exe " + quoted, out count);
                try { Check(count == 2 && Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, IntPtr.Size)) == argument, "Windows argument round trip"); }
                finally { LocalFree(pointer); }
            }
            ResetHistory();
            var history = History();
            Check(history.Version == 3 && comparisons == 1, "new history version 3");
            var generatedAt = history.GeneratedAtUtc;
            var comparedAt = history.Entries[0].ComparedAtUtc;
            var cachePath = Path.Combine(folder, "ChangesHistory.json");
            var cacheText = File.ReadAllText(cachePath);
            Check(ReferenceEquals(History(), history) && comparisons == 1, "unchanged history returns same memory document");
            ResetHistory(); History(); Check(comparisons == 1, "valid XML reuses disk cache");
            var id = history.Entries[0].Comparisons[0].Result.Elements[0].ExportId;
            var xmlPath = Path.Combine(folder, ".block-comparisons", "exports", id, "latest.xml");
            File.Delete(xmlPath);
            history = History(); Check(comparisons == 1, "missing XML does not invalidate memory history");
            Check(Call(Controller, null, "GetPublishedExport", id) == null, "missing XML unavailable for explicit comparison only");
            ResetHistory(); history = History(); Check(comparisons == 1, "missing XML does not invalidate JSON history");
            Check(history.GeneratedAtUtc == generatedAt && history.Entries[0].ComparedAtUtc == comparedAt
                && File.ReadAllText(cachePath) == cacheText, "cache hits preserve timestamps and JSON contents");
            var failedNode = history.Entries[0].Comparisons[0].Result.Elements[0];
            failedNode.ExportId = null;
            failedNode.ExportError = "fixture protected block export failed";
            File.WriteAllText(cachePath, JsonConvert.SerializeObject(history));
            ResetHistory(); history = History();
            Check(comparisons == 1 && history.Entries[0].Comparisons[0].Result.Elements[0].ExportError != null,
                "failed export reuses disk history without reopening projects");
            Check(ReferenceEquals(History(), history) && comparisons == 1, "failed export reuses memory history");
            failedNode = history.Entries[0].Comparisons[0].Result.Elements[0];
            failedNode.ExportId = id;
            failedNode.ExportError = null;
            File.WriteAllText(xmlPath, "fixture");
            File.WriteAllText(cachePath, JsonConvert.SerializeObject(history));
            var addedPath = Path.Combine(folder, "added.ap14");
            File.WriteAllText(addedPath, "fixture");
            File.SetLastWriteTimeUtc(addedPath, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
            history = History(); Check(comparisons == 2 && history.Entries.Count == 2, "new project only compares new adjacent pair");
            File.Delete(addedPath);
            history = History(); Check(comparisons == 2 && history.Entries.Count == 1, "removed trailing project preserves unchanged pair");
            history.Version = 2;
            File.WriteAllText(Path.Combine(folder, "ChangesHistory.json"), JsonConvert.SerializeObject(history));
            ResetHistory(); history = History(); Check(comparisons == 3 && history.Version == 3, "old v2 cache rebuilt");
            Check(Call(Controller, null, "GetPublishedExport", Guid.NewGuid().ToString("N")) == null, "unpublished export rejected");
            var exports = history.Entries[0].Comparisons[0].Result.Elements.Select(node => node.ExportId).ToArray();
            Call(Service, null, "StartHost");
            var service = Service.GetProperty("Current", Static).GetValue(null);
            var first = (BlockComparisonTask)Call(Service, service, "Create", Call(Controller, null, "GetPublishedExport", exports[0]));
            Wait(service, first.Id, "Running");
            var same = (BlockComparisonTask)Call(Service, service, "Create", Call(Controller, null, "GetPublishedExport", exports[0]));
            Check(same.Id == first.Id, "duplicate running task reused");
            var second = (BlockComparisonTask)Call(Service, service, "Create", Call(Controller, null, "GetPublishedExport", exports[1]));
            Expect<Exception>(() => Call(Service, service, "Create", Call(Controller, null, "GetPublishedExport", exports[2])), "bounded queue rejects excess work");
            Wait(service, first.Id, "Succeeded");
            Wait(service, second.Id, "Succeeded");
            using (var report = (Stream)Call(Service, service, "OpenReport", first.Id)) Check(report.Length > 8, "PDF report stream available");
            Check(Call(Service, service, "OpenReport", "../task.json") == null, "report path traversal rejected");
            var succeeded = (BlockComparisonTask)Call(Service, service, "Create", Call(Controller, null, "GetPublishedExport", exports[0]));
            Check(succeeded.Id == first.Id, "completed report reused");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            using (var host = WebApp.Start<Startup>("http://localhost:" + port + "/"))
            using (var client = new HttpClient { BaseAddress = new Uri("http://localhost:" + port + "/") })
            {
                var probe = client.GetAsync("test").Result;
                Check(probe.StatusCode == HttpStatusCode.OK && probe.Content.ReadAsStringAsync().Result == "Helloworld",
                    "cross-assembly host discovers backend probe route");
                var invalid = client.PostAsync("api/block-comparisons", new StringContent("{\"ExportId\":\"../file\"}", System.Text.Encoding.UTF8, "application/json")).Result;
                Check(invalid.StatusCode == HttpStatusCode.BadRequest, "HTTP POST rejects invalid export ID");
                var large = client.PostAsync("api/block-comparisons", new StringContent(new string('x', 2048), System.Text.Encoding.UTF8, "application/json")).Result;
                Check((int)large.StatusCode == 413, "HTTP POST request size limited");
                var accepted = client.PostAsync("api/block-comparisons", new StringContent(JsonConvert.SerializeObject(new { ExportId = exports[0] }), System.Text.Encoding.UTF8, "application/json")).Result;
                Check(accepted.StatusCode == HttpStatusCode.Accepted, "HTTP POST creates or reuses task");
                var response = client.GetAsync("api/block-comparisons/" + first.Id + "/report").Result;
                Check(response.IsSuccessStatusCode && response.Content.Headers.ContentType.MediaType == "application/pdf", "HTTP report PDF content type");
                Check(response.Headers.CacheControl.NoStore, "HTTP report not cached");
                var missing = client.GetAsync("api/block-comparisons/" + Guid.NewGuid().ToString("N") + "/report").Result;
                Check(missing.StatusCode == HttpStatusCode.NotFound, "HTTP unknown report returns 404");
                using (var preflight = new HttpRequestMessage(HttpMethod.Options, "api/block-comparisons"))
                {
                    preflight.Headers.Add("Origin", "http://localhost:5173");
                    preflight.Headers.Add("Access-Control-Request-Method", "POST");
                    preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
                    var cors = client.SendAsync(preflight).Result;
                    Check(cors.IsSuccessStatusCode && cors.Headers.Contains("Access-Control-Allow-Origin"), "CORS JSON POST preflight supported");
                }
            }
            Call(Service, null, "StopHost");
            var interrupted = Guid.NewGuid().ToString("N");
            var interruptedDirectory = Path.Combine(folder, ".block-comparisons", "jobs", interrupted);
            Directory.CreateDirectory(interruptedDirectory);
            File.WriteAllText(Path.Combine(interruptedDirectory, "task.json"), JsonConvert.SerializeObject(new BlockComparisonTask
            {
                Id = interrupted, ExportId = exports[0], State = "Running", CreatedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.UtcNow.AddHours(1)
            }));
            Call(Service, null, "StartHost");
            service = Service.GetProperty("Current", Static).GetValue(null);
            Check(((BlockComparisonTask)Call(Service, service, "Get", interrupted)).State == "Failed", "restart marks interrupted tasks failed");
            Check(((BlockComparisonTask)Call(Service, service, "Get", first.Id)).State == "Succeeded", "restart preserves completed report");
            Call(Service, null, "StopHost");
            var options = Activator.CreateInstance(Controller.Assembly.GetType("WindowsServiceV14.BlockComparisonOptions"));
            options.GetType().GetProperty("ExecutablePath").SetValue(options, executable);
            options.GetType().GetProperty("TimeoutSeconds").SetValue(options, 5);
            var source = Path.Combine(folder, "hang.xml");
            File.WriteAllText(source, "hang");
            var work = Path.Combine(folder, "timeout"); Directory.CreateDirectory(work);
            Expect<TimeoutException>(() => Call(Runner, null, "Run", options, source, source, work, CancellationToken.None), "timeout stops fake ACTCLI");
            work = Path.Combine(folder, "cancel"); Directory.CreateDirectory(work);
            using (var cancellation = new CancellationTokenSource(300))
                Expect<OperationCanceledException>(() => Call(Runner, null, "Run", options, source, source, work, cancellation.Token), "cancellation stops fake ACTCLI");
            var invalidPdf = Path.Combine(folder, "invalid.pdf"); File.WriteAllText(invalidPdf, "%PDF-1.4 incomplete");
            Expect<InvalidOperationException>(() => Call(Runner, null, "ValidatePdf", invalidPdf, 1024L), "incomplete PDF rejected");
            File.AppendAllText(latestPath, "changed");
            Check(Call(Controller, null, "GetPublishedExport", exports[0]) == null, "changed project invalidates published export");
            Console.WriteLine("ALL BACKEND VALIDATIONS PASSED (fake ACTCLI, no TIA projects opened)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            Call(Service, null, "StopHost");
            if (original == null) File.Delete(config); else File.WriteAllBytes(config, original);
            Directory.Delete(folder, true);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
