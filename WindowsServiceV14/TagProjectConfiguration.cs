using System;
using System.IO;
using Newtonsoft.Json;

namespace WindowsServiceV14
{
    internal class TagProjectConfiguration
    {
        [JsonProperty("projectPath")]
        public string ProjectPath { get; set; }

        public static string LoadProjectPath()
        {
            var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tagsconfig.json");
            var configuration = JsonConvert.DeserializeObject<TagProjectConfiguration>(File.ReadAllText(configPath));
            if (configuration == null || string.IsNullOrWhiteSpace(configuration.ProjectPath)
                || !Path.IsPathRooted(configuration.ProjectPath))
                throw new InvalidDataException("tagsconfig.json 中的 projectPath 必须是工程文件的绝对路径。");

            if (!File.Exists(configuration.ProjectPath))
                throw new FileNotFoundException("配置的博途工程文件不存在。", configuration.ProjectPath);

            return configuration.ProjectPath;
        }
    }
}
