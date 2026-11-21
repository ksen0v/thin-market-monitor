using System.IO;
using System.Media;

namespace ThinMarketMonitor;

internal sealed record SignalToneOption(SignalTone Tone, string Label);

internal static class SignalSound
{
    public static readonly IReadOnlyList<SignalToneOption> Options =
    [
        new(SignalTone.System, "Системный"),
        new(SignalTone.Bell, "Колокольчик"),
        new(SignalTone.Double, "Двойной"),
        new(SignalTone.Soft, "Мягкий")
    ];

    public static void Play(SignalTone tone)
    {
        if (tone == SignalTone.System)
        {
            SystemSounds.Asterisk.Play();
            return;
        }

        using var wave = CreateWave(tone);
        using var player = new SoundPlayer(wave);
        player.PlaySync();
    }

    internal static MemoryStream CreateWave(SignalTone tone)
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
                writer.Write((short)(short.MaxValue * 0.22 * envelope * sample));
            }
        }

        writer.Flush();
        stream.Position = 0;
        return stream;
    }
}
