using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System;
using System.IO;
using Avalonia.Markup.Xaml;
using FFXIVInjector.UI.Views;
using FFXIVInjector.UI.ViewModels;
using FFXIVInjector.Core;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security.Principal;

namespace FFXIVInjector.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services.ConfigurationService.Instance.LoadConfiguration();
            var vm = new MainViewModel();
            vm.OnRequestFolderPicker = async () =>
            {
                var storage = desktop.MainWindow?.StorageProvider;
                if (storage == null) return;

                var result = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = Services.LocalizationService.Instance.GetString("App.FolderPicker.Title"),
                    AllowMultiple = false
                });

                if (result.Count > 0)
                {
                    string selectedPath = result[0].Path.LocalPath;
                    
                    vm.GamePath = selectedPath;
                    
                    if (vm.IsGamePathValid)
                    {
                        // Set success message only if validation passed
                        vm.StatusMessage = Services.LocalizationService.Instance.GetString("Status.GamePathUpdated");
                    }
                    else
                    {
                        // UI Logic: Clear path to disable UI (error message set in ViewModel).
                        vm.GamePath = ""; 
                    }
                }
            };

            vm.OnRequestUpdateConfirmation = async () =>
            {
                if (desktop.MainWindow == null) return false;
                return await ConfirmWindow.ShowConfirmDialog(desktop.MainWindow);
            };
            vm.OnRequestClientVersionMismatch = async (resourceVersion, gameVersion) =>
            {
                if (desktop.MainWindow == null) return false;
                return await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ConfirmWindow.ShowConfirmDialog(
                    desktop.MainWindow,
                    "리소스 버전 확인",
                    "현재 게임 버전과 리소스 버전이 불일치합니다.\n마이너 패치가 아닌 경우 적용 후 문제가 발생할 수 있습니다.",
                    "확인",
                    "취소"));
            };
            vm.OnRequestRestoreConfirmation = async () =>
            {
                if (desktop.MainWindow == null) return null;
                return await RestoreWindow.ShowRestoreDialog(desktop.MainWindow);
            };

            vm.OnRequestCoverage = async () =>
            {
                string resourcePath = PathConstants.ResourcesRoot;
                
                if (desktop.MainWindow == null) return null;
                return await Views.CoverageWindow.ShowDialog(desktop.MainWindow, resourcePath);
            };

            vm.OnRequestClose = () =>
            {
                desktop.MainWindow?.Close();
            };

            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };

            desktop.MainWindow.Opened += async (_, _) =>
            {
                if (OperatingSystem.IsWindows())
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    {
                        var localization = Services.LocalizationService.Instance;
                        await ConfirmWindow.ShowNoticeDialog(
                            desktop.MainWindow,
                            localization.GetString("Warning.Administrator.Title"),
                            localization.GetString("Warning.Administrator.Message"),
                            localization.GetString("Button.Ok"));
                    }
                }
                vm.TriggerResourceCheck();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
