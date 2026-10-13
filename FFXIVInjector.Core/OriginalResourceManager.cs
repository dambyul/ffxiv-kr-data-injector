using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FFXIVInjector.Core;

/// <summary>Cache and restore publisher-supplied pristine files; never back up patched files.</summary>
public sealed class OriginalResourceManager
{
    public static readonly string[] FileNames = ["000000.win32.dat0", "000000.win32.index", "000000.win32.index2",
        "0a0000.win32.dat0", "0a0000.win32.dat1", "0a0000.win32.index", "0a0000.win32.index2"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public event Action<string>? OnLog;
    public event Action<double>? OnProgress;
    public sealed class Release
    {
        public int SchemaVersion { get; set; }
        public string ArchiveKey { get; set; } = "";
        public string TextVersion { get; set; } = "";
        public string GlobalVersion { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public long Bytes { get; set; }
        public long UnpackedBytes { get; set; }
    }
    public sealed class OriginalFile
    {
        public string Name { get; set; } = "";
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = "";
    }
    public sealed class Manifest
    {
        public int SchemaVersion { get; set; }
        public string GlobalVersion { get; set; } = "";
        public OriginalFile[] Files { get; set; } = [];
    }
    public static string VersionPath(ResourceBranch branch) => Path.Combine(PathConstants.OriginalPackagePath, branch.ToString(), "version.txt");
    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static Manifest Validate(string package, string? globalVersion = null)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(package, "manifest.json")), Json)
            ?? throw new InvalidDataException("원본 파일 목록이 없습니다.");
        if (manifest.SchemaVersion != 1 || !Regex.IsMatch(manifest.GlobalVersion, @"^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$") ||
            globalVersion != null && manifest.GlobalVersion != globalVersion ||
            !manifest.Files.Select(f => f.Name).Order().SequenceEqual(FileNames.Order()))
            throw new InvalidDataException("원본 파일 목록 또는 글로벌 버전이 일치하지 않습니다.");
        foreach (var file in manifest.Files)
        {
            string path = Path.Combine(package, file.Name);
            if (!Regex.IsMatch(file.Sha256, "^[0-9a-f]{64}$") || file.Bytes < 1024 ||
                !File.Exists(path) || new FileInfo(path).Length != file.Bytes || Hash(path) != file.Sha256)
                throw new InvalidDataException("원본 파일 검증 실패: " + file.Name);
        }
        return manifest;
    }
    public async Task<string> EnsurePackageAsync(ResourceBranch branch, CancellationToken ct = default, string? expectedVersion = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIVInjector/1.1");
        string baseUrl = Config.ResourceBaseUrl.TrimEnd('/') + "/";
        string suffix = "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // The pristine global originals are common to both text branches.
        string versionKey = "version.txt";
        string version = expectedVersion ?? S3ResourceManager.ParseVersionText(await client.GetStringAsync(baseUrl + versionKey + suffix, ct), ResourceBranch.Stable);
        string state = Path.Combine(PathConstants.OriginalPackagePath, branch.ToString());
        string releasePath = Path.Combine(state, "release.json");
        Release? release = null;
        if (File.Exists(VersionPath(branch)) && File.ReadAllText(VersionPath(branch)).Trim() == version && File.Exists(releasePath))
        {
            try { release = JsonSerializer.Deserialize<Release>(File.ReadAllText(releasePath), Json); }
            catch (JsonException) { }
        }
        if (release == null)
        {
            using var metadata = JsonDocument.Parse(await client.GetStringAsync(baseUrl + "data.json" + suffix, ct));
            if (!metadata.RootElement.TryGetProperty("originalFiles", out var descriptor))
                throw new InvalidDataException("원본 패키지가 게시되지 않았습니다. original.zip과 data.json을 먼저 업로드해 주세요.");
            release = descriptor.Deserialize<Release>(Json);
        }
        if (release == null || release.SchemaVersion != 1 || release.TextVersion != version ||
            release.ArchiveKey != "original.zip" || !Regex.IsMatch(release.Sha256, "^[0-9a-f]{64}$") || release.Bytes <= 0 || release.UnpackedBytes <= 0)
            throw new InvalidDataException("원본 패키지의 공유 버전 또는 메타데이터가 일치하지 않습니다.");
        string package = Path.Combine(PathConstants.OriginalPackagePath, release.Sha256);
        bool valid = false;
        if (Directory.Exists(package))
        {
            try { Validate(package, release.GlobalVersion); valid = true; }
            catch (IOException) { }
            catch (JsonException) { }
        }
        if (!valid)
        {
            Directory.CreateDirectory(PathConstants.OriginalPackagePath);
            string zipPath = Path.Combine(PathConstants.OriginalPackagePath, Guid.NewGuid() + ".zip");
            string staging = zipPath + ".files";
            try
            {
                OnLog?.Invoke("original.zip 다운로드 중...");
                using (var response = await client.GetAsync(baseUrl + release.ArchiveKey + suffix, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    await using var output = File.Create(zipPath);
                    byte[] buffer = new byte[1024 * 1024]; long total = 0; int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        total += read;
                        if (total > release.Bytes) throw new InvalidDataException("원본 ZIP 크기 초과");
                        await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        OnProgress?.Invoke(100.0 * total / release.Bytes);
                    }
                }
                if (new FileInfo(zipPath).Length != release.Bytes || Hash(zipPath) != release.Sha256)
                    throw new InvalidDataException("원본 ZIP 검증 실패");
                Directory.CreateDirectory(staging);
                using (var archive = ZipFile.OpenRead(zipPath))
                {
                    var allowed = FileNames.Append("manifest.json").ToHashSet(StringComparer.Ordinal);
                    if (archive.Entries.Count != 8 || !archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal).SetEquals(allowed) ||
                        archive.Entries.Sum(e => e.Length) > release.UnpackedBytes + 65536)
                        throw new InvalidDataException("원본 ZIP 구성 오류");
                    foreach (var entry in archive.Entries) { ct.ThrowIfCancellationRequested(); entry.ExtractToFile(Path.Combine(staging, entry.FullName)); }
                }
                var manifest = Validate(staging, release.GlobalVersion);
                if (manifest.Files.Sum(f => f.Bytes) != release.UnpackedBytes) throw new InvalidDataException("원본 크기 불일치");
                if (Directory.Exists(package)) Directory.Delete(package, true);
                Directory.Move(staging, package);
            }
            finally
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(state);
        File.WriteAllText(releasePath, JsonSerializer.Serialize(release));
        File.WriteAllText(VersionPath(branch), version);
        OnLog?.Invoke("원본 파일 준비 완료: " + release.GlobalVersion);
        return package;
    }
    public void Restore(string gamePath, string package, CancellationToken ct = default)
    {
        string workspace = PrepareWorkspace(gamePath, package, ct);
        try { Commit(gamePath, workspace, null, ct); OnLog?.Invoke("폰트·텍스트 원본 7개 파일 복원 완료"); }
        finally { CleanupWorkspace(workspace); }
    }
    public string PrepareWorkspace(string gamePath, string package, CancellationToken ct = default)
    {
        if (GameProcess.IsRunning()) throw new InvalidOperationException("게임을 종료한 뒤 복원해 주세요.");
        string game = Path.GetFullPath(gamePath);
        var version = File.ReadAllText(Path.Combine(game, "ffxivgame.ver")).Trim();
        var manifest = Validate(package, version);
        string destination = Path.Combine(game, "sqpack", "ffxiv");
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException(destination);
        // Check all existing targets before preparing replacements. Patched data is never backed up.
        foreach (var file in manifest.Files)
        {
            string path = Path.Combine(destination, file.Name);
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        string workspace = Path.Combine(game, ".injector-work-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(workspace, "sqpack", "ffxiv"));
            File.WriteAllText(Path.Combine(workspace, "ffxivgame.ver"), version);
            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(Path.Combine(package, file.Name), Path.Combine(workspace, "sqpack", "ffxiv", file.Name));
            }
            ct.ThrowIfCancellationRequested();
            return workspace;
        }
        catch { if (Directory.Exists(workspace)) Directory.Delete(workspace, true); throw; }
    }
    public void Commit(string gamePath, string workspace, Action? finishImages = null, CancellationToken ct = default, bool preserveFonts = false)
    {
        ct.ThrowIfCancellationRequested();
        if (GameProcess.IsRunning()) throw new InvalidOperationException("게임을 종료한 뒤 적용해 주세요.");
        string source = Path.Combine(workspace, "sqpack", "ffxiv");
        string destination = Path.Combine(gamePath, "sqpack", "ffxiv");
        if (File.ReadAllText(Path.Combine(gamePath, "ffxivgame.ver")).Trim() != File.ReadAllText(Path.Combine(workspace, "ffxivgame.ver")).Trim())
            throw new InvalidOperationException("작업 중 게임 버전이 변경되었습니다.");
        var targets = FileNames.Where(name => !preserveFonts || !name.StartsWith("000000.", StringComparison.Ordinal)).ToArray();
        foreach (string name in targets)
        {
            if (!File.Exists(Path.Combine(source, name))) throw new FileNotFoundException(name);
            using var probe = new FileStream(Path.Combine(destination, name), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        ct.ThrowIfCancellationRequested();
        var replaced = new List<(string Target, string Previous)>();
        try
        {
            // Once commit starts, finish all files. Temporary previous files exist only for rollback.
            foreach (string name in targets)
            {
                string target = Path.Combine(destination, name);
                string previous = Path.Combine(workspace, name + ".previous");
                File.Replace(Path.Combine(source, name), target, previous);
                replaced.Add((target, previous));
            }
            finishImages?.Invoke();
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            foreach (var (target, previous) in replaced.AsEnumerable().Reverse())
            {
                try { File.Move(previous, target, true); }
                catch (Exception ex) { errors.Add(ex); }
            }
            if (errors.Count > 0) throw new AggregateException("기존 상태 복구 중 오류가 발생했습니다. 복구 파일 위치: " + workspace, errors.Prepend(failure));
            throw;
        }
        foreach (var (_, previous) in replaced)
        {
            try { File.Delete(previous); }
            catch (IOException ex) { OnLog?.Invoke("적용 완료 후 임시 파일 정리 실패: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { OnLog?.Invoke("적용 완료 후 임시 파일 정리 실패: " + ex.Message); }
        }
    }
    public static void CleanupWorkspace(string workspace)
    {
        // Preserve recovery files if a filesystem failure also prevented rollback.
        if (Directory.Exists(workspace) && !Directory.EnumerateFiles(workspace, "*.previous").Any())
            Directory.Delete(workspace, true);
    }
}
