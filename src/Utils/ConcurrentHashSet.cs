using System.Collections.Concurrent;

namespace EventTickets.Utils; // Заміни на свій namespace, якщо покладеш в іншу папку

/// <summary>
/// Потокобезпечна колекція для зберігання унікальних значень.
/// </summary>
public class ConcurrentHashSet<T> where T : notnull
{
    private readonly ConcurrentDictionary<T, byte> _dictionary = new();

    /// <summary>
    /// Додає елемент. Повертає true, якщо елемент додано (його раніше не було).
    /// </summary>
    public bool Add(T item)
    {
        return _dictionary.TryAdd(item, 0);
    }

    public void Clear()
    {
        _dictionary.Clear();
    }
}