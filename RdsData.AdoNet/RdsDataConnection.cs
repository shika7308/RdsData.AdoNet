using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;

namespace RdsData.AdoNet;

/// <summary>
/// Data API はステートレスなので、Open/Close はローカルの状態遷移のみ。
/// 物理的な接続やプールは存在しない。
/// </summary>
public sealed class RdsDataConnection : DbConnection
{
    private readonly IAmazonRDSDataService? _injectedClient;
    private string _connectionString = string.Empty;
    private RdsDataConnectionStringBuilder _settings = new();
    private IAmazonRDSDataService? _client;
    private ConnectionState _state = ConnectionState.Closed;
    private string? _database;

    public RdsDataConnection() { }

    public RdsDataConnection(string connectionString)
    {
        ConnectionString = connectionString;
    }

    /// <summary>テストや独自設定のクライアントを使う場合。クライアントの破棄は呼び出し側の責任。</summary>
    public RdsDataConnection(string connectionString, IAmazonRDSDataService client) : this(connectionString)
    {
        _injectedClient = client ?? throw new ArgumentNullException(nameof(client));
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (_state != ConnectionState.Closed)
                throw new InvalidOperationException("ConnectionString cannot be changed while the connection is open.");
            _connectionString = value ?? string.Empty;
            _settings = new RdsDataConnectionStringBuilder(_connectionString);
            _database = _settings.Database;
        }
    }

    public override string Database => _database ?? string.Empty;
    public override string DataSource => _settings.ResourceArn ?? string.Empty;
    public override string ServerVersion => "PostgreSQL (RDS Data API)";
    public override ConnectionState State => _state;

    protected override DbProviderFactory DbProviderFactory => RdsDataProviderFactory.Instance;

    internal RdsDataConnectionStringBuilder Settings => _settings;
    internal RdsDataTransaction? CurrentTransaction { get; set; }

    internal IAmazonRDSDataService Client =>
        _client ?? throw new InvalidOperationException("The connection is not open.");

    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state == ConnectionState.Open)
            throw new InvalidOperationException("The connection is already open.");

        _settings.Validate();
        _client = _injectedClient ?? RdsDataClientCache.Get(_settings.Region);
        _state = ConnectionState.Open;

        if (!_settings.ValidateOnOpen) return;

        try
        {
            await using var cmd = CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _state = ConnectionState.Closed;
            _client = null;
            throw;
        }
    }

    public override void Close()
    {
        if (_state == ConnectionState.Closed) return;
        CurrentTransaction?.Dispose(); // 未完了ならロールバック
        _state = ConnectionState.Closed;
        _client = null;
    }

    public override async Task CloseAsync()
    {
        if (_state == ConnectionState.Closed) return;
        if (CurrentTransaction is { } tx) await tx.DisposeAsync().ConfigureAwait(false);
        _state = ConnectionState.Closed;
        _client = null;
    }

    public override void ChangeDatabase(string databaseName)
    {
        if (CurrentTransaction is not null)
            throw new InvalidOperationException("Cannot change database while a transaction is active.");
        _database = databaseName;
    }

    public new RdsDataCommand CreateCommand() => new() { Connection = this };

    protected override DbCommand CreateDbCommand() => CreateCommand();

    public new RdsDataTransaction BeginTransaction() => (RdsDataTransaction)BeginDbTransaction(IsolationLevel.Unspecified);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        BeginDbTransactionAsync(isolationLevel, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel, CancellationToken cancellationToken)
    {
        EnsureOpen();

        // Data API では分離レベルを指定できない。PostgreSQL の既定 (READ COMMITTED) のみ受け付ける。
        if (isolationLevel is not (IsolationLevel.Unspecified or IsolationLevel.ReadCommitted))
            throw new NotSupportedException($"IsolationLevel.{isolationLevel} is not supported by RDS Data API.");

        if (CurrentTransaction is not null)
            throw new InvalidOperationException("Nested transactions are not supported.");

        var request = new BeginTransactionRequest
        {
            ResourceArn = _settings.ResourceArn,
            SecretArn = _settings.SecretArn,
            Database = string.IsNullOrEmpty(_database) ? null : _database,
        };

        var response = await RdsDataExecutor.ExecuteAsync(
            ct => Client.BeginTransactionAsync(request, ct),
            _settings.ResumeRetryCount,
            cancellationToken).ConfigureAwait(false);

        var tx = new RdsDataTransaction(this, response.TransactionId);
        CurrentTransaction = tx;
        return tx;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    internal void EnsureOpen()
    {
        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("The connection is not open.");
    }
}
