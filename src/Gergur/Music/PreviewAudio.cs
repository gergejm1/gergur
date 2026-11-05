using System.Runtime.InteropServices;
using Gergur.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Gergur.Music;

/// <summary>
/// Turns a song preview (MP3 from Deezer, AAC from Apple) into what the fingerprints are made
/// from, in memory, through Windows' own decoders (Media Foundation). Nothing is written to disk.
/// Off the UI thread: Media Foundation needs a thread pool (MTA) thread here.
/// </summary>
internal static class PreviewAudio
{
    /// <summary>More than any preview: what is read is bounded whatever the file says of itself.</summary>
    internal const int MostSeconds = 60;

    /// <summary>Mono floats at the fingerprint rate, or null when Windows cannot read these bytes.</summary>
    public static float[]? Decode(byte[] compressed)
    {
        try
        {
            using var stream = new MemoryStream(compressed, writable: false);
            using var reader = new StreamMediaFoundationReader(stream);
            ISampleProvider source = reader.ToSampleProvider();
            if (source.WaveFormat.Channels == 2)
                source = new StereoToMonoSampleProvider(source);
            else if (source.WaveFormat.Channels != 1)
                return null;
            if (source.WaveFormat.SampleRate != AudioFingerprint.SampleRate)
                source = new WdlResamplingSampleProvider(source, AudioFingerprint.SampleRate);
            int most = MostSeconds * AudioFingerprint.SampleRate;
            var samples = new float[most];
            int filled = 0;
            while (filled < most)
            {
                int got = source.Read(samples, filled, Math.Min(4096, most - filled));
                if (got <= 0)
                    break;
                filled += got;
            }
            Array.Resize(ref samples, filled);
            return samples;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or IOException or NotSupportedException or InvalidCastException)
        {
            DebugLog.Write($"a song preview could not be read: {ex.Message}");
            return null;
        }
    }
}
