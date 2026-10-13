using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZstdSharp;

namespace VcvPatchTools.Core.Patch;

/// <summary>
/// A .vcv patch file, to read and write back. A .vcv is either:
///   - raw JSON (used for patches shipped inside the Cardinal repo), or
///   - a zstd-compressed tar archive containing "patch.json" (+ optional extra files, e.g. module data),
///     which is what Rack/Cardinal actually write when a user saves a patch from the UI.
/// Detection matches Rack's own check (src/patch.cpp): first 4 bytes == zstd magic.
/// Bytes in, bytes out: no file I/O, so it also runs in the browser.
/// </summary>
public sealed class PatchArchive
{
    /// <summary>
    /// The most a .vcv archive may decompress to. Real patches stay far below (a few MB with embedded samples);
    /// the cap stops a small hostile file from inflating into gigabytes of memory.
    /// </summary>
    public const long MaxDecompressedBytes = 256L * 1024 * 1024;

    private static readonly byte[] zstdMagic = { 0x28, 0xB5, 0x2F, 0xFD };


    // Rack and Cardinal write accents, "<" and "&" as they are: escaping them would only make notes unreadable.
    private static readonly JsonSerializerOptions writeOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public JsonObject Root { get; }

    /// <summary>Any non-"patch.json" files found in the source archive, preserved byte-for-byte on save.</summary>
    public List<(string Name, byte[] Data)> ExtraEntries { get; }

    public bool WasArchive { get; }

    public PatchArchive(JsonObject root, List<(string Name, byte[] Data)>? extraEntries = null, bool wasArchive = false)
    {
        Root = root;
        ExtraEntries = extraEntries ?? new List<(string Name, byte[] Data)>();
        WasArchive = wasArchive;
    }

    /// <exception cref="InvalidDataException">The bytes are not a readable patch: corrupted archive, invalid JSON, too large once decompressed.</exception>
    public static PatchArchive Read(byte[] bytes) => Read(bytes, MaxDecompressedBytes);

    internal static PatchArchive Read(byte[] bytes, long maxDecompressedBytes)
    {
        bool isArchive = bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(zstdMagic);
        if (!isArchive)
        {
            return new PatchArchive(ParseJson(bytes));
        }

        JsonObject? patchJson = null;
        List<(string Name, byte[] Data)> extras = new List<(string Name, byte[] Data)>();
        foreach ((string name, byte[] data) in TarEntries.Read(Decompress(bytes, maxDecompressedBytes)))
        {
            if (NormalizeEntryName(name) == "patch.json")
            {
                patchJson = ParseJson(data);
            }
            else
            {
                extras.Add((name, data));
            }
        }

        return patchJson is null
            ? throw new InvalidDataException("The file is a tar+zstd archive but contains no patch.json entry.")
            : new PatchArchive(patchJson, extras, wasArchive: true);
    }

    private static byte[] Decompress(byte[] bytes, long maxDecompressedBytes)
    {
        try
        {
            using MemoryStream compressedStream = new MemoryStream(bytes);
            using DecompressionStream tarStream = new DecompressionStream(compressedStream);
            using MemoryStream tarCopy = new MemoryStream();
            byte[] buffer = new byte[81920];
            int read;
            while ((read = tarStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (tarCopy.Length + read > maxDecompressedBytes)
                {
                    throw new InvalidDataException($"The archive decompresses to larger than {maxDecompressedBytes / (1024 * 1024)} MB: not a patch.");
                }
                tarCopy.Write(buffer, 0, read);
            }
            return tarCopy.ToArray();
        }
        catch (Exception e) when (e is ZstdException or IOException)
        {
            throw new InvalidDataException($"Corrupted zstd archive: {e.Message}", e);
        }
    }

    /// <summary>Always a tar+zstd archive, matching what Rack/Cardinal produce when saving from the UI.</summary>
    /// <param name="modified">Modification time of the archive's files (now, when omitted).</param>
    public byte[] Write(DateTimeOffset? modified = null)
    {
        byte[] patchJsonBytes = Encoding.UTF8.GetBytes(Root.ToJsonString(writeOptions));
        byte[] tar = TarEntries.Write(ExtraEntries.Prepend(("patch.json", patchJsonBytes)), modified ?? DateTimeOffset.UtcNow);

        using MemoryStream output = new MemoryStream();
        using (CompressionStream compressionStream = new CompressionStream(output, level: 19))
        {
            compressionStream.Write(tar);
        }
        return output.ToArray();
    }

    /// <summary>Strips a leading "./" (or repeated ones), matching tar writers that emit paths relative to "./".</summary>
    internal static string NormalizeEntryName(string name)
    {
        while (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }
        return name;
    }

    /// <summary>A JSON object, from UTF-8 bytes with or without a byte order mark (Windows editors add one).</summary>
    internal static JsonObject ParseJson(byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes.AsSpan();
        if (json.StartsWith(Encoding.UTF8.Preamble))
        {
            json = json[Encoding.UTF8.Preamble.Length..];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json.ToArray());
            return ToNode(document.RootElement) as JsonObject
                ?? throw new InvalidDataException("Root patch JSON value is not an object.");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Not valid JSON: {e.Message}", e);
        }
    }

    /// <summary>A duplicate key keeps its last value, as Rack's JSON library (jansson) does: such a patch still opens there.</summary>
    private static JsonNode? ToNode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                JsonObject obj = new JsonObject();
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    obj[property.Name] = ToNode(property.Value);
                }
                return obj;
            case JsonValueKind.Array:
                return new JsonArray(element.EnumerateArray().Select(ToNode).ToArray());
            case JsonValueKind.Null:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }
}