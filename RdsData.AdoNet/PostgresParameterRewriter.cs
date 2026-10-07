using System.Text;

namespace RdsData.AdoNet;

/// <summary>
/// Dapper 等が生成する @name 形式を Data API の :name 形式に書き換える。
/// - 書き換えるのはパラメータコレクションに存在する名前のみ (@&gt; や @@ 等の演算子を壊さないため)
/// - 文字列リテラル ('...', E'...')、引用符付き識別子 ("...")、ドル引用 ($tag$...$tag$)、
///   コメント (--, ネスト可能な /* */) の内部は書き換えない
/// - :: (キャスト) はそのまま
/// - すでに :name で書かれているパラメータも「使用済み」として検出する
/// </summary>
internal static class PostgresParameterRewriter
{
    public static string Rewrite(string sql, Func<string, bool> isParameter, ISet<string> usedNames)
    {
        var sb = new StringBuilder(sql.Length + 8);
        var i = 0;
        var n = sql.Length;

        while (i < n)
        {
            var c = sql[i];
            var prev = i > 0 ? sql[i - 1] : '\0';

            switch (c)
            {
                case '\'':
                {
                    // E'...' / e'...' はバックスラッシュエスケープを持つ
                    var escapeString = (prev is 'E' or 'e') && (i < 2 || !IsIdentChar(sql[i - 2]));
                    i = CopyQuoted(sql, i, '\'', escapeString, sb);
                    continue;
                }
                case '"':
                    i = CopyQuoted(sql, i, '"', backslashEscapes: false, sb);
                    continue;
                case '-' when i + 1 < n && sql[i + 1] == '-':
                {
                    var end = sql.IndexOf('\n', i);
                    end = end < 0 ? n : end + 1;
                    sb.Append(sql, i, end - i);
                    i = end;
                    continue;
                }
                case '/' when i + 1 < n && sql[i + 1] == '*':
                    i = CopyBlockComment(sql, i, sb);
                    continue;
                case '$' when !IsIdentChar(prev) && TryReadDollarTag(sql, i, out var tag):
                {
                    var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                    var end = close < 0 ? n : close + tag.Length;
                    sb.Append(sql, i, end - i);
                    i = end;
                    continue;
                }
                case ':' when i + 1 < n && sql[i + 1] == ':':
                    sb.Append("::");
                    i += 2;
                    continue;
                case '@' or ':' when i + 1 < n && IsIdentStart(sql[i + 1]) && !IsIdentChar(prev) && prev != '@' && prev != ':':
                {
                    var start = i + 1;
                    var end = start;
                    while (end < n && IsIdentChar(sql[end])) end++;
                    var name = sql.Substring(start, end - start);

                    if (isParameter(name))
                    {
                        sb.Append(':').Append(name);
                        usedNames.Add(name);
                    }
                    else
                    {
                        sb.Append(sql, i, end - i);
                    }
                    i = end;
                    continue;
                }
                default:
                    sb.Append(c);
                    i++;
                    continue;
            }
        }

        return sb.ToString();
    }

    private static int CopyQuoted(string sql, int start, char quote, bool backslashEscapes, StringBuilder sb)
    {
        var n = sql.Length;
        sb.Append(quote);
        var i = start + 1;
        while (i < n)
        {
            var c = sql[i];
            if (backslashEscapes && c == '\\' && i + 1 < n)
            {
                sb.Append(c).Append(sql[i + 1]);
                i += 2;
                continue;
            }
            sb.Append(c);
            i++;
            if (c != quote) continue;
            if (i < n && sql[i] == quote)
            {
                sb.Append(quote); // '' / "" はエスケープ
                i++;
                continue;
            }
            return i;
        }
        return n; // 未終端はそのまま (エラーはサーバー側に任せる)
    }

    private static int CopyBlockComment(string sql, int start, StringBuilder sb)
    {
        var n = sql.Length;
        var depth = 0;
        var i = start;
        while (i < n)
        {
            if (i + 1 < n && sql[i] == '/' && sql[i + 1] == '*') { depth++; sb.Append("/*"); i += 2; continue; }
            if (i + 1 < n && sql[i] == '*' && sql[i + 1] == '/')
            {
                depth--;
                sb.Append("*/");
                i += 2;
                if (depth == 0) return i;
                continue;
            }
            sb.Append(sql[i]);
            i++;
        }
        return n;
    }

    /// <summary>$$ または $tag$ を読む。$1 のような位置パラメータはタグではない。</summary>
    private static bool TryReadDollarTag(string sql, int start, out string tag)
    {
        tag = string.Empty;
        var i = start + 1;
        if (i < sql.Length && sql[i] == '$') { tag = "$$"; return true; }
        if (i >= sql.Length || !IsIdentStart(sql[i])) return false;
        while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
        if (i >= sql.Length || sql[i] != '$') return false;
        tag = sql.Substring(start, i - start + 1);
        return true;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
