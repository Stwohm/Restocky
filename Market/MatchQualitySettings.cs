using System.Collections.Generic;

namespace Restocker.Market;

public sealed class MatchQualitySettings
{
    public bool Default { get; set; } = true;
    public Dictionary<uint, bool> HQ { get; set; } = new();
    public Dictionary<uint, bool> NQ { get; set; } = new();

    public Dictionary<uint, bool> Overrides(bool hq) => hq ? HQ : NQ;

    public bool For(uint itemId, bool hq) => Overrides(hq).GetValueOrDefault(itemId, Default);
}
