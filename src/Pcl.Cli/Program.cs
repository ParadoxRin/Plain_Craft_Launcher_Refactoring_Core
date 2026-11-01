using Pcl.Cli;
if (args.FirstOrDefault() is "host-status" or "host-install" or "host-launch")
    return await RemoteCli.RunAsync(args);
Console.WriteLine("PCLCS CLI: host-status | host-install | host-launch --host /absolute/Pcl.Host --data-root /absolute/data");
Console.WriteLine("Legacy in-process diagnostics moved to tools/Pcl.CoreDiagnostics.");
return args.Length == 0 || args.Contains("--help") ? 0 : 2;
