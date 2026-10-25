namespace Pcl.Core;
// 保留原命名空间与 Get/Set/Delete 契约；只迁移定义位置，兼容已有账户管理和调用方。
// [实现] 接口契约：IAccountSecrets 声明模块依赖的操作，由具体实现负责存储或系统交互。
public interface IAccountSecrets
{
    // [实现] Get 的实现：仅声明调用契约，由实现类提供行为。
    string? Get(string id);
    // [实现] Set 的实现：仅声明调用契约，由实现类提供行为。
    void Set(string id, string secret);
    // [实现] Delete 的实现：仅声明调用契约，由实现类提供行为。
    void Delete(string id);
}
