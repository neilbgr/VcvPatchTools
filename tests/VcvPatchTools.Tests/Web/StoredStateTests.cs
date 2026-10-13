using System.Text;
using System.Text.Json;
using VcvPatchTools.Diagram.Layout;
using VcvPatchTools.Web;

namespace VcvPatchTools.Tests;

public class StoredStateTests
{
    /// <summary>Options compared by value (the record holds a set).</summary>
    private static string Fields(StoredState.Options o) =>
        $"{string.Join(",", o.HiddenLayers.Order())}|{o.ShowIntents}|{o.PortTabs}|{o.FunctionNames}|{o.LibraryLinks}|{o.Monitors}";

    [Fact]
    public void OptionsSurviveARoundTrip()
    {
        StoredState.Options options = new StoredState.Options(new HashSet<string> { "gate", "modulation" }, true, false, true, false, MonitorView.Badge);

        Assert.Equal(Fields(options), Fields(StoredState.ParseOptions(StoredState.SerializeOptions(options))));
    }

    [Fact]
    public void OptionsSavedByThePreviousVersionStillRead()
    {
        StoredState.Options read = StoredState.ParseOptions("""{"HiddenLayers":["gate"],"ShowIntents":true,"PortTabs":true,"FunctionNames":false,"LibraryLinks":true,"Monitors":2}""");

        Assert.Equal("gate|True|True|False|True|Modules", Fields(read));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("""{"HiddenLayers": null}""")]
    [InlineData("""{"HiddenLayers": "audio", "Monitors": "Badge", "PortTabs": "yes"}""")]
    [InlineData("""{"Monitors": 42}""")]
    public void DamagedOrOlderOptionsFallBackToTheDefaults(string? json)
    {
        Assert.Equal(Fields(StoredState.Options.Default), Fields(StoredState.ParseOptions(json)));
    }

    [Fact]
    public void UnknownLayersAndMissingOptionsAreLeftOut()
    {
        StoredState.Options read = StoredState.ParseOptions("""{"HiddenLayers": ["audio", "smell", 3, null], "ShowIntents": true}""");

        Assert.Equal(new[] { "audio" }, read.HiddenLayers);
        Assert.True(read.ShowIntents);
        Assert.True(read.PortTabs);
        Assert.True(read.LibraryLinks);
    }

    [Fact]
    public void IntentsSurviveARoundTrip()
    {
        Dictionary<long, string> intents = new Dictionary<long, string> { [12] = "why", [-3] = "", [9007199254740993] = "big id" };

        Assert.Equal(intents, StoredState.ParseIntents(StoredState.SerializeIntents(intents)));
    }

    [Fact]
    public void IntentsThatAreNotTextAreDropped()
    {
        Dictionary<long, string> read = StoredState.ParseIntents("""{"1": "kept", "2": null, "3": 5, "x": "no id", "4": {"a": 1}}""");

        Assert.Equal(new Dictionary<long, string> { [1] = "kept" }, read);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("[\"a\"]")]
    [InlineData("\"text\"")]
    public void DamagedIntentsReadAsNone(string? json) => Assert.Empty(StoredState.ParseIntents(json));

    [Fact]
    public void IntentsAreKeptPerPatchContentNotPerFileName()
    {
        byte[] one = Encoding.UTF8.GetBytes("""{"modules": [], "cables": []}""");
        byte[] other = Encoding.UTF8.GetBytes("""{"modules": [], "cables": [] }""");

        Assert.NotEqual(StoredState.IntentsKey(one), StoredState.IntentsKey(other));
        Assert.Equal(StoredState.IntentsKey(one), StoredState.IntentsKey(one.ToArray()));
        Assert.StartsWith("vpd-intents:", StoredState.IntentsKey(one));
        Assert.Equal("vpd-intents:My patch", StoredState.LegacyIntentsKey("My patch"));
    }

    [Fact]
    public void LoadErrorsSayWhichFileAndWhy()
    {
        Assert.Equal("a.vcv can't be opened: no patch.json", PatchLoadError.Describe("a.vcv", new InvalidDataException("no patch.json")));
        Assert.StartsWith("b.vcv can't be opened: its patch.json isn't valid JSON", PatchLoadError.Describe("b.vcv", new JsonException("bad")));
        Assert.StartsWith("c.vcv couldn't be read", PatchLoadError.Describe("c.vcv", new IOException("disk")));
        Assert.StartsWith("d.vcv can't be opened: it doesn't look like a VCV Rack or Cardinal patch", PatchLoadError.Describe("d.vcv", new NullReferenceException()));
    }
}