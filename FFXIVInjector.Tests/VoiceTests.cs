using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FFXIVInjector.Core;
internal static class VoiceTests
{
    public static void Run(string root)
    {
        const string version="2026.10.16.0000.0000";
        var json=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
        string package=Path.Combine(root,"voices");Directory.CreateDirectory(Path.Combine(package,"assets"));
        byte[] bytes=new byte[64];"SEDBSSCF"u8.CopyTo(bytes);string hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
        string asset=$"assets/{hash}.scd";File.WriteAllBytes(Path.Combine(package,asset),bytes);
        var manifest=new VoiceResourceManager.Manifest{SchemaVersion=1,Kind="voices-de-fr",GlobalVersion=version,KoreanVersion=version,GlobalExecutableSha256=new string('a',64),Entries=new[]{
            new VoiceResourceManager.Entry{Target="sound/voice/vo_line/1_de.scd",Source="sound/voice/vo_line/1_ko.scd",Status="prepared",CacheFile=asset,Sha256=hash},
            new VoiceResourceManager.Entry{Target="sound/voice/vo_line/1_fr.scd",Source="sound/voice/vo_line/1_ko.scd",Status="prepared",CacheFile=asset,Sha256=hash}}};
        File.WriteAllText(Path.Combine(package,"manifest.json"),JsonSerializer.Serialize(manifest,json));
        string archive=Path.Combine(root,"voices.zip");ZipFile.CreateFromDirectory(package,archive);
        var release=new VoiceResourceManager.Release{SchemaVersion=1,Kind="voices-de-fr",Channel="stable",GlobalVersion=version,KoreanVersion=version,GlobalExecutableSha256=manifest.GlobalExecutableSha256,ArchiveKey="voices.zip",Sha256=FileHash(archive),Bytes=new FileInfo(archive).Length,UnpackedBytes=bytes.Length,Mappings=2,UniqueFiles=1};
        VoiceResourceManager.ValidateRelease(release,version,"stable");
        long actualArchiveBytes=release.Bytes;release.Bytes=5_000_000_000;
        VoiceResourceManager.ValidateRelease(release,version,"stable");release.Bytes=actualArchiveBytes;
        if(VoiceResourceManager.ValidateManifest(manifest,release).Count!=1)throw new Exception("Voices must deduplicate shared Korean bytes");
        foreach(string target in new[]{"sound/voice/vo_line/1_en.scd","sound/voice/vo_line/1_ja.scd","sound/system/shared.scd","music/ex5/bgm_de.scd","cut/ex5/sound/../1_de.scd"})
        {
            string saved=manifest.Entries[0].Target;manifest.Entries[0].Target=target;
            Reject(()=>VoiceResourceManager.ValidateManifest(manifest,release));manifest.Entries[0].Target=saved;
        }
        string savedSource=manifest.Entries[0].Source;manifest.Entries[0].Source="sound/voice/vo_line/2_ko.scd";Reject(()=>VoiceResourceManager.ValidateManifest(manifest,release));manifest.Entries[0].Source=savedSource;
        string savedTarget=manifest.Entries[1].Target;manifest.Entries[1].Target=manifest.Entries[0].Target;Reject(()=>VoiceResourceManager.ValidateManifest(manifest,release));manifest.Entries[1].Target=savedTarget;
        string extracted=Path.Combine(root,"voice-extracted");VoiceResourceManager.ExtractPackage(archive,extracted,release);
        if(IconResourceManager.BuildMappings(null,extracted).Length!=1||IconResourceManager.BuildMappings(null,null).Length!=0)throw new Exception("Voice-only/disabled selection");
        if(!IconResourceManager.BuildMappings(null,extracted)[0].StartsWith("sound/voice/vo_line/1_fr.scd\t",StringComparison.Ordinal))throw new Exception("Legacy DE/FR cache must install French only");
        string image=Path.Combine(root,"voice-test-image");Directory.CreateDirectory(Path.Combine(image,"assets"));byte[] tex=new byte[80];tex[14]=1;string texHash=Convert.ToHexStringLower(SHA256.HashData(tex));string texAsset=$"assets/{texHash}.tex";File.WriteAllBytes(Path.Combine(image,texAsset),tex);
        var images=new IconResourceManager.Manifest{SchemaVersion=1,Kind="icons-12",GlobalVersion=version,KoreanVersion=version,GlobalExecutableSha256=manifest.GlobalExecutableSha256,Entries=new[]{new IconResourceManager.Entry{Target="ui/icon/120000/en/120001.tex",Source="ui/icon/120000/ko/120001.tex",Status="prepared",CacheFile=texAsset,Sha256=texHash}}};
        File.WriteAllText(Path.Combine(image,"manifest.json"),JsonSerializer.Serialize(images,json));
        if(IconResourceManager.BuildMappings(image,extracted).Length!=2||IconResourceManager.BuildMappings(image,null).Length!=1)throw new Exception("Independent image and voice selection");
        File.WriteAllBytes(Path.Combine(extracted,asset),new byte[64]);Reject(()=>IconResourceManager.BuildMappings(null,extracted));
        using(var zip=ZipFile.Open(archive,ZipArchiveMode.Update)){var extra=zip.CreateEntry("../unexpected");using var writer=new StreamWriter(extra.Open());writer.Write("bad");}
        release.Bytes=new FileInfo(archive).Length;release.Sha256=FileHash(archive);Reject(()=>VoiceResourceManager.ExtractPackage(archive,Path.Combine(root,"voice-bad-zip"),release));
        Console.WriteLine("PASS: French-only installation from legacy DE/FR cache, source correspondence, duplicates, shared payloads, archive extraction, corruption/traversal rejection, independent image/voice mapping selections.");
    }
    private static string FileHash(string path){using var stream=File.OpenRead(path);return Convert.ToHexStringLower(SHA256.HashData(stream));}
    private static void Reject(Action action){try{action();}catch(InvalidDataException){return;}throw new Exception("Expected invalid voice package to be rejected");}
}
