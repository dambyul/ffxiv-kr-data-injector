using Avalonia.Controls;
using FFXIVInjector.UI.Services;
using System.Threading.Tasks;

namespace FFXIVInjector.UI.Views;

public partial class ConfirmWindow : Window
{

    public ConfirmWindow()
    {
        InitializeComponent();
        this.FindControl<Button>("YesButton")!.Click += (s, e) => Close(true);
        this.FindControl<Button>("NoButton")!.Click += (s, e) => Close(false);
    }

    public static async Task<bool> ShowConfirmDialog(Window owner, string? title = null, string? message = null, string? yesText = null, string? noText = null)
    {
        var dialog = new ConfirmWindow();
        
        // Use localized defaults if not provided
        title ??= LocalizationService.Instance["ConfirmWindow.Title"];
        message ??= LocalizationService.Instance["ConfirmWindow.Message"];
        yesText ??= LocalizationService.Instance["ConfirmWindow.Button.Yes"];
        noText ??= LocalizationService.Instance["ConfirmWindow.Button.No"];

        dialog.FindControl<TextBlock>("TitleText")!.Text = title;
        dialog.FindControl<TextBlock>("MessageText")!.Text = message;
        dialog.FindControl<Button>("YesButton")!.Content = yesText;
        dialog.FindControl<Button>("NoButton")!.Content = noText;
        return await dialog.ShowDialog<bool>(owner);
    }
}
