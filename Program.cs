using System.Text;
using CodeCommandCenter;
using CodeCommandCenter.Services;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

try
{
    var mobile = args.Contains("-m") || args.Contains("--mobile");
    var localBackend = new TmuxBackend();

    // Load config to discover remote hosts
    var config = ConfigService.Load();

    // Build one RemoteTmuxBackend per configured remote host
    var remotes = config.RemoteHosts.ToDictionary(
        h => h.Name,
        h => new RemoteTmuxBackend(h));

    // Kick off ControlMaster connections in background (non-blocking startup)
    foreach (var host in config.RemoteHosts)
        _ = Task.Run(() => SshControlMasterService.EnsureConnected(host.Host));

    // Router is the single ISessionBackend used by App
    var routedBackend = new BackendRouter(localBackend, remotes, config);

    var app = new App(routedBackend, config, mobile);
    app.Run();
}
catch (Exception ex)
{
    CrashLog.Write(ex);
    Console.CursorVisible = true;
    Console.Write("\e[?1049l"); // Leave alternate screen if we're in it
    Console.Error.WriteLine("Fatal error — logged to ~/.ccc/crash.log");
    Console.Error.WriteLine(ex.Message);
    Environment.Exit(1);
}
