using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ParcaBul.Audio;

internal sealed class LoopbackCapture : IAsyncDisposable
{
    private const uint LoopbackFlag = 0x00020000;
    private const uint SilentFlag = 0x00000002;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private volatile PcmBuffer? _buffer;
    private volatile string? _error;

    public LoopbackCapture()
    {
        _worker = Task.Factory.StartNew(
            () => CaptureUntilStopped(_stop.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public bool IsReady => _buffer is not null;
    public string? Error => _error;
    public byte[]? GetRecentWave() => _buffer?.GetWave();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }

    private void CaptureUntilStopped(CancellationToken token)
    {
        // The dedicated worker owns all WASAPI interfaces for their entire lifetime.
        int result = CoInitializeEx(IntPtr.Zero, 0);
        if (result < 0)
        {
            _error = "Could not initialize Windows audio.";
            return;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    CaptureDefaultDevice(token);
                }
                catch (COMException)
                {
                    _error = "Could not connect to the Windows audio output.";
                }
                catch (NotSupportedException)
                {
                    _error = "The audio output uses an unsupported format.";
                }
                finally
                {
                    _buffer = null;
                }

                if (_error is not null)
                    token.WaitHandle.WaitOne(750);
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private void CaptureDefaultDevice(CancellationToken token)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? reader = null;
        IntPtr formatPointer = IntPtr.Zero;
        bool started = false;

        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            // eRender, eMultimedia
            enumerator.GetDefaultAudioEndpoint(0, 1, out device);
            device.GetId(out string deviceId);
            Guid clientInterface = typeof(IAudioClient).GUID;
            device.Activate(ref clientInterface, 23, IntPtr.Zero, out object clientObject);
            client = (IAudioClient)clientObject;
            client.GetMixFormat(out formatPointer);
            var format = new SampleFormat(formatPointer);

            // Shared-mode loopback with a 200 ms endpoint buffer.
            client.Initialize(0, LoopbackFlag, 2_000_000, 0, formatPointer, IntPtr.Zero);
            Guid readerInterface = typeof(IAudioCaptureClient).GUID;
            client.GetService(ref readerInterface, out object readerObject);
            reader = (IAudioCaptureClient)readerObject;

            client.Start();
            started = true;
            var buffer = new PcmBuffer(format.SampleRate);
            _error = null;
            _buffer = buffer;

            short[] packet = [];
            var deviceCheck = Stopwatch.StartNew();
            while (!token.IsCancellationRequested)
            {
                while (reader.GetNextPacketSize() != 0 && !token.IsCancellationRequested)
                    ReadPacket(reader, format, buffer, ref packet);

                if (deviceCheck.ElapsedMilliseconds >= 1000)
                {
                    if (!IsStillDefault(enumerator, deviceId))
                        break;

                    deviceCheck.Restart();
                }

                token.WaitHandle.WaitOne(20);
            }
        }
        finally
        {
            try
            {
                if (started)
                    client!.Stop();
            }
            finally
            {
                Marshal.FreeCoTaskMem(formatPointer);
                Release(reader);
                Release(client);
                Release(device);
                Release(enumerator);
            }
        }
    }

    private static bool IsStillDefault(IMMDeviceEnumerator enumerator, string previousId)
    {
        enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice current);
        try
        {
            current.GetId(out string id);
            return id == previousId;
        }
        finally
        {
            Release(current);
        }
    }

    private static unsafe void ReadPacket(IAudioCaptureClient reader, SampleFormat format, PcmBuffer buffer, ref short[] packet)
    {
        reader.GetBuffer(out IntPtr data, out uint frames, out uint flags, IntPtr.Zero, IntPtr.Zero);
        if (frames == 0)
            return;

        try
        {
            if (packet.Length < frames)
                packet = new short[(int)frames];

            Span<short> samples = packet.AsSpan(0, (int)frames);
            if ((flags & SilentFlag) != 0)
                samples.Clear();
            else
                format.Downmix(new ReadOnlySpan<byte>((void*)data, samples.Length * format.BlockAlign), samples);

            buffer.Append(samples);
        }
        finally
        {
            reader.ReleaseBuffer(frames);
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null)
            Marshal.ReleaseComObject(instance);
    }

