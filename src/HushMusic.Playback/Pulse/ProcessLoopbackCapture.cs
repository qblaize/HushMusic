using System.Runtime.InteropServices;

namespace HushMusic.Playback.Pulse;

/// <summary>
/// Captures the audio this process (and any child process) plays, through Windows' process loopback: an IAudioClient
/// activated on the virtual process-loopback device instead of an output device. It isn't tied to an output, so it
/// follows the output chosen in Settings by itself, and it hears the app after the app's own volume. Needs Windows
/// build 20348 or later (Windows 11). Not thread-safe: one capture thread uses it.
/// </summary>
/// <remarks>
/// COM is called through vtables by hand, as in the app's TaskbarList: built-in COM interop ([ComImport]) isn't
/// available in trimmed builds. ActivateAudioInterfaceAsync reports back through a completion handler that must be
/// agile; the one here is a minimal native object whose QueryInterface answers IAgileObject.
/// </remarks>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    /// <summary>The documented minimum for process loopback activation.</summary>
    public const int MinimumBuild = 20348;

    // Process loopback has no mix format to ask for; the client names one and the audio engine converts. This is the
    // format Microsoft's application loopback sample uses.
    public const int SampleRate = 44100;
    private const int Channels = 2;
    private const int BitsPerSample = 16;

    private const string VirtualProcessLoopbackDevice = "VAD\\Process_Loopback";
    private const int ActivationTypeProcessLoopback = 1;
    private const int IncludeTargetProcessTree = 0;
    private const short VariantBlob = 65;
    private const int ShareModeShared = 0;
    private const uint StreamFlagsLoopback = 0x00020000;
    private const uint BufferFlagsSilent = 0x2;
    private const int BufferEmpty = 0x08890001; // AUDCLNT_S_BUFFER_EMPTY

    // 20 ms, as in the sample. The engine keeps about 30 ms of a process loopback stream whatever is asked, so it must be
    // read more often than that (every 33 ms lost a tenth of the audio).
    private const long BufferDuration = 200_000;

    // vtable slots. IAudioClient: Initialize 3, Start 10, Stop 11, GetService 14.
    // IAudioCaptureClient: GetBuffer 3, ReleaseBuffer 4, GetNextPacketSize 5.
    private const int InitializeSlot = 3;
    private const int StartSlot = 10;
    private const int StopSlot = 11;
    private const int GetServiceSlot = 14;
    private const int GetBufferSlot = 3;
    private const int ReleaseBufferSlot = 4;
    private const int GetNextPacketSizeSlot = 5;

    private static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientId = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

    private readonly GetBufferFn _getBuffer;
    private readonly ReleaseBufferFn _releaseBuffer;
    private readonly GetNextPacketSizeFn _getNextPacketSize;
    private IntPtr _client;
    private IntPtr _capture;
    private bool _started;
    private short[] _pcm = new short[4096];
    private float[] _mono = new float[2048];
    private long _frames;
    private long _silentFrames;
    private float _peak;

    private ProcessLoopbackCapture(IntPtr client)
    {
        _client = client;
        var format = Marshal.AllocHGlobal(18);
        try
        {
            // WAVEFORMATEX (packed, 18 bytes): 16-bit PCM stereo.
            Marshal.WriteInt16(format, 0, 1);
            Marshal.WriteInt16(format, 2, Channels);
            Marshal.WriteInt32(format, 4, SampleRate);
            Marshal.WriteInt32(format, 8, SampleRate * Channels * BitsPerSample / 8);
            Marshal.WriteInt16(format, 12, Channels * BitsPerSample / 8);
            Marshal.WriteInt16(format, 14, BitsPerSample);
            Marshal.WriteInt16(format, 16, 0);
            Check(
                Method<InitializeFn>(client, InitializeSlot)(client, ShareModeShared, StreamFlagsLoopback, BufferDuration, 0, format, IntPtr.Zero),
                "IAudioClient.Initialize");
        }
        finally
        {
            Marshal.FreeHGlobal(format);
        }

        var captureId = AudioCaptureClientId;
        Check(Method<GetServiceFn>(client, GetServiceSlot)(client, ref captureId, out _capture), "IAudioClient.GetService");
        _getBuffer = Method<GetBufferFn>(_capture, GetBufferSlot);
        _releaseBuffer = Method<ReleaseBufferFn>(_capture, ReleaseBufferSlot);
        _getNextPacketSize = Method<GetNextPacketSizeFn>(_capture, GetNextPacketSizeSlot);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeFn(IntPtr self, int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NoArgumentsFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetServiceFn(IntPtr self, ref Guid iid, out IntPtr service);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBufferFn(IntPtr self, out IntPtr data, out uint frames, out uint flags, IntPtr devicePosition, IntPtr counterPosition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseBufferFn(IntPtr self, uint frames);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNextPacketSizeFn(IntPtr self, out uint frames);

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild);

    /// <summary>A synthetic groove added to what is captured (test instances only: HUSHMUSIC_TEST_PULSE).</summary>
    public TestSignal? TestSignal { get; set; }

    /// <summary>
    /// Activates process loopback capture of this process tree and initializes it (not started yet). Throws
    /// <see cref="COMException"/> when Windows refuses, <see cref="TimeoutException"/> when it doesn't answer.
    /// </summary>
    public static ProcessLoopbackCapture Activate(TimeSpan timeout)
    {
        // AUDIOCLIENT_ACTIVATION_PARAMS, passed as a VT_BLOB PROPVARIANT (8-byte header, then the blob's size and pointer).
        var parameters = Marshal.AllocHGlobal(12);
        var variant = Marshal.AllocHGlobal(8 + (2 * IntPtr.Size));
        var handler = CompletionHandler.Create();
        var operation = IntPtr.Zero;
        try
        {
            Marshal.WriteInt32(parameters, 0, ActivationTypeProcessLoopback);
            Marshal.WriteInt32(parameters, 4, Environment.ProcessId);
            Marshal.WriteInt32(parameters, 8, IncludeTargetProcessTree);
            for (var offset = 0; offset < 8 + (2 * IntPtr.Size); offset += 4)
            {
                Marshal.WriteInt32(variant, offset, 0);
            }

            Marshal.WriteInt16(variant, 0, VariantBlob);
            Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, 8 + IntPtr.Size, parameters);

            var clientId = AudioClientId;
            Check(ActivateAudioInterfaceAsync(VirtualProcessLoopbackDevice, ref clientId, variant, handler.Pointer, out operation), "ActivateAudioInterfaceAsync");
            var client = handler.Wait(timeout);
            try
            {
                return new ProcessLoopbackCapture(client);
            }
            catch
            {
                Marshal.Release(client);
                throw;
            }
        }
        finally
        {
            if (operation != IntPtr.Zero)
            {
                Marshal.Release(operation);
            }

            handler.Release();
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(parameters);
        }
    }

    public void Start()
    {
        Check(Method<NoArgumentsFn>(_client, StartSlot)(_client), "IAudioClient.Start");
        _started = true;
    }

    /// <summary>Reads every packet waiting and hands it to <paramref name="analyzer"/> as mono samples. Returns the frames read.</summary>
    public int Read(PulseAnalyzer analyzer)
    {
        var total = 0;
        while (true)
        {
            Check(_getNextPacketSize(_capture, out var packet), "IAudioCaptureClient.GetNextPacketSize");
            if (packet == 0)
            {
                return total;
            }

            var hr = _getBuffer(_capture, out var data, out var frames, out var flags, IntPtr.Zero, IntPtr.Zero);
            if (hr == BufferEmpty)
            {
                return total;
            }

            Check(hr, "IAudioCaptureClient.GetBuffer");
            try
            {
                if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                {
                    _silentFrames += frames;
                    Mix(IntPtr.Zero, (int)frames, analyzer);
                }
                else
                {
                    Mix(data, (int)frames, analyzer);
                }
            }
            finally
            {
                _releaseBuffer(_capture, frames);
            }

            _frames += frames;
            total += (int)frames;
        }
    }

    /// <summary>Frames read since the last call, how many of them were marked silent, and the loudest sample; then starts counting again.</summary>
    public (long Frames, long SilentFrames, float Peak) TakeStats()
    {
        var stats = (_frames, _silentFrames, _peak);
        _frames = 0;
        _silentFrames = 0;
        _peak = 0;
        return stats;
    }

    public void Dispose()
    {
        if (_client != IntPtr.Zero)
        {
            if (_started)
            {
                _ = Method<NoArgumentsFn>(_client, StopSlot)(_client);
                _started = false;
            }

            if (_capture != IntPtr.Zero)
            {
                Marshal.Release(_capture);
                _capture = IntPtr.Zero;
            }

            Marshal.Release(_client);
            _client = IntPtr.Zero;
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(string deviceInterfacePath, ref Guid iid, IntPtr activationParams, IntPtr completionHandler, out IntPtr activationOperation);

    private static void Check(int hresult, string call)
    {
        if (hresult < 0)
        {
            throw new COMException($"{call} failed (0x{hresult:X8}).", hresult);
        }
    }

    private static T Method<T>(IntPtr instance, int slot)
        where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    // 16-bit stereo to mono floats (a silent packet has no data), plus the test signal when there is one.
    private void Mix(IntPtr data, int frames, PulseAnalyzer analyzer)
    {
        if (data == IntPtr.Zero && TestSignal is null)
        {
            analyzer.PushSilence(frames);
            return;
        }

        if (_mono.Length < frames)
        {
            _pcm = new short[frames * Channels];
            _mono = new float[frames];
        }

        var mono = _mono.AsSpan(0, frames);
        if (data == IntPtr.Zero)
        {
            mono.Clear();
        }
        else
        {
            Marshal.Copy(data, _pcm, 0, frames * Channels);
            var peak = _peak;
            for (var i = 0; i < frames; i++)
            {
                var sample = (_pcm[2 * i] + _pcm[(2 * i) + 1]) / 65536f;
                mono[i] = sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }

            _peak = peak;
        }

        TestSignal?.AddTo(mono);
        analyzer.Push(mono);
    }

    /// <summary>
    /// A native IActivateAudioInterfaceCompletionHandler (also IAgileObject): a vtable shared by every instance, and per
    /// instance a pointer to it plus a GCHandle to the managed state. Reference counted like any COM object.
    /// </summary>
    private sealed class CompletionHandler
    {
        private const int NoInterface = unchecked((int)0x80004002);
        private const int Fail = unchecked((int)0x80004005);

        private static readonly Guid UnknownId = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid HandlerId = new("41D949AB-9862-444A-80F6-C261334DA5EB");
        private static readonly Guid AgileObjectId = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");

        // Kept alive for the process: native code calls through them.
        private static readonly QueryInterfaceFn s_queryInterface = QueryInterface;
        private static readonly AddRefFn s_addRef = AddRef;
        private static readonly AddRefFn s_release = ReleaseNative;
        private static readonly ActivateCompletedFn s_activateCompleted = ActivateCompleted;
        private static readonly IntPtr s_vtable = CreateVtable();

        private readonly ManualResetEventSlim _completed = new(false);
        private readonly object _gate = new();
        private int _references = 1;
        private int _result;
        private IntPtr _activated;
        private bool _abandoned;

        private CompletionHandler()
        {
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint AddRefFn(IntPtr self);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ActivateCompletedFn(IntPtr self, IntPtr operation);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetActivateResultFn(IntPtr self, out int result, out IntPtr activated);

        public IntPtr Pointer { get; private set; }

        public static CompletionHandler Create()
        {
            var handler = new CompletionHandler();
            var native = Marshal.AllocHGlobal(2 * IntPtr.Size);
            Marshal.WriteIntPtr(native, 0, s_vtable);
            Marshal.WriteIntPtr(native, IntPtr.Size, GCHandle.ToIntPtr(GCHandle.Alloc(handler)));
            handler.Pointer = native;
            return handler;
        }

        /// <summary>Waits for the activation; returns the activated interface (owned by the caller) or throws.</summary>
        public IntPtr Wait(TimeSpan timeout)
        {
            if (!_completed.Wait(timeout))
            {
                lock (_gate)
                {
                    if (!_completed.IsSet)
                    {
                        _abandoned = true;
                        throw new TimeoutException("Windows didn't finish activating process loopback capture.");
                    }
                }
            }

            Check(_result, "Process loopback activation");
            if (_activated == IntPtr.Zero)
            {
                throw new COMException("Process loopback activation returned no audio client.", Fail);
            }

            return _activated;
        }

        /// <summary>Drops the caller's reference (the native object goes once Windows has let go of it too).</summary>
        public void Release() => ReleaseNative(Pointer);

        private static CompletionHandler From(IntPtr self) =>
            (CompletionHandler)GCHandle.FromIntPtr(Marshal.ReadIntPtr(self, IntPtr.Size)).Target!;

        private static IntPtr CreateVtable()
        {
            var vtable = Marshal.AllocHGlobal(4 * IntPtr.Size);
            Marshal.WriteIntPtr(vtable, 0, Marshal.GetFunctionPointerForDelegate(s_queryInterface));
            Marshal.WriteIntPtr(vtable, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(s_addRef));
            Marshal.WriteIntPtr(vtable, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(s_release));
            Marshal.WriteIntPtr(vtable, 3 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(s_activateCompleted));
            return vtable;
        }

        private static int QueryInterface(IntPtr self, ref Guid iid, out IntPtr result)
        {
            if (iid == UnknownId || iid == HandlerId || iid == AgileObjectId)
            {
                AddRef(self);
                result = self;
                return 0;
            }

            result = IntPtr.Zero;
            return NoInterface;
        }

        private static uint AddRef(IntPtr self) => (uint)Interlocked.Increment(ref From(self)._references);

        private static uint ReleaseNative(IntPtr self)
        {
            var handler = From(self);
            var left = Interlocked.Decrement(ref handler._references);
            if (left == 0)
            {
                GCHandle.FromIntPtr(Marshal.ReadIntPtr(self, IntPtr.Size)).Free();
                Marshal.FreeHGlobal(self);
                handler._completed.Dispose();
            }

            return (uint)left;
        }

        // Called by Windows on one of its threads. Must not throw.
        private static int ActivateCompleted(IntPtr self, IntPtr operation)
        {
            var handler = From(self);
            var result = Fail;
            var activated = IntPtr.Zero;
            try
            {
                var getResult = Method<GetActivateResultFn>(operation, 3);
                var hr = getResult(operation, out var activateResult, out activated);
                result = hr < 0 ? hr : activateResult;
            }
            catch (Exception ex)
            {
                result = ex.HResult < 0 ? ex.HResult : Fail;
            }

            lock (handler._gate)
            {
                if (handler._abandoned)
                {
                    // Timed out already: nobody will use the client.
                    if (activated != IntPtr.Zero)
                    {
                        Marshal.Release(activated);
                    }
                }
                else
                {
                    handler._result = result;
                    handler._activated = result < 0 ? IntPtr.Zero : activated;
                    if (result < 0 && activated != IntPtr.Zero)
                    {
                        Marshal.Release(activated);
                    }

                    handler._completed.Set();
                }
            }

            return 0;
        }
    }
}
