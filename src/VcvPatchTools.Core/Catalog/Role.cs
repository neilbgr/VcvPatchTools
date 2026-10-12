namespace VcvPatchTools.Core.Catalog;

/// <summary>Functional role of a module in a patch, which decides its band in the diagram.</summary>
public enum Role
{
    /// <summary>Clocks, sequencers, logic, dividers.</summary>
    Time,
    /// <summary>Quantizers, pitch processors.</summary>
    Pitch,
    /// <summary>LFOs, envelopes, random, slew, CV utilities.</summary>
    Controller,
    /// <summary>Oscillators, noise, samplers.</summary>
    Source,
    /// <summary>Filters, VCAs, waveshapers, EQs: in a voice, on the bus or as an insert depending on where they're patched.</summary>
    Modifier,
    /// <summary>Reverbs, delays, chorus…: placed by the patch like modifiers (voice, bus or send/return).</summary>
    Effect,
    /// <summary>Mixers: where voices end and the bus starts.</summary>
    Mixer,
    /// <summary>Host audio/MIDI interfaces: the patch's link with the outside world.</summary>
    Io,
    /// <summary>Controls the musician plays live (on-screen pads, joystick…): from the outside world when played with the
    /// mouse or driven by MIDI, a time/control utility when another module of the patch drives them.</summary>
    Performance,
    /// <summary>Scopes, meters, displays: they watch signals but make no sound.</summary>
    Monitor,
    /// <summary>Switches and multiples: they pass signals on, so they take the role of what a patch routes through them
    /// (a switch picking between pitches is a pitch processor, one muting an oscillator is part of its voice).</summary>
    Router,
}