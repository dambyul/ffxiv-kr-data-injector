using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIVInjector.Core;
using Lumina.Data.Files;
if(args.Length==3&&args[0]=="verify") {
 var options=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
 var release=JsonSerializer.Deserialize<VoiceResourceManager.Release>(File.ReadAllText(Path.Combine(args[1],"voice-release.json")),options)!;
 VoiceResourceManager.ValidateRelease(release,release.GlobalVersion,"stable");
 VoiceResourceManager.ExtractPackage(Path.Combine(args[1],"voices.zip"),args[2],release);
 var lines=VoiceResourceManager.GetValidatedMappings(args[2]);
 var manifest=JsonSerializer.Deserialize<VoiceResourceManager.Manifest>(File.ReadAllText(Path.Combine(args[2],"manifest.json")),options)!;
 if(lines.Length!=manifest.Entries.Count(e=>e.Target.EndsWith("_fr.scd",StringComparison.Ordinal)))throw new Exception("French mapping count mismatch");
 Console.WriteLine($"Verified extracted voice package: {lines.Length} mappings; {release.UniqueFiles} Korean assets.");
 return;
}
if(args.Length!=4)throw new ArgumentException("VoicePackage <audit directory> <Korean game directory> <global game directory> <new output directory>");
string audit=Path.GetFullPath(args[0]),koRoot=Path.GetFullPath(args[1]),glRoot=Path.GetFullPath(args[2]),output=Path.GetFullPath(args[3]);
if(Directory.Exists(output))throw new IOException("Output already exists");
Directory.CreateDirectory(output);
using var kr=new Lumina.GameData(Path.Combine(koRoot,"sqpack"));using var gl=new Lumina.GameData(Path.Combine(glRoot,"sqpack"));
var json=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
using var input=ZipFile.OpenRead(Path.Combine(audit,"korean-voice-candidates.zip"));
var paths=File.ReadLines(Path.Combine(audit,"voice-mappings.tsv")).Skip(1).Select(l=>l.Split('\t')).Where(r=>r[3]=="candidate-unvalidated"&&r[1].EndsWith("_fr.scd",StringComparison.Ordinal)).GroupBy(r=>r[2]).ToArray();
var mappings=new List<VoiceResourceManager.Entry>();var rejected=new List<string>();var unique=new HashSet<string>();int seen=0;
using(var zip=new ZipArchive(File.Create(Path.Combine(output,"voices.zip")),ZipArchiveMode.Create)) {
 foreach(var group in paths) {
  seen++;var source=kr.GetFile<ScdFile>(group.Key);if(source==null)throw new IOException("Korean source missing: "+group.Key);
  string sha=Convert.ToHexStringLower(SHA256.HashData(source.Data));
  foreach(var row in group) {
   if(sha!=row[5])throw new InvalidDataException("Audit source changed: "+group.Key);
   string reason="";
   try {
    var target=gl.GetFile<ScdFile>(row[1])??throw new InvalidDataException("Global target missing");
    if(!source.Data.AsSpan(8,8).SequenceEqual(target.Data.AsSpan(8,8)))reason="version-platform";
    else if(source.SoundDataCount!=target.SoundDataCount||source.TrackDataCount!=target.TrackDataCount||source.AudioDataCount!=target.AudioDataCount)reason="cue-track-audio-count";
    else {
     for(int i=0;i<source.SoundDataCount;i++) {
      var a=source.GetSound(i).SoundBasicDesc;var b=target.GetSound(i).SoundBasicDesc;
      if(a.TrackCount!=b.TrackCount||a.Type!=b.Type||a.LocalNumber!=b.LocalNumber||a.UserId!=b.UserId){reason="sound-routing";break;}
     }
     if(reason.Length==0)for(int i=0;i<source.AudioDataCount;i++) {
      var a=source.GetAudio(i).AudioBasicDesc;var b=target.GetAudio(i).AudioBasicDesc;
      if(a.Channel!=b.Channel||a.Rate<8000||a.Rate>192000||b.Rate<8000||b.Rate>192000||a.Format!=b.Format||(a.LoopEnd>a.LoopStart)!=(b.LoopEnd>b.LoopStart)){reason="audio-format-channel-rate-loop";break;}
     }
    }
   } catch(Exception e) when(e is not OutOfMemoryException){reason="parse:"+e.GetType().Name;}
   if(reason.Length!=0){rejected.Add(row[1]+"\t"+group.Key+"\t"+reason);continue;}
   mappings.Add(new VoiceResourceManager.Entry{Target=row[1],Source=group.Key,Status="prepared",Sha256=sha,CacheFile=$"assets/{sha}.scd"});
   if(unique.Add(sha)) {
    var entry=zip.CreateEntry($"assets/{sha}.scd",CompressionLevel.Optimal);using var stream=entry.Open();stream.Write(source.Data);
   }
  }
  if(seen%1000==0)Console.WriteLine($"Checked {seen}/{paths.Length}; accepted mappings={mappings.Count}; rejected={rejected.Count}");
 }
 if(mappings.Count==0)throw new InvalidDataException("No compatible voices");
 var manifest=new VoiceResourceManager.Manifest{SchemaVersion=1,Kind="voices-de-fr",GlobalVersion=File.ReadAllText(Path.Combine(glRoot,"ffxivgame.ver")).Trim(),KoreanVersion=File.ReadAllText(Path.Combine(koRoot,"ffxivgame.ver")).Trim(),GlobalExecutableSha256=Hash(Path.Combine(glRoot,"ffxiv_dx11.exe")),Entries=mappings.ToArray()};
 var metadata=zip.CreateEntry("manifest.json",CompressionLevel.Optimal);using var writer=new StreamWriter(metadata.Open(),new UTF8Encoding(false));writer.Write(JsonSerializer.Serialize(manifest,json));
 File.WriteAllText(Path.Combine(output,"manifest.json"),JsonSerializer.Serialize(manifest,json));
}
using(var zip=ZipFile.OpenRead(Path.Combine(output,"voices.zip"))) {
 var m=JsonSerializer.Deserialize<VoiceResourceManager.Manifest>(File.ReadAllText(Path.Combine(output,"manifest.json")),json)!;
 var release=new VoiceResourceManager.Release{SchemaVersion=1,Kind="voices-de-fr",Channel="stable",GlobalVersion=m.GlobalVersion,KoreanVersion=m.KoreanVersion,GlobalExecutableSha256=m.GlobalExecutableSha256,ArchiveKey="voices.zip",Sha256=Hash(Path.Combine(output,"voices.zip")),Bytes=new FileInfo(Path.Combine(output,"voices.zip")).Length,UnpackedBytes=zip.Entries.Where(e=>e.FullName!="manifest.json").Sum(e=>e.Length),Mappings=m.Entries.Length,UniqueFiles=unique.Count};
 VoiceResourceManager.ValidateRelease(release,m.GlobalVersion,"stable");VoiceResourceManager.ValidateManifest(m,release);
 File.WriteAllText(Path.Combine(output,"voice-release.json"),JsonSerializer.Serialize(release,json));
 Console.WriteLine(JsonSerializer.Serialize(release,json));
}
File.WriteAllLines(Path.Combine(output,"rejected.tsv"),new[]{"target\tkoreanSource\treason"}.Concat(rejected),new UTF8Encoding(true));
File.WriteAllLines(Path.Combine(output,"mappings.tsv"),new[]{"target\tkoreanSource\tsha256"}.Concat(mappings.Select(e=>$"{e.Target}\t{e.Source}\t{e.Sha256}")),new UTF8Encoding(true));
Console.WriteLine("Complete. No installation or publication. Playback timing remains unverified.");
static string Hash(string path){using var stream=File.OpenRead(path);return Convert.ToHexStringLower(SHA256.HashData(stream));}