    private sealed class SampleFormat
    {
        private const int LowFrequencySpeaker = 0x8;
        private static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid Float = new("00000003-0000-0010-8000-00aa00389b71");
        private readonly int _channels;
        private readonly int _bytesPerSample;
        private readonly int _lowFrequencyChannel = -1;
        private readonly bool _isFloat;

        public int SampleRate { get; }
        public int BlockAlign { get; }

        public SampleFormat(IntPtr format)
        {
            int tag = (ushort)Marshal.ReadInt16(format, 0);
            _channels = (ushort)Marshal.ReadInt16(format, 2);
            SampleRate = Marshal.ReadInt32(format, 4);
            BlockAlign = (ushort)Marshal.ReadInt16(format, 12);
            int bits = (ushort)Marshal.ReadInt16(format, 14);
            _bytesPerSample = bits / 8;

            if (tag == 0xfffe && Marshal.ReadInt16(format, 16) >= 22)
            {
                Guid subformat = Marshal.PtrToStructure<Guid>(IntPtr.Add(format, 24));
                tag = subformat == Float ? 3 : subformat == Pcm ? 1 : 0;
                uint mask = (uint)Marshal.ReadInt32(format, 20);
                if (_channels > 1 && (mask & LowFrequencySpeaker) != 0)
                    _lowFrequencyChannel = BitOperations.PopCount(mask & (LowFrequencySpeaker - 1));
            }

            _isFloat = tag == 3;
            bool supported = tag switch
            {
                1 => bits is 8 or 16 or 24 or 32,
                3 => bits is 32 or 64,
                _ => false
            };
            if (!supported || _channels < 1 || SampleRate is < 8000 or > 384000 || BlockAlign < _channels * _bytesPerSample)
                throw new NotSupportedException();
        }

        public void Downmix(ReadOnlySpan<byte> source, Span<short> target)
        {
            int includedChannels = _channels - (_lowFrequencyChannel >= 0 && _lowFrequencyChannel < _channels ? 1 : 0);
            for (int frame = 0; frame < target.Length; frame++)
            {
                double sum = 0;
                for (int channel = 0; channel < _channels; channel++)
                {
                    if (channel == _lowFrequencyChannel)
                        continue;

                    double sample = ReadSample(source[(frame * BlockAlign + channel * _bytesPerSample)..]);
                    sum += double.IsFinite(sample) ? sample : 0;
                }

                target[frame] = (short)Math.Clamp(sum / includedChannels, short.MinValue, short.MaxValue);
            }
        }

        private double ReadSample(ReadOnlySpan<byte> source)
        {
            if (_isFloat)
                return (_bytesPerSample == 4 ? BinaryPrimitives.ReadSingleLittleEndian(source) : BinaryPrimitives.ReadDoubleLittleEndian(source)) * 32768;

            return _bytesPerSample switch
            {
                1 => (source[0] - 128) << 8,
                2 => BinaryPrimitives.ReadInt16LittleEndian(source),
                3 => (short)(source[1] | (source[2] << 8)),
                _ => BinaryPrimitives.ReadInt32LittleEndian(source) >> 16
            };
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint apartment);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [ComImport, Guid("bcde0395-e52f-467c-8e3d-c4579291692e")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int flow, uint stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint classContext, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(uint access, out IntPtr properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int shareMode, uint flags, long duration, long periodicity, IntPtr format, IntPtr sessionId);
        uint GetBufferSize();
        long GetStreamLatency();
        uint GetCurrentPadding();
        void IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("c8adbd64-e71e-48a0-a4de-185c395cd317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, IntPtr devicePosition, IntPtr qpcPosition);
        void ReleaseBuffer(uint frames);
        uint GetNextPacketSize();
    }
}
