﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Win8.Wave.WaveOutputs;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using NAudio.Utils;
using NativeMethods = NAudio.Win8.Wave.WaveOutputs.NativeMethods;

namespace NAudio.Wave
{
    enum WasapiCaptureState
    {
        Uninitialized,
        Stopped,
        Recording,
        Disposed
    }

    /// <summary>
    /// Audio Capture using Wasapi
    /// See http://msdn.microsoft.com/en-us/library/dd370800%28VS.85%29.aspx
    /// </summary>
    public class WasapiCaptureRT : IWaveIn
    {
        static readonly Guid IID_IAudioClient2 = new Guid("726778CD-F60A-4eda-82DE-E47610CD78AA");
        private const long REFTIMES_PER_SEC = 10000000;
        private const long REFTIMES_PER_MILLISEC = 10000;
        private volatile WasapiCaptureState captureState;
        private byte[] recordBuffer;
        private readonly string device;
        private int bytesPerFrame;
        private WaveFormat waveFormat;
        private AudioClient audioClient;
        private IntPtr hEvent;
        private Task captureTask;
        private SynchronizationContext syncContext;

        /// <summary>
        /// Indicates recorded data is available 
        /// </summary>
        public event EventHandler<WaveInEventArgs> DataAvailable;

        /// <summary>
        /// Indicates that all recorded data has now been received.
        /// </summary>
        public event EventHandler<StoppedEventArgs> RecordingStopped;
        private int latencyMilliseconds;

        /// <summary>
        /// Properties of the client's audio stream.
        /// Set before calling init
        /// </summary>
        private AudioClientProperties? audioClientProperties = null;

        /// <summary>
        /// Initialises a new instance of the WASAPI capture class
        /// </summary>
        public WasapiCaptureRT() : 
            this(GetDefaultCaptureDevice())
        {
        }

        /// <summary>
        /// Initialises a new instance of the WASAPI capture class
        /// </summary>
        /// <param name="device">Capture device to use</param>
        public WasapiCaptureRT(string device)
        {
            this.device = device;
            this.syncContext = SynchronizationContext.Current;
            //this.waveFormat = audioClient.MixFormat;
        }

        /// <summary>
        /// Recording wave format
        /// </summary>
        public virtual WaveFormat WaveFormat 
        {
            get
            {
                // for convenience, return a WAVEFORMATEX, instead of the real
                // WAVEFORMATEXTENSIBLE being used
                var wfe = waveFormat as WaveFormatExtensible;
                if (wfe != null)
                {
                    try
                    {
                        return wfe.ToStandardWaveFormat();
                    }
                    catch (InvalidOperationException)
                    {
                        // couldn't convert to a standard format
                    }
                }
                return waveFormat;
            }
            set { waveFormat = value; }
        }

        /// <summary>
        /// Way of enumerating all the audio capture devices available on the system
        /// </summary>
        /// <returns></returns>
        public async static Task<IEnumerable<DeviceInformation>> GetCaptureDevices()
        {
            var audioCaptureSelector = MediaDevice.GetAudioCaptureSelector();

            // (a PropertyKey)
            var supportsEventDrivenMode = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e} 7";

            var captureDevices = await DeviceInformation.FindAllAsync(audioCaptureSelector, new[] { supportsEventDrivenMode } );
            return captureDevices;
        }

