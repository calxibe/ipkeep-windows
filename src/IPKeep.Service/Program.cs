using System.ServiceProcess;
using System.Threading.Channels;
using IPKeep.Core;

if (args.Length > 0)
{
    Environment.ExitCode = InstallerOperations.Run(args, AppContext.BaseDirectory);
    return;
}
if (Environment.UserInteractive)
{
    Console.WriteLine("Open IPKeep.exe to set up and manage the background service.");
    return;
}
ServiceBase.Run(new IpKeepService());

sealed class IpKeepService : ServiceBase
{
    private readonly CancellationTokenSource stopping = new();
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly ActivityLog log = new(AppPaths.LogFile);
    private Task? worker;
    private ServiceSnapshot snapshot = new();

    // LocalService cannot create Windows Event Log sources. File logs are provisioned by the installer.
    public IpKeepService() { ServiceName = AppPaths.ServiceName; CanStop = true; CanShutdown = true; AutoLog = false; }
    protected override void OnStart(string[] args)
    {
        DeploymentSecurity.ValidateRuntime();
        log.Write("INFO", "IPKeep service started. Checking immediately; failed checks retry in 5 minutes.");
        worker = Task.Run(() => RunAsync(stopping.Token));
    }
    protected override void OnCustomCommand(int command) { if (command == 128) wake.Writer.TryWrite(true); }
    protected override void OnStop()
    {
        RequestAdditionalTime(30_000); stopping.Cancel();
        try { worker?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        snapshot = snapshot with { State = "Stopped", NextCheck = null, Message = "Background updates are stopped." };
        SaveStatus(); log.Write("INFO", "IPKeep service stopped. No further checks are scheduled.");
    }
    protected override void OnShutdown() => OnStop();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var ipv4Http = NetworkClients.CreateDiscovery(ipv6: false);
        using var ipv6Http = NetworkClients.CreateDiscovery(ipv6: true);
        using var updateHttp = NetworkClients.Create();
        var engine = new CheckEngine(new PublicIpResolver(ipv4Http, ipv6Http, log), new IpKeepClient(updateHttp, log), log);
        while (!cancellationToken.IsCancellationRequested)
        {
            bool success = false, authenticationRejected = false; int interval = 360;
            snapshot = snapshot with { State = "Checking", NextCheck = null, Message = "Checking your public IP address…" }; SaveStatus();
            try
            {
                // The full installation sweep runs once at startup. Each cycle re-checks the
                // directory permissions and the service binary, which is what actually gates
                // execution, instead of reading an ACL for all 200 published files every time.
                DeploymentSecurity.ValidateRuntime(includeAllFiles: false);
                var connection = SettingsStore.Load(); interval = connection.Settings.IntervalMinutes;
                log.SetSecret(connection.Token);
                var result = await engine.RunAsync(connection.Settings, connection.Token, cancellationToken);
                success = result.Success;
                authenticationRejected = result.AuthenticationRejected;
                snapshot = snapshot with { LastCheck = result, Message = result.Message };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                string message = ex is SettingsException ? ex.Message : "Cannot read the connection or verify the installation. Open IPKeep Settings to repair it.";
                log.Write("ERROR", $"Check failed ({ex.GetType().Name}). {message}");
                snapshot = snapshot with { Message = message };
            }
            int failures = success ? 0 : snapshot.ConsecutiveFailures + 1;
            var delay = CheckSchedule.Delay(success, interval, failures, authenticationRejected);
            snapshot = snapshot with { State = success ? "Waiting" : "Needs attention", NextCheck = delay is null ? null : DateTimeOffset.Now + delay, ConsecutiveFailures = failures };
            SaveStatus();
            log.Write("INFO", delay is null
                ? $"Updates paused until a new token is saved or a check is requested; consecutive_failures={failures}."
                : $"Next check: {snapshot.NextCheck:yyyy-MM-dd HH:mm:ss zzz}; consecutive_failures={failures}.");
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // A null delay waits only for Check now or service stop, never a timer.
            if (delay is not null) wait.CancelAfter(delay.Value);
            try { await wake.Reader.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }
    private void SaveStatus()
    {
        try { FileStore.WriteJson(AppPaths.StatusFile, snapshot); }
        catch (IOException) { log.Write("WARN", "Status file is temporarily unavailable."); }
        catch (UnauthorizedAccessException) { log.Write("ERROR", "Cannot write the status file. Check installation permissions."); }
    }
}
