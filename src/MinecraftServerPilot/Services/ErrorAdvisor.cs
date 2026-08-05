namespace MinecraftServerPilot.Services;

public static class ErrorAdvisor
{
    public static string Analyze(string output, int exitCode)
    {
        var text = output.ToLowerInvariant();
        var advice = new List<string>();
        if (text.Contains("unsupportedclassversionerror") || text.Contains("class file version"))
            advice.Add("Java 主版本不匹配。程序会重新探测该服务端要求的 Java，并避免使用“越新越好”的错误策略。");
        if (text.Contains("address already in use") || text.Contains("failed to bind to port"))
            advice.Add("服务端端口被占用。关闭同端口程序，或在 server.properties 中修改 server-port。");
        if (text.Contains("could not reserve enough space") || text.Contains("outofmemoryerror"))
            advice.Add("内存配置过高或内存不足。降低最大/最小内存，关闭占用内存的软件后重试。");
        if (text.Contains("you need to agree to the eula") || text.Contains("eula=false"))
            advice.Add("EULA 尚未生效。确认 eula.txt 与服务端位于同一工作目录，内容为 eula=true。");
        if (text.Contains("mod resolution encountered") || text.Contains("incompatible mod set") ||
            text.Contains("requires version") || text.Contains("missing mandatory dependencies"))
            advice.Add("存在模组版本或前置依赖冲突。检查报错中 requires/missing 后面的模组名与版本范围。");
        if (text.Contains("invalid or corrupt jarfile") || text.Contains("zip end header not found"))
            advice.Add("JAR 文件损坏。删除对应 JAR 与 .part 文件后重新下载；程序下载的新文件会进行哈希校验。");
        if (text.Contains("access is denied") || text.Contains("accessexception"))
            advice.Add("文件被占用或被安全软件阻止。关闭同目录服务端/编辑器，并将安装目录加入安全软件信任列表。");
        if (text.Contains("unable to access jarfile"))
            advice.Add("启动文件不存在或路径错误。不要移动单个 JAR；请整体移动服务端文件夹。");
        if (text.Contains("unknownhostexception") || text.Contains("connection timed out"))
            advice.Add("依赖下载阶段网络失败。检查 DNS/代理，稍后重试；Forge/Fabric 的库文件也需要联网下载。");
        if (text.Contains("failed to load data packs") || text.Contains("datapack"))
            advice.Add("数据包与当前版本不兼容。移出 world/datapacks 中最近加入的数据包后再试。");
        if (advice.Count == 0)
            advice.Add($"进程退出码为 {exitCode}。请查看完整日志末尾的首个 Caused by / ERROR；该位置通常是根因，而最后一行往往只是结果。");
        return string.Join(Environment.NewLine, advice.Distinct());
    }
}
