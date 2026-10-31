using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFXIVInjector.Core;
using FFXIVInjector.UI.Services;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace FFXIVInjector.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanGlobalInteract))]
    [NotifyPropertyChangedFor(nameof(CanInjectOrCancel))]
    private bool _isInjecting = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanGlobalInteract))]
    [NotifyPropertyChangedFor(nameof(CanInjectOrCancel))]
    private bool _isRestoring = false;

    [ObservableProperty]
    private bool _isLogExpanded = false;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanInjectOrCancel))]
    private bool _isDownloadingResources = false;

    public bool CanInteract => !IsInjecting && !IsRestoring && !IsDownloadingResources && IsGamePathValid;
    public bool CanGlobalInteract => !IsInjecting && !IsRestoring && !IsDownloadingResources;
    public bool CanCancel => IsInjecting;

    public bool CanInjectOrCancel => true; // Always allow interaction to handle the toggle

    [ObservableProperty]
    private bool _hasFailed = false;

    private CancellationTokenSource? _injectCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGamePathValid))] 
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanInjectOrCancel))]
    private string _gamePath = "Auto-detecting...";

    public bool IsGamePathValid => !string.IsNullOrEmpty(GamePath) && 
                                   (File.Exists(Path.Combine(GamePath, "sqpack", "ffxiv", "0a0000.win32.index")) || 
                                    File.Exists(Path.Combine(GamePath, "game", "sqpack", "ffxiv", "0a0000.win32.index")));

    private string GetActualGamePath()
    {
        if (string.IsNullOrEmpty(GamePath)) return "";
        if (File.Exists(Path.Combine(GamePath, "sqpack", "ffxiv", "0a0000.win32.index"))) return GamePath;
        
        string gameSubPath = Path.Combine(GamePath, "game");
        if (File.Exists(Path.Combine(gameSubPath, "sqpack", "ffxiv", "0a0000.win32.index"))) return gameSubPath;
        
        return GamePath;
    }

    partial void OnGamePathChanged(string value)
    {
        OnPropertyChanged(nameof(IsGamePathValid));
        OnPropertyChanged(nameof(CanInteract));
        
        if (string.IsNullOrEmpty(value)) return;

        if (IsGamePathValid)
        {
            StatusMessage = LocalizationService.Instance.GetString("Status.GamePathValid");
        }
        else
        {
            StatusMessage = LocalizationService.Instance.GetString("Status.GamePathError");
        }
    }

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _logText = "";


    [ObservableProperty]
    private GameLanguage _selectedLanguage = GameLanguage.Japanese;

    public GameLanguage[] TargetLanguages => Enum.GetValues<GameLanguage>();

    [ObservableProperty]
    private ResourceBranch _selectedBranch = ResourceBranch.Stable;

    partial void OnSelectedBranchChanged(ResourceBranch value)
    {
        RunResourceCheck(value);
    }

    public MainViewModel()
    {
        var finder = new GameFinder();
        var path = finder.FindGamePath();
        
        if (!string.IsNullOrEmpty(path))
        {
            GamePath = path;
            if (IsGamePathValid) StatusMessage = LocalizationService.Instance.GetString("Status.GamePathDetected");
            else StatusMessage = LocalizationService.Instance.GetString("Status.GamePathInvalid");
        }
        else
        {
            GamePath = "";
            StatusMessage = LocalizationService.Instance.GetString("Status.GamePathNotFound");
        }

        if (string.IsNullOrEmpty(StatusMessage))
             StatusMessage = LocalizationService.Instance.GetString("Status.Ready");

        // Refresh language names in UI when app language changes
        LocalizationService.Instance.PropertyChanged += (s, e) => {
            if (e.PropertyName == "Item" || e.PropertyName == nameof(LocalizationService.CurrentLanguage))
            {
                OnPropertyChanged(nameof(TargetLanguages));
            }
        };
    }

    [ObservableProperty]
    private double _progress = 0;

    [ObservableProperty]
    private bool _isIndeterminate = false;

    private int _isCheckingResources = 0;
    private string _csvResourcePath = "";

    public void TriggerResourceCheck()
    {
        LoadDefaultCoverage();
        RunResourceCheck(SelectedBranch);
    }

    private void LoadDefaultCoverage()
    {
        if (SelectedCoverageFiles != null) return;

        try
        {
            var cvm = new CoverageViewModel(PathConstants.ResourcesRoot);
            SelectedCoverageFiles = cvm.GetSelectedFiles();
            Log(LocalizationService.Instance.GetString("Log.DefaultCoverageLoaded", SelectedCoverageFiles.Length));
        }
        catch (Exception ex)
        {
            Log(LocalizationService.Instance.GetString("Log.FailedLoadCoverage", ex.Message));
        }
    }

    private void RunResourceCheck(ResourceBranch branch)
    {
        if (Interlocked.Exchange(ref _isCheckingResources, 1) == 1) return;

        Task.Run(async () => {
            try {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    IsDownloadingResources = true;
                    HasFailed = false;
                    Progress = 0;
                    StatusMessage = LocalizationService.Instance.GetString("Status.CheckingUpdates");
                });

                // Determine resource path
                string resourcePath = PathConstants.ResourcesRoot;
                PathConstants.EnsureDirectories();
                
                var s3Manager = new S3ResourceManager();
                s3Manager.OnLog += (msg) => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Log(msg));
                };
                
                s3Manager.OnStatusChanged += (status) => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        StatusMessage = status switch {
                            ResourceStatus.Checking => LocalizationService.Instance.GetString("Status.CheckingUpdates"),
                            ResourceStatus.Downloading => LocalizationService.Instance.GetString("Status.Downloading"),
                            ResourceStatus.Extracting => LocalizationService.Instance.GetString("Status.Extracting"),
                            ResourceStatus.Ready => LocalizationService.Instance.GetString("Status.Ready"),
                            ResourceStatus.Error => LocalizationService.Instance.GetString("Status.UpdateFailed"),
                            _ => StatusMessage
                        };

                        if (status == ResourceStatus.Error)
                        {
                            HasFailed = true;
                            Progress = 100;
                            IsIndeterminate = false;
                        }
                    });
                };

                s3Manager.OnProgressChanged += (percent) => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        if (percent < 0) {
                            IsIndeterminate = true;
                        } else {
                            IsIndeterminate = false;
                            Progress = percent;
                        }
                    });
                };
                
                bool checkSuccess = await s3Manager.CheckAllResourcesAsync(resourcePath, branch, async () => {
                    if (OnRequestUpdateConfirmation != null)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
                            bool result = await OnRequestUpdateConfirmation();
                            tcs.SetResult(result);
                        });
                        return await tcs.Task;
                    }
                    return true;
                });

                if (checkSuccess)
                {
                    _csvResourcePath = resourcePath;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        // Only set to Ready if we didn't end in an Error state from OnStatusChanged
                        if (StatusMessage != LocalizationService.Instance.GetString("Status.UpdateFailed"))
                        {
                            StatusMessage = LocalizationService.Instance.GetString("Status.Ready");
                            HasFailed = false;
                        }
                    });
                }
                else
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => OnRequestClose?.Invoke());
                }
            } catch (Exception ex) {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    Log(LocalizationService.Instance.GetString("Log.ResourceCheckFailed", ex.Message));
                    StatusMessage = LocalizationService.Instance.GetString("Status.UpdateFailed");
                    HasFailed = true;
                    Progress = 100;
                    IsIndeterminate = false;
                });
            } finally {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => IsDownloadingResources = false);
                Interlocked.Exchange(ref _isCheckingResources, 0);
            }
        });
    }

    [RelayCommand]
    private async Task BrowseFolder()
    {
        OnRequestFolderPicker?.Invoke();
    }

    public Action? OnRequestFolderPicker { get; set; }
    public Func<Task<bool>>? OnRequestUpdateConfirmation { get; set; }
    public Func<string, string, Task<bool>>? OnRequestClientVersionMismatch { get; set; }
    public Func<Task<bool?>>? OnRequestRestoreConfirmation { get; set; }
    public Action? OnRequestClose { get; set; }
    public Func<Task<string[]?>>? OnRequestCoverage { get; set; }


    [ObservableProperty]
    private string[]? _selectedCoverageFiles = null;

    [RelayCommand]
    private async Task OpenCoverage()
    {
        if (OnRequestCoverage != null)
        {
            var result = await OnRequestCoverage();
            if (result != null)
            {
                SelectedCoverageFiles = result;
                Log(LocalizationService.Instance.GetString("Log.CoverageUpdated", result.Length));
            }
        }
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Inject()
    {
        if (IsInjecting)
        {
            _injectCts?.Cancel();
            Log(LocalizationService.Instance.GetString("Log.CancellationRequested"));
            return;
        }
        if (IsRestoring || IsDownloadingResources) return;

        string targetPath = GetActualGamePath();
        
        if (!IsGamePathValid)
        {
            StatusMessage = LocalizationService.Instance.GetString("Status.SelectGamePathFirst");
            return;
        }

        if (string.IsNullOrEmpty(_csvResourcePath) || !Directory.Exists(_csvResourcePath))
        {
             _csvResourcePath = PathConstants.ResourcesRoot;
        }

        if (!Directory.Exists(_csvResourcePath))
        {
            StatusMessage = LocalizationService.Instance.GetString("Status.ResourcesNotFound");
            Log(LocalizationService.Instance.GetString("Log.ResourcesNotFound"));
            return;
        }
        
        IsInjecting = true;
        HasFailed = false;
        _injectCts = new CancellationTokenSource();
        string? workspace = null;

        StatusMessage = LocalizationService.Instance.GetString("Status.InjectingPath");
        Log(LocalizationService.Instance.GetString("Log.StartingInjection", targetPath));
        Progress = 0;
        IsIndeterminate = true;
        
        try
        {
            await Task.Run(async () => 
            {
                var cvm = new CoverageViewModel(_csvResourcePath);
                var selectedFiles = cvm.GetSelectedFiles();
                bool applyFonts = selectedFiles.Any(path => path.Replace('\\', '/').StartsWith("font/", StringComparison.OrdinalIgnoreCase));
                bool fontWasApplied = applyFonts;
                if (applyFonts && FontApplicationState.IsAlreadyApplied(targetPath, selectedFiles))
                {
                    selectedFiles = selectedFiles.Where(path => !path.Replace('\\', '/').StartsWith("font/", StringComparison.OrdinalIgnoreCase)).ToArray();
                    applyFonts = false;
                    Log("이미 적용된 폰트는 다시 처리하지 않습니다.");
                }
                bool imageResources = cvm.ImageResourcesSelected;
                var imageManager = new IconResourceManager();
                imageManager.OnLog += Log;
                bool versionMismatchConfirmed = false;
                Func<string, string, Task<bool>> confirmVersionMismatch = async (resourceVersion, gameVersion) =>
                {
                    if (versionMismatchConfirmed) return true;
                    bool result = OnRequestClientVersionMismatch != null && await OnRequestClientVersionMismatch(resourceVersion, gameVersion);
                    versionMismatchConfirmed = result;
                    return result;
                };
                imageManager.OnClientVersionMismatch = confirmVersionMismatch;
                imageManager.OnStatus += message => Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusMessage = message);
                imageManager.OnProgress += percent => Avalonia.Threading.Dispatcher.UIThread.Post(() => { IsIndeterminate = false; Progress = percent; });
                string? imagePackage = null;
                if (imageResources || IconResourceManager.HasInstallation(targetPath)) IconResourceManager.CheckInstallation(targetPath);
                if (imageResources) imagePackage = await imageManager.EnsurePackageAsync(targetPath, SelectedBranch, _injectCts.Token);
                _injectCts.Token.ThrowIfCancellationRequested();

                var originals = new OriginalResourceManager();
                originals.OnLog += Log;
                originals.OnClientVersionMismatch = confirmVersionMismatch;
                originals.OnProgress += percent => Avalonia.Threading.Dispatcher.UIThread.Post(() => { IsIndeterminate = false; Progress = percent; });
                string originalPackage = await originals.EnsurePackageAsync(SelectedBranch, _injectCts.Token);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusMessage = LocalizationService.Instance.GetString("Status.PreparingOriginals"));
                workspace = await originals.PrepareWorkspaceAsync(targetPath, originalPackage, _injectCts.Token, preserveFonts: true);

                // 2. Perform Injection
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    StatusMessage = LocalizationService.Instance.GetString("Status.Injecting");
                    IsIndeterminate = false;
                });

                var patcher = new CsvInjector { AbortOnFailure = true };
                patcher.OnLog += (msg) => {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => Log(msg));
                };

                var progress = new Progress<double>(percent => {
                     Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress = percent);
                });

                Log(LocalizationService.Instance.GetString("Log.InjectingFrom", _csvResourcePath));
                
                var fileFilter = new HashSet<string>(selectedFiles, StringComparer.OrdinalIgnoreCase);
                bool useCompatibilityMode = cvm.IsCompatibilityModeEnabled;

                if (selectedFiles.Length > 0)
                {
                    Log(LocalizationService.Instance.GetString("Log.UsingFilter", fileFilter.Count));
                }
                
                if (useCompatibilityMode)
                {
                     Log(LocalizationService.Instance.GetString("Log.CompatibilityModeEnabled"));
                     Log($"[Compatibility Mode] Enabled: Excluding BNpcName, TextCommand, LogMessage.");
                }
                
                // Note: SelectedLanguage is GameLanguage enum (jp/en/de/fr)
                if (selectedFiles.Length > 0)
                    patcher.Inject(workspace!, _csvResourcePath, SelectedLanguage, fileFilter, null, progress, _injectCts.Token, useCompatibilityMode);
                _injectCts.Token.ThrowIfCancellationRequested();
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusMessage = LocalizationService.Instance.GetString("Status.Committing"));
                originals.Commit(targetPath, workspace!, () =>
                {
                    if (imageResources) imageManager.Install(targetPath, imagePackage!);
                    else imageManager.Uninstall(targetPath);
                }, _injectCts.Token, preserveFonts: !applyFonts);
                if (fontWasApplied && applyFonts) FontApplicationState.MarkApplied(targetPath, cvm.GetSelectedFiles());

                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    Progress = 100;
                    IsIndeterminate = false;
                    StatusMessage = LocalizationService.Instance.GetString("Status.Success");
                });
            }, _injectCts.Token);
        }
        catch (OperationCanceledException)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                StatusMessage = LocalizationService.Instance.GetString("Status.Cancelled");
                Log(LocalizationService.Instance.GetString("Log.InjectionCancelled"));
                IsIndeterminate = true;
            });

            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                StatusMessage = LocalizationService.Instance.GetString("Status.CancelledUnchanged");
                IsIndeterminate = false;
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                Log(LocalizationService.Instance.GetString("Status.Error", ex.Message));
                StatusMessage = LocalizationService.Instance.GetString("Status.InjectionFailed");
                HasFailed = true;
                Progress = 100;
                IsIndeterminate = false;
            });
        }
        finally
        {
            try { if (workspace != null) OriginalResourceManager.CleanupWorkspace(workspace); }
            catch (Exception ex) { Log("임시 작업 폴더 정리 실패: " + ex.Message); }
            IsInjecting = false;
            _injectCts?.Dispose();
            _injectCts = null;
        }
    }

    [RelayCommand]
    private async Task Restore()
    {
        if (IsRestoring || IsInjecting || IsDownloadingResources) return;
        if (!IsGamePathValid)
        {
            StatusMessage = LocalizationService.Instance.GetString("Status.SelectGamePathFirst");
            return;
        }
        IsRestoring = true;
        HasFailed = false;
        IsIndeterminate = true;
        string targetPath = GetActualGamePath();
        string? workspace = null;
        try
        {
            bool? keepFonts = OnRequestRestoreConfirmation == null ? null : await OnRequestRestoreConfirmation();
            if (keepFonts == null)
            {
                StatusMessage = LocalizationService.Instance.GetString("Status.RestoreCancelled");
                return;
            }
            await Task.Run(async () =>
            {
                var images = new IconResourceManager();
                images.OnLog += Log;
                if (IconResourceManager.HasInstallation(targetPath)) IconResourceManager.CheckInstallation(targetPath);
                var originals = new OriginalResourceManager();
                originals.OnLog += Log;
                originals.OnProgress += percent => Avalonia.Threading.Dispatcher.UIThread.Post(() => { IsIndeterminate = false; Progress = percent; });
                originals.OnClientVersionMismatch = async (resourceVersion, gameVersion) =>
                    OnRequestClientVersionMismatch != null && await OnRequestClientVersionMismatch(resourceVersion, gameVersion);
                string package = await originals.EnsurePackageAsync(SelectedBranch);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusMessage = LocalizationService.Instance.GetString("Status.RestoringOriginals"));
                workspace = await originals.PrepareWorkspaceAsync(targetPath, package, preserveFonts: keepFonts.Value);
                originals.Commit(targetPath, workspace, () => images.Uninstall(targetPath), preserveFonts: keepFonts.Value);
                if (!keepFonts.Value) FontApplicationState.Clear(targetPath);
            });
            Progress = 100;
            StatusMessage = keepFonts.Value ? "텍스트 원복 및 이미지 적용 해제 완료 · 폰트 유지" : LocalizationService.Instance.GetString("Status.RestoreAndImagesRemoved");
            Log(StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = LocalizationService.Instance.GetString("Status.RestoreCancelled");
            Log(LocalizationService.Instance.GetString("Status.RestoreCancelled"));
        }
        catch (Exception ex)
        {
            HasFailed = true;
            StatusMessage = LocalizationService.Instance.GetString("Status.RestoreFailed", ex.Message);
            Log(StatusMessage);
        }
        finally
        {
            try { if (workspace != null) OriginalResourceManager.CleanupWorkspace(workspace); }
            catch (Exception ex) { Log("임시 작업 폴더 정리 실패: " + ex.Message); }
            IsIndeterminate = false;
            IsRestoring = false;
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new();
    private bool _isLogUpdating = false;

    private void Log(string message)
    {
        _logQueue.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        
        if (!_isLogUpdating)
        {
            _isLogUpdating = true;
            Task.Delay(100).ContinueWith(_ => FlushLogs());
        }
    }

    private void FlushLogs()
    {
        var logsToAppend = new List<string>();
        while (_logQueue.TryDequeue(out var log))
        {
            logsToAppend.Add(log);
        }

        if (logsToAppend.Count > 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var newText = LogText;
                if (newText.Length > 50000) // Limit log size ~50KB
                {
                    newText = newText.Substring(newText.Length - 30000);
                    int firstNewline = newText.IndexOf('\n');
                    if (firstNewline != -1) newText = newText.Substring(firstNewline + 1);
                }
                
                LogText = newText + string.Join("\n", logsToAppend) + "\n";
            });
        }
        _isLogUpdating = false;
    }
    [RelayCommand]
    private void OpenDiscord()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Secrets.DiscordUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log($"Could not open Discord link: {ex.Message}");
        }
    }
}
