using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;

// 只加载元数据和方法体，不运行桌面入口；检查项目运行时引用和迁移后命名空间的符号使用。
var root = Path.GetFullPath(args[0]);
var desktopFile = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
var violations = new List<string>();
var graph = new Dictionary<string, string[]>();
var expected = new Dictionary<string, string[]>
{
    ["Pcl.Contract"] = [],
    ["Pcl.Domain"] = [],
    ["Pcl.Ports"] = [
        "Pcl.Domain"
    ],
    ["Pcl.Application"] = [
        "Pcl.Contract",
        "Pcl.Domain",
        "Pcl.Ports"
    ],
    ["Pcl.Core"] = [
        "Pcl.Domain",
        "Pcl.Ports",
        "Pcl.Contract",
        "MeloongCore",
        "PCLCS",
        "Pcl.Legacy"
    ],
    ["Pcl.Client"] = [
        "Pcl.Contract"
    ],
    ["Pcl.Desktop"] = [
        "Pcl.Contract",
        "Pcl.Client"
    ],
    ["Pcl.Host"] = [
        "Pcl.Application",
        "Pcl.Contract",
        "Pcl.Core"
    ]
};
foreach (var(name, allowed)in expected)
{
    var project = XDocument.Load(
        Path.Combine(root, "src", name, name + ".csproj")
    );
    if (name == "Pcl.Desktop" && project.Descendants("Reference")
        .Any())
        violations.Add("UNREVIEWED_MANUAL_REFERENCE:" + name);
    if (name is "Pcl.Contract" or "Pcl.Domain" or "Pcl.Ports" or "Pcl.Application" or "Pcl.Client")
    {
        if (project.Descendants("Reference")
            .Any() || project.Descendants("PackageReference")
            .Any())
        {
            violations.Add("UNREVIEWED_MANUAL_REFERENCE:" + name);
        }
    }

    var references = project.Descendants("ProjectReference")
        .Where(
            x => !string.Equals(
                (string? )x.Attribute("ReferenceOutputAssembly"),
                "false",
                StringComparison.OrdinalIgnoreCase
            )
        )
        .Select(
            x => Path.GetFileNameWithoutExtension(
                (string)x.Attribute("Include")!
            )
        )
        .Order()
        .ToArray();
    graph[name] = references;
    if (!references.SequenceEqual(
        allowed.Order()
    ))
    {
        violations.Add("PROJECT_REFERENCES_CHANGED:" + name);
    }
}

// Core 提供既有执行适配，应用用例必须归属独立 Application，禁止重新塞回 Core/Application。
var coreApplication = Path.Combine(root, "src", "Pcl.Core", "Application");
if (Directory.Exists(coreApplication) && Directory.EnumerateFiles(coreApplication, "*.cs", SearchOption.AllDirectories)
    .Any())
{
    violations.Add("APPLICATION_IMPLEMENTATION_IN_CORE");
}

var directory = Path.GetDirectoryName(desktopFile)!;
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(directory, name.Name + ".dll");
    return File.Exists(path)
        ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
        : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(desktopFile);
var actual = assembly.GetReferencedAssemblies()
    .Select(
        x => x.Name!
    )
    .ToHashSet();
if (actual.Overlaps(
    new[]
    {
        "Pcl.Core",
        "Pcl.Application",
        "Pcl.Ports",
        "Pcl.Domain",
        "MeloongCore",
        "PCLCS"
    }
) || !actual.Contains("Pcl.Client") || actual.Contains("Pcl.Host"))
{
    violations.Add("DESKTOP_ASSEMBLY_REFERENCE_MISMATCH");
}

// 纯核心与客户端不得通过程序集引用反向触及旧 Core/平台实现。
foreach (var name in new[]
{
    "Pcl.Contract",
    "Pcl.Domain",
    "Pcl.Ports",
    "Pcl.Application",
    "Pcl.Client"
}

)
{
    var part = AssemblyLoadContext.Default.LoadFromAssemblyPath(
        File.Exists(
            Path.Combine(directory, name + ".dll")
        )
            ? Path.Combine(directory, name + ".dll")
            : Path.Combine(directory, "host", name + ".dll")
    );
    var references = part.GetReferencedAssemblies()
        .Select(
            x => x.Name!
        )
        .ToArray();
    foreach (var reference in references)
    {
        bool framework = reference.StartsWith("System.", StringComparison.Ordinal) || reference is "System" or "netstandard" or "mscorlib" or "Microsoft.CSharp";
        if (!framework && !expected[name].Contains(reference))
        {
            violations.Add("ASSEMBLY_BOUNDARY_VIOLATION:" + name + " -> " + reference);
        }
    }
}

