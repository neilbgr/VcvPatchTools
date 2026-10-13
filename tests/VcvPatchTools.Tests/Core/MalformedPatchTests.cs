using System.Text;
using System.Text.Json.Nodes;
using VcvPatchTools.Core.Patch;
using ZstdSharp;

namespace VcvPatchTools.Tests;

/// <summary>Broken or hostile files must fail with a clear <see cref="InvalidDataException"/>, never an arbitrary exception.</summary>
public class MalformedPatchTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Zstd(byte[] data)
    {
        using MemoryStream output = new MemoryStream();
        using (CompressionStream compression = new CompressionStream(output, level: 3))
        {
            compression.Write(data);
        }
        return output.ToArray();
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD })]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 1, 2, 3, 4, 5, 6, 7, 8, 9 })]
    public void Read_rejects_truncated_or_corrupted_archives(byte[] bytes) =>
        Assert.Throws<InvalidDataException>(() => PatchArchive.Read(bytes));

    [Fact]
    public void Read_rejects_an_archive_whose_tar_is_garbage() =>
        Assert.Throws<InvalidDataException>(() => PatchArchive.Read(Zstd(Utf8(new string('x', 600)))));

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"modules\": [")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Read_rejects_invalid_JSON(string json) =>
        Assert.Throws<InvalidDataException>(() => PatchArchive.Read(Utf8(json)));

    [Fact]
    public void Read_keeps_the_last_of_duplicate_keys_like_Rack()
    {
        PatchArchive patch = PatchArchive.Read(Utf8("{\"version\": \"2.6.0\", \"version\": \"2.6.1\", \"modules\": [{\"id\": 1, \"id\": 2}]}"));

        Assert.Equal("2.6.1", patch.Root["version"]!.GetValue<string>());
        Assert.Equal(2, patch.Root["modules"]![0]!["id"]!.GetValue<int>());
    }

    [Fact]
    public void Read_accepts_raw_JSON_with_a_UTF8_BOM()
    {
        byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Utf8("{\"modules\": []}")).ToArray();

        PatchArchive patch = PatchArchive.Read(bytes);

        Assert.Empty(patch.Root["modules"]!.AsArray());
    }

    [Fact]
    public void Read_stops_decompressing_past_the_size_cap()
    {
        // A megabyte of zeros compresses to a few bytes: the shape of a zip bomb, at a size a test can afford.
        byte[] tar = TarEntries.Write(new[] { ("patch.json", new byte[1024 * 1024]) }, DateTimeOffset.UnixEpoch);
        byte[] bytes = Zstd(tar);
        Assert.True(bytes.Length < 1024);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PatchArchive.Read(bytes, maxDecompressedBytes: 64 * 1024));
        Assert.Contains("larger than", error.Message);
        Assert.Equal(256L * 1024 * 1024, PatchArchive.MaxDecompressedBytes);
    }

    [Fact]
    public void Write_keeps_non_ASCII_and_markup_characters_readable()
    {
        JsonObject module = TestPatchBuilder.Module(1, "Core", "Notes");
        module["data"] = new JsonObject { ["text"] = "Été <lead> & bass" };
        PatchArchive patch = TestPatchBuilder.ToPatchArchive(TestPatchBuilder.Root(new[] { module }));

        byte[] tar;
        using (MemoryStream buffer = new MemoryStream())
        {
            using (DecompressionStream zstd = new DecompressionStream(new MemoryStream(patch.Write())))
            {
                zstd.CopyTo(buffer);
            }
            tar = buffer.ToArray();
        }
        string json = Encoding.UTF8.GetString(TarEntries.Read(tar).Single(e => e.Name == "patch.json").Data);

        Assert.Contains("\"Été <lead> & bass\"", json);
    }

    [Theory]
    [InlineData("{\"modules\": {}}")]
    [InlineData("{\"modules\": [], \"cables\": {}}")]
    [InlineData("{\"modules\": [{\"plugin\": \"Fundamental\", \"model\": \"VCO\"}]}")]
    [InlineData("{\"modules\": [{\"id\": \"one\", \"plugin\": \"Fundamental\", \"model\": \"VCO\"}]}")]
    [InlineData("{\"modules\": [{\"id\": 1, \"plugin\": 5, \"model\": \"VCO\"}]}")]
    [InlineData("{\"modules\": [{\"id\": 1, \"plugin\": \"Fundamental\", \"model\": \"VCO\", \"pos\": []}]}")]
    [InlineData("{\"modules\": [{\"id\": 1, \"plugin\": \"Fundamental\", \"model\": \"VCO\", \"pos\": [\"a\", 0]}]}")]
    [InlineData("{\"modules\": [{\"id\": 1, \"plugin\": \"Fundamental\", \"model\": \"VCO\", \"pos\": {}}]}")]
    [InlineData("{\"version\": 2, \"modules\": []}")]
    public void Parse_rejects_malformed_modules(string json)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PatchReader.Read(Utf8(json)));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Parse_rounds_fractional_positions()
    {
        PatchDocument patch = PatchReader.Read(Utf8("{\"modules\": [{\"id\": 1, \"plugin\": \"Fundamental\", \"model\": \"VCO\", \"pos\": [1.6, 2.0]}]}"));

        PatchModule module = Assert.Single(patch.Modules);
        Assert.Equal(2, module.Column);
        Assert.Equal(2, module.Row);
    }

    [Fact]
    public void Parse_reads_positions_held_as_doubles_in_memory()
    {
        JsonObject module = TestPatchBuilder.Module(1, "Fundamental", "VCO");
        module["pos"] = new JsonArray { 12.0, 1.0 };

        PatchModule parsed = Assert.Single(PatchReader.Parse(TestPatchBuilder.Root(new[] { module })).Modules);

        Assert.Equal(12, parsed.Column);
        Assert.Equal(1, parsed.Row);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"outputModuleId\": 1, \"outputId\": 0, \"inputModuleId\": 2}")]
    [InlineData("{\"outputModuleId\": 1, \"outputId\": 0, \"inputModuleId\": 2, \"inputId\": \"x\"}")]
    [InlineData("null")]
    public void Parse_skips_cables_missing_an_end_like_Rack_does(string cable)
    {
        string json = "{\"modules\": [{\"id\": 1, \"plugin\": \"Fundamental\", \"model\": \"VCO\"}, {\"id\": 2, \"plugin\": \"Fundamental\", \"model\": \"VCF\"}],"
            + "\"cables\": [" + cable + ", {\"id\": 9, \"outputModuleId\": 1, \"outputId\": 0, \"inputModuleId\": 2, \"inputId\": 0, \"color\": 3}]}";

        PatchDocument patch = PatchReader.Read(Utf8(json));

        PatchCable kept = Assert.Single(patch.Cables);
        Assert.Equal(9, kept.Id);
        Assert.Null(kept.Color);
    }

    [Fact]
    public void Parse_ignores_params_and_neighbour_links_of_the_wrong_type()
    {
        string json = "{\"modules\": [{\"id\": 1, \"plugin\": \"Fundamental\", \"model\": \"VCO\", \"leftModuleId\": \"x\","
            + "\"params\": [{\"id\": \"a\", \"value\": 1}, {\"id\": 2, \"value\": 0.5}, null]}]}";

        PatchModule module = Assert.Single(PatchReader.Read(Utf8(json)).Modules);

        Assert.Null(module.LeftModuleId);
        Assert.Equal(0.5, Assert.Single(module.Params).Value);
    }
}

