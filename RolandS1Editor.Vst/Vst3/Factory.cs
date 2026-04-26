using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace RolandS1Editor.Vst.Vst3;

// IPluginFactory implementation + the DLL entry point exported as GetPluginFactory.
// NativeAOT publishes this as a native DLL; the GetPluginFactory export is the only
// symbol a DAW needs.

internal static unsafe class Factory
{
    // ── Static vtable + factory object (one per DLL, never freed) ────────────

    private static Vtbl_IPluginFactory s_vtbl;
    private static FactoryObject       s_obj;

    static Factory()
    {
        s_vtbl.QueryInterface = &QI;
        s_vtbl.AddRef         = &AddRef;
        s_vtbl.Release        = &Release;
        s_vtbl.GetFactoryInfo = &GetFactoryInfo;
        s_vtbl.CountClasses   = &CountClasses;
        s_vtbl.GetClassInfo   = &GetClassInfo;
        s_vtbl.CreateInstance = &CreateInstance;

        s_obj.vtbl     = (Vtbl_IPluginFactory*)Unsafe.AsPointer(ref s_vtbl);
        s_obj.refCount = 1;
    }

    // ── DLL export ────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(EntryPoint = "GetPluginFactory")]
    public static IntPtr GetPluginFactory() =>
        (IntPtr)Unsafe.AsPointer(ref s_obj);

    // ── IPluginFactory implementation ─────────────────────────────────────────

    private static uint DoAddRef(IntPtr self) =>
        (uint)System.Threading.Interlocked.Increment(ref ((FactoryObject*)self)->refCount);

    [UnmanagedCallersOnly]
    private static int QI(IntPtr self, Guid* iid, IntPtr* obj)
    {
        if (*iid == Ids.FUnknown || *iid == Ids.IPluginFactory)
        {
            *obj = self;
            DoAddRef(self);
            return Vst3Result.kResultOk;
        }
        *obj = IntPtr.Zero;
        return Vst3Result.kNoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(IntPtr self) => DoAddRef(self);

    [UnmanagedCallersOnly]
    private static uint Release(IntPtr self)
    {
        var o = (FactoryObject*)self;
        return (uint)System.Threading.Interlocked.Decrement(ref o->refCount);
        // s_obj is static — never actually freed
    }

    [UnmanagedCallersOnly]
    private static int GetFactoryInfo(IntPtr _, PFactoryInfo* info)
    {
        FillAscii(info->vendor, 64, "Your Name / Studio");
        FillAscii(info->url,   256, "");
        FillAscii(info->email, 128, "");
        info->flags = PFactoryInfo.kUnicode;
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int CountClasses(IntPtr _) => 1;

    [UnmanagedCallersOnly]
    private static int GetClassInfo(IntPtr _, int index, PClassInfo* info)
    {
        if (index != 0) return Vst3Result.kResultFalse;

        // Write CID bytes (Windows GUID memory order)
        var cidBytes = Ids.PluginCid.ToByteArray();
        for (int i = 0; i < 16; i++) info->cid[i] = cidBytes[i];

        info->cardinality = PClassInfo.kManyInstances;
        FillAscii(info->category, 32, "Audio Module Class");
        FillAscii(info->name,     64, "Roland S-1 Editor");
        return Vst3Result.kResultOk;
    }

    [UnmanagedCallersOnly]
    private static int CreateInstance(IntPtr _, byte* cid, byte* iid, IntPtr* obj)
    {
        // Verify requested CID matches our plugin
        var cidBytes = Ids.PluginCid.ToByteArray();
        for (int i = 0; i < 16; i++)
            if (cid[i] != cidBytes[i]) { *obj = IntPtr.Zero; return Vst3Result.kNoInterface; }

        var instance = PluginObjectImpl.Allocate();
        var guidIid  = *(Guid*)iid;
        return PluginObjectImpl.QueryInterfaceOn(instance, &guidIid, obj);
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    internal static void FillAscii(byte* dst, int size, string src)
    {
        var bytes = Encoding.ASCII.GetBytes(src);
        int len = Math.Min(bytes.Length, size - 1);
        for (int i = 0; i < len; i++) dst[i] = bytes[i];
        dst[len] = 0;
    }

    internal static void FillUtf16(char* dst, int count, string src)
    {
        int len = Math.Min(src.Length, count - 1);
        for (int i = 0; i < len; i++) dst[i] = src[i];
        dst[len] = '\0';
    }
}
