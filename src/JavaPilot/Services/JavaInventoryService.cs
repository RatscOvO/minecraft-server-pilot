using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using JavaPilot.Models;
using Microsoft.VisualBasic.FileIO;

namespace JavaPilot.Services;

public sealed class JavaInventoryService
{
    private const string MarkerFileName = ".java-pilot-installation.json";
    private readonly AppLog _log;
    private readonly JavaDiscoveryService _discovery;
    private readonly UserEnvironmentService _environment;

    public JavaInventoryService(AppLog log)
    {
        _log = log;
        _discovery = new JavaDiscoveryService(log);
        _environment = new UserEnvironmentService(log);
    }

    public async Task<IReadOnlyList<JavaInventoryItem>> ListAsync(
        string installRoot,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(installRoot);
        var installations = await _discovery.ScanAsync(
            root,
            cancellationToken,
            forceRefresh: true,
            include32Bit: true);
        var uninstallEntries = WindowsUninstallCatalog.ReadJavaEntries();
        var userDefault = Normalize(
            Environment.GetEnvironmentVariable(
                "JAVA_HOME",
                EnvironmentVariableTarget.User) ?? "");
        var result = new List<JavaInventoryItem>();

        foreach (var installation in installations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var markerPath = Path.Combine(installation.JavaHome, MarkerFileName);
            var managed = IsDirectChild(root, installation.JavaHome) &&
                          File.Exists(markerPath);
            var uninstall = managed
                ? null
                : WindowsUninstallCatalog.FindBestMatch(
                    installation.JavaHome,
                    uninstallEntries);
            var marker = managed ? ReadMarker(markerPath) : null;
            var ownership = managed
                ? JavaOwnership.JavaPilotManaged
                : uninstall is not null
                    ? JavaOwnership.RegisteredInstaller
                    : IsDedicatedPortableHome(installation.JavaHome)
                        ? JavaOwnership.ExternalPortable
                        : JavaOwnership.ExternalUnknown;
            result.Add(new JavaInventoryItem(
                installation.Major,
                installation.FullVersion,
                installation.JavaHome,
                installation.JavaExe,
                installation.Source,
                installation.Is64Bit,
                Healthy: File.Exists(installation.JavaExe),
                ownership,
                ProductName: marker?.Provider ?? uninstall?.DisplayName,
                Publisher: uninstall?.Publisher,
                UninstallCommand:
                    BuildRegisteredUninstallCommand(uninstall),
                IsCurrentUserDefault:
                    Normalize(installation.JavaHome).Equals(
                        userDefault,
                        StringComparison.OrdinalIgnoreCase),
                InstalledAt: marker?.InstalledAt));
        }

        return result
            .OrderBy(item => OwnershipPriority(item.Ownership))
            .ThenByDescending(item => item.Major)
            .ThenBy(item => item.JavaHome, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<JavaDefaultChangeResult> SetAsDefaultAsync(
        JavaInventoryItem runtime,
        CancellationToken cancellationToken)
    {
        if (!runtime.Healthy || !runtime.Is64Bit)
            throw new InvalidOperationException(
                "只有通过验证的 64 位 Java 才能设为 Minecraft 默认运行时。");
        return _environment.SetDefaultJavaAsync(
            runtime.JavaHome,
            cancellationToken);
    }

    public async Task<JavaInventoryItem> AdoptAsync(
        string installRoot,
        JavaInventoryItem runtime,
        CancellationToken cancellationToken)
    {
        if (!runtime.Healthy || !runtime.Is64Bit)
            throw new InvalidOperationException("只能纳入通过验证的 64 位 Java。");
        if (runtime.Ownership == JavaOwnership.JavaPilotManaged)
            throw new InvalidOperationException("这个 Java 已经由 Java Pilot 管理。");

        var root = Path.GetFullPath(installRoot);
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, $"jdk-{runtime.Major}");
        if (Directory.Exists(target))
            throw new InvalidOperationException(
                $"Java Pilot 目录中已经存在 Java {runtime.Major}：\n{target}\n" +
                "请先在管理器中处理现有版本，避免含义不明确的覆盖。");

        var temporary = Path.Combine(
            root,
            $".java-pilot-adopt-{runtime.Major}-{Guid.NewGuid():N}");
        EnsureDirectChild(root, temporary);
        try
        {
            _log.Info(
                "ADOPT",
                $"开始复制外部 Java 到受管目录：{runtime.JavaHome} -> {target}");
            await Task.Run(
                () => CopyDirectory(runtime.JavaHome, temporary, cancellationToken),
                cancellationToken);
            var copiedJava = Path.Combine(temporary, "bin", "java.exe");
            var details = await JavaVersionProbe.ProbeDetailsAsync(
                copiedJava,
                cancellationToken);
            if (!details.Is64Bit || details.Major != runtime.Major)
                throw new InvalidDataException(
                    $"复制后验证不一致：得到 Java {details.Major} " +
                    $"{(details.Is64Bit ? "64 位" : "32 位")}。");

            WriteMarker(
                temporary,
                $"本机 Java 纳入管理 · {runtime.ProviderText}",
                details.FullVersion);
            WriteLauncher(temporary);
            Directory.Move(temporary, target);
            _log.Info("ADOPT", $"外部 Java 已纳入管理：{target}");
            return new JavaInventoryItem(
                details.Major,
                details.FullVersion,
                target,
                Path.Combine(target, "bin", "java.exe"),
                "Java Pilot",
                details.Is64Bit,
                Healthy: true,
                JavaOwnership.JavaPilotManaged,
                ProductName: $"本机 Java 纳入管理 · {runtime.ProviderText}",
                InstalledAt: DateTimeOffset.Now);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                try
                {
                    Directory.Delete(temporary, recursive: true);
                }
                catch
                {
                    _log.Warn("ADOPT", $"临时复制目录清理失败：{temporary}");
                }
            }
        }
    }

