using OpenClaw.Connection.NativeGateway;
using System.Diagnostics;

namespace OpenClaw.SetupEngine.UI;

internal sealed class NativeGatewaySetupHost(
    Action<string>? progress = null,
    Action<NativeGatewaySetupStage>? stageProgress = null) : INativeGatewaySetupHost
{
    // Includes packaged Node/CLI startup, not just the Gateway RPC timeout.
    private static readonly TimeSpan PairingCommandTimeout = TimeSpan.FromMinutes(2);
    private readonly NativeGatewayPackageClient _packageClient = new();

    public Task<NativeGatewayContract> DetectContractAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken) =>
        _packageClient.DetectAsync(package, cancellationToken);

    public Task<IsolatedGatewayConfiguration> PrepareIsolatedConfigurationAsync(
        NativeGatewayPackage package, int port, CancellationToken cancellationToken) =>
        _packageClient.PrepareAsync(package, port, cancellationToken);

    public Task ApplyIsolatedCapabilitiesAsync(
        NativeGatewayPackage package, IReadOnlyList<string> commandIds, CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath,
            ["config", "set", ConfigureGatewayStep.NodeCommandsAllowKey,
                System.Text.Json.JsonSerializer.Serialize(commandIds), "--strict-json"],
            new Dictionary<string, string>(), TimeSpan.FromMinutes(2),
            "正在隔离网关中应用 Windows 节点能力…", cancellationToken);

    public void ReportProgress(NativeGatewaySetupStage stage)
    {
        stageProgress?.Invoke(stage);
        progress?.Invoke(stage switch
        {
            NativeGatewaySetupStage.StartingGateway => "正在启动打包网关并验证其监听…",
            NativeGatewaySetupStage.VerifyingEndpoint => "正在连接前验证网关端点…",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        });
    }

    public IDisposable OpenRecoveryTerminal(
        NativeGatewayPackage package, IReadOnlyDictionary<string, string> environment)
    {
        if (package.Contract == NativeGatewayContract.IsolatedSessionV1)
        {
            var terminal = new ProcessStartInfo(package.ClawCtlAliasPath)
            {
                UseShellExecute = true
            };
            terminal.ArgumentList.Add("pwsh");
            return new RecoveryTerminal(Process.Start(terminal)
                ?? throw new InvalidOperationException("无法打开隔离网关终端。"));
        }
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = environment["OPENCLAW_STATE_DIR"],
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NoExit");
        foreach (var (key, value) in environment)
            start.Environment[key] = value;
        start.Environment["PATH"] = Path.GetDirectoryName(package.OpenClawAliasPath) +
            Path.PathSeparator + start.Environment["PATH"];
        return new RecoveryTerminal(Process.Start(start)
            ?? throw new InvalidOperationException("无法打开原生配置终端。"));
    }

    private sealed class RecoveryTerminal(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            finally { process.Dispose(); }
        }
    }

    public Task PreparePackageAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        if (package.Contract == NativeGatewayContract.IsolatedSessionV1)
        {
            progress?.Invoke("正在准备隔离网关会话…");
            return _packageClient.SetupAsync(package, cancellationToken);
        }
        return RunAsync(package.ClawCtlAliasPath, ["setup"], environment,
            TimeSpan.FromMinutes(3), "正在准备打包网关运行时…", cancellationToken);
    }

    public Task ValidateConfigurationAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath, ["config", "validate", "--json"], environment,
            TimeSpan.FromMinutes(2), "正在校验原生网关配置…", cancellationToken);

    public Task VerifyHealthAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken) =>
        RunAsync(package.OpenClawAliasPath, ["gateway", "health", "--json"], environment,
            TimeSpan.FromMinutes(2), "正在检查原生网关健康状态…", cancellationToken);

    public Task<IsolatedGatewayConfiguration> CheckIsolatedPairingConfigurationAsync(
        NativeGatewayPackage package, CancellationToken cancellationToken) =>
        _packageClient.CheckAsync(package, cancellationToken);

    public Task<string> ListDevicePairingRequestsAsync(
        NativeGatewayPackage package,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken) =>
        package.Contract == NativeGatewayContract.IsolatedSessionV1
            ? RunAsync(package.OpenClawAliasPath, ["devices", "list", "--json"],
                new Dictionary<string, string>(), PairingCommandTimeout,
                "正在验证本桌面端的配对请求…", cancellationToken)
            : RunAsync(package.OpenClawAliasPath, ["devices", "list", "--json"],
                environment, PairingCommandTimeout, "正在验证本桌面端的配对请求…", cancellationToken);

    public Task<string> ApproveDevicePairingAsync(
        NativeGatewayPackage package, string requestId,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken) =>
        package.Contract == NativeGatewayContract.IsolatedSessionV1
            ? RunAsync(package.OpenClawAliasPath, ["devices", "approve", requestId, "--json"],
                new Dictionary<string, string>(), PairingCommandTimeout,
                "正在把本桌面端与安装用网关配对…", cancellationToken)
            : RunAsync(package.OpenClawAliasPath, ["devices", "approve", requestId, "--json"],
                environment, PairingCommandTimeout, "正在把本桌面端与安装用网关配对…", cancellationToken);

    private async Task<string> RunAsync(
        string executable, string[] arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout, string message, CancellationToken cancellationToken)
    {
        progress?.Invoke(message);
        using var logger = new SetupLogger(filePath: null);
        var result = await new CommandRunner(logger).RunAsync(
            executable, arguments, timeout, environment,
            workingDirectory: environment.TryGetValue("OPENCLAW_STATE_DIR", out var directory)
                ? directory
                : null,
            ct: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.TimedOut || result.ExitCode != 0)
        {
            var detail = SetupLogger.Sanitize(result.Stderr + Environment.NewLine + result.Stdout).Trim();
            throw new InvalidOperationException(
                $"{message} {(result.TimedOut ? "Timed out." : $"Exit code {result.ExitCode}.")} {detail}");
        }
        if (arguments is ["setup"] && !string.IsNullOrWhiteSpace(result.Stdout))
            progress?.Invoke(SetupLogger.Sanitize(result.Stdout).Trim());
        return result.Stdout;
    }
}
