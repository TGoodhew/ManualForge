#:property TargetFramework=net10.0-windows
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false
// #89: drives ManualForge.ExplorerMenu.dll the way Explorer does, without registering any package: loads it,
// asks it for its command, reads the title, icon and state Explorer would show, and, given files, invokes it
// on them, which starts ManualForge.App.exe from the DLL's own folder with --add and those paths.
// usage:
//   dotnet run tools/probe-explorer-menu.cs -- <folder with the DLL and ManualForge.App.exe> [file...]
using System.Runtime.InteropServices;

var folder = Path.GetFullPath(args[0]);
var dll = NativeLibrary.Load(Path.Combine(folder, "ManualForge.ExplorerMenu.dll"));
var getClassObject = Marshal.GetDelegateForFunctionPointer<DllGetClassObject>(NativeLibrary.GetExport(dll, "DllGetClassObject"));

var clsid = new Guid("4930d037-f987-4d71-ad01-63a7ea88a20c");
var classFactoryId = typeof(IClassFactory).GUID;
Marshal.ThrowExceptionForHR(getClassObject(ref clsid, ref classFactoryId, out var factoryObject));
var factory = (IClassFactory)factoryObject;
var commandId = typeof(IExplorerCommand).GUID;
factory.CreateInstance(null, ref commandId, out var commandObject);
var command = (IExplorerCommand)commandObject;

command.GetTitle(null, out var title);
command.GetIcon(null, out var icon);
command.GetState(null, false, out var state);
command.GetCanonicalName(out var canonical);
Console.WriteLine($"Title     : {title}");
Console.WriteLine($"Icon      : {icon}");
Console.WriteLine($"State     : {state} (0 is enabled)");
Console.WriteLine($"Canonical : {canonical}");

if (args.Length < 2)
    return;

var items = args.Skip(1).Select(f => SHCreateItemFromParsingName(Path.GetFullPath(f), IntPtr.Zero, typeof(IShellItem).GUID)).ToArray();
var array = SHCreateShellItemArrayFromItems(items);
command.Invoke(array, null);
Console.WriteLine($"Invoked on {items.Length} item(s).");

static IShellItemArray SHCreateShellItemArrayFromItems(object[] items)
{
    var idLists = items.Select(i => { SHGetIDListFromObject(i, out var pidl); return pidl; }).ToArray();
    Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)idLists.Length, idLists, out var array));
    return array;
}

[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
[return: MarshalAs(UnmanagedType.Interface)]
static extern object SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid);

[DllImport("shell32.dll", PreserveSig = false)]
static extern void SHGetIDListFromObject([MarshalAs(UnmanagedType.IUnknown)] object item, out IntPtr pidl);

[DllImport("shell32.dll")]
static extern int SHCreateShellItemArrayFromIDLists(uint count, IntPtr[] idLists, out IShellItemArray array);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int DllGetClassObject(ref Guid clsid, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IClassFactory
{
    void CreateInstance([MarshalAs(UnmanagedType.IUnknown)] object? outer, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    void LockServer(bool lockServer);
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItem { }

[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItemArray { }

[ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IExplorerCommand
{
    void GetTitle(IShellItemArray? items, [MarshalAs(UnmanagedType.LPWStr)] out string title);
    void GetIcon(IShellItemArray? items, [MarshalAs(UnmanagedType.LPWStr)] out string icon);
    void GetToolTip(IShellItemArray? items, [MarshalAs(UnmanagedType.LPWStr)] out string tip);
    void GetCanonicalName(out Guid name);
    void GetState(IShellItemArray? items, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out uint state);
    void Invoke(IShellItemArray? items, [MarshalAs(UnmanagedType.IUnknown)] object? bindContext);
    void GetFlags(out uint flags);
    void EnumSubCommands(out IntPtr commands);
}
