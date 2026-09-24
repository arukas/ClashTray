using System.Collections.ObjectModel;

namespace ClashTray.Core;

public sealed record StableRowReconcileResult(
    int CreatedRows,
    int UpdatedRows,
    int RemovedRows,
    int AddedToView,
    int RemovedFromView,
    int MovedInView);

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

    public StableRowReconciler(Func<TItem, TKey> keySelector)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _keySelector = keySelector;
    }

    public ObservableCollection<TRow> Rows { get; } = [];

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

        HashSet<TRow> targetRowSet = new(targetRows, ReferenceEqualityComparer.Instance);
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
}