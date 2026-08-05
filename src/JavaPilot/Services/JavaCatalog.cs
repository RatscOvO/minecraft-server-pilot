using JavaPilot.Models;

namespace JavaPilot.Services;

public static class JavaCatalog
{
    public static IReadOnlyList<JavaOption> Options { get; } =
    [
        new(
            25,
            "Minecraft 26.x（新版本以 Mojang 元数据为准）",
            "当前新版本服务端的优先选择。Java 25 是长期支持版本。"),
        new(
            21,
            "Minecraft 1.20.5–1.21.11",
            "现代原版、Paper、Fabric 与 Forge 服务端的常用版本。"),
        new(
            17,
            "Minecraft 1.18–1.20.4",
            "大量现代模组服和插件服使用；部分 1.17 加载器也要求 Java 17。"),
        new(
            16,
            "Minecraft 1.17–1.17.1 原版",
            "过渡版本，仅在服务端明确要求 Java 16 时安装。"),
        new(
            11,
            "部分旧版 Paper/服务端核心（约 1.12–1.16）",
            "不是主流原版首选，主要用于明确要求 Java 11 的第三方服务端。"),
        new(
            8,
            "Minecraft 1.7.10–1.16.5",
            "经典 Forge 模组服最常用；不要用更高版本 Java 强行替代。"),
        new(
            7,
            "Minecraft 1.2.5–1.6.4 与旧版模组",
            "仅用于无法在 Java 8 上运行的旧服务端。请勿将此旧运行时暴露到公网。",
            IsLegacy: true),
        new(
            6,
            "Minecraft Alpha/Beta 至约 1.5.2",
            "极旧兼容环境，只有 Azul 仍提供可自动获取的 Windows x64 归档版；仅建议离线测试。",
            IsLegacy: true)
    ];

    public static JavaOption Get(int major) =>
        Options.First(option => option.Major == major);

    public static IReadOnlyList<JavaOption> WithLatestMinecraftRelease(
        string latestRelease,
        int requiredJavaMajor)
    {
        var found = false;
        var calibrated = Options.Select(option =>
        {
            if (option.Major != requiredJavaMajor)
                return option;
            found = true;
            var description = option.Description.Contains(
                "Mojang 当前最新正式版",
                StringComparison.Ordinal)
                ? option.Description
                : option.Description +
                  $" Mojang 当前最新正式版 {latestRelease} 的元数据要求 Java {requiredJavaMajor}。";
            return option with { Description = description };
        }).ToList();

        if (!found)
        {
            calibrated.Add(new JavaOption(
                requiredJavaMajor,
                $"Minecraft {latestRelease}（当前最新正式版）",
                $"此项由 Mojang 最新正式版元数据在线识别；安装前仍会验证真实 Java 主版本。"));
        }

        return calibrated
            .OrderBy(option => option.IsLegacy)
            .ThenByDescending(option => option.Major)
            .ToArray();
    }
}
