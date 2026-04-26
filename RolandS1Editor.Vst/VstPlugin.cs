using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RolandS1Editor.Vst.Vst3;

namespace RolandS1Editor.Vst;

// Managed state shared by all four COM interfaces on the same plugin instance.
internal sealed class PluginState
{
    internal readonly S1Patch          Patch     = new();
    internal readonly VstMidiTransport Transport = new();
    internal          IntPtr           ComponentHandler; // IComponentHandler* from DAW

    internal PluginState() => Patch.SetTransport(Transport, channel: 1);
}

// All [UnmanagedCallersOnly] methods for the combined
// IComponent + IAudioProcessor + IEditController + IMidiMapping object.
// Kept in a separate static class so the name doesn't conflict with the
// PluginObject struct defined in Vtables.cs.
internal static unsafe class PluginObjectImpl
{
    private static Vtbl_IComponent      s_vtblComponent;
    private static Vtbl_IAudioProcessor s_vtblProcessor;
    private static Vtbl_IEditController s_vtblController;
    private static Vtbl_IMidiMapping    s_vtblMidiMap;

    static PluginObjectImpl()
    {
        s_vtblComponent.QueryInterface       = &QI_Component;
        s_vtblComponent.AddRef               = &AddRef_Component;
        s_vtblComponent.Release              = &Release_Component;
        s_vtblComponent.Initialize           = &Initialize;
        s_vtblComponent.Terminate            = &Terminate;
        s_vtblComponent.GetControllerClassId = &GetControllerClassId;
        s_vtblComponent.SetIoMode            = &SetIoMode;
        s_vtblComponent.GetBusCount          = &GetBusCount;
        s_vtblComponent.GetBusInfo           = &GetBusInfo;
        s_vtblComponent.GetRoutingInfo       = &GetRoutingInfo;
        s_vtblComponent.ActivateBus          = &ActivateBus;
        s_vtblComponent.SetActive            = &SetActive;
        s_vtblComponent.SetState             = &SetState_Component;
        s_vtblComponent.GetState             = &GetState_Component;

        s_vtblProcessor.QueryInterface       = &QI_Processor;
        s_vtblProcessor.AddRef               = &AddRef_Processor;
        s_vtblProcessor.Release              = &Release_Processor;
        s_vtblProcessor.SetBusArrangements   = &SetBusArrangements;
        s_vtblProcessor.GetBusArrangement    = &GetBusArrangement;
        s_vtblProcessor.CanProcessSampleSize = &CanProcessSampleSize;
        s_vtblProcessor.GetLatencySamples    = &GetLatencySamples;
        s_vtblProcessor.SetupProcessing      = &SetupProcessing;
        s_vtblProcessor.SetProcessing        = &SetProcessing;
        s_vtblProcessor.Process              = &Process;
        s_vtblProcessor.GetTailSamples       = &GetTailSamples;

        s_vtblController.QueryInterface      = &QI_Controller;
        s_vtblController.AddRef              = &AddRef_Controller;
        s_vtblController.Release             = &Release_Controller;
        s_vtblController.Initialize          = &Initialize;
        s_vtblController.Terminate           = &Terminate;
        s_vtblController.SetComponentState   = &SetComponentState;
        s_vtblController.SetState            = &SetState_Controller;
        s_vtblController.GetState            = &GetState_Controller;
        s_vtblController.GetParameterCount   = &GetParameterCount;
        s_vtblController.GetParameterInfo    = &GetParameterInfo;
        s_vtblController.GetParamStringByValue  = &GetParamStringByValue;
        s_vtblController.GetParamValueByString  = &GetParamValueByString;
        s_vtblController.NormalizedParamToPlain = &NormalizedParamToPlain;
        s_vtblController.PlainParamToNormalized = &PlainParamToNormalized;
        s_vtblController.GetParamNormalized  = &GetParamNormalized;
        s_vtblController.SetParamNormalized  = &SetParamNormalized;
        s_vtblController.SetComponentHandler = &SetComponentHandler;
        s_vtblController.CreateView          = &CreateView;

        s_vtblMidiMap.QueryInterface              = &QI_MidiMap;
        s_vtblMidiMap.AddRef                      = &AddRef_MidiMap;
        s_vtblMidiMap.Release                     = &Release_MidiMap;
        s_vtblMidiMap.GetMidiControllerAssignment = &GetMidiControllerAssignment;
    }

