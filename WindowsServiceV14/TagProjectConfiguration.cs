using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json;

namespace WindowsServiceV14
{
    internal class TagProjectConfiguration
    {
        [JsonProperty("projectPath")]
        public string ProjectPath { get; set; }

        [JsonProperty("comparasionFolder")]
        public string ComparasionFolder { get; set; }

        [JsonProperty("listenIp")]
        public string ListenIp { get; set; }

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

        private static TagProjectConfiguration Load()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tagsconfig.json");
            return JsonConvert.DeserializeObject<TagProjectConfiguration>(File.ReadAllText(configPath));
        }
    }
}
