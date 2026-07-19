using System.Reflection;
using DeskPilot.Application.Voice;
using DeskPilot.Core.Voice;
using FluentAssertions;
using Xunit;

namespace DeskPilot.Application.Tests;

public sealed class VoicePipelineStateStoreTests
{
    private static readonly MethodInfo PublishMethod = typeof(VoicePipelineStateStore)
        .GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("VoicePipelineStateStore.Publish was not found.");
    private static readonly ConstructorInfo InterleavingConstructor =
        typeof(VoicePipelineStateStore).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(Action)],
            modifiers: null)
        ?? throw new InvalidOperationException(
            "VoicePipelineStateStore interleaving constructor was not found.");

    [Fact]
    public void Publish_ReentrantNotificationPreservesSubscriberOrder()
    {
        var store = new VoicePipelineStateStore();
        var first = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.WaitingForWakeWord };
        var second = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.WakeWordDetected };
        var notifications = new List<string>();
        store.SnapshotChanged += (_, snapshot) =>
        {
            notifications.Add($"A:{snapshot.State}");
            if (ReferenceEquals(snapshot, first))
            {
                Publish(store, second);
            }
        };
        store.SnapshotChanged += (_, snapshot) => notifications.Add($"B:{snapshot.State}");

        Publish(store, first);

        notifications.Should().Equal(
            "A:WaitingForWakeWord",
            "B:WaitingForWakeWord",
            "A:WakeWordDetected",
            "B:WakeWordDetected");
        store.Snapshot.Should().BeSameAs(second);
    }

    [Fact]
    public void Publish_SubscriberFailureIsIsolatedFromOtherSubscribers()
    {
        var store = new VoicePipelineStateStore();
        var observed = new List<VoiceAssistantState>();
        store.SnapshotChanged += (_, _) => throw new InvalidOperationException("subscriber failure");
        store.SnapshotChanged += (_, snapshot) => observed.Add(snapshot.State);
        var snapshot = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.ListeningForCommand };

        var action = () => Publish(store, snapshot);

        action.Should().NotThrow();
        observed.Should().Equal(VoiceAssistantState.ListeningForCommand);
        store.Snapshot.Should().BeSameAs(snapshot);
    }

    [Fact(Timeout = 5_000)]
    public async Task Publish_DoesNotInvokeExternalSubscriberWhileHoldingSerializationMonitor()
    {
        var store = new VoicePipelineStateStore();
        var first = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.WaitingForWakeWord };
        var second = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.WakeWordDetected };
        Task? nestedPublish = null;
        var nestedReturnedWhileSubscriberWasActive = false;
        store.SnapshotChanged += (_, snapshot) =>
        {
            if (!ReferenceEquals(snapshot, first))
            {
                return;
            }

            nestedPublish = Task.Run(() => Publish(store, second));
            nestedReturnedWhileSubscriberWasActive = nestedPublish.Wait(TimeSpan.FromMilliseconds(250));
        };

        Publish(store, first);
        await (nestedPublish ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2));

        nestedReturnedWhileSubscriberWasActive.Should().BeTrue();
        store.Snapshot.Should().BeSameAs(second);
    }

    [Fact]
    public void Publish_FatalSubscriberFailurePropagatesAndNextPublishDrainsQueuedSnapshots()
    {
        var store = new VoicePipelineStateStore();
        var first = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.WaitingForWakeWord };
        var queued = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.WakeWordDetected };
        var final = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.ListeningForCommand };
        var observed = new List<VoiceAssistantState>();
        var fatal = new AggregateException(
            "private subscriber detail",
            new InvalidOperationException("ordinary inner failure"),
            new OutOfMemoryException("private fatal detail"));
        EventHandler<VoicePipelineSnapshot>? fatalSubscriber = null;
        fatalSubscriber = (_, snapshot) =>
        {
            if (ReferenceEquals(snapshot, first))
            {
                Publish(store, queued);
                throw fatal;
            }
        };
        store.SnapshotChanged += fatalSubscriber;
        store.SnapshotChanged += (_, snapshot) => observed.Add(snapshot.State);

        var failure = Record.Exception(() => PublishUnwrapped(store, first));

        failure.Should().BeSameAs(fatal);
        store.SnapshotChanged -= fatalSubscriber;
        Publish(store, final);
        observed.Should().Equal(
            VoiceAssistantState.WakeWordDetected,
            VoiceAssistantState.ListeningForCommand);
        store.Snapshot.Should().BeSameAs(final);
    }

    [Fact(Timeout = 5_000)]
    public async Task Publish_NormalOwnershipReleaseCannotResetNextDispatcherOwnership()
    {
        var first = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.WaitingForWakeWord };
        var second = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.WakeWordDetected };
        var third = VoicePipelineSnapshot.Disabled with { State = VoiceAssistantState.ListeningForCommand };
        var firstReleaseEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowFirstDispatcherReturn = new ManualResetEventSlim();
        using var allowSecondSubscriberReturn = new ManualResetEventSlim();
        var normalReleaseCount = 0;
        var store = CreateStoreWithInterleaving(() =>
        {
            if (Interlocked.Increment(ref normalReleaseCount) == 1)
            {
                firstReleaseEntered.TrySetResult();
                if (!allowFirstDispatcherReturn.Wait(TimeSpan.FromSeconds(2)))
                {
                    throw new TimeoutException("The first dispatcher was not released by the test.");
                }
            }
        });
        var secondSubscriberEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new System.Collections.Concurrent.ConcurrentQueue<VoiceAssistantState>();
        var activeSubscribers = 0;
        var maxActiveSubscribers = 0;
        store.SnapshotChanged += (_, snapshot) =>
        {
            var active = Interlocked.Increment(ref activeSubscribers);
            UpdateMaximum(ref maxActiveSubscribers, active);
            observed.Enqueue(snapshot.State);
            try
            {
                if (ReferenceEquals(snapshot, second))
                {
                    secondSubscriberEntered.TrySetResult();
                    if (!allowSecondSubscriberReturn.Wait(TimeSpan.FromSeconds(2)))
                    {
                        throw new TimeoutException("The second subscriber was not released by the test.");
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref activeSubscribers);
            }
        };

        var firstPublish = Task.Run(() => Publish(store, first));
        Task? secondPublish = null;
        try
        {
            await firstReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            secondPublish = Task.Run(() => Publish(store, second));
            await secondSubscriberEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            allowFirstDispatcherReturn.Set();
            await firstPublish.WaitAsync(TimeSpan.FromSeconds(2));

            await Task.Run(() => Publish(store, third)).WaitAsync(TimeSpan.FromSeconds(2));
            allowSecondSubscriberReturn.Set();
            await secondPublish.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            allowFirstDispatcherReturn.Set();
            allowSecondSubscriberReturn.Set();
            await firstPublish.WaitAsync(TimeSpan.FromSeconds(2));
            if (secondPublish is not null)
            {
                await secondPublish.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        maxActiveSubscribers.Should().Be(1);
        observed.Should().Equal(
            VoiceAssistantState.WaitingForWakeWord,
            VoiceAssistantState.WakeWordDetected,
            VoiceAssistantState.ListeningForCommand);
        store.Snapshot.Should().BeSameAs(third);
    }

    private static void Publish(VoicePipelineStateStore store, VoicePipelineSnapshot snapshot) =>
        PublishMethod.Invoke(store, [snapshot]);

    private static void PublishUnwrapped(
        VoicePipelineStateStore store,
        VoicePipelineSnapshot snapshot)
    {
        try
        {
            Publish(store, snapshot);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(exception.InnerException)
                .Throw();
        }
    }

    private static VoicePipelineStateStore CreateStoreWithInterleaving(Action callback) =>
        (VoicePipelineStateStore)InterleavingConstructor.Invoke([callback]);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
