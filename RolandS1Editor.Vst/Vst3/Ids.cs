using System;

namespace RolandS1Editor.Vst.Vst3;

// VST3 interface FUIDs.  On Windows these are stored in Windows GUID byte order
// (Data1/2/3 little-endian, Data4 big-endian), which matches System.Guid exactly.
internal static class Ids
{
    // Standard VST3 SDK interfaces
    internal static readonly Guid FUnknown          = new("00000000-0000-0000-C000-000000000046");
    internal static readonly Guid IPluginBase        = new("22888DDB-156E-45AE-8358-B34808190625");
    internal static readonly Guid IPluginFactory     = new("7A4D811C-5211-4A1F-AED9-D2EE0B43BF9F");
    internal static readonly Guid IComponent         = new("E831FF31-F2D5-4301-928E-BBEE25697802");
    internal static readonly Guid IAudioProcessor    = new("42043F99-B7DA-453C-A569-E79D9AAEC33D");
    internal static readonly Guid IEditController    = new("DCD7BBE3-7742-448D-A874-AACC979C759E");
    internal static readonly Guid IMidiMapping       = new("DF0FF9F7-49B7-4669-B63A-B7327ADBF5E5");
    internal static readonly Guid IPlugView          = new("5BC32507-D060-49EA-A615-1B522B755B29");
    internal static readonly Guid IComponentHandler  = new("93A0BEA3-0BD0-45DB-8E89-0B0CC1E46AC6");

    // This plugin's class IDs — must be stable across plugin versions.
    // PluginCid is what the factory advertises; ControllerCid is returned by
    // IComponent.GetControllerClassId (we use a single combined object, so they
    // can be the same, but keeping them separate is the VST3 convention).
    internal static readonly Guid PluginCid     = new("3E7A8B9C-4D5E-6F70-8192-A3B4C5D6E7F8");
    internal static readonly Guid ControllerCid = new("4F8A9B0C-5E6F-7080-9203-B4C5D6E7F809");
}

// VST3 result codes (tresult).
internal static class Vst3Result
{
    internal const int kResultOk      = 0;
    internal const int kResultTrue    = 0;
    internal const int kResultFalse   = 1;
    internal const int kNoInterface   = unchecked((int)0x80000004);
    internal const int kInvalidArg    = unchecked((int)0x80000003);
    internal const int kNotImplemented = unchecked((int)0x80000006);
}