        /// <summary>
        /// Gets the default audio capture device
        /// </summary>
        /// <returns>The default audio capture device</returns>
        public static string GetDefaultCaptureDevice()
        {
            var defaultCaptureDeviceId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);
            return defaultCaptureDeviceId;
        }

        /// <summary>
        /// Initializes the capture device. Must be called on the UI (STA) thread.
        /// If not called manually then StartRecording() will call it internally.
        /// </summary>
        public async Task InitAsync()
        {
            if (captureState == WasapiCaptureState.Disposed) throw new ObjectDisposedException(nameof(WasapiCaptureRT));
            if (captureState != WasapiCaptureState.Uninitialized) throw new InvalidOperationException("Already initialized");

/*            var icbh = new ActivateAudioInterfaceCompletionHandler(ac2 => InitializeCaptureDevice((IAudioClient)ac2));
              IActivateAudioInterfaceAsyncOperation activationOperation;
              // must be called on UI thread
              NativeMethods.ActivateAudioInterfaceAsync(device, IID_IAudioClient2, IntPtr.Zero, icbh, out activationOperation);

              audioClient = new AudioClient((IAudioClient)(await icbh));

              hEvent = NativeMethods.CreateEventExW(IntPtr.Zero, IntPtr.Zero, 0, EventAccess.EVENT_ALL_ACCESS);
              audioClient.SetEventHandle(hEvent);*/

            var icbh = new ActivateAudioInterfaceCompletionHandler(ac2 =>
            {
                if (this.audioClientProperties != null)
                {
                    IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(this.audioClientProperties.Value));
                    Marshal.StructureToPtr(this.audioClientProperties.Value, p, false);
                    ac2.SetClientProperties(p);
                    Marshal.FreeHGlobal(p);
                    // TODO: consider whether we can marshal this without the need for AllocHGlobal
                }

                InitializeCaptureDevice((IAudioClient2)ac2);
                audioClient = new AudioClient((IAudioClient2)ac2);

                hEvent = NativeMethods.CreateEventExW(IntPtr.Zero, IntPtr.Zero, 0, EventAccess.EVENT_ALL_ACCESS);
                audioClient.SetEventHandle(hEvent);
            });

            IActivateAudioInterfaceAsyncOperation activationOperation;
            // must be called on UI thread
            NativeMethods.ActivateAudioInterfaceAsync(device, IID_IAudioClient2, IntPtr.Zero, icbh, out activationOperation);
            await icbh;

            captureState = WasapiCaptureState.Stopped;
        }

        private void InitializeCaptureDevice(IAudioClient2 audioClientInterface)
        {
            var audioClient = new AudioClient((IAudioClient2)audioClientInterface);
            if (waveFormat == null)
            {                
                waveFormat = audioClient.MixFormat;
            }         

            long requestedDuration = REFTIMES_PER_MILLISEC * 100;

            
            if (!audioClient.IsFormatSupported(AudioClientShareMode.Shared, waveFormat))
            {
                throw new ArgumentException("Unsupported Wave Format");
            }
            
            var streamFlags = GetAudioClientStreamFlags();

            audioClient.Initialize(AudioClientShareMode.Shared,
                streamFlags,
                requestedDuration,
                0,
                waveFormat,
                Guid.Empty);
           

            int bufferFrameCount = audioClient.BufferSize;
            this.bytesPerFrame = this.waveFormat.Channels * this.waveFormat.BitsPerSample / 8;
            this.recordBuffer = new byte[bufferFrameCount * bytesPerFrame];
            Debug.WriteLine(string.Format("record buffer size = {0}", this.recordBuffer.Length));

            // Get back the effective latency from AudioClient
            latencyMilliseconds = (int)(audioClient.StreamLatency / 10000);
        }

        /// <summary>
        /// Sets the parameters that describe the properties of the client's audio stream.
        /// </summary>
        /// <param name="useHardwareOffload">Boolean value to indicate whether or not the audio stream is hardware-offloaded.</param>
        /// <param name="category">An enumeration that is used to specify the category of the audio stream.</param>
        /// <param name="options">A bit-field describing the characteristics of the stream. Supported in Windows 8.1 and later.</param>
        public void SetClientProperties(bool useHardwareOffload, AudioStreamCategory category, AudioClientStreamOptions options)
        {
            audioClientProperties = new AudioClientProperties()
            {
                cbSize = (uint)MarshalHelpers.SizeOf<AudioClientProperties>(),
                bIsOffload = Convert.ToInt32(useHardwareOffload),
                eCategory = category,
                Options = options
            };
        }

        /// <summary>
        /// To allow overrides to specify different flags (e.g. loopback)
        /// </summary>
        protected virtual AudioClientStreamFlags GetAudioClientStreamFlags()
        {
            return AudioClientStreamFlags.EventCallback;
        }

        /// <summary>
        /// Start Recording
        /// </summary>
        public async void StartRecording()
        {
            if (captureState == WasapiCaptureState.Disposed) throw new ObjectDisposedException(nameof(WasapiCaptureRT));
            if (captureState == WasapiCaptureState.Uninitialized) await InitAsync();

            captureState = WasapiCaptureState.Recording;

            captureTask = Task.Run(() => DoRecording());

            Debug.WriteLine("Recording...");
        }

        /// <summary>
        /// Stop Recording
        /// </summary>
        public void StopRecording()
        {
            if (captureState == WasapiCaptureState.Disposed) throw new ObjectDisposedException(nameof(WasapiCaptureRT));
            if (captureState != WasapiCaptureState.Recording) return;

            captureState = WasapiCaptureState.Stopped;
            captureTask?.Wait(5000);
            Debug.WriteLine("WasapiCaptureRT stopped");
        }

        private void DoRecording()
        {
            Debug.WriteLine("Recording buffer size: " + audioClient.BufferSize);

            var buf = new Byte[audioClient.BufferSize * bytesPerFrame];

            int bufLength = 0;
            int minPacketSize = waveFormat.AverageBytesPerSecond / 100; //100ms
                       
            try
            {
                AudioCaptureClient capture = audioClient.AudioCaptureClient;
                audioClient.Start();

                int packetSize = capture.GetNextPacketSize();

                while (captureState == WasapiCaptureState.Recording)
                {                    
                    IntPtr pData = IntPtr.Zero;
                    int numFramesToRead = 0;
                    AudioClientBufferFlags dwFlags = 0;                   

                    if (packetSize == 0)
                    {
                        if (NativeMethods.WaitForSingleObjectEx(hEvent, 100, true) != 0)
                        {
                            throw new Exception("Capture event timeout");
                        }
                    }

                    pData = capture.GetBuffer(out numFramesToRead, out dwFlags);                    

                    if ((int)(dwFlags & AudioClientBufferFlags.Silent) > 0)
                    {
                        pData = IntPtr.Zero;
                    }                    

                    if (numFramesToRead == 0) { continue; }

                    int capturedBytes =  numFramesToRead * bytesPerFrame;

                    if (pData == IntPtr.Zero)
                    {
                        Array.Clear(buf, bufLength, capturedBytes);
                    }
                    else
                    {
                        Marshal.Copy(pData, buf, bufLength, capturedBytes);
                    }
                    
                    bufLength += capturedBytes;

                    capture.ReleaseBuffer(numFramesToRead);

                    if (bufLength >= minPacketSize)
                    {
                        if (DataAvailable != null)
                        {
                            DataAvailable(this, new WaveInEventArgs(buf, bufLength));
                        }
                        bufLength = 0;
                    }

                    packetSize = capture.GetNextPacketSize();
                }
            }
            catch (Exception ex)
            {
                RaiseRecordingStopped(ex);
                Debug.WriteLine("stop wasapi");
            }
            finally
            {
                RaiseRecordingStopped(null);
                
                audioClient.Stop();
            }
            Debug.WriteLine("stop wasapi");
        }

        private void RaiseRecordingStopped(Exception exception)
        {
            var handler = RecordingStopped;
            if (handler != null)
            {
                if (this.syncContext == null)
                {
                    handler(this, new StoppedEventArgs(exception));
                }
                else
                {
                    syncContext.Post(state => handler(this, new StoppedEventArgs(exception)), null);
                }
            }
        }

        private void ReadNextPacket(AudioCaptureClient capture)
        {
            IntPtr buffer;
            int framesAvailable;
            AudioClientBufferFlags flags;
            int packetSize = capture.GetNextPacketSize();
            int recordBufferOffset = 0;
            //Debug.WriteLine(string.Format("packet size: {0} samples", packetSize / 4));

            while (packetSize != 0)
            {
                buffer = capture.GetBuffer(out framesAvailable, out flags);

                int bytesAvailable = framesAvailable * bytesPerFrame;

                // apparently it is sometimes possible to read more frames than we were expecting?
                // fix suggested by Michael Feld:
                int spaceRemaining = Math.Max(0, recordBuffer.Length - recordBufferOffset);
                if (spaceRemaining < bytesAvailable && recordBufferOffset > 0)
                {
                    if (DataAvailable != null) DataAvailable(this, new WaveInEventArgs(recordBuffer, recordBufferOffset));
                    recordBufferOffset = 0;
                }

                // if not silence...
                if ((flags & AudioClientBufferFlags.Silent) != AudioClientBufferFlags.Silent)
                {
                    Marshal.Copy(buffer, recordBuffer, recordBufferOffset, bytesAvailable);
                }
                else
                {
                    Array.Clear(recordBuffer, recordBufferOffset, bytesAvailable);
                }
                recordBufferOffset += bytesAvailable;
                capture.ReleaseBuffer(framesAvailable);
                packetSize = capture.GetNextPacketSize();
            }
            if (DataAvailable != null)
            {
                DataAvailable(this, new WaveInEventArgs(recordBuffer, recordBufferOffset));
            }
        }

        /// <summary>
        /// Dispose
        /// </summary>
        public void Dispose()
        {
            if (captureState == WasapiCaptureState.Disposed) return;

            try
            {
                StopRecording();

                NativeMethods.CloseHandle(hEvent);
                audioClient?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Exception disposing WasapiCaptureRT: " + ex.ToString());
            }
            
            hEvent = IntPtr.Zero;
            audioClient = null;

            captureState = WasapiCaptureState.Disposed;
        }
    }
}
