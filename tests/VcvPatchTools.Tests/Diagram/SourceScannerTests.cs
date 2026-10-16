using VcvPatchTools.Core.Catalog;

namespace VcvPatchTools.Tests;

public class SourceScannerTests
{
    [Fact]
    public void EnumBodyExpandsEnumsMacroAndExplicitValues()
    {
        List<(string Id, int Index, string Fallback)> entries = SourceScanner.ParseEnumBody("""
            CLOCK_INPUT,
            ENUMS(CV_INPUTS, 3),
            RESET_INPUT = CV_INPUTS + 3,
            NUM_INPUTS
            """);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, entries.Select(e => e.Index));
        Assert.Equal("Clock", entries[0].Fallback);
        Assert.Equal("CV_INPUTS+2", entries[3].Id);
        Assert.Equal("Cv 3", entries[3].Fallback);
        Assert.Equal(("RESET_INPUT", 4, "Reset"), entries[4]);
    }

    [Fact]
    public void EnumBodyStopsAtUnknownExpression()
    {
        List<(string Id, int Index, string Fallback)> entries = SourceScanner.ParseEnumBody("INPUT_L, INPUT_R, MOD_INPUT, NUM_INPUTS = MOD_INPUT + n_mod_inputs");

        Assert.Equal(3, entries.Count);
    }

    [Theory]
    // The count closing the enum isn't a port, whatever its name.
    [InlineData("A, B, INPUTS_LEN", new[] { "A", "B" })]
    [InlineData("COUNT_OUTPUT, OUTPUTS_LEN", new[] { "COUNT_OUTPUT" })]
    [InlineData("A, INPUT_COUNT", new[] { "A" })]
    [InlineData("A, nINPUTS", new[] { "A" })]
    // A range is sized by the next entry, or by the end marker.
    [InlineData("OSC_INPUT, CV_INPUT = OSC_INPUT + 3, NUM_INPUTS = CV_INPUT + 2", new[] { "OSC_INPUT", "OSC_INPUT+1", "OSC_INPUT+2", "CV_INPUT", "CV_INPUT+1" })]
    // An inclusive "last index" marker, then the count.
    [InlineData("OUTPUTS_START, OUTPUTS_END = OUTPUTS_START + 3 - 1, NUM_OUTPUTS = 3", new[] { "OUTPUTS_START", "OUTPUTS_START+1", "OUTPUTS_START+2" })]
    public void EnumBodyEndsAtItsCountAndSizesRanges(string body, string[] ids)
    {
        Assert.Equal(ids, SourceScanner.ParseEnumBody(body).Select(e => e.Id));
    }

    [Fact]
    public void StripCommentsKeepsUrlsInStrings()
    {
        string cleaned = SourceScanner.StripComments("""configInput(A, "http://x"); // gone""" + "\n/* gone */ B");

        Assert.Contains("\"http://x\"", cleaned);
        Assert.DoesNotContain("gone", cleaned);
    }

    [Fact]
    public void ScansPluginWithConfiguredAndHumanizedNames()
    {
        string manifest = """
            { "slug": "Demo", "modules": [ { "slug": "Env", "name": "Envelope", "tags": ["Envelope generator"] }, { "slug": "Ghost" } ] }
            """;
        string source = """
            struct Env : Module {
                enum InputIds { GATE_INPUT, ENUMS(CV_INPUTS, 2), NUM_INPUTS };
                enum OutputIds { ENV_OUTPUT, NUM_OUTPUTS };
                Env() {
                    configInput(GATE_INPUT, "Gate");
                    configInput(CV_INPUTS + 1, "Release CV");
                }
            };
            Model* modelEnv = createModel<Env, EnvWidget>("Env");
            """;

        ScanResult result = SourceScanner.ScanPlugin(manifest, new[] { new SourceFile("Env.cpp", source) });

        ModuleInfo env = result.Modules["Demo/Env"];
        Assert.Equal("Envelope", env.Name);
        Assert.Equal(new[] { "Gate", "Cv 1", "Release CV" }, env.Inputs);
        Assert.Equal(new[] { "Env" }, env.Outputs);
        Assert.Contains(result.Problems, p => p.StartsWith("Demo/Ghost", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolvesSurgeStyleAliasAndModulationMatrix()
    {
        string manifest = """{ "slug": "SurgeXTRack", "modules": [ { "slug": "SurgeXTVCF", "name": "VCF" } ] }""";
        string header = """
            struct VCF : public modules::XTModule
            {
                static constexpr int n_mod_inputs{4};
                enum ParamIds { FREQUENCY, RESONANCE, VCF_MOD_PARAM_0, VCF_TYPE = VCF_MOD_PARAM_0 + 2 * n_mod_inputs, NUM_PARAMS };
                enum InputIds { INPUT_L, INPUT_R, VCF_MOD_INPUT, NUM_INPUTS = VCF_MOD_INPUT + n_mod_inputs };
                enum OutputIds { OUTPUT_L, OUTPUT_R, NUM_OUTPUTS };
                static int modulatorIndexFor(int baseParam, int modulator)
                {
                    int offset = baseParam - FREQUENCY;
                    return VCF_MOD_PARAM_0 + offset * n_mod_inputs + modulator;
                }
                modules::ModulationAssistant<VCF, 2, FREQUENCY, n_mod_inputs, VCF_MOD_INPUT> modulationAssistant;
                VCF()
                {
                    configParam<modules::VOctParamQuantity<60>>(FREQUENCY, -4, 6, 0, "Frequency");
                    configParam(RESONANCE, 0, 1, 0.7, "Resonance", "%", 0.f, 100.f);
                    configInput(INPUT_L, "Left");
                }
            };
            """;
        string widget = """
            struct VCFWidget : widgets::XTModuleWidget
            {
                typedef vcf::VCF M;
            };
            rack::Model *modelSurgeVCF = rack::createModel<sst::surgext_rack::vcf::ui::VCFWidget::M,
                                                           sst::surgext_rack::vcf::ui::VCFWidget>("SurgeXTVCF");
            """;

        ScanResult result = SourceScanner.ScanPlugin(manifest, new[] { new SourceFile("VCF.h", header), new SourceFile("VCF.cpp", widget) });

        ModuleInfo vcf = result.Modules["SurgeXTRack/SurgeXTVCF"];
        Assert.Equal((2, 4, 2), (vcf.Modulation!.FirstInput, vcf.Modulation.InputCount, vcf.Modulation.FirstDepthParam));
        Assert.Equal(new[] { "Frequency", "Resonance" }, vcf.Modulation!.Targets);
        Assert.Equal("Modulation Signal 4", vcf.Inputs[5]);
    }

    [Fact]
    public void ResolvesSurgeTemplatedWidgetsMacroSlugsAndEngineConstants()
    {
        string manifest = """{ "slug": "SurgeXTRack", "modules": [ { "slug": "SurgeXTOSCClassic" }, { "slug": "SurgeXTFXReverb" } ] }""";
        string vco = """
            template <int oscType> struct VCO : modules::XTModule
            {
                static constexpr int n_mod_inputs{4};
                enum ParamIds { PITCH_0, OSC_CTRL_PARAM_0, OSC_MOD_PARAM_0 = OSC_CTRL_PARAM_0 + n_osc_params, NUM_PARAMS };
                enum InputIds { PITCH_CV, RETRIGGER, OSC_MOD_INPUT, AUDIO_INPUT = OSC_MOD_INPUT + n_mod_inputs, NUM_INPUTS };
                enum OutputIds { OUTPUT_L, OUTPUT_R, NUM_OUTPUTS };
                modules::ModulationAssistant<VCO<oscType>, n_osc_params + 1, PITCH_CV, n_mod_inputs, OSC_MOD_INPUT> modAssist;
                VCO()
                {
                    configParam<modules::VOctParamQuantity<60>>(PITCH_0, -5, 5, 0, "Pitch (v/oct)");
                    for (int i = 0; i < n_osc_params + 1; ++i)
                    {
                        configParam<modules::SurgeParameterParamQuantity>(OSC_CTRL_PARAM_0 + i, 0, 1, oscstorage->p[i].get_value_f01());
                    }
                    configInput(PITCH_CV, "V/Oct");
                    configInput(RETRIGGER, "Reset/Retrigger");
                    configInput(AUDIO_INPUT, "Audio");
                    for (int m = 0; m < n_mod_inputs; ++m)
                    {
                        auto s = std::string("Modulation Signal ") + std::to_string(m + 1);
                        configInput(OSC_MOD_INPUT + m, s);
                    }
                    configOutput(OUTPUT_L, "Left (or Mono merged)");
                    configOutput(OUTPUT_R, "Right");
                }
                static int modulatorIndexFor(int baseParam, int modulator)
                {
                    int offset = baseParam - PITCH_0;
                    return OSC_MOD_PARAM_0 + offset * n_mod_inputs + modulator;
                }
            };
            template <int oscType> struct VCOWidget : widgets::XTModuleWidget { typedef VCO<oscType> M; };
            auto m = rack::createModel<vcoui::VCOWidget<ot_classic>::M, vcoui::VCOWidget<ot_classic>>(
                "SurgeXTOSCClassic");
            template <int fxType> struct FX : modules::XTModule
            {
                enum InputIds { INPUT_L, INPUT_R, NUM_INPUTS };
                enum OutputIds { OUTPUT_L, NUM_OUTPUTS };
                FX() { configInput(INPUT_L, "Left"); configInput(INPUT_R, "Right"); configOutput(OUTPUT_L, "Left"); }
            };
            template <int fxType> struct FXWidget : widgets::XTModuleWidget { typedef FX<fxType> M; };
            #define FXMODEL(type, nm)                                                                    \
                rack::Model *modelFX##nm = rack::createModel<fxui::FXWidget<type>::M, fxui::FXWidget<type>>( \
                    std::string("SurgeXTFX") + #nm);
            FXMODEL(fxt_reverb, Reverb);
            """;
        string engine = "const int n_osc_params = 7;";

        ScanResult result = SourceScanner.ScanPlugin(manifest, new[] { new SourceFile("VCO.h", vco) }, new[] { new SourceFile("SurgeStorage.h", engine) }, surgeEngine);

        ModuleInfo classic = result.Modules["SurgeXTRack/SurgeXTOSCClassic"];
        Assert.Equal(new[] { "V/Oct", "Reset/Retrigger", "Modulation Signal 1", "Modulation Signal 2", "Modulation Signal 3", "Modulation Signal 4", "Audio" }, classic.Inputs);
        Assert.Equal((2, 4, 8), (classic.Modulation!.FirstInput, classic.Modulation.InputCount, classic.Modulation.FirstDepthParam));
        // The engine names the classic oscillator's knobs; past its 3 test names, the generic ones.
        Assert.Equal(new[] { "Pitch (v/oct)", "Shape", "Width 1", "Ratio", "Osc ctrl param 4" }, classic.Modulation.Targets.Take(5));
        Assert.Equal(new[] { "Left", "Right" }, result.Modules["SurgeXTRack/SurgeXTFXReverb"].Inputs);
        Assert.Empty(result.Problems);
    }

    private static readonly SourceFile[] surgeEngine =
    {
        new SourceFile("dsp/Oscillator.cpp", """
            Oscillator *spawn_osc(int osctype, SurgeStorage *storage, OscillatorStorage *oscdata, pdata *localcopy, unsigned char *onto)
            {
                switch (osctype)
                {
                case ot_classic:
                    return new (onto) ClassicOscillator(storage, oscdata, localcopy);
                case ot_window:
                {
                    // misconfigured: a sine instead
                    if (storage && storage->WindowWT.size == 0)
                        return new (onto) SineOscillator(storage, oscdata, localcopy);
                    return new (onto) WindowOscillator(storage, oscdata, localcopy);
                }
                case ot_modern:
                    return new (onto) ModernOscillator(storage, oscdata, localcopy);
                case ot_sine:
                default:
                    return new (onto) SineOscillator(storage, oscdata, localcopy);
                }
            }
            """),
        new SourceFile("dsp/oscillators/ClassicOscillator.h", "class ClassicOscillator { enum co_params { co_shape = 0, co_width1, co_ratio }; };"),
        new SourceFile("dsp/oscillators/ClassicOscillator.cpp", """
            void ClassicOscillator::init_ctrltypes()
            {
                oscdata->p[co_width1].set_name("Width 1");
                oscdata->p[co_shape].set_name("Shape");
                if (oscdata->p[co_ratio].absolute)
                {
                    oscdata->p[co_ratio].set_name("Frequency");
                }
                else
                {
                    oscdata->p[co_ratio].set_name("Ratio");
                }
            }
            """),
        new SourceFile("dsp/oscillators/WindowOscillator.cpp", """void WindowOscillator::init_ctrltypes() { oscdata->p[win_morph].set_name("Morph"); }"""),
        new SourceFile("dsp/oscillators/WindowOscillator.h", "enum win_params { win_morph = 0 };"),
        new SourceFile("dsp/oscillators/ModernOscillator.h", """
            enum mo_params { mo_saw_mix = 0, mo_tri_mix };
            const char mo_multitype_names[3][16] = {"Triangle", "Square", "Sine"};
            """),
        new SourceFile("dsp/oscillators/ModernOscillator.cpp", """
            static struct ModernTriName : public ParameterDynamicNameFunction
            {
                const char *getName(const Parameter *p) const override { return mo_multitype_names[p->deform_type & 0xF]; }
            } dpwTriName;
            void ModernOscillator::init_ctrltypes()
            {
                oscdata->p[mo_saw_mix].set_name("Sawtooth");
                oscdata->p[mo_tri_mix].set_name("--DYNAMIC-NAME--");
                oscdata->p[mo_tri_mix].dynamicName = &dpwTriName;
            }
            """),
    };

    [Fact]
    public void ReadsSurgeOscillatorParameterNamesFromTheEngine()
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> names = SurgeOscillators.ParameterNames(surgeEngine);

        // In enum order; the default branch's name (the last one) when a condition picks it.
        Assert.Equal(new[] { "Shape", "Width 1", "Ratio" }, names["ot_classic"]);
        // ot_window's real class, not the sine it falls back to.
        Assert.Equal(new[] { "Morph" }, names["ot_window"]);
        // A name the patch picks: its options.
        Assert.Equal(new[] { "Sawtooth", "Triangle / Square / Sine" }, names["ot_modern"]);
    }

    [Fact]
    public void ResolvesCompositeEnumsFromAnIncludedHeaderAndQualifiedIds()
    {
        // SquinkyLabs' architecture: the module only knows "Comp", the enums are in a composite header outside src/.
        string manifest = """{ "slug": "squinkylabs-plug1", "modules": [ { "slug": "squinkylabs-mixm" } ] }""";
        string module = """
            #include "MixM.h"
            using Comp = MixM<WidgetComposite>;
            struct MixMModule : MixerModule
            {
                MixMModule();
            };
            MixMModule::MixMModule()
            {
                config(Comp::NUM_PARAMS, Comp::NUM_INPUTS, Comp::NUM_OUTPUTS, Comp::NUM_LIGHTS);
                configInput(Comp::AUDIO0_INPUT, "Channel 1");
            }
            Model *modelMixMModule = createModel<MixMModule, MixMWidget>("squinkylabs-mixm");
            """;
        string composite = """
            template <class TBase>
            class MixM : public TBase
            {
            public:
                enum InputIds { AUDIO0_INPUT, AUDIO1_INPUT, NUM_INPUTS };
                enum OutputIds { LEFT_OUTPUT, NUM_OUTPUTS };
            };
            """;

        ScanResult result = SourceScanner.ScanPlugin(manifest, new[] { new SourceFile("src/MixMModule.cpp", module) }, new[] { new SourceFile("composites/MixM.h", composite) });

        ModuleInfo mixer = result.Modules["squinkylabs-plug1/squinkylabs-mixm"];
        Assert.Equal(new[] { "Channel 1", "Audio1" }, mixer.Inputs);
        Assert.Equal(new[] { "Left" }, mixer.Outputs);
    }

    [Fact]
    public void ResolvesPluginModelHelpersAndAliasedBases()
    {
        // Sapphire: its own createSapphireModel helper, a base class reached through an alias.
        string manifest = """{ "slug": "CosineKitty-Sapphire", "modules": [ { "slug": "Frolic" } ] }""";
        string source = """
            namespace Sapphire::Chaos
            {
                template <typename circuit_t>
                struct ChaosModule : SapphireModule
                {
                    enum InputId { SPEED_CV_INPUT, CHAOS_CV_INPUT, INPUTS_LEN };
                    enum OutputId { X_OUTPUT, OUTPUTS_LEN };
                    ChaosModule() { configInput(SPEED_CV_INPUT, "Speed CV"); configOutput(X_OUTPUT, "X"); }
                };
            }
            namespace Sapphire
            {
                using FrolicModuleBase = Sapphire::Chaos::ChaosModule<Rucklidge>;
                struct FrolicModule : FrolicModuleBase {};
            }
            Model* modelSapphireFrolic = createSapphireModel<Sapphire::FrolicModule, Sapphire::FrolicWidget>(
                "Frolic",
                Sapphire::ExpanderRole::ChaosOpReceiver
            );
            """;

        ScanResult result = SourceScanner.ScanPlugin(manifest, new[] { new SourceFile("frolic_vcv.cpp", source) });

        ModuleInfo frolic = result.Modules["CosineKitty-Sapphire/Frolic"];
        Assert.Equal(new[] { "Speed CV", "Chaos cv" }, frolic.Inputs);
        Assert.Equal(new[] { "X" }, frolic.Outputs);
    }

    [Fact]
    public void InstantiatesHeadersWrittenAgainstTheIncludingFilesMacros()
    {
        // mscHack: each mixer .cpp defines its sizes and its struct name, then includes one shared header.
        string manifest = """{ "slug": "mscHack", "modules": [ { "slug": "Mix_4_0_4" }, { "slug": "Mix_9_3_4" }, { "slug": "ASAF8" } ] }""";
        string header = """
            struct Mixer_ : Module
            {
                enum InputIds
                {
                    IN_LEFT,
                    IN_RIGHT = IN_LEFT + nCHANNELS,
            #if nGROUPS > 0
                    IN_FADEX = IN_RIGHT + nCHANNELS,
                    nINPUTS
            #else
                    nINPUTS = IN_RIGHT + nCHANNELS
            #endif
                };
                enum OutputIds { OUT_MAINL, nOUTPUTS };
            };
            """;
        string mixer(string name, int channels, int groups) => $"""
            #define nCHANNELS {channels}
            #define nGROUPS {groups}
            #define Mixer_ {name}
            #include "Mixer_x_x_x.hpp"
            Model *model{name} = createModel<{name}, {name}_Widget>( "{name.Replace("Mixer", "Mix")}" );
            """;
        // A file's own #define wins over another file's.
        string asaf = """
            #define nCHANNELS 8
            struct ASAF8 : Module { enum InputIds { IN_TRIGS, nINPUTS = IN_TRIGS + nCHANNELS }; };
            Model *modelASAF8 = createModel<ASAF8, ASAF8_Widget>( "ASAF8" );
            """;

        ScanResult result = SourceScanner.ScanPlugin(manifest, new[]
        {
            new SourceFile("Mixer_x_x_x.hpp", header),
            new SourceFile("Mixer_4_0_4.cpp", mixer("Mixer_4_0_4", 4, 0)),
            new SourceFile("Mixer_9_3_4.cpp", mixer("Mixer_9_3_4", 9, 3)),
            new SourceFile("ASAF8.cpp", asaf),
        });

        Assert.Equal(8, result.Modules["mscHack/Mix_4_0_4"].Inputs.Count);
        Assert.Equal(19, result.Modules["mscHack/Mix_9_3_4"].Inputs.Count);
        Assert.Equal("In fadex", result.Modules["mscHack/Mix_9_3_4"].Inputs[^1]);
        Assert.Equal(8, result.Modules["mscHack/ASAF8"].Inputs.Count);
    }

    [Fact]
    public void FindsEnumsThroughConfigCountsSlugConstantsAndRackDefaultNames()
    {
        string manifest = """{ "slug": "P", "modules": [ { "slug": "ACLFold" }, { "slug": "8CV" }, { "slug": "Thru" }, { "slug": "Blank" }, { "slug": "atoms" } ] }""";
        string source = """
            constexpr auto SlugLFold {"ACLFold"};
            struct LFold : Module
            {
                enum LFoldInputIds { SIGNAL_INPUT, CLOCK_INPUT, NUM_INPUTS };
                enum LFoldOutputIds { OUT_OUTPUT, NUM_OUTPUTS };
                LFold() { config(NUM_PARAMS, NUM_INPUTS, NUM_OUTPUTS, NUM_LIGHTS); configInput(SIGNAL_INPUT, "Signal"); }
            };
            Model *modelLFold = createModel<LFold, LFoldWidget>(SlugLFold);
            struct Module8CV : Module { Module8CV() { config( 0, 8, 1, 0 ); } };
            Model *model8CV = createModel<Module8CV, Widget8CV>("8CV");
            struct Thru : VenomModule
            {
                enum ExpInputId { ENUMS(POLY_INPUT, 2), EXP_INPUTS_LEN };
                enum ExpOutputId { ENUMS(POLY_OUTPUT, 2), EXP_OUTPUTS_LEN };
                Thru() { venomConfig(EXP_PARAMS_LEN, EXP_INPUTS_LEN, EXP_OUTPUTS_LEN, EXP_LIGHTS_LEN); configOutput(POLY_OUTPUT + 1, ""); }
            };
            Model *modelThru = createModel<Thru, ThruWidget>("Thru");
            Model *modelBlank = createModel<Module, BlankWidget>("Blank");
            """;
        string atoms = """
            #define SLUG "atoms"
            struct Atoms : Module { enum InputIds { ATOM_INPUT, NUM_INPUTS }; enum OutputIds { NUM_OUTPUTS }; };
            Model *modelAtoms = createModel<Atoms, AtomsWidget>(SLUG);
            """;

        ScanResult result = SourceScanner.ScanPlugin(manifest, new[] { new SourceFile("all.cpp", source), new SourceFile("atoms.cpp", atoms) });

        Assert.Equal(new[] { "Signal", "Clock" }, result.Modules["P/ACLFold"].Inputs);
        Assert.Equal(new[] { "#1", "#2", "#3", "#4", "#5", "#6", "#7", "#8" }, result.Modules["P/8CV"].Inputs);
        Assert.Equal(new[] { "#1" }, result.Modules["P/8CV"].Outputs);
        // An empty configured name shows as "#n", as in Rack.
        Assert.Equal(new[] { "Poly 1", "#2" }, result.Modules["P/Thru"].Outputs);
        Assert.Equal(new[] { "Atom" }, result.Modules["P/atoms"].Inputs);
        Assert.Empty(result.Modules["P/Blank"].Inputs);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void NamesLoopPortsFromStringConcatenations()
    {
        string manifest = """{ "slug": "P", "modules": [ { "slug": "Loops" } ] }""";
        string source = """
            struct Loops : Module
            {
                enum InputIds { ENUMS(OR_INPUT, 3), ENUMS(RESET_INPUT, 2), ENUMS(ANG_INPUT, 3), NUM_INPUTS };
                enum OutputIds { ENUMS(TRACK_OUTPUTS, 4), NUM_OUTPUTS };
                Loops()
                {
                    const char* colors[2] = { "Orange", "Yellow" };
                    for (int i = 0; i < 3; i++) { configInput(OR_INPUT + i, "OR " + std::to_string((2-i)+1)); }
                    for (int i = 0; i < 2; i++) { configInput(RESET_INPUT + i, "Reset " + std::string(colors[i])); }
                    configInput(ANG_INPUT, "Ball");
                    for (int i = 0; i < 2; i++) { configInput(ANG_INPUT + i+1, "Angle " + std::to_string(i+1)); }
                    for (unsigned int i = 0; i < 4; i += 2)
                    {
                        configOutput(TRACK_OUTPUTS + i, "Track " + std::to_string((i / 2) + 1) + ": left");
                        configOutput(TRACK_OUTPUTS + i + 1, "Track " + std::to_string((i / 2) + 1) + ": right");
                    }
                }
            };
            Model *modelLoops = createModel<Loops, LoopsWidget>("Loops");
            """;

        ModuleInfo loops = SourceScanner.ScanPlugin(manifest, new[] { new SourceFile("loops.cpp", source) }).Modules["P/Loops"];

        Assert.Equal(new[] { "OR 3", "OR 2", "OR 1", "Reset Orange", "Reset Yellow", "Ball", "Angle 1", "Angle 2" }, loops.Inputs);
        Assert.Equal(new[] { "Track 1: left", "Track 1: right", "Track 2: left", "Track 2: right" }, loops.Outputs);
    }

    [Fact]
    public void ResolvesDheStyleModulesFolderByFolder()
    {
        // Every DHE module is "Module" in its own folder; enums nested in structs, sized by macros, in a header next to it.
        string manifest = """{ "slug": "DHE-Modules", "modules": [ { "slug": "Scannibal4" }, { "slug": "Blossom" } ] }""";
        string scannibalIds = """
            #define PER_STEP(name, n) name, name##_LAST = (name) + (n)-1
            #define ONE_PER_STEP(name, n) PER_STEP(name, n)
            namespace dhe { namespace scannibal {
            template <int N> struct InputIds { enum { InA, Phase, ONE_PER_STEP(StepCurvatureCv, N), Count }; };
            struct OutputId { enum { Out, Count }; };
            } }
            """;
        string scannibalModule = """
            #include "control-ids.h"
            namespace dhe { namespace scannibal {
            template <int N> struct Module : rack::engine::Module
            {
                using InputId = InputIds<N>;
                Module()
                {
                    config(ParamId::Count, InputId::Count, OutputId::Count);
                    configInput(InputId::InA, "A");
                    for (auto step = 0; step < N; step++)
                    {
                        auto const step_name = "Step " + std::to_string(step + 1) + " ";
                        configInput(InputId::StepCurvatureCv + step, step_name + "curvature CV");
                    }
                }
            };
            } }
            """;
        string scannibalInit = """
            #include "module.h"
            void init(rack::plugin::Plugin *plugin) { plugin->addModel(rack::createModel<Module<4>, Panel<Scannibal4>>("Scannibal4")); }
            """;
        string blossomIds = "namespace dhe { namespace blossom { struct InputId { enum { SpinSpeedCv, Count }; }; struct OutputId { enum { X, Count }; }; } }";
        string blossomModule = """
            #include "control-ids.h"
            namespace dhe { namespace blossom { struct Module : rack::engine::Module { Module() { configOutput(OutputId::X, "X"); } }; } }
            """;
        string blossomInit = """
            #include "module.h"
            void init(rack::plugin::Plugin *plugin) { plugin->addModel(rack::createModel<Module, Panel>("Blossom")); }
            """;

        ScanResult result = SourceScanner.ScanPlugin(manifest, new[]
        {
            new SourceFile("modules/blossom/control-ids.h", blossomIds),
            new SourceFile("modules/blossom/module.h", blossomModule),
            new SourceFile("modules/blossom/init.cpp", blossomInit),
            new SourceFile("modules/scannibal/control-ids.h", scannibalIds),
            new SourceFile("modules/scannibal/module.h", scannibalModule),
            new SourceFile("modules/scannibal/init.cpp", scannibalInit),
        });

        Assert.Equal(new[] { "A", "Phase", "Step 1 curvature CV", "Step 2 curvature CV", "Step 3 curvature CV", "Step 4 curvature CV" }, result.Modules["DHE-Modules/Scannibal4"].Inputs);
        Assert.Equal(new[] { "Spin speed cv" }, result.Modules["DHE-Modules/Blossom"].Inputs);
        Assert.Equal(new[] { "X" }, result.Modules["DHE-Modules/Blossom"].Outputs);
    }

    [Fact]
    public void ResolvesIndexOnlyPortsTypeMacrosAndCStrings()
    {
        string manifest = """{ "slug": "P", "modules": [ { "slug": "SS-112" }, { "slug": "SS-208" }, { "slug": "X4" }, { "slug": "Clock" }, { "slug": "WM-101" } ] }""";
        string source = """
            struct SS_112 : Module {
                static constexpr int deviceCount = 3;
                SS_112() : Module() {
                    config(0, deviceCount, 0, 0);
                    for (unsigned int i = 0; i < deviceCount; i++) { configInput(i, "Signal Sink"); }
                }
            };
            struct SS_208 : Module {
                static constexpr int deviceCount = 2;
                SS_208() { config(0, 0, deviceCount * 1, 0); configOutput(0, "\xcf\x80 V"); configOutput(1, "\xe2\x88\x9a" "2 V"); }
            };
            template<size_t MSIZE>
            struct XX : Module {
                enum InputId { M_INPUT, INPUTS_LEN = M_INPUT + MSIZE };
                enum OutputId { OUTPUTS_LEN = MSIZE };
                XX() {
                    config(PARAMS_LEN, INPUTS_LEN, OUTPUTS_LEN, LIGHTS_LEN);
                    for (int k = 0; k < MSIZE; k++) { configOutput(k, "Out" + std::to_string(k + 1)); }
                }
            };
            #define X4 XX<4>
            struct Clocked : Module {
                enum OutputIds { ENUMS(CLK_OUTPUTS, 2), NUM_OUTPUTS };
                Clocked() { configOutput(CLK_OUTPUTS + 0, "Master clock"); configOutput(CLK_OUTPUTS + 1, "Clock 1"); }
            };
            struct SizeableModule : Module { float size = 0; };
            Model *modelSS112 = createModel<SS_112, SS112>("SS-112");
            Model *modelSS208 = createModel<SS_208, SS208>("SS-208");
            Model* modelX4 = createModel<X4, X4Widget>("X4");
            Model* modelClocked = createModel<Clocked, ClockedWidget>("Clock");
            Model *modelWM101 = createModel<SizeableModule, WM101>("WM-101");
            """;

        ScanResult result = SourceScanner.ScanPlugin(manifest, new[] { new SourceFile("all.cpp", source) });

        Assert.Equal(new[] { "Signal Sink", "Signal Sink", "Signal Sink" }, result.Modules["P/SS-112"].Inputs);
        Assert.Equal(new[] { "π V", "√2 V" }, result.Modules["P/SS-208"].Outputs);
        Assert.Equal(4, result.Modules["P/X4"].Inputs.Count);
        Assert.Equal(new[] { "Out1", "Out2", "Out3", "Out4" }, result.Modules["P/X4"].Outputs);
        // "+ 0" isn't a loop: each port keeps its own name.
        Assert.Equal(new[] { "Master clock", "Clock 1" }, result.Modules["P/Clock"].Outputs);
        // No config() call: no ports, and not a problem.
        Assert.Empty(result.Modules["P/WM-101"].Inputs);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData(@"\xcf\x80 V", "π V")]
    [InlineData(@"tab\there", "tab\there")]
    [InlineData(@"\u00b0C", "°C")]
    [InlineData(@"Say \""hi\""", "Say \"hi\"")]
    public void DecodesCStringLiterals(string content, string expected)
    {
        Assert.Equal(expected, SourceScanner.CString(content));
    }

    [Fact]
    public void ResolvesConstantSizesTemplateArgsAndLoopNames()
    {
        string manifest = """{ "slug": "P", "modules": [ { "slug": "Mix" }, { "slug": "Drone" } ] }""";
        string source = """
            struct Voice { static const int NUM_OSC = 3; };
            template<int N_TRK, int N_GRP>
            struct Mixer : Module {
                enum InputIds { ENUMS(TRACK_INPUTS, N_TRK * 2), ENUMS(GROUP_INPUTS, N_GRP), NUM_INPUTS };
                enum OutputIds { MAIN_OUTPUT, NUM_OUTPUTS };
            };
            struct Drone : Module {
                static const int NUM_OSC = Voice::NUM_OSC;
                enum InputIds { ENUMS(TRIG_INPUT, NUM_OSC), GATE_INPUT, NUM_INPUTS };
                enum OutputIds { ENUMS(OUT_OUTPUT, NUM_OSC), NUM_OUTPUTS };
                Drone() {
                    static const char* labels[NUM_OSC] = { "Low", "Mid", "High" };
                    for (int i = 0; i < NUM_OSC; i++) {
                        configInput(TRIG_INPUT + i, string::f("Oscillator %d trigger", i + 1));
                        configOutput(OUT_OUTPUT + i, labels[i]);
                    }
                    configInput(GATE_INPUT, "Gate");
                }
            };
            Model* modelMix = createModel<Mixer<4, 1>, MixWidget>("Mix");
            Model* modelDrone = createModel<Drone, DroneWidget>("Drone");
            """;

        ScanResult result = SourceScanner.ScanPlugin(manifest, new[] { new SourceFile("all.cpp", source) });

        Assert.Equal(9, result.Modules["P/Mix"].Inputs.Count);
        Assert.Equal(new[] { "Oscillator 1 trigger", "Oscillator 2 trigger", "Oscillator 3 trigger", "Gate" }, result.Modules["P/Drone"].Inputs);
        Assert.Equal(new[] { "Low", "Mid", "High" }, result.Modules["P/Drone"].Outputs);
    }
}