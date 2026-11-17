using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AnimaWindows.App.Services;

/// <summary>Frame-based navigation inside the shell.</summary>
public interface INavigationService
{
    bool CanGoBack { get; }

    /// <summary>The page type currently shown, or null before the first navigation.</summary>
    Type? CurrentPage { get; }

    void Navigate(Type page, object? parameter = null, NavigationTransitionInfo? transition = null);

    void GoBack();
}

public sealed class NavigationService : INavigationService
{
    private readonly IDispatcher _dispatcher;
    private Frame? _frame;

    public NavigationService(IDispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Binds the service to the shell's content frame.</summary>
    public void Attach(Frame frame) => _frame = frame;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public Type? CurrentPage => _frame?.CurrentSourcePageType;

    public void Navigate(Type page, object? parameter = null, NavigationTransitionInfo? transition = null)
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(() => Navigate(page, parameter, transition));
            return;
        }

        if (_frame is null)
        {
            return;
        }

        _frame.Navigate(page, parameter, transition ?? new EntranceNavigationTransitionInfo());
    }

    public void GoBack()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            _dispatcher.TryEnqueue(GoBack);
            return;
        }

        if (_frame is { CanGoBack: true })
        {
            _frame.GoBack();
        }
    }
}
