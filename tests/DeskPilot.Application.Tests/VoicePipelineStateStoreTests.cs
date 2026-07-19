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

    private static void Publish(VoicePipelineStateStore store, VoicePipelineSnapshot snapshot) =>
        PublishMethod.Invoke(store, [snapshot]);
}
