using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AnimaWindows.App.Controls;

/// <summary>Connected animation from a poster card to the details hero.</summary>
public static class PosterTransition
{
    public const string Key = "poster";

    /// <summary>Prepares the animation from the card shown for <paramref name="item"/> inside an ItemsView.</summary>
    public static void PrepareFor(ItemsView view, object item)
    {
        var container = view.FindDescendant<ItemContainer>(c => ReferenceEquals(c.DataContext, item));
        Prepare(container);
    }

    /// <summary>Prepares the animation from the poster card inside <paramref name="container"/> (or the card itself).</summary>
    public static void Prepare(DependencyObject? container)
    {
        if (container is null)
        {
            return;
        }

        var card = container as PosterCard ?? container.FindDescendant<PosterCard>();
        if (card is null)
        {
            return;
        }

        try
        {
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate(Key, card.PosterElement);
        }
        catch (Exception)
        {
            // The animation is cosmetic.
        }
    }

    public static void Start(UIElement target)
    {
        try
        {
            ConnectedAnimationService.GetForCurrentView().GetAnimation(Key)?.TryStart(target);
        }
        catch (Exception)
        {
        }
    }
}
