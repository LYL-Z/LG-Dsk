namespace GlassDesk.Dock.Tests;

public sealed class ObservableRangeCollectionTests
{
    [Fact]
    public void ReplaceAllPublishesOneResetAfterCompleteSnapshotIsInstalled()
    {
        var collection = new ObservableRangeCollection<int> { 1, 2 };
        var notifications = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, e) => notifications.Add(e);

        collection.ReplaceAll([3, 4, 5]);

        Assert.Equal([3, 4, 5], collection);
        var notification = Assert.Single(notifications);
        Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, notification.Action);
    }
}
