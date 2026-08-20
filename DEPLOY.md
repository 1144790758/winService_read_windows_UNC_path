# WindowsServiceHost 部署指南

本文档描述从零开始：准备环境 → 编译发布 → 配置网络共享 → 创建共享账号 → 安装并启动服务 → 验证。
适用场景：把一个普通 exe（示例为 Java 程序）托管为 Windows 服务，并让它读取**网络共享（UNC）**上的文件。

> 配套文档：`README.md`（功能与配置项说明）、`TROUBLESHOOTING.md`（踩坑与排错）、`AGENTS.md`（项目背景）。

---

## 0. 架构速览

```
[本机 WindowsServiceHost.exe]  --托管-->  [子进程 java.exe]  --读取-->  \\共享机\share\file.txt
        (Windows 服务)                                            (SMB/UNC 网络共享)
```

关键点：
- 服务**不能**用默认 LocalSystem 访问网络共享（无凭据），必须换成本地用户账号运行。
- 工作组（非域）环境下，访问共享靠"**两台机器存在同名同密码的本地账号**"。
- 服务账号**不要用微软账户**，务必用纯本地账号（见 TROUBLESHOOTING 第 8 节）。

---

## 1. 环境准备

### 1.1 安装 .NET 8 SDK
构建需要 SDK（不是仅运行时）。验证：
```cmd
dotnet --version        :: 应输出 8.x.xxx
```
若报 "No SDKs were found"，SDK 可能在 `%LOCALAPPDATA%\Microsoft\dotnet`，修正 `PATH` 或 `DOTNET_ROOT`。

