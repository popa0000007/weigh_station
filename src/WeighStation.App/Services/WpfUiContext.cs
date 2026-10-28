using System.Windows;
using System.Windows.Threading;
using WeighStation.Presentation.Services;

namespace WeighStation.App.Services;

/// <summary>Runs work on the UI thread. Device events come from background threads; WPF only lets the UI thread touch bound objects.</summary>
public sealed class WpfUiContext(Dispatcher dispatcher) : IUiContext
{
    public void Post(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            // BeginInvoke (not Invoke): the device thread must never wait for the UI.
            dispatcher.BeginInvoke(action);
        }
    }
}
