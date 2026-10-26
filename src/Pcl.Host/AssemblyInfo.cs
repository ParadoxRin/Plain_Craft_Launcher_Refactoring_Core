using System.Runtime.CompilerServices;

// 集成验收直接构造真实组合根，不提供生产 RPC 或全局访问入口。
[assembly: InternalsVisibleTo("Pcl.PhaseTwo.Tests")]
