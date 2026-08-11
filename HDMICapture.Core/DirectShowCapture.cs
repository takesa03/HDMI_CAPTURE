using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace HDMICapture.Core
{
    public class DirectShowCapture : IVideoSource
    {
        private IGraphBuilder? _graphBuilder;
        private IMediaControl? _mediaControl;
        private IBaseFilter? _captureFilter;
        private IBaseFilter? _sampleGrabberFilter;
        private ISampleGrabber? _sampleGrabber;
        private bool _isRunning;
        private bool _disposed;
        private readonly object _lock = new();
        private SampleGrabberCallback? _sampleGrabberCallback;

        public event EventHandler<VideoFrameEventArgs>? FrameReceived;
        public event EventHandler<string>? ErrorOccurred;

        public string DeviceName { get; set; } = "HDMI Capture Device";
        public bool IsRunning => _isRunning;
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public double FrameRate { get; set; } = 30.0;

        public DirectShowCapture(string deviceName)
        {
            DeviceName = deviceName;
        }

        public void Start()
        {
            if (_isRunning) return;

            try
            {
                lock (_lock)
                {
                    InitializeCapture();
                    _isRunning = true;

                    if (_mediaControl != null)
                    {
                        int hr = _mediaControl.Run();
                        if (hr < 0)
                            throw new InvalidOperationException($"Failed to start graph: 0x{hr:X8}");
                    }
                }
            }
            catch (Exception ex)
            {
                _isRunning = false;
                ErrorOccurred?.Invoke(this, $"Failed to start capture: {ex.Message}");
                Cleanup();
                throw;
            }
        }

        public void Stop()
        {
            if (!_isRunning) return;

            try
            {
                lock (_lock)
                {
                    _isRunning = false;

                    if (_mediaControl != null)
                    {
                        _mediaControl.Stop();
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Failed to stop capture: {ex.Message}");
            }
        }

        private void InitializeCapture()
        {
            try
            {
                _graphBuilder = new FilterGraph() as IGraphBuilder;
                if (_graphBuilder == null)
                    throw new InvalidOperationException("Failed to create FilterGraph");

                _mediaControl = _graphBuilder as IMediaControl;
                if (_mediaControl == null)
                    throw new InvalidOperationException("Failed to get IMediaControl");

                _captureFilter = FindCaptureDevice(DeviceName);
                if (_captureFilter == null)
                    throw new InvalidOperationException($"Capture device '{DeviceName}' not found");

                int hr = _graphBuilder.AddFilter(_captureFilter, "Capture");
                if (hr < 0)
                    throw new InvalidOperationException($"Failed to add capture filter: 0x{hr:X8}");

                _sampleGrabberFilter = new SampleGrabber() as IBaseFilter;
                if (_sampleGrabberFilter == null)
                    throw new InvalidOperationException("Failed to create SampleGrabber filter");

                hr = _graphBuilder.AddFilter(_sampleGrabberFilter, "SampleGrabber");
                if (hr < 0)
                    throw new InvalidOperationException($"Failed to add sample grabber: 0x{hr:X8}");

                _sampleGrabber = _sampleGrabberFilter as ISampleGrabber;
                if (_sampleGrabber == null)
                    throw new InvalidOperationException("Failed to get ISampleGrabber");

                _sampleGrabberCallback = new SampleGrabberCallback();
                _sampleGrabberCallback.FrameReceived += (sender, args) =>
                {
                    FrameReceived?.Invoke(this, args);
                };

                hr = _sampleGrabber.SetCallback(_sampleGrabberCallback, 1);
                if (hr < 0)
                    throw new InvalidOperationException($"Failed to set sample grabber callback: 0x{hr:X8}");

                // Set media type
                var mediaType = new AM_MEDIA_TYPE
                {
                    majortype = DirectShowGuids.MEDIATYPE_Video,
                    subtype = DirectShowGuids.MEDIASUBTYPE_RGB24,
                    formattype = DirectShowGuids.FORMAT_VideoInfo,
                };

                hr = _sampleGrabber.SetMediaType(ref mediaType);
                if (hr < 0)
                    System.Diagnostics.Debug.WriteLine($"Warning: Failed to set media type: 0x{hr:X8}");

                ConnectFilters();
            }
            catch (Exception ex)
            {
                Cleanup();
                throw;
            }
        }

        private void ConnectFilters()
        {
            if (_graphBuilder == null || _captureFilter == null || _sampleGrabberFilter == null)
                throw new InvalidOperationException("Filters not initialized");

            try
            {
                IPin? capturePin = FindPin(_captureFilter, PinDirection.PINDIR_OUTPUT);
                IPin? grabberPin = FindPin(_sampleGrabberFilter, PinDirection.PINDIR_INPUT);

                if (capturePin == null || grabberPin == null)
                    throw new InvalidOperationException("Failed to find pins");

                try
                {
                    int hr = _graphBuilder.Connect(capturePin, grabberPin);
                    if (hr < 0)
                        throw new InvalidOperationException($"Failed to connect filters: 0x{hr:X8}");
                }
                finally
                {
                    Marshal.ReleaseComObject(capturePin);
                    Marshal.ReleaseComObject(grabberPin);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to connect filters: {ex.Message}", ex);
            }
        }

        private IPin? FindPin(IBaseFilter filter, PinDirection direction)
        {
            IEnumPins? enumPins = null;

            try
            {
                int hr = filter.EnumPins(out enumPins);
                if (hr < 0 || enumPins == null)
                    return null;

                while (enumPins.Next(1, out IPin pin, out int fetched) == 0 && fetched == 1)
                {
                    pin.QueryDirection(out PinDirection pinDir);
                    if (pinDir == direction)
                        return pin;

                    Marshal.ReleaseComObject(pin);
                }
            }
            finally
            {
                if (enumPins != null)
                    Marshal.ReleaseComObject(enumPins);
            }

            return null;
        }

        private IBaseFilter? FindCaptureDevice(string deviceName)
        {
            var devices = DirectShowDeviceEnumerator.GetVideoDevices();

            foreach (var device in devices)
            {
                if (device.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Create filter from moniker
                        var moniker = GetMonikerFromString(device.MonikerString);
                        if (moniker != null)
                        {
                            var iid = GetIID_IBaseFilter();
                            int hr = moniker.BindToObject(null, null, ref iid, out object filterObj);
                            if (hr == 0 && filterObj is IBaseFilter filter)
                            {
                                return filter;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to create filter from moniker: {ex.Message}");
                    }
                }
            }

            return null;
        }

        private IMoniker? GetMonikerFromString(string monikerString)
        {
            try
            {
                if (string.IsNullOrEmpty(monikerString))
                    return null;

                // Parse moniker string (this is simplified - a full implementation would use MkParseDisplayName)
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static Guid GetIID_IBaseFilter() => new("56a86895-0ad4-11ce-b03a-0020af0ba770");

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                Stop();
                Cleanup();
            }
            catch { }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        private void Cleanup()
        {
            try
            {
                if (_mediaControl != null)
                {
                    try { _mediaControl.Stop(); }
                    catch { }
                }

                if (_captureFilter != null)
                {
                    Marshal.ReleaseComObject(_captureFilter);
                }

                if (_sampleGrabberFilter != null)
                {
                    Marshal.ReleaseComObject(_sampleGrabberFilter);
                }

                if (_graphBuilder != null)
                {
                    Marshal.ReleaseComObject(_graphBuilder);
                }
            }
            catch { }

            _captureFilter = null;
            _sampleGrabberFilter = null;
            _graphBuilder = null;
            _mediaControl = null;
            _sampleGrabber = null;
        }
    }

    internal class SampleGrabberCallback : ISampleGrabberCB
    {
        public event EventHandler<VideoFrameEventArgs>? FrameReceived;
        private int _frameCount;

        public int SampleCB(double SampleTime, IMediaSample pSample)
        {
            try
            {
                int hr = pSample.GetPointer(out IntPtr pBuffer);
                if (hr < 0 || pBuffer == IntPtr.Zero)
                    return 0;

                int len = pSample.GetActualDataLength();
                if (len <= 0)
                    return 0;

                byte[] frameData = new byte[len];
                Marshal.Copy(pBuffer, frameData, 0, len);

                var args = new VideoFrameEventArgs
                {
                    FrameData = frameData,
                    Width = 1920,
                    Height = 1080,
                    Timestamp = (long)SampleTime
                };

                FrameReceived?.Invoke(this, args);
            }
            catch { }

            return 0;
        }

        public int BufferCB(double SampleTime, IntPtr pBuffer, int BufferLen)
        {
            return 0;
        }
    }
}
