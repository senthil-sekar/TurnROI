using System.Text;
using TurnRoi.Core.Storage;

namespace TurnRoi.Core.Tests;

public class SqliteReaderTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Reads_the_schema_and_columns()
    {
        using var db = SqliteReader.Open(Fixture("sqlite-basic.db"));

        Assert.True(db.HasTable("kv"));
        Assert.True(db.HasTable("items"));
        var (columns, alias) = db.Columns("items");
        Assert.Equal(new[] { "id", "name", "score", "n", "blob" }, columns.ToArray());
        Assert.Equal(0, alias);
    }

    [Fact]
    public void Scans_every_row_of_a_multi_level_table()
    {
        using var db = SqliteReader.Open(Fixture("sqlite-basic.db"));

        var rows = db.Rows("kv").ToList();
        Assert.Equal(1505, rows.Count);
        Assert.Equal(1505, rows.Select(r => (string)r.Row["key"]!).Distinct().Count());
    }

    [Fact]
    public void Decodes_integers_reals_text_and_blobs()
    {
        using var db = SqliteReader.Open(Fixture("sqlite-basic.db"));

        var rows = db.Rows("items").Select(r => r.Row).ToList();
        Assert.Equal(400, rows.Count);
        Assert.Equal(1L, rows[0]["id"]);
        Assert.Equal("0", rows[0]["name"]);
        Assert.Equal("é名é名2", rows[2]["name"]);
        // SQLite stores integral REAL values as integers on disk.
        Assert.Equal(1.0, Convert.ToDouble(rows[3]["score"]), 6);
        Assert.Equal(1.0 / 3, Convert.ToDouble(rows[1]["score"]), 6);
        long[] expected = [0, 1, -1, 127, -128, 32767, -40000, 8388607, -8388608, 2147483647, -2147483648, 140737488355327, -9007199254740993];
        for (var i = 0; i < 13; i++) Assert.Equal(expected[i], rows[i]["n"]);
        Assert.Equal(new byte[] { 4, 4, 4, 4 }, (byte[])rows[4]["blob"]!);
    }

    [Fact]
    public void Follows_overflow_pages_for_large_values()
    {
        using var db = SqliteReader.Open(Fixture("sqlite-basic.db"));

        var big = db.RowsWithPrefix("kv", "key", "composerData:c4").Single();
        var text = Encoding.UTF8.GetString((byte[])big["value"]!);
        Assert.Equal(25_010, text.Length);
        Assert.EndsWith("x\"}", text);
    }

    [Fact]
    public void Prefix_search_uses_the_index_and_returns_rows_in_key_order()
    {
        using var db = SqliteReader.Open(Fixture("sqlite-basic.db"));

        var keys = db.RowsWithPrefix("kv", "key", "bubbleId:c3:").Select(r => (string)r["key"]!).ToList();
        var expected = Enumerable.Range(0, 1500).Where(i => i % 7 == 3).Select(i => $"bubbleId:c3:{i:D5}").ToList();
        Assert.Equal(expected.ToArray(), keys.ToArray());
        Assert.Empty(db.RowsWithPrefix("kv", "key", "nothing:"));
    }

    [Fact]
    public void Row_lookup_by_rowid()
    {
        using var db = SqliteReader.Open(Fixture("sqlite-basic.db"));

        Assert.Equal("399", db.RowById("items", 400)!["name"]);
        Assert.Null(db.RowById("items", 401));
    }

    [Fact]
    public void Sees_committed_rows_still_in_the_write_ahead_log()
    {
        using var db = SqliteReader.Open(Fixture("sqlite-wal.db"));

        var rows = db.Rows("kv").Select(r => r.Row).ToList();
        Assert.Equal(201, rows.Count);
        Assert.Equal("2", rows.Single(r => (string)r["key"]! == "a")["value"] switch { byte[] b => Encoding.UTF8.GetString(b), var v => v?.ToString() });
        Assert.Equal(200, db.RowsWithPrefix("kv", "key", "w").Count());
    }

    [Fact]
    public void Rejects_files_that_are_not_sqlite()
    {
        Assert.Throws<InvalidDataException>(() => SqliteReader.Open(Fixture("cursor-usage.csv")));
    }
}
