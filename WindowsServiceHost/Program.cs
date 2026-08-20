using Microsoft.Extensions.FileProviders;
using WindowsServiceHost;

// 解析 --workspace 参数:指定配置加载和相对路径解析的基准目录
// 未指定时默认为 exe 所在目录(SCM 启动时工作目录是 System32,不能依赖 Environment.CurrentDirectory)
var basePath = AppContext.BaseDirectory;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i].Equals("--workspace", StringComparison.OrdinalIgnoreCase))
    {
        var candidate = Path.GetFullPath(args[i + 1]);
        if (Directory.Exists(candidate))
        {
            basePath = candidate.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? candidate
                : candidate + Path.DirectorySeparatorChar;
        }
        else
        {
            Console.Error.WriteLine($"[警告] --workspace 指定的目录不存在: {args[i + 1]},回退到 exe 目录");
        }
        break;
    }
}

var builder = Host.CreateApplicationBuilder(args);

builder.Environment.ContentRootPath = basePath;
builder.Configuration.AddJsonFile(new PhysicalFileProvider(basePath), "appsettings.json",
    optional: false, reloadOnChange: true);

// 将 workspace 路径注入配置,供 ExeHostWorker 和 FileLogger 使用
builder.Configuration["Workspace:BasePath"] = basePath;

// 作为 Windows 服务运行时,自动接入 SCM 生命周期并写入 Windows 事件日志
builder.Services.AddWindowsService(options => options.ServiceName = "MyExeService");

builder.Services.Configure<ExeHostOptions>(builder.Configuration.GetSection(ExeHostOptions.SectionName));

var fileLog = builder.Configuration.GetSection("Logging:FileLog").Get<FileLoggerOptions>();
if (fileLog?.Enabled == true)
{
    builder.Logging.AddProvider(new FileLoggerProvider(fileLog.Path, basePath));
}

builder.Services.AddHostedService<ExeHostWorker>();

builder.Build().Run();
