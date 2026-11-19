using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;

namespace AudioTastic.Platform.Windows;

/// <summary>
/// SI-02 / SI-07: a Start menu shortcut that carries the app's AppUserModelID. Windows names an unpackaged app in the
/// media flyout and in notifications through such a shortcut ("Unknown app" without one). Written with the shell's
/// ShellLink object through source-generated COM (the app has built-in COM interop switched off).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class StartMenuShortcut
{
    private static readonly Guid ShellLinkClass = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid AppUserModelIdFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint AppUserModelIdProperty = 5;
    private const ushort VtLpwstr = 31;

    /// <summary>The per-user Start menu programs folder.</summary>
    public static string ProgramsFolder => Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    /// <summary>Writes (or replaces) <paramref name="shortcutPath"/> pointing at <paramref name="target"/> with the given AppUserModelID.</summary>
    public static void Write(string shortcutPath, string target, string appId, string description)
    {
        var comWrappers = new StrategyBasedComWrappers();
        var classId = ShellLinkClass;
        var linkInterface = typeof(IShellLinkW).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(in classId, 0, ClsctxInprocServer, in linkInterface, out var pointer));
        try
        {
            var link = (IShellLinkW)comWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? string.Empty);
            link.SetDescription(description);
            link.SetIconLocation(target, 0);

            var store = (IPropertyStore)link;
            var key = new PropertyKey { FormatId = AppUserModelIdFormat, PropertyId = AppUserModelIdProperty };
            var value = new PropVariant { Type = VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(appId) };
            try
            {
                store.SetValue(in key, in value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.Pointer);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Reads the target and AppUserModelID of a shortcut (tests, and to skip rewriting an unchanged one).</summary>
    public static (string Target, string? AppId) Read(string shortcutPath)
    {
        var comWrappers = new StrategyBasedComWrappers();
        var classId = ShellLinkClass;
        var linkInterface = typeof(IShellLinkW).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(in classId, 0, ClsctxInprocServer, in linkInterface, out var pointer));
        try
        {
            var link = (IShellLinkW)comWrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
            ((IPersistFile)link).Load(shortcutPath, 0);
            var buffer = new char[1024];
            link.GetPath(buffer, buffer.Length, 0, 0);
            var target = new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length);
            var key = new PropertyKey { FormatId = AppUserModelIdFormat, PropertyId = AppUserModelIdProperty };
            ((IPropertyStore)link).GetValue(in key, out var value);
            string? appId = null;
            if (value.Type == VtLpwstr && value.Pointer != 0)
            {
                appId = Marshal.PtrToStringUni(value.Pointer);
                Marshal.FreeCoTaskMem(value.Pointer);
            }
            return (target, appId);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private const uint ClsctxInprocServer = 1;

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>PROPVARIANT, enough for a VT_LPWSTR (24 bytes on 64-bit, 16 on 32-bit, like the native union).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Pointer;
        public nint Extra;
    }

    [GeneratedComInterface]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] [Out] char[] file, int size, nint findData, uint flags);
        void GetIDList(out nint list);
        void SetIDList(nint list);
        void GetDescription(nint name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(nint dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(nint args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(nint iconPath, int size, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [GeneratedComInterface]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile(out nint fileName);
    }

    [GeneratedComInterface]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropVariant value);
        void SetValue(in PropertyKey key, in PropVariant value);
        void Commit();
    }
}
