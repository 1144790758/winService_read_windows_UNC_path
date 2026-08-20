namespace WindowsServiceHost;

public sealed class ExeHostOptions
{
    public const string SectionName = "ExeHost";

    /// <summary>目标 exe 路径,支持相对于服务安装目录(BaseDirectory)的路径。</summary>
    public string TargetExePath { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    /// <summary>子进程工作目录,留空则使用服务安装目录。</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>崩溃后重启前的等待秒数。</summary>
    public int RestartDelaySeconds { get; set; } = 5;

    /// <summary>连续崩溃重启的最大次数,超过后服务停止。</summary>
    public int MaxRestarts { get; set; } = 10;

    /// <summary>子进程稳定运行超过该秒数后,崩溃计数归零。</summary>
    public int ResetCountAfterSeconds { get; set; } = 60;

    /// <summary>停止服务时等待子进程正常退出的秒数,超时后强制 kill。</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 15;
}
