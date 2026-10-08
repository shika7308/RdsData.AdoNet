using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;

namespace RdsData.AdoNet;

public sealed partial class RdsDataException : DbException
{
    private readonly string? _sqlState;
    private readonly bool _isTransient;

    public RdsDataException(string message, Exception? innerException = null, string? sqlState = null, bool isTransient = false)
        : base(message, innerException)
    {
        _sqlState = sqlState;
        _isTransient = isTransient;
    }

    /// <summary>PostgreSQL の SQLSTATE (例: 23505 = unique_violation)。取得できない場合は null。</summary>
    public override string? SqlState => _sqlState;

    public override bool IsTransient => _isTransient;

    // Data API (PostgreSQL) のエラーメッセージ末尾に "SQLState: 23505" の形式で含まれる想定。実メッセージで要確認。
    [GeneratedRegex(@"SQLState:\s*([0-9A-Z]{5})")]
    private static partial Regex SqlStateRegex();

    internal static RdsDataException From(AmazonRDSDataServiceException ex)
    {
        var match = SqlStateRegex().Match(ex.Message ?? string.Empty);
        var transient = ex is DatabaseResumingException
            or DatabaseUnavailableException
            or ServiceUnavailableErrorException
            || ex.ErrorCode is "ThrottlingException";
        return new RdsDataException(ex.Message ?? ex.ErrorCode ?? "RDS Data API error.", ex,
            match.Success ? match.Groups[1].Value : null, transient);
    }
}

internal static class RdsDataExecutor
{
    private static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(16);

    /// <summary>
    /// ステートメント未実行が確定しているエラー (DatabaseResumingException / スロットリング) のみリトライする。
    /// 非冪等な書き込みでも安全に再試行できる。それ以外の SDK 例外は RdsDataException に変換する。
    /// </summary>
    /// <param name="attemptTimeout">
    /// API 呼び出し 1 回ごとのタイムアウト。リトライ間の待機は含まない
    /// (0 ACU からの再開待ちでコマンドタイムアウトを使い切らないようにするため)。
    /// </param>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        int resumeRetryCount,
        TimeSpan? attemptTimeout,
        CancellationToken cancellationToken,
        TimeSpan? initialDelay = null)
    {
        var delay = initialDelay ?? DefaultInitialDelay;
        for (var attempt = 0; ; attempt++)
        {
            using var timeoutCts = attemptTimeout is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
            if (attemptTimeout is { } t) timeoutCts!.CancelAfter(t);

            try
            {
                return await action(timeoutCts?.Token ?? cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonRDSDataServiceException ex) when (IsRetryable(ex) && attempt < resumeRetryCount)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = delay * 2 > MaxDelay ? MaxDelay : delay * 2;
            }
            catch (AmazonRDSDataServiceException ex)
            {
                throw RdsDataException.From(ex);
            }
            catch (OperationCanceledException ex)
                when (timeoutCts is { IsCancellationRequested: true } && !cancellationToken.IsCancellationRequested)
            {
                // タイムアウト時はサーバー側で実行済みの可能性がある。再試行は呼び出し側の判断に委ねるため IsTransient=false
                throw new RdsDataException(
                    $"The RDS Data API call timed out after {attemptTimeout!.Value.TotalSeconds:0.###} seconds. " +
                    "The statement may or may not have been executed on the server.", ex);
            }
        }
    }

    private static bool IsRetryable(AmazonRDSDataServiceException ex) =>
        ex is DatabaseResumingException || ex.ErrorCode is "ThrottlingException";
}

/// <summary>SDK クライアントはスレッドセーフかつ生成コストが高いため、リージョン単位で共有する。</summary>
internal static class RdsDataClientCache
{
    private static readonly ConcurrentDictionary<string, IAmazonRDSDataService> Clients = new(StringComparer.Ordinal);

    public static IAmazonRDSDataService Get(string? region) =>
        Clients.GetOrAdd(region ?? string.Empty, static r => new AmazonRDSDataServiceClient(CreateConfig(r)));

    /// <summary>
    /// SDK の自動リトライは無効化する。ExecuteStatement / CommitTransaction は冪等ではなく、
    /// 応答が失われた後に再送すると二重実行やコミットの偽失敗につながるため。
    /// 安全なリトライは <see cref="RdsDataExecutor"/> が行う。
    /// </summary>
    internal static AmazonRDSDataServiceConfig CreateConfig(string? region)
    {
        var config = new AmazonRDSDataServiceConfig { MaxErrorRetry = 0 };
        if (!string.IsNullOrEmpty(region)) config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        return config;
    }
}
