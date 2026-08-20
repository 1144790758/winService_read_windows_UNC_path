# AGENTS.md

## 项目概述

本项目用于将普通的 exe 程序打包/包装为 Windows 服务（Windows Service），使其可以：

- 通过 Windows 服务控制管理器（SCM）安装、启动、停止、卸载
- 随系统启动自动运行（无人值守）
- 在宿主 exe 崩溃时自动重启子进程
- 记录运行日志，便于运维排查

## 技术选型

- **语言/框架**: C# / .NET 8 (LTS)，Worker Service 模板
- **目标平台**: Windows 10/11、Windows Server 2016+（x64）
- **构建工具**: dotnet CLI（`dotnet build`、`dotnet publish`）
- **服务宿主方式**: 使用 `BackgroundService` 作为服务主体，内部以 `Process` 启动并托管目标 exe 子进程

选择理由：.NET 对 Windows SCM 的原生支持最成熟（服务生命周期、事件日志、优雅停止），发布为单文件自包含 exe 后部署简单。

## 项目结构（规划）

```
windows_service/
├── WindowsServiceHost/          # .NET Worker Service 主项目
│   ├── Program.cs               # 入口，注册服务
│   ├── ExeHostWorker.cs         # 核心：启动/监控/重启子进程 exe
│   ├── appsettings.json         # 配置：exe 路径、参数、重启策略
│   └── WindowsServiceHost.csproj
└── README.md
```

## 常用命令

```bash
# 构建
dotnet build WindowsServiceHost -c Release

# 发布为单文件自包含 exe
dotnet publish WindowsServiceHost -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# 安装/卸载服务（需管理员权限）
sc create "MyExeService" binPath="C:\path\to\WindowsServiceHost.exe"
sc delete "MyExeService"
```

## 核心设计要求

1. **子进程生命周期托管**：服务启动时拉起目标 exe；服务停止时必须优雅终止子进程（先尝试正常退出，超时后强制 kill）。
2. **崩溃自动重启**：监控子进程退出，非预期退出时按配置的策略（重试间隔、最大次数）重启。
3. **配置驱动**：目标 exe 路径、工作目录、命令行参数、重启策略全部放在 `appsettings.json`。
4. **日志**：默认写入 Windows 事件日志（EventLog），可选同时写入文件日志。
5. **路径处理**：exe 路径支持相对服务安装目录的解析，避免硬编码绝对路径。
6. **最终效果**：最终我想使用一个自己写的java程序运行测试，注意该java程序会尝试读取网路共享目录的文件

## 编码约定

- 遵循 C# 官方编码规范（PascalCase 公共成员、camelCase 局部变量）
- 异步代码统一使用 `async/await` + `CancellationToken`，服务停止通过取消令牌传递
- 不吞异常：子进程监控循环中的异常必须记录日志
- 提交前确保 `dotnet build -c Release` 无警告

## 当前进度（2026-08-20 迁移交接）

本项目在旧机器（CN-5CG60803MG，用户 holi）上开发，因该机器无管理员权限、无法创建 SMB 共享做网络共享测试，整体迁移到本机继续。

### 迁移范围（目标机器已有同名目录时）

目标机器上若已存在 `WindowsServiceHost`、`jdk-25`、`jre25`，处理方式：

- **WindowsServiceHost/：必须用本次开发版本覆盖**（含本次全部新代码；bin、obj 可不拷，重新 build 即可）。若目标机器的是其他来源的旧版本，直接覆盖源码文件。
- **jdk-25/：可以不拷**。任何 JDK 9+ 都能编译 test-java；用目标机器已有的即可（改 `jdk-25/bin/javac` 为对应路径）。
- **jre25/：可以不拷，但要验证**。用目标机器已有 JDK 检查：`jre25\bin\java -version`。若没有或版本不符，重新生成：
  ```bash
  jdk-25/bin/jlink --module-path "jdk-25/jmods" --add-modules ALL-MODULE-PATH --output jre25 --no-man-pages --no-header-files
  ```
- **必须拷贝的小文件**：`test-java/`、`test-data/`、`AGENTS.md`、`README.md`。
- 拷完后最终目录布局必须与下方"实际目录结构"一致（jre25、test-java 等与 WindowsServiceHost 同级），因为 appsettings.json 依赖该相对层级。

### 实际目录结构（已实现）

