using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace ThinMarketMonitor;

internal record Release(Version Version, Uri Download, long Size, string Digest);

internal sealed class Updater(HttpClient http)
{
    internal const string Repository = "ksen0v/thin-market-monitor";
    internal const string Asset = "ThinMarketMonitor-win-x64.exe";
    internal static Version Current => new(typeof(Updater).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]);

    public async Task<Release?> Check(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("ThinMarketMonitor/" + Current);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return Parse(json.RootElement, Current);
    }

    internal static Release? Parse(JsonElement root, Version current)
    {
        string tag = root.Str("tag_name");
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || version <= current ||
            root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.Str("name") != Asset || asset.Str("state") != "uploaded") continue;
            string expected = $"https://github.com/{Repository}/releases/download/{tag}/{Asset}";
            string digest = asset.Optional("digest");
            long size = asset.GetProperty("size").Int();
            if (asset.Str("browser_download_url") != expected || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                !ValidHash(digest[7..]) || size is <= 0 or > 300_000_000)
                break;
            return new(version, new Uri(expected), size, digest[7..]);
        }
        throw new IOException("В релизе отсутствует Windows EXE с контрольной суммой SHA-256.");
    }

    public async Task Stage(Release release, IProgress<int> progress, CancellationToken ct)
    {
        string target = Environment.ProcessPath ?? throw new IOException("Путь к EXE недоступен.");
        if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Обновление доступно для автономного EXE.");
        string id = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(Path.GetDirectoryName(target)!, ".tmm-update-" + id);
        Directory.CreateDirectory(stage);
        string payload = Path.Combine(stage, "download.exe");
        string helper = Path.Combine(stage, "updater.exe");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(12));
            var token = timeout.Token;
            using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                byte[] buffer = new byte[131072];
                long received = 0;
                int lastPercent = -1;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, token);
                    if (read == 0) break;
                    received += read;
                    if (received > release.Size) throw new IOException("Размер EXE превышает данные релиза.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    int percent = (int)(received * 100 / release.Size);
                    if (percent != lastPercent) progress.Report(lastPercent = percent);
                }
                if (received != release.Size) throw new IOException("Размер EXE отличается от данных релиза.");
            }

            await using (var content = File.OpenRead(payload))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(content, token));
                if (!digest.Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Контрольная сумма EXE не совпала.");
            }
            await Task.Run(() => File.Copy(target, helper), token);
            token.ThrowIfCancellationRequested();
            using var process = Launch(helper, "--apply-update", id, Environment.ProcessId.ToString(),
                Path.GetFileName(target), release.Digest);
        }
        catch
        {
            TryDelete(payload);
            TryDelete(helper);
            try { Directory.Delete(stage); } catch (IOException) { }
            throw;
        }
    }

    internal static string ReadyName(string id) => @"Local\ThinMarketMonitor-Update-" + id;

    internal static void Apply(string[] args)
    {
        if (args is not ["--apply-update", var id, var pidText, var name, var digest] ||
            !Guid.TryParseExact(id, "N", out _) || !int.TryParse(pidText, out int pid) || !ValidHash(digest) ||
            Path.GetFileName(name) != name || !Path.GetExtension(name).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return;
        string stage = Path.GetDirectoryName(Environment.ProcessPath!)!;
        if (Path.GetFileName(stage) != ".tmm-update-" + id) return;
        string target = Path.Combine(Path.GetDirectoryName(stage)!, name);
        string payload = Path.Combine(stage, "download.exe");
        string backup = Path.Combine(stage, "previous.exe");
        bool replaced = false;
        try
        {
            Process? original = null;
            try { original = Process.GetProcessById(pid); } catch (ArgumentException) { }
            using (original)
            {
                if (original != null && !original.HasExited)
                {
                    if (!string.Equals(original.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Исходный процесс не совпадает с обновляемым EXE.");
                    if (!original.WaitForExit(30_000)) throw new IOException("Приложение не завершилось за 30 секунд.");
                }
            }

            using (var content = File.OpenRead(payload))
                if (!Convert.ToHexString(SHA256.HashData(content)).Equals(digest, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Контрольная сумма загруженного EXE изменилась.");

            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName(id));
            File.Replace(payload, target, backup, ignoreMetadataErrors: false);
            replaced = true;
            using var updated = Launch(target, "--update-ready", id);
            var timeout = Stopwatch.StartNew();
            while (!ready.WaitOne(200))
            {
                if (!updated.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(40)) continue;
                if (!updated.HasExited)
                {
                    updated.Kill();
                    if (!updated.WaitForExit(8000)) throw new IOException("Новый процесс не удалось остановить.");
                }
                throw new IOException("Новая версия не подтвердила запуск.");
            }
            replaced = false;
            TryDelete(backup);
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            try
            {
                if (replaced) File.Replace(backup, target, null);
                using var rollback = Launch(target, "--update-cleanup", id);
            }
            catch (Exception rollback)
            {
                message += "\n" + rollback.Message;
                if (File.Exists(backup)) message += "\nРезервная копия — " + backup;
            }
            MessageBox.Show(message, "Обновление Thin Market Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            TryDelete(payload);
        }
    }

    internal static async Task CleanupHelper(string id)
    {
        string stage = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, ".tmm-update-" + id);
        string helper = Path.Combine(stage, "updater.exe");
        for (int attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(2000);
            try
            {
                if (!Directory.Exists(stage)) return;
                File.Delete(helper);
                if (!Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { return; }
        }
    }

    private static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static Process Launch(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Не удалось запустить EXE.");
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
