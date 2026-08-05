# Minecraft Server Pilot

面向 Windows 10/11 的 Minecraft Java 版服务端安装、验证与故障诊断工具。

## 已实现

- 原版 Vanilla：读取 Mojang 官方版本清单，覆盖清单中带服务端 JAR 的正式版与快照。
- Paper 插件服：只选择 PaperMC 官方稳定构建。
- Fabric 模组服：自动选择稳定 Loader 与 Installer。
- Forge 模组服：解析官方 Maven 元数据，兼容新旧安装器与两种启动结构。
- Java 精确匹配：优先复用本机同主版本 Java；用户可选择自动便携下载（Temurin 失败再切 Azul）或仅使用已安装 Java并暂停获取手动说明。
- 下载网状容错：连接超时、每源重试、断点续传、官方/权威/国内镜像回退、SHA/大小校验、原子落盘。
- 安装验证：自动接受 EULA；可下载一次性 spark 测试组件；启动成功后安全停服、清理测试组件与世界，再纯净启动一次。
- Fabric 测试依赖：自动解析并安装 Fabric API 前置；测试完成后与 spark 一起清理。
- 自动纠错：启动阶段可重写未生效 EULA、切换被抢占端口、降低无法分配的内存、隔离失败测试组件并复测。
- 可恢复会话：失败或取消后保存 `.pilot-session.json`，同一请求会续作原目录并复用完整下载/断点。
- 真正的引导模式：发行版与 Java、EULA 与安全配置、测试组件、最终交付分别确认。
- 故障报告：WPF、后台任务、下载、安装器和服务端进程均保留完整异常链与 UTF-8 日志。
- 便携交付：`server-pilot.json`、`Start-Server.cmd`、`NETWORK-GUIDE.txt` 与独立便携 Java（需要时）。
- 交付后管理：载入已有服务端，修改内存并原子更新 JSON/启动文件，再在独立控制台启动。
- 中文属性面板：按当前 Minecraft 版本和真实 `server.properties` 字段显示正版验证、白名单、玩家数、难度、模式、视距等常用设置；未知字段原样保留，保存前自动备份。
- 联机诊断：显示局域网地址和端口监听状态；经用户确认与 UAC 同意，可只为指定 Java、TCP 端口和专用网络创建防火墙规则。

## 构建

```powershell
dotnet build .\MinecraftServerPilot.sln -c Release
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release
dotnet run --project .\tests\MinecraftServerPilot.SmokeTests\MinecraftServerPilot.SmokeTests.csproj -c Release -- --providers --session-resume --guided-cancel
dotnet publish .\src\MinecraftServerPilot\MinecraftServerPilot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\win-x64
```

最终用户只需运行 `dist\win-x64\MinecraftServerPilot.exe`，不需要预装 .NET，也不需要运行 PowerShell 脚本。

## Java Pilot 独立工具

`src/JavaPilot` 是面向 Windows 10/11 x64 的独立 Java 自动安装器：

