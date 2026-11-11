using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AjudanteDeSom
{
    // ---------- Interfaces COM do Windows (WASAPI + captura de áudio por processo) ----------

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    // Marca o objeto como "ágil": o Windows pode chamá-lo de qualquer thread.
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAgileObject { }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        public int Result = -1;
        public object Interface;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                int hr;
                object iface;
                int callHr = op.GetActivateResult(out hr, out iface);
                Result = callHr < 0 ? callHr : hr;
                Interface = iface;
            }
            catch (Exception ex) { Result = ex.HResult; }
            finally { Done.Set(); }
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AudioClientActivationParams
    {
        public int ActivationType;      // 1 = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
        public uint TargetProcessId;
        public int ProcessLoopbackMode; // 0 = incluir o processo e os filhos dele
    }

    // PROPVARIANT do tipo VT_BLOB (layout de 64 bits)
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    static class Native
    {
        [DllImport("Mmdevapi.dll", ExactSpelling = true)]
        public static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);
    }

    /// <summary>
    /// Captura só o som de um processo (e dos processos filhos dele), em PCM 16 bits, 48 kHz, estéreo.
    /// Usa a API "Application Loopback" do Windows (Windows 10 build 20348+ / Windows 11).
    /// </summary>
    sealed class ProcessAudioCapture : IDisposable
    {
        public const int SampleRate = 48000;
        public const int Channels = 2;
        const int BytesPerFrame = 4;

        const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
        const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

        static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        readonly int pid;
        readonly bool exclude; // true: todo o som do PC, menos este processo e os filhos dele
        readonly Action<byte[], int> onData;
        IAudioClient client;
        IAudioCaptureClient capture;
        EventWaitHandle bufferReady;
        Thread thread;
        volatile bool running;

        public ProcessAudioCapture(int pid, Action<byte[], int> onData, bool exclude = false)
        {
            this.pid = pid;
            this.exclude = exclude;
            this.onData = onData;
        }

        public void Start()
        {
            client = Activate(pid, exclude);

            var fmt = new WaveFormatEx
            {
                wFormatTag = 1, // PCM
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                nAvgBytesPerSec = SampleRate * BytesPerFrame,
                nBlockAlign = BytesPerFrame,
                wBitsPerSample = 16,
                cbSize = 0
            };
            IntPtr pFmt = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveFormatEx)));
            try
            {
                Marshal.StructureToPtr(fmt, pFmt, false);
                Check(client.Initialize(0,
                    AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                    AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                    2000000 /* 200 ms */, 0, pFmt, IntPtr.Zero), "preparar a captura");
            }
            finally { Marshal.FreeHGlobal(pFmt); }

            object svc;
            Check(client.GetService(ref IID_IAudioCaptureClient, out svc), "abrir o leitor de áudio");
            capture = (IAudioCaptureClient)svc;

            bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset);
            Check(client.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle()), "configurar o aviso de áudio");
            Check(client.Start(), "iniciar a captura");

            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "Captura de audio" };
            thread.Start();
        }

        static IAudioClient Activate(int pid, bool exclude)
        {
            // modo 0: só o processo (e filhos); modo 1: tudo, menos o processo (e filhos)
            var ap = new AudioClientActivationParams { ActivationType = 1, TargetProcessId = (uint)pid, ProcessLoopbackMode = exclude ? 1 : 0 };
            int apSize = Marshal.SizeOf(typeof(AudioClientActivationParams));
            IntPtr pParams = Marshal.AllocHGlobal(apSize);
            IntPtr pVariant = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(PropVariantBlob)));
            try
            {
                Marshal.StructureToPtr(ap, pParams, false);
                var pv = new PropVariantBlob { vt = 65 /* VT_BLOB */, cbSize = (uint)apSize, pBlobData = pParams };
                Marshal.StructureToPtr(pv, pVariant, false);

                var handler = new ActivationHandler();
                IActivateAudioInterfaceAsyncOperation op;
                int hr = Native.ActivateAudioInterfaceAsync("VAD\\Process_Loopback", IID_IAudioClient, pVariant, handler, out op);
                Check(hr, "pedir o som do programa ao Windows");
                if (!handler.Done.WaitOne(10000))
                    throw new Exception("O Windows não respondeu ao pedido de captura.");
                GC.KeepAlive(op);
                Check(handler.Result, "pedir o som do programa ao Windows");
                var c = handler.Interface as IAudioClient;
                if (c == null) throw new Exception("O Windows não entregou o canal de áudio.");
                return c;
            }
            finally
            {
                Marshal.FreeHGlobal(pVariant);
                Marshal.FreeHGlobal(pParams);
            }
        }

        void Loop()
        {
            byte[] buf = new byte[SampleRate * BytesPerFrame]; // 1 s de folga
            try
            {
                while (running)
                {
                    bufferReady.WaitOne(100);
                    if (!running) break;
                    uint next;
                    while (running && capture.GetNextPacketSize(out next) >= 0 && next > 0)
                    {
                        IntPtr data; uint frames, flags; ulong devPos, qpc;
                        if (capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc) < 0) break;
                        int bytes = (int)frames * BytesPerFrame;
                        if (bytes > buf.Length) buf = new byte[bytes];
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(buf, 0, bytes);
                        else Marshal.Copy(data, buf, 0, bytes);
                        capture.ReleaseBuffer(frames);
                        if (bytes > 0) onData(buf, bytes);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Erro na captura: " + ex.Message);
            }
        }

        public void Dispose()
        {
            running = false;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(1000);
            try { if (client != null) client.Stop(); } catch { }
            if (capture != null) { try { Marshal.ReleaseComObject(capture); } catch { } capture = null; }
            if (client != null) { try { Marshal.ReleaseComObject(client); } catch { } client = null; }
            if (bufferReady != null) { bufferReady.Dispose(); bufferReady = null; }
        }

        static void Check(int hr, string step)
        {
            if (hr >= 0) return;
            string extra = "";
            if (hr == unchecked((int)0x80070057)) extra = " (o programa pode ter sido fechado)";
            if (hr == unchecked((int)0x80004001) || hr == unchecked((int)0x80040154)) extra = " (precisa do Windows 11)";
            throw new Exception(string.Format("Não consegui {0}. Código 0x{1:X8}{2}.", step, hr, extra));
        }
    }
}