    public void LaunchRegisteredUninstaller(JavaInventoryItem runtime)
    {
        if (runtime.Ownership != JavaOwnership.RegisteredInstaller ||
            string.IsNullOrWhiteSpace(runtime.UninstallCommand))
        {
            throw new InvalidOperationException("没有找到可信的 Windows 注册卸载入口。");
        }

        var arguments = SplitCommandLine(runtime.UninstallCommand);
        if (arguments.Count == 0)
            throw new InvalidOperationException("Windows 注册卸载命令为空。");
        var startInfo = new ProcessStartInfo
        {
            FileName = arguments[0],
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (var argument in arguments.Skip(1))
            startInfo.ArgumentList.Add(argument);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows 没有启动厂商卸载程序。");
        _log.Info(
            "UNINSTALL",
            $"已启动 Windows 注册卸载入口：{runtime.ProductName}；{runtime.JavaHome}");
    }

    public void MovePortableToRecycleBin(
        string installRoot,
        JavaInventoryItem runtime)
    {
        if (runtime.Ownership != JavaOwnership.ExternalPortable)
            throw new InvalidOperationException("该项目不是可直接处理的外部便携 Java。");
        var root = Path.GetFullPath(installRoot);
        var target = Path.GetFullPath(runtime.JavaHome);
        if (IsInside(target, root) || IsInside(root, target))
            throw new InvalidOperationException("拒绝通过外部 Java 流程操作 Java Pilot 安装目录。");
        if (!IsDedicatedPortableHome(target))
            throw new InvalidOperationException(
                "目录缺少 JDK/JRE 的 release 与 bin\\java.exe，无法证明它是独立 Java 主目录。");
        if (IsProtectedSystemLocation(target))
            throw new InvalidOperationException(
                "该 Java 位于 Windows 或 Program Files 受保护目录，但没有匹配到可信卸载项。" +
                "程序不会直接删除它；请先打开目录核对，或从 Windows“已安装的应用”处理。");

        _environment.ClearIfPointsTo(target);
        FileSystem.DeleteDirectory(
            target,
            UIOption.OnlyErrorDialogs,
            RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
        _log.Info("UNINSTALL", $"外部便携 Java 已移入回收站：{target}");
    }

    private static string? BuildRegisteredUninstallCommand(
        WindowsUninstallEntry? entry)
    {
        if (entry is null)
            return null;
        if (entry.IsWindowsInstaller &&
            Guid.TryParse(entry.RegistryKeyName.Trim('{', '}'), out var productCode))
        {
            return $"msiexec.exe /x {{{productCode}}} /passive /norestart";
        }

        return entry.UninstallCommand;
    }

    private static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == IntPtr.Zero)
            throw new InvalidOperationException(
                $"无法解析 Windows 卸载命令，错误码：{Marshal.GetLastWin32Error()}");
        try
        {
            var result = new string[count];
            for (var index = 0; index < count; index++)
            {
                var itemPointer = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
                result[index] = Marshal.PtrToStringUni(itemPointer) ?? "";
            }

            return result;
        }
        finally
        {
            _ = LocalFree(pointer);
        }
    }

