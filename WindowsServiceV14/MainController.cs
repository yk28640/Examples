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

namespace WindowsServiceV14
{
    /// <summary>提供服务探测、PLC 标签读取、离线工程比较和持久化更改历史接口。</summary>
    public class MainController : ApiController
    {
        // 标签首次读取成功后在进程内复用；当前实现不自动按工程变动刷新标签缓存。
        private static readonly object TagsLock = new object();
        private static List<TagInfo> cachedTags;

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

                var existing = string.Equals(cachedHistoryPath, cachePath, StringComparison.OrdinalIgnoreCase)
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

                var changed = existing == null || !SameHistoryProjects(existing.Projects, projects)
                    || existing.Entries.Count != projects.Count - 1;
                var entries = new List<ChangesHistoryEntry>();
                for (var i = 1; i < projects.Count; i++)
                {
                    var previous = projects[i - 1];
                    var latest = projects[i];
                    ChangesHistoryEntry entry;
                    if (!reusable.TryGetValue(GetHistoryPairKey(previous, latest), out entry))
                    {
                        var comparisons = compareProjects(new FileInfo(latest.Path), new FileInfo(previous.Path));
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
                return document;
            }
        }

        private class HistoryChangedException : Exception
        {
            public HistoryChangedException(string message) : base(message) { }
        }

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

        private static bool SameHistoryProjects(List<HistoryProjectFile> left, List<HistoryProjectFile> right)
        {
            return left != null && right != null
                && string.Equals(JsonConvert.SerializeObject(left), JsonConvert.SerializeObject(right),
                    StringComparison.Ordinal);
        }

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
                            var latestBlocks = FindBlocks(pair.Value);
                            var previousBlocks = FindBlocks(previousPlcs[pair.Key]);
                            var root = new ComparisonNode
                            {
                                LeftName = pair.Key,
                                RightName = pair.Key,
                                ComparisonResult = "Identical",
                                Elements = new List<ComparisonNode>()
                            };
                            foreach (var name in latestBlocks.Keys.Union(previousBlocks.Keys, StringComparer.OrdinalIgnoreCase)
                                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                            {
                                DateTime latestDate;
                                DateTime previousDate;
                                var inLatest = latestBlocks.TryGetValue(name, out latestDate);
                                var inPrevious = previousBlocks.TryGetValue(name, out previousDate);
                                var status = !inLatest ? "Removed" : !inPrevious ? "Added"
                                    : latestDate == previousDate ? "Identical" : "Different";
                                if (status != "Identical")
                                    root.ComparisonResult = "Different";

                                root.Elements.Add(new ComparisonNode
                                {
                                    LeftName = inLatest ? name : null,
                                    RightName = inPrevious ? name : null,
                                    ComparisonResult = status,
                                    LatestModifiedDate = inLatest ? (DateTime?)latestDate : null,
                                    PreviousModifiedDate = inPrevious ? (DateTime?)previousDate : null,
                                    Elements = new List<ComparisonNode>()
                                });
                            }
                            comparisons.Add(new PlcComparison
                            {
                                Device = pair.Key,
                                Result = root
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

        private static Dictionary<string, DateTime> FindBlocks(PlcSoftware plcSoftware)
        {
            var objects = new Dictionary<string, ComparisonObjectSnapshot>(StringComparer.OrdinalIgnoreCase);
            AppendBlocks(plcSoftware.BlockGroup, "", objects);
            var group = plcSoftware.TechnologicalObjectGroup;
            if (group != null)
                foreach (TechnologicalInstanceDB technologicalObject in group.TechnologicalObjects)
                    AddComparisonObject(objects, technologicalObject.Name, "TechnologicalObject", technologicalObject.ModifiedDate);
            return objects;
        }

        private static void AppendBlocks(PlcBlockGroup group, string groupPath, Dictionary<string, DateTime> blocks)
        {
            foreach (PlcBlock block in group.Blocks)
            {
                var name = groupPath + block.Name;
                if (blocks.ContainsKey(name))
                    throw new PlcPairingException("存在重复的程序块: " + name);
                blocks.Add(name, block.ModifiedDate);
            }

            foreach (PlcBlockUserGroup child in group.Groups)
                AppendBlocks(child, groupPath + child.Name + "/", blocks);
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
