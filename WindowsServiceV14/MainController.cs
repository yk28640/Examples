using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Web.Http;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;

namespace WindowsServiceV14
{
    public class MainController : ApiController
    {
        private static readonly Lazy<List<TagInfo>> CachedTags = new Lazy<List<TagInfo>>(
            () => ReadTags(TagProjectConfiguration.LoadProjectPath()),
            LazyThreadSafetyMode.ExecutionAndPublication);

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
                return Ok(CachedTags.Value);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("读取博途工程标签失败: {0}", ex);
                return Content(HttpStatusCode.ServiceUnavailable,
                    "无法读取博途工程的 PLC 变量表标签，请检查服务端日志。");
            }
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
