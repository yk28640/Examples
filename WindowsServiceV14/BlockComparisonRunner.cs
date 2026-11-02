using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WindowsServiceV14
{
    /// <summary>外部 ACTCLI 的路径、并发、超时及磁盘保留限制。</summary>
    internal sealed class BlockComparisonOptions
    {
        public string ExecutablePath { get; set; } = @"C:\Program Files\Siemens\Automation\SIMATIC Automation Compare Tool\app-1.4.0\ACT-CLI\ACTCLI.exe";
        public int TimeoutSeconds { get; set; } = 180;
        public int MaxConcurrency { get; set; } = 1;
        public int QueueCapacity { get; set; } = 8;
        public int RetentionHours { get; set; } = 168;
        public int MaxJobs { get; set; } = 100;
        public int MaxFileMegabytes { get; set; } = 20;

        /// <summary>校验配置范围和可执行文件名称；文件是否存在在实际执行时检查。</summary>
        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(ExecutablePath) || !Path.IsPathRooted(ExecutablePath)
                || !string.Equals(Path.GetFileName(ExecutablePath), "ACTCLI.exe", StringComparison.OrdinalIgnoreCase)
                || TimeoutSeconds < 5 || TimeoutSeconds > 1800 || MaxConcurrency < 1 || MaxConcurrency > 4
                || QueueCapacity < 1 || QueueCapacity > 32 || RetentionHours < 1 || RetentionHours > 720
                || MaxJobs < 1 || MaxJobs > 1000 || MaxFileMegabytes < 1 || MaxFileMegabytes > 100)
                throw new InvalidDataException("blockComparison 配置无效，请检查工具路径及资源限制。");
        }
    }

    /// <summary>在独立任务目录中运行 ACTCLI，并约束进程、输入和输出文件。</summary>
    internal static class BlockComparisonRunner
    {
        /// <summary>按 Windows 命令行引号规则转义单个参数，拒绝空字符及换行。</summary>
        internal static string Quote(string value)
        {
            if (value == null || value.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw new ArgumentException("命令参数无效。");
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }
            // 结尾反斜杠需要加倍，避免吞掉用于关闭参数的引号。
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        /// <summary>复制 XML、执行比较并验证 PDF；返回工具退出码，取消或失败时抛出异常。</summary>
        internal static int Run(BlockComparisonOptions settings, string previousXml, string latestXml,
            string directory, CancellationToken cancellationToken)
        {
            if (!File.Exists(settings.ExecutablePath))
                throw new InvalidOperationException("ACTCLI.exe 不存在，请配置后端安装路径。");
            var previous = Path.Combine(directory, "previous.xml");
            var latest = Path.Combine(directory, "latest.xml");
            var report = Path.Combine(directory, "report.pdf");
            var maxBytes = settings.MaxFileMegabytes * 1024L * 1024L;
            foreach (var input in new[] { previousXml, latestXml })
                if (!File.Exists(input) || new FileInfo(input).Length > maxBytes)
                    throw new InvalidOperationException("XML 缺失或超过大小限制，请刷新历史或检查配置。");
            // 每个任务使用自己的输入副本，避免外部工具直接操作共享导出文件。
            File.Copy(previousXml, previous);
            File.Copy(latestXml, latest);
            cancellationToken.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo
            {
                FileName = settings.ExecutablePath,
                Arguments = Quote(previous) + " " + Quote(latest) + "  -e " + Quote(report),
                WorkingDirectory = Path.GetDirectoryName(settings.ExecutablePath),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var job = new ProcessJob())
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                try { job.Attach(process); }
                catch { if (!process.HasExited) process.Kill(); throw; }
                // 同时持续读取两个重定向流，避免管道写满导致子进程阻塞。
                var output = Drain(process.StandardOutput);
                var error = Drain(process.StandardError);
                var clock = Stopwatch.StartNew();
                try
                {
                    // 轮询期间响应服务停止，并约束耗时与报告大小。
                    while (!process.WaitForExit(250))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (clock.Elapsed.TotalSeconds > settings.TimeoutSeconds)
                            throw new TimeoutException("ACTCLI 比较超时，请检查非交互运行能力或增加超时设置。");
                        if (File.Exists(report) && new FileInfo(report).Length > maxBytes)
                            throw new InvalidOperationException("比较报告超过大小限制。");
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var exitCode = process.ExitCode;
                    // 主进程退出后也回收 Job 中的残留进程，再等待输出流结束。
                    job.Dispose();
                    if (!Task.WaitAll(new Task[] { output, error }, 5000))
                        throw new InvalidOperationException("ACTCLI 输出流未正常关闭。");
                    Trace.TraceInformation("ACTCLI 退出码 {0}; stdout: {1}; stderr: {2}", exitCode, output.Result, error.Result);
                    ValidatePdf(report, maxBytes);
                    return exitCode;
                }
                finally
                {
                    job.Dispose();
                    try { if (!process.HasExited) process.WaitForExit(5000); }
                    catch (InvalidOperationException) { }
                    File.Delete(previous);
                    File.Delete(latest);
                }
            }
        }

        /// <summary>持续排空输出，仅保留前 8192 个字符用于日志，限制内存占用。</summary>
        private static async Task<string> Drain(StreamReader reader)
        {
            var buffer = new char[1024];
            var text = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                if (text.Length < 8192) text.Append(buffer, 0, Math.Min(count, 8192 - text.Length));
            return text.ToString();
        }

        /// <summary>检查文件大小、PDF 文件头及尾部结束标记；不进行完整 PDF 结构解析。</summary>
        internal static void ValidatePdf(string path, long maxBytes)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < 8 || file.Length > maxBytes)
                throw new InvalidOperationException("ACTCLI 未生成有效 PDF，或报告超过大小限制，请检查服务端日志。");
            using (var stream = File.OpenRead(path))
            {
                var header = new byte[5];
                if (stream.Read(header, 0, header.Length) != header.Length || Encoding.ASCII.GetString(header) != "%PDF-")
                    throw new InvalidOperationException("ACTCLI 输出不是 PDF 文件。");
                stream.Seek(-Math.Min(1024, stream.Length), SeekOrigin.End);
                using (var reader = new StreamReader(stream, Encoding.ASCII))
                    if (!reader.ReadToEnd().Contains("%%EOF"))
                        throw new InvalidOperationException("ACTCLI 输出的 PDF 不完整。");
            }
        }

        /// <summary>以 Windows Job Object 管理进程，关闭句柄时终止仍留在 Job 内的进程。</summary>
        private sealed class ProcessJob : IDisposable
        {
            private IntPtr handle;

            internal ProcessJob()
            {
                handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
                var limits = new ExtendedLimits();
                // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE；信息类别 9 对应扩展限制结构。
                limits.Basic.LimitFlags = 0x2000;
                if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf(typeof(ExtendedLimits))))
                {
                    Dispose();
                    throw new System.ComponentModel.Win32Exception();
                }
            }

            /// <summary>把已启动的进程加入 Job；失败时由调用方终止该进程。</summary>
            internal void Attach(Process process)
            {
                if (!AssignProcessToJobObject(handle, process.Handle))
                    throw new System.ComponentModel.Win32Exception();
            }

            public void Dispose()
            {
                if (handle == IntPtr.Zero) return;
                CloseHandle(handle);
                handle = IntPtr.Zero;
            }

            // 以下结构字段顺序和指针宽度须与 Win32 Job Object 原生布局一致。
            [StructLayout(LayoutKind.Sequential)]
            private struct BasicLimits
            {
                public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass, SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
                public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ExtendedLimits
            {
                public BasicLimits Basic;
                public IoCounters Io;
                public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimits info, int length);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}
