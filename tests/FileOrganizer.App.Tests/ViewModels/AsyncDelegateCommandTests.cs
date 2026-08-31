using System.Windows.Input;
using FileOrganizer.App.ViewModels;

namespace FileOrganizer.App.Tests.ViewModels;

public sealed class AsyncDelegateCommandTests
{
    [Fact]
    public async Task OverlappingExecutionsInvokeTheDelegateOnlyOnce()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TaskCompletionSource entered = CompletionSource();
        TaskCompletionSource release = CompletionSource();
        int invocationCount = 0;
        AsyncDelegateCommand command = new(
            async token =>
            {
                Interlocked.Increment(ref invocationCount);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            },
            static _ => throw new InvalidOperationException("No failure was expected."));

        Task first = command.ExecuteAsync(cancellationToken);
        await entered.Task.WaitAsync(cancellationToken);
        Task overlapping = command.ExecuteAsync(cancellationToken);

        await overlapping;
        Assert.Equal(1, Volatile.Read(ref invocationCount));
        Assert.True(command.IsRunning);

        release.TrySetResult();
        await first;

        Assert.Equal(1, Volatile.Read(ref invocationCount));
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task RunningStateRaisesPropertyAndCanExecuteNotifications()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TaskCompletionSource entered = CompletionSource();
        TaskCompletionSource release = CompletionSource();
        List<(bool IsRunning, bool CanExecute)> canExecuteStates = [];
        List<string?> propertyChanges = [];
        AsyncDelegateCommand? command = null;
        command = new AsyncDelegateCommand(
            async token =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            },
            static _ => throw new InvalidOperationException("No failure was expected."));
        command.CanExecuteChanged += (_, _) =>
            canExecuteStates.Add((command.IsRunning, command.CanExecute(null)));
        command.PropertyChanged += (_, eventArgs) => propertyChanges.Add(eventArgs.PropertyName);

        Task execution = command.ExecuteAsync(cancellationToken);
        await entered.Task.WaitAsync(cancellationToken);

        Assert.True(command.IsRunning);
        Assert.False(command.CanExecute(null));

        release.TrySetResult();
        await execution;

        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
        Assert.Equal(
            [(true, false), (false, true)],
            canExecuteStates);
        Assert.Equal(
            [nameof(AsyncDelegateCommand.IsRunning), nameof(AsyncDelegateCommand.IsRunning)],
            propertyChanges);
    }

    [Fact]
    public async Task ExternalCanExecuteStateCanBeRequeriedWithoutInvokingTheDelegate()
    {
        bool allowed = false;
        int invocationCount = 0;
        int notificationCount = 0;
        AsyncDelegateCommand command = new(
            _ =>
            {
                invocationCount++;
                return Task.CompletedTask;
            },
            static _ => throw new InvalidOperationException("No failure was expected."),
            () => allowed);
        command.CanExecuteChanged += (_, _) => notificationCount++;

        await command.ExecuteAsync(TestContext.Current.CancellationToken);
        allowed = true;
        command.RaiseCanExecuteChanged();

        Assert.Equal(0, invocationCount);
        Assert.True(command.CanExecute(null));
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public async Task AwaitedExecutionPropagatesFailureWithoutUsingTheVoidFailureHandler()
    {
        int reportedFailureCount = 0;
        var expected = new InvalidOperationException("Injected command failure.");
        AsyncDelegateCommand command = new(
            _ => Task.FromException(expected),
            _ => reportedFailureCount++);

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => command.ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
        Assert.Equal(0, reportedFailureCount);
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task ICommandExecuteRoutesAsyncFailureToTheInjectedHandler()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TaskCompletionSource<Exception> reported = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new IOException("Injected asynchronous failure.");
        AsyncDelegateCommand command = new(
            async _ =>
            {
                await Task.Yield();
                throw expected;
            },
            exception => reported.TrySetResult(exception));

        ((ICommand)command).Execute(null);
        Exception actual = await reported.Task.WaitAsync(cancellationToken);

        Assert.Same(expected, actual);
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }

    private static TaskCompletionSource CompletionSource() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}
