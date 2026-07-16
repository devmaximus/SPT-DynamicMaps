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
                try
                {
                    EnsureSubscribed(container);
                    SyncContainerMarkers(container);
                }
                catch (System.Exception ex)
                {
                    Plugin.Log.LogWarning($"[ContainerGear] failed on container '{container.name}': {ex.Message}");
                }
            }

            sw.Stop();
            Plugin.Log.LogInfo(
                $"[ContainerGear] containers={containerCount} marked={_markersByContainer.Count} ms={sw.ElapsedMilliseconds}");
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
            var root = container.ItemOwner.RootItem;
            root.GetAllAssembledItemsNonAlloc(_scanBuffer);
            CollectHits(_scanBuffer, root, _hits);

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
                // Under bots/exfils so gear pins don't bury combat/extract UI.
                marker.transform.SetAsFirstSibling();
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

        private void CollectHits(List<Item> buffer, Item root, List<ContentHit> hits)
        {
            hits.Clear();

            Weapon longGun = null;
            Weapon pistol = null;
            Item bodyArmor = null;
            Item helmet = null;
            Item vest = null;
            Item bag = null;
            Item stuff = null;
            var hasAnyLoot = false;

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
                // Root is the crate/stash shell — never treat it as loot (empty-crate noise).
                if (root != null && ReferenceEquals(item, root)) continue;

                hasAnyLoot = true;

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

            // Gray box for any remaining contents when no weapon/armor/vest/bag pin.
            // Empty crates (root only) must produce zero hits.
            var hasGearHit = hits.Count > 0;
            if (wantStuff && !hasGearHit && hasAnyLoot)
            {
                hits.Add(new ContentHit
                {
                    Item = stuff,
                    Category = "ContainerStuff",
                    Color = Settings.ContainerStuffColor.Value,
                    Label = stuff != null ? stuff.LocalizedName() : "Loot"
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

        /// <summary>Misc pin can use any remaining loot; skip nested mods/plates as the label pick.</summary>
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

        // Mid sizes — readable without covering map labels / other markers (default map pin is 30²).
        private static readonly Vector2 SizeGun = new Vector2(20f, 11f);
        private static readonly Vector2 SizePistol = new Vector2(14f, 12f);
        private static readonly Vector2 SizeArmor = new Vector2(14f, 16f);
        private static readonly Vector2 SizeHelmet = new Vector2(14f, 12f);
        private static readonly Vector2 SizeVest = new Vector2(14f, 16f);
        private static readonly Vector2 SizeBag = new Vector2(14f, 14f);
        private static readonly Vector2 SizeStuff = new Vector2(10f, 10f);
        private static readonly Color32 White = new Color32(255, 255, 255, 255);

        private DynamicMaps.UI.Components.MapMarker AddContentMarker(Item item, string category, Color color, Vector3 mapPosition, string label)
        {
            ResolveVisual(category, out var sprite, out var size);
            var markerDef = new MapMarkerDef
            {
                Category = category,
                Color = color,
                Sprite = sprite,
                ImagePath = null,
                Position = mapPosition,
                Text = label
            };

            var marker = _lastMapView.AddMapMarker(markerDef);
            marker.Size = size;
            return marker;
        }

        private static void ResolveVisual(string category, out Sprite sprite, out Vector2 size)
        {
            switch (category)
            {
                case "ContainerWeapon":
                    size = SizeGun;
                    sprite = TextureUtils.GetOrCreateCachedSprite("proc/gun", 20, 10, PaintGun);
                    break;
                case "ContainerPistol":
                    size = SizePistol;
                    sprite = TextureUtils.GetOrCreateCachedSprite("proc/pistol", 12, 10, PaintPistol);
                    break;
                case "ContainerArmor":
                    size = SizeArmor;
                    sprite = TextureUtils.GetOrCreateCachedSprite("proc/armor", 12, 14, PaintArmor);
                    break;
                case "ContainerHelmet":
                    size = SizeHelmet;
                    sprite = TextureUtils.GetOrCreateCachedSprite("proc/helmet", 12, 10, PaintHelmet);
                    break;
                case "ContainerVest":
                    size = SizeVest;
                    sprite = TextureUtils.GetOrCreateCachedSprite("proc/vest", 12, 14, PaintVest);
                    break;
                case "ContainerBag":
                    size = SizeBag;
                    sprite = TextureUtils.GetOrLoadCachedSprite("Markers/backpack.png")
                             ?? TextureUtils.GetOrCreateCachedSprite("proc/bag", 12, 12, PaintBag);
                    break;
                default:
                    size = SizeStuff;
                    sprite = TextureUtils.GetOrCreateCachedSprite("proc/square", 8, 8, PaintSquare);
                    break;
            }

            if (sprite == null)
            {
                sprite = TextureUtils.GetOrCreateCachedSprite("proc/square", 8, 8, PaintSquare);
                size = SizeStuff;
            }
        }

        private static void PaintGun(Texture2D tex)
        {
            TextureUtils.FillRect(tex, 1, 3, 5, 7, White);
            TextureUtils.FillRect(tex, 5, 2, 12, 7, White);
            TextureUtils.FillRect(tex, 12, 3, 19, 5, White);
            TextureUtils.FillRect(tex, 8, 7, 11, 9, White);
            TextureUtils.FillRect(tex, 10, 1, 12, 2, White);
        }

        private static void PaintPistol(Texture2D tex)
        {
            TextureUtils.FillRect(tex, 2, 2, 10, 5, White);
            TextureUtils.FillRect(tex, 2, 5, 6, 9, White);
            TextureUtils.FillRect(tex, 6, 5, 8, 6, White);
        }

        private static void PaintArmor(Texture2D tex)
        {
            TextureUtils.FillRect(tex, 2, 2, 9, 12, White);
            TextureUtils.FillRect(tex, 1, 3, 2, 6, White);
            TextureUtils.FillRect(tex, 9, 3, 10, 6, White);
            TextureUtils.FillRect(tex, 4, 0, 7, 2, White);
        }

        private static void PaintHelmet(Texture2D tex)
        {
            TextureUtils.FillRect(tex, 2, 3, 9, 8, White);
            TextureUtils.FillRect(tex, 3, 1, 8, 3, White);
            TextureUtils.FillRect(tex, 1, 7, 10, 8, White);
            TextureUtils.FillRect(tex, 4, 8, 7, 9, White);
        }

        private static void PaintVest(Texture2D tex)
        {
            TextureUtils.FillRect(tex, 1, 1, 4, 12, White);
            TextureUtils.FillRect(tex, 7, 1, 10, 12, White);
            TextureUtils.FillRect(tex, 4, 2, 7, 4, White);
            TextureUtils.FillRect(tex, 4, 10, 7, 12, White);
            TextureUtils.FillRect(tex, 2, 0, 3, 2, White);
            TextureUtils.FillRect(tex, 8, 0, 9, 2, White);
        }

        private static void PaintBag(Texture2D tex)
        {
            TextureUtils.FillRect(tex, 2, 2, 9, 11, White);
            TextureUtils.FillRect(tex, 3, 0, 8, 2, White);
            TextureUtils.FillRect(tex, 1, 4, 2, 9, White);
            TextureUtils.FillRect(tex, 9, 4, 10, 9, White);
        }

        private static void PaintSquare(Texture2D tex)
        {
            TextureUtils.FillRect(tex, 0, 0, tex.width - 1, tex.height - 1, White);
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
