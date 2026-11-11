using System.Buffers.Binary;
using System.Text;

namespace TurnRoi.Core.Storage;

/// <summary>
/// A minimal, read-only SQLite reader: enough to scan rowid tables and search a text index, including pages that are
/// still in the write-ahead log. Agents such as Cursor and OpenCode keep their sessions in SQLite, and reading it
/// directly keeps TurnROI free of native dependencies.
///
/// Supported: UTF-8 databases, rowid tables (with an INTEGER PRIMARY KEY alias), index b-trees with BINARY collation,
/// overflow pages, and committed WAL frames (salt- and checksum-verified). Not supported: WITHOUT ROWID tables and
/// UTF-16 databases; those throw <see cref="NotSupportedException"/>.
/// The file is opened with full sharing, so it can be read while the agent is running.
/// </summary>
public sealed class SqliteReader : IDisposable
{
    private readonly FileStream _db;
    private readonly FileStream? _wal;
    private readonly Dictionary<uint, long> _walPages = new();
    private readonly Dictionary<uint, byte[]> _cache = new();
    private readonly int _pageSize;
    private readonly int _usable;
    private List<SchemaEntry>? _schema;

    public sealed record SchemaEntry(string Type, string Name, string TableName, uint RootPage, string? Sql);

    private SqliteReader(FileStream db, FileStream? wal, int pageSize, int usable)
    {
        _db = db;
        _wal = wal;
        _pageSize = pageSize;
        _usable = usable;
    }