public class TarEntriesRobustnessTests
{
    private static byte[] Header(string name, string sizeField)
    {
        byte[] header = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        Encoding.ASCII.GetBytes(sizeField).CopyTo(header, 124);
        header[156] = (byte)'0';
        return header;
    }

    [Theory]
    [InlineData("0000000001x\0")]
    [InlineData("12 34\0")]
    [InlineData("zzzzzzzzzzz\0")]
    public void Read_rejects_an_entry_size_that_is_not_octal(string sizeField)
    {
        byte[] tar = Header("patch.json", sizeField).Concat(new byte[2048]).ToArray();

        Assert.Throws<InvalidDataException>(() => TarEntries.Read(tar).ToList());
    }

    [Theory]
    [InlineData("00000000002\0", 2)]
    [InlineData("          2 ", 2)]
    [InlineData("\0\0\0\0\0\0\0\0\0\0\0\0", 0)]
    public void Read_accepts_padded_octal_sizes(string sizeField, int expected)
    {
        byte[] tar = Header("patch.json", sizeField).Concat(new byte[2048]).ToArray();

        (string _, byte[] data) = Assert.Single(TarEntries.Read(tar));

        Assert.Equal(expected, data.Length);
    }

    [Fact]
    public void Write_never_cuts_a_name_inside_a_surrogate_pair()
    {
        // 30 emoji (4 UTF-8 bytes each) then one ASCII letter: trimming 100 header bytes from the front
        // lands exactly between the two halves of an emoji.
        string name = string.Concat(Enumerable.Repeat("\U0001F3B9", 30)) + "a";

        byte[] tar = TarEntries.Write(new[] { (name, new byte[] { 1 }) }, DateTimeOffset.UnixEpoch);

        byte[] replacement = Encoding.UTF8.GetBytes("�");
        Assert.True(tar.AsSpan().IndexOf(replacement) < 0, "a lone surrogate was written as U+FFFD");
        Assert.Equal(name, Assert.Single(TarEntries.Read(tar)).Name);
    }
}