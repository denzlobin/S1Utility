using System;
using System.Runtime.InteropServices;

namespace RolandS1Editor.Vst.Vst3;

// ── Vtable struct for IPluginFactory ────────────────────────────────────────
// Slot order must exactly match the VST3 SDK vtable (FUnknown first, then interface methods).

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Vtbl_IPluginFactory
{
    // FUnknown (slots 0-2)
    public delegate* unmanaged<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged<IntPtr, uint>                AddRef;
    public delegate* unmanaged<IntPtr, uint>                Release;
    // IPluginFactory (slots 3-6)
    public delegate* unmanaged<IntPtr, PFactoryInfo*, int>  GetFactoryInfo;
    public delegate* unmanaged<IntPtr, int>                 CountClasses;
    public delegate* unmanaged<IntPtr, int, PClassInfo*, int> GetClassInfo;
    public delegate* unmanaged<IntPtr, byte*, byte*, IntPtr*, int> CreateInstance;
}

// ── Vtable struct for IComponent ─────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Vtbl_IComponent
{
    // FUnknown (0-2)
    public delegate* unmanaged<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged<IntPtr, uint>                AddRef;
    public delegate* unmanaged<IntPtr, uint>                Release;
    // IPluginBase (3-4)
    public delegate* unmanaged<IntPtr, IntPtr, int>         Initialize;
    public delegate* unmanaged<IntPtr, int>                 Terminate;
    // IComponent (5-13)
    public delegate* unmanaged<IntPtr, byte*, int>          GetControllerClassId;
    public delegate* unmanaged<IntPtr, int, int>            SetIoMode;
    public delegate* unmanaged<IntPtr, int, int, int>       GetBusCount;
    public delegate* unmanaged<IntPtr, int, int, int, BusInfo*, int> GetBusInfo;
    public delegate* unmanaged<IntPtr, RoutingInfo*, RoutingInfo*, int> GetRoutingInfo;
    public delegate* unmanaged<IntPtr, int, int, int, byte, int> ActivateBus;
    public delegate* unmanaged<IntPtr, byte, int>           SetActive;
    public delegate* unmanaged<IntPtr, void*, int>          SetState;
    public delegate* unmanaged<IntPtr, void*, int>          GetState;
}

// ── Vtable struct for IAudioProcessor ────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Vtbl_IAudioProcessor
{
    // FUnknown (0-2)
    public delegate* unmanaged<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged<IntPtr, uint>                AddRef;
    public delegate* unmanaged<IntPtr, uint>                Release;
    // IAudioProcessor (3-10)
    public delegate* unmanaged<IntPtr, long*, int, long*, int, int> SetBusArrangements;
    public delegate* unmanaged<IntPtr, int, int, long*, int>        GetBusArrangement;
    public delegate* unmanaged<IntPtr, int, int>                    CanProcessSampleSize;
    public delegate* unmanaged<IntPtr, uint>                        GetLatencySamples;
    public delegate* unmanaged<IntPtr, ProcessSetup*, int>          SetupProcessing;
    public delegate* unmanaged<IntPtr, byte, int>                   SetProcessing;
    public delegate* unmanaged<IntPtr, ProcessData*, int>           Process;
    public delegate* unmanaged<IntPtr, uint>                        GetTailSamples;
}

// ── Vtable struct for IEditController ────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Vtbl_IEditController
{
    // FUnknown (0-2)
    public delegate* unmanaged<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged<IntPtr, uint>                AddRef;
    public delegate* unmanaged<IntPtr, uint>                Release;
    // IPluginBase (3-4)
    public delegate* unmanaged<IntPtr, IntPtr, int>         Initialize;
    public delegate* unmanaged<IntPtr, int>                 Terminate;
    // IEditController (5-17)
    public delegate* unmanaged<IntPtr, void*, int>          SetComponentState;
    public delegate* unmanaged<IntPtr, void*, int>          SetState;
    public delegate* unmanaged<IntPtr, void*, int>          GetState;
    public delegate* unmanaged<IntPtr, int>                 GetParameterCount;
    public delegate* unmanaged<IntPtr, int, ParameterInfo*, int> GetParameterInfo;
    public delegate* unmanaged<IntPtr, uint, double, char*, int> GetParamStringByValue;
    public delegate* unmanaged<IntPtr, uint, char*, double*, int> GetParamValueByString;
    public delegate* unmanaged<IntPtr, uint, double, double>     NormalizedParamToPlain;
    public delegate* unmanaged<IntPtr, uint, double, double>     PlainParamToNormalized;
    public delegate* unmanaged<IntPtr, uint, double>             GetParamNormalized;
    public delegate* unmanaged<IntPtr, uint, double, int>        SetParamNormalized;
    public delegate* unmanaged<IntPtr, IntPtr, int>              SetComponentHandler;
    public delegate* unmanaged<IntPtr, byte*, IntPtr>            CreateView;
}