    public static SqliteReader Open(string path)
    {
        var db = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            var header = new byte[100];
            if (db.Read(header, 0, 100) != 100 || Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
                throw new InvalidDataException("not a SQLite database");
            int pageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16));
            if (pageSize == 1) pageSize = 65536;
            var reserved = header[20];
            var encoding = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(56));
            if (encoding is not (0 or 1)) throw new NotSupportedException("only UTF-8 SQLite databases are supported");

            FileStream? wal = null;
            var walPath = path + "-wal";
            if (File.Exists(walPath))
            {
                try { wal = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                catch (IOException) { wal = null; }
            }

            var reader = new SqliteReader(db, wal, pageSize, pageSize - reserved);
            if (wal is not null) reader.LoadWal();
            return reader;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        _wal?.Dispose();
    }

    // ---- WAL ----

    private void LoadWal()
    {
        var wal = _wal!;
        if (wal.Length < 32) return;
        var header = new byte[32];
        wal.Position = 0;
        if (wal.Read(header, 0, 32) != 32) return;
        var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (magic is not (0x377f0682 or 0x377f0683)) return;
        var bigEndianChecksum = (magic & 1) == 1;
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)) != _pageSize) return;
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));

        var (s0, s1) = Checksum(header.AsSpan(0, 24), 0, 0, bigEndianChecksum);
        if (s0 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24)) || s1 != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28)))
            return;

        var frame = new byte[24 + _pageSize];
        var pending = new Dictionary<uint, long>();
        long offset = 32;
        while (offset + frame.Length <= wal.Length)
        {
            wal.Position = offset;
            if (wal.Read(frame, 0, frame.Length) != frame.Length) break;
            var page = BinaryPrimitives.ReadUInt32BigEndian(frame);
            var commitSize = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4));
            if (BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(8)) != salt1 || BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(12)) != salt2) break;
            (s0, s1) = Checksum(frame.AsSpan(0, 8), s0, s1, bigEndianChecksum);
            (s0, s1) = Checksum(frame.AsSpan(24, _pageSize), s0, s1, bigEndianChecksum);
            if (s0 != BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(16)) || s1 != BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(20))) break;

            pending[page] = offset + 24;
            if (commitSize != 0)
            {
                // Only committed transactions are visible.
                foreach (var (p, o) in pending) _walPages[p] = o;
                pending.Clear();
            }
            offset += frame.Length;
        }
    }

    private static (uint, uint) Checksum(ReadOnlySpan<byte> data, uint s0, uint s1, bool bigEndian)
    {
        for (var i = 0; i + 8 <= data.Length; i += 8)
        {
            var x0 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[i..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            var x1 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
            s0 += x0 + s1;
            s1 += x1 + s0;
        }
        return (s0, s1);
    }

    // ---- pages ----

    private byte[] Page(uint number)
    {
        if (number == 0) throw new InvalidDataException("page 0 does not exist");
        if (_cache.TryGetValue(number, out var cached)) return cached;
        var buffer = new byte[_pageSize];
        var (stream, pos) = _walPages.TryGetValue(number, out var walPos) ? (_wal!, walPos) : (_db, (long)(number - 1) * _pageSize);
        stream.Position = pos;
        var read = 0;
        while (read < _pageSize)
        {
            var n = stream.Read(buffer, read, _pageSize - read);
            if (n == 0) throw new InvalidDataException($"page {number} is beyond the end of the file");
            read += n;
        }
        if (_cache.Count > 4096) _cache.Clear();
        _cache[number] = buffer;
        return buffer;
    }

    private static int HeaderOffset(uint page) => page == 1 ? 100 : 0;

    private static (long Value, int Length) Varint(ReadOnlySpan<byte> b)
    {
        long v = 0;
        for (var i = 0; i < 8; i++)
        {
            v = (v << 7) | (uint)(b[i] & 0x7f);
            if ((b[i] & 0x80) == 0) return (v, i + 1);
        }
        return ((v << 8) | b[8], 9);
    }

    /// <summary>Reads a cell's full payload, following overflow pages.</summary>
    private byte[] Payload(byte[] page, int start, long size, bool isTable)
    {
        var u = _usable;
        var x = isTable ? u - 35 : (u - 12) * 64 / 255 - 23;
        var m = (u - 12) * 32 / 255 - 23;
        int local;
        if (size <= x) local = (int)size;
        else
        {
            var k = (int)(m + (size - m) % (u - 4));
            local = k <= x ? k : m;
        }

        var result = new byte[size];
        Array.Copy(page, start, result, 0, local);
        if (local == size) return result;

        var next = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(start + local));
        var copied = local;
        var guard = 0;
        while (copied < size && next != 0 && guard++ < 1_000_000)
        {
            var overflow = Page(next);
            next = BinaryPrimitives.ReadUInt32BigEndian(overflow);
            var chunk = (int)Math.Min(u - 4, size - copied);
            Array.Copy(overflow, 4, result, copied, chunk);
            copied += chunk;
        }
        if (copied < size) throw new InvalidDataException("overflow chain ended early");
        return result;
    }

    // ---- records ----

    /// <summary>Decodes a record into column values: null, long, double, string or byte[].</summary>
    public static object?[] Decode(byte[] record)
    {
        var (headerSize, n) = Varint(record);
        var types = new List<long>();
        var p = n;
        while (p < headerSize)
        {
            var (t, len) = Varint(record.AsSpan(p));
            types.Add(t);
            p += len;
        }

        var values = new object?[types.Count];
        var body = (int)headerSize;
        for (var i = 0; i < types.Count; i++)
        {
            var t = types[i];
            switch (t)
            {
                case 0: values[i] = null; break;
                case 1: values[i] = (long)(sbyte)record[body]; body += 1; break;
                case 2: values[i] = (long)BinaryPrimitives.ReadInt16BigEndian(record.AsSpan(body)); body += 2; break;
                case 3: values[i] = (long)((((sbyte)record[body]) << 16) | (record[body + 1] << 8) | record[body + 2]); body += 3; break;
                case 4: values[i] = (long)BinaryPrimitives.ReadInt32BigEndian(record.AsSpan(body)); body += 4; break;
                case 5:
                {
                    long v = (sbyte)record[body];
                    for (var j = 1; j < 6; j++) v = (v << 8) | record[body + j];
                    values[i] = v; body += 6; break;
                }
                case 6: values[i] = BinaryPrimitives.ReadInt64BigEndian(record.AsSpan(body)); body += 8; break;
                case 7: values[i] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(record.AsSpan(body))); body += 8; break;
                case 8: values[i] = 0L; break;
                case 9: values[i] = 1L; break;
                default:
                    if (t >= 12 && t % 2 == 0)
                    {
                        var len = (int)((t - 12) / 2);
                        values[i] = record.AsSpan(body, len).ToArray(); body += len;
                    }
                    else if (t >= 13)
                    {
                        var len = (int)((t - 13) / 2);
                        values[i] = Encoding.UTF8.GetString(record, body, len); body += len;
                    }
                    break;
            }
        }
        return values;
    }

    // ---- schema ----

    public IReadOnlyList<SchemaEntry> Schema()
    {
        if (_schema is not null) return _schema;
        var list = new List<SchemaEntry>();
        foreach (var (_, record) in ScanTablePage(1))
        {
            var v = Decode(record);
            if (v.Length < 5) continue;
            list.Add(new SchemaEntry(v[0] as string ?? "", v[1] as string ?? "", v[2] as string ?? "",
                v[3] is long root ? (uint)root : 0, v[4] as string));
        }
        return _schema = list;
    }

    public bool HasTable(string name) => Schema().Any(s => s.Type == "table" && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Column names of a table, from its CREATE TABLE statement, and the INTEGER PRIMARY KEY column if any.</summary>
    public (IReadOnlyList<string> Columns, int RowidAlias) Columns(string table)
    {
        var entry = Schema().FirstOrDefault(s => s.Type == "table" && s.Name.Equals(table, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"no table '{table}'");
        if (entry.Sql is null) return ([], -1);
        if (entry.Sql.Contains("WITHOUT ROWID", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"table '{table}' is WITHOUT ROWID");

        var open = entry.Sql.IndexOf('(');
        var close = entry.Sql.LastIndexOf(')');
        var body = entry.Sql[(open + 1)..close];
        var parts = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        foreach (var c in body)
        {
            if (c == '(') depth++;
            if (c == ')') depth--;
            if (c == ',' && depth == 0) { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        parts.Add(current.ToString());

        var columns = new List<string>();
        var alias = -1;
        foreach (var raw in parts)
        {
            var def = raw.Trim();
            if (def.Length == 0) continue;
            var firstWord = def.Split((char[])[' ', '\t', '\n', '\r'], 2)[0].Trim('"', '`', '[', ']');
            if (firstWord.ToUpperInvariant() is "PRIMARY" or "UNIQUE" or "CHECK" or "FOREIGN" or "CONSTRAINT") continue;
            var rest = def.Length > firstWord.Length ? def[(def.IndexOf(' ') + 1)..].ToUpperInvariant() : "";
            if (rest.TrimStart().StartsWith("INTEGER", StringComparison.Ordinal) && rest.Contains("PRIMARY KEY", StringComparison.Ordinal))
                alias = columns.Count;
            columns.Add(firstWord);
        }
        return (columns, alias);
    }

    // ---- table scan ----

    /// <summary>Every row of a table as (rowid, column name → value).</summary>
    public IEnumerable<(long RowId, Dictionary<string, object?> Row)> Rows(string table)
    {
        var (columns, alias) = Columns(table);
        var root = Schema().First(s => s.Type == "table" && s.Name.Equals(table, StringComparison.OrdinalIgnoreCase)).RootPage;
        foreach (var (rowid, record) in ScanTablePage(root))
            yield return (rowid, ToRow(columns, alias, rowid, Decode(record)));
    }

    private static Dictionary<string, object?> ToRow(IReadOnlyList<string> columns, int alias, long rowid, object?[] values)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
            row[columns[i]] = i == alias ? rowid : i < values.Length ? values[i] : null;
        return row;
    }

    private IEnumerable<(long RowId, byte[] Record)> ScanTablePage(uint pageNumber, int depth = 0)
    {
        if (depth > 64) throw new InvalidDataException("b-tree too deep");
        var page = Page(pageNumber);
        var h = HeaderOffset(pageNumber);
        var type = page[h];
        var cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3));

        if (type == 0x0D)
        {
            for (var i = 0; i < cells; i++)
            {
                int ptr = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 8 + i * 2));
                var (size, a) = Varint(page.AsSpan(ptr));
                var (rowid, b) = Varint(page.AsSpan(ptr + a));
                yield return (rowid, Payload(page, ptr + a + b, size, isTable: true));
            }
        }
        else if (type == 0x05)
        {
            var children = new List<uint>(cells + 1);
            for (var i = 0; i < cells; i++)
            {
                int ptr = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 12 + i * 2));
                children.Add(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(ptr)));
            }
            children.Add(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(h + 8)));
            foreach (var child in children)
            foreach (var row in ScanTablePage(child, depth + 1))
                yield return row;
        }
        else
        {
            throw new InvalidDataException($"page {pageNumber} is not a table b-tree page (type {type})");
        }
    }

    /// <summary>Looks up one row by rowid.</summary>
    public Dictionary<string, object?>? RowById(string table, long rowid)
    {
        var (columns, alias) = Columns(table);
        var pageNumber = Schema().First(s => s.Type == "table" && s.Name.Equals(table, StringComparison.OrdinalIgnoreCase)).RootPage;
        for (var depth = 0; depth < 64; depth++)
        {
            var page = Page(pageNumber);
            var h = HeaderOffset(pageNumber);
            var type = page[h];
            var cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3));
            if (type == 0x0D)
            {
                for (var i = 0; i < cells; i++)
                {
                    int ptr = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 8 + i * 2));
                    var (size, a) = Varint(page.AsSpan(ptr));
                    var (id, b) = Varint(page.AsSpan(ptr + a));
                    if (id == rowid) return ToRow(columns, alias, id, Decode(Payload(page, ptr + a + b, size, isTable: true)));
                }
                return null;
            }
            if (type != 0x05) return null;

            var next = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(h + 8)); // right-most
            for (var i = 0; i < cells; i++)
            {
                int ptr = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 12 + i * 2));
                var (key, _) = Varint(page.AsSpan(ptr + 4));
                if (rowid <= key) { next = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(ptr)); break; }
            }
            pageNumber = next;
        }
        return null;
    }

    // ---- index range scan ----

    /// <summary>
    /// Rows of <paramref name="table"/> whose text column, as stored in an index on that column, starts with
    /// <paramref name="prefix"/>, in key order. Falls back to a full scan when no such index exists.
    /// </summary>
    public IEnumerable<Dictionary<string, object?>> RowsWithPrefix(string table, string column, string prefix)
    {
        var index = FindIndex(table, column);
        if (index is null)
        {
            foreach (var (_, row) in Rows(table))
                if (row.GetValueOrDefault(column) is string s && s.StartsWith(prefix, StringComparison.Ordinal)) yield return row;
            yield break;
        }

        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        foreach (var rowid in IndexPrefix(index.RootPage, prefixBytes, 0))
            if (RowById(table, rowid) is { } row) yield return row;
    }

    private SchemaEntry? FindIndex(string table, string column)
    {
        foreach (var e in Schema().Where(s => s.Type == "index" && s.TableName.Equals(table, StringComparison.OrdinalIgnoreCase)))
        {
            if (e.Sql is null)
            {
                // sqlite_autoindex_<table>_<n>: the n-th UNIQUE / non-integer PRIMARY KEY column in declaration order.
                if (AutoIndexOrdinal(table, column) is { } n && e.Name.Equals($"sqlite_autoindex_{table}_{n}", StringComparison.OrdinalIgnoreCase))
                    return e;
                continue;
            }
            var open = e.Sql.IndexOf('(');
            var first = e.Sql[(open + 1)..].Split(',', ')')[0].Trim().Trim('"', '`', '[', ']');
            if (first.Equals(column, StringComparison.OrdinalIgnoreCase) && !e.Sql.Contains("COLLATE", StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    private int? AutoIndexOrdinal(string table, string column)
    {
        var sql = Schema().First(s => s.Type == "table" && s.Name.Equals(table, StringComparison.OrdinalIgnoreCase)).Sql ?? "";
        var body = sql[(sql.IndexOf('(') + 1)..sql.LastIndexOf(')')];
        var n = 0;
        foreach (var def in body.Split(','))
        {
            var d = def.Trim();
            var name = d.Split(' ', 2)[0].Trim('"', '`', '[', ']');
            var upper = d.ToUpperInvariant();
            var unique = upper.Contains("UNIQUE", StringComparison.Ordinal) ||
                         (upper.Contains("PRIMARY KEY", StringComparison.Ordinal) && !upper.Contains(" INTEGER ", StringComparison.Ordinal) &&
                          !upper.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault()?.StartsWith("INTEGER", StringComparison.Ordinal) == true);
            if (!unique) continue;
            n++;
            if (name.Equals(column, StringComparison.OrdinalIgnoreCase)) return n;
        }
        return null;
    }

    private IEnumerable<long> IndexPrefix(uint pageNumber, byte[] prefix, int depth)
    {
        if (depth > 64) yield break;
        var page = Page(pageNumber);
        var h = HeaderOffset(pageNumber);
        var type = page[h];
        var cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3));

        if (type == 0x0A)
        {
            for (var i = 0; i < cells; i++)
            {
                int ptr = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 8 + i * 2));
                var (size, a) = Varint(page.AsSpan(ptr));
                var values = Decode(Payload(page, ptr + a, size, isTable: false));
                var key = KeyBytes(values);
                if (key is null) continue;
                var cmp = ComparePrefix(key, prefix);
                if (cmp < 0) continue;
                if (cmp > 0) yield break;
                if (values[^1] is long rowid) yield return rowid;
            }
        }
        else if (type == 0x02)
        {
            for (var i = 0; i < cells; i++)
            {
                int ptr = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 12 + i * 2));
                var child = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(ptr));
                var (size, a) = Varint(page.AsSpan(ptr + 4));
                var values = Decode(Payload(page, ptr + 4 + a, size, isTable: false));
                var key = KeyBytes(values);
                var cmp = key is null ? -1 : ComparePrefix(key, prefix);
                // Everything left of this cell's key is smaller than it: only descend if the key isn't below the prefix.
                if (cmp >= 0)
                    foreach (var r in IndexPrefix(child, prefix, depth + 1)) yield return r;
                if (cmp > 0) yield break;
                if (cmp == 0 && values[^1] is long rowid) yield return rowid; // interior cells hold entries too
            }
            var right = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(h + 8));
            foreach (var r in IndexPrefix(right, prefix, depth + 1)) yield return r;
        }
    }

    private static byte[]? KeyBytes(object?[] values) => values.Length > 0 && values[0] is string s ? Encoding.UTF8.GetBytes(s) : null;

    /// <summary>-1 if key sorts before every key with the prefix, 0 if it has the prefix, 1 if it sorts after.</summary>
    private static int ComparePrefix(byte[] key, byte[] prefix)
    {
        var n = Math.Min(key.Length, prefix.Length);
        for (var i = 0; i < n; i++)
        {
            if (key[i] != prefix[i]) return key[i] < prefix[i] ? -1 : 1;
        }
        return key.Length >= prefix.Length ? 0 : -1;
    }
}
