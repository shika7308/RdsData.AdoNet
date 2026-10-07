using System.Data;
using System.Globalization;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;

namespace RdsData.AdoNet;

internal static class RdsDataTypeMapper
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Data API の TIMESTAMP 形式: YYYY-MM-DD HH:MM:SS[.FFF]
    // 小数部の許容桁数 (6 桁まで通るか) は実環境で要検証
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.FFFFFF";
    private const string DateFormat = "yyyy-MM-dd";
    private const string TimeFormat = "HH:mm:ss.FFFFFF";

    // ---------------------------------------------------------------------
    // CLR -> Data API
    // ---------------------------------------------------------------------

    public static SqlParameter ToSqlParameter(RdsDataParameter p)
    {
        var field = new Field();
        var hint = p.TypeHint;

        switch (p.Value)
        {
            case null or DBNull:
                field.IsNull = true;
                break;
            case bool b:
                field.BooleanValue = b;
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                field.LongValue = Convert.ToInt64(p.Value, Inv);
                break;
            case ulong ul:
                field.StringValue = ul.ToString(Inv);
                hint ??= TypeHint.DECIMAL;
                break;
            case float f:
                field.DoubleValue = f;
                break;
            case double d:
                field.DoubleValue = d;
                break;
            case decimal m:
                // double 経由だと精度が落ちるため文字列 + DECIMAL ヒント
                field.StringValue = m.ToString(Inv);
                hint ??= TypeHint.DECIMAL;
                break;
            case Guid g:
                field.StringValue = g.ToString("D");
                hint ??= TypeHint.UUID;
                break;
            case DateTime dt when p.DbType == DbType.Date:
                field.StringValue = dt.ToString(DateFormat, Inv);
                hint ??= TypeHint.DATE;
                break;
            case DateTime dt:
                // Local は UTC に正規化。Unspecified/Utc はそのまま。
                // timestamptz 列への書き込みはセッションの TimeZone (RDS 既定は UTC) で解釈される
                field.StringValue = (dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt).ToString(TimestampFormat, Inv);
                hint ??= TypeHint.TIMESTAMP;
                break;
            case DateTimeOffset dto:
                field.StringValue = dto.UtcDateTime.ToString(TimestampFormat, Inv);
                hint ??= TypeHint.TIMESTAMP;
                break;
            case DateOnly date:
                field.StringValue = date.ToString(DateFormat, Inv);
                hint ??= TypeHint.DATE;
                break;
            case TimeOnly time:
                field.StringValue = time.ToString(TimeFormat, Inv);
                hint ??= TypeHint.TIME;
                break;
            case TimeSpan ts:
                if (ts < TimeSpan.Zero || ts >= TimeSpan.FromDays(1))
                    throw new NotSupportedException("TimeSpan parameters must be within 00:00:00 to 23:59:59.999999 (mapped to TIME).");
                field.StringValue = TimeOnly.FromTimeSpan(ts).ToString(TimeFormat, Inv);
                hint ??= TypeHint.TIME;
                break;
            case byte[] bytes:
                field.BlobValue = new MemoryStream(bytes, writable: false);
                break;
            case string s:
                field.StringValue = s;
                hint ??= HintFromDbType(p.DbType);
                break;
            case char ch:
                field.StringValue = ch.ToString();
                break;
            case Enum e:
                field.LongValue = Convert.ToInt64(e, Inv);
                break;
            case System.Collections.IEnumerable:
                throw new NotSupportedException(
                    $"Array parameters are not supported by RDS Data API (parameter '{p.ParameterName}'). " +
                    "For IN clauses, let Dapper expand the list.");
            default:
                throw new NotSupportedException(
                    $"Parameter type {p.Value!.GetType().FullName} is not supported (parameter '{p.ParameterName}').");
        }

        return new SqlParameter { Name = p.ParameterName, Value = field, TypeHint = hint };
    }

    private static TypeHint? HintFromDbType(DbType dbType) => dbType switch
    {
        DbType.Date => TypeHint.DATE,
        DbType.Time => TypeHint.TIME,
        DbType.DateTime or DbType.DateTime2 or DbType.DateTimeOffset => TypeHint.TIMESTAMP,
        DbType.Decimal or DbType.Currency or DbType.VarNumeric => TypeHint.DECIMAL,
        DbType.Guid => TypeHint.UUID,
        _ => null,
    };

    // ---------------------------------------------------------------------
    // Data API -> CLR
    // ---------------------------------------------------------------------

    /// <summary>
    /// 列の CLR 型を ColumnMetadata.TypeName (PostgreSQL の型名) から決める。
    /// GetFieldType() と GetValue() の実行時型を必ず一致させること (Dapper はこれを前提に変換コードを生成する)。
    /// </summary>
    public static Type ResolveClrType(ColumnMetadata? meta)
    {
        var typeName = meta?.TypeName?.ToLowerInvariant();
        if (typeName is not null && typeName.StartsWith('_')) return typeof(object); // 配列 (ベストエフォート)

        return typeName switch
        {
            "int2" or "smallint" or "smallserial" => typeof(short),
            "int4" or "int" or "integer" or "serial" => typeof(int),
            "int8" or "bigint" or "bigserial" or "oid" => typeof(long),
            "bool" or "boolean" => typeof(bool),
            "float4" or "real" => typeof(float),
            "float8" or "double precision" => typeof(double),
            "numeric" or "decimal" => typeof(decimal),
            "uuid" => typeof(Guid),
            "date" or "timestamp" or "timestamptz" => typeof(DateTime),
            "time" => typeof(TimeSpan),
            "bytea" => typeof(byte[]),
            // text, varchar, bpchar, json, jsonb, name, timetz, interval, money など
            _ => typeof(string),
        };
    }

    public static object FromField(Field field, Type clrType, string? typeName)
    {
        if (field.IsNull == true) return DBNull.Value;

        if (clrType == typeof(string)) return AsString(field);
        if (clrType == typeof(int)) return checked((int)AsLong(field));
        if (clrType == typeof(long)) return AsLong(field);
        if (clrType == typeof(short)) return checked((short)AsLong(field));
        if (clrType == typeof(bool)) return field.BooleanValue ?? bool.Parse(AsString(field));
        if (clrType == typeof(double)) return AsDouble(field);
        if (clrType == typeof(float)) return (float)AsDouble(field);
        if (clrType == typeof(decimal)) return AsDecimal(field);
        if (clrType == typeof(Guid)) return Guid.Parse(AsString(field));
        if (clrType == typeof(DateTime)) return ParseDateTime(AsString(field), typeName);
        if (clrType == typeof(TimeSpan)) return TimeSpan.Parse(AsString(field), Inv);
        if (clrType == typeof(byte[])) return field.BlobValue?.ToArray() ?? Convert.FromBase64String(AsString(field));
        if (clrType == typeof(object)) return FromArray(field.ArrayValue) ?? (object)AsString(field);

        throw new NotSupportedException($"Unsupported CLR type mapping: {clrType}.");
    }

    private static string AsString(Field f) =>
        f.StringValue
        ?? f.LongValue?.ToString(Inv)
        ?? f.DoubleValue?.ToString("R", Inv)
        ?? f.BooleanValue?.ToString(Inv)
        ?? throw new InvalidCastException("Field has no scalar value.");

    private static long AsLong(Field f) =>
        f.LongValue
        ?? (f.StringValue is { } s ? (long?)long.Parse(s, Inv) : null)
        ?? (f.DoubleValue is { } d ? (long?)checked((long)d) : null)
        ?? throw new InvalidCastException("Field cannot be converted to Int64.");

    private static double AsDouble(Field f) =>
        f.DoubleValue
        ?? f.LongValue
        ?? (f.StringValue is { } s ? (double?)double.Parse(s, NumberStyles.Float, Inv) : null)
        ?? throw new InvalidCastException("Field cannot be converted to Double.");

    private static decimal AsDecimal(Field f) =>
        f.StringValue is { } s ? decimal.Parse(s, NumberStyles.Float, Inv)
        : f.LongValue is { } l ? l
        : f.DoubleValue is { } d ? (decimal)d
        : throw new InvalidCastException("Field cannot be converted to Decimal.");

    private static DateTime ParseDateTime(string s, string? typeName)
    {
        if (s.Equals("infinity", StringComparison.OrdinalIgnoreCase)) return DateTime.MaxValue;
        if (s.Equals("-infinity", StringComparison.OrdinalIgnoreCase)) return DateTime.MinValue;

        // timestamptz は UTC として扱い Kind=Utc で返す (Npgsql と同じ挙動)。
        // Data API が返す文字列にオフセットが付くかは要検証。付いていても AdjustToUniversal で吸収される。
        return string.Equals(typeName, "timestamptz", StringComparison.OrdinalIgnoreCase)
            ? DateTime.Parse(s, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            : DateTime.Parse(s, Inv, DateTimeStyles.None);
    }

    private static object? FromArray(ArrayValue? array)
    {
        if (array is null) return null;
        if (array.LongValues is not null) return array.LongValues.ToArray();
        if (array.StringValues is not null) return array.StringValues.ToArray();
        if (array.DoubleValues is not null) return array.DoubleValues.ToArray();
        if (array.BooleanValues is not null) return array.BooleanValues.ToArray();
        if (array.ArrayValues is not null) return array.ArrayValues.Select(FromArray).ToArray();
        return Array.Empty<object>();
    }
}
