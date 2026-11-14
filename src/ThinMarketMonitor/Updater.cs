using System.Diagnostics;
using Microsoft.Win32;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace ThinMarketMonitor;

internal record Release(Version Version, Uri Download, long Size, string Digest, bool Installed = false);

internal sealed class Updater(HttpClient http)
{
    internal const string Repository = "ksen0v/thin-market-monitor";
    internal const string Asset = "ThinMarketMonitor-win-x64.exe";
    internal const string SetupAsset = "ThinMarketMonitor-Setup-win-x64.exe";
    internal const string Executable = "ThinMarketMonitor.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ThinMarketMonitor_is1";
    public static bool Installed => RegisteredExecutable() is { } path &&
        string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
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
        return Parse(json.RootElement, Current, Installed);
    }

    internal static Release? Parse(JsonElement root, Version current, bool installed = false)
    {
        string tag = root.Str("tag_name");
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || version <= current ||
            root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;

        string assetName = installed ? SetupAsset : Asset;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (asset.Str("name") != assetName || asset.Str("state") != "uploaded") continue;
            string expected = $"https://github.com/{Repository}/releases/download/{tag}/{assetName}";
            string digest = asset.Optional("digest");
            long size = asset.GetProperty("size").GetInt64();
            if (asset.Str("browser_download_url") != expected || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                !ValidHash(digest[7..]) || size is <= 0 or > 300_000_000)
                break;
            return new(version, new Uri(expected), size, digest[7..], installed);
        }
        throw new IOException($"В релизе отсутствует {assetName} с контрольной суммой SHA-256.");
    }

    public async Task Stage(Release release, IProgress<int> progress, CancellationToken ct)
    {
        string target = Environment.ProcessPath ?? throw new IOException("Путь к EXE недоступен.");
        if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Обновление доступно для автономного EXE.");
        if (release.Installed != Installed)
            throw new IOException("Режим установки изменился. Запустите нужную копию и проверьте обновление снова.");
        string id = Guid.NewGuid().ToString("N");
        string stage = release.Installed ? SetupStage(id) : Path.Combine(Path.GetDirectoryName(target)!, ".tmm-update-" + id);
        Directory.CreateDirectory(stage);
        string payload = Path.Combine(stage, release.Installed ? SetupAsset : "download.exe");
        string helper = Path.Combine(stage, "updater.exe");
        try
        {
            await Download(release, payload, progress, ct);
            await Task.Run(() => File.Copy(target, helper), ct);
            ct.ThrowIfCancellationRequested();
            using var process = release.Installed
                ? Launch(helper, "--apply-setup-update", id, Environment.ProcessId.ToString(), release.Version.ToString(), release.Digest, target)
                : Launch(helper, "--apply-update", id, Environment.ProcessId.ToString(), Path.GetFileName(target), release.Digest);
        }
        catch
        {
            TryDelete(payload);
            TryDelete(helper);
            try { Directory.Delete(stage); } catch (IOException) { }
            throw;
        }
    }

    private async Task Download(Release release, string payload, IProgress<int> progress, CancellationToken ct)
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

        await using var content = File.OpenRead(payload);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(content, token));
        if (!digest.Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Контрольная сумма EXE не совпала.");
    }

    internal static string ReadyName(string id) => @"Local\ThinMarketMonitor-Update-" + id;

    private static string SetupStage(string id) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThinMarketMonitor", "Updates", id);

    private static string? RegisteredExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") is string location && Path.IsPathFullyQualified(location)
            ? Path.GetFullPath(Path.Combine(location, Executable)) : null;
    }

    private static Version? RegisteredVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return Version.TryParse(key?.GetValue("DisplayVersion") as string, out var version) ? version : null;
    }

    internal static int ApplySetup(string[] args)
    {
        if (args is not ["--apply-setup-update", var id, var pidText, var versionText, var digest, var target] ||
            !Guid.TryParseExact(id, "N", out _) || !int.TryParse(pidText, out int pid) || pid <= 0 || !ValidHash(digest) ||
            !Version.TryParse(versionText, out var version) || version <= Current || version.Build < 0 || version.Revision >= 0 ||
            !string.Equals(Environment.ProcessPath, Path.Combine(SetupStage(id), "updater.exe"), StringComparison.OrdinalIgnoreCase))
            return 1;

        string stage = SetupStage(id);
        string payload = Path.Combine(stage, SetupAsset);
        string log = Path.Combine(stage, "setup.log");
        bool mayRestart = false;
        try
        {
            using var updateLock = new Mutex(true, @"Local\ThinMarketMonitor-SetupUpdate", out bool ownsUpdate);
            if (!ownsUpdate) throw new IOException("Другое обновление уже выполняется.");
            if (!IsRegisteredTarget() || RegisteredVersion() != Current)
                throw new IOException("Не удалось подтвердить путь и версию установленного приложения.");
            WaitForOriginal(pid, target);
            mayRestart = true;

            using (var content = File.OpenRead(payload))
            {
                if (!Convert.ToHexString(SHA256.HashData(content)).Equals(digest, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Контрольная сумма загруженного Setup изменилась.");
                var info = FileVersionInfo.GetVersionInfo(payload);
                if (FileVersion(info) != version)
                    throw new IOException($"Версия Setup не совпадает с v{version}.");
            }

            using var installer = Launch(payload, "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                "/NOCLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS", "/RESTARTEXITCODE=3010",
                "/DIR=" + Path.GetDirectoryName(target), "/LOG=" + log);
            installer.WaitForExit();
            if (installer.ExitCode != 0)
                throw new IOException(installer.ExitCode == 3010 ? "Для завершения установки требуется перезапуск Windows."
                    : $"Setup завершился с кодом {installer.ExitCode}.");
            if (!IsRegisteredTarget() || RegisteredVersion() != version ||
                FileVersion(FileVersionInfo.GetVersionInfo(target)) != version)
                throw new IOException($"Setup не подтвердил установку v{version}.");

            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName(id));
            using var updated = Launch(target, "--setup-update-ready", id);
            mayRestart = false;
            var timeout = Stopwatch.StartNew();
            while (!ready.WaitOne(200))
                if (updated.HasExited || timeout.Elapsed >= TimeSpan.FromSeconds(40))
                    throw new IOException("Новая версия установлена. Запустите Thin Market Monitor из меню «Пуск».");
            TryDelete(log);
            return 0;
        }
        catch (Exception ex)
        {
            string message = ex.Message;
            if (mayRestart && IsRegisteredTarget() && File.Exists(target))
            {
                try { using var previous = Launch(target, "--setup-update-cleanup", id); }
                catch (Exception restart) { message += "\n" + restart.Message; }
            }
            if (File.Exists(log)) message += "\nЖурнал установки — " + log;
            try { File.WriteAllText(Path.Combine(stage, "error.txt"), message); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            MessageBox.Show(message, "Обновление Thin Market Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return 1;
        }
        finally { TryDelete(payload); }

        bool IsRegisteredTarget() => string.Equals(RegisteredExecutable(), target, StringComparison.OrdinalIgnoreCase);
    }

    private static Version FileVersion(FileVersionInfo info) => new(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);

    private static void WaitForOriginal(int pid, string target)
    {
        Process? original = null;
        try { original = Process.GetProcessById(pid); } catch (ArgumentException) { }
        using (original)
        {
            if (original == null || original.HasExited) return;
            if (!string.Equals(original.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Исходный процесс не совпадает с обновляемым EXE.");
            if (!original.WaitForExit(30_000)) throw new IOException("Приложение не завершилось за 30 секунд.");
        }
    }

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
            WaitForOriginal(pid, target);

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

    internal static async Task CleanupHelper(string id, bool installed = false)
    {
        string stage = installed ? SetupStage(id) : Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, ".tmm-update-" + id);
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
