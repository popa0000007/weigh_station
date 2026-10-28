using CommunityToolkit.Mvvm.ComponentModel;

namespace WeighStation.Presentation.Pages;

/// <summary>A tab of the main window. The shell shows <see cref="Title"/> and <see cref="IconData"/> in the left rail.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>SVG-style path data (24×24 grid, stroked) for the rail icon.</summary>
    public abstract string IconData { get; }

    /// <summary>Called every time the page is shown: reload whatever other pages may have changed.</summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;
}
