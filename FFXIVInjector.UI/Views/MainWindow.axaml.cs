using Avalonia.Controls;
using FFXIVInjector.UI.ViewModels;
using System.ComponentModel;

namespace FFXIVInjector.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        DataContextChanged += (s, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LogText))
        {
            var textBox = this.FindControl<TextBox>("LogTextBox");
            if (textBox != null)
            {
                // Ensure layout is updated before scrolling
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    textBox.CaretIndex = textBox.Text?.Length ?? 0;
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
        }
    }
}
