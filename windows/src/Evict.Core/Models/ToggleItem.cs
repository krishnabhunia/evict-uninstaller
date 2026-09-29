namespace Evict.Core.Models;

/// <summary>
/// One switchable thing in a Software Health list: an app's notifications, an app's camera permission, a program's
/// background service… Providers read and flip them; the UI is shared.
/// </summary>
public sealed class ToggleItem
{
    /// <summary>Provider-specific identity (AUMID, registry path, service name…).</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Section in the list ("Apps", "Windows tips", "Camera"…).</summary>
    public required string Group { get; init; }
    public string? Detail { get; init; }
    public bool IsOn { get; set; }
    /// <summary>Evict suggests switching this off (promotional notifications, updater services…).</summary>
    public bool RecommendOff { get; init; }
    /// <summary>Switch is read-only (essential Windows component, or administrator rights needed and missing).</summary>
    public bool Locked { get; init; }
    public string? LockedReason { get; init; }
    /// <summary>Free-form data the provider needs to flip the switch (registry location, original start type…).</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

/// <summary>A source of switchable items for the shared toggle-list window.</summary>
public interface IToggleProvider
{
    string Title { get; }
    string Subtitle { get; }
    /// <summary>What "on" means in the list's first column ("Allowed", "Awake"…).</summary>
    string OnLabel { get; }
    List<ToggleItem> Load();
    /// <summary>Switches the item; returns an error message on failure.</summary>
    string? Set(ToggleItem item, bool on);
}
