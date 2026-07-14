using System.Runtime.InteropServices;

namespace Tracker.Watcher;

/// <summary>
/// Reads a window's AppUserModelID via SHGetPropertyStoreForWindow + PKEY_AppUserModel_ID.
/// Chrome/Edge set a DISTINCT AUMID per browser profile (taskbar grouping) — the native
/// profile→project signal (decision #12). Default profile carries only the BaseAppId
/// (no suffix), so it is disambiguated by the extension label instead.
/// </summary>
internal static class AumidReader
{
    private const ushort VT_LPWSTR = 31;

    private static readonly Guid IidIPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    private static PROPERTYKEY _pkeyAppUserModelId = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    public static string TryGet(IntPtr hwnd)
    {
        try
        {
            var iid = IidIPropertyStore;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null)
                return "";
            try
            {
                if (store.GetValue(ref _pkeyAppUserModelId, out var pv) != 0)
                    return "";
                try
                {
                    return pv.vt == VT_LPWSTR && pv.pwszVal != IntPtr.Zero
                        ? Marshal.PtrToStringUni(pv.pwszVal) ?? ""
                        : "";
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception)
        {
            return ""; // never let shell interop take the watcher down
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pwszVal;
        public IntPtr reserved;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        [PreserveSig] int Commit();
    }
}
