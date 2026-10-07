using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Amazon.RDSDataService.Model;

namespace RdsData.AdoNet;

/// <summary>
/// Data API はカーソル/ストリーミングを持たないため、レスポンス全体をバッファした上で読み出す。
/// 結果セットは常に 1 つ (NextResult は false)。
/// </summary>
public sealed class RdsDataReader : DbDataReader
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly List<List<Field>> _records;
    private readonly List<ColumnMetadata> _columns;
    private readonly Type[] _types;
    private readonly string[] _names;
    private readonly int _recordsAffected;
    private readonly CommandBehavior _behavior;
    private readonly RdsDataConnection _connection;

    private int _index = -1;
    private object[]? _current;
    private bool _closed;

    internal RdsDataReader(
        List<List<Field>> records,
        List<ColumnMetadata> columns,
        int recordsAffected,
        CommandBehavior behavior,
        RdsDataConnection connection)
    {
        _records = records;
        _columns = columns;
        _recordsAffected = recordsAffected;
        _behavior = behavior;
        _connection = connection;

        _types = new Type[columns.Count];
        _names = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            _types[i] = RdsDataTypeMapper.ResolveClrType(columns[i]);
            // Label = エイリアス (AS 句)、Name = 元の列名
            _names[i] = !string.IsNullOrEmpty(columns[i].Label) ? columns[i].Label : columns[i].Name ?? $"?column{i}?";
        }
    }

    public override int FieldCount => _columns.Count;
    public override bool HasRows => _records.Count > 0;
    public override bool IsClosed => _closed;
    public override int RecordsAffected => _recordsAffected;
    public override int Depth => 0;

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read()
    {
        EnsureNotClosed();
        if (_index + 1 >= _records.Count)
        {
            _index = _records.Count;
            _current = null;
            return false;
        }

        _index++;
        var row = _records[_index];
        var values = new object[_types.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = RdsDataTypeMapper.FromField(row[i], _types[i], _columns[i].TypeName);
        }
        _current = values;
        return true;
    }

    public override bool NextResult()
    {
        EnsureNotClosed();
        _index = _records.Count;
        _current = null;
        return false;
    }

    public override object GetValue(int ordinal) => Current[ordinal];

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        Array.Copy(Current, values, count);
        return count;
    }

    public override bool IsDBNull(int ordinal) => Current[ordinal] is DBNull;

    public override string GetName(int ordinal) => _names[ordinal];

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _names.Length; i++)
            if (string.Equals(_names[i], name, StringComparison.Ordinal)) return i;
        for (var i = 0; i < _names.Length; i++)
            if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new IndexOutOfRangeException($"Column '{name}' not found.");
    }

    public override Type GetFieldType(int ordinal) => _types[ordinal];

    public override string GetDataTypeName(int ordinal) => _columns[ordinal].TypeName ?? "unknown";

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal), Inv);
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal), Inv);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal), Inv);
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal), Inv);
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal), Inv);
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal), Inv);
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal), Inv);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal), Inv);
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal), Inv);
    public override string GetString(int ordinal) => Convert.ToString(GetValue(ordinal), Inv) ?? string.Empty;
    public override char GetChar(int ordinal) => GetString(ordinal)[0];

    public override Guid GetGuid(int ordinal) => GetValue(ordinal) switch
    {
        Guid g => g,
        string s => Guid.Parse(s),
        var v => throw new InvalidCastException($"Cannot convert {v.GetType()} to Guid."),
    };

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is T t) return t;
        if (value is DBNull && default(T) is null) return default!;

        object? converted = typeof(T) switch
        {
            var x when x == typeof(DateOnly) && value is DateTime dt => DateOnly.FromDateTime(dt),
            var x when x == typeof(TimeOnly) && value is TimeSpan ts => TimeOnly.FromTimeSpan(ts),
            var x when x == typeof(DateTimeOffset) && value is DateTime dt => new DateTimeOffset(
                dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt),
            _ => Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), Inv),
        };
        return (T)converted!;
    }

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = (byte[])GetValue(ordinal);
        if (buffer is null) return bytes.Length;
        var count = (int)Math.Max(0, Math.Min(length, bytes.Length - dataOffset));
        Array.Copy(bytes, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var s = GetString(ordinal);
        if (buffer is null) return s.Length;
        var count = (int)Math.Max(0, Math.Min(length, s.Length - dataOffset));
        s.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override void Close()
    {
        if (_closed) return;
        _closed = true;
        if ((_behavior & CommandBehavior.CloseConnection) != 0) _connection.Close();
    }

    private object[] Current
    {
        get
        {
            EnsureNotClosed();
            return _current ?? throw new InvalidOperationException("No current row. Call Read() first.");
        }
    }

    private void EnsureNotClosed()
    {
        if (_closed) throw new InvalidOperationException("The reader is closed.");
    }
}
