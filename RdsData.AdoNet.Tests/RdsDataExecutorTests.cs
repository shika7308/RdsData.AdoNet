using System.Net;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;
using Amazon.Runtime;

namespace RdsData.AdoNet.Tests;

public class RdsDataExecutorTests
{
    private static readonly TimeSpan FastDelay = TimeSpan.FromMilliseconds(1);

    private static AmazonRDSDataServiceException Throttling() =>
        new("Rate exceeded", ErrorType.Sender, "ThrottlingException", "req", HttpStatusCode.BadRequest);

    [Fact]
    public async Task Retries_database_resuming_then_succeeds()
    {
        var calls = 0;
        var result = await RdsDataExecutor.ExecuteAsync(_ =>
        {
            calls++;
            return calls < 3 ? throw new DatabaseResumingException("resuming") : Task.FromResult(42);
        }, resumeRetryCount: 5, attemptTimeout: null, CancellationToken.None, FastDelay);

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Retries_throttling()
    {
        var calls = 0;
        var result = await RdsDataExecutor.ExecuteAsync(_ =>
        {
            calls++;
            return calls < 2 ? throw Throttling() : Task.FromResult(1);
        }, resumeRetryCount: 5, attemptTimeout: null, CancellationToken.None, FastDelay);

        Assert.Equal(1, result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Does_not_retry_other_errors()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<RdsDataException>(() => RdsDataExecutor.ExecuteAsync<int>(_ =>
        {
            calls++;
            throw new BadRequestException("ERROR: duplicate key value violates unique constraint; SQLState: 23505");
        }, resumeRetryCount: 5, attemptTimeout: null, CancellationToken.None, FastDelay));

        Assert.Equal(1, calls);
        Assert.Equal("23505", ex.SqlState);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task Gives_up_after_retry_count_as_transient_error()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<RdsDataException>(() => RdsDataExecutor.ExecuteAsync<int>(_ =>
        {
            calls++;
            throw new DatabaseResumingException("resuming");
        }, resumeRetryCount: 2, attemptTimeout: null, CancellationToken.None, FastDelay));

        Assert.Equal(3, calls);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Timeout_applies_per_attempt_and_excludes_retry_delay()
    {
        // 待機の合計 (300ms + 600ms) が attemptTimeout (200ms) を超えても、各呼び出しが速ければ成功する
        var calls = 0;
        var result = await RdsDataExecutor.ExecuteAsync(_ =>
        {
            calls++;
            return calls < 3 ? throw new DatabaseResumingException("resuming") : Task.FromResult(7);
        }, resumeRetryCount: 5, TimeSpan.FromMilliseconds(200), CancellationToken.None, TimeSpan.FromMilliseconds(300));

        Assert.Equal(7, result);
    }

    [Fact]
    public async Task Timeout_is_not_transient()
    {
        var ex = await Assert.ThrowsAsync<RdsDataException>(() => RdsDataExecutor.ExecuteAsync(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }, resumeRetryCount: 5, TimeSpan.FromMilliseconds(50), CancellationToken.None, FastDelay));

        Assert.False(ex.IsTransient);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RdsDataExecutor.ExecuteAsync(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }, resumeRetryCount: 5, TimeSpan.FromSeconds(30), cts.Token, FastDelay));
    }

    [Theory]
    [InlineData("ap-northeast-1")]
    [InlineData(null)]
    public void Sdk_retries_are_disabled(string? region)
    {
        var config = RdsDataClientCache.CreateConfig(region);
        Assert.Equal(0, config.MaxErrorRetry);
        if (region is not null) Assert.Equal(region, config.RegionEndpoint.SystemName);
    }
}
