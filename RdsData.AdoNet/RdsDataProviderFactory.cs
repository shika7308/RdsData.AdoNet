using System.Data.Common;
using Amazon.RDSDataService;

namespace RdsData.AdoNet;

public sealed class RdsDataProviderFactory : DbProviderFactory
{
    public static readonly RdsDataProviderFactory Instance = new();

    private RdsDataProviderFactory() { }

    public override DbConnection CreateConnection() => new RdsDataConnection();
    public override DbCommand CreateCommand() => new RdsDataCommand();
    public override DbParameter CreateParameter() => new RdsDataParameter();
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new RdsDataConnectionStringBuilder();
    public override DbDataSource CreateDataSource(string connectionString) => new RdsDataSource(connectionString);
    public override bool CanCreateDataSourceEnumerator => false;
}

/// <summary>
/// DI 登録用。Singleton で登録し、リクエストごとに OpenConnectionAsync() で接続を取得する。
/// </summary>
public sealed class RdsDataSource : DbDataSource
{
    private readonly string _connectionString;
    private readonly IAmazonRDSDataService? _client;

    public RdsDataSource(string connectionString, IAmazonRDSDataService? client = null)
    {
        new RdsDataConnectionStringBuilder(connectionString).Validate();
        _connectionString = connectionString;
        _client = client;
    }

    public override string ConnectionString => _connectionString;

    public new RdsDataConnection CreateConnection() => (RdsDataConnection)CreateDbConnection();

    public new async ValueTask<RdsDataConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var conn = CreateConnection();
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected override DbConnection CreateDbConnection() =>
        _client is null ? new RdsDataConnection(_connectionString) : new RdsDataConnection(_connectionString, _client);
}
