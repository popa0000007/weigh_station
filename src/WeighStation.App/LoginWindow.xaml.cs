using System.Windows;
using WeighStation.Presentation.Security;

namespace WeighStation.App;

/// <summary>Shown before the main window. DialogResult is true only after a successful sign-in.</summary>
public partial class LoginWindow : Window
{
    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Succeeded += (_, _) => DialogResult = true;
        Loaded += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            LoginBox.Focus();
        };
    }

    private void OnExit(object sender, RoutedEventArgs e) => DialogResult = false;
}
