using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Amazon.RDSDataService;

namespace RdsData.AdoNet;

public sealed class RdsDataParameter : DbParameter
{
    private string _parameterName = string.Empty;

    public RdsDataParameter() { }

    public RdsDataParameter(string parameterName, object? value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    /// <summary>
    /// 明示的な TypeHint (例: jsonb 列へ string を渡すときの TypeHint.JSON)。
    /// 未指定なら値の CLR 型と DbType から推論する。
    /// </summary>
    public TypeHint? TypeHint { get; set; }

    public override DbType DbType { get; set; } = DbType.Object;

    /// <summary>Data API は出力パラメータを持たないため Input のみ。</summary>
    public override ParameterDirection Direction
    {
        get => ParameterDirection.Input;
        set
        {
            if (value != ParameterDirection.Input)
                throw new NotSupportedException("Only input parameters are supported by RDS Data API.");
        }
    }

    public override bool IsNullable { get; set; }

    /// <summary>先頭の '@' / ':' は取り除いて保持する。</summary>
    [AllowNull]
    public override string ParameterName
    {
        get => _parameterName;
        set => _parameterName = NormalizeName(value);
    }

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override int Size { get; set; }

    public override void ResetDbType() => DbType = DbType.Object;

    internal static string NormalizeName(string? name) =>
        string.IsNullOrEmpty(name) ? string.Empty : name.TrimStart('@', ':');
}

public sealed class RdsDataParameterCollection : DbParameterCollection, IEnumerable<RdsDataParameter>
{
    private readonly List<RdsDataParameter> _items = new();

    public override int Count => _items.Count;
    public override object SyncRoot => ((ICollection)_items).SyncRoot;

    public new RdsDataParameter this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    public new RdsDataParameter this[string parameterName]
    {
        get => (RdsDataParameter)GetParameter(parameterName);
        set => SetParameter(parameterName, value);
    }

    public RdsDataParameter Add(RdsDataParameter parameter)
    {
        _items.Add(parameter ?? throw new ArgumentNullException(nameof(parameter)));
        return parameter;
    }

    public RdsDataParameter AddWithValue(string parameterName, object? value) =>
        Add(new RdsDataParameter(parameterName, value));

    public override int Add(object value)
    {
        Add(Cast(value));
        return _items.Count - 1;
    }

    public override void AddRange(Array values)
    {
        foreach (var v in values) Add(Cast(v));
    }

    public override void Clear() => _items.Clear();

    public override bool Contains(object value) => value is RdsDataParameter p && _items.Contains(p);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    public override IEnumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<RdsDataParameter> IEnumerable<RdsDataParameter>.GetEnumerator() => _items.GetEnumerator();

    public override int IndexOf(object value) => value is RdsDataParameter p ? _items.IndexOf(p) : -1;

    public override int IndexOf(string parameterName)
    {
        var name = RdsDataParameter.NormalizeName(parameterName);
        for (var i = 0; i < _items.Count; i++)
        {
            if (string.Equals(_items[i].ParameterName, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    public override void Insert(int index, object value) => _items.Insert(index, Cast(value));

    public override void Remove(object value) => _items.Remove(Cast(value));

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    public override void RemoveAt(string parameterName) => RemoveAt(IndexOfOrThrow(parameterName));

    protected override DbParameter GetParameter(int index) => _items[index];

    protected override DbParameter GetParameter(string parameterName) => _items[IndexOfOrThrow(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _items[index] = Cast(value);

    protected override void SetParameter(string parameterName, DbParameter value) =>
        _items[IndexOfOrThrow(parameterName)] = Cast(value);

    internal bool ContainsName(string normalizedName) => IndexOf(normalizedName) >= 0;

    private int IndexOfOrThrow(string parameterName)
    {
        var i = IndexOf(parameterName);
        return i >= 0 ? i : throw new IndexOutOfRangeException($"Parameter '{parameterName}' not found.");
    }

    private static RdsDataParameter Cast(object? value) =>
        value as RdsDataParameter
        ?? throw new InvalidCastException($"Expected {nameof(RdsDataParameter)} but got {value?.GetType().Name ?? "null"}.");
}
