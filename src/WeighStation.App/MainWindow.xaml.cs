using System.Windows;
using System.Windows.Input;
using WeighStation.Presentation.Pages;

namespace WeighStation.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>
    /// F5 captures the weight from any control on the weighing page. Handled here rather than with a KeyBinding
    /// because a KeyBinding only fires while focus is inside the element that owns it.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5
            && DataContext is Presentation.ShellViewModel { SelectedPage: WeighingViewModel weighing }
            && weighing.CaptureCommand.CanExecute(null))
        {
            weighing.CaptureCommand.Execute(null);
            e.Handled = true;
        }
    }
}
