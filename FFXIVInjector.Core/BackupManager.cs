namespace FFXIVInjector.Core;

public class BackupManager
{
    private const string BackupRoot = @"resources\backup";

    // Target files: 000000 (boot/common) and 0a0000 (game data)
    private static readonly string[] Targets = 
    {
        "sqpack/ffxiv/000000.win32.index",
        "sqpack/ffxiv/000000.win32.index2",
        "sqpack/ffxiv/000000.win32.dat0", 
        "sqpack/ffxiv/0a0000.win32.index",
        "sqpack/ffxiv/0a0000.win32.index2",
        "sqpack/ffxiv/0a0000.win32.dat0", 
        "sqpack/ffxiv/0a0000.win32.dat1" 
    };

    public string CreateBackup(string gamePath, string backupName = "")
    {
        if (GameProcess.IsRunning())
        {
            throw new InvalidOperationException("FFXIV is currently running. Cannot create backup while files are locked.");
        }

        PathConstants.EnsureDirectories();
        
        string folderName;
        if (string.IsNullOrWhiteSpace(backupName))
        {
            folderName = DateTime.Now.ToString("yyyyMMddHHmm");
        }
        else
        {
            folderName = backupName;
            // Basic validation for safety, though UI handles it too
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                folderName = folderName.Replace(c, '_');
            }
        }

        var backupDir = Path.Combine(PathConstants.BackupPath, folderName);
        
        // Handle collision: append timestamp if exists
        if (Directory.Exists(backupDir))
        {
             if (!string.IsNullOrWhiteSpace(backupName))
             {
                 backupDir += "_" + DateTime.Now.ToString("yyyyMMddHHmm");
                 folderName = Path.GetFileName(backupDir); 
             }
        }
        
        Directory.CreateDirectory(backupDir);

        foreach (var relativePath in Targets)
        {
            var source = Path.Combine(gamePath, relativePath);
            if (File.Exists(source))
            {
                var dest = Path.Combine(backupDir, relativePath);
                var destDir = Path.GetDirectoryName(dest);
                if (destDir != null) Directory.CreateDirectory(destDir);
                
                File.Copy(source, dest, true);
            }
        }

        return folderName;
    }

    public void RenameBackup(string oldName, string newName)
    {
        var oldDir = Path.Combine(PathConstants.BackupPath, oldName);
        var newDir = Path.Combine(PathConstants.BackupPath, newName);

        if (!Directory.Exists(oldDir)) return;
        if (Directory.Exists(newDir)) throw new IOException($"Backup with name '{newName}' already exists.");

        Directory.Move(oldDir, newDir);
    }

    public void RestoreBackup(string gamePath, string backupId)
    {
        if (GameProcess.IsRunning())
        {
            throw new InvalidOperationException("FFXIV is currently running. Cannot restore backup while game is active.");
        }
        
        var backupDir = Path.Combine(PathConstants.BackupPath, backupId);
        if (!Directory.Exists(backupDir)) throw new DirectoryNotFoundException($"Backup {backupId} not found");

        foreach (var relativePath in Targets)
        {
            var source = Path.Combine(backupDir, relativePath);
            if (File.Exists(source))
            {
                var dest = Path.Combine(gamePath, relativePath);
                File.Copy(source, dest, true); // Overwrite game files with backup
            }
        }
    }

    public void DeleteBackup(string backupId)
    {
        var backupDir = Path.Combine(PathConstants.BackupPath, backupId);
        if (Directory.Exists(backupDir))
        {
            Directory.Delete(backupDir, true);
        }
    }
    
    public List<string> GetBackups()
    {
        if (!Directory.Exists(PathConstants.BackupPath)) return new List<string>();

        var dirInfo = new DirectoryInfo(PathConstants.BackupPath);
        return dirInfo.GetDirectories()
                      .OrderByDescending(d => d.CreationTime)
                      .Select(d => d.Name)
                      .ToList();
    }
}
