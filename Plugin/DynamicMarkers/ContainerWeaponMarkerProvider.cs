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
    /// Marks lootable containers by content type (long gun / pistol / body armor / helmet / vest / bag / misc).
    /// Markers sit on the container transform; multi-type uses a tight map-plane fan (~0.35m).
    /// </summary>
    public class ContainerWeaponMarkerProvider : IDynamicMarkerProvider
    {
        /// <summary>Map-plane meters between sibling icons — keep tiny so the pin stays on the crate.</summary>
        private const float MarkerFanStep = 0.35f;

        private MapView _lastMapView;
        private readonly Dictionary<LootableContainer, List<DynamicMaps.UI.Components.MapMarker>> _markersByContainer = [];
        private readonly Dictionary<IItemOwner, (Action<GEventArgs3> onRemove, Action<GEventArgs2> onAdd)> _ownerHandlers = [];
        private readonly List<Item> _scanBuffer = new(64);
        private readonly List<ContentHit> _hits = new(8);

        private struct ContentHit
        {
            public Item Item;
            public string Category;
            public Color Color;
            public string Label;
        }

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

        private static bool AnyContainerContentEnabled()
        {
            return Settings.ShowContainerWeaponsInRaid.Value
                   || Settings.ShowContainerArmorInRaid.Value
                   || Settings.ShowContainerVestsInRaid.Value
                   || Settings.ShowContainerBagsInRaid.Value
                   || Settings.ShowContainerStuffInRaid.Value;
        }

        private void IndexContainers()
        {
            if (!AnyContainerContentEnabled()) return;

            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null) return;

            var sw = Stopwatch.StartNew();
            var containerCount = 0;

            foreach (var killable in gameWorld.LootList)
            {
                if (killable is not LootableContainer container) continue;
                if (!container.IsInitialized || container.ItemOwner?.RootItem == null) continue;

                containerCount++;
                EnsureSubscribed(container);
                SyncContainerMarkers(container);
            }

            sw.Stop();
#if DEBUG
            Plugin.Log.LogInfo(
                $"[ContainerGear] containers={containerCount} marked={_markersByContainer.Count} ms={sw.ElapsedMilliseconds}");
#endif
        }

        private void SyncContainerMarkers(LootableContainer container)
        {
            if (container == null || _lastMapView == null) return;

            TryRemoveMarkers(container);

            if (container.ItemOwner?.RootItem == null)
            {
                TryRestoreHiddenStash(container);
                return;
            }

            _scanBuffer.Clear();
            container.ItemOwner.RootItem.GetAllAssembledItemsNonAlloc(_scanBuffer);
            CollectHits(_scanBuffer, _hits);

            if (_hits.Count == 0)
            {
                TryRestoreHiddenStash(container);
                return;
            }

            var transform = container.transform;
            if (transform == null) return;

            // Exact container world→map position (no large pair offset).
            var basePos = MathUtils.ConvertToMapPosition(transform);
            var list = new List<DynamicMaps.UI.Components.MapMarker>(_hits.Count);

            for (var i = 0; i < _hits.Count; i++)
            {
                var hit = _hits[i];
                var pos = FanPosition(basePos, i, _hits.Count);
                var marker = AddContentMarker(hit.Item, hit.Category, hit.Color, pos, hit.Label);
                marker.transform.SetAsLastSibling();
                list.Add(marker);
            }

            _markersByContainer[container] = list;
            TrySuppressHiddenStash(container);
        }

        private static Vector3 FanPosition(Vector3 basePos, int index, int total)
        {
            if (total <= 1)
            {
                return basePos;
            }

            var start = -0.5f * (total - 1) * MarkerFanStep;
            return basePos + new Vector3(start + index * MarkerFanStep, 0f, 0f);
        }

        private void CollectHits(List<Item> buffer, List<ContentHit> hits)
        {
            hits.Clear();

            Weapon longGun = null;
            Weapon pistol = null;
            Item bodyArmor = null;
            Item helmet = null;
            Item vest = null;
            Item bag = null;
            Item stuff = null;

            var intel = GameUtils.GetIntelLevel();
            var wantWeapons = Settings.ShowContainerWeaponsInRaid.Value
                              && Settings.ShowContainerWeaponIntelLevel.Value <= intel;
            var wantArmor = Settings.ShowContainerArmorInRaid.Value
                            && Settings.ShowContainerArmorIntelLevel.Value <= intel;
            var wantVests = Settings.ShowContainerVestsInRaid.Value
                            && Settings.ShowContainerArmorIntelLevel.Value <= intel;
            var wantBags = Settings.ShowContainerBagsInRaid.Value
                           && Settings.ShowContainerArmorIntelLevel.Value <= intel;
            var wantStuff = Settings.ShowContainerStuffInRaid.Value
                            && Settings.ShowContainerArmorIntelLevel.Value <= intel;

            for (var i = 0; i < buffer.Count; i++)
            {
                var item = buffer[i];
                if (item == null) continue;

                if (item is Weapon weapon)
                {
                    if (IsPistol(weapon))
                    {
                        pistol ??= weapon;
                    }
                    else
                    {
                        longGun ??= weapon;
                    }

                    continue;
                }

                if (IsBodyArmor(item))
                {
                    bodyArmor ??= item;
                    continue;
                }

                if (IsArmoredHelmet(item))
                {
                    helmet ??= item;
                    continue;
                }

                if (IsVest(item))
                {
                    vest ??= item;
                    continue;
                }

                if (IsBag(item))
                {
                    bag ??= item;
                    continue;
                }

                if (wantStuff && stuff == null && IsNotableStuff(item))
                {
                    stuff = item;
                }
            }

            if (wantWeapons && longGun != null)
            {
                hits.Add(new ContentHit
                {
                    Item = longGun,
                    Category = "ContainerWeapon",
                    Color = Settings.ContainerWeaponColor.Value,
                    Label = longGun.LocalizedName()
                });
            }

            if (wantWeapons && pistol != null)
            {
                hits.Add(new ContentHit
                {
                    Item = pistol,
                    Category = "ContainerPistol",
                    Color = Settings.ContainerPistolColor.Value,
                    Label = pistol.LocalizedName()
                });
            }

            if (wantArmor && bodyArmor != null)
            {
                hits.Add(new ContentHit
                {
                    Item = bodyArmor,
                    Category = "ContainerArmor",
                    Color = Settings.ContainerArmorColor.Value,
                    Label = bodyArmor.LocalizedName()
                });
            }

            if (wantArmor && helmet != null)
            {
                hits.Add(new ContentHit
                {
                    Item = helmet,
                    Category = "ContainerHelmet",
                    Color = Settings.ContainerHelmetColor.Value,
                    Label = helmet.LocalizedName()
                });
            }

            if (wantVests && vest != null)
            {
                hits.Add(new ContentHit
                {
                    Item = vest,
                    Category = "ContainerVest",
                    Color = Settings.ContainerVestColor.Value,
                    Label = vest.LocalizedName()
                });
            }

            if (wantBags && bag != null)
            {
                hits.Add(new ContentHit
                {
                    Item = bag,
                    Category = "ContainerBag",
                    Color = Settings.ContainerBagColor.Value,
                    Label = bag.LocalizedName()
                });
            }

            // Misc only when no gear categories are shown — avoids clutter on rich crates.
            var hasGearHit = hits.Count > 0;
            if (wantStuff && stuff != null && !hasGearHit)
            {
                hits.Add(new ContentHit
                {
                    Item = stuff,
                    Category = "ContainerStuff",
                    Color = Settings.ContainerStuffColor.Value,
                    Label = stuff.LocalizedName()
                });
            }
        }

        private static bool IsPistol(Weapon weapon)
        {
            return string.Equals(weapon.WeapClass, "pistol", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Chest/body armor only — not plates, not soft vests.</summary>
        private static bool IsBodyArmor(Item item)
        {
            return item is ArmorItemClass && item is not ArmorPlateItemClass;
        }

        /// <summary>Headwear that actually has an ArmorComponent (real helmets, not caps/ushankas).</summary>
        private static bool IsArmoredHelmet(Item item)
        {
            return item is HeadwearItemClass && item.GetItemComponent<ArmorComponent>() != null;
        }

        private static bool IsVest(Item item)
        {
            return item is VestItemClass;
        }

        private static bool IsBag(Item item)
        {
            if (item is BackpackItemClass)
            {
                return true;
            }

            return ItemViewFactory.GetItemType(item.GetType()) == EItemType.Backpack;
        }

        /// <summary>Skip pure junk (ammo stacks, money, keys noise) for the "stuff" pin.</summary>
        private static bool IsNotableStuff(Item item)
        {
            if (item is AmmoItemClass || item is MoneyItemClass || item is KeyItemClass)
            {
                return false;
            }

            if (item is ArmorPlateItemClass)
            {
                return false;
            }

            // Nested weapon parts / mags are not useful as the crate summary.
            if (item is MagazineItemClass || item is Mod)
            {
                return false;
            }

            return true;
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

        private DynamicMaps.UI.Components.MapMarker AddContentMarker(Item item, string category, Color color, Vector3 mapPosition, string label)
        {
            var itemType = ItemViewFactory.GetItemType(item.GetType());
            var itemSprite = EFTHardSettings.Instance.StaticIcons.ItemTypeSprites.GetValueOrDefault(itemType);

            var markerDef = new MapMarkerDef
            {
                Category = category,
                Color = color,
                Sprite = itemSprite,
                Position = mapPosition,
                Text = label
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
                SyncContainerMarkers(container);
            };

            Action<GEventArgs2> onAdd = args =>
            {
                if (args == null || args.Status != CommandStatus.Succeed) return;
                SyncContainerMarkers(container);
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

        private void TryRemoveAllMarkers()
        {
            foreach (var container in _markersByContainer.Keys.ToList())
            {
                TryRemoveMarkers(container);
            }
        }

        private void TryRemoveMarkers(LootableContainer container)
        {
            if (!_markersByContainer.TryGetValue(container, out var list)) return;

            foreach (var marker in list)
            {
                if (marker == null) continue;
                marker.ContainingMapView?.RemoveMapMarker(marker);
            }

            _markersByContainer.Remove(container);
        }
    }
}
