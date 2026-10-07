using RdsData.AdoNet;
using Xunit;

public class PostgresParameterRewriterTests
{
    private static (string Sql, HashSet<string> Used) Rewrite(string sql, params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return (PostgresParameterRewriter.Rewrite(sql, set.Contains, used), used);
    }

    [Fact]
    public void Rewrites_at_parameters()
    {
        var (sql, used) = Rewrite("SELECT * FROM t WHERE id = @Id AND name = @name", "Id", "name");
        Assert.Equal("SELECT * FROM t WHERE id = :Id AND name = :name", sql);
        Assert.Equal(2, used.Count);
    }

    [Theory]
    [InlineData("SELECT '@Id' WHERE x = @Id", "SELECT '@Id' WHERE x = :Id")]
    [InlineData("SELECT 'it''s @Id' , @Id", "SELECT 'it''s @Id' , :Id")]
    [InlineData(@"SELECT E'\'@Id' , @Id", @"SELECT E'\'@Id' , :Id")]
    [InlineData("SELECT \"@Id\" FROM t WHERE a = @Id", "SELECT \"@Id\" FROM t WHERE a = :Id")]
    [InlineData("SELECT 1 -- @Id\n, @Id", "SELECT 1 -- @Id\n, :Id")]
    [InlineData("SELECT /* /* @Id */ @Id */ @Id", "SELECT /* /* @Id */ @Id */ :Id")]
    [InlineData("SELECT $$ @Id $$, $fn$ @Id $fn$, @Id", "SELECT $$ @Id $$, $fn$ @Id $fn$, :Id")]
    public void Skips_literals_identifiers_and_comments(string input, string expected)
    {
        Assert.Equal(expected, Rewrite(input, "Id").Sql);
    }

    [Fact]
    public void Keeps_casts_and_operators()
    {
        var (sql, _) = Rewrite("SELECT @Id::int, tags @> @Tags, doc @@ to_tsquery(@Q)", "Id", "Tags", "Q");
        Assert.Equal("SELECT :Id::int, tags @> :Tags, doc @@ to_tsquery(:Q)", sql);
    }

    [Fact]
    public void Leaves_unknown_names_untouched()
    {
        var (sql, used) = Rewrite("SELECT @Unknown, @Id", "Id");
        Assert.Equal("SELECT @Unknown, :Id", sql);
        Assert.Single(used);
    }

    [Fact]
    public void Detects_existing_colon_parameters()
    {
        var (sql, used) = Rewrite("SELECT :Id", "Id");
        Assert.Equal("SELECT :Id", sql);
        Assert.Contains("Id", used);
    }

    [Fact]
    public void Does_not_touch_array_slices()
    {
        var (sql, used) = Rewrite("SELECT arr[1:n] FROM t", "n");
        Assert.Equal("SELECT arr[1:n] FROM t", sql);
        Assert.Empty(used);
    }
}