    // ── Allocation / QueryInterface ───────────────────────────────────────────

    internal static PluginObject* Allocate()
    {
        var obj = (PluginObject*)NativeMemory.AllocZeroed((nuint)sizeof(PluginObject));
        obj->vtblComponent  = (Vtbl_IComponent*)    Unsafe.AsPointer(ref s_vtblComponent);
        obj->vtblProcessor  = (Vtbl_IAudioProcessor*)Unsafe.AsPointer(ref s_vtblProcessor);
        obj->vtblController = (Vtbl_IEditController*)Unsafe.AsPointer(ref s_vtblController);
        obj->vtblMidiMap    = (Vtbl_IMidiMapping*)   Unsafe.AsPointer(ref s_vtblMidiMap);
        obj->refCount       = 1;
        obj->gcHandle       = GCHandle.ToIntPtr(GCHandle.Alloc(new PluginState()));
        return obj;
    }

    internal static int QueryInterfaceOn(PluginObject* obj, Guid* iid, IntPtr* result)
    {
        if (*iid == Ids.FUnknown || *iid == Ids.IPluginBase || *iid == Ids.IComponent)
            *result = (IntPtr)(&obj->vtblComponent);
        else if (*iid == Ids.IAudioProcessor)
            *result = (IntPtr)(&obj->vtblProcessor);
        else if (*iid == Ids.IEditController)
            *result = (IntPtr)(&obj->vtblController);
        else if (*iid == Ids.IMidiMapping)
            *result = (IntPtr)(&obj->vtblMidiMap);
        else
        {
            *result = IntPtr.Zero;
            return Vst3Result.kNoInterface;
        }
        System.Threading.Interlocked.Increment(ref obj->refCount);
        return Vst3Result.kResultOk;
    }

    private static PluginState GetState(PluginObject* obj) =>
        (PluginState)GCHandle.FromIntPtr(obj->gcHandle).Target!;

    private static void DoRelease(PluginObject* obj)
    {
        int v = System.Threading.Interlocked.Decrement(ref obj->refCount);
        if (v == 0)
        {
            GCHandle.FromIntPtr(obj->gcHandle).Free();
            obj->gcHandle = IntPtr.Zero;
            NativeMemory.Free(obj);
        }
    }

    // ── QI / AddRef / Release ─────────────────────────────────────────────────

    [UnmanagedCallersOnly] private static int  QI_Component (IntPtr p, Guid* iid, IntPtr* r) => QueryInterfaceOn(PluginObject.FromComponent(p),  iid, r);
    [UnmanagedCallersOnly] private static int  QI_Processor (IntPtr p, Guid* iid, IntPtr* r) => QueryInterfaceOn(PluginObject.FromProcessor(p),  iid, r);
    [UnmanagedCallersOnly] private static int  QI_Controller(IntPtr p, Guid* iid, IntPtr* r) => QueryInterfaceOn(PluginObject.FromController(p), iid, r);
    [UnmanagedCallersOnly] private static int  QI_MidiMap   (IntPtr p, Guid* iid, IntPtr* r) => QueryInterfaceOn(PluginObject.FromMidiMap(p),    iid, r);

