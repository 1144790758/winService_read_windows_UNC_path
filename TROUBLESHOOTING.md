# WindowsServiceHost 开发踩坑记录

> 记录将普通 exe 托管为 Windows 服务、并让其读取网络共享（UNC）文件过程中遇到的全部坑点。
> 环境：Windows 11 工作组（非域）双机，本机 QWERASD，共享机 DESKTOP-0O93IBK。

---

## 一、权限类

### 1. `sc` 命令全部需要管理员权限
`sc create / start / stop / delete / config` 在普通终端一律报 `错误 5：拒绝访问`。

**解决**：必须以管理员身份打开 CMD/PowerShell 再执行。无捷径。

### 2. 服务默认 LocalSystem 无法访问网络共享
服务以 LocalSystem 运行时，网络访问使用**机器账户**（如 `QWERASD$`），而非当前登录用户。日志中体现为：

```
user=QWERASD$
read FAILED: java.nio.file.AccessDeniedException: \\server\share\file
```

**解决**：改用有权限的用户账户运行服务（见下文"服务账号"一节）。

### 3. 工作组下搜不到对方机器账户
想给共享目录授权 `QWERASD$`，但在共享机的权限对话框里**搜不到**这个账户。

**原因**：两台机器不在同一域，只是工作组，机器账户无法跨机解析。

**解决**：放弃机器账户方案，改用"两台机器创建同名同密码本地用户"的方式（见下）。

### 4. 共享权限 ≠ NTFS 权限
只设置了共享（Share）权限为 Everyone 仍可能失败。访问 SMB 需同时通过：
- **共享权限**（高级共享 → 权限）
- **NTFS 安全权限**（属性 → 安全选项卡）

**解决**：两处都要给对应用户/组读取权限。

---

## 二、服务账号类

### 5. 新建服务账号启动报"拒绝访问"
用 `sc config obj= ".\svcuser"` 配置后启动仍报错误 5。两个常见原因：

**a) 缺少"作为服务登录"权限**
`secpol.msc` → 本地策略 → 用户权限分配 → **作为服务登录** → 添加该用户。

**b) 服务账号无权访问 exe 所在目录**
exe 放在 `C:\Users\21475\...`（用户配置文件目录），其他账号默认无权进入。

**解决**：把服务部署到公共目录（如 `C:\MyService`），或用 `icacls` 授权：
```cmd
icacls "C:\path\to\publish" /grant "svcuser:(OI)(CI)RX" /T
```

### 6. 双机访问 UNC 必须"同名同密码"
工作组下，服务账号访问远程共享靠的是**远程机器上是否存在同名同密码账户**。三处密码必须完全一致：

1. 本机服务账号密码（`sc config password=`）
2. 本机本地用户密码（`net user 21475 xxx`）
3. 共享机上同名用户密码（在共享机执行 `net user 21475 xxx`）

密码不一致时报：
```
java.nio.file.FileSystemException: \\server\share\file: 用户名或密码不正确。
```
注意这与"无权限"（AccessDeniedException）是**不同错误**：前者是认证失败，后者是授权失败。

### 7. 忘记密码可用 net user 重置
```cmd
net user 21475 新密码
```
改完记得同步更新 `sc config MyExeService password= "新密码"`。

### ⚠️ 微软账户不适合直接当服务账号（重要）
如果登录账户是**微软账户**（用邮箱登录），拿它当服务账号会反复出现"密码不正确"：

- 微软账户的密码是**微软账户密码**，不是本地密码
- `net user 21475 xxx` 改本地密码时，与微软账户密码可能不同步、相互冲突
- 服务用微软账户认证走的是更复杂的机制，极易失败

**这就是之前用 21475 当服务账号密码一直对不上的根因。**

**解决**：服务专用一个**纯本地账户**（如 `svcuser`），不要复用微软账户。
```cmd
net user svcuser MyPass@123 /add
```
配合把服务部署到公共目录（`C:\MyService`），本地账户方案就没有目录权限问题，两台机器各建一个同名同密码的 `svcuser` 即可访问 UNC。

---

## 三、配置与路径类

### 8. SCM 启动时工作目录是 System32
服务由 SCM 拉起时，`Environment.CurrentDirectory` 是 `C:\Windows\System32`，直接按相对路径读 `appsettings.json` 会找不到。