### 1.2 准备 Java 运行环境（jre25）
若已有 JDK 9+，用 `jlink` 提取运行环境（只需运行、不需 javac）：
```cmd
jdk-25\bin\jlink --module-path "jdk-25\jmods" --add-modules ALL-MODULE-PATH --output jre25 --no-man-pages --no-header-files
```
产物为 `jre25\` 目录，含 `jre25\bin\java.exe`。

### 1.3 编译测试 Java 程序
```cmd
jdk-25\bin\javac -d test-java\out test-java\Main.java
```
产物为 `test-java\out\Main.class`。
> 替换成自己的 Java 程序时，此步换成打自己的 jar 即可。

---

## 2. 编译与发布 exe

### 2.1 构建（验证 0 警告）
```cmd
dotnet build WindowsServiceHost -c Release
```

### 2.2 发布为单文件自包含 exe
```cmd
dotnet publish WindowsServiceHost -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```
产物：`publish\WindowsServiceHost.exe` + `publish\appsettings.json`。

> ⚠️ 若目标目录里已有正在运行的服务占用了 exe/配置，publish 会报 `MSB3030`。先 `sc stop` 再发布。

### 2.3 组装部署目录
把 exe、配置、运行环境、业务文件放到**同一个公共目录**（推荐 `C:\MyService`，避免用户目录权限问题）：
```
C:\MyService\
├── WindowsServiceHost.exe      # 发布产物
├── appsettings.json            # 发布产物（相对路径配置）
├── jre25\                      # Java 运行环境
└── test-java\out\              # 测试程序 class（或你的 jar）
```
示例：
```cmd
mkdir C:\MyService
xcopy publish C:\MyService\ /E /I /Y
xcopy jre25 C:\MyService\jre25\ /E /I /Y
xcopy test-java\out C:\MyService\test-java\out\ /E /I /Y
```

因为 exe 与 jre25、test-java 同级，`appsettings.json` 可直接用浅相对路径：
```json
"TargetExePath": "jre25\\bin\\java.exe",
"Arguments": "-cp test-java\\out Main \\\\共享机名\\share\\sample.txt 5"
```

### 2.4 appsettings.json 配置项说明

完整示例：
```json
{
  "ExeHost": {
    "TargetExePath": "jre25\\bin\\java.exe",
    "Arguments": "-cp test-java\\out Main \\\\DESKTOP-0O93IBK\\test\\sample.txt 5",
    "WorkingDirectory": "",
    "RestartDelaySeconds": 5,
    "MaxRestarts": 10,
    "ResetCountAfterSeconds": 60,
    "ShutdownTimeoutSeconds": 10
  },
  "Logging": {
    "LogLevel": { "Default": "Information", "Microsoft.Hosting.Lifetime": "Information" },
    "FileLog": { "Enabled": true, "Path": "" }
  }
}
```

| 配置项 | 作用 | 默认值 |
| --- | --- | --- |
| `ExeHost:TargetExePath` | 要托管的目标程序路径。相对路径按 workspace（默认 exe 目录）解析，也可写绝对路径 | 必填 |
| `ExeHost:Arguments` | 传给目标程序的命令行参数。Java 示例里 `-cp ... Main <UNC路径> <间隔秒>` | 空 |
| `ExeHost:WorkingDirectory` | 子进程的工作目录。留空则用 workspace 目录 | 空 |
| `ExeHost:RestartDelaySeconds` | 子进程崩溃后，等待多少秒再重启 | 5 |
| `ExeHost:MaxRestarts` | 连续崩溃重启上限，超过后服务停止（防止无限重启） | 10 |
| `ExeHost:ResetCountAfterSeconds` | 子进程稳定运行超过该秒数后，崩溃计数清零（重新获得完整重启次数） | 60 |
| `ExeHost:ShutdownTimeoutSeconds` | 停止服务时等待子进程自行退出的秒数，超时则强制 kill 进程树 | 15 |
| `Logging:LogLevel:Default` | 日志级别（Information / Warning / Error 等） | Information |
| `Logging:FileLog:Enabled` | 是否同时写文件日志（事件日志始终写入） | true |
| `Logging:FileLog:Path` | 日志文件路径。留空则为 workspace 下 `logs/service.log` | 空 |

**退出码语义**：子进程退出码 `0` = 正常退出，服务随之停止且不再重启；非 `0` = 崩溃，按上述策略重启。

> 注意：`Arguments` 里的 UNC 路径在 JSON 中每个反斜杠都要转义，`\\server\share\file.txt` 写作 `\\\\server\\share\\file.txt`。

---

## 3. 配置网络共享（在共享机上操作）

假设共享机名为 `DESKTOP-0O93IBK`，要共享 `C:\share` 目录。

### 3.1 创建共享
1. 右键目标文件夹 → 属性 → **共享** → 高级共享
2. 勾选"共享此文件夹"，共享名设为 `share`
3. 点"权限"，添加共享账号（见第 4 节），给**读取**权限

### 3.2 配置 NTFS 安全权限（关键，常被遗漏）
共享权限和 NTFS 权限**都要**放行，缺一不可：
1. 同一文件夹 → 属性 → **安全** 选项卡 → 编辑 → 添加
2. 添加共享账号（如 `svcuser`），给**读取**权限

### 3.3 验证共享可访问（在本机）
```powershell
Test-Path '\\DESKTOP-0O93IBK\share\sample.txt'
```
返回 `True` 即通。若 `False`，用 `Get-ChildItem` + try/catch 看具体错误。

---

## 4. 创建共享用的本地账号（两台机器都要）

> ⚠️ 务必用**纯本地账号**，不要用微软账户（密码机制不同，会一直认证失败）。

工作组下访问共享依赖"远程机器上存在同名同密码账号"，所以**本机和共享机各建一个同名同密码账号**。

### 4.1 本机创建账号（管理员 CMD）
```cmd
net user svcuser MyPass@123 /add
```

### 4.2 共享机创建同名同密码账号（在共享机管理员 CMD）
```cmd
net user svcuser MyPass@123 /add
```

### 4.3 把该账号加入共享目录权限
即第 3.1 / 3.2 步里添加的用户，填 `svcuser`。

> 密码三处必须一致：本机 `net user`、共享机 `net user`、服务 `sc config password=`。

---

## 5. 安装并配置服务（本机，管理员 CMD）

### 5.1 创建服务
```cmd
sc create "MyExeService" binPath= "C:\MyService\WindowsServiceHost.exe" start= auto
```
> `binPath=` 后面必须有空格，这是 `sc` 的语法。

若配置文件不在 exe 同目录，用 `--workspace` 指定基准目录：
```cmd
sc create "MyExeService" binPath= "C:\MyService\WindowsServiceHost.exe --workspace C:\MyService" start= auto
```

### 5.2 指定服务运行账号（关键）
默认 LocalSystem 无法访问网络共享，改成第 4 节创建的本地账号：
```cmd
sc config MyExeService obj= ".\svcuser" password= "MyPass@123"
```

### 5.3 授予"作为服务登录"权限
否则启动报"拒绝访问"。两种方式：

**方式 A：图形界面**
`secpol.msc` → 本地策略 → 用户权限分配 → **作为服务登录** → 添加 `svcuser`

**方式 B：命令行**（需 ntrights 工具）
```cmd
ntrights +r SeServiceLogonRight -u svcuser
```

### 5.4 启动服务
```cmd
sc start MyExeService
```

---

## 6. 验证

### 6.1 看文件日志（最直观）
```cmd
type C:\MyService\logs\service.log
```
成功标志：
```
[子进程] [java-test] user=svcuser ...
[子进程] [...] read OK, 27 bytes, content: ...
```
- `user=svcuser`：说明服务以正确账号运行
- `read OK`：说明 UNC 文件读取成功

失败对照：
- `user=QWERASD$`（机器账户）→ 没换成用户账号，回到 5.2
- `AccessDeniedException` → 共享/NTFS 权限没放行，回到第 3 节
- `用户名或密码不正确` → 双机账号密码不一致，回到第 4 节

### 6.2 看 Windows 事件日志
事件查看器 → Windows 日志 → 应用程序，来源为 `MyExeService`。

### 6.3 验证优雅停止
```cmd
sc stop MyExeService
```
日志应出现子进程 shutdown hook（Java 程序打印 `shutdown hook invoked`）。超时未退出会被强制 kill，属预期。

---

## 7. 日常运维命令速查

```cmd
sc start MyExeService          :: 启动
sc stop MyExeService           :: 停止
sc delete MyExeService         :: 卸载（先 stop）
sc qc MyExeService             :: 查看配置（含运行账号）
sc query MyExeService          :: 查看运行状态
sc config MyExeService password= "新密码"   :: 改密码后同步
```

---

## 8. 替换成自己的 Java 程序

只需改 `appsettings.json` 的 `Arguments`：
```json
"TargetExePath": "jre25\\bin\\java.exe",
"Arguments": "-jar myapp.jar"
```
把 `myapp.jar` 放到 `C:\MyService\` 下即可。`test-java` 只是占位测试程序。

---

## 完整流程清单（TL;DR）

- [ ] 装 .NET 8 SDK，`dotnet --version` 正常
- [ ] `jlink` 生成 jre25；`javac` 编译测试程序
- [ ] `dotnet publish` 出单文件 exe
- [ ] 组装 `C:\MyService`（exe + appsettings.json + jre25 + test-java）
- [ ] 共享机：建共享 + 共享权限 + NTFS 安全权限
- [ ] 本机 + 共享机：各建同名同密码本地账号 `svcuser`
- [ ] 本机：`sc create` + `sc config obj/password` + 授"作为服务登录"
- [ ] `sc start`，看 `logs\service.log` 出现 `read OK`
