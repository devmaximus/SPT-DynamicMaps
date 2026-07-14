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

namespace DynamicMaps.DynamicMarkers
{
    /// <summary>
    /// Marks LootableContainers whose in-memory inventory contains at least one firearm (Weapon).
    /// Scans on map show — loot is populated before UI can open (see investigate HANDOFF).
    /// </summary>
    public class ContainerWeaponMarkerProvider : IDynamicMarkerProvider
    {
        private MapView _lastMapView;
        private readonly Dictionary<LootableContainer, MapMarker> _markers = [];
        private readonly List<Item> _scanBuffer = new(64);

        public void OnShowInRaid(MapView map)
        {
            _lastMapView = map;
            IndexContainers(map);
        }

        public void OnHideInRaid(MapView map)
        {
            // Keep markers; no event subscriptions in v1
        }

        public void OnRaidEnd(MapView map)
        {
            TryRemoveMarkers();
        }

        public void OnMapChanged(MapView map, MapDef mapDef)
        {
            _lastMapView = map;

            foreach (var container in _markers.Keys.ToList())
            {
                TryRemoveMarker(container);
                TryAddMarker(container);
            }
        }

        public void OnDisable(MapView map)
        {
            OnRaidEnd(map);
        }

        public void RefreshMarkers()
        {
            if (!GameUtils.IsInRaid() || _lastMapView == null) return;

            TryRemoveMarkers();
            IndexContainers(_lastMapView);
        }

        public void OnShowOutOfRaid(MapView map)
        {
        }

        public void OnHideOutOfRaid(MapView map)
        {
        }

        private void IndexContainers(MapView map)
        {
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

                var weapon = FindFirstWeapon(_scanBuffer);
                if (weapon == null) continue;

                TryAddMarker(container, weapon);
            }

            sw.Stop();
#if DEBUG
            Plugin.Log.LogInfo(
                $"[ContainerWeapon] indexed containers={containerCount} itemVisits={itemVisits} maxI={maxItems} markers={_markers.Count} ms={sw.ElapsedMilliseconds}");
#endif
        }

        private static Weapon FindFirstWeapon(List<Item> buffer)
        {
            for (var i = 0; i < buffer.Count; i++)
            {
                if (buffer[i] is Weapon weapon)
                {
                    return weapon;
                }
            }

            return null;
        }

        private void TryAddMarker(LootableContainer container)
        {
            _scanBuffer.Clear();
            var root = container.ItemOwner?.RootItem;
            if (root == null) return;

            root.GetAllAssembledItemsNonAlloc(_scanBuffer);
            var weapon = FindFirstWeapon(_scanBuffer);
            if (weapon == null) return;

            TryAddMarker(container, weapon);
        }

        private void TryAddMarker(LootableContainer container, Weapon weapon)
        {
            if (container == null || _lastMapView == null) return;
            if (_markers.ContainsKey(container)) return;
            if (Settings.ShowContainerWeaponIntelLevel.Value > GameUtils.GetIntelLevel()) return;

            var transform = container.transform;
            if (transform == null) return;

            var itemType = ItemViewFactory.GetItemType(weapon.GetType());
            var itemSprite = EFTHardSettings.Instance.StaticIcons.ItemTypeSprites.GetValueOrDefault(itemType);

            var markerDef = new MapMarkerDef
            {
                Category = "ContainerWeapon",
                Color = Settings.ContainerWeaponColor.Value,
                Sprite = itemSprite,
                Position = MathUtils.ConvertToMapPosition(transform),
                Text = weapon.TemplateId.LocalizedName()
            };

            var marker = _lastMapView.AddMapMarker(markerDef);
            _markers[container] = marker;
        }

        private void TryRemoveMarkers()
        {
            foreach (var container in _markers.Keys.ToList())
            {
                TryRemoveMarker(container);
            }
        }

        private void TryRemoveMarker(LootableContainer container)
        {
            if (!_markers.TryGetValue(container, out var marker)) return;

            marker.ContainingMapView.RemoveMapMarker(marker);
            _markers.Remove(container);
        }
    }
}
