using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FFXIVInjector.Core;

/// <summary>Download immutable icon packages and connect the bundled proxy to local TEX files.</summary>
public sealed class IconResourceManager
{
    private readonly string cacheRoot;
    public IconResourceManager() => cacheRoot = Path.GetFullPath(PathConstants.IconPackagePath);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly Regex VersionPattern = new(@"^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$");
    private static readonly Regex DigestPattern = new("^[0-9a-f]{64}$");
    private static readonly Regex TargetPattern = new(@"^ui/icon/(?<family>\d{3})000/(?:(?<language>en|ja|de|fr)/)?\k<family>\d{3}(_hr1)?\.tex$");
    public event Action<string>? OnLog;
    public event Action<double>? OnProgress;
    public event Action<string>? OnStatus;
    public Func<string, string, Task<bool>>? OnClientVersionMismatch { get; set; }
    public sealed class Release
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = "";
        public string Channel { get; set; } = "";
        public string TextVersion { get; set; } = "";
        public string GlobalVersion { get; set; } = "";
        public string KoreanVersion { get; set; } = "";
        public string GlobalExecutableSha256 { get; set; } = "";
        public string ArchiveKey { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Bytes { get; set; }
        public long UnpackedBytes { get; set; }
        public int Mappings { get; set; }
        public int UniqueFiles { get; set; }
    }
    public sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public string Kind { get; set; } = "";
        public string GlobalVersion { get; set; } = "";
        public string KoreanVersion { get; set; } = "";
        public string GlobalExecutableSha256 { get; set; } = "";
        public Entry[] Entries { get; set; } = [];
    }
    public sealed class Entry
    {
        public string Target { get; set; } = "";
        public string Source { get; set; } = "";
        public string Status { get; set; } = "";
        public string CacheFile { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
    private sealed record Installation(string GamePath, string DllSha256, string IniSha256);
    private static string StatePath(string game) => Path.Combine(game, "resource-swap.installation.json");
    public static bool HasInstallation(string gamePath) => File.Exists(StatePath(GameDirectory(gamePath)));
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty metadata");
    private static string GameDirectory(string path)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(Path.Combine(path, "game", "ffxiv_dx11.exe"))) path = Path.Combine(path, "game");
        if (!File.Exists(Path.Combine(path, "ffxiv_dx11.exe"))) throw new FileNotFoundException("Global executable not found");
        return path;
    }
    private static void RequireClosed()
    {
        if (GameProcess.IsRunning()) throw new InvalidOperationException("게임을 종료한 뒤 리소스를 적용하거나 해제해 주세요.");
    }
    public static void ValidateRelease(Release release, string version, string channel, bool allowClientVersionMismatch = false)
    {
        if (release.SchemaVersion != 1 || release.Kind != "icons-12" || (!allowClientVersionMismatch && release.GlobalVersion != version) ||
            release.Channel != channel || !VersionPattern.IsMatch(version) || !VersionPattern.IsMatch(release.KoreanVersion) ||
            !DigestPattern.IsMatch(release.Sha256) || !DigestPattern.IsMatch(release.GlobalExecutableSha256) ||
            release.Bytes is <= 0 or > 1073741824 || release.UnpackedBytes is <= 0 or > 8589934592 ||
            release.Mappings is <= 0 or > 100000 || release.UniqueFiles is <= 0 or > 20000)
            throw new InvalidDataException("Invalid or incompatible image release");
        if (release.ArchiveKey != "resources.zip") throw new InvalidDataException("Invalid resource archive key");
    }
    public static Dictionary<string, string> ValidateManifest(Manifest manifest, Release release)
    {
        if (manifest.SchemaVersion != 1 || manifest.Kind != release.Kind || manifest.GlobalVersion != release.GlobalVersion ||
            manifest.KoreanVersion != release.KoreanVersion || manifest.GlobalExecutableSha256 != release.GlobalExecutableSha256 ||
            manifest.Entries.Length != release.Mappings) throw new InvalidDataException("Manifest/release mismatch");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
        {
            var match = TargetPattern.Match(entry.Target);
            string language = match.Groups["language"].Value;
            string source = language.Length == 0 ? entry.Target : entry.Target.Replace($"/{language}/", "/ko/");
            if (!match.Success || entry.Source != source || entry.Status != "prepared" || !targets.Add(entry.Target) ||
                !DigestPattern.IsMatch(entry.Sha256) || entry.CacheFile != $"assets/{entry.Sha256}.tex")
                throw new InvalidDataException("Invalid image mapping");
            files[entry.CacheFile] = entry.Sha256;
        }
        if (files.Count != release.UniqueFiles) throw new InvalidDataException("Image file count mismatch");
        return files;
    }
    private static void VerifyTexture(string file, string digest)
    {
        if (Hash(file) != digest) throw new InvalidDataException("Image checksum mismatch");
        using var stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[80];
        stream.ReadExactly(header);
        if ((header[14] & 63) != 1) throw new InvalidDataException("Unsupported TEX mip count");
    }
    private static Manifest VerifyPackage(string package, Release release, CancellationToken ct)
    {
        var manifest = Read<Manifest>(Path.Combine(package, "manifest.json"));
        var files = ValidateManifest(manifest, release);
        long bytes = 0;
        foreach (var pair in files)
        {
            ct.ThrowIfCancellationRequested();
            string file = Path.Combine(package, pair.Key);
            VerifyTexture(file, pair.Value);
            bytes += new FileInfo(file).Length;
        }
        if (bytes != release.UnpackedBytes) throw new InvalidDataException("Unpacked size mismatch");
        return manifest;
    }
    public static void ExtractPackage(string archive, string destination, Release release, CancellationToken ct = default)
    {
        if (Directory.Exists(destination)) throw new IOException("Extraction destination already exists");
        if (new FileInfo(archive).Length != release.Bytes || Hash(archive) != release.Sha256)
            throw new InvalidDataException("Archive checksum/size mismatch");
        using var zip = ZipFile.OpenRead(archive);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in zip.Entries)
            if (!names.Add(item.FullName)) throw new InvalidDataException("Duplicate ZIP entry");
        var metadata = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Manifest missing");
        if (metadata.Length > 32 * 1024 * 1024) throw new InvalidDataException("Oversized manifest");
        Manifest manifest;
        using (var stream = metadata.Open()) manifest = JsonSerializer.Deserialize<Manifest>(stream, Json) ?? throw new InvalidDataException("Empty manifest");
        var files = ValidateManifest(manifest, release);
        if (zip.Entries.Count != files.Count + 1) throw new InvalidDataException("Unexpected ZIP contents");
        long total = 0;
        foreach (var item in zip.Entries.Where(e => e.FullName != "manifest.json"))
        {
            if (!files.ContainsKey(item.FullName) || item.Length is < 80 or > 67108864)
                throw new InvalidDataException("Unexpected or oversized TEX");
            total += item.Length;
        }
        if (total != release.UnpackedBytes) throw new InvalidDataException("ZIP size mismatch");
        Directory.CreateDirectory(Path.Combine(destination, "assets"));
        foreach (var pair in files)
        {
            ct.ThrowIfCancellationRequested();
            string file = Path.Combine(destination, pair.Key);
            zip.GetEntry(pair.Key)!.ExtractToFile(file);
            VerifyTexture(file, pair.Value);
        }
        File.WriteAllText(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
    }
    public async Task<string> EnsurePackageAsync(string gamePath, ResourceBranch branch, CancellationToken ct = default)
    {
        string game = GameDirectory(gamePath);
        string version = File.ReadAllText(Path.Combine(game, "ffxivgame.ver")).Trim();
        if (!VersionPattern.IsMatch(version)) throw new InvalidDataException("Invalid global client version");
        string channel = branch == ResourceBranch.Stable ? "stable" : "staging";
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIVInjector/1.1");
        string baseUrl = Config.ResourceBaseUrl.TrimEnd('/') + "/";
        OnStatus?.Invoke("한국어 이미지 버전 확인 중...");
        string versionFile = branch == ResourceBranch.Stable ? "version.txt" : "dev-version.txt";
        using var response = await client.GetAsync($"{baseUrl}{versionFile}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException($"글로벌 {version}용 한국어 이미지 패키지가 아직 배포되지 않았습니다.");
        response.EnsureSuccessStatusCode();
        string remoteVersion = S3ResourceManager.ParseVersionText(await response.Content.ReadAsStringAsync(ct), branch);
        if (!Regex.IsMatch(remoteVersion, @"^\d+(\.\d+){4,6}$")) throw new InvalidDataException("Invalid shared text/resource version");
        string versionRoot = Path.Combine(cacheRoot, "versions", channel, version);
        string localVersion = Path.Combine(versionRoot, "version.txt"), localRelease = Path.Combine(versionRoot, "release.json");
        bool current = File.Exists(localVersion) && File.ReadAllText(localVersion).Trim() == remoteVersion && File.Exists(localRelease);
        string json = "";
        if (current)
        {
            json = File.ReadAllText(localRelease);
            try { current = JsonSerializer.Deserialize<Release>(json, Json)?.ArchiveKey == "resources.zip"; }
            catch (JsonException) { current = false; }
        }
        if (!current)
        {
            string metadata = await client.GetStringAsync($"{baseUrl}data.json?t={remoteVersion}", ct);
            using var document = JsonDocument.Parse(metadata);
            if (!document.RootElement.TryGetProperty("imageResources", out var images))
                throw new InvalidOperationException("한국어 이미지 패키지가 아직 배포되지 않았습니다.");
            json = images.GetRawText();
        }
        if (json.Length > 65536) throw new InvalidDataException("Oversized release descriptor");
        var release = JsonSerializer.Deserialize<Release>(json, Json) ?? throw new InvalidDataException("Empty release");
        if (release.GlobalVersion != version)
        {
            bool proceed = OnClientVersionMismatch != null && await OnClientVersionMismatch(release.GlobalVersion, version);
            if (!proceed) throw new OperationCanceledException(ct);
        }
        ValidateRelease(release, version, channel, allowClientVersionMismatch: true);
        if (release.TextVersion != remoteVersion) throw new InvalidDataException("Text/image release versions differ; retry after publication completes");
        Directory.CreateDirectory(cacheRoot);
        string package = Path.Combine(cacheRoot, release.Sha256);
        if (Directory.Exists(package))
        {
            OnStatus?.Invoke("저장된 한국어 이미지 검증 중...");
            try { VerifyPackage(package, release, ct); SaveVersion(); OnLog?.Invoke("한국어 이미지 최신 버전: 다운로드 없이 캐시 재사용"); return package; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            { OnLog?.Invoke("손상된 이미지 캐시를 다시 다운로드합니다."); }
        }
        string stage = Path.Combine(cacheRoot, ".stage-" + Guid.NewGuid().ToString("N"));
        string archive = stage + ".zip";
        try
        {
            OnStatus?.Invoke("한국어 이미지 다운로드 중...");
            using (var download = await client.GetAsync(baseUrl + release.ArchiveKey + "?v=" + release.Sha256, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                download.EnsureSuccessStatusCode();
                using var input = await download.Content.ReadAsStreamAsync(ct);
                using var output = File.Create(archive);
                byte[] buffer = new byte[131072];
                long count = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) != 0)
                {
                    count += read;
                    if (count > release.Bytes) throw new InvalidDataException("Download exceeds expected size");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    OnProgress?.Invoke(100.0 * count / release.Bytes);
                }
                if (count != release.Bytes) throw new InvalidDataException("Incomplete resource download");
            }
            OnStatus?.Invoke("한국어 이미지 압축 해제·검증 중...");
            ExtractPackage(archive, stage, release, ct);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(package))
            {
                string damaged = package + ".damaged-" + Guid.NewGuid().ToString("N");
                Directory.Move(package, damaged);
                try { Directory.Move(stage, package); }
                catch { Directory.Move(damaged, package); throw; }
                Directory.Delete(damaged, true);
            }
            else Directory.Move(stage, package);
            SaveVersion();
            return package;
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        void SaveVersion()
        {
            Directory.CreateDirectory(versionRoot);
            AtomicText(localRelease, json);
            AtomicText(localVersion, remoteVersion + "\n");
        }
    }
    private static byte[] LoaderBytes()
    {
        using var stream = typeof(IconResourceManager).Assembly.GetManifestResourceStream("FFXIVInjector.Core.Native.dinput8.dll")
            ?? throw new InvalidOperationException("Bundled image loader missing");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    private static bool IsLegacyLoader(string game)
    {
        string dll = Path.Combine(game, "dinput8.dll"), ini = Path.Combine(game, "resource-swap.ini");
        if (!File.Exists(dll) || !File.Exists(ini) || File.Exists(Path.Combine(game, "config.xivalexinit.json"))) return false;
        string digest = Hash(dll);
        // Fingerprint of the previously verified development loader, plus this bundled build.
        if (digest != "a9cf4926f0dd037c440091323bbcd1bfd1d2697a0158b873350a2aa1b10de570" &&
            digest != Convert.ToHexStringLower(SHA256.HashData(LoaderBytes()))) return false;
        var lines = File.ReadAllLines(ini);
        return lines.Length == 2 && Path.IsPathFullyQualified(lines[0]) && !lines[0].Any(char.IsControl) && DigestPattern.IsMatch(lines[1]);
    }
    public static void CheckInstallation(string gamePath)
    {
        RequireClosed();
        string game = GameDirectory(gamePath);
        string dll = Path.Combine(game, "dinput8.dll"), ini = Path.Combine(game, "resource-swap.ini");
        if (File.Exists(StatePath(game)))
        {
            var state = Read<Installation>(StatePath(game));
            if (state.GamePath != game || !File.Exists(dll) || !File.Exists(ini) || Hash(dll) != state.DllSha256 || Hash(ini) != state.IniSha256)
                throw new InvalidOperationException("기존 이미지 로더 파일이 변경되어 자동 덮어쓰기를 중단했습니다.");
        }
        else if ((File.Exists(dll) || File.Exists(ini) || File.Exists(Path.Combine(game, "config.xivalexinit.json"))) && !IsLegacyLoader(game))
            throw new InvalidOperationException("기존 dinput8 로더가 있습니다. 기존 설치 도구로 제거한 뒤 다시 적용해 주세요.");
    }
    public void Install(string gamePath, string package)
    {
        CheckInstallation(gamePath);
        string game = GameDirectory(gamePath);
        var manifest = Read<Manifest>(Path.Combine(package, "manifest.json"));
        string version = File.ReadAllText(Path.Combine(game, "ffxivgame.ver")).Trim();
        string exeHash = Hash(Path.Combine(game, "ffxiv_dx11.exe"));
        var release = new Release { Kind = "icons-12", GlobalVersion = manifest.GlobalVersion, KoreanVersion = manifest.KoreanVersion,
            GlobalExecutableSha256 = manifest.GlobalExecutableSha256, Mappings = manifest.Entries.Length,
            UniqueFiles = manifest.Entries.Select(e => e.CacheFile).Distinct().Count() };
        var files = ValidateManifest(manifest, release);
        foreach (var pair in files) VerifyTexture(Path.Combine(package, pair.Key), pair.Value);
        if (HasInstallation(game) && Hash(Path.Combine(game, "dinput8.dll")) == Convert.ToHexStringLower(SHA256.HashData(LoaderBytes())))
        {
            var existingIni = File.ReadAllLines(Path.Combine(game, "resource-swap.ini"));
            var expected = manifest.Entries.Select(e => $"{e.Target}\t{Path.GetFullPath(Path.Combine(package, e.CacheFile))}\t{e.Sha256}");
            if (existingIni.Length >= 2 && existingIni[1] == exeHash && File.Exists(existingIni[0]) && File.ReadAllLines(existingIni[0]).SequenceEqual(expected))
            {
                OnLog?.Invoke("기존 한국어 이미지 로더와 매핑을 유지합니다.");
                return;
            }
        }
        string runtime = Path.Combine(cacheRoot, "runtime", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(game))));
        Directory.CreateDirectory(runtime);
        string map = Path.Combine(runtime, "mappings-" + Guid.NewGuid().ToString("N") + ".tsv");
        var lines = manifest.Entries.Select(e =>
        {
            string file = Path.GetFullPath(Path.Combine(package, e.CacheFile));
            if (file.Length >= 260 || file.Any(char.IsControl)) throw new InvalidDataException("Resource path too long/invalid");
            return $"{e.Target}\t{file}\t{e.Sha256}";
        });
        File.WriteAllText(map, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        string dll = Path.Combine(game, "dinput8.dll"), ini = Path.Combine(game, "resource-swap.ini"), statePath = StatePath(game);
        byte[]? oldDll = File.Exists(dll) ? File.ReadAllBytes(dll) : null;
        byte[]? oldIni = File.Exists(ini) ? File.ReadAllBytes(ini) : null;
        byte[]? oldState = File.Exists(statePath) ? File.ReadAllBytes(statePath) : null;
        if (oldState == null && oldDll != null && oldIni != null)
        {
            string backup = Path.Combine(runtime, "legacy-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            File.WriteAllBytes(Path.Combine(backup, "dinput8.dll"), oldDll);
            File.WriteAllBytes(Path.Combine(backup, "resource-swap.ini"), oldIni);
            OnLog?.Invoke("기존 개발용 이미지 로더를 확인했습니다. 백업 후 새 로더로 교체합니다.");
        }
        try
        {
            RequireClosed();
            File.WriteAllBytes(dll, LoaderBytes());
            AtomicText(ini, map + "\n" + exeHash + "\n");
            AtomicText(statePath, JsonSerializer.Serialize(new Installation(game, Hash(dll), Hash(ini)), Json));
        }
        catch
        {
            RestoreFile(dll, oldDll); RestoreFile(ini, oldIni); RestoreFile(statePath, oldState);
            File.Delete(map); throw;
        }
        OnLog?.Invoke($"한국어 이미지 적용 완료: {manifest.Entries.Length:N0}개 경로, 모든 글로벌 언어에 적용됩니다.");
    }
    private static void AtomicText(string path, string text)
    {
        string temp = path + ".tmp";
        try { File.WriteAllText(temp, text, new UTF8Encoding(false)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void RestoreFile(string path, byte[]? bytes) { if (bytes == null) { if (File.Exists(path)) File.Delete(path); } else File.WriteAllBytes(path, bytes); }
    public void Uninstall(string gamePath)
    {
        string game = GameDirectory(gamePath);
        if (!File.Exists(StatePath(game))) return;
        CheckInstallation(game);
        string dll = Path.Combine(game, "dinput8.dll"), ini = Path.Combine(game, "resource-swap.ini"), state = StatePath(game);
        var saved = new[] { dll, ini, state }.ToDictionary(p => p, File.ReadAllBytes);
        try { File.Delete(ini); File.Delete(dll); File.Delete(state); }
        catch { foreach (var pair in saved) File.WriteAllBytes(pair.Key, pair.Value); throw; }
        OnLog?.Invoke("한국어 이미지 적용 해제 완료. 다운로드한 패키지는 재사용할 수 있습니다.");
    }
}
