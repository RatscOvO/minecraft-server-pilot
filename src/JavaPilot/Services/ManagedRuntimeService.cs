using System.Text.Json;
using JavaPilot.Models;

namespace JavaPilot.Services;

public sealed class ManagedRuntimeService
{
    private const string MarkerFileName = ".java-pilot-installation.json";
    private readonly AppLog _log;
    private readonly UserEnvironmentService _environment;

    public ManagedRuntimeService(AppLog log)
    {
        _log = log;
        _environment = new UserEnvironmentService(log);
    }

    public async Task<IReadOnlyList<ManagedRuntimeInfo>> ListAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installRoot) ||
            !Path.IsPathFullyQualified(installRoot))
            return [];
        var root = Path.GetFullPath(installRoot);
        if (!Directory.Exists(root))
            return [];

        var result = new List<ManagedRuntimeInfo>();
        foreach (var directory in Directory.EnumerateDirectories(root, "jdk-*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markerPath = Path.Combine(directory, MarkerFileName);
            if (!File.Exists(markerPath))
                continue;

            var marker = ReadMarker(markerPath);
            var majorFromName = int.TryParse(
                Path.GetFileName(directory).AsSpan("jdk-".Length),
                out var parsedMajor)
                ? parsedMajor
                : 0;
            var javaExe = Path.Combine(directory, "bin", "java.exe");
            try
            {
                var probe = await JavaVersionProbe.ProbeDetailsAsync(
                    javaExe,
                    cancellationToken);
                var healthy = probe.Is64Bit &&
                              (majorFromName == 0 || probe.Major == majorFromName);
                result.Add(new ManagedRuntimeInfo(
                    probe.Major,
                    probe.FullVersion,
                    marker.Provider ?? "Java Pilot",
                    directory,
                    javaExe,
                    marker.InstalledAt,
                    healthy,
                    healthy
                        ? "启动与位数验证通过"
                        : "版本目录与真实 Java 不一致"));
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result.Add(new ManagedRuntimeInfo(
                    majorFromName,
                    marker.Version ?? "未知",
                    marker.Provider ?? "Java Pilot",
                    directory,
                    javaExe,
                    marker.InstalledAt,
                    Healthy: false,
                    $"验证失败：{ex.Message}"));
            }
        }

        return result
            .OrderByDescending(item => item.Major)
            .ThenBy(item => item.JavaHome, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<JavaDefaultChangeResult> SetAsDefaultAsync(
        ManagedRuntimeInfo runtime,
        CancellationToken cancellationToken)
    {
        if (!runtime.Healthy || !File.Exists(runtime.JavaExe))
            throw new InvalidOperationException("此 Java 未通过验证，不能设为默认。请先重新安装修复。");
        return _environment.SetDefaultJavaAsync(
            runtime.JavaHome,
            cancellationToken);
    }

    public async Task RemoveAsync(
        string installRoot,
        ManagedRuntimeInfo runtime,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(installRoot);
        var target = Path.GetFullPath(runtime.JavaHome);
        EnsureDirectChild(root, target);
        if (!File.Exists(Path.Combine(target, MarkerFileName)))
            throw new InvalidOperationException(
                "安全检查拒绝删除：目标不是 Java Pilot 管理的运行时。");

        _environment.ClearIfPointsTo(target);
        var removing = Path.Combine(
            root,
            $".java-pilot-removing-{Path.GetFileName(target)}-{Guid.NewGuid():N}");
        EnsureDirectChild(root, removing);
        Directory.Move(target, removing);
        try
        {
            await Task.Run(
                () => Directory.Delete(removing, recursive: true),
                cancellationToken);
            _log.Info("MANAGER", $"已删除 Java Pilot 运行时：{target}");
        }
        catch (Exception ex)
        {
            _log.Error(
                "MANAGER",
                $"运行时已从正式目录移出，但临时删除失败：{removing}",
                ex);
            throw new IOException(
                $"Java 已从安装列表移除，但部分文件被其他程序占用，残留在：\n{removing}\n" +
                "关闭相关 Java 进程后可手动删除。",
                ex);
        }
    }

    private static MarkerInfo ReadMarker(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var provider = root.TryGetProperty("provider", out var providerValue)
                ? providerValue.GetString()
                : null;
            var version = root.TryGetProperty("version", out var versionValue)
                ? versionValue.GetString()
                : null;
            DateTimeOffset? installedAt = null;
            if (root.TryGetProperty("installedAt", out var installedValue) &&
                installedValue.TryGetDateTimeOffset(out var parsed))
                installedAt = parsed;
            return new MarkerInfo(provider, version, installedAt);
        }
        catch
        {
            return new MarkerInfo(null, null, null);
        }
    }

    private static void EnsureDirectChild(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var parent = Directory.GetParent(candidate)?.FullName?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (parent is null ||
            !parent.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"安全检查拒绝操作非安装根目录的直接子目录：{candidate}");
    }

    private sealed record MarkerInfo(
        string? Provider,
        string? Version,
        DateTimeOffset? InstalledAt);
}
