using System;
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
    /// Marks any LootableContainer whose inventory contains firearms and/or armor.
    /// Live-clears / refreshes markers when gear is taken out or put in (Add/RemoveItemEvent).
    /// </summary>
    public class ContainerWeaponMarkerProvider : IDynamicMarkerProvider
    {
        /// <summary>
        /// Small map-plane offset (meters) so paired icons do not fully stack.
        /// Keep tiny — a prior 12m offset placed armor away from the real container.
        /// </summary>
        private const float MarkerPairOffset = 1.5f;

        private MapView _lastMapView;
        private readonly Dictionary<LootableContainer, DynamicMaps.UI.Components.MapMarker> _weaponMarkers = [];
        private readonly Dictionary<LootableContainer, DynamicMaps.UI.Components.MapMarker> _armorMarkers = [];
        private readonly Dictionary<IItemOwner, (Action<GEventArgs3> onRemove, Action<GEventArgs2> onAdd)> _ownerHandlers = [];
        private readonly List<Item> _scanBuffer = new(64);

        public void OnShowInRaid(MapView map)
        {
            _lastMapView = map;
            IndexContainers();
        }

        public void OnHideInRaid(MapView map)
        {
            // Keep markers + subscriptions so looting while map is closed still clears icons.
        }

        public void OnRaidEnd(MapView map)
        {
            UnsubscribeAll();
            TryRemoveAllMarkers();
        }

        public void OnMapChanged(MapView map, MapDef mapDef)
        {
            _lastMapView = map;
            UnsubscribeAll();
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

            UnsubscribeAll();
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
                EnsureSubscribed(container);

                _scanBuffer.Clear();
                container.ItemOwner.RootItem.GetAllAssembledItemsNonAlloc(_scanBuffer);

                var n = _scanBuffer.Count;
                itemVisits += n;
                if (n > maxItems) maxItems = n;

                SyncContainerMarkers(container, wantWeapons, wantArmor);
            }

            sw.Stop();
#if DEBUG
            Plugin.Log.LogInfo(
                $"[ContainerGear] containers={containerCount} itemVisits={itemVisits} maxI={maxItems} " +
                $"weapons={_weaponMarkers.Count} armor={_armorMarkers.Count} ms={sw.ElapsedMilliseconds}");
#endif
        }

        private void SyncContainerMarkers(LootableContainer container, bool wantWeapons, bool wantArmor)
        {
            if (container == null || _lastMapView == null) return;

            if (container.ItemOwner?.RootItem == null)
            {
                TryRemoveWeaponMarker(container);
                TryRemoveArmorMarker(container);
                return;
            }

            _scanBuffer.Clear();
            container.ItemOwner.RootItem.GetAllAssembledItemsNonAlloc(_scanBuffer);
            FindFirstWeaponAndArmor(_scanBuffer, out var weapon, out var armor);

            var showWeapon = wantWeapons
                             && weapon != null
                             && Settings.ShowContainerWeaponIntelLevel.Value <= GameUtils.GetIntelLevel();
            var showArmor = wantArmor
                            && armor != null
                            && Settings.ShowContainerArmorIntelLevel.Value <= GameUtils.GetIntelLevel();

            // Always rebuild so pair-offset vs centered stays correct after loot.
            TryRemoveWeaponMarker(container);
            TryRemoveArmorMarker(container);

            if (!showWeapon && !showArmor)
            {
                TryRestoreHiddenStash(container);
                return;
            }

            var transform = container.transform;
            if (transform == null) return;

            var basePos = MathUtils.ConvertToMapPosition(transform);
            var both = showWeapon && showArmor;

            if (showWeapon)
            {
                var pos = both
                    ? basePos + new Vector3(-MarkerPairOffset, 0f, 0f)
                    : basePos;
                var marker = AddContentMarker(
                    weapon,
                    "ContainerWeapon",
                    Settings.ContainerWeaponColor.Value,
                    pos);
                marker.transform.SetAsLastSibling();
                _weaponMarkers[container] = marker;
            }

            if (showArmor)
            {
                var pos = both
                    ? basePos + new Vector3(MarkerPairOffset, 0f, 0f)
                    : basePos;
                var marker = AddContentMarker(
                    armor,
                    "ContainerArmor",
                    Settings.ContainerArmorColor.Value,
                    pos);
                marker.transform.SetAsLastSibling();
                _armorMarkers[container] = marker;
            }

            // Same-container barrel would cover the gun — suppress stash icon while gear is shown.
            TrySuppressHiddenStash(container);
        }

        private static void TrySuppressHiddenStash(LootableContainer container)
        {
            Plugin.Instance?.Map?.TryGetMarkerProvider<HiddenStashMarkerProvider>()
                ?.SuppressForContainer(container);
        }

        private static void TryRestoreHiddenStash(LootableContainer container)
        {
            Plugin.Instance?.Map?.TryGetMarkerProvider<HiddenStashMarkerProvider>()
                ?.TryRestoreForContainer(container);
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

        /// <summary>
        /// Body armor + armored headwear. Soft chest rigs (VestItemClass) and loose plates excluded.
        /// </summary>
        private static bool IsArmor(Item item)
        {
            if (item is ArmorPlateItemClass)
            {
                return false;
            }

            return item is ArmorItemClass || item is HeadwearItemClass;
        }

        private DynamicMaps.UI.Components.MapMarker AddContentMarker(Item item, string category, Color color, Vector3 mapPosition)
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

        private void EnsureSubscribed(LootableContainer container)
        {
            var owner = container.ItemOwner;
            if (owner == null || _ownerHandlers.ContainsKey(owner)) return;

            Action<GEventArgs3> onRemove = args =>
            {
                if (args == null || args.Status != CommandStatus.Succeed) return;
                OnContainerInventoryChanged(container, args.Item);
            };

            Action<GEventArgs2> onAdd = args =>
            {
                if (args == null || args.Status != CommandStatus.Succeed) return;
                OnContainerInventoryChanged(container, args.Item);
            };

            owner.RemoveItemEvent += onRemove;
            owner.AddItemEvent += onAdd;
            _ownerHandlers[owner] = (onRemove, onAdd);
        }

        private void UnsubscribeAll()
        {
            foreach (var pair in _ownerHandlers.ToList())
            {
                var owner = pair.Key;
                if (owner == null) continue;

                owner.RemoveItemEvent -= pair.Value.onRemove;
                owner.AddItemEvent -= pair.Value.onAdd;
            }

            _ownerHandlers.Clear();
        }

        private void OnContainerInventoryChanged(LootableContainer container, Item changedItem)
        {
            if (_lastMapView == null || container == null) return;

            // Ignore ammo / junk noise; always refresh if item unknown (safer for nested moves).
            if (changedItem != null
                && changedItem is not Weapon
                && !IsArmor(changedItem))
            {
                return;
            }

            SyncContainerMarkers(
                container,
                Settings.ShowContainerWeaponsInRaid.Value,
                Settings.ShowContainerArmorInRaid.Value);
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
