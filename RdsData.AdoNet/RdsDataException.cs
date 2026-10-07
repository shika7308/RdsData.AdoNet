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
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(16);

    /// <summary>
    /// DatabaseResumingException のみリトライする。この例外はステートメント未実行を意味するため、
    /// 非冪等な書き込みでも安全に再試行できる。それ以外の SDK 例外は RdsDataException に変換する。
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action, int resumeRetryCount, CancellationToken cancellationToken)
    {
        var delay = InitialDelay;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action(cancellationToken).ConfigureAwait(false);
            }
            catch (DatabaseResumingException) when (attempt < resumeRetryCount)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = delay * 2 > MaxDelay ? MaxDelay : delay * 2;
            }
            catch (AmazonRDSDataServiceException ex)
            {
                throw RdsDataException.From(ex);
            }
        }
    }
}

/// <summary>SDK クライアントはスレッドセーフかつ生成コストが高いため、リージョン単位で共有する。</summary>
internal static class RdsDataClientCache
{
    private static readonly ConcurrentDictionary<string, IAmazonRDSDataService> Clients = new(StringComparer.Ordinal);

    public static IAmazonRDSDataService Get(string? region) =>
        Clients.GetOrAdd(region ?? string.Empty, static r => r.Length == 0
            ? new AmazonRDSDataServiceClient()
            : new AmazonRDSDataServiceClient(RegionEndpoint.GetBySystemName(r)));
}
