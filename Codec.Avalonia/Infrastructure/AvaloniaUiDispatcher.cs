using Avalonia.Threading;

namespace Codec.Infrastructure;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action, UiDispatchPriority priority = UiDispatchPriority.Normal)
        => Dispatcher.UIThread.Post(action, MapPriority(priority));

    public Task InvokeAsync(Action action, UiDispatchPriority priority = UiDispatchPriority.Normal)
    {
        if (CheckAccess)
        {
            action();
            return Task.CompletedTask;
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
            return Task.FromResult(action());

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
            return action();

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
