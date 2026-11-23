using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;

namespace ServiceV14
{
    /// <summary>读取可执行文件目录中的 tagsconfig.json，并按使用场景校验路径和选项。</summary>
    internal class TagProjectConfiguration
    {
        [JsonProperty("projectPath")]
        public string ProjectPath { get; set; }

        // 保留既有 JSON 键的拼写，确保兼容已经部署的配置文件。
        [JsonProperty("comparasionFolder")]
        public string ComparasionFolder { get; set; }

        [JsonProperty("listenIp")]
        public string ListenIp { get; set; }

        [JsonProperty("blockComparison")]
        public BlockComparisonOptions BlockComparison { get; set; }

        [JsonProperty("sat")]
        public SatOptions Sat { get; set; }

        internal static string LoadSatScanAdapter() => Load()?.Sat?.ScanAdapter;

        internal sealed class SatOptions
        {
            [JsonProperty("scanAdapter")]
            public string ScanAdapter { get; set; }
        }

        /// <summary>加载比较资源限制，缺少该配置节时使用默认值，再统一校验范围。</summary>
        internal static BlockComparisonOptions LoadBlockComparison()
        {
            var settings = Load()?.BlockComparison ?? new BlockComparisonOptions();
            settings.Validate();
            return settings;
        }

        /// <summary>获取标签读取所用工程，要求绝对路径且文件存在。</summary>
        public static string LoadProjectPath()
        {
            var configuration = Load();
            if (configuration == null || string.IsNullOrWhiteSpace(configuration.ProjectPath)
                || !Path.IsPathRooted(configuration.ProjectPath))
                throw new InvalidDataException("tagsconfig.json 中的 projectPath 必须是工程文件的绝对路径。");

            if (!File.Exists(configuration.ProjectPath))
                throw new FileNotFoundException("配置的博途工程文件不存在。", configuration.ProjectPath);

            return configuration.ProjectPath;
        }

        /// <summary>获取历史扫描及导出存储使用的目录，要求绝对路径且目录存在。</summary>
        public static string LoadComparasionFolder()
        {
            var configuration = Load();
            if (configuration == null || string.IsNullOrWhiteSpace(configuration.ComparasionFolder)
                || !Path.IsPathRooted(configuration.ComparasionFolder))
                throw new InvalidDataException("tagsconfig.json 中的 comparasionFolder 必须是文件夹的绝对路径。");

            if (!Directory.Exists(configuration.ComparasionFolder))
                throw new DirectoryNotFoundException("配置的比较文件夹不存在: " + configuration.ComparasionFolder);

            return configuration.ComparasionFolder;
        }

        /// <summary>生成固定 5736 端口的监听地址，仅接受 localhost 或 IPv4 地址。</summary>
        public static string LoadListenUrl()
        {
            var configuration = Load();
            var listenIp = configuration?.ListenIp ?? "localhost";
            IPAddress address;
            if (string.IsNullOrWhiteSpace(listenIp)
                || (!string.Equals(listenIp, "localhost", StringComparison.OrdinalIgnoreCase)
                    && (!IPAddress.TryParse(listenIp, out address) || address.AddressFamily != AddressFamily.InterNetwork)))
                throw new InvalidDataException("tagsconfig.json 中的 listenIp 必须是 localhost 或有效的 IPv4 地址。");

            return $"http://{listenIp}:5736/";
        }

        /// <summary>每次从程序基目录读取配置，不依赖 Windows 服务进程的当前工作目录。</summary>
        private static TagProjectConfiguration Load()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tagsconfig.json");
            return JsonConvert.DeserializeObject<TagProjectConfiguration>(File.ReadAllText(configPath));
        }
    }
}