// 解码 IL 而非按源码字符串匹配，覆盖别名、全限定名、目标类型 new 和编译器生成的异步状态机。
var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
    .Where(
        x => x.FieldType == typeof(OpCode)
    )
    .Select(
        x => (OpCode)x.GetValue(null)!
    )
    .ToDictionary(
        x => unchecked((ushort)x.Value)
    );
// Domain 的框架引用仍可能隐藏文件、进程或宿主探测；检查实际 IL，允许内存流与密码学等纯计算。
var domainAssembly = AppDomain.CurrentDomain.GetAssemblies()
    .Single(
        x => x.GetName()
            .Name == "Pcl.Domain"
    );
var domainMethods = 0;
foreach (var type in domainAssembly.GetTypes())
{
    foreach (var method in type.GetMethods(
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly
    )
        .Cast<MethodBase>()
        .Concat(
            type.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
            )
        ))
    {
        domainMethods++;
        if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
        {
            violations.Add("DOMAIN_NATIVE_CALL:" + type.FullName + "." + method.Name);
        }

        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            continue;
        }

        for (int offset = 0; offset < il.Length;)
        {
            ushort key = il[offset++];
            if (key == 0xfe)
            {
                key = (ushort)(0xfe00 | il[offset++]);
            }

            var code = codes[key];
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                _ => 4
            };
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var used = method.Module.ResolveMember(
                    BitConverter.ToInt32(il, offset),
                    type.GetGenericArguments(),
                    method.IsGenericMethod
                        ? method.GetGenericArguments()
                        : null
                );
                var full = (used as Type ?? used?.DeclaringType)?.FullName ?? "";
                bool external = full.StartsWith("System.Net.", StringComparison.Ordinal) || full is "System.IO.File" or "System.IO.Directory" or "System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.FileStream" or "System.IO.FileSystemWatcher" or "System.IO.DriveInfo" or "System.IO.Path" or "System.Diagnostics.Process" or "System.Environment" or "System.OperatingSystem" or "System.Runtime.InteropServices.RuntimeInformation" or "System.Console" or "System.Random" || (full is "System.DateTime" or "System.DateTimeOffset") && used?.Name is "get_Now" or "get_UtcNow" or "get_Today" || full == "System.Guid" && used?.Name == "NewGuid";
                if (external)
                {
                    violations.Add(
                        "DOMAIN_EXTERNAL_DEPENDENCY:" + type.FullName + "." + method.Name + " -> " + full + "." + used?.Name
                    );
                }
            }

            offset += size;
        }
    }
}

int methods = 0, memberUses = 0;
bool strictPage = false;
bool Forbidden(Type? type)
{
    if (type is null)
    {
        return false;
    }

    var ns = type.Namespace ?? "";
    if (new[]
    {
        "Pcl.Core",
        "Pcl.Application",
        "Pcl.Domain",
        "Pcl.Ports",
        "MeloongCore",
        "PCLCS"
    }.Contains(
        type.Assembly.GetName()
            .Name
    ))
    {
        return true;
    }

    return ns == "Pcl.Core.Application" || ns.StartsWith("Pcl.Core.Application.", StringComparison.Ordinal) || ns == "Pcl.Core.Adapters" || ns.StartsWith("Pcl.Core.Adapters.", StringComparison.Ordinal) || type.HasElementType && Forbidden(
        type.GetElementType()
    ) || type.IsGenericType && type.GetGenericArguments()
        .Any(Forbidden);
}

