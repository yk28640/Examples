using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace WindowsServiceV14
{
    /// <summary>可持久化的任务快照；State 依次表示排队、运行以及成功或失败。</summary>
    public sealed class BlockComparisonTask
    {
        public string Id { get; set; }
        public string ExportId { get; set; }
        public string State { get; set; }
        public string Message { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public int? ExitCode { get; set; }
    }

    /// <summary>队列或任务保留数量达到配置上限，由接口映射为 HTTP 429。</summary>
    internal sealed class ComparisonCapacityException : Exception
    {
        internal ComparisonCapacityException() : base("比较队列或报告存储已达上限，请稍后重试或清理过期报告。") { }
    }

    /// <summary>按需创建的比较任务服务，负责有界队列、持久化状态与报告回收。</summary>
    internal sealed class BlockComparisonService : IDisposable
    {
        // HostGate 保护单例生命周期；gate 保护任务字典及相关磁盘状态。
        private static readonly object HostGate = new object();
        private static BlockComparisonService instance;
        private static bool stopping;
        private readonly object gate = new object();
        private readonly Dictionary<string, BlockComparisonTask> tasks = new Dictionary<string, BlockComparisonTask>();
        private readonly BlockingCollection<BlockComparisonTask> queue;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly Task[] workers;
        private readonly BlockComparisonOptions settings;
        private readonly string root;

        /// <summary>允许当前宿主接受任务，但不立即创建队列、工作线程或读取任务目录。</summary>
        internal static void StartHost()
        {
            lock (HostGate) stopping = false;
        }

        /// <summary>首次访问时恢复任务并启动工作线程，宿主停止后禁止重新创建。</summary>
        internal static BlockComparisonService Current
        {
            get
            {
                lock (HostGate)
                {
                    if (stopping) throw new InvalidOperationException("比较服务正在停止，请稍后重试。");
                    return instance ?? (instance = new BlockComparisonService());
                }
            }
        }

        /// <summary>先阻止新请求获取实例，再在生命周期锁外停止原服务。</summary>
        internal static void StopHost()
        {
            BlockComparisonService service;
            lock (HostGate)
            {
                stopping = true;
                service = instance;
                instance = null;
            }
            service?.Dispose();
        }

        /// <summary>恢复尚未过期的任务记录，并按配置创建固定数量的消费者。</summary>
        private BlockComparisonService()
        {
            settings = TagProjectConfiguration.LoadBlockComparison();
            root = Path.Combine(BlockExportStore.Root, "jobs");
            Directory.CreateDirectory(root);
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var id = Path.GetFileName(directory);
                if (!BlockExportStore.IsId(id)) continue;
                var path = Path.Combine(directory, "task.json");
                if (!File.Exists(path)) { BlockExportStore.DeleteDirectory(directory); continue; }
                try
                {
                    var task = JsonConvert.DeserializeObject<BlockComparisonTask>(File.ReadAllText(path));
                    if (task == null || task.Id != id || task.ExpiresUtc <= DateTime.UtcNow)
                    {
                        BlockExportStore.DeleteDirectory(directory);
                        continue;
                    }
                    // 重启不会自动重跑中断任务，避免重复调用外部工具。
                    if (task.State != "Succeeded" && task.State != "Failed")
                    {
                        task.State = "Failed";
                        task.Message = "服务重启导致比较中断，请重新点击修改重试。";
                        Save(task);
                    }
                    tasks.Add(id, task);
                }
                catch (JsonException ex)
                {
                    System.Diagnostics.Trace.TraceWarning("比较任务记录无效: {0}", ex);
                    BlockExportStore.DeleteDirectory(directory);
                }
            }
            queue = new BlockingCollection<BlockComparisonTask>(settings.QueueCapacity);
            workers = Enumerable.Range(0, settings.MaxConcurrency).Select(index =>
                Task.Factory.StartNew(Work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        }

        /// <summary>为导出创建或复用任务；持久化和入队失败时回滚新建目录及内存记录。</summary>
        internal BlockComparisonTask Create(BlockExportManifest manifest)
        {
            lock (gate)
            {
                if (shutdown.IsCancellationRequested) throw new InvalidOperationException("比较服务正在停止。");
                Cleanup();
                // 同一导出复用活动任务或有效报告；新建后十秒内也复用，抑制快速重复请求。
                var existing = tasks.Values.Where(item => item.ExportId == manifest.Id)
                    .OrderByDescending(item => item.CreatedUtc).FirstOrDefault();
                if (existing != null && (existing.State == "Queued" || existing.State == "Running"
                    || (existing.State == "Succeeded" && HasReport(existing.Id))
                    || existing.CreatedUtc.AddSeconds(10) > DateTime.UtcNow)) return Copy(existing);
                if (tasks.Count >= settings.MaxJobs || queue.Count >= settings.QueueCapacity)
                    throw new ComparisonCapacityException();
                var task = new BlockComparisonTask
                {
                    Id = Guid.NewGuid().ToString("N"), ExportId = manifest.Id, State = "Queued",
                    Message = "等待后端比较。", CreatedUtc = DateTime.UtcNow,
                    ExpiresUtc = DateTime.UtcNow.AddHours(settings.RetentionHours)
                };
                Directory.CreateDirectory(Path.Combine(root, task.Id));
                try
                {
                    Save(task);
                    tasks.Add(task.Id, task);
                    if (!queue.TryAdd(task)) throw new ComparisonCapacityException();
                }
                catch
                {
                    tasks.Remove(task.Id);
                    BlockExportStore.DeleteDirectory(Path.Combine(root, task.Id));
                    throw;
                }
                return Copy(task);
            }
        }

        /// <summary>返回未过期任务的副本，避免调用方持有内部可变任务对象。</summary>
        internal BlockComparisonTask Get(string id)
        {
            if (!BlockExportStore.IsId(id)) return null;
            lock (gate)
            {
                Cleanup();
                BlockComparisonTask task;
                return tasks.TryGetValue(id, out task) && task.ExpiresUtc > DateTime.UtcNow ? Copy(task) : null;
            }
        }

        /// <summary>仅为成功且未过期的任务打开报告；调用方负责释放返回的流。</summary>
        internal Stream OpenReport(string id)
        {
            if (!BlockExportStore.IsId(id)) return null;
            lock (gate)
            {
                Cleanup();
                BlockComparisonTask task;
                if (!tasks.TryGetValue(id, out task) || task.ExpiresUtc <= DateTime.UtcNow
                    || task.State != "Succeeded" || !File.Exists(ReportPath(id))) return null;
                BlockComparisonRunner.ValidatePdf(ReportPath(id), settings.MaxFileMegabytes * 1024L * 1024L);
                // 允许清理逻辑删除目录项，不以正在下载为由长期占用过期报告。
                return new FileStream(ReportPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
        }

        private string ReportPath(string id) => Path.Combine(root, id, "report.pdf");

        /// <summary>复用成功任务前重新检查 PDF，丢失或损坏的报告不视为可复用。</summary>
        private bool HasReport(string id)
        {
            try
            {
                BlockComparisonRunner.ValidatePdf(ReportPath(id), settings.MaxFileMegabytes * 1024L * 1024L);
                return true;
            }
            catch (InvalidOperationException) { return false; }
            catch (IOException) { return false; }
        }

        /// <summary>持续消费队列；外部比较在任务锁外执行，状态变更在锁内持久化。</summary>
        private void Work()
        {
            try
            {
                foreach (var task in queue.GetConsumingEnumerable(shutdown.Token))
                {
                    try
                    {
                        lock (gate)
                        {
                            task.State = "Running";
                            task.Message = "后端正在运行 ACTCLI。";
                            Save(task);
                        }
                        // 排队期间工程可能变化，执行前再次校验已发布导出，不自动重建 XML。
                        var manifest = MainController.GetPublishedExport(task.ExportId);
                        if (manifest == null) throw new InvalidOperationException("导出文件或工程版本已失效，请刷新历史后重试。");
                        var exitCode = BlockComparisonRunner.Run(settings,
                            BlockExportStore.XmlPath(manifest, false), BlockExportStore.XmlPath(manifest, true),
                            Path.Combine(root, task.Id), shutdown.Token);
                        lock (gate)
                        {
                            // Run 已验证 PDF；非零退出码保留在任务中，提示用户核对报告。
                            task.ExitCode = exitCode;
                            task.State = "Succeeded";
                            task.Message = exitCode == 0 ? "PDF 报告已生成。"
                                : "PDF 已生成，但 ACTCLI 返回非零退出码 " + exitCode + "，请核对报告和服务端日志。";
                            Save(task);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceError("程序块比较失败 {0}: {1}", task.Id, ex);
                        lock (gate)
                        {
                            task.State = "Failed";
                            task.Message = ex is OperationCanceledException ? "服务停止，比较已中断，请重试。"
                                : ex is TimeoutException || ex is InvalidOperationException ? ex.Message
                                : "后端比较失败，请检查 ACTCLI 安装、运行账户权限及服务端日志。";
                            try { Save(task); }
                            catch (Exception saveError) { System.Diagnostics.Trace.TraceError("保存比较失败状态失败: {0}", saveError); }
                        }
                        try { if (File.Exists(ReportPath(task.Id))) File.Delete(ReportPath(task.Id)); }
                        catch (Exception cleanupError) { System.Diagnostics.Trace.TraceWarning("清理失败报告失败: {0}", cleanupError); }
                    }
                    finally
                    {
                        foreach (var name in new[] { "previous.xml", "latest.xml" })
                        {
                            try { File.Delete(Path.Combine(root, task.Id, name)); }
                            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("清理任务 XML 副本失败: {0}", ex); }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>在任务锁内按需回收过期的终态任务；活动任务不在此处删除。</summary>
        private void Cleanup()
        {
            foreach (var task in tasks.Values.Where(task => task.State != "Queued" && task.State != "Running"
                && task.ExpiresUtc <= DateTime.UtcNow).ToArray())
            {
                BlockExportStore.DeleteDirectory(Path.Combine(root, task.Id));
                if (!Directory.Exists(Path.Combine(root, task.Id))) tasks.Remove(task.Id);
            }
        }

        /// <summary>先写临时文件，再替换正式状态，避免读到只写了一部分的 JSON。</summary>
        private void Save(BlockComparisonTask task)
        {
            var path = Path.Combine(root, task.Id, "task.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(task), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        /// <summary>复制任务的对外字段，不向请求线程暴露内部对象。</summary>
        private static BlockComparisonTask Copy(BlockComparisonTask task)
        {
            return new BlockComparisonTask
            {
                Id = task.Id, ExportId = task.ExportId, State = task.State, Message = task.Message,
                CreatedUtc = task.CreatedUtc, ExpiresUtc = task.ExpiresUtc, ExitCode = task.ExitCode
            };
        }

        /// <summary>取消消费者及运行中的比较，并将仍在排队的任务保存为失败。</summary>
        public void Dispose()
        {
            lock (gate)
            {
                shutdown.Cancel();
                queue.CompleteAdding();
            }
            // 等待放在任务锁外，让工作线程能够保存退出状态。
            if (!Task.WaitAll(workers, 15000))
                System.Diagnostics.Trace.TraceError("比较工作线程未在停止期限内退出。");
            lock (gate)
            {
                foreach (var task in tasks.Values.Where(task => task.State == "Queued"))
                {
                    task.State = "Failed";
                    task.Message = "服务停止导致排队任务中断，请重试。";
                    try { Save(task); }
                    catch (Exception ex) { System.Diagnostics.Trace.TraceError("保存中断任务失败: {0}", ex); }
                }
            }
        }
    }
}
