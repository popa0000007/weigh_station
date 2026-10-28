using CommunityToolkit.Mvvm.ComponentModel;
using WeighStation.Core.Security;

namespace WeighStation.Presentation.Pages;

/// <summary>A tab of the main window. The shell shows <see cref="Title"/> and <see cref="IconData"/> in the left rail.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>SVG-style path data (24×24 grid, stroked) for the rail icon.</summary>
    public abstract string IconData { get; }

    /// <summary>The page is only listed in the navigation for users who have this permission.</summary>
    public abstract Permission Required { get; }

    /// <summary>Called every time the page is shown: reload whatever other pages may have changed.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;
}