    private static void CopyDirectory(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(
                     source,
                     "*",
                     System.IO.SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(
                target,
                Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(
                     source,
                     "*",
                     System.IO.SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(
                target,
                Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private static bool IsDedicatedPortableHome(string path) =>
        File.Exists(Path.Combine(path, "bin", "java.exe")) &&
        File.Exists(Path.Combine(path, "release"));

    private static bool IsProtectedSystemLocation(string path)
    {
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(value => !string.IsNullOrWhiteSpace(value));
        return protectedRoots.Any(root => IsInside(path, root) ||
                                          Normalize(path).Equals(
                                              Normalize(root),
                                              StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsDirectChild(string root, string candidate)
    {
        var parent = Directory.GetParent(Path.GetFullPath(candidate))?.FullName;
        return parent is not null &&
               Normalize(parent).Equals(Normalize(root), StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureDirectChild(string root, string candidate)
    {
        if (!IsDirectChild(root, candidate))
            throw new InvalidOperationException(
                $"安全检查拒绝操作安装根目录以外的路径：{candidate}");
    }

    private static bool IsInside(string candidate, string parent)
    {
        var prefix = Normalize(parent) + Path.DirectorySeparatorChar;
        return Normalize(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        try
        {
            return Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return value.Trim().TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
    }

    private static int OwnershipPriority(JavaOwnership ownership) => ownership switch
    {
        JavaOwnership.JavaPilotManaged => 0,
        JavaOwnership.RegisteredInstaller => 1,
        JavaOwnership.ExternalPortable => 2,
        _ => 3
    };

    private static MarkerInfo? ReadMarker(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var provider = root.TryGetProperty("provider", out var providerValue)
                ? providerValue.GetString()
                : null;
            DateTimeOffset? installedAt = null;
            if (root.TryGetProperty("installedAt", out var installedValue) &&
                installedValue.TryGetDateTimeOffset(out var parsed))
                installedAt = parsed;
            return new MarkerInfo(provider, installedAt);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteMarker(
        string target,
        string provider,
        string version)
    {
        var marker = JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                provider,
                version,
                installedAt = DateTimeOffset.Now
            },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(
            Path.Combine(target, MarkerFileName),
            marker,
            new UTF8Encoding(false));
    }

    private static void WriteLauncher(string javaHome)
    {
        const string script =
            "@echo off\r\n" +
            "setlocal\r\n" +
            "set \"JAVA_HOME=%~dp0\"\r\n" +
            "set \"PATH=%JAVA_HOME%bin;%PATH%\"\r\n" +
            "\"%JAVA_HOME%bin\\java.exe\" %*\r\n";
        File.WriteAllText(
            Path.Combine(javaHome, "Run-Java.cmd"),
            script,
            new UTF8Encoding(false));
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed record MarkerInfo(
        string? Provider,
        DateTimeOffset? InstalledAt);
}
