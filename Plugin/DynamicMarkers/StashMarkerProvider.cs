using System.Collections.Generic;
using System.Linq;
using DynamicMaps.Config;
using DynamicMaps.Data;
using DynamicMaps.Patches;
using DynamicMaps.UI.Components;
using DynamicMaps.Utils;
using EFT.Interactive;
using EFT.InventoryLogic;

namespace DynamicMaps.DynamicMarkers;

public class HiddenStashMarkerProvider : IDynamicMarkerProvider
{
    private MapView _lastMapView;
    private readonly Dictionary<LootableContainer, DynamicMaps.UI.Components.MapMarker> _stashMarkers = [];
    private readonly List<Item> _scanBuffer = new(32);
    private const string _hiddenCacheImagePath = "Markers/barrel.png";

    public void OnShowInRaid(MapView map)
    {
        _lastMapView = map;

        foreach (var stash in GameStartedPatch.HiddenStashes)
        {
            TryAddMarker(stash);
        }
    }

    public void OnHideInRaid(MapView map)
    {
        // Do nothing
    }

    public void OnRaidEnd(MapView map)
    {
        TryRemoveMarkers();
    }

    public void OnMapChanged(MapView map, MapDef mapDef)
    {
        _lastMapView = map;

        foreach (var stash in _stashMarkers.Keys.ToList())
        {
            TryRemoveMarker(stash);
            TryAddMarker(stash);
        }
    }

    public void OnDisable(MapView map)
    {
        OnRaidEnd(map);
    }

    public void RefreshMarkers()
    {
        if (!GameUtils.IsInRaid()) return;

        foreach (var stash in GameStartedPatch.HiddenStashes.ToList())
        {
            TryRemoveMarker(stash);
            TryAddMarker(stash);
        }
    }

    /// <summary>
    /// Hide barrel when container gear markers take priority (same transform).
    /// </summary>
    public void SuppressForContainer(LootableContainer container)
    {
        TryRemoveMarker(container);
    }

    /// <summary>
    /// Re-show barrel after gear markers are cleared (if still a tracked hidden stash).
    /// </summary>
    public void TryRestoreForContainer(LootableContainer container)
    {
        if (container == null) return;
        if (!GameStartedPatch.HiddenStashes.Contains(container)) return;
        TryAddMarker(container);
    }

    private void TryAddMarker(LootableContainer stash)
    {
        if (_stashMarkers.ContainsKey(stash)) return;
        if (_lastMapView == null) return;
        if (Settings.ShowHiddenStashIntelLevel.Value > GameUtils.GetIntelLevel()) return;

        // Gear icons take priority over barrel when both would sit on the same container.
        if (ContainerHasPriorityGear(stash))
        {
            return;
        }

        var markerDef = new MapMarkerDef
        {
            Category = "Hidden Stash",
            Color = Settings.HiddenStashColor.Value,
            ImagePath = _hiddenCacheImagePath,
            Position = MathUtils.ConvertToMapPosition(stash.transform),
            Text = "Hidden Stash"
        };

        var marker = _lastMapView.AddMapMarker(markerDef);
        _stashMarkers[stash] = marker;
    }

    private bool ContainerHasPriorityGear(LootableContainer stash)
    {
        var wantWeapons = Settings.ShowContainerWeaponsInRaid.Value;
        var wantArmor = Settings.ShowContainerArmorInRaid.Value;
        if (!wantWeapons && !wantArmor) return false;
        if (stash?.ItemOwner?.RootItem == null) return false;

        _scanBuffer.Clear();
        stash.ItemOwner.RootItem.GetAllAssembledItemsNonAlloc(_scanBuffer);

        for (var i = 0; i < _scanBuffer.Count; i++)
        {
            var item = _scanBuffer[i];
            if (wantWeapons && item is Weapon)
            {
                return true;
            }

            if (wantArmor && IsArmor(item))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsArmor(Item item)
    {
        if (item is ArmorPlateItemClass) return false;
        if (item is ArmorItemClass) return true;
        return item is HeadwearItemClass && item.GetItemComponent<ArmorComponent>() != null;
    }

    private void TryRemoveMarkers()
    {
        foreach (var stash in _stashMarkers.Keys.ToList())
        {
            TryRemoveMarker(stash);
        }
    }

    private void TryRemoveMarker(LootableContainer stash)
    {
        if (!_stashMarkers.TryGetValue(stash, out var marker)) return;

        marker.ContainingMapView.RemoveMapMarker(marker);
        _stashMarkers.Remove(stash);
    }

    public void OnShowOutOfRaid(MapView map)
    {
        // Do nothing
    }

    public void OnHideOutOfRaid(MapView map)
    {
        // Do Nothing
    }
}
