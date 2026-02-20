using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace FFXIVInjector.UI.ViewModels;

public partial class FileNode : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _fullPath = "";

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private bool _isChecked = true;

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isExpanded;

    public ObservableCollection<FileNode> Children { get; } = new();

    partial void OnIsCheckedChanged(bool value)
    {
        // Update all children when parent changes
        if (IsDirectory)
        {
            foreach (var child in Children)
            {
                child.IsChecked = value;
            }
        }
    }
}
