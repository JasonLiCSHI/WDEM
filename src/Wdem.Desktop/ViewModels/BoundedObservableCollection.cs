using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Wdem.Desktop.ViewModels;

public sealed class BoundedObservableCollection<T> :
    IReadOnlyList<T>,
    INotifyCollectionChanged,
    INotifyPropertyChanged
{
  private readonly T[] _items;
  private int _head;

  public BoundedObservableCollection(int capacity)
  {
    ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
    _items = new T[capacity];
  }

  public event NotifyCollectionChangedEventHandler? CollectionChanged;

  public event PropertyChangedEventHandler? PropertyChanged;

  public int Count { get; private set; }

  public T this[int index]
  {
    get
    {
      ArgumentOutOfRangeException.ThrowIfNegative(index);
      if (index >= Count)
      {
        throw new ArgumentOutOfRangeException(nameof(index));
      }

      return _items[(_head + index) % _items.Length];
    }
  }

  public void Add(T item)
  {
    if (Count < _items.Length)
    {
      _items[(_head + Count) % _items.Length] = item;
      Count++;
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
      PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
      CollectionChanged?.Invoke(
          this,
          new NotifyCollectionChangedEventArgs(
              NotifyCollectionChangedAction.Add,
              item,
              Count - 1));
      return;
    }

    _items[_head] = item;
    _head = (_head + 1) % _items.Length;
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    CollectionChanged?.Invoke(
        this,
        new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
  }

  public void Clear()
  {
    if (Count == 0)
    {
      return;
    }

    Array.Clear(_items);
    _head = 0;
    Count = 0;
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    CollectionChanged?.Invoke(
        this,
        new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
  }

  public IEnumerator<T> GetEnumerator()
  {
    for (int index = 0; index < Count; index++)
    {
      yield return this[index];
    }
  }

  IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
