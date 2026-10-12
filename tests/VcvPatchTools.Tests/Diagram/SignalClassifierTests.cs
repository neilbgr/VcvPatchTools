using VcvPatchTools.Core.Catalog;
using VcvPatchTools.Diagram.Analysis;

namespace VcvPatchTools.Tests;

public class SignalClassifierTests
{
    private readonly SignalClassifier classifier = new SignalClassifier();

    [Fact]
    public void ColorWinsOverDestinationPort()
    {
        // Pitch patched into an envelope's Attack input (key tracking) is still pitch.
        (SignalType type, string? mismatch) = classifier.Classify("#ffd452", "1V/octave pitch", "Attack", Role.Io, Role.Controller);

        Assert.Equal(SignalType.Pitch, type);
        Assert.Null(mismatch);
    }

    [Fact]
    public void MismatchBetweenColorAndOutputIsReported()
    {
        (SignalType type, string? mismatch) = classifier.Classify("#ff5252", "Gate", "Left", Role.Io, Role.Modifier);

        Assert.Equal(SignalType.Audio, type);
        Assert.NotNull(mismatch);
    }

    [Theory]
    [InlineData("Clk 2", "Trigger", SignalType.Gate)]
    [InlineData("Sequence", "Pitch (1V/octave)", SignalType.Pitch)]
    [InlineData("Signal", "Pulse width CV", SignalType.Cv)]
    public void UnknownColorFallsBackToPortNames(string fromPort, string toPort, SignalType expected) =>
        Assert.Equal(expected, classifier.Classify("#123456", fromPort, toPort, Role.Controller, Role.Source).Type);

    [Fact]
    public void UnknownColorBetweenAudioRolesIsAudio() =>
        Assert.Equal(SignalType.Audio, classifier.Classify(null, "Out", "In", Role.Source, Role.Modifier).Type);

    [Theory]
    [InlineData("Velocity Zone B", "1V/octave pitch")]
    [InlineData("Aftertouch", "Pitch")]
    public void ExpressionOutputsAreCvWhereverTheyGo(string fromPort, string toPort) =>
        Assert.Equal(SignalType.Cv, classifier.Classify("#00b56e", fromPort, toPort, Role.Io, Role.Pitch).Type);

    [Fact]
    public void RetriggerIsAGate() =>
        Assert.Equal(SignalType.Gate, classifier.Classify(null, "Retrigger", "Retrigger (poly)", Role.Io, Role.Io).Type);

    [Fact]
    public void AnInputNamedCvTakesAModulationEvenFromAProcessor() =>
        Assert.Equal(SignalType.Cv, classifier.Classify(null, "Channel", "Channel 2 CV", Role.Modifier, Role.Mixer).Type);
}