using Pcl.Core.Contracts.Installation;
using Pcl.Core.Contracts.Operations;

namespace Pcl.Core.Application.Operations;
// 显式旧界面兼容器：旧按钮的取消意图被转换成 operation.cancel，而非把调用等待令牌绑定后台 CTS。
public static class OperationInstallBridge
{
    public static async Task<VersionInstallResult> InstallAsync(
        IVersionInstallOperations operations,
        VersionInstallRequest request,
        IProgress<VersionInstallProgress>? progress,
        CancellationToken ct
    )
    {
        OperationAcceptance accepted;
        try
        {
            accepted = await operations.AcceptAsync(
                new(
                    Guid.NewGuid()
                        .ToString("N"),
                    request
                ),
                ct
            )
                .ConfigureAwait(false);
        }
        catch (OperationRejectedException ex)
        {
            return new(
                null,
                new(ex.Code)
            );
        }
        catch (OperationCanceledException)
        {
            return new(
                null,
                new("CANCELLED")
            );
        }

        bool cancellationSent = false;
        var completion = operations.WaitAsync(accepted.OperationId);
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested && !cancellationSent)
                {
                    await operations.CancelAsync(accepted.OperationId)
                        .ConfigureAwait(false);
                    cancellationSent = true;
                }

                if (completion.IsCompleted)
                {
                    var final = await completion.ConfigureAwait(false);
                    return new(
                        final.Result,
                        final.State == "succeeded"
                            ? null
                            : new(
                                final.ErrorCode ?? final.State.ToUpperInvariant()
                            )
                    );
                }

                var snapshot = (await operations.GetAsync(accepted.OperationId)
                    .ConfigureAwait(false))!;
                if (snapshot.Progress is { } value)
                {
                    progress?.Report(
                        new(
                            value.Stage,
                            value.Completed,
                            value.Total ?? 0,
                            value.Bytes,
                            value.TotalBytes ?? 0
                        )
                    );
                }

                await Task.Delay(100)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationRejectedException ex)
        {
            return new(
                null,
                new(ex.Code)
            );
        }
    }
}
