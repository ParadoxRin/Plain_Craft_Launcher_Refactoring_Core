# Desktop 图标请求队列测试

使用 .NET 10 SDK 在仓库根目录运行：

```sh
dotnet run --project tests/Pcl.DesktopQueue.Tests/Pcl.DesktopQueue.Tests.csproj
```

此项目直接链接生产代码 `src/Pcl.Desktop/IconRequestQueue.cs`，无需 Avalonia、Host、网络或真实账户。断言失败时进程以非零状态退出。

验证 24 个突发请求最多放行 4 个；页面取消后排队请求不执行；请求异常释放槽位；新页面可继续请求；退出等待全部任务结束并拒绝新增请求。实际界面挂载和 Host 队列交互仍由集成测试验证。
