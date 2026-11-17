using Microsoft.Extensions.DependencyInjection;

namespace AnimaWindows.App.Services;

/// <summary>
/// Static accessor for the DI container. Pages are constructed by <c>Frame</c> (parameterless constructors), so they resolve
/// their view models through <see cref="Get{T}"/>.
/// </summary>
public static class AppServices
{
    private static IServiceProvider? _provider;

    public static IServiceProvider Provider =>
        _provider ?? throw new InvalidOperationException("AppServices.Initialize has not been called yet.");

    public static void Initialize(IServiceProvider provider) => _provider = provider;

    public static T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
}