```
windows_service/
├── WindowsServiceHost/          # .NET 8 Worker Service（已完成）
│   ├── Program.cs               # 入口；显式从 exe 目录加载 appsettings.json
│   ├── ExeHostWorker.cs         # 核心：启动/监控/重启/优雅停止子进程
│   ├── ExeHostOptions.cs        # 配置模型
│   ├── FileLogger.cs            # 可选文件日志 Provider
│   ├── appsettings.json         # 当前配置为用 jre25 运行 test-java
│   └── WindowsServiceHost.csproj
├── jdk-25/                      # OpenJDK 25（含 javac，仅编译测试程序用）
├── jre25/                       # 用 jlink 从 jdk-25 提取的完整运行环境
├── test-java/Main.java          # 测试程序：循环读取指定文件（本地或 UNC），编译产物在 test-java/out
├── test-data/sample.txt         # 本地测试文件
└── README.md                    # 部署/安装/网络共享注意事项
```

### 已完成

1. jre25 运行环境提取（jlink 全模块）。
2. WindowsServiceHost 全部代码：子进程托管、崩溃自动重启（间隔/最大次数/稳定运行后计数清零）、退出码 0 视为正常退出、优雅停止（CloseMainWindow → 超时 kill 进程树）、事件日志 + 文件日志、相对路径解析。构建 0 警告，单文件自包含 publish 已验证。
3. 控制台模式功能验证通过：启动 Java 子进程、周期读文件、stdout 转发日志、手动 kill 子进程后 5 秒自动重启。
4. **真实服务安装测试通过**：`sc create/start/stop/delete` 全流程验证，服务部署到 `C:\MyService` 公共目录。
5. **网络共享（UNC）测试通过**：Java 子进程成功周期读取 `\\DESKTOP-0O93IBK\test\sample.txt`。关键：服务以本地账号运行 + 双机同名同密码账号 + 共享/NTFS 双权限放行。
6. **workspace 特性**：`Program.cs` 支持 `--workspace <dir>` 指定配置加载与相对路径解析的基准目录，默认 exe 目录；`appsettings.json` 改用浅相对路径。
7. 文档齐全：`README.md`、`DEPLOY.md`（部署指南）、`TROUBLESHOOTING.md`（踩坑排错）、`.gitignore`。

### 未完成（可选）

1. **替换为用户自己的 Java 程序**：改 `ExeHost:Arguments`（如 `-jar app.jar`）即可，test-java 只是占位测试程序。这是唯一的后续可选项，框架本身已完备。

### 已知坑与注意事项

- **配置加载**：SCM 启动服务时工作目录是 `C:\Windows\System32`。`Program.cs` 用 `PhysicalFileProvider` 显式从 workspace 基准目录加载 appsettings.json，改动入口时不要破坏这一点。基准目录可用 `--workspace <dir>` 指定，默认 exe 目录；`TargetExePath`/`WorkingDirectory`/日志路径的相对解析都以它为基准。
- **dotnet 环境**：旧机器上 PATH 中的 `dotnet`（C:\Program Files\dotnet）只有运行时无 SDK，SDK 在 `%LOCALAPPDATA%\Microsoft\dotnet`（8.0.424）。本机若报 "No SDKs were found"，用完整路径或修正 PATH/DOTNET_ROOT。
- **appsettings.json 开发期相对路径**：`..\..\..\..\jre25\...` 是相对 `bin\Release\net8.0` 的 4 层向上，仅适用于 `dotnet run`/直接跑 build 产物。真实部署时应把 jre25、jar 放在 exe 旁并改为浅相对路径。
- **控制台中文乱码**：GBK 控制台输出乱码属正常，排查问题看 `logs/service.log`（UTF-8，位于 exe 同目录 logs 下）。
- **Java 程序语义**：子进程退出码 0 = 正常退出，服务随之停止且不再重启；测试程序是死循环，不会自行退出。
- **可重建产物**：`WindowsServiceHost/bin`、`obj` 可删；发布产物在 `bin/Release/net8.0/win-x64/publish/`。

### 快速开始

```bash
dotnet build WindowsServiceHost -c Release                      # 应 0 警告
dotnet WindowsServiceHost/bin/Release/net8.0/WindowsServiceHost.dll   # 控制台模式验证
jdk-25/bin/javac -d test-java/out test-java/Main.java           # 重新编译测试程序
```
