using System.Windows;
using System.Windows.Controls;

namespace WeighStation.App;

/// <summary>
/// PasswordBox.Password can't be data-bound (by design, to discourage keeping passwords in memory). Login screens still need it,
/// so this attached property mirrors the box into a binding source and back (the view model clears it after use).
/// </summary>
public static class PasswordBoxAssist
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password", typeof(string), typeof(PasswordBoxAssist),
        // The default is null, not "": a binding to an empty string must count as a change so the handler gets attached.
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

    private static readonly DependencyProperty UpdatingProperty =
        DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBoxAssist), new PropertyMetadata(false));

    public static string? GetPassword(DependencyObject d) => (string?)d.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject d, string? value) => d.SetValue(PasswordProperty, value);

    private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
        {
            return;
        }

        box.PasswordChanged -= OnBoxChanged;
        if (!(bool)box.GetValue(UpdatingProperty))
        {
            box.Password = (string?)e.NewValue ?? "";
        }

        box.PasswordChanged += OnBoxChanged;
    }

    private static void OnBoxChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(UpdatingProperty, true);
        SetPassword(box, box.Password);
        box.SetValue(UpdatingProperty, false);
    }
}
