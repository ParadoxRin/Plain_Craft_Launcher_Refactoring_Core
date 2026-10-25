using System.Reflection;
using System.Text.Json;
using Pcl.Client;
using Pcl.Core.Contracts.Host;
using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Operations;

// 本程序只引用 Client/Contract，不能访问 Core 类型；真实 Host 在独立进程运行。
var root = Path.GetFullPath(args[0]);
var report = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool value, string code)
{
    if (!value)
    {
        throw new Exception(code);
    }

    checks.Add(code);
}

try
{
    var clientReferences = typeof(HostClient).Assembly.GetReferencedAssemblies()
        .Select(
            x => x.Name!
        )
        .ToArray();
    Check(
        clientReferences.Where(
            x => x.StartsWith("Pcl.", StringComparison.Ordinal)
        )
            .SequenceEqual(
                new[]
                {
                    "Pcl.Contract"
                }
            ),
        "client-references-contract-only"
    );
    Check(
        !Directory.GetFiles(AppContext.BaseDirectory, "Pcl.Core.dll")
            .Any(),
        "client-consumer-output-has-no-core"
    );
    Check(
        typeof(IVersionInstallService).Assembly.GetName()
            .Name == "Pcl.Contract",
        "contract-type-owned-by-contract-assembly"
    );
    Check(
        !typeof(IVersionInstallService).Assembly.GetReferencedAssemblies()
            .Any(
                x => x.Name!.StartsWith("Pcl.", StringComparison.Ordinal)
            ),
        "contract-has-no-pcl-dependencies"
    );
    var projectRoot = Path.GetFullPath(".");
    await using (var host = new HostClient(
        Path.Combine(projectRoot, ".tooling/dotnet/dotnet"),
        Path.Combine(projectRoot, "src/Pcl.Host/bin/Debug/net10.0/Pcl.Host.dll"),
        Path.Combine(root, "real-host")
    ))
    {
        var hello = await host.HelloAsync();
        Check(
            hello.ProtocolVersion == 1 && hello.InstallationTarget is not null,
            "isolated-client-real-host-handshake"
        );
        var resource = await host.OpenResourceAsync("operations");
        var page = await host.ReadPageAsync(resource.ResourceId, 0, 50);
        Check(page.Eof, "isolated-client-operations-page");
        await host.CloseResourceAsync(resource.ResourceId);
        try
        {
            await host.Call<JsonElement>(
                "arbitrary.execute",
                new { }
            );
            throw new Exception("UNKNOWN_METHOD_ACCEPTED");
        }
        catch (OperationRejectedException ex)when (ex.Code == "METHOD_NOT_SUPPORTED")
        {
            checks.Add("controlled-host-error-survives-contract-boundary");
        }

        await host.ShutdownAsync();
    }

    // 受控假 Host 只用于迟到/错误 ID 及 stderr 分块，不冒充真实安装或登录。
    var script = Path.Combine(root, "fake_host.py");
    await File.WriteAllTextAsync(
        script,
        """
import json, sys, time
for line in sys.stdin:
    request=json.loads(line)
    rid=request['id']
    if request['method']=='host.hello':
        result={'protocolVersion':1,'hostInstanceId':'fixture','capabilities':[],'maximumFrameBytes':1048576}
    elif request['method']=='fixture.bad':
        print(json.dumps({'protocolVersion':99,'id':rid,'result':{}}),flush=True)
        continue
    elif request['method']=='host.shutdown':
        print(json.dumps({'protocolVersion':1,'id':rid,'result':{'accepted':True}}),flush=True)
        break
    else:
        sys.stderr.write('access_');sys.stderr.flush()
        time.sleep(0.05)
        sys.stderr.write('token=fixture-secret Bearer fixture-secret\n');sys.stderr.flush()
        print(json.dumps({'protocolVersion':1,'id':'wrong-request-id','result':{'wrong':True}}),flush=True)
        result={'correct':True}
    print(json.dumps({'protocolVersion':1,'id':rid,'result':result}),flush=True)
"""
    );
    await using (var host = new HostClient(
        "/usr/bin/python3",
        script,
        Path.Combine(root, "fake")
    ))
    {
        await host.HelloAsync();
        var reply = await host.Call<JsonElement>(
            "fixture.check",
            new { }
        );
        Check(
            reply.GetProperty("correct")
                .GetBoolean(),
            "wrong-response-id-not-misdelivered"
        );
        await Task.Delay(200);
        Check(
            host.SafeDiagnostics == "HOST_STDERR_OUTPUT_SUPPRESSED" && !host.SafeDiagnostics.Contains("fixture-secret"),
            "split-stderr-secret-is-never-retained"
        );
        try
        {
            await host.Call<JsonElement>(
                "fixture.bad",
                new { }
            );
            throw new Exception("INVALID_PROTOCOL_ACCEPTED");
        }
        catch (OperationRejectedException ex)when (ex.Code == "HOST_DISCONNECTED")
        {
            checks.Add("invalid-response-closes-connection");
        }

        try
        {
            await host.Call<JsonElement>(
                "fixture.check",
                new { }
            )
                .WaitAsync(
                    TimeSpan.FromSeconds(2)
                );
            throw new Exception("DEAD_READER_ACCEPTED_REQUEST");
        }
        catch (OperationRejectedException ex)when (ex.Code == "HOST_DISCONNECTED")
        {
            checks.Add("post-disconnect-call-fails-without-hanging");
        }
    }

    File.WriteAllText(
        report,
        JsonSerializer.Serialize(
            new
            {
                status = "passed",
                checks
            },
            new JsonSerializerOptions
            {
                WriteIndented = true
            }
        )
    );
}
catch (Exception ex)
{
    File.WriteAllText(
        report,
        JsonSerializer.Serialize(
            new
            {
                status = "failed",
                checks,
                error = ex.Message
            }
        )
    );
    Environment.ExitCode = 1;
}