- 在每个 Java 选项前显示推荐的 Minecraft 版本范围。
- 启动后通过 Mojang 正式版元数据在线校准最新 Minecraft 所需 Java；离线时使用本地缓存和内置表，未来出现新 Java 主版本时自动增加选项。
- 支持 Java 6、7、8、11、16、17、21、25；Java 6/7 会显示停止安全维护警告。
- Eclipse Temurin、Azul Zulu、BellSoft Liberica、Amazon Corretto 自动解析与跨供应商回退。
- 使用柔和的雾蓝浅色主题，日志控制台单独保留低眩光深色，主界面与 Java 管理器保持一致。
- 自动显示命令行实际调用的 PATH Java 与当前 `JAVA_HOME`；真实启动验证版本和位数，两者冲突或配置损坏时直接给出说明。
- 切换默认 Java 时会把目标 `bin` 写成用户 PATH 的明确首项并验证当前命令行；若系统 PATH 抢占优先级，可由用户确认 UAC 后把目标放到系统 PATH 首位。旧 Java 与其他 PATH 条目不会删除，修改前会保存完整备份。
- Java 管理器右上角显示环境备份数量，可在二次确认与 UAC 授权后清除全部 Java Pilot 环境备份；只删除备份目录顶层的 `environment-*.json`，不会修改 Java、`JAVA_HOME`、PATH 或其他文件。
- 默认 Java 的启动验证、环境写入和管理器操作采用全异步调用，不在 WPF 界面线程同步等待；操作期间禁用重复点击，并保留超时、取消和完整异常报告。
- 自动扫描 `JAVA_HOME`、PATH、注册表、常见厂商目录与 Java Pilot 目录，只复用通过真实启动验证的 64 位同主版本 Java。
- 使用 ZIP 便携安装，不调用 MSI，不需要管理员权限或点击安装向导。
- 18 秒无数据自动保留断点并重连；连续 30 秒低于 24 KB/s 自动换线，避免“有一点速度但永远下不完”。
- 支持哈希/大小校验、解压前后双重版本验证和原子部署。
- 可选设置当前用户 `JAVA_HOME` 与用户 `PATH`，不会修改系统级环境变量。
- 环境变量写入后执行回读校验，被组策略或安全软件拦截时不会误报成功。
- 取消安装时保留下载断点，临时解压目录会被安全清理。
- 记住上次选择的版本、目录和安装选项；可强制更新到最新补丁或覆盖修复受损的便携 Java。
- 管理 Java Pilot 安装的多个运行时：重新验证、设为当前用户默认、打开目录和带标记校验的安全卸载。
- 全机 Java 库存会合并 `JAVA_HOME`、PATH、注册表、嵌套 JRE 和厂商目录中的重复入口，并同时显示 32/64 位。
- Windows 已注册的外部 Java 通过其 MSI/厂商正式卸载入口处理；来源无法确认的系统目录不会获得删除权限。
- 用户目录中的独立便携 Java 可在严格目录验证后二次确认移入回收站；外部 Java 也可先复制并纳入 Java Pilot 管理，原安装保持不变。
- 支持导入用户已有的 Windows x64 JDK ZIP；导入时仍执行版本、位数、启动和部署后复核，不会信任文件名。
- 只覆盖带有 Java Pilot 管理标记的 `jdk-*` 目录，绝不覆盖碰巧同名的普通文件夹。

构建与测试：

```powershell
dotnet run --project .\tests\JavaPilot.SmokeTests\JavaPilot.SmokeTests.csproj -c Release
dotnet run --project .\tests\JavaPilot.SmokeTests\JavaPilot.SmokeTests.csproj -c Release -- --network
dotnet publish .\src\JavaPilot\JavaPilot.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\dist\java-pilot-win-x64
```

最终用户只需运行 `dist\java-pilot-win-x64\JavaPilot.exe`。PDB 仅用于开发者调试，不需要分发。

## 安全边界

- 不静默关闭正版验证、不自动暴露 RCON、不整体关闭防火墙。
- 不自动下载任意搜索结果中的未知模组；兼容性测试只使用已知的开源 spark 项目。没有匹配构建时安全降级为加载器级验证。
- 已有同名且非空的服务端目录不会被覆盖，会创建带时间戳的新目录。
- 自动清理只允许发生在本次新建的服务端目录内。
- Paper、Fabric、Forge 并非支持 Mojang 的每一个版本；无官方构建时会明确说明，不会伪造下载地址。

## 项目结构

- `src/MinecraftServerPilot`：WPF 桌面应用与核心服务。
- `src/JavaPilot`：独立 Java 下载、换源、便携安装与环境配置工具。
- `tests/MinecraftServerPilot.SmokeTests`：无第三方测试框架的核心规则与下载回退测试。
- `tests/JavaPilot.SmokeTests`：Java 版本映射、版本解析和在线供应商元数据测试。
- `docs/ARCHITECTURE.md`：状态机、来源策略和错误恢复说明。
- `docs/REQUIREMENTS-AUDIT.md`：逐条原始需求审计。
- `docs/TEST-MATRIX.md`：真实版本与恢复路径验收记录。
