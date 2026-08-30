using System.Collections.Specialized;
using Wdem.Desktop.ViewModels;
using Xunit;

namespace Wdem.Desktop.Tests.ViewModels;

public sealed class BoundedObservableCollectionTests
{
  [Fact]
  public void AddBeyondCapacityKeepsLatestItemsWithoutHeadRemovalNotifications()
  {
    var collection = new BoundedObservableCollection<int>(3);
    var actions = new List<NotifyCollectionChangedAction>();
    collection.CollectionChanged += (_, args) => actions.Add(args.Action);

    collection.Add(1);
    collection.Add(2);
    collection.Add(3);
    collection.Add(4);
    collection.Add(5);

    Assert.Equal([3, 4, 5], collection);
    Assert.Equal(
        [
          NotifyCollectionChangedAction.Add,
          NotifyCollectionChangedAction.Add,
          NotifyCollectionChangedAction.Add,
          NotifyCollectionChangedAction.Reset,
          NotifyCollectionChangedAction.Reset
        ],
        actions);
    Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, actions);
  }
}
