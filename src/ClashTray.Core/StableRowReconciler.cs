using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ClashTray.Core;

public sealed record StableRowReconcileResult(
    int CreatedRows,
    int UpdatedRows,
    int RemovedRows,
    int AddedToView,
    int RemovedFromView,
    int MovedInView,
    bool ViewReset = false);

/// <summary>
/// Keeps row view models keyed by stable domain identity and applies only the
/// visible collection changes needed for the current projection.
/// </summary>
public sealed class StableRowReconciler<TKey, TItem, TRow>
    where TKey : notnull
    where TRow : class
{
    private readonly Func<TItem, TKey> _keySelector;
    private readonly Dictionary<TKey, TRow> _rowsByKey = [];
    private readonly BatchRowCollection _view = [];

    public StableRowReconciler(Func<TItem, TKey> keySelector)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _keySelector = keySelector;
    }

    public ObservableCollection<TRow> Rows => _view;

    public int CachedRowCount => _rowsByKey.Count;
    private int IndexOfReference(TRow target)
    {
        for (int index = 0; index < Rows.Count; index++)
        {
            if (ReferenceEquals(Rows[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    public StableRowReconcileResult Reconcile(
        IReadOnlyList<TItem> source,
        IReadOnlyList<TKey> visibleKeys,
        Func<TItem, TRow> createRow,
        Func<TRow, TItem, bool> updateRow)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(visibleKeys);
        ArgumentNullException.ThrowIfNull(createRow);
        ArgumentNullException.ThrowIfNull(updateRow);

        Dictionary<TKey, TItem> itemsByKey = new();
        foreach (TItem item in source)
        {
            TKey key = _keySelector(item);
            if (key is null || !itemsByKey.TryAdd(key, item))
            {
                throw new InvalidOperationException("The source contains duplicate or null stable row identities.");
            }
        }

        HashSet<TKey> targetKeys = new();
        foreach (TKey key in visibleKeys)
        {
            if (key is null || !targetKeys.Add(key))
            {
                throw new InvalidOperationException("The visible projection contains duplicate or null stable row identities.");
            }
        }

        int createdRows = 0;
        int updatedRows = 0;
        foreach ((TKey key, TItem item) in itemsByKey)
        {
            if (_rowsByKey.TryGetValue(key, out TRow? row))
            {
                if (updateRow(row, item))
                {
                    updatedRows++;
                }
            }
            else
            {
                _rowsByKey.Add(key, createRow(item));
                createdRows++;
            }
        }

        int removedRows = 0;
        foreach (TKey key in _rowsByKey.Keys.Where(key => !itemsByKey.ContainsKey(key)).ToArray())
        {
            _rowsByKey.Remove(key);
            removedRows++;
        }

        List<TRow> targetRows = [];
        foreach (TKey key in visibleKeys)
        {
            if (_rowsByKey.TryGetValue(key, out TRow? row))
            {
                targetRows.Add(row);
            }
        }

        int differences = Math.Abs(Rows.Count - targetRows.Count);
        for (int i = 0; i < Math.Min(Rows.Count, targetRows.Count) && differences <= 64; i++)
        {
            if (!ReferenceEquals(Rows[i], targetRows[i]))
            {
                differences++;
            }
        }

        if (differences == 0)
        {
            return new(createdRows, updatedRows, removedRows, 0, 0, 0);
        }

        HashSet<TRow> targetRowSet = new(targetRows, ReferenceEqualityComparer.Instance);
        HashSet<TRow> previousRows = new(Rows, ReferenceEqualityComparer.Instance);
        int removedCount = Rows.Count(row => !targetRowSet.Contains(row));
        int addedCount = targetRows.Count(row => !previousRows.Contains(row));

        if (differences > 64 && removedCount + addedCount <= 64)
        {
            // An insertion/removal at the head shifts every position, but can
            // still be a small edit when the surviving rows retain their order.
            using IEnumerator<TRow> previousOrder = Rows.Where(targetRowSet.Contains).GetEnumerator();
            bool sameOrder = true;
            foreach (TRow row in targetRows.Where(previousRows.Contains))
            {
                if (!previousOrder.MoveNext() || !ReferenceEquals(row, previousOrder.Current))
                {
                    sameOrder = false;
                    break;
                }
            }

            if (sameOrder) { differences = removedCount + addedCount; }
        }

        // Limit costly UI collection notifications and linear move searches for
        // large projections; smaller edits keep their incremental notifications.
        if (Math.Max(Rows.Count, targetRows.Count) >= 128 && differences > 64)
        {
            _view.ReplaceAll(targetRows);
            return new(createdRows, updatedRows, removedRows, addedCount, removedCount, 0, ViewReset: true);
        }

        int removedFromView = 0;
        for (int index = Rows.Count - 1; index >= 0; index--)
        {
            if (!targetRowSet.Contains(Rows[index]))
            {
                Rows.RemoveAt(index);
                removedFromView++;
            }
        }

        int addedToView = 0;
        int movedInView = 0;
        for (int index = 0; index < targetRows.Count; index++)
        {
            TRow target = targetRows[index];
            if (index < Rows.Count && ReferenceEquals(Rows[index], target))
            {
                continue;
            }

            if (targetRows.Count >= 128 && removedFromView + addedToView + movedInView >= 64)
            {
                _view.ReplaceAll(targetRows);
                return new(createdRows, updatedRows, removedRows, addedCount, removedCount, movedInView, ViewReset: true);
            }

            int existingIndex = IndexOfReference(target);
            if (existingIndex >= 0)
            {
                Rows.Move(existingIndex, index);
                movedInView++;
            }
            else
            {
                Rows.Insert(index, target);
                addedToView++;
            }
        }

        while (Rows.Count > targetRows.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
            removedFromView++;
        }

        return new StableRowReconcileResult(
            createdRows,
            updatedRows,
            removedRows,
            addedToView,
            removedFromView,
            movedInView);
    }

    private sealed class BatchRowCollection : ObservableCollection<TRow>
    {
        public void ReplaceAll(List<TRow> rows)
        {
            CheckReentrancy();
            Items.Clear();
            foreach (TRow row in rows)
            {
                Items.Add(row);
            }

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
