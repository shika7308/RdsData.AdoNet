using System.Data;
using System.Data.Common;
using Amazon.RDSDataService.Model;

namespace RdsData.AdoNet;

/// <summary>
/// Data API の transactionId を保持する。Commit/Rollback されずに Dispose された場合はロールバックを試みる。
/// サーバー側のアイドルタイムアウトで既に破棄されている場合、ロールバック失敗は握りつぶす。
/// </summary>
public sealed class RdsDataTransaction : DbTransaction
{
    private RdsDataConnection? _connection;

    internal RdsDataTransaction(RdsDataConnection connection, string transactionId)
    {
        _connection = connection;
        TransactionId = transactionId;
    }

    public string TransactionId { get; }

    public new RdsDataConnection? Connection => _connection;

    protected override DbConnection? DbConnection => _connection;

    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    public override void Commit() => CommitAsync().GetAwaiter().GetResult();

    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        var conn = EnsureActive();
        var request = new CommitTransactionRequest
        {
            ResourceArn = conn.Settings.ResourceArn,
            SecretArn = conn.Settings.SecretArn,
            TransactionId = TransactionId,
        };
        // コミット失敗時は状態不明のため Complete しない (後続の Dispose でロールバックを試みる)
        await RdsDataExecutor.ExecuteAsync(ct => conn.Client.CommitTransactionAsync(request, ct), 0, cancellationToken)
            .ConfigureAwait(false);
        Complete();
    }

    public override void Rollback() => RollbackAsync().GetAwaiter().GetResult();

    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        var conn = EnsureActive();
        var request = new RollbackTransactionRequest
        {
            ResourceArn = conn.Settings.ResourceArn,
            SecretArn = conn.Settings.SecretArn,
            TransactionId = TransactionId,
        };
        try
        {
            await RdsDataExecutor.ExecuteAsync(ct => conn.Client.RollbackTransactionAsync(request, ct), 0, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Complete();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _connection is not null)
        {
            try { Rollback(); }
            catch { /* best effort */ }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            try { await RollbackAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private RdsDataConnection EnsureActive()
    {
        var conn = _connection ?? throw new InvalidOperationException("The transaction has already been committed or rolled back.");
        conn.EnsureOpen();
        return conn;
    }

    private void Complete()
    {
        if (_connection is null) return;
        if (ReferenceEquals(_connection.CurrentTransaction, this)) _connection.CurrentTransaction = null;
        _connection = null;
    }
}
