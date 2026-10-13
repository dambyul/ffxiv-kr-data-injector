using Avalonia.Controls;
using System.Threading.Tasks;

namespace FFXIVInjector.UI.Views;

public partial class RestoreWindow : Window
{
    public RestoreWindow()
    {
        InitializeComponent();
        this.FindControl<Button>("RemoveFontButton")!.Click += (_, _) => Close((bool?)false);
        this.FindControl<Button>("KeepFontButton")!.Click += (_, _) => Close((bool?)true);
    }
    // Closing the window without a button returns null and cancels restoration.
    public static Task<bool?> ShowRestoreDialog(Window owner) => new RestoreWindow().ShowDialog<bool?>(owner);
}
