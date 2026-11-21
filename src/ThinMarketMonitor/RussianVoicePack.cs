using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed class RussianVoicePack(HttpClient http, CancellationToken shutdown, string? storage = null) : IDisposable
{
    private const string Voice = "ru_RU-dmitri-medium.onnx";
    private const string VoiceRepository = "https://huggingface.co/rhasspy/piper-voices/resolve/2f8dbe0/ru/ru_RU/dmitri/medium/";
    private const string PackName = "piper-dmitri-v1";
    internal static readonly string Preview = SignalSpeech.Format(new(new(Venue.Gate, "BTC_USDT", "BTC", "USDT"), 0, 15, 5));
    private static readonly Artifact[] Artifacts =
    [
        new("piper.zip", "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip",
            22477236, "F3C58906402B24F3A96D92145F58ACBA6D86C9B5DB896D207F78DC80811EFCEA"),
        new(Voice, VoiceRepository + Voice, 63201294, "F073356EBC4BD0F80C5AF58DF2953A5988BD5BDAB1EB38635CE960B071FBEFCB"),
        new(Voice + ".json", VoiceRepository + Voice + ".json", 6770, "86A529367E6DEFAFFA2219F0E664776354C37652AF64AF5D011E18372E7BDC74")
    ];

    private readonly string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storage ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThinMarketMonitor", "Speech")));
    private readonly object gate = new();
    private readonly SemaphoreSlim synthLock = new(1);
    private Task? installation;
    private Process? piper;

    private string PackDirectory => Path.Combine(root, PackName);
    internal bool IsReady => File.Exists(Path.Combine(PackDirectory, ".verified")) && HasFiles(PackDirectory);

    public Task Install()
    {
        lock (gate)
        {
            if (IsReady) return Task.CompletedTask;
            if (installation is { IsCompleted: false }) return installation;
            return installation = InstallCore();
        }
    }

    private async Task InstallCore()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var ct = deadline.Token;
        var staging = Path.Combine(root, $".downloading-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            await Task.WhenAll(Artifacts.Select(artifact => Download(artifact, staging, ct))).ConfigureAwait(false);
            await Task.Run(() => ZipFile.ExtractToDirectory(Path.Combine(staging, "piper.zip"), staging), ct).ConfigureAwait(false);
            File.Delete(Path.Combine(staging, "piper.zip"));
            if (!HasFiles(staging)) throw new IOException("В архиве отсутствуют файлы движка озвучки.");

            using (var engine = Start(staging))
            {
                try { using var demo = await Generate(engine, staging, Preview, ct).ConfigureAwait(false); }
                finally
                {
                    Stop(engine);
                    await engine.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(staging, ".verified"), PackName);
            using var finalize = await AcquireInstallLock(ct).ConfigureAwait(false);
            if (IsReady) return;
            RemoveCache(PackDirectory);
            Directory.Move(staging, PackDirectory);
        }
        finally { RemoveCache(staging); }
    }

    private async Task<FileStream> AcquireInstallLock(CancellationToken ct)
    {
        string path = Path.Combine(root, ".install.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(75, ct).ConfigureAwait(false); }
        }
    }

    private async Task Download(Artifact asset, string directory, CancellationToken ct)
    {
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Bytes)
            throw new IOException("Размер файла нейроголоса не совпадает.");
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(Path.Combine(directory, asset.Name), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 65536, FileOptions.Asynchronous);
        var buffer = new byte[65536];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (target.Length + read > asset.Bytes) throw new IOException("Получен файл неверного размера.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        target.Position = 0;
        var digest = await SHA256.HashDataAsync(target, ct).ConfigureAwait(false);
        if (target.Length != asset.Bytes || !Convert.ToHexString(digest).Equals(asset.Hash, StringComparison.Ordinal))
            throw new IOException("Контрольная сумма файла нейроголоса не совпадает.");
    }

    public void Speak(string text, CancellationToken ct)
    {
        try
        {
            if (IsReady)
            {
                using var audio = Render(text, ct).GetAwaiter().GetResult();
                if (ct.IsCancellationRequested) return;
                using var player = new SoundPlayer(audio);
                using var cancelled = ct.Register(player.Stop);
                if (!ct.IsCancellationRequested) player.PlaySync();
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || shutdown.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning("Neural signal voice unavailable: {0}", ex.Message);
        }

        if (!ct.IsCancellationRequested && !shutdown.IsCancellationRequested) WindowsSignalVoice.Speak(text, ct);
    }

    internal async Task<MemoryStream> Render(string text, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown);
        deadline.CancelAfter(TimeSpan.FromSeconds(7));
        await synthLock.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            Process engine;
            lock (gate)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (piper == null || piper.HasExited)
                {
                    piper?.Dispose();
                    piper = Start(PackDirectory);
                }
                engine = piper;
            }
            try { return await Generate(engine, PackDirectory, text, deadline.Token).ConfigureAwait(false); }
            catch
            {
                CloseEngine();
                throw;
            }
        }
        finally { synthLock.Release(); }
    }

    private static Process Start(string directory)
    {
        var info = new ProcessStartInfo(Path.Combine(directory, "piper", "piper.exe"))
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (string argument in new[] { "--model", Voice, "--espeak_data", "piper/espeak-ng-data", "--json-input",
            "--length_scale", "0.78", "--sentence_silence", "0.07", "--quiet" }) info.ArgumentList.Add(argument);
        var process = Process.Start(info) ?? throw new IOException("Не удалось открыть движок нейроголоса.");
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task<MemoryStream> Generate(Process engine, string directory, string text, CancellationToken ct)
    {
        string name = $"signal-{Guid.NewGuid():N}.wav";
        string path = Path.Combine(directory, name);
        try
        {
            string request = JsonSerializer.Serialize(new { text, output_file = name });
            await engine.StandardInput.WriteLineAsync(request.AsMemory(), ct).ConfigureAwait(false);
            await engine.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            if (await engine.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) != name)
                throw new IOException("Нейроголос не вернул ожидаемый аудиофайл.");
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                    if (bytes.Length > 44 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
                        BitConverter.ToInt32(bytes, 4) + 8 == bytes.Length)
                        return new MemoryStream(bytes, writable: false);
                }
                catch (IOException) when (attempt < 11) { }
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
            throw new IOException("Нейроголос вернул неполный аудиофайл.");
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }

    private static bool HasFiles(string directory) =>
        File.Exists(Path.Combine(directory, "piper", "piper.exe")) &&
        File.Exists(Path.Combine(directory, Voice)) && File.Exists(Path.Combine(directory, Voice + ".json")) &&
        Directory.Exists(Path.Combine(directory, "piper", "espeak-ng-data"));

    private void RemoveCache(string path)
    {
        if (!Directory.Exists(path)) return;
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), root, StringComparison.OrdinalIgnoreCase) ||
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Недопустимый путь локального нейроголоса.");
        Directory.Delete(path, recursive: true);
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    private void CloseEngine()
    {
        lock (gate)
        {
            if (piper == null) return;
            Stop(piper);
            piper.Dispose();
            piper = null;
        }
    }

    public void Dispose() => CloseEngine();

    private sealed record Artifact(string Name, string Url, int Bytes, string Hash);
}
