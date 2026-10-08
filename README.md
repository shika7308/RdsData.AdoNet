# RdsData.AdoNet

Aurora PostgreSQL 向け RDS Data API の ADO.NET ラッパー。Dapper から普通に使えることを最小ゴールとする。

## 構成

```
src/RdsData.AdoNet/
  RdsDataConnectionStringBuilder.cs  接続文字列 (ResourceArn / SecretArn / Database / Region ...)
  RdsDataConnection.cs               Open/Close はローカル状態のみ。SDK クライアントはリージョン単位で共有
  RdsDataCommand.cs                  ExecuteStatement 呼び出し、@name → :name 変換、タイムアウト
  RdsDataParameter.cs                パラメータ + コレクション (TypeHint を明示指定可能)
  RdsDataReader.cs                   全件バッファ型 DataReader
  RdsDataTransaction.cs              transactionId を保持。未完了 Dispose でロールバック
  RdsDataTypeMapper.cs               CLR ⇔ Field / TypeHint の変換
  PostgresParameterRewriter.cs       文字列・コメント・ドル引用を考慮したパラメータ書き換え
  RdsDataException.cs                DbException 派生 (SqlState / IsTransient)、再開中リトライ
  RdsDataProviderFactory.cs          DbProviderFactory / DbDataSource
tests/RdsData.AdoNet.Tests/          書き換えと型変換のユニットテスト
```

## 使い方

```csharp
// 接続文字列 (ARN はプレースホルダ)
const string cs =
    "ResourceArn=arn:aws:rds:ap-northeast-1:<ACCOUNT_ID>:cluster:<CLUSTER_NAME>;" +
    "SecretArn=arn:aws:secretsmanager:ap-northeast-1:<ACCOUNT_ID>:secret:<SECRET_NAME>;" +
    "Database=appdb;Region=ap-northeast-1";

// DI (ASP.NET Core)
builder.Services.AddSingleton(new RdsDataSource(cs));

// Dapper
await using var conn = await dataSource.OpenConnectionAsync();

var user = await conn.QuerySingleOrDefaultAsync<User>(
    "SELECT id, name, created_at AS CreatedAt FROM users WHERE id = @Id", new { Id = 42 });

var users = await conn.QueryAsync<User>(
    "SELECT id, name FROM users WHERE id IN @Ids", new { Ids = new[] { 1, 2, 3 } });

await using var tx = await conn.BeginTransactionAsync();
var newId = await conn.ExecuteScalarAsync<long>(
    "INSERT INTO users (name, profile) VALUES (@Name, @Profile::jsonb) RETURNING id",
    new { Name = "foo", Profile = "{\"lang\":\"ja\"}" }, tx);
await tx.CommitAsync();
```

`IN` 句は Dapper の展開 (`WHERE id IN @Ids`) を使う。jsonb 列は SQL 側で `@Doc::jsonb` とキャストするか、ADO.NET を直接使う場合は `RdsDataParameter.TypeHint = TypeHint.JSON` を指定する。`= ANY(@Ids)` は使えない。Dapper が配列をそのまま渡すのは Npgsql の接続に限られ、それ以外では `(@Ids1,@Ids2)` に展開されて PostgreSQL の構文エラーになる。ADO.NET を直接使って配列を渡した場合は `NotSupportedException`。

## リトライとタイムアウト

- AWS SDK の自動リトライは無効化している (`MaxErrorRetry = 0`)。`ExecuteStatement` / `CommitTransaction` は冪等ではなく、応答喪失後の再送で二重実行やコミットの偽失敗が起きるため
- 自動で再試行するのは、ステートメント未実行が確定しているエラー (`DatabaseResumingException`、スロットリング) のみ。回数は接続文字列の `ResumeRetryCount` (既定 5、待機は 1 秒から倍々で最大 16 秒)
- `CommandTimeout` は API 呼び出し 1 回ごとに適用し、再開待ちの時間は含まない
- タイムアウト時の `RdsDataException` は `IsTransient = false`。サーバー側で実行済みの可能性があるため、再試行するかは呼び出し側で判断すること
- `Commit` が例外を投げた場合、コミット済みかどうかは不明として扱うこと
- `RdsDataSource` / `RdsDataConnection` に独自の `IAmazonRDSDataService` を渡す場合は、そのクライアントでも `MaxErrorRetry = 0` を設定すること

## 必要な IAM 権限

```json
{
  "Effect": "Allow",
  "Action": [
    "rds-data:ExecuteStatement",
    "rds-data:BeginTransaction",
    "rds-data:CommitTransaction",
    "rds-data:RollbackTransaction"
  ],
  "Resource": "arn:aws:rds:ap-northeast-1:<ACCOUNT_ID>:cluster:<CLUSTER_NAME>"
},
{
  "Effect": "Allow",
  "Action": "secretsmanager:GetSecretValue",
  "Resource": "arn:aws:secretsmanager:ap-northeast-1:<ACCOUNT_ID>:secret:<SECRET_NAME>-*"
}
```

## 型マッピング

| PostgreSQL | 送信 (CLR → Data API) | 受信 (Data API → CLR) |
|---|---|---|
| int2 / int4 / int8 | `LongValue` | `short` / `int` / `long` |
| numeric | `StringValue` + `DECIMAL` | `decimal` (`DecimalReturnType=STRING` で受信) |
| float4 / float8 | `DoubleValue` | `float` / `double` |
| bool | `BooleanValue` | `bool` |
| uuid | `StringValue` + `UUID` | `Guid` |
| date | `DateOnly` または `DateTime`+`DbType.Date` → `DATE` | `DateTime` |
| timestamp | `DateTime` → `TIMESTAMP` | `DateTime` (Kind=Unspecified) |
| timestamptz | `DateTime`/`DateTimeOffset` を UTC 化 → `TIMESTAMP` | `DateTime` (Kind=Utc) |
| time | `TimeOnly` / `TimeSpan` → `TIME` | `TimeSpan` |
| bytea | `BlobValue` | `byte[]` |
| json / jsonb / text 等 | `StringValue` | `string` |

`GetFieldType()` と `GetValue()` の実行時型は必ず一致させている (Dapper の変換コード生成がこれに依存するため)。

## 既知の制約

- 結果セットは 1 つのみ。マルチステートメント、`QueryMultiple` は非対応
- レスポンスサイズ上限あり (大きな結果は Data API 側でエラー)。ページングは SQL で行う
- 同期実行の上限は Data API 側で 45 秒
- 分離レベル指定不可 (READ COMMITTED 固定)、ネストトランザクション・セーブポイント・`TransactionScope` 非対応
- 出力パラメータ、`CommandType.StoredProcedure` 非対応
- 同期 API は sync-over-async。Lambda / ASP.NET Core では async API を使うこと

## 要検証事項 (実環境で確認が必要)

- TIMESTAMP / TIME パラメータの小数部を 6 桁まで受け付けるか
- timestamptz の受信文字列にオフセットが付くか、セッション TimeZone が UTC か
- エラーメッセージの `SQLState: xxxxx` 形式 (SqlState 抽出用正規表現)
- `ColumnMetadata.Label` / `Name` へのエイリアスの入り方
- serial 列等で `TypeName` が `int4` ではなく `serial` で返るケース (マッピング済みだが網羅性要確認)
