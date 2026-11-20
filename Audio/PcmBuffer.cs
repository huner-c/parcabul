using System.Diagnostics;
using System.IO;

namespace ParcaBul.Audio;

internal sealed class PcmBuffer(int sampleRate)
{
    internal const int ClipSeconds = 10;
    private const int MinimumSignalSeconds = 2;
    private readonly short[] _samples = new short[sampleRate * ClipSeconds];
    private readonly object _sync = new();
    private int _position;
    private int _count;
    private long _lastPacket;

    public void Append(ReadOnlySpan<short> samples)
    {
        lock (_sync)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastPacket != 0)
            {
                double gap = Stopwatch.GetElapsedTime(_lastPacket, now).TotalSeconds;
                // An idle render endpoint may stop delivering packets entirely.
                if (gap > 0.25)
                {
                    int missing = (int)Math.Clamp(gap * sampleRate - samples.Length, 0, _samples.Length);
                    int first = Math.Min(missing, _samples.Length - _position);
                    _samples.AsSpan(_position, first).Clear();
                    _samples.AsSpan(0, missing - first).Clear();
                    Advance(missing);
                }
            }

            samples = samples[^Math.Min(samples.Length, _samples.Length)..];
            int head = Math.Min(samples.Length, _samples.Length - _position);
            samples[..head].CopyTo(_samples.AsSpan(_position));
            samples[head..].CopyTo(_samples);
            Advance(samples.Length);
            _lastPacket = now;
        }
    }

    public byte[]? GetWave()
    {
        short[] snapshot;
        lock (_sync)
        {
            int elapsedSamples = (int)Math.Min(_samples.Length, Stopwatch.GetElapsedTime(_lastPacket).TotalSeconds * sampleRate);
            int length = Math.Min(_count, _samples.Length - elapsedSamples);
            if (length < sampleRate * MinimumSignalSeconds)
                return null;

            snapshot = new short[length];
            int start = (_position + _samples.Length - length) % _samples.Length;
            int head = Math.Min(length, _samples.Length - start);
            _samples.AsSpan(start, head).CopyTo(snapshot);
            _samples.AsSpan(0, length - head).CopyTo(snapshot.AsSpan(head));
        }

        // Find audible 50 ms blocks to ignore silent padding and isolated clicks.
        int blockSize = sampleRate / 20;
        int firstSignal = -1;
        int lastSignal = 0;
        int audibleSamples = 0;
        for (int offset = 0; offset < snapshot.Length; offset += blockSize)
        {
            int length = Math.Min(blockSize, snapshot.Length - offset);
            long energy = 0;
            foreach (short sample in snapshot.AsSpan(offset, length))
                energy += sample * (long)sample;

            if (energy < 24L * 24 * length)
                continue;

            if (firstSignal < 0)
                firstSignal = offset;
            lastSignal = offset + length;
            audibleSamples += length;
        }

        if (audibleSamples < sampleRate * MinimumSignalSeconds)
            return null;

        return EncodeWave(snapshot.AsSpan(firstSignal, lastSignal - firstSignal));
    }

    private void Advance(int length)
    {
        _position = (_position + length) % _samples.Length;
        _count = Math.Min(_samples.Length, _count + length);
    }

    private byte[] EncodeWave(ReadOnlySpan<short> samples)
    {
        int dataSize = samples.Length * sizeof(short);
        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples));
        return stream.ToArray();
    }
}
