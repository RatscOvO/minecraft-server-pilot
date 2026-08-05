using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace JavaPilot.Services;

public static partial class JavaVersionProbe
{
    public sealed record ProbeResult(
        int Major,
        string FullVersion,
        bool Is64Bit,
        string Architecture);

    public static async Task<(int Major, string FullVersion)> ProbeAsync(
        string javaExe,
        CancellationToken cancellationToken)
    {
        var result = await ProbeDetailsAsync(javaExe, cancellationToken);
        return (result.Major, result.FullVersion);
    }

    public static async Task<ProbeResult> ProbeDetailsAsync(
        string javaExe,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(javaExe))
            throw new FileNotFoundException("找不到 java.exe。", javaExe);

        var text = await RunVersionCommandAsync(
            javaExe,
            "-XshowSettings:properties -version",
            cancellationToken).ConfigureAwait(false);
        var parsed = Parse(text);
        if (parsed is null)
        {
            text = await RunVersionCommandAsync(
                javaExe,
                "-version",
                cancellationToken).ConfigureAwait(false);
            parsed = Parse(text);
        }

        if (parsed is null)
            throw new InvalidDataException($"无法识别 Java 版本输出：\n{text.Trim()}");

        var propertyArchitecture = ArchitectureRegex().Match(text);
        var architecture = propertyArchitecture.Success
            ? propertyArchitecture.Groups["bits"].Success
                ? propertyArchitecture.Groups["bits"].Value + "-bit"
                : propertyArchitecture.Groups["arch"].Value.Trim()
            : ReadExecutableArchitecture(javaExe);
        var is64Bit = architecture.Contains("64", StringComparison.OrdinalIgnoreCase) ||
                      architecture.Equals("amd64", StringComparison.OrdinalIgnoreCase) ||
                      architecture.Equals("x86_64", StringComparison.OrdinalIgnoreCase);
        return new ProbeResult(
            parsed.Value.Major,
            parsed.Value.FullVersion,
            is64Bit,
            architecture);
    }

    private static async Task<string> RunVersionCommandAsync(
        string javaExe,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(javaExe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 保留原始超时或取消异常。
            }

            throw;
        }

        return (await errorTask.ConfigureAwait(false)) +
               Environment.NewLine +
               (await outputTask.ConfigureAwait(false));
    }

    public static (int Major, string FullVersion)? Parse(string text)
    {
        var match = VersionRegex().Match(text);
        if (!match.Success)
            return null;

        var full = match.Groups["version"].Value;
        var pieces = full.Split('.', '_', '-', '+');
        if (!int.TryParse(pieces.ElementAtOrDefault(0), out var first))
            return null;
        var major = first == 1 && int.TryParse(pieces.ElementAtOrDefault(1), out var legacy)
            ? legacy
            : first;
        return (major, full);
    }

    [GeneratedRegex("version\\s+\"(?<version>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(
        "(?:sun\\.arch\\.data\\.model\\s*=\\s*(?<bits>\\d+)|os\\.arch\\s*=\\s*(?<arch>[^\\r\\n]+))",
        RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureRegex();

    private static string ReadExecutableArchitecture(string javaExe)
    {
        try
        {
            using var stream = File.OpenRead(javaExe);
            using var reader = new PEReader(stream);
            return reader.PEHeaders.CoffHeader.Machine.ToString();
        }
        catch
        {
            return "unknown";
        }
    }
}
