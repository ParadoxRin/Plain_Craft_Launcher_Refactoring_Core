using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

static HashSet<string> Methods(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    return metadata.MethodDefinitions.Select(h => metadata.GetString(metadata.GetMethodDefinition(h).Name)).ToHashSet();
}
var production = Methods(args[0]);
var acceptance = Methods(args[1]);
string[] probes = ["LoginCancelSmokeAsync", "RemoteDesktopSmokeAsync", "UnifiedBrowserSmokeAsync", "CloseoutFifthAcceptanceAsync", "SmokeAsync"];
foreach (var probe in probes)
{
    if (production.Contains(probe)) throw new Exception("TEST_ENTRY_IN_PRODUCTION: " + probe);
    if (!acceptance.Contains(probe)) throw new Exception("TEST_ENTRY_MISSING: " + probe);
}
using (var stream = File.OpenRead(args[2]))
using (var pe = new PEReader(stream))
{
    var metadata = pe.GetMetadataReader();
    if (metadata.ManifestResources.Count(h => metadata.GetString(metadata.GetManifestResource(h).Name) == "Pcl.Core.Compatibility.pcl-optifine-hashes.json") != 1)
        throw new Exception("OPTIFINE_RESOURCE_MISSING_OR_DUPLICATED");
}
var jar = Path.Combine(args[3], "lwjgl-unsafe-agent-2.0.jar");
if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(jar))) != "6c07d3508dc090a12076876bdc227a19ce195a41c9e2c6535c3f82213b1b2015")
    throw new Exception("HOST_AGENT_INTEGRITY_FAILED");
if (File.Exists(Path.Combine(Path.GetDirectoryName(args[0])!, "lwjgl-unsafe-agent-2.0.jar")))
    throw new Exception("HOST_AGENT_IN_CLIENT");
Console.WriteLine("BUILD_LAYOUT_PASSED: production/test isolation, OptiFine resource, Host agent identity and client boundary");
