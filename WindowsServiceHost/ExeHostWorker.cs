using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace WindowsServiceHost;

/// <summary>
/// 服务主体:启动并托管目标 exe 子进程,监控其退出,
/// 非预期退出时按配置策略重启;服务停止时优雅终止子进程。
/// </summary>
public sealed class ExeHostWorker(
    IOptions<ExeHostOptions> options,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<ExeHostWorker> logger) : BackgroundService
{
    private readonly ExeHostOptions _options = options.Value;
    private readonly string _basePath = configuration["Workspace:BasePath"] ?? AppContext.BaseDirectory;
    private Process? _process;
    private readonly object _processLock = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var exePath = ResolvePath(_options.TargetExePath);
        if (!File.Exists(exePath))
        {
            logger.LogCritical("目标程序不存在: {Path}(配置项 ExeHost:TargetExePath)", exePath);
            lifetime.StopApplication();
            return;
        }

        logger.LogInformation("服务启动,托管目标: {Path} {Args}", exePath, _options.Arguments);

        var restartCount = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var startedAt = DateTime.UtcNow;
            int exitCode;

            try
            {
                var process = StartProcess(exePath);
                lock (_processLock) _process = process;

                try
                {
                    await process.WaitForExitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // 服务正在停止,优雅终止子进程后直接退出
                    await StopProcessAsync(process);
                    return;
                }

                exitCode = process.ExitCode;
                logger.LogInformation("子进程已退出,退出码: {Code}", exitCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "启动/监控子进程时发生异常");
                exitCode = -1;
            }
            finally
            {
                lock (_processLock)
                {
                    _process?.Dispose();
                    _process = null;
                }
            }

            if (stoppingToken.IsCancellationRequested) return;

            if (exitCode == 0)
            {
                logger.LogInformation("子进程正常退出(退出码 0),不再重启");
                lifetime.StopApplication();
                return;
            }

            var uptime = (DateTime.UtcNow - startedAt).TotalSeconds;
            if (uptime >= _options.ResetCountAfterSeconds)
            {
                restartCount = 0;
            }

            restartCount++;
            if (restartCount > _options.MaxRestarts)
            {
                logger.LogCritical("连续重启次数已达上限({Max}),服务停止。请检查目标程序为何反复崩溃", _options.MaxRestarts);
                lifetime.StopApplication();
                return;
            }

            logger.LogWarning("子进程非预期退出(退出码 {Code}),第 {Count}/{Max} 次重启,{Delay} 秒后执行",
                exitCode, restartCount, _options.MaxRestarts, _options.RestartDelaySeconds);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.RestartDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private Process StartProcess(string exePath)
    {
        var workingDirectory = string.IsNullOrWhiteSpace(_options.WorkingDirectory)
            ? _basePath
            : ResolvePath(_options.WorkingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = _options.Arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) logger.LogInformation("[子进程] {Line}", e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) logger.LogWarning("[子进程] {Line}", e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException($"无法启动进程: {exePath}");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        logger.LogInformation("子进程已启动,PID: {Pid},工作目录: {Dir}", process.Id, workingDirectory);
        return process;
    }

    /// <summary>先尝试正常退出,超时后强制结束整棵进程树。</summary>
    private async Task StopProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;

            logger.LogInformation("正在停止子进程(PID {Pid}),等待最多 {Timeout} 秒", process.Id, _options.ShutdownTimeoutSeconds);
            process.CloseMainWindow();

            var exited = await Task.Run(() => process.WaitForExit(_options.ShutdownTimeoutSeconds * 1000));
            if (!exited)
            {
                logger.LogWarning("子进程未在超时时间内退出,强制终止(PID {Pid})", process.Id);
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            logger.LogInformation("子进程已停止");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            logger.LogWarning(ex, "停止子进程时发生异常(可能已退出)");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Process? process;
        lock (_processLock) process = _process;
        if (process is not null)
        {
            await StopProcessAsync(process);
        }
        await base.StopAsync(cancellationToken);
    }

    /// <summary>相对路径按 workspace 基准目录解析,避免硬编码绝对路径。</summary>
    private string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_basePath, path));
}
