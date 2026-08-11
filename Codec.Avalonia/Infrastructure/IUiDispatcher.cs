namespace Codec.Infrastructure;

public enum UiDispatchPriority
{
    Background,
    Normal
}

public interface IUiDispatcher
{
    bool CheckAccess { get; }

    void Post(Action action, UiDispatchPriority priority = UiDispatchPriority.Normal);

    Task InvokeAsync(Action action, UiDispatchPriority priority = UiDispatchPriority.Normal);

    Task<T> InvokeAsync<T>(Func<T> action, UiDispatchPriority priority = UiDispatchPriority.Normal);

    Task InvokeAsync(Func<Task> action, UiDispatchPriority priority = UiDispatchPriority.Normal);
}