foreach (var type in assembly.GetTypes())
{
    strictPage = type.FullName!.StartsWith("Pcl.Desktop.ComponentManagerWindow", StringComparison.Ordinal) || type.FullName.StartsWith("Pcl.Desktop.ArtifactDownloadWindow", StringComparison.Ordinal) || type.FullName.StartsWith("Pcl.Desktop.PageTasksWindow", StringComparison.Ordinal) || new[]
    {
        "Pcl.Desktop.MultiplayerWindow",
        "Pcl.Desktop.OptiFabricWindow",
        "Pcl.Desktop.ResourceBrowser",
        "Pcl.Desktop.SkinManagerWindow",
        "Pcl.Desktop.AccountToolsWindow",
        "Pcl.Desktop.UnifiedSearchWindow",
        "Pcl.Desktop.CurseForgeWindow",
        "Pcl.Desktop.SettingsHelpWindow",
        "Pcl.Desktop.CacheNetworkWindow",
        "Pcl.Desktop.InstanceWorkspace",
        "Pcl.Desktop.InstanceViewCache",
        "Pcl.Desktop.LocalResourcesWindow",
        "Pcl.Desktop.PackManagerWindow",
        "Pcl.Desktop.PackSelectionWindow"
    }.Any(
        name => type.FullName.StartsWith(name, StringComparison.Ordinal)
    );
    foreach (var field in type.GetFields(
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly
    ))
    {
        if (Forbidden(field.FieldType))
        {
            violations.Add("SHELL_IMPLEMENTATION_FIELD:" + type.FullName + "." + field.Name);
        }
    }

    var members = type.GetMethods(
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly
    )
        .Cast<MethodBase>()
        .Concat(
            type.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly
            )
        );
    foreach (var method in members)
    {
        if (method.GetParameters()
            .Any(
                x => Forbidden(x.ParameterType)
            ) || method is MethodInfo mi && Forbidden(mi.ReturnType))
        {
            violations.Add("SHELL_IMPLEMENTATION_SIGNATURE:" + type.FullName + "." + method.Name);
        }

        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            continue;
        }

        methods++;
        for (int offset = 0; offset < il.Length;)
        {
            ushort key = il[offset++];
            if (key == 0xfe)
            {
                key = (ushort)(0xfe00 | il[offset++]);
            }

            var code = codes[key];
            int size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                _ => 4
            };
            if (code.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var token = BitConverter.ToInt32(il, offset);
                var symbol = method.Module.ResolveMember(
                    token,
                    type.GetGenericArguments(),
                    method.IsGenericMethod
                        ? method.GetGenericArguments()
                        : null
                );
                memberUses++;
                if (symbol is MethodInfo called && called.DeclaringType?.FullName == "Avalonia.Controls.Window" && called.Name == "ShowDialog" && type.FullName?.Contains("Smoke", StringComparison.Ordinal) != true && !method.Name.Contains("Smoke", StringComparison.Ordinal) && type.FullName != "Pcl.Desktop.WindowPresentation")
                {
                    violations.Add("UNMANAGED_DIALOG_PRESENTATION:" + type.FullName + "." + method.Name);
                }

                var usedType = symbol as Type ?? symbol?.DeclaringType;
                // Transitional MainWindow still has legacy display models, but these migrated handlers cannot call old executors.
                var migratedMain = type.FullName!.StartsWith("Pcl.Desktop.MainWindow", StringComparison.Ordinal) && new[]
                {
                    "ManageVersionsAsync",
                    "BuildClassicVersionsAsync",
                    "FabricClick",
                    "MacSetupClick",
                    "LoginClick"
                }.Any(
                    name => (type.FullName + "." + method.Name).Contains(name, StringComparison.Ordinal)
                );
                if (migratedMain && usedType?.Namespace == "Pcl.Core" && new[]
                {
                    "VersionCatalog",
                    "FabricService",
                    "ForgeService",
                    "OptiFineService",
                    "LiteLoaderService",
                    "MacSetupService",
                    "OptiFabricService",
                    "MicrosoftAuthentication",
                    "LaunchHookRunner"
                }.Contains(usedType.Name))
                {
                    violations.Add("SHELL_EXECUTION_USE:" + type.FullName + "." + method.Name + " -> " + usedType.FullName);
                }

                if (Forbidden(usedType))
                {
                    violations.Add(
                        "SHELL_IMPLEMENTATION_USE:" + type.FullName + "." + method.Name + " -> " + usedType!.FullName
                    );
                }
            }

            offset += size;
        }
    }
}

Directory.CreateDirectory(
    Path.GetDirectoryName(output)!
);
File.WriteAllText(
    output,
    JsonSerializer.Serialize(
        new
        {
            status = violations.Count == 0
                ? "passed"
                : "failed",
            runtimeProjectReferences = graph,
            checkedDomainMethods = domainMethods,
            checkedMethods = methods,
            checkedMemberUses = memberUses,
            knownTransitionExceptions = Array.Empty<string>(),
            violations
        },
        new JsonSerializerOptions
        {
            WriteIndented = true
        }
    )
);
foreach (var violation in violations)
{
    Console.Error.WriteLine(violation);
}

Console.WriteLine(
    $"ARCHITECTURE_GUARD: {methods} methods, {memberUses} symbol uses, {violations.Count} violations"
);
return violations.Count == 0
    ? 0
    : 1;
