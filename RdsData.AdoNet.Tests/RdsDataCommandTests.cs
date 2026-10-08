using System.Data;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using NSubstitute;

namespace RdsData.AdoNet.Tests;

public class RdsDataCommandTests
{
    private const string ConnectionString =
        "ResourceArn=arn:aws:rds:ap-northeast-1:000000000000:cluster:test;" +
        "SecretArn=arn:aws:secretsmanager:ap-northeast-1:000000000000:secret:test;Database=testdb";

    private static async Task<(RdsDataConnection Conn, IAmazonRDSDataService Client)> OpenAsync()
    {
        var client = Substitute.For<IAmazonRDSDataService>();
        var conn = new RdsDataConnection(ConnectionString, client);
        await conn.OpenAsync();
        return (conn, client);
    }

    [Fact]
    public async Task SchemaOnly_is_rejected_without_executing()
    {
        var (conn, client) = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM users";

        await Assert.ThrowsAsync<NotSupportedException>(() => cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly));
        await client.DidNotReceiveWithAnyArgs().ExecuteStatementAsync(default!, default);
    }

    [Fact]
    public async Task CloseConnection_closes_on_failure()
    {
        var (conn, client) = await OpenAsync();
        client.ExecuteStatementAsync(Arg.Any<ExecuteStatementRequest>(), Arg.Any<CancellationToken>())
            .Returns<ExecuteStatementResponse>(_ => throw new BadRequestException("ERROR: syntax error; SQLState: 42601"));
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELEC 1";

        await Assert.ThrowsAsync<RdsDataException>(() => cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection));
        Assert.Equal(ConnectionState.Closed, conn.State);
    }

    [Fact]
    public async Task Command_timeout_is_not_transient()
    {
        var (conn, client) = await OpenAsync();
        client.ExecuteStatementAsync(Arg.Any<ExecuteStatementRequest>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                await Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>());
                return new ExecuteStatementResponse();
            });
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE users SET name = 'x'";
        cmd.CommandTimeout = 1;

        var ex = await Assert.ThrowsAsync<RdsDataException>(() => cmd.ExecuteNonQueryAsync());
        Assert.False(ex.IsTransient);
    }
}
