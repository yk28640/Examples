using Newtonsoft.Json;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ServiceV14
{
    /// <summary>记录一对新旧程序块 XML 与来源工程版本，供后续比较任务定位输入文件。</summary>
    internal sealed class BlockExportManifest
    {
        public string Id { get; set; }
        public string Device { get; set; }
        public string BlockName { get; set; }
        public MainController.HistoryProjectFile PreviousProject { get; set; }
        public MainController.HistoryProjectFile LatestProject { get; set; }
        public string PreviousXml { get; set; }
        public string LatestXml { get; set; }
    }

    /// <summary>管理比较目录下的块导出文件；不负责打开工程或重建历史缓存。</summary>
    internal static class BlockExportStore
    {
        // 存储位置随服务端比较目录配置变化，不接受客户端传入的文件路径。
        internal static string Root => Path.Combine(TagProjectConfiguration.LoadComparasionFolder(), ".block-comparisons");

        /// <summary>仅接受不含分隔符的 N 格式 GUID，避免将任意字符串用于目录定位。</summary>
        internal static bool IsId(string id)
        {
            Guid value;
            return id != null && Guid.TryParseExact(id, "N", out value);
        }

        /// <summary>刷新文件信息并记录路径、长度和 UTC 修改时间；此指纹不是内容哈希。</summary>
        internal static MainController.HistoryProjectFile Fingerprint(FileInfo file)
        {
            file.Refresh();
            return new MainController.HistoryProjectFile
            {
                Path = file.FullName, ModifiedUtc = file.LastWriteTimeUtc, Length = file.Length
            };
        }

        /// <summary>按长度和修改时间检查工程是否仍与已记录版本一致。</summary>
        internal static bool Unchanged(MainController.HistoryProjectFile version)
        {
            var file = new FileInfo(version.Path);
            return file.Exists && file.Length == version.Length && file.LastWriteTimeUtc == version.ModifiedUtc;
        }

        /// <summary>编译并导出已打开工程中的新旧块，完整写入清单后发布目录并返回导出标识。</summary>
        internal static string Export(string device, string name, PlcBlock previous, PlcBlock latest,
            MainController.HistoryProjectFile previousProject, MainController.HistoryProjectFile latestProject)
        {
            var id = Guid.NewGuid().ToString("N");
            var parent = Path.Combine(Root, "exports");
            var temporary = Path.Combine(parent, id + ".tmp");
            var directory = Path.Combine(parent, id);
            Directory.CreateDirectory(temporary);
            try
            {
                // 名称仅用于可读文件名；真正的存储隔离依赖随机导出标识。
                var label = new string((device + "_" + name).Select(c =>
                    char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).Take(32).ToArray()).TrimEnd(' ', '.');
                var manifest = new BlockExportManifest
                {
                    Id = id, Device = device, BlockName = name,
                    PreviousProject = previousProject, LatestProject = latestProject,
                    PreviousXml = "block_" + label + "_previous_" + previousProject.ModifiedUtc.ToString("yyyyMMddHHmmss") + ".xml",
                    LatestXml = "block_" + label + "_latest_" + latestProject.ModifiedUtc.ToString("yyyyMMddHHmmss") + ".xml"
                };
                // 块引用依赖调用方保持两份工程打开；导出完成后只保存 XML，不保留 TIA 对象。
                var previouscompilable = previous.GetService<ICompilable>(); ;
                previouscompilable.Compile();
                previous.Export(new FileInfo(Path.Combine(temporary, manifest.PreviousXml)), ExportOptions.WithDefaults);
                var latestcompilable = latest.GetService<ICompilable>();
                latestcompilable.Compile();
                latest.Export(new FileInfo(Path.Combine(temporary, manifest.LatestXml)), ExportOptions.WithDefaults);
                if (new FileInfo(Path.Combine(temporary, manifest.PreviousXml)).Length == 0
                    || new FileInfo(Path.Combine(temporary, manifest.LatestXml)).Length == 0)
                    throw new InvalidDataException("导出的程序块 XML 为空。");
                File.WriteAllText(Path.Combine(temporary, "manifest.json"),
                    JsonConvert.SerializeObject(manifest, Formatting.Indented), new UTF8Encoding(false));
                // 两份 XML 与清单全部写完后才改为正式目录，避免读取到半成品。
                Directory.Move(temporary, directory);
                return id;
            }
            finally
            {
                // 发布成功时临时目录已移走；失败时尽力清理尚未发布的文件。
                DeleteDirectory(temporary);
            }
        }

        /// <summary>读取并校验已发布清单及非空 XML；不验证当前工程版本，也不重新导出。</summary>
        internal static BlockExportManifest Load(string id)
        {
            if (!IsId(id)) return null;
            var directory = Path.Combine(Root, "exports", id);
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path)) return null;
            try
            {
                var manifest = JsonConvert.DeserializeObject<BlockExportManifest>(File.ReadAllText(path));
                if (manifest == null || manifest.Id != id || manifest.PreviousProject == null || manifest.LatestProject == null
                    || !IsXmlName(manifest.PreviousXml) || !IsXmlName(manifest.LatestXml)) return null;
                if (!File.Exists(Path.Combine(directory, manifest.PreviousXml))
                    || !File.Exists(Path.Combine(directory, manifest.LatestXml))) return null;
                if (new FileInfo(Path.Combine(directory, manifest.PreviousXml)).Length == 0
                    || new FileInfo(Path.Combine(directory, manifest.LatestXml)).Length == 0) return null;
                return manifest;
            }
            catch (JsonException) { return null; }
        }

        /// <summary>清单中的 XML 只能是合法文件名，不能携带目录或跳出导出目录。</summary>
        private static bool IsXmlName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>根据已校验的清单定位指定版本的 XML。</summary>
        internal static string XmlPath(BlockExportManifest manifest, bool latest)
        {
            return Path.Combine(Root, "exports", manifest.Id, latest ? manifest.LatestXml : manifest.PreviousXml);
        }

        /// <summary>按有效导出标识回收目录，供比较失败或历史未发布时回滚。</summary>
        internal static void Delete(string id)
        {
            if (IsId(id)) DeleteDirectory(Path.Combine(Root, "exports", id));
        }

        /// <summary>尽力删除目录；记录清理失败而不覆盖调用方原始异常。</summary>
        internal static void DeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("清理比较临时文件失败: {0}", ex); }
        }
    }
}
