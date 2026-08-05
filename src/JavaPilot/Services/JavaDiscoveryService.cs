using Microsoft.Win32;
using JavaPilot.Models;

namespace JavaPilot.Services;

public sealed class JavaDiscoveryService
{
    private readonly AppLog _log;
    private DateTimeOffset _cacheTime;
    private string? _cacheRoot;
    private bool _cacheIncludes32Bit;
    private IReadOnlyList<JavaInstallation>? _cache;

    public JavaDiscoveryService(AppLog log)
    {
        _log = log;
    }

    public async Task<IReadOnlyList<JavaInstallation>> ScanAsync(
        string? managedInstallRoot,
        CancellationToken cancellationToken,
        bool forceRefresh = false,
        bool include32Bit = false)
    {
        var normalizedRoot = string.IsNullOrWhiteSpace(managedInstallRoot)
            ? null
            : Path.GetFullPath(managedInstallRoot);
        if (!forceRefresh &&
            _cache is not null &&
            normalizedRoot == _cacheRoot &&
            include32Bit == _cacheIncludes32Bit &&
            DateTimeOffset.Now - _cacheTime < TimeSpan.FromSeconds(30))
            return _cache;

        var candidates = DiscoverCandidates(normalizedRoot).ToArray();
        _log.Info("DISCOVERY", $"找到 {candidates.Length} 个 Java 候选路径，开始并行验证。");
        using var concurrency = new SemaphoreSlim(4);
        var tasks = candidates.Select(async candidate =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var details = await JavaVersionProbe.ProbeDetailsAsync(
                    candidate.JavaExe,
                    cancellationToken);
                if (!details.Is64Bit)
                {
                    if (!include32Bit)
                    {
                        _log.Warn(
                            "DISCOVERY",
                            $"忽略非 64 位 Java {details.FullVersion}：{candidate.JavaExe}");
                        return null;
                    }
                }

                var home = ResolveLogicalJavaHome(candidate.JavaExe);
                var logicalJavaExe = Path.Combine(home, "bin", "java.exe");
                return new JavaInstallation(
                    details.Major,
                    details.FullVersion,
                    home,
                    File.Exists(logicalJavaExe) ? logicalJavaExe : candidate.JavaExe,
                    candidate.Source,
                    details.Is64Bit);
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _log.Warn(
                    "DISCOVERY",
                    $"无法验证 {candidate.JavaExe}（{candidate.Source}）：{ex.Message}");
                return null;
            }
            finally
            {
                concurrency.Release();
            }
        });

        var probed = await Task.WhenAll(tasks);
        var result = probed
            .Where(item => item is not null)
            .Cast<JavaInstallation>()
            .GroupBy(item => item.JavaHome, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var preferred = group
                    .OrderBy(item => SourcePriority(item.Source))
                    .ThenBy(item => item.JavaExe.Length)
                    .First();
                var sources = string.Join(
                    " / ",
                    group.Select(item => item.Source)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(SourcePriority));
                return preferred with { Source = sources };
            })
            .OrderByDescending(item => item.Major)
            .ThenBy(item => SourcePriority(item.Source))
            .ThenBy(item => item.JavaExe, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _cache = result;
        _cacheRoot = normalizedRoot;
        _cacheIncludes32Bit = include32Bit;
        _cacheTime = DateTimeOffset.Now;
        _log.Info(
            "DISCOVERY",
            result.Length == 0
                ? "没有检测到可用的 64 位 Java。"
                : "检测完成：" + string.Join(
                    "; ",
                    result.Select(item =>
                        $"Java {item.FullVersion} [{item.Source}] {item.JavaHome}")));
        return result;
    }

    private static IEnumerable<Candidate> DiscoverCandidates(string? managedInstallRoot)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<Candidate>();

        void Add(string? path, string source)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                var full = Path.GetFullPath(path.Trim().Trim('"'));
                if (File.Exists(full) && found.Add(full))
                    candidates.Add(new Candidate(full, source));
            }
            catch
            {
                // 单个无效 PATH 项不能阻止其余 Java 探测。
            }
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            Add(Path.Combine(javaHome, "bin", "java.exe"), "JAVA_HOME");

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Add(Path.Combine(Environment.ExpandEnvironmentVariables(directory), "java.exe"), "PATH");

        if (managedInstallRoot is not null && Directory.Exists(managedInstallRoot))
        {
            foreach (var directory in SafeEnumerateDirectories(managedInstallRoot))
                Add(Path.Combine(directory, "bin", "java.exe"), "Java Pilot");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[]
                 {
                     Path.Combine(programFiles, "Java"),
                     Path.Combine(programFiles, "Eclipse Adoptium"),
                     Path.Combine(programFiles, "Microsoft"),
                     Path.Combine(programFiles, "Amazon Corretto"),
                     Path.Combine(programFiles, "BellSoft"),
                     Path.Combine(programFiles, "Zulu"),
                     Path.Combine(programFilesX86, "Java"),
                     Path.Combine(programFilesX86, "Eclipse Adoptium"),
                     Path.Combine(localAppData, "Programs", "Eclipse Adoptium"),
                     Path.Combine(localAppData, "Programs", "Java")
                 })
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var directory in SafeEnumerateDirectories(root))
            {
                Add(Path.Combine(directory, "bin", "java.exe"), "程序目录");
                Add(Path.Combine(directory, "jre", "bin", "java.exe"), "程序目录");
            }
        }

        foreach (var candidate in DiscoverRegistryCandidates())
            Add(candidate.JavaExe, candidate.Source);

        return candidates;
    }

    private static IEnumerable<Candidate> DiscoverRegistryCandidates()
    {
        var candidates = new List<Candidate>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (var keyName in new[]
                         {
                             @"SOFTWARE\JavaSoft\JDK",
                             @"SOFTWARE\JavaSoft\Java Runtime Environment",
                             @"SOFTWARE\Eclipse Adoptium\JDK",
                             @"SOFTWARE\Eclipse Adoptium\JRE",
                             @"SOFTWARE\Microsoft\JDK",
                             @"SOFTWARE\Amazon Corretto\JDK",
                             @"SOFTWARE\Azul Systems\Zulu"
                         })
                {
                    using var key = baseKey.OpenSubKey(keyName);
                    if (key is null)
                        continue;
                    AddRegistryHome(key, $"{hive}/{view}", candidates);
                    foreach (var version in key.GetSubKeyNames())
                    {
                        using var versionKey = key.OpenSubKey(version);
                        if (versionKey is not null)
                            AddRegistryHome(versionKey, $"{hive}/{view} 注册表", candidates);
                    }
                }
            }
            catch
            {
                // 某个注册表视图不可读时继续其余来源。
            }
        }

        return candidates;
    }

    private static void AddRegistryHome(
        RegistryKey key,
        string source,
        ICollection<Candidate> candidates)
    {
        var home = key.GetValue("JavaHome") as string ??
                   key.GetValue("Path") as string;
        if (!string.IsNullOrWhiteSpace(home))
            candidates.Add(new Candidate(Path.Combine(home, "bin", "java.exe"), source));
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string ResolveLogicalJavaHome(string javaExe)
    {
        var executable = javaExe;
        try
        {
            executable = new FileInfo(javaExe).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                         ?? javaExe;
        }
        catch
        {
            // 无法解析链接时仍使用已验证的原始路径。
        }

        var bin = Path.GetDirectoryName(executable)
                  ?? throw new InvalidDataException($"无法解析 Java bin 目录：{javaExe}");
        var home = Directory.GetParent(bin)?.FullName
                   ?? throw new InvalidDataException($"无法解析 Java 主目录：{javaExe}");
        if (Path.GetFileName(home).Equals("jre", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(home)?.FullName;
            if (parent is not null &&
                File.Exists(Path.Combine(parent, "bin", "java.exe")))
                return parent;
        }

        return Path.GetFullPath(home);
    }

    private static int SourcePriority(string source) => source switch
    {
        "JAVA_HOME" => 0,
        "PATH" => 1,
        "Java Pilot" => 2,
        _ => 3
    };

    private sealed record Candidate(string JavaExe, string Source);
}
