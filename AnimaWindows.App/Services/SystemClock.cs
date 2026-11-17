using AnimaWindows.Core.Abstractions;

namespace AnimaWindows.App.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
