using CodeCommandCenter.Enums;

namespace CodeCommandCenter.Models;

public class SettingsItem
{
    public required string Label { get; init; }
    public SettingsItemType Type { get; init; }
    public Func<CccConfig, string>? GetValue { get; init; }
    public Action<CccConfig, string>? SetValue { get; init; }
    public string? ActionId { get; init; }
    public string? RemoteHostName { get; init; }
    public int? FavoriteIndex { get; init; }
    public int? ContainerIndex { get; init; }
}