**解决**：`Program.cs` 用 `PhysicalFileProvider(AppContext.BaseDirectory)` 显式从 exe 目录加载配置。本项目已进一步支持 `--workspace <dir>` 参数指定基准目录。

### 9. 改错了配置文件
exe 部署在 `C:\MyService`，但手改的是项目源码目录下的 `appsettings.json`，导致服务仍读旧配置。

**教训**：服务读的是 **exe 同目录**的 `appsettings.json`，改配置要改部署目录那一份，改完重启服务。

### 10. 相对路径基准要统一
`TargetExePath`、`Arguments` 里的相对路径都相对 workspace（默认 exe 目录）解析。部署时保证目录结构：
```
C:\MyService\
├── WindowsServiceHost.exe
├── appsettings.json
├── jre25\
└── test-java\
```
配置即可写 `jre25\bin\java.exe` 这类浅相对路径。

---

## 四、构建与部署类

### 11. 向运行中的服务目录 publish 会失败
服务正在运行时，publish 目录里的 exe/配置文件被占用，`dotnet publish -o` 报 `MSB3030 无法复制文件`。

**解决**：先 `sc stop` 再 publish。

### 12. 单文件 publish 不含 appsettings.json 需确认
`PublishSingleFile=true` 时 appsettings.json 默认仍会复制到输出目录，但被占用时复制会失败（见上）。发布后务必核对输出目录的 appsettings.json 内容是否最新。

### 13. dotnet SDK 可能不在 PATH
报 "No SDKs were found" 时，SDK 可能在 `%LOCALAPPDATA%\Microsoft\dotnet`。用完整路径或修正 `PATH`/`DOTNET_ROOT`。

---

## 五、调试与观测类

### 14. 控制台中文乱码（GBK）
GBK 控制台输出中文乱码属正常，**排查问题看 `logs/service.log`**（UTF-8，位于 exe 同目录 logs 下），别看控制台。

### 15. Bash 里传 UNC 路径反斜杠会被吃掉
在 bash 调 PowerShell/cmd 传 `\\server\share` 时，反斜杠常被 shell 吞掉，导致 UNC 被解析成本地路径（如 `C:\server\share`）。

**解决**：把 PowerShell 逻辑写成 `.ps1` 脚本文件再执行，避免命令行转义。

### 16. Test-Path 失败只返回 False，无错误详情
`Test-Path \\server\share` 不通时只给 `False`。要看具体原因用 `Get-ChildItem` + `try/catch` 捕获 `Exception.Message`。

### 17. 子进程退出码语义
- 退出码 `0` = 正常退出，服务随之停止且不再重启
- 非 `0`（含被强杀的 `-1`）= 崩溃，按策略重启
- 测试 Java 程序是死循环，不会自行退出；`sc stop` 超时后被强杀属预期

---

## 六、最终可用的部署步骤（速查）

```cmd
:: 1. 发布（先停服务）
sc stop MyExeService
dotnet publish WindowsServiceHost -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish

:: 2. 部署到公共目录（exe + appsettings.json + jre25 + test-java 同级）
xcopy publish C:\MyService\ /E /I /Y

:: 3. 本机与共享机创建同名同密码用户（若需访问 UNC）
net user 21475 MyPass@123 /add            :: 两台机器都执行

:: 4. 安装服务并指定运行账号
sc create "MyExeService" binPath= "C:\MyService\WindowsServiceHost.exe" start= auto
sc config MyExeService obj= "QWERASD\21475" password= "MyPass@1234"

:: 5. 授予"作为服务登录"权限（secpol.msc 或 ntrights）

:: 6. 启动
sc start MyExeService

:: 7. 看日志
type C:\MyService\logs\service.log
```

---

## 关键结论

| 现象 | 根因 | 对策 |
| --- | --- | --- |
| sc 报拒绝访问 | 非管理员终端 | 管理员身份运行 |
| AccessDeniedException | LocalSystem 无网络凭据 | 换用户账号运行服务 |
| 用户名或密码不正确 | 双机账号密码不一致 | 同名同密码 |
| 密码反复对不上 | 用了微软账户当服务账号 | 改用纯本地账户 svcuser |
| 服务起不来(错误5) | 缺服务登录权限/exe 目录无权 | secpol 授权 + 部署到公共目录 |
| 配置不生效 | 改了源码目录而非部署目录 | 改 exe 同目录的配置并重启 |
| publish 失败 | 服务占用文件 | 先 sc stop |