// ── Vtable struct for IMidiMapping ────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Vtbl_IMidiMapping
{
    // FUnknown (0-2)
    public delegate* unmanaged<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged<IntPtr, uint>                AddRef;
    public delegate* unmanaged<IntPtr, uint>                Release;
    // IMidiMapping (3)
    // busIndex, channel, midiControllerNumber → paramId
    public delegate* unmanaged<IntPtr, int, int, int, uint*, int> GetMidiControllerAssignment;
}

// ── Vtable struct for IPlugView ───────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Vtbl_IPlugView
{
    // FUnknown (0-2)
    public delegate* unmanaged<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged<IntPtr, uint>                AddRef;
    public delegate* unmanaged<IntPtr, uint>                Release;
    // IPlugView (3-14)
    public delegate* unmanaged<IntPtr, byte*, int>          IsPlatformTypeSupported;
    public delegate* unmanaged<IntPtr, void*, byte*, int>   Attached;
    public delegate* unmanaged<IntPtr, int>                 Removed;
    public delegate* unmanaged<IntPtr, float, int>          OnWheel;
    public delegate* unmanaged<IntPtr, char, short, byte, int> OnKeyDown;
    public delegate* unmanaged<IntPtr, char, short, byte, int> OnKeyUp;
    public delegate* unmanaged<IntPtr, ViewRect*, int>      GetSize;
    public delegate* unmanaged<IntPtr, ViewRect*, int>      OnSize;
    public delegate* unmanaged<IntPtr, byte, int>           OnFocus;
    public delegate* unmanaged<IntPtr, IntPtr, int>         SetFrame;
    public delegate* unmanaged<IntPtr, int>                 CanResize;
    public delegate* unmanaged<IntPtr, ViewRect*, int>      CheckSizeConstraint;
}

// ── Memory layout of plugin objects ──────────────────────────────────────────

// Factory object layout: vtable pointer, then ref count.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FactoryObject
{
    public Vtbl_IPluginFactory* vtbl;
    public int refCount;
}

// Plugin object layout: four vtable pointers (one per interface), then state.
// QueryInterface returns the address of the matching vtable pointer field.
// Receivers recover the base by subtracting the field offset (see PluginObject.*From*).
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PluginObject
{
    public Vtbl_IComponent*    vtblComponent;   // offset  0 ← IComponent iface ptr
    public Vtbl_IAudioProcessor* vtblProcessor; // offset  8 ← IAudioProcessor iface ptr
    public Vtbl_IEditController* vtblController;// offset 16 ← IEditController iface ptr
    public Vtbl_IMidiMapping*  vtblMidiMap;     // offset 24 ← IMidiMapping iface ptr
    public int   refCount;                      // offset 32
    public IntPtr gcHandle;                     // offset 40 — GCHandle to PluginState

    // Helper: recover base pointer from each interface pointer.
    internal static PluginObject* FromComponent  (IntPtr p) => (PluginObject*)p;
    internal static PluginObject* FromProcessor  (IntPtr p) => (PluginObject*)((byte*)p -  8);
    internal static PluginObject* FromController (IntPtr p) => (PluginObject*)((byte*)p - 16);
    internal static PluginObject* FromMidiMap    (IntPtr p) => (PluginObject*)((byte*)p - 24);
}

// View object layout: vtable pointer, ref count, parent HWND, managed state handle.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ViewObject
{
    public Vtbl_IPlugView* vtbl;
    public int   refCount;
    public void* parentHwnd;
    public IntPtr gcHandle;  // GCHandle to PluginState (shared with plugin)
}
