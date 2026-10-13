using VcvPatchTools.Cli;

namespace VcvPatchTools.Tests;

public sealed class ConvertCommandTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("vcvpatch-convert-").FullName;

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private string WriteInput(string json)
    {
        string path = Path.Combine(directory, "Input.vcv");
        File.WriteAllText(path, json);
        return path;
    }

    private string ValidInput() => WriteInput("""{ "modules": [{ "id": 1, "plugin": "Cardinal", "model": "HostAudio8", "pos": [0, 0] }], "cables": [] }""");

    private static (int Code, string Output, string Error) Run(params string[] args)
    {
        StringWriter output = new StringWriter();
        StringWriter error = new StringWriter();
        int code = ConvertCommand.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private static string[] Lines(string text) => text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void A_malformed_patch_is_reported_on_one_line()
    {
        string input = WriteInput("{ \"modules\": [");

        (int code, string _, string error) = Run("convert", input);

        Assert.Equal(1, code);
        string line = Assert.Single(Lines(error));
        Assert.Contains(input, line);
    }

    [Fact]
    public void A_patch_with_an_invalid_module_is_reported_on_one_line()
    {
        string input = WriteInput("""{ "modules": [{ "plugin": "Cardinal", "model": "HostMIDI" }] }""");

        (int code, string _, string error) = Run("convert", input, Path.Combine(directory, "Output.vcv"));

        Assert.Equal(1, code);
        Assert.Single(Lines(error));
        Assert.False(File.Exists(Path.Combine(directory, "Output.vcv")));
    }

    [Fact]
    public void A_trailing_to_option_is_a_usage_error_not_an_output_file()
    {
        string input = ValidInput();

        try
        {
            (int code, string _, string error) = Run("convert", input, "--to");

            Assert.Equal(1, code);
            Assert.Contains("--to", error);
            Assert.False(File.Exists("--to"));
        }
        finally
        {
            File.Delete("--to");
        }
    }

    [Fact]
    public void An_unknown_target_is_a_usage_error()
    {
        (int code, string _, string error) = Run("convert", ValidInput(), Path.Combine(directory, "Output.vcv"), "--to", "foo");

        Assert.Equal(1, code);
        Assert.Contains("\"foo\"", error);
    }

    [Fact]
    public void An_unknown_option_is_a_usage_error()
    {
        (int code, string _, string _) = Run("convert", ValidInput(), Path.Combine(directory, "Output.vcv"), "--frobnicate");

        Assert.Equal(1, code);
    }

    [Fact]
    public void A_valid_patch_is_converted()
    {
        string output = Path.Combine(directory, "Output.vcv");

        (int code, string _, string _) = Run("convert", ValidInput(), output, "--to", "rack");

        Assert.Equal(0, code);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Info_on_a_malformed_patch_exits_with_an_error_code()
    {
        string input = WriteInput("not a patch");

        object? code = typeof(ConvertCommand).Assembly.EntryPoint!.Invoke(null, new object[] { new[] { "info", input } });

        Assert.Equal(1, code);
    }
}