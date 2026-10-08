using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;

namespace RdsData.AdoNet;

public sealed class RdsDataCommand : DbCommand
{
    private readonly RdsDataParameterCollection _parameters = new();
    private RdsDataConnection? _connection;
    private RdsDataTransaction? _transaction;

    public RdsDataCommand() { }

    public RdsDataCommand(string commandText, RdsDataConnection? connection = null)
    {
        CommandText = commandText;
        _connection = connection;
    }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    /// <summary>
    /// クライアント側のタイムアウト (秒)。0 は無制限。API 呼び出し 1 回ごとに適用し、
    /// DatabaseResumingException のリトライ待ちは含まない (待ちの上限は ResumeRetryCount で決まる)。
    /// Data API 側にも同期実行の上限 (45 秒) があり、超過するとそちらが先にエラーになる。
    /// クライアント側でタイムアウトしてもサーバー側の SQL は止まらない可能性がある点に注意。
    /// タイムアウトの例外は実行済みかどうか不明なため IsTransient=false とする。
    /// </summary>
    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
                throw new NotSupportedException("Only CommandType.Text is supported. Use CALL / SELECT func() instead.");
        }
    }

    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    public new RdsDataConnection? Connection
    {
        get => _connection;
        set => _connection = value;
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value switch
        {
            null => null,
            RdsDataConnection c => c,
            _ => throw new ArgumentException($"Connection must be {nameof(RdsDataConnection)}."),
        };
    }

    public new RdsDataParameterCollection Parameters => _parameters;
    protected override DbParameterCollection DbParameterCollection => _parameters;

    public new RdsDataTransaction? Transaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value switch
        {
            null => null,
            RdsDataTransaction t => t,
            _ => throw new ArgumentException($"Transaction must be {nameof(RdsDataTransaction)}."),
        };
    }

    public new RdsDataParameter CreateParameter() => new();
    protected override DbParameter CreateDbParameter() => CreateParameter();

    public override void Cancel() { /* Data API には実行中ステートメントのキャンセル API がない */ }
    public override void Prepare() { /* no-op */ }

    // ---- 同期 API (sync-over-async。可能な限り async 版を使うこと) ----

    public override int ExecuteNonQuery() =>
        ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override object? ExecuteScalar() =>
        ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    // ---- 非同期 API ----

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var response = await ExecuteCoreAsync(includeResultMetadata: false, cancellationToken).ConfigureAwait(false);
        return checked((int)(response.NumberOfRecordsUpdated ?? 0));
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        return reader.FieldCount > 0 && reader.Read() ? reader.GetValue(0) : null;
    }

    public new async Task<RdsDataReader> ExecuteReaderAsync(CancellationToken cancellationToken) =>
        (RdsDataReader)await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
    {
        // Data API には実行せずにメタデータだけ取る手段がないため、SQL を実行してしまう前に拒否する
        if ((behavior & CommandBehavior.SchemaOnly) != 0)
            throw new NotSupportedException("CommandBehavior.SchemaOnly is not supported by RDS Data API.");

        ExecuteStatementResponse response;
        try
        {
            response = await ExecuteCoreAsync(includeResultMetadata: true, cancellationToken).ConfigureAwait(false);
        }
        catch when ((behavior & CommandBehavior.CloseConnection) != 0 && _connection is not null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            throw;
        }

        var columns = response.ColumnMetadata ?? new List<ColumnMetadata>();

        // SELECT (列あり) の RecordsAffected は ADO.NET の慣例どおり -1
        var recordsAffected = columns.Count > 0 ? -1 : checked((int)(response.NumberOfRecordsUpdated ?? 0));

        return new RdsDataReader(
            response.Records ?? new List<List<Field>>(),
            columns,
            recordsAffected,
            behavior,
            _connection!);
    }

    private async Task<ExecuteStatementResponse> ExecuteCoreAsync(bool includeResultMetadata, CancellationToken cancellationToken)
    {
        var conn = _connection ?? throw new InvalidOperationException("Connection property has not been initialized.");
        conn.EnsureOpen();

        if (string.IsNullOrWhiteSpace(CommandText))
            throw new InvalidOperationException("CommandText has not been set.");

        if (_transaction is not null && !ReferenceEquals(_transaction.Connection, conn))
            throw new InvalidOperationException("The transaction is not associated with this connection or has already completed.");

        // Npgsql と同様、明示されていなければ接続上のアクティブなトランザクションに参加する
        var tx = _transaction ?? conn.CurrentTransaction;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sql = PostgresParameterRewriter.Rewrite(CommandText, _parameters.ContainsName, used);

        // SQL 中で参照されているパラメータだけを送る
        var parameters = new List<SqlParameter>(used.Count);
        for (var i = 0; i < _parameters.Count; i++)
        {
            var p = _parameters[i];
            if (used.Contains(p.ParameterName)) parameters.Add(RdsDataTypeMapper.ToSqlParameter(p));
        }

        var request = new ExecuteStatementRequest
        {
            ResourceArn = conn.Settings.ResourceArn,
            SecretArn = conn.Settings.SecretArn,
            Database = string.IsNullOrEmpty(conn.Database) ? null : conn.Database,
            Sql = sql,
            TransactionId = tx?.TransactionId,
            Parameters = parameters.Count > 0 ? parameters : null,
            IncludeResultMetadata = includeResultMetadata,
            ResultSetOptions = new ResultSetOptions
            {
                DecimalReturnType = DecimalReturnType.STRING, // numeric を精度落ちなく受け取る
                LongReturnType = LongReturnType.LONG,
            },
        };

        return await RdsDataExecutor.ExecuteAsync(
            ct => conn.Client.ExecuteStatementAsync(request, ct),
            conn.Settings.ResumeRetryCount,
            CommandTimeout > 0 ? TimeSpan.FromSeconds(CommandTimeout) : null,
            cancellationToken).ConfigureAwait(false);
    }
}
