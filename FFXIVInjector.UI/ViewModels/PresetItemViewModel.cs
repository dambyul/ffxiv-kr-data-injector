using CommunityToolkit.Mvvm.ComponentModel;
using FFXIVInjector.UI.Models;

namespace FFXIVInjector.UI.ViewModels;

public partial class PresetItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private bool _isChecked = true;

    [ObservableProperty]
    private int _unresolvedRsvCount = 0;

    public bool HasUnresolvedRsv => UnresolvedRsvCount > 0;

    public string? Tooltip => HasUnresolvedRsv 
        ? Services.LocalizationService.Instance["CoverageWindow.UnresolvedRsv.Tooltip"] 
        : null;

    public CoveragePreset Preset { get; set; } = new();
}
