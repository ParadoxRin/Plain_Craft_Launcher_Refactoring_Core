namespace Pcl.Core.Contracts.Workspaces;
/// <summary>只含可显示计划与不透明指纹，归属判定和事务留在 Host。</summary>
public sealed record ShaderEnvironmentPreview(string Fingerprint, string[] Remove, string[] Retain, string[] Conflicts)
{
    public bool CanApply => Remove.Length > 0 && Conflicts.Length == 0;
}

public sealed record ShaderEnvironmentResult(string[] Removed, string[] Retained, string BackupDirectory);
