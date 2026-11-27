using System.Buffers.Binary;
using System.IO;
using System.Media;
using Microsoft.Win32;

namespace ThinMarketMonitor;

internal sealed record SignalToneOption(SignalTone Tone, string Label);

internal sealed class SoundMix
{
    public int Master { get; set; } = 100;
    public int Price { get; set; } = 100;
    public int Volume { get; set; } = 100;
    public int Density { get; set; } = 100;
    public int PriceAlert { get; set; } = 100;
    public int Voice { get; set; } = 100;

    internal int Apply(int level) => (Master * level + 50) / 100;

    internal void Normalize()
    {
        Master = Math.Clamp(Master, 0, 100);
        Price = Math.Clamp(Price, 0, 100);
        Volume = Math.Clamp(Volume, 0, 100);
        Density = Math.Clamp(Density, 0, 100);
        PriceAlert = Math.Clamp(PriceAlert, 0, 100);
        Voice = Math.Clamp(Voice, 0, 100);
    }
}

internal static class SignalSound
{
    public static readonly IReadOnlyList<SignalToneOption> Options =
    [
        new(SignalTone.System, "Системный"),
        new(SignalTone.Bell, "Колокольчик"),
        new(SignalTone.Double, "Двойной"),
        new(SignalTone.Soft, "Мягкий")
    ];

    public static void Play(SignalTone tone, int volume, CancellationToken ct)
    {
        if (volume == 0 || ct.IsCancellationRequested) return;
        if (tone == SignalTone.System)
        {
            string? configured;
            using (var sound = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps\.Default\SystemAsterisk\.Current"))
                configured = sound?.GetValue(null) as string;
            if (configured == "") return;
            string path = configured == null
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Notify System Generic.wav")
                : Environment.ExpandEnvironmentVariables(configured.Trim('"'));
            if (Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path))
            {
                using var source = File.OpenRead(path);
                if (source.Length is > 0 and <= 16_000_000)
                {
                    using var adjusted = volume == 100 ? null : VolumeAdjustedWave(source, volume);
                    if (volume == 100 || adjusted != null)
                    {
                        PlayWave(adjusted ?? (Stream)source, ct);
                        return;
                    }
                }
            }
            tone = SignalTone.Bell;
        }

        using var wave = CreateWave(tone, volume);
        PlayWave(wave, ct);
    }

    internal static void PlayWave(Stream wave, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        using var player = new SoundPlayer(wave);
        using var stopped = ct.Register(player.Stop);
        if (!ct.IsCancellationRequested) player.PlaySync();
    }

    internal static MemoryStream? VolumeAdjustedWave(Stream wave, int volume)
    {
        using var copy = new MemoryStream();
        wave.CopyTo(copy);
        var bytes = copy.ToArray();
        var data = bytes.AsSpan();
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WAVE"u8)) return null;
        int bits = 0, start = 0, length = 0;
        for (int offset = 12; offset <= data.Length - 8;)
        {
            var tag = data.Slice(offset, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 4, 4));
            offset += 8;
            if (size < 0 || size > data.Length - offset) return null;
            if (tag.SequenceEqual("fmt "u8) && size >= 16 && BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2)) == 1)
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 14, 2));
            else if (tag.SequenceEqual("data"u8))
                (start, length) = (offset, size);
            offset += size + (size & 1);
        }
        if (length == 0 || bits is not (8 or 16) || bits == 16 && (length & 1) != 0) return null;
        var samples = data.Slice(start, length);
        if (bits == 8)
        {
            for (int i = 0; i < samples.Length; i++) samples[i] = (byte)(128 + (samples[i] - 128) * volume / 100);
        }
        else
        {
            for (int i = 0; i < samples.Length; i += 2)
            {
                var sample = samples.Slice(i, 2);
                BinaryPrimitives.WriteInt16LittleEndian(sample, (short)(BinaryPrimitives.ReadInt16LittleEndian(sample) * volume / 100));
            }
        }
        return new MemoryStream(bytes, writable: false);
    }

    internal static MemoryStream CreateWave(SignalTone tone, int volume = 100)
    {
        (int Hertz, int Milliseconds)[] notes = tone switch
        {
            SignalTone.Bell => [(880, 250)],
            SignalTone.Double => [(660, 95), (0, 38), (990, 140)],
            SignalTone.Soft => [(480, 220)],
            _ => throw new ArgumentOutOfRangeException(nameof(tone))
        };
        const int sampleRate = 24000, bytesPerSample = 2;
        int sampleCount = notes.Sum(note => note.Milliseconds * sampleRate / 1000);
        int dataLength = sampleCount * bytesPerSample;
        var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * bytesPerSample);
        writer.Write((short)bytesPerSample);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);

        foreach (var note in notes)
        {
            int samples = note.Milliseconds * sampleRate / 1000;
            for (int i = 0; i < samples; i++)
            {
                double phase = Math.Tau * note.Hertz * i / sampleRate;
                double envelope = Math.Min(1, i / 180.0) * Math.Pow(1.0 - i / (double)samples, 1.5);
                double sample = Math.Sin(phase);
                if (tone == SignalTone.Bell) sample = sample * 0.75 + Math.Sin(2.25 * phase) * 0.25;
                writer.Write((short)(short.MaxValue * 0.22 * (volume / 100d) * envelope * sample));
            }
        }

        writer.Flush();
        stream.Position = 0;
        return stream;
    }
}
