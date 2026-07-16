using System.Collections.Generic;
using System.Linq;
using DynamicMaps.Config;
using DynamicMaps.Data;
using DynamicMaps.Patches;
using DynamicMaps.UI.Components;
using DynamicMaps.Utils;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;

namespace DynamicMaps.DynamicMarkers;

public class HiddenStashMarkerProvider : IDynamicMarkerProvider
{
    private MapView _lastMapView;
    private readonly Dictionary<LootableContainer, DynamicMaps.UI.Components.MapMarker> _stashMarkers = [];
    private readonly HashSet<LootableContainer> _emptiedStashes = [];
    private readonly List<Item> _scanBuffer = new(32);
    private const string _hiddenCacheImagePath = "Markers/barrel.png";
    private static readonly Vector2 StashMarkerSize = new(14f, 14f);

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
        _emptiedStashes.Clear();
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

    /// <summary>Hide barrel when a content pin (gear or gray box) is shown on the same container.</summary>
    public void SuppressForContainer(LootableContainer container)
    {
        TryRemoveMarker(container);
    }

    /// <summary>
    /// Container was emptied / has no content pin — never show barrel again this raid.
    /// </summary>
    public void MarkEmptiedAndClear(LootableContainer container)
    {
        if (container == null) return;
        _emptiedStashes.Add(container);
        TryRemoveMarker(container);
    }

    private void TryAddMarker(LootableContainer stash)
    {
        if (_stashMarkers.ContainsKey(stash)) return;
        if (_lastMapView == null) return;
        if (Settings.ShowHiddenStashIntelLevel.Value > GameUtils.GetIntelLevel()) return;
        if (_emptiedStashes.Contains(stash)) return;

        // Content pin (weapon/armor/vest/bag/misc box) wins — never barrel+box together.
        if (ContainerHasContentPin(stash))
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
        marker.Size = StashMarkerSize;
        marker.transform.SetAsFirstSibling();
        _stashMarkers[stash] = marker;
    }

    /// <summary>
    /// True when ContainerWeaponMarkerProvider would place any pin (including gray misc box).
    /// </summary>
    private bool ContainerHasContentPin(LootableContainer stash)
    {
        if (stash?.ItemOwner?.RootItem == null) return false;

        var wantWeapons = Settings.ShowContainerWeaponsInRaid.Value;
        var wantArmor = Settings.ShowContainerArmorInRaid.Value;
        var wantVests = Settings.ShowContainerVestsInRaid.Value;
        var wantBags = Settings.ShowContainerBagsInRaid.Value;
        var wantStuff = Settings.ShowContainerStuffInRaid.Value;
        if (!wantWeapons && !wantArmor && !wantVests && !wantBags && !wantStuff)
        {
            return false;
        }

        var root = stash.ItemOwner.RootItem;
        _scanBuffer.Clear();
        root.GetAllAssembledItemsNonAlloc(_scanBuffer);

        var hasAnyLoot = false;
        for (var i = 0; i < _scanBuffer.Count; i++)
        {
            var item = _scanBuffer[i];
            if (item == null || ReferenceEquals(item, root)) continue;

            hasAnyLoot = true;

            if (wantWeapons && item is Weapon) return true;
            if (wantArmor && IsArmor(item)) return true;
            if (wantVests && item is VestItemClass) return true;
            if (wantBags && (item is BackpackItemClass)) return true;
        }

        // Misc gray box covers any remaining loot when gear categories are empty.
        return wantStuff && hasAnyLoot;
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
