using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Web.Http;
using Newtonsoft.Json.Linq;
using ServiceV14;
using Swashbuckle.Application;

internal static class EventLogValidation
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private static void Main()
    {
        var service = new EventLogService();
        foreach (var limit in new[] { 0, -1, 5001 })
        {
            bool rejected = false;
            try { service.ReadOperationLogs(limit); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected, "Service rejects limit " + limit);
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool cancelled = false;
            try { service.ReadOperationLogs(1, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancelled read stops before accessing event channel");
        }
        using (var config = new HttpConfiguration())
        {
            config.MapHttpAttributeRoutes();
            config.EnableSwagger(options =>
            {
                options.SingleApiVersion("v1", "Event log validation");
                options.OperationFilter<SwaggerOperationFilter>();
            });
            using (var client = new HttpClient(new HttpServer(config)))
            {
                client.BaseAddress = new Uri("http://localhost/");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
                foreach (var value in new[] { "0", "-1", "5001", "abc", "1.5", "", "2147483648" })
                {
                    using (var response = client.GetAsync("GetEventLog?maximumEntries=" + value).GetAwaiter().GetResult())
                        Check(response.StatusCode == HttpStatusCode.BadRequest, "HTTP rejects invalid maximumEntries '" + value + "'");
                }
                foreach (var limit in new[] { 0, 1, 100, 5000 })
                {
                    var url = limit == 0 ? "GetEventLog" : "GetEventLog?maximumEntries=" + limit;
                    using (var response = client.GetAsync(url).GetAwaiter().GetResult())
                    {
                        Check(response.StatusCode == HttpStatusCode.OK, "Real event channel: " + url + " returns HTTP 200");
                        Check(response.Content.Headers.ContentType.MediaType == "application/json", "Successful response is JSON");
                        var entries = JArray.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                        Check(entries.Count <= (limit == 0 ? 100 : limit), "Response respects requested limit");
                        string[] fields = { "TimeCreated", "User", "Computer", "EventContent", "EventId", "Level", "Provider", "RecordId" };
                        Check(entries.All(entry => fields.All(field => ((JObject)entry).Property(field) != null)), "Response includes all eight fields");
                        var ids = entries.Select(entry => (string)entry["RecordId"]).Where(id => !string.IsNullOrEmpty(id)).Select(long.Parse).ToArray();
                        Check(ids.Zip(ids.Skip(1), (left, right) => left > right).All(value => value), "Records are newest first");
                        Console.WriteLine("Read record count: " + entries.Count);
                    }
                }
                using (var response = client.GetAsync("swagger/docs/v1").GetAwaiter().GetResult())
                {
                    Check(response.StatusCode == HttpStatusCode.OK, "Swagger loads without project operations");
                    var swagger = JObject.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    var operation = swagger["paths"]["/GetEventLog"]["get"];
                    Check(operation != null, "Swagger contains GetEventLog GET operation");
                    var parameter = operation["parameters"].Single(item => (string)item["name"] == "maximumEntries");
                    Check((int)parameter["default"] == 100 && (int)parameter["minimum"] == 1 && (int)parameter["maximum"] == 5000,
                        "Swagger limit default and bounds match API");
                    Check(new[] { "200", "400", "403", "503" }.All(status => operation["responses"][status] != null), "Swagger documents all response statuses");
                    Check((string)operation["responses"]["200"]["schema"]["type"] == "array", "Swagger success model is an array");
                }
                using (var response = client.GetAsync("test").GetAwaiter().GetResult())
                    Check(response.StatusCode == HttpStatusCode.OK && response.Content.ReadAsStringAsync().GetAwaiter().GetResult() == "Helloworld", "Existing test endpoint unchanged");
            }
        }
    }
}
