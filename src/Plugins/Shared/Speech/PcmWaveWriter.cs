using System.Buffers.Binary;

namespace Oscova.Agents.Plugins.Shared.Speech;

/// <summary>Wraps plugin-produced PCM samples in a WAV header for browser and Avatar playback.</summary>
internal static class PcmWaveWriter
{
    /// <summary>Writes the supplied PCM samples with the caller's format metadata.</summary>
    internal static byte[] Write(byte[] pcm, int sampleRate, short channels, short bitsPerSample)
    {
        var buffer = new byte[44 + pcm.Length];
        var span = buffer.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], buffer.Length - 8);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * bitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * bitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], bitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Length);
        pcm.CopyTo(buffer, 44);
        return buffer;
    }
}
