using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Codec.Helpers;

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        IReadOnlyList<T> replacement = items as IReadOnlyList<T> ?? new List<T>(items);
        if (Items.Count == replacement.Count)
        {
            bool unchanged = true;
            for (int index = 0; index < replacement.Count; index++)
            {
                if (!EqualityComparer<T>.Default.Equals(Items[index], replacement[index]))
                {
                    unchanged = false;
                    break;
                }
            }

            if (unchanged)
                return;
        }

        Items.Clear();
        foreach (T item in replacement)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
