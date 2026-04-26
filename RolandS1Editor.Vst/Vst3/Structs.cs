using System.Runtime.InteropServices;

namespace RolandS1Editor.Vst.Vst3;

// ── IPluginFactory data structures ──────────────────────────────────────────

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct PFactoryInfo
{
    public fixed byte vendor[64];   // ASCII
    public fixed byte url[256];     // ASCII
    public fixed byte email[128];   // ASCII
    public int        flags;        // kUnicode = 0x10

    internal const int kUnicode = 0x10;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct PClassInfo
{
    public fixed byte cid[16];       // FUID bytes
    public int        cardinality;   // 0x7FFFFFFF = kManyInstances
    public fixed byte category[32];  // ASCII, use "Audio Module Class"
    public fixed byte name[64];      // ASCII

    internal const int kManyInstances = 0x7FFFFFFF;
}

// ── IAudioProcessor data structures ─────────────────────────────────────────

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct ProcessSetup
{
    public int    processMode;         // 0=Realtime, 1=Prefetch, 2=Offline
    public int    symbolicSampleSize;  // 0=32-bit, 1=64-bit
    public int    maxSamplesPerBlock;
    public double sampleRate;
}

// Minimal ProcessData — only outputEvents is used; all other pointers are read-only or ignored.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct ProcessData
{
    public int   processMode;
    public int   symbolicSampleSize;
    public int   numSamples;
    public int   numInputs;
    public int   numOutputs;
    public void* inputs;
    public void* outputs;
    public void* inputParameterChanges;
    public void* outputParameterChanges;
    public void* inputEvents;
    public void* outputEvents;   // IEventList* — AddEvent here to send MIDI out
    public void* processContext;
}

// VST3 Event as written to IEventList.outputEvents.
[StructLayout(LayoutKind.Explicit, Pack = 1, Size = 40)]
internal struct Vst3Event
{
    [FieldOffset(0)]  public int    busIndex;
    [FieldOffset(4)]  public int    sampleOffset;
    [FieldOffset(8)]  public double ppqPosition;
    [FieldOffset(16)] public ushort flags;
    [FieldOffset(18)] public ushort type;       // kLegacyMidiCCOut = 65535
    // Union payload starts at offset 20 (size 20 bytes, padded to NoteOnEvent size).
    [FieldOffset(20)] public LegacyMidiCCOut midiCCOut;

    internal const ushort kLegacyMidiCCOut = 65535;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct LegacyMidiCCOut
{
    public byte  controlNumber;
    public sbyte value;
    public sbyte value2;    // unused, set 0
    public sbyte reserved;  // set 0
}

// ── IEditController data structures ─────────────────────────────────────────

// ParameterInfo.title / shortTitle / units use UTF-16 (char in C#).
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct ParameterInfo
{
    public uint  id;
    public fixed char title[128];       // UTF-16, 256 bytes
    public fixed char shortTitle[128];  // UTF-16, 256 bytes
    public fixed char units[128];       // UTF-16, 256 bytes
    public int   stepCount;             // 0=continuous, 1=toggle, n>1=discrete with n+1 states
    public double defaultNormalizedValue;
    public int   unitId;                // 0 = root unit
    public int   flags;                 // kCanAutomate=1, kIsReadOnly=2, kIsWrapAround=4

    internal const int kCanAutomate  = 1;
    internal const int kIsReadOnly   = 2;
}

// ── IPlugView data structures ────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ViewRect
{
    public int left, top, right, bottom;
}

// ── Bus structures (IComponent) ──────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct BusInfo
{
    public int   mediaType;    // 0=Audio, 1=Event(MIDI)
    public int   direction;    // 0=Input, 1=Output
    public int   channelCount;
    public fixed char name[128]; // UTF-16
    public int   busType;      // 0=Main, 1=Aux
    public uint  flags;        // kDefaultActive=1
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RoutingInfo
{
    public int mediaType;
    public int busIndex;
    public int channel;
}
