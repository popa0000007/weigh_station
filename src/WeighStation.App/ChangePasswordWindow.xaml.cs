using System.Windows;
using WeighStation.Presentation.Security;

namespace WeighStation.App;

public partial class ChangePasswordWindow : Window
{
    public ChangePasswordWindow(PasswordChangeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Completed += (_, _) => DialogResult = true;
    }
}
