using System.Text;
using VcvPatchTools.Core.Patch;

namespace VcvPatchTools.Tests;

public class PatchReaderTests
{
    [Fact]
    public void ReadsCardinalArchiveWithDotSlashEntries()
    {
        PatchDocument patch = PatchReader.Read(Fixtures.AmbientJamBytes);

        Assert.Equal("2.4.1", patch.RackVersion);
        Assert.Equal(23, patch.Modules.Count);
        Assert.Equal(38, patch.Cables.Count);
    }

    [Fact]
    public void ReadsModulesWhoseDataIsNotAnObject()
    {
        string json = """
            {
              "modules": [
                { "id": 1, "plugin": "Cardinal", "model": "Ildaeil", "data": "<?xml version='1.0'?><CARLA-PROJECT/>" },
                { "id": 2, "plugin": "Cardinal", "model": "HostMIDIGate", "data": { "notes": [36, -1] } }
              ],
              "cables": []
            }
            """;

        PatchDocument patch = PatchReader.Read(Encoding.UTF8.GetBytes(json));

        Assert.Empty(patch.Module(1).LearnedNotes);
        Assert.Equal(new[] { 36, -1 }, patch.Module(2).LearnedNotes);
    }

    [Fact]
    public void ReadsTheLabelsTypedOnAModule()
    {
        string json = """
            {
              "modules": [
                { "id": 1, "plugin": "Aluminium", "model": "Faders2", "data": { "faders": [{ "label": "Cutoff" }, { "label": "" }] } },
                { "id": 2, "plugin": "Aluminium", "model": "Buttons", "data": { "buttons": [{ "color": 0 }, { "label": "Sub" }] } },
                { "id": 3, "plugin": "Aluminium", "model": "PadX", "data": { "label": "Drums" } },
                { "id": 4, "plugin": "Fundamental", "model": "VCO" }
              ],
              "cables": []
            }
            """;

        PatchDocument patch = PatchReader.Read(Encoding.UTF8.GetBytes(json));

        Assert.Equal(new[] { "Cutoff", "" }, patch.Module(1).Labels);
        Assert.Equal(new[] { "", "Sub" }, patch.Module(2).Labels);
        Assert.Equal(new[] { "Drums" }, patch.Module(3).Labels);
        Assert.Empty(patch.Module(4).Labels);
    }

    [Fact]
    public void ReadsRawJsonPatch()
    {
        string json = """
            {
              "version": "2.5.2",
              "modules": [
                { "id": 1, "plugin": "Fundamental", "model": "VCO", "pos": [0, 0] },
                { "id": 2, "plugin": "Fundamental", "model": "VCF", "pos": [10, 0] }
              ],
              "cables": [
                { "id": 7, "outputModuleId": 1, "outputId": 2, "inputModuleId": 2, "inputId": 3, "color": "#ff5252" }
              ]
            }
            """;

        PatchDocument patch = PatchReader.Read(Encoding.UTF8.GetBytes(json));

        PatchCable cable = Assert.Single(patch.Cables);
        Assert.Equal(new PortRef(1, 2), cable.From);
        Assert.Equal(new PortRef(2, 3), cable.To);
        Assert.Equal("#ff5252", cable.Color);
        Assert.Equal("Fundamental/VCF", patch.Module(2).CatalogKey);
    }
}

public class TarEntriesTests
{
    [Fact]
    public void ReadsPaxLongPathLikeSystemFormatsTar()
    {
        string longName = new string('d', 120) + "/patch.json";
        using MemoryStream buffer = new MemoryStream();
        using (System.Formats.Tar.TarWriter writer = new System.Formats.Tar.TarWriter(buffer, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.Directory, "dir/"));
            writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, longName)
            {
                DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{}")),
            });
        }

        (string name, byte[] data) = Assert.Single(VcvPatchTools.Core.Patch.TarEntries.Read(buffer.ToArray()));

        Assert.Equal(longName, name);
        Assert.Equal("{}", System.Text.Encoding.UTF8.GetString(data));
    }

    [Theory]
    [InlineData("patch.json")]
    [InlineData("a-folder-whose-name-is-long-enough-to-need-a-pax-header-because-ustar-names-stop-at-one-hundred-bytes/patch.json")]
    public void WritesArchivesThatStandardTarReadersRead(string name)
    {
        byte[] tar = VcvPatchTools.Core.Patch.TarEntries.Write(new[] { (name, System.Text.Encoding.UTF8.GetBytes("{\"version\":\"2.6.0\"}")), ("module/1.bin", new byte[] { 0, 1, 2, 255 }) }, DateTimeOffset.UnixEpoch.AddDays(20000));

        using System.Formats.Tar.TarReader reader = new System.Formats.Tar.TarReader(new MemoryStream(tar));
        System.Formats.Tar.TarEntry first = reader.GetNextEntry()!;
        System.Formats.Tar.TarEntry second = reader.GetNextEntry()!;
        using MemoryStream data = new MemoryStream();
        second.DataStream!.CopyTo(data);

        Assert.Equal(name, first.Name);
        Assert.Equal(System.Formats.Tar.TarEntryType.RegularFile, first.EntryType);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(20000), first.ModificationTime);
        Assert.Equal("module/1.bin", second.Name);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, data.ToArray());
        Assert.Null(reader.GetNextEntry());
        Assert.Equal(new[] { name, "module/1.bin" }, VcvPatchTools.Core.Patch.TarEntries.Read(tar).Select(e => e.Name));
    }
}