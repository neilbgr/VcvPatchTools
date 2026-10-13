using System.Text;

namespace VcvPatchTools.Core.Patch;

/// <summary>
/// Minimal tar reader and writer. System.Formats.Tar throws PlatformNotSupportedException in the browser (Blazor WASM),
/// and a .vcv only holds a few regular files, so this handles just what Rack/Cardinal write:
/// ustar headers (name + prefix), pax extended headers ('x', "path=") and GNU long names ('L').
/// </summary>
public static class TarEntries
{
    private const int blockSize = 512;

    public static IEnumerable<(string Name, byte[] Data)> Read(byte[] tar)
    {
        int offset = 0;
        string? longName = null;
        while (offset + blockSize <= tar.Length)
        {
            ReadOnlySpan<byte> header = tar.AsSpan(offset, blockSize);
            if (header.IndexOfAnyExcept((byte)0) < 0)
            {
                yield break; // End-of-archive marker (zero block).
            }

            string name = Text(header.Slice(0, 100));
            long size = Octal(header.Slice(124, 12));
            char type = (char)header[156];
            string prefix = Text(header.Slice(345, 155));
            if (prefix.Length > 0 && Encoding.ASCII.GetString(header.Slice(257, 5)) == "ustar")
            {
                name = $"{prefix}/{name}";
            }

            int dataStart = offset + blockSize;
            if (size < 0 || dataStart + size > tar.Length)
            {
                throw new InvalidDataException("Truncated or corrupted tar archive.");
            }
            byte[] data = tar.AsSpan(dataStart, (int)size).ToArray();
            offset = dataStart + (int)(((size + blockSize - 1) / blockSize) * blockSize);

            switch (type)
            {
                case 'x':
                    longName = PaxPath(data) ?? longName;
                    continue;
                case 'L':
                    longName = Text(data);
                    continue;
                case 'g':
                    continue;
                case '0' or '\0' or '7':
                    yield return (longName ?? name, data);
                    break;
            }
            longName = null;
        }
    }

    /// <summary>
    /// Regular files as a ustar archive; a name too long for the header's 100 bytes gets a pax "path" record before it.
    /// </summary>
    public static byte[] Write(IEnumerable<(string Name, byte[] Data)> entries, DateTimeOffset modified)
    {
        using MemoryStream tar = new MemoryStream();
        foreach ((string name, byte[] data) in entries)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > 100)
            {
                byte[] pax = PaxRecord("path", name);
                WriteEntry(tar, "PaxHeaders/" + Truncate(name, 80), 'x', pax, modified);
            }
            WriteEntry(tar, Truncate(name, 100), '0', data, modified);
        }
        // End of archive: two zero blocks.
        tar.Write(new byte[blockSize * 2]);
        return tar.ToArray();
    }

    private static void WriteEntry(Stream tar, string name, char type, byte[] data, DateTimeOffset modified)
    {
        byte[] header = new byte[blockSize];
        Field(header, 0, 100, Encoding.UTF8.GetBytes(name));
        OctalField(header, 100, 8, 0b110_100_100); // 0644
        OctalField(header, 108, 8, 0);
        OctalField(header, 116, 8, 0);
        OctalField(header, 124, 12, data.Length);
        OctalField(header, 136, 12, modified.ToUnixTimeSeconds());
        header[156] = (byte)type;
        Field(header, 257, 6, Encoding.ASCII.GetBytes("ustar\0"));
        Field(header, 263, 2, Encoding.ASCII.GetBytes("00"));
        // Checksum: the sum of the header's bytes, with its own field counted as spaces.
        Field(header, 148, 8, Encoding.ASCII.GetBytes("        "));
        int sum = header.Sum(b => b);
        Field(header, 148, 8, Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 "));
        tar.Write(header);
        tar.Write(data);
        tar.Write(new byte[(blockSize - (data.Length % blockSize)) % blockSize]);
    }

    /// <summary>"LENGTH key=value\n", where LENGTH counts the whole record, its own digits included.</summary>
    private static byte[] PaxRecord(string key, string value)
    {
        int body = Encoding.UTF8.GetByteCount($" {key}={value}\n");
        int length = body + 1;
        while (body + Digits(length) != length)
        {
            length = body + Digits(length);
        }
        return Encoding.UTF8.GetBytes($"{length} {key}={value}\n");
    }

    private static int Digits(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

    /// <summary>Keeps the end of the name (the file name matters most), never splitting a surrogate pair.</summary>
    private static string Truncate(string name, int maxBytes)
    {
        while (Encoding.UTF8.GetByteCount(name) > maxBytes)
        {
            name = name[(char.IsSurrogatePair(name, 0) ? 2 : 1)..];
        }
        return name;
    }

    private static void Field(byte[] header, int offset, int length, byte[] value) =>
        value.AsSpan(0, Math.Min(length, value.Length)).CopyTo(header.AsSpan(offset, length));

    /// <summary>Zero-padded octal digits followed by a NUL, filling the field.</summary>
    private static void OctalField(byte[] header, int offset, int length, long value) =>
        Field(header, offset, length, Encoding.ASCII.GetBytes(Convert.ToString(value, 8).PadLeft(length - 1, '0') + "\0"));

    /// <summary>Pax records are "LENGTH key=value\n"; only "path" matters here.</summary>
    private static string? PaxPath(byte[] data)
    {
        foreach (string record in Encoding.UTF8.GetString(data).Split('\n'))
        {
            int space = record.IndexOf(' ');
            if (space > 0 && record[(space + 1)..].StartsWith("path=", StringComparison.Ordinal))
            {
                return record[(space + 6)..];
            }
        }
        return null;
    }

    private static string Text(ReadOnlySpan<byte> field)
    {
        int end = field.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
    }

    /// <summary>
    /// Octal digits, optionally padded with leading spaces and ended by a space or NUL (an all-NUL field is 0).
    /// Anything else, including GNU's base-256 encoding of sizes over 8 GB, is not something a .vcv holds.
    /// </summary>
    private static long Octal(ReadOnlySpan<byte> field)
    {
        ReadOnlySpan<byte> digits = field.TrimStart((byte)' ');
        int end = digits.IndexOfAnyExcept("01234567"u8);
        if (end < 0)
        {
            end = digits.Length;
        }
        if (digits[end..].IndexOfAnyExcept((byte)0, (byte)' ') >= 0)
        {
            throw new InvalidDataException($"Corrupted tar archive: invalid entry size \"{Encoding.ASCII.GetString(field).TrimEnd('\0')}\".");
        }

        long value = 0;
        foreach (byte b in digits[..end])
        {
            value = (value * 8) + (b - '0');
        }
        return value;
    }
}