namespace AutomationPlatform;

public sealed record ProjectDirectoryNode(
    string Name,
    string RelativePath,
    int Level,
    IReadOnlyList<ProjectDirectoryNode> Children);

public sealed class ProjectDirectoryService
{
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

    public string Create(string parentRelativePath, string suffix)
    {
        lock (syncRoot)
        {
            var (parentPath, level) = Resolve(parentRelativePath);
            if (level >= 2)
            {
                throw new InvalidOperationException("工站节点不能再创建子节点。");
            }

            var name = BuildName(level == 0 ? "Line" : "ST", suffix);
            EnsureNameAvailable(parentPath, name);
            var path = Path.Combine(parentPath, name);
            Directory.CreateDirectory(path);
            CheckDirectoryChain(path);
            return Path.GetRelativePath(RootPath, path);
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
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new InvalidOperationException("只能删除空目录。请先处理工站、子目录及文件后再删除。");
            }

            Directory.Delete(path, recursive: false);
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
            relativePath, level, children);
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
