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

            vm.OnRequestBackupDeletionWarning = async () =>
            {
                if (desktop.MainWindow == null) return false;
                return await ConfirmWindow.ShowConfirmDialog(
                    desktop.MainWindow,
                    title: Services.LocalizationService.Instance.GetString("Warning.LastBackup.Title"),
                    message: Services.LocalizationService.Instance.GetString("Warning.LastBackup.Message"),
                    yesText: Services.LocalizationService.Instance.GetString("Button.Ok"),
                    noText: Services.LocalizationService.Instance.GetString("BackupWindow.Button.Cancel")
                );
            };
            vm.OnRequestBackupSelection = async (backups, deleteHandler, renameHandler, listProvider) =>
            {
                var dialog = new BackupSelectionWindow();
                dialog.SetBackups(backups);
                dialog.ListProvider = listProvider;
                dialog.OnDeleteRequest = deleteHandler;
                dialog.OnRenameRequest = renameHandler;
                
                if (desktop.MainWindow == null) 
                {
                    return null;
                }
                
                var result = await dialog.ShowDialog<BackupSelectionResult?>(desktop.MainWindow);
                // Handle result: Return ID only if Restore action (Type 0) confirmed.
                
                if (result != null && result.ActionType == 0)
                {
                    return result.BackupId;
                }
                
                return null;
            };

            vm.OnRequestUpdateConfirmation = async () =>
            {
                if (desktop.MainWindow == null) return false;
                return await ConfirmWindow.ShowConfirmDialog(desktop.MainWindow);
            };

            vm.OnRequestBackupRetention = async () =>
            {
                if (desktop.MainWindow == null) return true;
                return await ConfirmWindow.ShowConfirmDialog(
                    desktop.MainWindow,
                    title: Services.LocalizationService.Instance.GetString("BackupRetention.Title"),
                    message: Services.LocalizationService.Instance.GetString("BackupRetention.Message"),
                    yesText: Services.LocalizationService.Instance.GetString("BackupRetention.Button.Yes"),
                    noText: Services.LocalizationService.Instance.GetString("BackupRetention.Button.No")
                );
            };

            vm.OnRequestCoverage = async () =>
            {
                string resourcePath = PathConstants.ResourcesRoot;
                
                if (desktop.MainWindow == null) return null;
                return await Views.CoverageWindow.ShowDialog(desktop.MainWindow, resourcePath);
            };

            vm.OnRequestInput = async (title, message, defaultValue) =>
            {
                if (desktop.MainWindow == null) return null;
                return await InputWindow.ShowInputDialog(desktop.MainWindow, title, message, defaultValue);
            };

            vm.OnRequestClose = () =>
            {
                desktop.MainWindow?.Close();
            };

            desktop.MainWindow = new MainWindow
            {
                DataContext = vm
            };

            // Trigger Initial S3 Resource Check after window is ready
            vm.TriggerResourceCheck();
        }

        base.OnFrameworkInitializationCompleted();
    }
}