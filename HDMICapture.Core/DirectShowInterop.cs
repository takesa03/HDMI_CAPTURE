using System;
using System.Runtime.InteropServices;

namespace HDMICapture.Core
{
    // DirectShow COM interfaces and enums
    [ComImport, Guid("56a86891-0ad4-11ce-b03a-0020af0ba770")]
    internal interface IFilterGraph { }

    [ComImport, Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770")]
    internal interface IGraphBuilder : IFilterGraph
    {
        [PreserveSig]
        int Connect(IPin ppinOut, IPin ppinIn);

        [PreserveSig]
        int Render(IPin ppinOut);

        [PreserveSig]
        int RenderFile(string lpcwstrFile, string lpcwstrPlayList);

        [PreserveSig]
        int AddSourceFilter(string lpcwstrFileName, string lpcwstrFilterName, out IBaseFilter ppFilter);

        [PreserveSig]
        int SetLogFile(IntPtr hFile);

        [PreserveSig]
        int Abort();

        [PreserveSig]
        int Reconnect(IPin ppin);

        [PreserveSig]
        int Disconnect(IPin ppin);

        [PreserveSig]
        int SetDefaultSyncSource();

        [PreserveSig]
        int AddFilter(IBaseFilter pFilter, string pName);

        [PreserveSig]
        int RemoveFilter(IBaseFilter pFilter);
    }

    [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770")]
    internal interface IBaseFilter
    {
        [PreserveSig]
        int GetClassID(out Guid pClassID);

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Pause();

        [PreserveSig]
        int Run(long tStart);

        [PreserveSig]
        int GetState(int dwMilliSecsTimeout, out int pState);

        [PreserveSig]
        int SetSyncSource(IReferenceClock pClock);

        [PreserveSig]
        int GetSyncSource(out IReferenceClock pClock);

        [PreserveSig]
        int EnumPins(out IEnumPins ppEnum);

        [PreserveSig]
        int FindPin(string Id, out IPin ppPin);

        [PreserveSig]
        int QueryFilterInfo(out FilterInfo pInfo);

        [PreserveSig]
        int JoinFilterGraph(IFilterGraph pGraph, string pName);

        [PreserveSig]
        int QueryVendorInfo(out string pVendorInfo);
    }

    [ComImport, Guid("56a86892-0ad4-11ce-b03a-0020af0ba770")]
    internal interface IPin
    {
        [PreserveSig]
        int Connect(IPin pReceivePin, ref AM_MEDIA_TYPE pmt);

        [PreserveSig]
        int ReceiveConnection(IPin pReceivePin, ref AM_MEDIA_TYPE pmt);

        [PreserveSig]
        int Disconnect();

        [PreserveSig]
        int ConnectedTo(out IPin pPin);

        [PreserveSig]
        int ConnectionMediaType(out AM_MEDIA_TYPE pmt);

        [PreserveSig]
        int QueryPinInfo(out PinInfo pInfo);

        [PreserveSig]
        int QueryDirection(out PinDirection pPinDir);

        [PreserveSig]
        int QueryId(out string Id);

        [PreserveSig]
        int QueryAccept(ref AM_MEDIA_TYPE pmt);

        [PreserveSig]
        int EnumMediaTypes(out IEnumMediaTypes ppEnum);

        [PreserveSig]
        int QueryInternalConnections(IntPtr apPin, ref int nPin);

        [PreserveSig]
        int EndOfStream();

        [PreserveSig]
        int BeginFlush();

        [PreserveSig]
        int EndFlush();

        [PreserveSig]
        int NewSegment(long tStart, long tStop, double dRate);
    }

    [ComImport, Guid("56a86893-0ad4-11ce-b03a-0020af0ba770")]
    internal interface IEnumPins
    {
        [PreserveSig]
        int Next(int cPins, out IPin ppPins, out int pcFetched);

        [PreserveSig]
        int Skip(int cPins);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone(out IEnumPins ppEnum);
    }

    [ComImport, Guid("629cc881-2b0a-45f4-8cbf-66fa0fd80053")]
    internal interface IReferenceClock { }

    [ComImport, Guid("6994ad05-93e7-11d0-a3cc-00a0c9223196")]
    internal interface IEnumMediaTypes { }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FilterInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string achName;
        public IFilterGraph pGraph;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PinInfo
    {
        public IBaseFilter pFilter;
        public PinDirection dir;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string achName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AM_MEDIA_TYPE
    {
        public Guid majortype;
        public Guid subtype;
        public bool bFixedSizeSamples;
        public bool bTemporalCompression;
        public int lSampleSize;
        public Guid formattype;
        public IntPtr pUnk;
        public int cbFormat;
        public IntPtr pbFormat;
    }

    internal enum PinDirection
    {
        PINDIR_INPUT = 0,
        PINDIR_OUTPUT = 1
    }

    [ComImport, Guid("56a8689c-0ad4-11ce-b03a-0020af0ba770")]
    internal class FilterGraph { }

    [ComImport, Guid("79000960-644d-11cf-ae66-28355810e1f5")]
    internal class SampleGrabber { }

    [ComImport, Guid("6b652fff-11fe-4fce-92ad-0266b5d7c78f")]
    internal interface ISampleGrabber
    {
        [PreserveSig]
        int SetOneShot(bool OneShot);

        [PreserveSig]
        int SetMediaType(ref AM_MEDIA_TYPE pType);

        [PreserveSig]
        int GetConnectedMediaType(out AM_MEDIA_TYPE pType);

        [PreserveSig]
        int SetBufferSamples(bool BufferThem);

        [PreserveSig]
        int GetCurrentBuffer(ref int pBufferSize, IntPtr pBuffer);

        [PreserveSig]
        int GetCurrentSample(out IMediaSample ppSample);

        [PreserveSig]
        int SetCallback(ISampleGrabberCB pCallback, int WhichMethodToCallback);
    }

    internal interface ISampleGrabberCB
    {
        [PreserveSig]
        int SampleCB(double SampleTime, IMediaSample pSample);

        [PreserveSig]
        int BufferCB(double SampleTime, IntPtr pBuffer, int BufferLen);
    }

    [ComImport, Guid("56a8689a-0ad4-11ce-b03a-0020af0ba770")]
    internal interface IMediaSample
    {
        [PreserveSig]
        int GetPointer(out IntPtr ppBuffer);

        [PreserveSig]
        int GetSize();

        [PreserveSig]
        int GetTime(out long pTimeStart, out long pTimeEnd);

        [PreserveSig]
        int SetTime(ref long pTimeStart, ref long pTimeEnd);

        [PreserveSig]
        int IsSyncPoint();

        [PreserveSig]
        int SetSyncPoint(bool bIsSyncPoint);

        [PreserveSig]
        int IsPreroll();

        [PreserveSig]
        int SetPreroll(bool bIsPreroll);

        [PreserveSig]
        int GetActualDataLength();

        [PreserveSig]
        int SetActualDataLength(int length);

        [PreserveSig]
        int GetMediaType(out AM_MEDIA_TYPE ppMediaType);

        [PreserveSig]
        int SetMediaType(ref AM_MEDIA_TYPE pMediaType);

        [PreserveSig]
        int IsDiscontinuity();

        [PreserveSig]
        int SetDiscontinuity(bool bDiscontinuity);

        [PreserveSig]
        int GetMediaTime(out long pTimeStart, out long pTimeEnd);

        [PreserveSig]
        int SetMediaTime(ref long pTimeStart, ref long pTimeEnd);
    }

    [ComImport, Guid("29840822-5b84-11d0-bd3b-00a0c911ce86")]
    internal interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(ref Guid clsidDeviceClass, out IEnumMoniker ppEnumMoniker, int dwFlags);

        [PreserveSig]
        int EnumDevices(Guid clsidDeviceClass, out IEnumMoniker ppEnumMoniker);
    }

    [ComImport, Guid("56a86893-0ad4-11ce-b03a-0020af0ba770")]
    internal interface IEnumFilters { }

    [ComImport, Guid("bf87b6e0-8c27-11d0-a3cf-00a0c9223196")]
    internal class CreateDevEnum { }

    [ComImport, Guid("9556eb99-57b2-47d7-a52b-cbbae3623472")]
    internal interface IMediaControl
    {
        [PreserveSig]
        int Run();

        [PreserveSig]
        int Pause();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int GetState(int msTimeout, out int pState);

        [PreserveSig]
        int RenderFile(string strFilename);

        [PreserveSig]
        int AddSourceFilter(string strFilename, out IDispatch ppUnk);

        [PreserveSig]
        int get_FilterCollection(out IDispatch ppUnk);

        [PreserveSig]
        int get_RegFilterCollection(out IDispatch ppUnk);

        [PreserveSig]
        int AbortOperation();
    }

    [ComImport, Guid("00020400-0000-0000-C000-000000000046")]
    internal interface IDispatch { }

    // GUIDs for DirectShow
    internal static class DirectShowGuids
    {
        public static readonly Guid CLSID_VideoInputDeviceCategory = new("860BB310-5D91-11d0-BD3B-00A0C911CE86");
        public static readonly Guid CLSID_LegacyAmFilterCategory = new("083863F1-70DE-11d0-BD40-00A0C911CE86");
        public static readonly Guid MEDIATYPE_Video = new("73646976-0000-0010-8000-00AA00389B71");
        public static readonly Guid MEDIASUBTYPE_RGB24 = new("E436EB7D-524F-11CE-9F53-0020AF0BA770");
        public static readonly Guid FORMAT_VideoInfo = new("05589F80-C356-11CE-BF01-00AA0055595A");
    }
}
