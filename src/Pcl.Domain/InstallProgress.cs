namespace Pcl.Core;
// 下载和安装进度的不可变值；不接触磁盘或平台。
public sealed record InstallProgress(string Stage, int Completed, int Total, long Bytes, long TotalBytes)
{
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)Bytes / TotalBytes, 0, 1)
        : 0;
}
