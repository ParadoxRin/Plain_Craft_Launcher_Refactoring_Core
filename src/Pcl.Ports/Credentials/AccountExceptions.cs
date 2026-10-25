namespace Pcl.Core;
// 凭据边界的受控拒绝，应用服务无需引用具体微软 HTTP 实现即可映射需登录状态。
public sealed class LoginRequiredException(string message) : InvalidOperationException(message);
