using Microsoft.Win32;

namespace JavaPilot.Services;

public sealed record WindowsUninstallEntry(
    string RegistryKeyName,
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? InstallLocation,
    string? DisplayIcon,
    string? UninstallCommand,
    string? QuietUninstallCommand,
    bool IsWindowsInstaller);

public static class WindowsUninstallCatalog
{
    public static IReadOnlyList<WindowsUninstallEntry> ReadJavaEntries()
    {
        var result = new List<WindowsUninstallEntry>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                    continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(keyName);
                        if (key is null)
                            continue;
                        var displayName = key.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName) ||
                            !LooksLikeJavaProduct(displayName))
                            continue;
                        result.Add(new WindowsUninstallEntry(
                            keyName,
                            displayName.Trim(),
                            key.GetValue("DisplayVersion") as string,
                            key.GetValue("Publisher") as string,
                            key.GetValue("InstallLocation") as string,
                            key.GetValue("DisplayIcon") as string,
                            key.GetValue("UninstallString") as string,
                            key.GetValue("QuietUninstallString") as string,
                            Convert.ToInt32(key.GetValue("WindowsInstaller", 0)) == 1));
                    }
                    catch
                    {
                        // 单个卸载项损坏时继续读取其余项。
                    }
                }
            }
            catch
            {
                // 某个注册表视图无权访问时继续其余视图。
            }
        }

        return result
            .GroupBy(
                entry => $"{entry.RegistryKeyName}|{entry.InstallLocation}|{entry.DisplayName}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    public static WindowsUninstallEntry? FindBestMatch(
        string javaHome,
        IEnumerable<WindowsUninstallEntry> entries)
    {
        var home = NormalizeDirectory(javaHome);
        return entries
            .Select(entry => new
            {
                Entry = entry,
                Score = MatchScore(home, entry)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item =>
                NormalizeDirectory(item.Entry.InstallLocation ?? "").Length)
            .Select(item => item.Entry)
            .FirstOrDefault();
    }

    private static int MatchScore(string home, WindowsUninstallEntry entry)
    {
        var installLocation = NormalizeDirectory(entry.InstallLocation ?? "");
        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            if (home.Equals(installLocation, StringComparison.OrdinalIgnoreCase))
                return 100;
            if (IsInside(home, installLocation) || IsInside(installLocation, home))
                return 90;
        }

        var iconPath = ExtractExecutablePath(entry.DisplayIcon);
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            var iconDirectory = NormalizeDirectory(
                Path.GetDirectoryName(iconPath) ?? "");
            if (iconDirectory.Equals(home, StringComparison.OrdinalIgnoreCase) ||
                IsInside(iconDirectory, home))
                return 70;
        }

        return 0;
    }

    private static bool LooksLikeJavaProduct(string name) =>
        name.Contains("Java", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("JDK", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("JRE", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Corretto", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Liberica", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Zulu", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }

        var comma = trimmed.IndexOf(',');
        return (comma >= 0 ? trimmed[..comma] : trimmed).Trim();
    }

    private static bool IsInside(string candidate, string parent)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(parent))
            return false;
        var prefix = parent.TrimEnd(
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        try
        {
            return Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return "";
        }
    }
}
