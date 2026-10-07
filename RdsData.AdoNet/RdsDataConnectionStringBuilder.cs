using System.Data.Common;
using System.Globalization;

namespace RdsData.AdoNet;

/// <summary>
/// 例: ResourceArn=arn:aws:rds:...;SecretArn=arn:aws:secretsmanager:...;Database=mydb;Region=ap-northeast-1
/// 認証情報は含めない。SDK の標準クレデンシャルチェーンを使う。
/// </summary>
public sealed class RdsDataConnectionStringBuilder : DbConnectionStringBuilder
{
    public RdsDataConnectionStringBuilder() { }

    public RdsDataConnectionStringBuilder(string? connectionString)
    {
        ConnectionString = connectionString ?? string.Empty;
    }

    /// <summary>Aurora クラスターの ARN。</summary>
    public string? ResourceArn
    {
        get => GetString(nameof(ResourceArn));
        set => SetOrRemove(nameof(ResourceArn), value);
    }

    /// <summary>DB 認証情報を格納した Secrets Manager シークレットの ARN。</summary>
    public string? SecretArn
    {
        get => GetString(nameof(SecretArn));
        set => SetOrRemove(nameof(SecretArn), value);
    }

    public string? Database
    {
        get => GetString(nameof(Database));
        set => SetOrRemove(nameof(Database), value);
    }

    /// <summary>省略時は SDK の既定リージョン解決に従う。</summary>
    public string? Region
    {
        get => GetString(nameof(Region));
        set => SetOrRemove(nameof(Region), value);
    }

    /// <summary>DatabaseResumingException (0 ACU からの再開中) を受けたときの最大リトライ回数。</summary>
    public int ResumeRetryCount
    {
        get => TryGetValue(nameof(ResumeRetryCount), out var v)
            ? Convert.ToInt32(v, CultureInfo.InvariantCulture)
            : 5;
        set => this[nameof(ResumeRetryCount)] = value;
    }

    /// <summary>true の場合、Open 時に SELECT 1 を実行して疎通確認する。</summary>
    public bool ValidateOnOpen
    {
        get => TryGetValue(nameof(ValidateOnOpen), out var v) && Convert.ToBoolean(v, CultureInfo.InvariantCulture);
        set => this[nameof(ValidateOnOpen)] = value;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ResourceArn))
            throw new ArgumentException("ResourceArn is required in the connection string.");
        if (string.IsNullOrWhiteSpace(SecretArn))
            throw new ArgumentException("SecretArn is required in the connection string.");
    }

    private string? GetString(string key) =>
        TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    private void SetOrRemove(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) Remove(key);
        else this[key] = value;
    }
}