    [UnmanagedCallersOnly] private static uint AddRef_Component (IntPtr p) => (uint)System.Threading.Interlocked.Increment(ref PluginObject.FromComponent(p)->refCount);
    [UnmanagedCallersOnly] private static uint AddRef_Processor (IntPtr p) => (uint)System.Threading.Interlocked.Increment(ref PluginObject.FromProcessor(p)->refCount);
    [UnmanagedCallersOnly] private static uint AddRef_Controller(IntPtr p) => (uint)System.Threading.Interlocked.Increment(ref PluginObject.FromController(p)->refCount);
    [UnmanagedCallersOnly] private static uint AddRef_MidiMap   (IntPtr p) => (uint)System.Threading.Interlocked.Increment(ref PluginObject.FromMidiMap(p)->refCount);

    [UnmanagedCallersOnly] private static uint Release_Component (IntPtr p) { DoRelease(PluginObject.FromComponent(p));  return 0; }
    [UnmanagedCallersOnly] private static uint Release_Processor (IntPtr p) { DoRelease(PluginObject.FromProcessor(p));  return 0; }
    [UnmanagedCallersOnly] private static uint Release_Controller(IntPtr p) { DoRelease(PluginObject.FromController(p)); return 0; }
    [UnmanagedCallersOnly] private static uint Release_MidiMap   (IntPtr p) { DoRelease(PluginObject.FromMidiMap(p));    return 0; }

    // ── IPluginBase ───────────────────────────────────────────────────────────

    [UnmanagedCallersOnly]
    private static int Initialize(IntPtr p, IntPtr hostContext) => Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static int Terminate(IntPtr p)
    {
        GetState(PluginObject.FromComponent(p)).Patch.Dispose();
        return Vst3Result.kResultOk;
    }

