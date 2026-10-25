using System.Runtime.CompilerServices;
// 旧核心解析适配器仍调用内部纯求解器；Domain 不反向引用 Core。
[assembly: InternalsVisibleTo("Pcl.Core")]
