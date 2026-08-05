using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MinecraftServerPilot.Models;

namespace MinecraftServerPilot.Services;

public sealed class ServerPropertiesService
{
    private readonly AppLog _log;

    public ServerPropertiesService(AppLog log)
    {
        _log = log;
    }

    public ServerPropertiesSnapshot Load(string serverDirectory, string minecraftVersion)
    {
        var path = PropertiesPath(serverDirectory);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "服务端尚未生成 server.properties。请先完成至少一次启动验证，再打开设置面板。",
                path);
        }

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var rawValues = ReadValues(lines);
        var definitions = BuildDefinitions(minecraftVersion, rawValues);
        var values = new List<ServerPropertyValue>();
        var unavailable = new List<string>();
        foreach (var definition in definitions)
        {
            if (rawValues.TryGetValue(definition.Key, out var value))
            {
                values.Add(new(definition, NormalizeChoiceValue(definition, value), true));
            }
            else if (definition.AddWhenMissing)
            {
                values.Add(new(definition, definition.DefaultValue, false));
            }
            else
            {
                unavailable.Add($"{definition.ChineseName}（{definition.Key}）");
            }
        }

        var info = new FileInfo(path);
        _log.Info("PROPERTIES",
            $"读取属性面板：{path}; Minecraft={minecraftVersion}; 可编辑={values.Count}; " +
            $"版本未提供={unavailable.Count}");
        return new ServerPropertiesSnapshot(
            path, minecraftVersion, info.LastWriteTimeUtc, info.Length,
            ContentHash(path), values, unavailable);
    }

    public ServerPropertiesSaveResult Save(
        ServerPropertiesSnapshot snapshot,
        IReadOnlyDictionary<string, string> requestedValues)
    {
        var path = Path.GetFullPath(snapshot.FilePath);
        if (!File.Exists(path))
            throw new FileNotFoundException("保存前 server.properties 已不存在。", path);
        var currentInfo = new FileInfo(path);
        if (currentInfo.LastWriteTimeUtc != snapshot.LastWriteTimeUtc ||
            currentInfo.Length != snapshot.FileLength ||
            !ContentHash(path).Equals(snapshot.ContentHash, StringComparison.Ordinal))
        {
            throw new IOException(
                "server.properties 在面板打开后被服务端或其他程序修改。为避免覆盖新内容，请点击“重新读取”后再修改。");
        }

        var definitions = snapshot.Values.ToDictionary(
            value => value.Definition.Key,
            value => value.Definition,
            StringComparer.OrdinalIgnoreCase);
        var validated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in requestedValues)
        {
            if (!definitions.TryGetValue(pair.Key, out var definition))
                throw new InvalidDataException($"设置面板提交了未知字段：{pair.Key}");
            validated[pair.Key] = Validate(definition, pair.Value);
        }

        var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
        var before = ReadValues(lines);
        var changed = new List<string>();
        foreach (var value in snapshot.Values)
        {
            var key = value.Definition.Key;
            if (!validated.TryGetValue(key, out var newValue))
                continue;
            if (!before.TryGetValue(key, out var oldValue) ||
                !oldValue.Equals(newValue, StringComparison.Ordinal))
            {
                changed.Add(key);
            }
            SetValue(lines, key, newValue);
        }

        if (changed.Count == 0)
            return new ServerPropertiesSaveResult([], ReadPort(validated));

        var backup = path + ".pilot-backup";
        File.Copy(path, backup, overwrite: true);
        var temporary = path + ".pilot.tmp";
        File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        _log.Info("PROPERTIES",
            $"已原子保存 server.properties；修改字段={string.Join(",", changed)}；备份={backup}");
        return new ServerPropertiesSaveResult(changed, ReadPort(validated));
    }

    private static IReadOnlyList<ServerPropertyDefinition> BuildDefinitions(
        string minecraftVersion,
        IReadOnlyDictionary<string, string> current)
    {
        var version = ParseVersion(minecraftVersion);
        var namedGameValues =
            UsesNamedValue(current, "difficulty") ||
            UsesNamedValue(current, "gamemode") ||
            version is not null && version >= new Version(1, 14);
        var spectatorSupported = version is null || version >= new Version(1, 8);

        var difficulties = namedGameValues
            ?
            new List<ServerPropertyChoice>
            {
                new("peaceful", "和平"),
                new("easy", "简单"),
                new("normal", "普通"),
                new("hard", "困难")
            }
            :
            new List<ServerPropertyChoice>
            {
                new("0", "和平"),
                new("1", "简单"),
                new("2", "普通"),
                new("3", "困难")
            };
        var gameModes = new List<ServerPropertyChoice>
        {
            new(namedGameValues ? "survival" : "0", "生存"),
            new(namedGameValues ? "creative" : "1", "创造"),
            new(namedGameValues ? "adventure" : "2", "冒险")
        };
        if (spectatorSupported)
            gameModes.Add(new(namedGameValues ? "spectator" : "3", "旁观"));
        PreserveUnknownChoice(difficulties, current, "difficulty");
        PreserveUnknownChoice(gameModes, current, "gamemode");

        var simulationSupported = version is null || version >= new Version(1, 18);
        return
        [
            Bool("online-mode", "正版验证", "验证玩家账号与身份，公网服务端强烈建议保持启用。",
                "连接与安全", "true", security: true),
            Bool("white-list", "白名单", "只允许白名单内玩家加入；玩家名单仍需在控制台管理。",
                "连接与安全", "false"),
            Bool("enforce-whitelist", "强制白名单", "从白名单移除的在线玩家也会被移出服务器。",
                "连接与安全", "false", addWhenMissing:
                    version is null || version >= new Version(1, 16)),
            Integer("max-players", "最大玩家数", "允许同时在线的最大玩家数量。",
                "连接与安全", "20", 1, 1000),
            Integer("server-port", "服务端端口", "朋友连接及端口映射使用的 TCP 端口。",
                "连接与安全", "25565", 1, 65535),
            Bool("hide-online-players", "隐藏在线玩家列表", "不在服务器状态查询中公开玩家名单。",
                "连接与安全", "false", addWhenMissing: false),
            Bool("enable-status", "允许服务器列表查询", "关闭后客户端服务器列表无法正常显示状态。",
                "连接与安全", "true", addWhenMissing: false),

            Bool("allow-flight", "允许飞行", "避免允许飞行的玩家被服务器判定并踢出；并不直接赋予飞行能力。",
                "玩家与玩法", "false"),
            Bool("pvp", "玩家互相伤害（PVP）", "控制玩家之间能否造成伤害；仅在该版本实际提供此字段时显示。",
                "玩家与玩法", "true", addWhenMissing: false),
            Choice("difficulty", "游戏难度", "控制怪物伤害、饥饿等全局难度。",
                "玩家与玩法", namedGameValues ? "easy" : "1", difficulties),
            Choice("gamemode", "默认游戏模式", "新玩家首次进入时使用的游戏模式。",
                "玩家与玩法", namedGameValues ? "survival" : "0", gameModes),
            Bool("force-gamemode", "强制默认游戏模式", "玩家加入时强制切换到上面选择的默认模式。",
                "玩家与玩法", "false"),
            Bool("hardcore", "极限模式", "死亡惩罚更严厉；启用前请确认已有世界和玩家数据可接受。",
                "玩家与玩法", "false", security: true),
            Bool("enable-command-block", "启用命令方块", "允许命令方块运行；仅在该版本实际提供此字段时显示。",
                "玩家与玩法", "false", addWhenMissing: false),
            Integer("player-idle-timeout", "空闲踢出时间（分钟）", "0 表示不因挂机自动踢出玩家。",
                "玩家与玩法", "0", 0, 10080),

            Text("motd", "服务器介绍（MOTD）", "显示在多人游戏服务器列表中的介绍，支持中文。",
                "世界与性能", "A Minecraft Server created by Server Pilot", 256),
            Integer("view-distance", "视距（区块）", "数值越大视野越远，也会增加内存、CPU 与网络压力。",
                "世界与性能", "10", 2, 32),
            Integer("simulation-distance", "模拟距离（区块）", "生物、红石等保持活动的距离；1.18 及以后提供。",
                "世界与性能", "10", 2, 32, addWhenMissing: simulationSupported),
            Integer("spawn-protection", "出生点保护半径", "0 表示关闭；非管理员不能在保护范围内修改方块。",
                "世界与性能", "16", 0, 64),
            Bool("generate-structures", "生成世界结构", "控制新生成区块中是否出现村庄、要塞等结构。",
                "世界与性能", "true"),
            Bool("allow-nether", "允许下界", "仅在当前版本实际提供此字段时显示。",
                "世界与性能", "true", addWhenMissing: false),
            Bool("spawn-monsters", "生成怪物", "仅在当前版本实际提供此字段时显示。",
                "世界与性能", "true", addWhenMissing: false),
            Bool("spawn-animals", "生成动物", "仅在当前版本实际提供此字段时显示。",
                "世界与性能", "true", addWhenMissing: false),
            Bool("spawn-npcs", "生成村民等 NPC", "仅在当前版本实际提供此字段时显示。",
                "世界与性能", "true", addWhenMissing: false)
        ];
    }

    private static ServerPropertyDefinition Bool(
        string key, string name, string description, string category, string defaultValue,
        bool addWhenMissing = true, bool security = false) =>
        new(key, name, description, category, ServerPropertyEditorKind.Boolean,
            defaultValue, AddWhenMissing: addWhenMissing, SecuritySensitive: security);

    private static ServerPropertyDefinition Integer(
        string key, string name, string description, string category, string defaultValue,
        int minimum, int maximum, bool addWhenMissing = true) =>
        new(key, name, description, category, ServerPropertyEditorKind.Integer,
            defaultValue, minimum, maximum, AddWhenMissing: addWhenMissing);

    private static ServerPropertyDefinition Text(
        string key, string name, string description, string category, string defaultValue,
        int maximumLength) =>
        new(key, name, description, category, ServerPropertyEditorKind.Text,
            defaultValue, Maximum: maximumLength);

    private static ServerPropertyDefinition Choice(
        string key, string name, string description, string category, string defaultValue,
        IReadOnlyList<ServerPropertyChoice> choices) =>
        new(key, name, description, category, ServerPropertyEditorKind.Choice,
            defaultValue, Choices: choices);

    private static string Validate(ServerPropertyDefinition definition, string value)
    {
        if (definition.EditorKind != ServerPropertyEditorKind.Text)
            value = value.Trim();
        switch (definition.EditorKind)
        {
            case ServerPropertyEditorKind.Boolean:
                if (!bool.TryParse(value, out var boolean))
                    throw new ArgumentException($"{definition.ChineseName} 必须选择启用或关闭。");
                return boolean ? "true" : "false";

            case ServerPropertyEditorKind.Integer:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var number))
                    throw new ArgumentException($"{definition.ChineseName} 必须填写整数。");
                if (definition.Minimum is int minimum && number < minimum ||
                    definition.Maximum is int maximum && number > maximum)
                {
                    throw new ArgumentOutOfRangeException(definition.Key,
                        $"{definition.ChineseName} 必须在 {definition.Minimum}–{definition.Maximum} 之间。");
                }
                return number.ToString(CultureInfo.InvariantCulture);

            case ServerPropertyEditorKind.Choice:
                if (definition.Choices?.Any(choice =>
                        choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) != true)
                    throw new ArgumentException($"{definition.ChineseName} 的选项不受当前版本支持。");
                return definition.Choices.First(choice =>
                    choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase)).Value;

            case ServerPropertyEditorKind.Text:
                if (value.Contains('\r') || value.Contains('\n'))
                    throw new ArgumentException($"{definition.ChineseName} 不能包含换行。");
                if (definition.Maximum is int maximumLength && value.Length > maximumLength)
                    throw new ArgumentException(
                        $"{definition.ChineseName} 最多允许 {maximumLength} 个字符。");
                return value;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static Dictionary<string, string> ReadValues(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (!TrySplitProperty(line, out var key, out var value))
                continue;
            values[key] = Unescape(value);
        }
        return values;
    }

    private static bool TrySplitProperty(string line, out string key, out string value)
    {
        key = "";
        value = "";
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is '#' or '!')
            return false;
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (!escaped && character is '=' or ':')
            {
                key = line[..index].Trim();
                value = line[(index + 1)..].TrimStart();
                return key.Length > 0;
            }
            if (character == '\\' && !escaped)
                escaped = true;
            else
                escaped = false;
        }
        return false;
    }

    private static void SetValue(List<string> lines, string key, string value)
    {
        var foundIndex = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (TrySplitProperty(lines[index], out var existingKey, out _) &&
                existingKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                foundIndex = index;
        }
        var encoded = $"{key}={Escape(value)}";
        if (foundIndex >= 0)
            lines[foundIndex] = encoded;
        else
            lines.Add(encoded);
    }

    private static string NormalizeChoiceValue(
        ServerPropertyDefinition definition,
        string value)
    {
        if (definition.EditorKind != ServerPropertyEditorKind.Choice)
            return value;
        if (definition.Choices?.Any(choice =>
                choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase)) == true)
            return definition.Choices.First(choice =>
                choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase)).Value;

        var mapped = MapChoiceAlias(definition.Key, value);
        return mapped is not null &&
               definition.Choices?.Any(choice => choice.Value == mapped) == true
            ? mapped
            : value;
    }

    private static string? MapChoiceAlias(string key, string value) =>
        key switch
        {
            "difficulty" => value.ToLowerInvariant() switch
            {
                "0" => "peaceful", "1" => "easy", "2" => "normal", "3" => "hard",
                "peaceful" => "0", "easy" => "1", "normal" => "2", "hard" => "3",
                _ => null
            },
            "gamemode" => value.ToLowerInvariant() switch
            {
                "0" => "survival", "1" => "creative", "2" => "adventure", "3" => "spectator",
                "survival" => "0", "creative" => "1", "adventure" => "2", "spectator" => "3",
                _ => null
            },
            _ => null
        };

    private static void PreserveUnknownChoice(
        ICollection<ServerPropertyChoice> choices,
        IReadOnlyDictionary<string, string> current,
        string key)
    {
        if (!current.TryGetValue(key, out var value) ||
            choices.Any(choice =>
                choice.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
            return;
        var mapped = MapChoiceAlias(key, value);
        if (mapped is not null && choices.Any(choice => choice.Value == mapped))
            return;
        choices.Add(new(value, $"保留当前值：{value}"));
    }

    private static bool UsesNamedValue(
        IReadOnlyDictionary<string, string> values,
        string key) =>
        values.TryGetValue(key, out var value) &&
        !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static Version? ParseVersion(string text)
    {
        var prefix = new string(text.TakeWhile(character =>
            char.IsDigit(character) || character == '.').ToArray()).TrimEnd('.');
        if (string.IsNullOrWhiteSpace(prefix))
            return null;
        return Version.TryParse(prefix, out var version) ? version : null;
    }

    private static int? ReadPort(IReadOnlyDictionary<string, string> values) =>
        values.TryGetValue("server-port", out var text) &&
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            ? port
            : null;

    private static string ContentHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string PropertiesPath(string serverDirectory)
    {
        var directory = Path.GetFullPath(serverDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"服务端目录不存在：{directory}");
        return Path.Combine(directory, "server.properties");
    }

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                builder.Append(value[index]);
                continue;
            }
            var escaped = value[++index];
            switch (escaped)
            {
                case 't': builder.Append('\t'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 'f': builder.Append('\f'); break;
                case 'u' when index + 4 < value.Length &&
                                   ushort.TryParse(value.AsSpan(index + 1, 4),
                                       NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                                       out var unicode):
                    builder.Append((char)unicode);
                    index += 4;
                    break;
                default: builder.Append(escaped); break;
            }
        }
        return builder.ToString();
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '\\': builder.Append(@"\\"); break;
                case '\t': builder.Append(@"\t"); break;
                case '\n': builder.Append(@"\n"); break;
                case '\r': builder.Append(@"\r"); break;
                case '\f': builder.Append(@"\f"); break;
                case ' ' when index == 0: builder.Append(@"\ "); break;
                default:
                    if (character < 0x20 || character > 0x7E)
                        builder.Append(@"\u").Append(((int)character).ToString("X4"));
                    else
                        builder.Append(character);
                    break;
            }
        }
        return builder.ToString();
    }
}
