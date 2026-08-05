using System.Text.Json;

namespace JavaPilot.Services;

public sealed class AppSettingsService
{
    private readonly AppLog _log;
    private readonly string _settingsFile;

    public AppSettingsService(AppLog log, string? settingsFile = null)
    {
        _log = log;
        _settingsFile = settingsFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JavaPilot",
            "settings.json");
    }

    public JavaPilotSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsFile))
                return JavaPilotSettings.CreateDefault();
            var settings = JsonSerializer.Deserialize<JavaPilotSettings>(
                File.ReadAllText(_settingsFile));
            if (settings is null || settings.SchemaVersion != 1)
                throw new InvalidDataException("设置文件版本无法识别。");
            if (string.IsNullOrWhiteSpace(settings.InstallRoot) ||
                !Path.IsPathFullyQualified(settings.InstallRoot))
                settings.InstallRoot = JavaPilotSettings.DefaultInstallRoot;
            if (settings.SelectedJavaMajor is < 6 or > 99)
                settings.SelectedJavaMajor = 21;
            return settings;
        }
        catch (Exception ex)
        {
            _log.Warn("SETTINGS", $"设置文件读取失败，使用安全默认值：{ex.Message}");
            return JavaPilotSettings.CreateDefault();
        }
    }

    public void Save(JavaPilotSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
            var temporary = _settingsFile + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    settings,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temporary, _settingsFile, overwrite: true);
            _log.Info("SETTINGS", $"已保存用户设置：{_settingsFile}");
        }
        catch (Exception ex)
        {
            _log.Warn("SETTINGS", $"无法保存用户设置：{ex.Message}");
        }
    }
}

public sealed class JavaPilotSettings
{
    public const string DefaultInstallRootName = "runtimes";
    public static string DefaultInstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JavaPilot",
        DefaultInstallRootName);

    public int SchemaVersion { get; set; } = 1;
    public string InstallRoot { get; set; } = DefaultInstallRoot;
    public bool ReuseSystemJava { get; set; } = true;
    public bool SetDefaultJava { get; set; } = true;
    public bool ForceReinstall { get; set; }
    public int SelectedJavaMajor { get; set; } = 21;

    public static JavaPilotSettings CreateDefault() => new();
}
