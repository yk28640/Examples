using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web.Http;
using Newtonsoft.Json;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.TechnologicalObjects;

namespace ServiceV14
{
    /// <summary>提供服务探测、PLC 标签读取、离线工程比较和持久化更改历史接口。</summary>
    public class MainController : ApiController
    {
        // 标签首次读取成功后在进程内复用；当前实现不自动按工程变动刷新标签缓存。
        private static readonly object TagsLock = new object();
        private static List<TagInfo> cachedTags;

        // 缓存格式或比较语义变化时，通过版本号拒绝复用旧历史。
        private const int HistoryCacheVersion = 3;
        // 串行化历史生成与已发布导出查询，保持内存、磁盘及导出引用的一致性。
        private static readonly object HistoryLock = new object();
        private static string cachedHistoryPath;
        private static ChangesHistoryDocument cachedHistory;

        /// <summary>仅检查 HTTP 服务可用性，不访问 TIA 工程。</summary>
        [HttpGet]
        [Route("test")]
        public HttpResponseMessage Get()
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Helloworld", Encoding.UTF8, "text/plain")
            };
        }

        /// <summary>获取配置工程的标签，首次读取由锁保护以避免并发重复打开工程。</summary>
        [HttpGet]
        [Route("getTags")]
        public IHttpActionResult GetTags()
        {
            try
            {
                lock (TagsLock)
                {
                    if (cachedTags == null)
                        cachedTags = ReadTags(TagProjectConfiguration.LoadProjectPath());

                    return Ok(cachedTags);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("读取博途工程标签失败: {0}", ex);
                return Content(HttpStatusCode.ServiceUnavailable,
                    "无法读取博途工程的 PLC 变量表标签，请检查服务端日志。");
            }
        }

        /// <summary>立即比较修改时间最新的两个工程；与历史接口不同，此接口每次执行比较。</summary>
        [HttpGet]
        [Route("compareOfflinePlcs")]
        public IHttpActionResult CompareOfflinePlcs()
        {
            try
            {
                var folder = TagProjectConfiguration.LoadComparasionFolder();
                var projects = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(path => string.Equals(Path.GetExtension(path), ".ap14", StringComparison.OrdinalIgnoreCase))
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                    .Take(2)
                    .ToArray();

                if (projects.Length < 2)
                    return BadRequest("比较目录中至少需要两个 .ap14 工程文件。");

                var comparisons = CompareProjects(projects[0], projects[1]);
                return Ok(new
                {
                    LatestProject = projects[0].FullName,
                    LatestModifiedUtc = projects[0].LastWriteTimeUtc,
                    PreviousProject = projects[1].FullName,
                    PreviousModifiedUtc = projects[1].LastWriteTimeUtc,
                    Comparisons = comparisons
                });
            }
            catch (InvalidDataException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (DirectoryNotFoundException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (PlcPairingException ex)
            {
                return Content(HttpStatusCode.Conflict, ex.Message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("离线工程比较失败: {0}", ex);
                return Content(HttpStatusCode.ServiceUnavailable, "无法比较离线工程" + ex.Message);
            }
        }

        /// <summary>获取或增量生成相邻工程版本的比较历史，优先复用内存及磁盘缓存。</summary>
        [HttpGet]
        [Route("GenerateChangesHistory")]
        public IHttpActionResult GenerateChangesHistory()
        {
            try
            {
                return Json(GetChangesHistory(TagProjectConfiguration.LoadComparasionFolder(), CompareProjects));
            }
            catch (InvalidDataException ex)
            {
                System.Diagnostics.Trace.TraceError("历史比较输入无效: {0}", ex);
                return BadRequest(ex.Message);
            }
            catch (DirectoryNotFoundException ex)
            {
                System.Diagnostics.Trace.TraceError("历史比较目录不存在: {0}", ex);
                return BadRequest("配置的比较目录不存在。");
            }
            catch (PlcPairingException ex)
            {
                System.Diagnostics.Trace.TraceError("历史比较 PLC 配对失败: {0}", ex);
                return Content(HttpStatusCode.Conflict, ex.Message);
            }
            catch (HistoryChangedException ex)
            {
                return Content(HttpStatusCode.Conflict, ex.Message);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("生成工程更改历史失败: {0}", ex);
                return Content(HttpStatusCode.ServiceUnavailable, "无法生成工程更改历史，请检查服务端日志。");
            }
        }

        /// <summary>按工程指纹复用历史，仅对缺少结果的相邻版本调用比较委托。</summary>
        private static ChangesHistoryDocument GetChangesHistory(string folder,
            Func<FileInfo, FileInfo, List<PlcComparison>> compareProjects)
        {
            lock (HistoryLock)
            {
                folder = Path.GetFullPath(folder);
                var cachePath = Path.Combine(folder, "ChangesHistory.json");
                var projects = GetHistoryProjects(folder);
                if (projects.Count < 2)
                    throw new InvalidDataException("比较目录中至少需要两个 .ap14 工程文件。");

                // 同一路径优先复用内存，否则读取服务端 JSON；正常请求不强制重新比较或导出。
                var existing = cachedHistory != null && string.Equals(cachedHistoryPath, cachePath, StringComparison.OrdinalIgnoreCase)
                    ? cachedHistory : LoadHistoryCache(cachePath);
                if (existing != null && existing.Version != HistoryCacheVersion)
                    existing = null;
                var reusable = new Dictionary<string, ChangesHistoryEntry>(StringComparer.Ordinal);
                if (existing != null)
                {
                    foreach (var entry in existing.Entries)
                    {
                        if (entry != null && entry.PreviousProject != null && entry.LatestProject != null
                            && entry.Comparisons != null && entry.Comparisons.Count > 0
                            && entry.Comparisons.All(item => item != null && item.Result != null))
                            reusable[GetHistoryPairKey(entry.PreviousProject, entry.LatestProject)] = entry;
                    }
                }

                // 复用键包含新旧两份工程的完整指纹，防止把旧版本结果用于新文件。
                var changed = existing == null || !SameHistoryProjects(existing.Projects, projects)
                    || existing.Entries.Count != projects.Count - 1;
                if (!changed && Enumerable.Range(1, projects.Count - 1).All(index =>
                    reusable.ContainsKey(GetHistoryPairKey(projects[index - 1], projects[index]))))
                {
                    // 历史完全命中时只补回缺失的 JSON；不因 XML 丢失自动重建导出。
                    if (!File.Exists(cachePath)) SaveHistoryCache(cachePath, existing);
                    cachedHistoryPath = cachePath;
                    cachedHistory = existing;
                    return existing;
                }
                var entries = new List<ChangesHistoryEntry>();
                var newExports = new List<string>();
                var published = false;
                try
                {
                    for (var i = 1; i < projects.Count; i++)
                    {
                        var previous = projects[i - 1];
                        var latest = projects[i];
                        ChangesHistoryEntry entry;
                        if (!reusable.TryGetValue(GetHistoryPairKey(previous, latest), out entry))
                        {
                            var comparisons = compareProjects(new FileInfo(latest.Path), new FileInfo(previous.Path));
                            newExports.AddRange(comparisons.SelectMany(item => item.Result.Elements)
                                .Where(node => node.ExportId != null).Select(node => node.ExportId));
                            entry = new ChangesHistoryEntry
                            {
                                PreviousProject = previous,
                                LatestProject = latest,
                                ComparedAtUtc = DateTime.UtcNow,
                                Comparisons = comparisons
                            };
                            changed = true;
                        }
                        entries.Add(entry);
                    }
                    // 比较可能耗时，发布前重新扫描目录，拒绝把跨版本结果写入历史。
                    if (!SameHistoryProjects(projects, GetHistoryProjects(folder)))
                        throw new HistoryChangedException("比较期间工程文件发生变化，未更新历史缓存，请稍后重试。");

                    var document = new ChangesHistoryDocument
                    {
                        Version = HistoryCacheVersion,
                        GeneratedAtUtc = changed ? DateTime.UtcNow : existing.GeneratedAtUtc,
                        Projects = projects,
                        Entries = entries
                    };
                    if (changed || !File.Exists(cachePath))
                        SaveHistoryCache(cachePath, document);

                    cachedHistoryPath = cachePath;
                    cachedHistory = document;
                    published = true;
                    return document;
                }
                finally
                {
                    // 只回收本次新导出的文件，不能删除复用历史仍引用的导出。
                    if (!published)
                        foreach (var id in newExports) BlockExportStore.Delete(id);
                }
            }
        }

        /// <summary>确认导出清单与历史中的设备、块名称及工程版本对一致。</summary>
        private static bool ExportMatches(ChangesHistoryEntry entry, string device, ComparisonNode node)
        {
            var manifest = BlockExportStore.Load(node.ExportId);
            return manifest != null && manifest.Device == device && manifest.BlockName == node.LeftName
                && GetHistoryPairKey(manifest.PreviousProject, manifest.LatestProject)
                    == GetHistoryPairKey(entry.PreviousProject, entry.LatestProject);
        }

        /// <summary>只返回当前有效历史引用的导出；历史更新中快速拒绝，不等待或触发重建。</summary>
        internal static BlockExportManifest GetPublishedExport(string id)
        {
            if (!BlockExportStore.IsId(id)) return null;
            // 不在工作线程中长时间等待历史比较，交由调用方提示稍后重试。
            if (!System.Threading.Monitor.TryEnter(HistoryLock))
                throw new InvalidOperationException("历史正在更新，请稍后重试。");
            try
            {
                var folder = TagProjectConfiguration.LoadComparasionFolder();
                var path = Path.Combine(Path.GetFullPath(folder), "ChangesHistory.json");
                var document = string.Equals(path, cachedHistoryPath, StringComparison.OrdinalIgnoreCase)
                    ? cachedHistory : LoadHistoryCache(path);
                if (document == null || document.Version != HistoryCacheVersion
                    || !SameHistoryProjects(document.Projects, GetHistoryProjects(folder))) return null;
                foreach (var entry in document.Entries)
                {
                    if (entry?.Comparisons == null) continue;
                    foreach (var item in entry.Comparisons.Where(item => item?.Result?.Elements != null))
                        foreach (var node in item.Result.Elements.Where(IsModifiedBlock))
                            if (node.ExportId == id && ExportMatches(entry, item.Device, node))
                                return BlockExportStore.Load(id);
                }
                return null;
            }
            finally { System.Threading.Monitor.Exit(HistoryLock); }
        }

        /// <summary>表示比较期间来源文件发生变化，结果不能作为稳定历史发布。</summary>
        private class HistoryChangedException : Exception
        {
            public HistoryChangedException(string message) : base(message) { }
        }

        /// <summary>读取当前格式的历史 JSON；格式无效时返回空值，由生成流程决定重建。</summary>
        private static ChangesHistoryDocument LoadHistoryCache(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                var document = JsonConvert.DeserializeObject<ChangesHistoryDocument>(
                    File.ReadAllText(path, Encoding.UTF8));
                if (document == null || document.Version != HistoryCacheVersion
                    || document.Projects == null || document.Entries == null)
                {
                    System.Diagnostics.Trace.TraceWarning("历史缓存格式或版本无效，将重新生成: {0}", path);
                    return null;
                }
                return document;
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Trace.TraceWarning("历史缓存无法解析，将重新生成: {0}", ex);
                return null;
            }
        }

        /// <summary>先写同目录临时文件，再替换正式历史，避免发布半写入的 JSON。</summary>
        private static void SaveHistoryCache(string path, ChangesHistoryDocument document)
        {
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(document, Formatting.Indented),
                    new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(temporaryPath, path, null);
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning("清理历史缓存临时文件失败: {0}", ex);
                }
            }
        }

        /// <summary>递归扫描 .ap14 工程，按 UTC 修改时间和路径稳定排序，以便比较相邻版本。</summary>
        private static List<HistoryProjectFile> GetHistoryProjects(string folder)
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".ap14", StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .Select(file => new HistoryProjectFile
                {
                    Path = file.FullName,
                    ModifiedUtc = file.LastWriteTimeUtc,
                    Length = file.Length
                })
                .OrderBy(file => file.ModifiedUtc)
                .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>比较有序工程指纹列表；不读取工程内部内容。</summary>
        private static bool SameHistoryProjects(List<HistoryProjectFile> left, List<HistoryProjectFile> right)
        {
            return left != null && right != null
                && string.Equals(JsonConvert.SerializeObject(left), JsonConvert.SerializeObject(right),
                    StringComparison.Ordinal);
        }

        /// <summary>序列化有方向的新旧版本对，作为历史复用和导出归属校验键。</summary>
        private static string GetHistoryPairKey(HistoryProjectFile previous, HistoryProjectFile latest)
        {
            return JsonConvert.SerializeObject(new[] { previous, latest });
        }

        /// <summary>工程文件的轻量版本指纹；路径、时间和长度均参与历史复用判断。</summary>
        public class HistoryProjectFile
        {
            public string Path { get; set; }
            public DateTime ModifiedUtc { get; set; }
            public long Length { get; set; }
        }

        /// <summary>一对相邻版本的比较结果及实际比较时间。</summary>
        public class ChangesHistoryEntry
        {
            public HistoryProjectFile PreviousProject { get; set; }
            public HistoryProjectFile LatestProject { get; set; }
            public DateTime ComparedAtUtc { get; set; }
            public List<PlcComparison> Comparisons { get; set; }
        }

        /// <summary>同时用于磁盘持久化和 API 返回的完整历史文档。</summary>
        public class ChangesHistoryDocument
        {
            public int Version { get; set; }
            public DateTime GeneratedAtUtc { get; set; }
            public List<HistoryProjectFile> Projects { get; set; }
            public List<ChangesHistoryEntry> Entries { get; set; }
        }

        /// <summary>执行两工程比较，异常退出时回收本次已记录的新导出。</summary>
        private static List<PlcComparison> CompareProjects(FileInfo latestFile, FileInfo previousFile)
        {
            var exports = new List<string>();
            try { return CompareProjectsCore(latestFile, previousFile, exports); }
            catch
            {
                foreach (var id in exports) BlockExportStore.Delete(id);
                throw;
            }
        }

        /// <summary>按设备配对 PLC，生成对象差异，并为两侧均存在的修改块导出 XML。</summary>
        private static List<PlcComparison> CompareProjectsCore(FileInfo latestFile, FileInfo previousFile, List<string> exports)
        {
            var latestVersion = BlockExportStore.Fingerprint(latestFile);
            var previousVersion = BlockExportStore.Fingerprint(previousFile);
            // V14 SP1 的一个 TiaPortal 实例只能打开一个工程，两份离线工程必须使用独立实例。
            using (var latestPortal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
            using (var previousPortal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
            {
                var latestProject = latestPortal.Projects.Open(latestFile);
                try
                {
                    var previousProject = previousPortal.Projects.Open(previousFile);
                    try
                    {
                        var latestPlcs = FindPlcs(latestProject);
                        var previousPlcs = FindPlcs(previousProject);
                        if (latestPlcs.Count == 0 || previousPlcs.Count == 0)
                            throw new PlcPairingException("工程中未找到 PLC 软件。");

                        if (!latestPlcs.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                            .SequenceEqual(previousPlcs.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
                                StringComparer.OrdinalIgnoreCase))
                            throw new PlcPairingException("两份工程的 PLC 设备名称不一致，无法可靠地配对比较。");

                        var comparisons = new List<PlcComparison>();
                        foreach (var pair in latestPlcs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            var latestObjects = FindComparisonObjects(pair.Value);
                            var previousObjects = FindComparisonObjects(previousPlcs[pair.Key]);
                            var result = CompareObjects(pair.Key, latestObjects, previousObjects);
                            foreach (var node in result.Elements.Where(IsModifiedBlock))
                            {
                                try
                                {
                                    node.ExportId = BlockExportStore.Export(pair.Key, node.LeftName,
                                        previousObjects["Block/" + node.RightName].Block,
                                        latestObjects["Block/" + node.LeftName].Block,
                                        previousVersion, latestVersion);
                                    exports.Add(node.ExportId);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Trace.TraceError("导出修改程序块失败 {0}/{1}: {2}", pair.Key, node.LeftName, ex);
                                    // 单个块导出失败仍保留比较结果，并向调用方标记该块无法继续比较。
                                    node.ExportError = "无法导出新旧程序块 XML，请检查块保护状态和服务端日志。";
                                }
                            }
                            comparisons.Add(new PlcComparison
                            {
                                Device = pair.Key,
                                Result = result
                            });
                        }
                        if (!BlockExportStore.Unchanged(latestVersion) || !BlockExportStore.Unchanged(previousVersion))
                        {
                            foreach (var node in comparisons.SelectMany(item => item.Result.Elements))
                                BlockExportStore.Delete(node.ExportId);
                            throw new HistoryChangedException("比较期间工程文件发生变化，请重新生成历史。");
                        }
                        return comparisons;
                    }
                    finally
                    {
                        previousProject.Close();
                    }
                }
                finally
                {
                    latestProject.Close();
                }
            }
        }

        /// <summary>递归收集工程中的 PLC 软件，以不区分大小写的设备名建立配对索引。</summary>
        private static Dictionary<string, PlcSoftware> FindPlcs(Project project)
        {
            var plcs = new Dictionary<string, PlcSoftware>(StringComparer.OrdinalIgnoreCase);
            foreach (Device device in project.Devices)
                foreach (DeviceItem item in device.DeviceItems)
                    FindPlcs(item, device.Name, plcs);
            return plcs;
        }

        /// <summary>遍历设备项；同设备存在多个 PLC 软件时拒绝进行有歧义的配对。</summary>
        private static void FindPlcs(DeviceItem item, string deviceName, Dictionary<string, PlcSoftware> plcs)
        {
            var container = item.GetService<SoftwareContainer>();
            var plcSoftware = container?.Software as PlcSoftware;
            if (plcSoftware != null)
            {
                if (plcs.ContainsKey(deviceName))
                    throw new PlcPairingException("设备 " + deviceName + " 包含多个 PLC 软件，无法可靠地配对比较。");
                plcs.Add(deviceName, plcSoftware);
            }

            foreach (DeviceItem child in item.DeviceItems)
                FindPlcs(child, deviceName, plcs);
        }

        /// <summary>按对象键和修改时间生成增删改结果；不执行块内容的逐项语义比较。</summary>
        private static ComparisonNode CompareObjects(string deviceName,
            Dictionary<string, ComparisonObjectSnapshot> latestObjects,
            Dictionary<string, ComparisonObjectSnapshot> previousObjects)
        {
            var root = new ComparisonNode
            {
                LeftName = deviceName,
                RightName = deviceName,
                ComparisonResult = "Identical",
                Elements = new List<ComparisonNode>()
            };
            foreach (var key in latestObjects.Keys.Union(previousObjects.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase))
            {
                ComparisonObjectSnapshot latest;
                ComparisonObjectSnapshot previous;
                var inLatest = latestObjects.TryGetValue(key, out latest);
                var inPrevious = previousObjects.TryGetValue(key, out previous);
                // 左侧为最新版本，右侧为旧版本；同键对象仅依据 ModifiedDate 判断是否不同。
                var status = !inLatest ? "Removed" : !inPrevious ? "Added"
                    : latest.ModifiedDate == previous.ModifiedDate ? "Identical" : "Different";
                if (status != "Identical")
                    root.ComparisonResult = "Different";
                root.Elements.Add(new ComparisonNode
                {
                    ObjectType = (latest ?? previous).ObjectType,
                    LeftName = inLatest ? latest.Name : null,
                    RightName = inPrevious ? previous.Name : null,
                    ComparisonResult = status,
                    LatestModifiedDate = inLatest ? (DateTime?)latest.ModifiedDate : null,
                    PreviousModifiedDate = inPrevious ? (DateTime?)previous.ModifiedDate : null,
                    Elements = new List<ComparisonNode>()
                });
            }
            return root;
        }

        /// <summary>收集普通块及工艺对象，形成用于比较的轻量快照索引。</summary>
        private static Dictionary<string, ComparisonObjectSnapshot> FindComparisonObjects(PlcSoftware plcSoftware)
        {
            var objects = new Dictionary<string, ComparisonObjectSnapshot>(StringComparer.OrdinalIgnoreCase);
            AppendBlocks(plcSoftware.BlockGroup, "", objects);
            var group = plcSoftware.TechnologicalObjectGroup;
            if (group != null)
                foreach (TechnologicalInstanceDB technologicalObject in group.TechnologicalObjects)
                    AddComparisonObject(objects, technologicalObject.Name, "TechnologicalObject", technologicalObject.ModifiedDate);
            return objects;
        }

        /// <summary>递归块组，以组路径区分普通块，并保留当前工程内的块引用供 XML 导出。</summary>
        private static void AppendBlocks(PlcBlockGroup group, string groupPath,
            Dictionary<string, ComparisonObjectSnapshot> objects)
        {
            foreach (PlcBlock block in group.Blocks)
            { 
                var technologicalObject = block as TechnologicalInstanceDB;
                if (technologicalObject != null)
                    AddComparisonObject(objects, technologicalObject.Name, "TechnologicalObject", technologicalObject.ModifiedDate);
                else
                {
                    AddComparisonObject(objects, groupPath + block.Name, "Block", block.ModifiedDate);
                    objects["Block/" + groupPath + block.Name].Block = block;
                }
            }

            foreach (PlcBlockUserGroup child in group.Groups)
                AppendBlocks(child, groupPath + child.Name + "/", objects);
        }

        /// <summary>按类型和名称登记对象，去重一致的工艺对象，并拒绝其他重复或冲突。</summary>
        private static void AddComparisonObject(Dictionary<string, ComparisonObjectSnapshot> objects,
            string name, string objectType, DateTime modifiedDate)
        {
            var key = objectType + "/" + name;
            ComparisonObjectSnapshot existing;
            if (objects.TryGetValue(key, out existing))
            {
                // 工艺对象可能同时出现在块组和工艺对象组中，只统计一次。
                if (objectType == "TechnologicalObject" && existing.ModifiedDate == modifiedDate)
                    return;
                throw new PlcPairingException("存在重复或不一致的比较对象: " + name);
            }
            objects.Add(key, new ComparisonObjectSnapshot
            {
                Name = name,
                ObjectType = objectType,
                ModifiedDate = modifiedDate
            });
        }

        /// <summary>比较阶段的对象快照；Block 仅在所属工程保持打开时用于导出。</summary>
        private class ComparisonObjectSnapshot
        {
            public string Name { get; set; }
            public string ObjectType { get; set; }
            public DateTime ModifiedDate { get; set; }
            [JsonIgnore]
            public PlcBlock Block { get; set; }
        }

        /// <summary>一个设备的 PLC 比较结果。</summary>
        public class PlcComparison
        {
            public string Device { get; set; }
            public ComparisonNode Result { get; set; }
        }

        /// <summary>表示设备或对象无法建立可靠的一一对应关系。</summary>
        private class PlcPairingException : Exception
        {
            public PlcPairingException(string message) : base(message) { }
        }

        /// <summary>PLC 或对象级结果节点；块导出标识供异步比较接口使用。</summary>
        public class ComparisonNode
        {
            public string ObjectType { get; set; }
            public string ExportId { get; set; }
            public string ExportError { get; set; }
            /// <summary>最新版本中的对象名称；已删除对象没有左侧名称。</summary>
            public string LeftName { get; set; }
            /// <summary>旧版本中的对象名称；新增对象没有右侧名称。</summary>
            public string RightName { get; set; }
            public string ComparisonResult { get; set; }
            public DateTime? LatestModifiedDate { get; set; }
            public DateTime? PreviousModifiedDate { get; set; }
            public List<ComparisonNode> Elements { get; set; }
        }

        /// <summary>仅选取新旧两侧均存在且修改时间不同的普通块。</summary>
        private static bool IsModifiedBlock(ComparisonNode node)
        {
            return node != null && node.ObjectType == "Block" && node.ComparisonResult == "Different"
                && !string.IsNullOrEmpty(node.LeftName) && !string.IsNullOrEmpty(node.RightName);
        }

        /// <summary>短暂打开配置工程，将标签复制为普通数据后关闭工程和 TIA 实例。</summary>
        private static List<TagInfo> ReadTags(string projectPath)
        {
            var tags = new List<TagInfo>();
            using (var portal = new TiaPortal(TiaPortalMode.WithoutUserInterface))
            {
                var project = portal.Projects.Open(new FileInfo(projectPath));
                try
                {
                    foreach (Device device in project.Devices)
                        foreach (DeviceItem item in device.DeviceItems)
                            AppendDeviceTags(tags, item, device.Name);
                }
                finally
                {
                    project.Close();
                }
            }

            return tags;
        }

        /// <summary>递归设备项，读取各 PLC 的根标签表及用户分组。</summary>
        private static void AppendDeviceTags(List<TagInfo> tags, DeviceItem item, string deviceName)
        {
            var container = item.GetService<SoftwareContainer>();
            var plcSoftware = container?.Software as PlcSoftware;
            if (plcSoftware != null)
            {
                AppendTableTags(tags, plcSoftware.TagTableGroup.TagTables, deviceName, "");
                foreach (PlcTagTableUserGroup group in plcSoftware.TagTableGroup.Groups)
                    AppendGroupTags(tags, group, deviceName, group.Name);
            }

            foreach (DeviceItem child in item.DeviceItems)
                AppendDeviceTags(tags, child, deviceName);
        }

        /// <summary>递归标签表分组，并保留完整分组路径。</summary>
        private static void AppendGroupTags(List<TagInfo> tags, PlcTagTableUserGroup group,
            string deviceName, string groupPath)
        {
            AppendTableTags(tags, group.TagTables, deviceName, groupPath);
            foreach (PlcTagTableUserGroup child in group.Groups)
                AppendGroupTags(tags, child, deviceName, groupPath + "/" + child.Name);
        }

        /// <summary>将 TIA 标签转换为不依赖工程生命周期的 API 数据对象。</summary>
        private static void AppendTableTags(List<TagInfo> tags, PlcTagTableComposition tables,
            string deviceName, string groupPath)
        {
            foreach (PlcTagTable table in tables)
                foreach (PlcTag tag in table.Tags)
                    tags.Add(new TagInfo
                    {
                        Device = deviceName,
                        Group = groupPath,
                        Table = table.Name,
                        Name = tag.Name,
                        DataType = tag.DataTypeName,
                        Address = tag.LogicalAddress
                    });
        }

        /// <summary>PLC 标签的设备、分组、表名、数据类型及逻辑地址。</summary>
        public class TagInfo
        {
            public string Device { get; set; }
            public string Group { get; set; }
            public string Table { get; set; }
            public string Name { get; set; }
            public string DataType { get; set; }
            public string Address { get; set; }
        }
    }
}
