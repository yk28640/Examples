using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceV14
{
    internal sealed class SatHostProcess : IDisposable
    {
        internal const string ConfigurationKey = "ServiceV14.SatHostProcess";
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        })
        {
            BaseAddress = new Uri("http://127.0.0.1:5737/"),
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 1024 * 1024
        };

        private readonly object gate = new object();
        private Process process;
        private string token;
        private bool ready;
        private bool disposed;

        internal void StartInBackground() => Task.Run((Func<Task>)StartAsync);

        private async Task StartAsync()
        {
            try
            {
                string sessionToken;
                lock (gate)
                {
                    if (disposed)
                        return;

                    var directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SatApiHost");
                    var executable = Path.Combine(directory, "SatApiHost.exe");
                    if (!File.Exists(executable))
                    {
                        Trace.TraceWarning("SAT 宿主未部署：{0}", executable);
                        return;
                    }

                    var startInfo = new ProcessStartInfo(executable)
                    {
                        WorkingDirectory = directory,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    token = sessionToken = NewRandomHex();
                    startInfo.EnvironmentVariables["SERVICEV14_SAT_TOKEN"] = sessionToken;
                    process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                    process.Exited += OnProcessExited;
                    if (!process.Start())
                        throw new InvalidOperationException("无法启动 SAT 宿主进程。");
                }

                var elapsed = Stopwatch.StartNew();
                while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
                {
                    lock (gate)
                    {
                        if (!IsRunning())
                            return;
                    }

                    if (await VerifyHealthAsync(sessionToken).ConfigureAwait(false))
                    {
                        lock (gate)
                            ready = IsRunning();
                        return;
                    }

                    await Task.Delay(250).ConfigureAwait(false);
                }

                Trace.TraceWarning("SAT 宿主未在启动期限内通过健康检查，请检查 5737 端口及部署依赖。");
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("SAT 宿主启动失败：{0}", ex);
            }
        }

        internal Task<HttpResponseMessage> GetAdaptersAsync(CancellationToken cancellationToken) =>
            ForwardAsync(HttpMethod.Get, "api/sat/adapters", null, TimeSpan.FromSeconds(10), cancellationToken);

        internal Task<HttpResponseMessage> ScanAsync(string adapter, CancellationToken cancellationToken) =>
            ForwardAsync(HttpMethod.Post, "api/sat/scan", new { adapter }, TimeSpan.FromSeconds(90), cancellationToken);

        private async Task<HttpResponseMessage> ForwardAsync(HttpMethod method, string path, object body,
            TimeSpan requestTimeout, CancellationToken cancellationToken)
        {
            string sessionToken;
            lock (gate)
            {
                if (!ready || !IsRunning())
                    return Error(HttpStatusCode.ServiceUnavailable, "SAT 宿主未就绪或未运行。");
                sessionToken = token;
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var request = new HttpRequestMessage(method, path))
            {
                timeout.CancelAfter(requestTimeout);
                request.Headers.Add("X-Sat-Token", sessionToken);
                if (body != null)
                    request.Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                try
                {
                    using (var upstream = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                    {
                        var content = upstream.Content;
                        var response = new HttpResponseMessage(upstream.StatusCode);
                        if (content != null)
                        {
                            response.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync().ConfigureAwait(false));
                            response.Content.Headers.ContentType = content.Headers.ContentType;
                        }
                        return response;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Error(HttpStatusCode.GatewayTimeout, "SAT 宿主响应超时。");
                }
                catch (HttpRequestException ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (timeout.IsCancellationRequested)
                        return Error(HttpStatusCode.GatewayTimeout, "SAT 宿主响应超时。");
                    Trace.TraceWarning("无法调用 SAT 宿主：{0}", ex.Message);
                    return Error(HttpStatusCode.ServiceUnavailable, "无法连接 SAT 宿主。");
                }
            }
        }

        internal static HttpResponseMessage Error(HttpStatusCode status, string message) => new HttpResponseMessage(status)
        {
            Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(new { message }), Encoding.UTF8, "application/json")
        };

        private static async Task<bool> VerifyHealthAsync(string sessionToken)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
            using (var request = new HttpRequestMessage(HttpMethod.Get, "health"))
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sessionToken)))
            {
                var challenge = NewRandomHex();
                var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.ASCII.GetBytes(challenge)));
                request.Headers.Add("X-Sat-Challenge", challenge);
                try
                {
                    using (var response = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                    {
                        return response.StatusCode == HttpStatusCode.OK
                            && response.Headers.TryGetValues("X-Sat-Proof", out var values)
                            && string.Equals(values.SingleOrDefault(), expected, StringComparison.Ordinal);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException)
                {
                    return false;
                }
            }
        }

        private static string NewRandomHex()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "");
        }

        private bool IsRunning() => !disposed && process != null && !process.HasExited;

        private void OnProcessExited(object sender, EventArgs args)
        {
            lock (gate)
            {
                ready = false;
                if (!disposed)
                    Trace.TraceWarning("SAT 宿主已退出；现有 TIA API 不受影响。");
            }
        }

        public void Dispose()
        {
            Process child;
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                ready = false;
                token = null;
                child = process;
                process = null;
            }

            if (child == null)
                return;

            using (child)
            {
                child.Exited -= OnProcessExited;
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill();
                        if (!child.WaitForExit(2000))
                            Trace.TraceWarning("等待 SAT 宿主退出超时。");
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    Trace.TraceWarning("停止 SAT 宿主失败：{0}", ex.Message);
                }
            }
        }
    }
}
