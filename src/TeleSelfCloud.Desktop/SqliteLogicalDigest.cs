using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TeleSelfCloud.Desktop;

internal static class SqliteLogicalDigest
{
    public static async Task<string> ComputeAsync(SqliteConnection connection, CancellationToken token)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if ((await integrity.ExecuteScalarAsync(token))?.ToString() != "ok") throw new InvalidDataException("The database failed integrity verification.");
        }
        var tables = new List<string>();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema ORDER BY type COLLATE BINARY,name COLLATE BINARY;";
            await using var reader = await schema.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                for (var i = 0; i < 4; i++) Add(aggregate, reader.IsDBNull(i) ? null : reader.GetString(i));
                if (reader.GetString(0) == "table") tables.Add(reader.GetString(1));
            }
        }
        foreach (var table in tables)
        {
            Add(aggregate, table); var rows = new List<string>();
            using var select = connection.CreateCommand(); select.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
            await using var reader = await select.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                if (rows.Count >= 1_000_000) throw new InvalidDataException("The database exceeds the migration verification row limit.");
                using var row = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    var value = reader.GetValue(column);
                    switch (value)
                    {
                        case DBNull: Add(row, "null"); break;
                        case long integer: Add(row, "integer"); row.AppendData(BitConverter.GetBytes(integer)); break;
                        case double real: Add(row, "real"); row.AppendData(BitConverter.GetBytes(real)); break;
                        case string text: Add(row, "text"); Add(row, text); break;
                        case byte[] blob:
                            if (blob.Length > 32 * 1024 * 1024) throw new InvalidDataException("A database field exceeds the migration verification size limit.");
                            Add(row, "blob"); row.AppendData(BitConverter.GetBytes(blob.Length)); row.AppendData(blob); break;
                        default: throw new InvalidDataException("The database contains an unsupported value type.");
                    }
                }
                rows.Add(Convert.ToHexString(row.GetHashAndReset()));
            }
            rows.Sort(StringComparer.Ordinal); aggregate.AppendData(BitConverter.GetBytes(rows.Count));
            foreach (var row in rows) aggregate.AppendData(Convert.FromHexString(row));
        }
        return Convert.ToHexString(aggregate.GetHashAndReset());
    }
    private static void Add(IncrementalHash hash, string? text)
    {
        if (text is null) { hash.AppendData(BitConverter.GetBytes(-1)); return; }
        if (text.Length > 16 * 1024 * 1024) throw new InvalidDataException("A database field exceeds the migration verification size limit.");
        var bytes = Encoding.UTF8.GetBytes(text); hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
    }
}

internal sealed class DigestSinkStream : Stream
{
    private IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long length;
    public string Finish() => Convert.ToHexString(hash.GetHashAndReset());
    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position { get => length; set { if (value != length && value != 0) throw new NotSupportedException(); if (value == 0) Reset(); } }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) { hash.AppendData(buffer); length += buffer.Length; }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
    public override void SetLength(long value) { if (value != 0 && value != length) throw new NotSupportedException(); if (value == 0) Reset(); }
    private void Reset() { hash.Dispose(); hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); length = 0; }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) { if (origin == SeekOrigin.Begin) { Position = offset; return Position; } throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) hash.Dispose(); base.Dispose(disposing); }
}
