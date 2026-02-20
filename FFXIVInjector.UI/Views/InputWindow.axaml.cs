using Avalonia.Controls;
using Avalonia.Interactivity;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FFXIVInjector.UI.Views;

public partial class InputWindow : Window
{
    private readonly char[] _invalidChars;

    public InputWindow()
    {
        InitializeComponent();
        _invalidChars = Path.GetInvalidFileNameChars();

        var okButton = this.FindControl<Button>("OkButton");
        var cancelButton = this.FindControl<Button>("CancelButton");
        var textBox = this.FindControl<TextBox>("InputTextBox");

        if (okButton != null) okButton.Click += OnOkClick;
        if (cancelButton != null) cancelButton.Click += OnCancelClick;
        
        if (textBox != null)
        {
            textBox.TextChanged += (s, e) => ValidateInput();
            
            // Focus text box when window shows
            this.Opened += (s, e) => textBox.Focus();
        }
    }

    private void ValidateInput()
    {
        var textBox = this.FindControl<TextBox>("InputTextBox");
        var okButton = this.FindControl<Button>("OkButton");
        
        if (textBox == null || okButton == null) return;
        
        string text = textBox.Text ?? "";
        bool isValid = !string.IsNullOrWhiteSpace(text) && 
                       text.IndexOfAny(_invalidChars) == -1;
        
        okButton.IsEnabled = isValid;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var textBox = this.FindControl<TextBox>("InputTextBox");
        Close(textBox?.Text);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    public static async Task<string?> ShowInputDialog(Window owner, string title, string message, string defaultValue = "")
    {
        var dialog = new InputWindow();
        dialog.Title = title;
        
        var messageText = dialog.FindControl<TextBlock>("MessageText");
        var textBox = dialog.FindControl<TextBox>("InputTextBox");
        
        if (messageText != null) messageText.Text = message;
        if (textBox != null) 
        {
            textBox.Text = defaultValue;
            textBox.SelectAll();
        }
        
        return await dialog.ShowDialog<string?>(owner);
    }
}
