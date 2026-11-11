using Avalonia.Threading;

namespace Codec.Infrastructure;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action, UiDispatchPriority priority = UiDispatchPriority.Normal)
        => Dispatcher.UIThread.Post(action, MapPriority(priority));

    public Task InvokeAsync(Action action, UiDispatchPriority priority = UiDispatchPriority.Normal)
    {
        // Inline calls fail the same way posted ones do: through the task, not by throwing.
        if (CheckAccess)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, priority);
        return completion.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> action, UiDispatchPriority priority = UiDispatchPriority.Normal)
    {
        if (CheckAccess)
        {
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, priority);
        return completion.Task;
    }

    public Task InvokeAsync(Func<Task> action, UiDispatchPriority priority = UiDispatchPriority.Normal)
    {
        if (CheckAccess)
        {
            try
            {
                return action();
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, priority);
        return completion.Task;
    }

    private static DispatcherPriority MapPriority(UiDispatchPriority priority)
        => priority == UiDispatchPriority.Background
            ? DispatcherPriority.Background
            : DispatcherPriority.Normal;
}
