using Avalonia.Controls;
using FFXIVInjector.UI.ViewModels;
using System.Threading.Tasks;

namespace FFXIVInjector.UI.Views;

public partial class CoverageWindow : Window
{
    private string[]? _selectedFiles = null;

    public CoverageWindow()
    {
        InitializeComponent();
        
        this.FindControl<Button>("ApplyButton")!.Click += (s, e) =>
        {
            if (DataContext is CoverageViewModel vm)
            {
                _selectedFiles = vm.GetSelectedFiles();
            }
            Close();
        };
        
        this.FindControl<Button>("CancelButton")!.Click += (s, e) =>
        {
            _selectedFiles = null;
            Close();
        };
    }

    public static async Task<string[]?> ShowDialog(Window owner, string resourcePath)
    {
        var window = new CoverageWindow
        {
            DataContext = new CoverageViewModel(resourcePath)
        };
        
        await window.ShowDialog(owner);
        return window._selectedFiles;
    }
}
