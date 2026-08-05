# 验收测试矩阵

执行环境：Windows 11 x64，.NET SDK 8.0.423。所有临时服务端目录均由测试在校验路径边界后清理。

| 场景 | 验收内容 | 结果 |
|---|---|---|
| 基础规则与下载回退 | Java 8/16/17/21/25 边界；诊断指纹；损坏 HTTP 源被 SHA-256 拒绝后切换健康源 | 通过 |
| 下载失速与取消 | 响应体半途停止发送；20 秒无数据自动保留断点并重连；用户取消可立即中断等待且保留 `.part` | 通过 |
| 中文属性管理 | 1.7.10 数字难度/模式；26.2 隐藏缺失旧字段；中文 MOTD 转义往返；注释/未知键保留；SHA-256 并发修改保护 | 通过 |
| 在线提供器矩阵 | Mojang 1.7.10 SHA-1；Paper 1.21.11 稳定构建；Fabric 1.20 稳定 Loader；Forge 1.7.10 数字最新版 | 通过 |
| Vanilla 1.7.10 | Java 8；官方 JAR；两次 `Done`；安全停服；EULA/世界/便携交付；已有服务端内存更新 | 通过 |
| Vanilla 1.17.1（缺 Java） | 未发现 Java 16；Adoptium 路径失败后切 Azul；73 MB 便携 JRE；版本探测；两次启动 | 通过 |
| Paper 1.21.11 | PaperMC SHA-256；Java 21；首次下载 Mojang 核心并应用补丁；两次启动与世界清理 | 通过 |
| Fabric 1.20 | Fabric Loader；自动下载 Fabric API + spark；首次识别 44 个模组条目；全部清理；纯净复测只保留基础项 | 通过 |
| Forge 1.7.10 | 官方旧安装器；Java 8；universal JAR；两次启动 | 通过（测试组件不可用，诚实标记 LoaderOnly） |
| Forge 1.20 | 官方现代安装器；Java 17；首次 Mojang 下载超时后兼容重试；win_args 启动；测试组件状态必须 Passed | 通过 |
| 失败会话续作 | 无效版本第一次失败后写 Failed；第二次复用同目录与 CreatedAt；显示“恢复” | 通过 |
| 引导取消 | 解析完成后出现发行版/Java确认；用户拒绝时标记 Cancelled；确认前无 server.jar 下载 | 通过 |
| EULA + 端口故障注入 | 首次启动前将 EULA 改回 false 并抢占 25565；连续修复 EULA、改用 25566、完成双启动、生成恢复报告 | 通过 |
| JVM 内存故障注入 | 测试 Java 首次返回 `Could not reserve enough space`；降至 512–2048 MB；重建 JSON/CMD；转发真实 Java 后双启动 | 通过 |
| Java 手动策略 | 请求不存在的 Java 主版本且关闭自动下载；在创建下载目录前暂停，并返回官方手动安装说明 | 通过 |
| WPF 启动与视觉 | 主窗口真实启动；版本清单 903 项；可访问控件树完整；无应用崩溃日志 | 通过 |

## 可重复命令

```powershell
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --providers
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --session-resume
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --guided-cancel
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --recovery-faults
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --recovery-memory
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --integration
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --integration-paper
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --integration-fabric
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --integration-forge
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --integration-forge-modern
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --integration-java-bootstrap
```

真实上游与网络会随时间变化。在线测试失败时应先阅读持久化日志，区分程序回归、上游停止发布和暂时网络故障。