    // ── IComponent ────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly]
    private static int GetControllerClassId(IntPtr p, byte* classId)
    {
        var bytes = Ids.ControllerCid.ToByteArray();
        for (int i = 0; i < 16; i++) classId[i] = bytes[i];
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int SetIoMode(IntPtr p, int mode) => Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static int GetBusCount(IntPtr p, int mediaType, int dir) =>
        mediaType == 1 ? 1 : 0;   // 1 Event bus (MIDI), no audio

    [UnmanagedCallersOnly]
    private static int GetBusInfo(IntPtr p, int mediaType, int dir, int index, BusInfo* info)
    {
        if (mediaType != 1 || index != 0) return Vst3Result.kResultFalse;
        info->mediaType    = 1; // Event
        info->direction    = dir;
        info->channelCount = 1;
        info->busType      = 0; // Main
        info->flags        = 1; // kDefaultActive
        Factory.FillUtf16(info->name, 128, dir == 1 ? "MIDI Out" : "MIDI In");
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int GetRoutingInfo(IntPtr p, RoutingInfo* inInfo, RoutingInfo* outInfo) =>
        Vst3Result.kResultFalse;

    [UnmanagedCallersOnly]
    private static int ActivateBus(IntPtr p, int mediaType, int dir, int index, byte state) =>
        Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static int SetActive(IntPtr p, byte state) => Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static int SetState_Component(IntPtr p, void* stream)
    {
        // TODO: deserialise patch state from IBStream (for DAW session recall)
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int GetState_Component(IntPtr p, void* stream)
    {
        // TODO: serialise patch state into IBStream (for DAW session save)
        return Vst3Result.kResultOk;
    }

    // ── IAudioProcessor ───────────────────────────────────────────────────────

    [UnmanagedCallersOnly]
    private static int SetBusArrangements(IntPtr p, long* inputs, int numIn, long* outputs, int numOut) =>
        Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static int GetBusArrangement(IntPtr p, int dir, int index, long* arr)
    {
        *arr = 0; return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int CanProcessSampleSize(IntPtr p, int size) => Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static uint GetLatencySamples(IntPtr p) => 0;

    [UnmanagedCallersOnly]
    private static int SetupProcessing(IntPtr p, ProcessSetup* setup) => Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static int SetProcessing(IntPtr p, byte state) => Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static int Process(IntPtr p, ProcessData* data)
    {
        var transport = GetState(PluginObject.FromProcessor(p)).Transport;
        if (data->outputEvents != null)
            transport.FlushToEventList(data->outputEvents);
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static uint GetTailSamples(IntPtr p) => 0;

    // ── IEditController ───────────────────────────────────────────────────────

    [UnmanagedCallersOnly]
    private static int SetComponentState(IntPtr p, void* stream)  => Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static int SetState_Controller(IntPtr p, void* stream) => Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static int GetState_Controller(IntPtr p, void* stream) => Vst3Result.kResultOk;

    [UnmanagedCallersOnly]
    private static int GetParameterCount(IntPtr p) =>
        GetState(PluginObject.FromController(p)).Patch.AllParameters.Count;

    [UnmanagedCallersOnly]
    private static int GetParameterInfo(IntPtr p, int idx, ParameterInfo* info)
    {
        var patch = GetState(PluginObject.FromController(p)).Patch;
        if ((uint)idx >= (uint)patch.AllParameters.Count) return Vst3Result.kResultFalse;

        var param = patch.AllParameters[idx];
        info->id                     = (uint)param.CcNumber;
        info->stepCount              = 0;  // continuous
        info->defaultNormalizedValue = param.Value / 127.0;
        info->unitId                 = 0;
        info->flags                  = ParameterInfo.kCanAutomate;
        Factory.FillUtf16(info->title,      128, param.Name);
        Factory.FillUtf16(info->shortTitle, 128, param.Name.Length > 8 ? param.Name[..8] : param.Name);
        Factory.FillUtf16(info->units,      128, "");
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int GetParamStringByValue(IntPtr p, uint id, double norm, char* str)
    {
        Factory.FillUtf16(str, 128, ((int)Math.Round(norm * 127)).ToString());
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int GetParamValueByString(IntPtr p, uint id, char* str, double* norm)
    {
        if (!int.TryParse(new string(str), out int v)) return Vst3Result.kResultFalse;
        *norm = Math.Clamp(v, 0, 127) / 127.0;
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static double NormalizedParamToPlain(IntPtr p, uint id, double norm) =>
        Math.Round(norm * 127);

    [UnmanagedCallersOnly]
    private static double PlainParamToNormalized(IntPtr p, uint id, double plain) =>
        Math.Clamp(plain, 0, 127) / 127.0;

    [UnmanagedCallersOnly]
    private static double GetParamNormalized(IntPtr p, uint id) =>
        (GetState(PluginObject.FromController(p)).Patch.GetByCC((int)id)?.Value ?? 0) / 127.0;

    [UnmanagedCallersOnly]
    private static int SetParamNormalized(IntPtr p, uint id, double norm)
    {
        var param = GetState(PluginObject.FromController(p)).Patch.GetByCC((int)id);
        if (param == null) return Vst3Result.kResultFalse;
        param.Value = (int)Math.Round(norm * 127);
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int SetComponentHandler(IntPtr p, IntPtr handler)
    {
        GetState(PluginObject.FromController(p)).ComponentHandler = handler;
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static IntPtr CreateView(IntPtr p, byte* name)
    {
        // TODO: allocate a ViewObject, start Avalonia UI inside the HWND provided by
        //       IPlugView.Attached(), and return the ViewObject COM pointer.
        return IntPtr.Zero;
    }

    // ── IMidiMapping ──────────────────────────────────────────────────────────

    // Maps incoming MIDI CC to the corresponding VST3 parameter ID so DAWs can
    // optionally record CC moves as automation lanes.
    [UnmanagedCallersOnly]
    private static int GetMidiControllerAssignment(IntPtr p, int bus, int ch, int cc, uint* paramId)
    {
        if (GetState(PluginObject.FromMidiMap(p)).Patch.GetByCC(cc) == null)
            return Vst3Result.kResultFalse;
        *paramId = (uint)cc;
        return Vst3Result.kResultOk;
    }
}
