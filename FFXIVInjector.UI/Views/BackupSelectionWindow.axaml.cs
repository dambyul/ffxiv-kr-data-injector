using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CommunityToolkit.Mvvm.ComponentModel;
using FFXIVInjector.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace FFXIVInjector.UI.Views;

// ActionType: 0=Restore, 1=Rename, 2=Delete
public record BackupSelectionResult(string BackupId, int ActionType, string? NewName = null);

public partial class BackupItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string SizeDisplay { get; init; } = "";

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public BackupItem(string id, string sizeDisplay)
    {
        Id = id;
        SizeDisplay = sizeDisplay;
    }
}

public partial class BackupSelectionWindow : Window
{
    private ObservableCollection<BackupItem>? _backups;

    public static readonly DirectProperty<BackupSelectionWindow, bool> IsEmptyProperty =
        AvaloniaProperty.RegisterDirect<BackupSelectionWindow, bool>(
            nameof(IsEmpty),
            o => o.IsEmpty);

    private bool _isEmpty = true;
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetAndRaise(IsEmptyProperty, ref _isEmpty, value);
    }

    public BackupSelectionWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public void SetBackups(List<string> backupIds)
    {
        var items = backupIds.Select(id => {
            string fullPath = Path.Combine(PathConstants.BackupPath, id);
            string sizeStr = "0.0 B";
            if (Directory.Exists(fullPath))
            {
                try
                {
                    long totalBytes = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
                                             .Sum(f => new FileInfo(f).Length);
                    sizeStr = FormatFileSize(totalBytes);
                }
                catch
                {
                    sizeStr = Services.LocalizationService.Instance.GetString("BackupWindow.List.Error");
                }
            }
            return new BackupItem(id, sizeStr);
        }).ToList();

        _backups = new ObservableCollection<BackupItem>(items);
        IsEmpty = _backups.Count == 0;

        var listBox = this.FindControl<ListBox>("BackupListBox");
        if (listBox != null)
        {
            listBox.ItemsSource = _backups;
            if (_backups.Count > 0)
            {
                listBox.SelectedIndex = 0;
            }
        }
    }

    private string FormatFileSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:F1} {units[unitIndex]}";
    }

    public Func<List<string>>? ListProvider { get; set; }
    public Action<string>? OnDeleteRequest { get; set; }
    public Action<string, string>? OnRenameRequest { get; set; }

    private void RefreshList()
    {
        if (ListProvider != null)
        {
            var newBackups = ListProvider();
            SetBackups(newBackups);
        }
    }

    private void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        var listBox = this.FindControl<ListBox>("BackupListBox");
        if (listBox?.SelectedItem is BackupItem selected)
        {
            Close(new BackupSelectionResult(selected.Id, 0));
        }
    }

    private void OnRenameClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is BackupItem item)
        {
            PromptRename(item);
        }
    }

    private async void PromptRename(BackupItem item)
    {
        var newName = await InputWindow.ShowInputDialog(this, 
            Services.LocalizationService.Instance.GetString("Input.RenameBackup.Title"), 
            Services.LocalizationService.Instance.GetString("Input.RenameBackup.Message"), 
            item.Id);

        if (!string.IsNullOrWhiteSpace(newName) && newName != item.Id)
        {
             OnRenameRequest?.Invoke(item.Id, newName);
             RefreshList();
             // No Close() here
        }
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is BackupItem item)
        {
            // Proceed with delete without window confirmation
            OnDeleteRequest?.Invoke(item.Id);
            RefreshList();
            // No Close() here
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}
