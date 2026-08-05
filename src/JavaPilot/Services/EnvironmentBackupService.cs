namespace JavaPilot.Services;

/// <summary>
/// 只管理 Java Pilot 写入的系统环境变量 JSON 备份。
/// 不递归扫描，也不会删除备份目录中的其他文件。
/// </summary>
public sealed class EnvironmentBackupService
{
    private const string BackupPrefix = "environment-";
    private readonly string _directory;

    public EnvironmentBackupService(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? DefaultDirectory);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "JavaPilot",
        "environment-backups");

    public JavaBackupSummary Inspect()
    {
        var files = EnumerateBackupFiles();
        return new JavaBackupSummary(
            _directory,
            files.Count,
            files.Sum(file => file.Length));
    }

    public JavaBackupCleanupResult Clear()
    {
        var files = EnumerateBackupFiles();
        var deletedCount = 0;
        long deletedBytes = 0;
        var failures = new List<string>();

        foreach (var file in files)
        {
            try
            {
                var length = file.Length;
                File.Delete(file.FullName);
                deletedCount++;
                deletedBytes += length;
            }
            catch (Exception ex)
            {
                failures.Add($"{file.Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new IOException(
                $"已删除 {deletedCount} 个备份，但有 {failures.Count} 个文件无法删除：\n" +
                string.Join("\n", failures));
        }

        return new JavaBackupCleanupResult(
            _directory,
            deletedCount,
            deletedBytes);
    }

    private List<FileInfo> EnumerateBackupFiles()
    {
        if (!Directory.Exists(_directory))
            return [];

        return Directory
            .EnumerateFiles(
                _directory,
                "*",
                SearchOption.TopDirectoryOnly)
            .Where(IsManagedBackupPath)
            .Select(path => new FileInfo(path))
            .Where(file =>
                (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .ToList();
    }

    private bool IsManagedBackupPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var parent = Directory.GetParent(full)?.FullName;
            var name = Path.GetFileName(full);
            return parent is not null &&
                   Path.GetFullPath(parent).Equals(
                       _directory,
                       StringComparison.OrdinalIgnoreCase) &&
                   name.StartsWith(
                       BackupPrefix,
                       StringComparison.OrdinalIgnoreCase) &&
                   name.EndsWith(
                       ".json",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

public sealed record JavaBackupSummary(
    string DirectoryPath,
    int Count,
    long TotalBytes);

public sealed record JavaBackupCleanupResult(
    string DirectoryPath,
    int DeletedCount,
    long DeletedBytes);
