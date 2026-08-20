# WindowsServiceHost

将普通 exe 程序托管为 Windows 服务:由 SCM 安装/启动/停止,随系统自启,子进程崩溃自动重启,日志写入事件日志与文件。

> **相关文档**
> - `DEPLOY.md` — 完整部署指南(环境准备、发布、配置共享、服务账号、安装启动)
> - `TROUBLESHOOTING.md` — 开发踩坑与排错(权限、服务账号、UNC 认证等)
> - `AGENTS.md` — 项目背景与技术选型

## 构建与发布

```bash
dotnet build WindowsServiceHost -c Release
dotnet publish WindowsServiceHost -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# 产物: WindowsServiceHost/bin/Release/net8.0/win-x64/publish/WindowsServiceHost.exe
```

## 部署目录布局(示例)

```
deploy/
├── WindowsServiceHost.exe
├── appsettings.json          # 与 exe 同目录
├── jre25/                    # Java 运行环境(可选)
└── myapp.jar 或 your-app.exe
```

配置中 `TargetExePath` 使用相对路径时,按 workspace 基准目录解析。默认 workspace 为 exe 所在目录;若配置与 exe 不在同一目录,可用 `--workspace <dir>` 指定基准目录(配置加载、相对路径、日志路径都以它为基准):

```bat
WindowsServiceHost.exe --workspace C:\MyService
```

## 配置(appsettings.json)

| 配置项 | 说明 | 默认值 |
| --- | --- | --- |
| ExeHost:TargetExePath | 目标程序路径(相对 exe 目录或绝对路径) | 必填 |
| ExeHost:Arguments | 命令行参数 | 空 |
| ExeHost:WorkingDirectory | 子进程工作目录,留空为 exe 目录 | 空 |
| ExeHost:RestartDelaySeconds | 崩溃后重启等待秒数 | 5 |
| ExeHost:MaxRestarts | 连续崩溃重启上限,超过后服务停止 | 10 |
| ExeHost:ResetCountAfterSeconds | 稳定运行该秒数后崩溃计数清零 | 60 |
| ExeHost:ShutdownTimeoutSeconds | 停止时等待子进程退出的秒数,超时强制 kill | 15 |
| Logging:FileLog:Enabled | 是否同时写文件日志 | true |
| Logging:FileLog:Path | 日志文件路径,留空为 `logs/service.log` | 空 |

子进程退出码为 0 视为正常退出,服务随之停止;非 0 视为崩溃,按策略重启。

## 安装/卸载服务(管理员权限)

```bat
sc create "MyExeService" binPath= "C:\deploy\WindowsServiceHost.exe" start= auto
sc start MyExeService
sc stop MyExeService
sc delete MyExeService
```

事件日志位置:`事件查看器 → Windows 日志 → 应用程序`,来源为 `MyExeService`。

## Java 测试程序

`test-java/Main.java`:循环读取指定文件(本地或 UNC 路径)并打印。

```bash
jdk-25/bin/javac -d test-java/out test-java/Main.java
# 运行参数: <文件路径> [间隔秒数]
```

`appsettings.json` 中当前配置为使用 `jre25/bin/java.exe` 运行该测试程序。替换为自己的 Java 程序时,修改 `Arguments`(如 `-jar myapp.jar`)即可。

## 网络共享目录注意事项

服务默认以 **LocalSystem** 账户运行,访问网络共享(UNC 路径)无凭据而失败(日志表现为 `user=机器名$` + `AccessDeniedException`)。解决方式:

1. 让服务以**本地用户**身份运行(工作组环境需在**本机和共享机各建一个同名同密码的本地账号**):
   ```bat
   sc config MyExeService obj= ".\svcuser" password= "xxx"
   ```
2. ⚠️ **不要用微软账户**当服务账号(密码机制不同,会一直认证失败,报"用户名或密码不正确")。
3. 共享目录需同时放行**共享权限**和 **NTFS 安全权限**。

可先用 `test-java` 读取 UNC 路径验证:把 `appsettings.json` 中的文件路径改为
`\\\\server\\share\\file.txt`。程序读取失败时会打印异常但不会退出,便于观察权限问题。

详细的共享配置、账号创建、服务安装步骤见 `DEPLOY.md`;各类报错的根因与对策见 `TROUBLESHOOTING.md`。

## 本地验证方式(无需安装服务)

```bash
dotnet WindowsServiceHost/bin/Release/net8.0/WindowsServiceHost.dll
```

控制台直接运行,行为与作为服务运行时一致(日志同时写入 `logs/service.log`)。
