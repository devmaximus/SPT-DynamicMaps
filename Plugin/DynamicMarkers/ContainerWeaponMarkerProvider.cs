using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Comfort.Common;
using DynamicMaps.Config;
using DynamicMaps.Data;
using DynamicMaps.UI.Components;
using DynamicMaps.Utils;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using UnityEngine;

namespace DynamicMaps.DynamicMarkers
{
    /// <summary>
    /// Marks any LootableContainer (crates, stashes, etc.) whose inventory contains firearms and/or armor.
    /// Single NonAlloc scan per container; weapon and armor markers are offset when both are shown.
    /// </summary>
    public class ContainerWeaponMarkerProvider : IDynamicMarkerProvider
    {
        /// <summary>Map-plane offset (world X) so paired markers do not stack.</summary>
        private const float MarkerPairOffset = 12f;

        private MapView _lastMapView;
        private readonly Dictionary<LootableContainer, DynamicMaps.UI.Components.MapMarker> _weaponMarkers = [];
        private readonly Dictionary<LootableContainer, DynamicMaps.UI.Components.MapMarker> _armorMarkers = [];
        private readonly List<Item> _scanBuffer = new(64);

        public void OnShowInRaid(MapView map)
        {
            _lastMapView = map;
            IndexContainers();
        }

        public void OnHideInRaid(MapView map)
        {
        }

        public void OnRaidEnd(MapView map)
        {
            TryRemoveAllMarkers();
        }

        public void OnMapChanged(MapView map, MapDef mapDef)
        {
            _lastMapView = map;
            TryRemoveAllMarkers();
            IndexContainers();
        }

        public void OnDisable(MapView map)
        {
            OnRaidEnd(map);
        }

        public void RefreshMarkers()
        {
            if (!GameUtils.IsInRaid() || _lastMapView == null) return;

            TryRemoveAllMarkers();
            IndexContainers();
        }

        public void OnShowOutOfRaid(MapView map)
        {
        }

        public void OnHideOutOfRaid(MapView map)
        {
        }

        private void IndexContainers()
        {
            var wantWeapons = Settings.ShowContainerWeaponsInRaid.Value;
            var wantArmor = Settings.ShowContainerArmorInRaid.Value;
            if (!wantWeapons && !wantArmor) return;

            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null) return;

            var sw = Stopwatch.StartNew();
            var containerCount = 0;
            var itemVisits = 0;
            var maxItems = 0;

            foreach (var killable in gameWorld.LootList)
            {
                if (killable is not LootableContainer container) continue;
                if (!container.IsInitialized || container.ItemOwner?.RootItem == null) continue;

                containerCount++;
                _scanBuffer.Clear();
                container.ItemOwner.RootItem.GetAllAssembledItemsNonAlloc(_scanBuffer);

                var n = _scanBuffer.Count;
                itemVisits += n;
                if (n > maxItems) maxItems = n;

                FindFirstWeaponAndArmor(_scanBuffer, out var weapon, out var armor);

                var addWeapon = wantWeapons && weapon != null;
                var addArmor = wantArmor && armor != null;
                if (!addWeapon && !addArmor) continue;

                TryAddMarkers(container, addWeapon ? weapon : null, addArmor ? armor : null);
            }

            sw.Stop();
#if DEBUG
            Plugin.Log.LogInfo(
                $"[ContainerGear] containers={containerCount} itemVisits={itemVisits} maxI={maxItems} " +
                $"weapons={_weaponMarkers.Count} armor={_armorMarkers.Count} ms={sw.ElapsedMilliseconds}");
#endif
        }

        private static void FindFirstWeaponAndArmor(List<Item> buffer, out Weapon weapon, out Item armor)
        {
            weapon = null;
            armor = null;

            for (var i = 0; i < buffer.Count; i++)
            {
                var item = buffer[i];
                if (weapon == null && item is Weapon w)
                {
                    weapon = w;
                }

                if (armor == null && IsArmor(item))
                {
                    armor = item;
                }

                if (weapon != null && armor != null)
                {
                    return;
                }
            }
        }

        private static bool IsArmor(Item item)
        {
            return item is ArmorItemClass
                || item is VestItemClass
                || item is HeadwearItemClass;
        }

        private void TryAddMarkers(LootableContainer container, Weapon weapon, Item armor)
        {
            if (container == null || _lastMapView == null) return;

            var transform = container.transform;
            if (transform == null) return;

            var basePos = MathUtils.ConvertToMapPosition(transform);
            var both = weapon != null && armor != null;

            if (weapon != null
                && !_weaponMarkers.ContainsKey(container)
                && Settings.ShowContainerWeaponIntelLevel.Value <= GameUtils.GetIntelLevel())
            {
                var pos = both
                    ? basePos + new Vector3(-MarkerPairOffset, 0f, 0f)
                    : basePos;
                _weaponMarkers[container] = AddContentMarker(
                    container,
                    weapon,
                    "ContainerWeapon",
                    Settings.ContainerWeaponColor.Value,
                    pos);
            }

            if (armor != null
                && !_armorMarkers.ContainsKey(container)
                && Settings.ShowContainerArmorIntelLevel.Value <= GameUtils.GetIntelLevel())
            {
                var pos = both
                    ? basePos + new Vector3(MarkerPairOffset, 0f, 0f)
                    : basePos;
                _armorMarkers[container] = AddContentMarker(
                    container,
                    armor,
                    "ContainerArmor",
                    Settings.ContainerArmorColor.Value,
                    pos);
            }
        }

        private DynamicMaps.UI.Components.MapMarker AddContentMarker(
            LootableContainer container,
            Item item,
            string category,
            Color color,
            Vector3 mapPosition)
        {
            var itemType = ItemViewFactory.GetItemType(item.GetType());
            var itemSprite = EFTHardSettings.Instance.StaticIcons.ItemTypeSprites.GetValueOrDefault(itemType);

            var markerDef = new MapMarkerDef
            {
                Category = category,
                Color = color,
                Sprite = itemSprite,
                Position = mapPosition,
                Text = item.TemplateId.LocalizedName()
            };

            return _lastMapView.AddMapMarker(markerDef);
        }

        private void TryRemoveAllMarkers()
        {
            foreach (var container in _weaponMarkers.Keys.ToList())
            {
                TryRemoveWeaponMarker(container);
            }

            foreach (var container in _armorMarkers.Keys.ToList())
            {
                TryRemoveArmorMarker(container);
            }
        }

        private void TryRemoveWeaponMarker(LootableContainer container)
        {
            if (!_weaponMarkers.TryGetValue(container, out var marker)) return;

            marker.ContainingMapView.RemoveMapMarker(marker);
            _weaponMarkers.Remove(container);
        }

        private void TryRemoveArmorMarker(LootableContainer container)
        {
            if (!_armorMarkers.TryGetValue(container, out var marker)) return;

            marker.ContainingMapView.RemoveMapMarker(marker);
            _armorMarkers.Remove(container);
        }
    }
}
