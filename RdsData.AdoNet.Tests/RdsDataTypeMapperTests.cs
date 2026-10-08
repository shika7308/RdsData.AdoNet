using System.Data;
using Amazon.RDSDataService;
using Amazon.RDSDataService.Model;

namespace RdsData.AdoNet.Tests;

public class RdsDataTypeMapperTests
{
    [Fact]
    public void Decimal_is_sent_as_string_with_decimal_hint()
    {
        var sp = RdsDataTypeMapper.ToSqlParameter(new RdsDataParameter("@Price", 1234.5678m));
        Assert.Equal("Price", sp.Name);
        Assert.Equal("1234.5678", sp.Value.StringValue);
        Assert.Equal(TypeHint.DECIMAL, sp.TypeHint);
    }

    [Fact]
    public void DateTime_is_sent_as_timestamp()
    {
        var dt = new DateTime(2026, 10, 7, 9, 30, 0, 123, DateTimeKind.Utc);
        var sp = RdsDataTypeMapper.ToSqlParameter(new RdsDataParameter("At", dt));
        Assert.Equal("2026-10-07 09:30:00.123", sp.Value.StringValue);
        Assert.Equal(TypeHint.TIMESTAMP, sp.TypeHint);
    }

    [Fact]
    public void DateTime_with_DbType_Date_is_sent_as_date()
    {
        var p = new RdsDataParameter("D", new DateTime(2026, 10, 7)) { DbType = DbType.Date };
        var sp = RdsDataTypeMapper.ToSqlParameter(p);
        Assert.Equal("2026-10-07", sp.Value.StringValue);
        Assert.Equal(TypeHint.DATE, sp.TypeHint);
    }

    [Fact]
    public void Null_is_sent_as_isNull()
    {
        var sp = RdsDataTypeMapper.ToSqlParameter(new RdsDataParameter("X", null));
        Assert.True(sp.Value.IsNull);
    }

    [Fact]
    public void Explicit_json_hint_is_kept()
    {
        var p = new RdsDataParameter("Doc", "{\"a\":1}") { TypeHint = TypeHint.JSON };
        Assert.Equal(TypeHint.JSON, RdsDataTypeMapper.ToSqlParameter(p).TypeHint);
    }

    [Fact]
    public void Arrays_are_rejected()
    {
        Assert.Throws<NotSupportedException>(() =>
            RdsDataTypeMapper.ToSqlParameter(new RdsDataParameter("Ids", new[] { 1, 2, 3 })));
    }

    [Theory]
    [InlineData("int4", typeof(int))]
    [InlineData("int8", typeof(long))]
    [InlineData("numeric", typeof(decimal))]
    [InlineData("timestamptz", typeof(DateTime))]
    [InlineData("jsonb", typeof(string))]
    [InlineData("uuid", typeof(Guid))]
    public void Resolves_clr_types(string typeName, Type expected)
    {
        Assert.Equal(expected, RdsDataTypeMapper.ResolveClrType(new ColumnMetadata { TypeName = typeName }));
    }

    [Fact]
    public void Int4_long_value_becomes_int()
    {
        var v = RdsDataTypeMapper.FromField(new Field { LongValue = 42 }, typeof(int), "int4");
        Assert.IsType<int>(v);
        Assert.Equal(42, v);
    }

    [Fact]
    public void Numeric_string_becomes_decimal()
    {
        var v = RdsDataTypeMapper.FromField(new Field { StringValue = "12345678901234.5678" }, typeof(decimal), "numeric");
        Assert.Equal(12345678901234.5678m, v);
    }

    [Fact]
    public void Timestamptz_is_utc()
    {
        var v = (DateTime)RdsDataTypeMapper.FromField(new Field { StringValue = "2026-10-07 00:30:00" }, typeof(DateTime), "timestamptz");
        Assert.Equal(DateTimeKind.Utc, v.Kind);
        Assert.Equal(new DateTime(2026, 10, 7, 0, 30, 0), v);
    }

    [Fact]
    public void Null_field_becomes_DBNull()
    {
        Assert.Same(DBNull.Value, RdsDataTypeMapper.FromField(new Field { IsNull = true }, typeof(int), "int4"));
    }
}
