using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AutomationPlatform;

public sealed record ProjectDirectoryNode(
    string Name,
    string RelativePath,
    int Level,
    IReadOnlyList<ProjectDirectoryNode> Children,
    string? StationIpAddress,
    string? StationIpError);

public sealed class ProjectDirectoryService
{
    private const string StationConfigFileName = "station.json";
    private static readonly JsonSerializerOptions stationConfigJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object syncRoot = new();

    public ProjectDirectoryService() : this(@"E:\AP")
    {
    }

    internal ProjectDirectoryService(string rootPath)
    {
        RootPath = Path.GetFullPath(rootPath);
    }

    public string RootPath { get; }

    public ProjectDirectoryNode Load()
    {
        lock (syncRoot)
        {
            var drive = Path.GetPathRoot(RootPath);
            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive))
            {
                throw new DirectoryNotFoundException("根目录所在磁盘不可用，请检查 E 盘。");
            }

            CheckDirectoryChain(RootPath, allowMissing: true);
            Directory.CreateDirectory(RootPath);
            CheckDirectoryChain(RootPath);
            return ReadNode(RootPath, string.Empty, 0);
        }
    }

    public string Create(string parentRelativePath, string suffix, string? stationIpAddress = null)
    {
        lock (syncRoot)
        {
            var (parentPath, level) = Resolve(parentRelativePath);
            if (level >= 2)
            {
                throw new InvalidOperationException("工站节点不能再创建子节点。");
            }

            if (level == 0 && stationIpAddress is not null)
            {
                throw new InvalidOperationException("Line 线体不配置 IP，只有 ST 工站需要配置 IP。");
            }

            if (level == 1)
            {
                stationIpAddress = NormalizeStationIp(stationIpAddress ?? string.Empty);
            }

            var name = BuildName(level == 0 ? "Line" : "ST", suffix);
            EnsureNameAvailable(parentPath, name);
            var path = Path.Combine(parentPath, name);
            Directory.CreateDirectory(path);
            try
            {
                CheckDirectoryChain(path);
                if (level == 1)
                {
                    WriteStationIp(path, stationIpAddress);
                }
            }
            catch
            {
                try
                {
                    if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                    {
                        Directory.Delete(path, recursive: false);
                    }
                }
                catch
                {
                }

                throw;
            }

            return Path.GetRelativePath(RootPath, path);
        }
    }

    public string SetStationIp(string relativePath, string stationIpAddress)
    {
        lock (syncRoot)
        {
            var (path, level) = Resolve(relativePath);
            EnsureStation(level);
            WriteStationIp(path, stationIpAddress);
            return Path.GetRelativePath(RootPath, path);
        }
    }

    public string GetStationPath(string relativePath)
    {
        lock (syncRoot)
        {
            var (path, level) = Resolve(relativePath);
            EnsureStation(level);
            return path;
        }
    }

    public string Rename(string relativePath, string suffix)
    {
        lock (syncRoot)
        {
            var (path, level) = Resolve(relativePath);
            EnsureNotRoot(level);
            var name = BuildName(level == 1 ? "Line" : "ST", suffix);
            if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("名称未改变或仅大小写不同，请输入不同的名称。");
            }

            var parent = Path.GetDirectoryName(path)!;
            EnsureNameAvailable(parent, name);
            var target = Path.Combine(parent, name);
            Directory.Move(path, target);
            return Path.GetRelativePath(RootPath, target);
        }
    }

    public void Delete(string relativePath)
    {
        lock (syncRoot)
        {
            var (path, level) = Resolve(relativePath);
            EnsureNotRoot(level);

            var entries = Directory.EnumerateFileSystemEntries(path).ToList();
            if (entries.Count == 0)
            {
                Directory.Delete(path, recursive: false);
                return;
            }

            if (level == 2 && entries.Count == 1 && IsStationConfigEntry(entries[0]))
            {
                EnsureStationConfigFile(entries[0]);
                File.Delete(entries[0]);
                Directory.Delete(path, recursive: false);
                return;
            }

            throw new InvalidOperationException("只能删除空目录。工站目录仅包含 IP 配置文件时也允许删除。请先处理工站、子目录及文件后再删除。");
        }
    }

    private (string Path, int Level) Resolve(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var parts = relativePath.Length == 0
            ? Array.Empty<string>()
            : relativePath.Split(['\\', '/']);
        if (Path.IsPathRooted(relativePath) || parts.Length > 2 ||
            parts.Where((part, index) => !IsNodeName(part, index == 0 ? "Line" : "ST")).Any())
        {
            throw new ArgumentException("项目路径无效，只允许 AP 下的 Line 线体和 ST 工站。");
        }

        var path = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        if (!string.Equals(path, RootPath, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith(Path.TrimEndingDirectorySeparator(RootPath) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("不能访问 AP 根目录之外的路径。");
        }

        CheckDirectoryChain(path);
        return (path, parts.Length);
    }

    private static string BuildName(string prefix, string suffix)
    {
        if (suffix is null || !IsNodeName(prefix + suffix, prefix))
        {
            throw new ArgumentException("请输入有效后缀：不能包含路径分隔符、非法字符、连续点、首尾空格或末尾点，完整名称不能超过 255 个字符。");
        }

        return prefix + suffix;
    }

    private static void EnsureNotRoot(int level)
    {
        if (level == 0)
        {
            throw new InvalidOperationException("AP 根节点不能重命名或删除。");
        }
    }

    private static bool IsStationConfigEntry(string entryPath) =>
        string.Equals(Path.GetFileName(entryPath), StationConfigFileName, StringComparison.OrdinalIgnoreCase);

    private static void EnsureNameAvailable(string parent, string name)
    {
        if (Directory.EnumerateFileSystemEntries(parent).Any(path =>
                string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("同级目录中已存在同名文件或文件夹，请使用其他名称。");
        }
    }

    private ProjectDirectoryNode ReadNode(string path, string relativePath, int level)
    {
        var children = new List<ProjectDirectoryNode>();
        string? stationIpAddress = null;
        string? stationIpError = null;

        if (level == 2)
        {
            (stationIpAddress, stationIpError) = ReadStationIp(path);
        }

        if (level < 2)
        {
            var prefix = level == 0 ? "Line" : "ST";
            foreach (var directory in new DirectoryInfo(path).EnumerateDirectories()
                         .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase))
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !IsNodeName(directory.Name, prefix))
                {
                    continue;
                }

                CheckDirectoryChain(directory.FullName);
                children.Add(ReadNode(directory.FullName,
                    Path.Combine(relativePath, directory.Name), level + 1));
            }
        }

        return new ProjectDirectoryNode(level == 0 ? "AP" : Path.GetFileName(path),
            relativePath, level, children, stationIpAddress, stationIpError);
    }

    private static (string? StationIpAddress, string? StationIpError) ReadStationIp(string stationPath)
    {
        var configPath = GetStationConfigPath(stationPath);
        if (!File.Exists(configPath))
        {
            return (null, null);
        }

        try
        {
            EnsureStationConfigFile(configPath);
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<StationConfig>(json, stationConfigJsonOptions)
                ?? throw new InvalidDataException("工站 IP 配置文件内容为空。");
            var normalizedIp = NormalizeStationIp(config.IpAddress);
            return (normalizedIp, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return (null, $"工站 IP 配置文件无效：{exception.Message}");
        }
    }

    private static string GetStationConfigPath(string stationPath) => Path.Combine(stationPath, StationConfigFileName);

    private static void EnsureStation(int level)
    {
        if (level != 2)
        {
            throw new InvalidOperationException("只能为 ST 工站配置 IP。");
        }
    }

    private static void WriteStationIp(string stationPath, string stationIpAddress)
    {
        var normalizedIp = NormalizeStationIp(stationIpAddress);
        var configPath = GetStationConfigPath(stationPath);
        if (File.Exists(configPath))
        {
            EnsureStationConfigFile(configPath);
        }

        var tempPath = Path.Combine(stationPath, $"{StationConfigFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var content = JsonSerializer.Serialize(new StationConfig { IpAddress = normalizedIp }, stationConfigJsonOptions);
            File.WriteAllText(tempPath, content);
            if (File.Exists(configPath))
            {
                File.Replace(tempPath, configPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, configPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string NormalizeStationIp(string stationIpAddress)
    {
        if (string.IsNullOrWhiteSpace(stationIpAddress))
        {
            throw new ArgumentException("请输入工站 IP 地址。");
        }

        if (!IPAddress.TryParse(stationIpAddress.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("请输入有效的 IPv4 地址。");
        }

        return address.ToString();
    }

    private static void EnsureStationConfigFile(string configPath)
    {
        var attributes = File.GetAttributes(configPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("工站 IP 配置文件不能是符号链接或目录联接。");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("工站 IP 配置路径必须是文件，不能是目录。");
        }
    }

    private sealed class StationConfig
    {
        public string IpAddress { get; set; } = string.Empty;
    }

    private static bool IsNodeName(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.Ordinal) &&
        IsValidSuffix(name[prefix.Length..]) && name.Length <= 255;

    private static bool IsValidSuffix(string suffix) =>
        !string.IsNullOrWhiteSpace(suffix) && suffix == suffix.Trim() &&
        !suffix.EndsWith('.') && !suffix.Contains("..", StringComparison.Ordinal) &&
        !suffix.Any(character => character < ' ' || "<>:\"/\\|?*".Contains(character));

    private static void CheckDirectoryChain(string path, bool allowMissing = false)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            CheckDirectoryChain(parent, allowMissing);
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException) when (allowMissing)
        {
            return;
        }
        catch (DirectoryNotFoundException) when (allowMissing)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("不允许操作符号链接或目录联接，请使用实际目录。");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException("目标路径已被文件占用，无法作为项目目录使用。");
        }
    }
}
