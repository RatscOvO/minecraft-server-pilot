namespace MinecraftServerPilot.Models;

public enum ServerPropertyEditorKind
{
    Boolean,
    Integer,
    Text,
    Choice
}

public sealed record ServerPropertyChoice(string Value, string Label);

public sealed record ServerPropertyDefinition(
    string Key,
    string ChineseName,
    string Description,
    string Category,
    ServerPropertyEditorKind EditorKind,
    string DefaultValue,
    int? Minimum = null,
    int? Maximum = null,
    IReadOnlyList<ServerPropertyChoice>? Choices = null,
    bool AddWhenMissing = true,
    bool SecuritySensitive = false);

public sealed record ServerPropertyValue(
    ServerPropertyDefinition Definition,
    string Value,
    bool ExistedInFile);

public sealed record ServerPropertiesSnapshot(
    string FilePath,
    string MinecraftVersion,
    DateTime LastWriteTimeUtc,
    long FileLength,
    string ContentHash,
    IReadOnlyList<ServerPropertyValue> Values,
    IReadOnlyList<string> UnavailableKnownSettings);

public sealed record ServerPropertiesSaveResult(
    IReadOnlyList<string> ChangedKeys,
    int? ServerPort);
