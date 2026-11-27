using System;
using System.Collections.Generic;
using System.Linq;

namespace Restocker.Plan;

[Serializable]
public class StockTrackingConfiguration
{
    public List<LowStockEntry> LowStock { get; set; } = new();
    public StockCategories StockCategories { get; set; } = new();
}

public sealed class StockCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Collapsed { get; set; }
}

public sealed class StockCategories
{
    internal const string Uncategorized = "Uncategorized";
    public List<StockCategory> Ordered { get; set; } = new();
    public bool UncategorizedCollapsed { get; set; }

    internal bool Create(string name)
    {
        if (!ValidName(name)) return false;
        Ordered.Add(new() { Name = name.Trim() });
        return true;
    }

    internal bool Rename(Guid id, string name)
    {
        var category = Ordered.Find(c => c.Id == id);
        if (category == null || !ValidName(name, id)) return false;
        category.Name = name.Trim();
        return true;
    }

    internal void Delete(Guid id, IEnumerable<LowStockEntry> entries)
    {
        if (Ordered.RemoveAll(c => c.Id == id) == 0) return;
        foreach (var e in entries.Where(e => e.CategoryId == id)) e.CategoryId = Guid.Empty;
    }

    private bool ValidName(string name, Guid except = default)
        => !string.IsNullOrWhiteSpace(name) && !name.Contains("##")
        && !string.Equals(name.Trim(), Uncategorized, StringComparison.OrdinalIgnoreCase)
        && !Ordered.Any(c => c.Id != except && string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}
