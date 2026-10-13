using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web.Http;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;

namespace WindowsServiceV14
{
    public class MainController : ApiController
    {
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

        private static List<PlcComparison> CompareProjects(FileInfo latestFile, FileInfo previousFile)
        {
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

        private static Dictionary<string, PlcSoftware> FindPlcs(Project project)
        {
            var plcs = new Dictionary<string, PlcSoftware>(StringComparer.OrdinalIgnoreCase);
            foreach (Device device in project.Devices)
                foreach (DeviceItem item in device.DeviceItems)
                    FindPlcs(item, device.Name, plcs);
            return plcs;
        }

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

        private static Dictionary<string, DateTime> FindBlocks(PlcSoftware plcSoftware)
        {
            var blocks = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            AppendBlocks(plcSoftware.BlockGroup, "", blocks);
            return blocks;
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

        public class PlcComparison
        {
            public string Device { get; set; }
            public ComparisonNode Result { get; set; }
        }

        private class PlcPairingException : Exception
        {
            public PlcPairingException(string message) : base(message) { }
        }

        public class ComparisonNode
        {
            public string LeftName { get; set; }
            public string RightName { get; set; }
            public string ComparisonResult { get; set; }
            public DateTime? LatestModifiedDate { get; set; }
            public DateTime? PreviousModifiedDate { get; set; }
            public List<ComparisonNode> Elements { get; set; }
        }

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

        private static void AppendGroupTags(List<TagInfo> tags, PlcTagTableUserGroup group,
            string deviceName, string groupPath)
        {
            AppendTableTags(tags, group.TagTables, deviceName, groupPath);
            foreach (PlcTagTableUserGroup child in group.Groups)
                AppendGroupTags(tags, child, deviceName, groupPath + "/" + child.Name);
        }

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
