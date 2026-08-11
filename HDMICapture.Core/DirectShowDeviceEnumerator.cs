using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HDMICapture.Core
{
    public static class DirectShowDeviceEnumerator
    {
        public class VideoDevice
        {
            public string Name { get; set; } = string.Empty;
            public string MonikerString { get; set; } = string.Empty;
        }

        public static List<VideoDevice> GetVideoDevices()
        {
            var devices = new List<VideoDevice>();

            try
            {
                ICreateDevEnum? devEnum = null;
                IEnumMoniker? enumMoniker = null;
                IMoniker? moniker = null;

                try
                {
                    devEnum = (ICreateDevEnum)new CreateDevEnum();
                    int hr = devEnum.EnumDevices(DirectShowGuids.CLSID_VideoInputDeviceCategory, out enumMoniker);

                    if (hr < 0 || enumMoniker == null)
                        return devices;

                    while (enumMoniker.Next(1, out moniker, out int fetched) == 0 && fetched == 1)
                    {
                        try
                        {
                            moniker.GetDisplayName(null, null, out string deviceName);
                            moniker.GetDisplayName(null, null, out string monikerString);

                            devices.Add(new VideoDevice
                            {
                                Name = deviceName ?? "Unknown Device",
                                MonikerString = monikerString ?? string.Empty
                            });
                        }
                        finally
                        {
                            if (moniker != null)
                                Marshal.ReleaseComObject(moniker);
                        }
                    }
                }
                finally
                {
                    if (enumMoniker != null)
                        Marshal.ReleaseComObject(enumMoniker);
                    if (devEnum != null)
                        Marshal.ReleaseComObject(devEnum);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error enumerating video devices: {ex.Message}");
            }

            return devices;
        }
    }

    [ComImport, Guid("00000100-0000-0000-C000-000000000046")]
    internal interface IEnumMoniker
    {
        [PreserveSig]
        int Next(int celt, out IMoniker ppMoniker, out int pceltFetched);

        [PreserveSig]
        int Skip(int celt);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumMoniker ppEnum);
    }

    [ComImport, Guid("0000010c-0000-0000-C000-000000000046")]
    internal interface IMoniker
    {
        [PreserveSig]
        int GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load(IStream pStm);

        [PreserveSig]
        int Save(IStream pStm, bool fClearDirty);

        [PreserveSig]
        int GetSizeMax(out long pcbSize);

        [PreserveSig]
        int BindToObject(IBindCtx pbc, IMoniker pmkToLeft, ref Guid riidResult, out object ppvResult);

        [PreserveSig]
        int BindToStorage(IBindCtx pbc, IMoniker pmkToLeft, ref Guid riid, out object ppvUnk);

        [PreserveSig]
        int Reduce(IBindCtx pbc, int dwReduceHowFar, ref IMoniker ppmkToLeft, out IMoniker ppmkReduced);

        [PreserveSig]
        int ComposeWith(IMoniker pmkRight, bool fOnlyIfNotGeneric, out IMoniker ppmkComposite);

        [PreserveSig]
        int Enum(bool fForward, out IEnumMoniker ppenumMoniker);

        [PreserveSig]
        int IsEqual(IMoniker pmkOtherMoniker);

        [PreserveSig]
        int Hash(out int pdwHash);

        [PreserveSig]
        int IsRunning(IBindCtx pbc, IMoniker pmkToLeft, IMoniker pmkNewlyRunning);

        [PreserveSig]
        int GetTimeOfLastChange(IBindCtx pbc, IMoniker pmkToLeft, out long pFileTime);

        void Inverse(out IMoniker ppmk);

        [PreserveSig]
        int CommonPrefixWith(IMoniker pmkOther, out IMoniker ppmkPrefix);

        [PreserveSig]
        int RelativePathTo(IMoniker pmkOther, out IMoniker ppmkRelPath);

        [PreserveSig]
        int GetDisplayName(IBindCtx pbc, IMoniker pmkToLeft, out string ppszDisplayName);

        [PreserveSig]
        int ParseDisplayName(IBindCtx pbc, IMoniker pmkToLeft, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out int pchEaten, out IMoniker ppmkOut);

        [PreserveSig]
        int IsSystemMoniker(out int pdwMksys);
    }

    [ComImport, Guid("0000000c-0000-0000-C000-000000000046")]
    internal interface IStream { }

    [ComImport, Guid("0000000E-0000-0000-C000-000000000046")]
    internal interface IBindCtx { }

}
