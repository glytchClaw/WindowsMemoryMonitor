using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace MemoryWidget;

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000001-0000-0000-C000-000000000046")]
internal interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
    [PreserveSig] int LockServer(bool fLock);
}

[ComVisible(true)]
internal sealed class WidgetProviderFactory : IClassFactory
{
    const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero) return CLASS_E_NOAGGREGATION;

        // Hand back whichever interface the host asked for (IUnknown, IInspectable or IWidgetProvider).
        IntPtr provider = MarshalInspectable<IWidgetProvider>.FromManaged(new WidgetProvider());
        try
        {
            return Marshal.QueryInterface(provider, in riid, out ppvObject);
        }
        finally
        {
            Marshal.Release(provider);
        }
    }

    public int LockServer(bool fLock) => 0;
}

internal static class ClassObject
{
    const uint CLSCTX_LOCAL_SERVER = 0x4;
    const uint REGCLS_MULTIPLEUSE = 0x1;

    [DllImport("ole32.dll")]
    static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);

    [DllImport("ole32.dll")]
    static extern int CoRevokeClassObject(uint dwRegister);

    public static void Register(Guid clsid, object factory, out uint cookie)
    {
        int hr = CoRegisterClassObject(clsid, factory, CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out cookie);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static void Revoke(uint cookie) => CoRevokeClassObject(cookie);
}
