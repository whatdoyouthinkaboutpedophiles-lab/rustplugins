using System.Collections.Generic;
using UnityEngine;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("BuildServerCraft", "TheVekT", "1.0.8")]
    [Description("Идеальный крафт: моментально, без верстака (через ClientRPC), все чертежи и ресурсы")]
    class BuildServerCraft : RustPlugin
    {
        private const string PermUse = "buildservercraft.use";

        // Хранилища для отката изменений при выгрузке плагина
        private Dictionary<int, int> originalWbLevels = new Dictionary<int, int>();
        private Dictionary<int, float> originalCraftTimes = new Dictionary<int, float>();

        // Расширенный список ресурсов
        private Dictionary<string, int> resourceList = new Dictionary<string, int>
        {
            { "wood", 1000000 }, 
            { "stones", 1000000 }, 
            { "metal.fragments", 1000000 },
            { "metal.refined", 1000000 }, 
            { "cloth", 1000000 }, 
            { "leather", 1000000 },
            { "lowgradefuel", 1000000 }, 
            { "animalfat", 1000000 }, 
            { "scrap", 1000000 },
            { "gears", 10000 }, 
            { "metalpipe", 10000 }, 
            { "metalspring", 10000 },
            { "riflebody", 10000 }, 
            { "roadsigns", 10000 }, 
            { "rope", 10000 },
            { "semibody", 10000 }, 
            { "sewingkit", 10000 }, 
            { "smgbody", 10000 },
            { "tarp", 10000 }, 
            { "techparts", 10000 }, 
            { "sheetmetal", 10000 }, 
            { "propanetank", 10000 },
            { "cctv.camera", 10000 }, 
            { "targeting.computer", 10000 }, 
            { "metalblade", 10000 },
            { "gunpowder", 1000000 }, 
            { "sulfur", 1000000 }, 
            { "explosives", 100000 },
            { "advancedblueprintfragment", 10000 },
            { "basicblueprintfragment", 10000 },
            { "ladder.wooden.wall", 10000 }
        };

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
        }

        private void OnServerInitialized()
        {
            PatchBlueprints();

            foreach (var player in BasePlayer.activePlayerList)
            {
                SetupPlayer(player);
            }
        }

        private void Unload()
        {
            RestoreBlueprints();

            foreach (var player in BasePlayer.activePlayerList)
            {
                RemoveHiddenItems(player);
                
                // Возвращаем обычный режим крафта при выгрузке
                if (player != null && player.IsConnected)
                {
                    player.ClientRPC(RpcTarget.Player("craftMode", player), 0);
                }
            }
        }

        // ==========================================
        // 1. ГЛОБАЛЬНЫЙ ПАТЧ ЧЕРТЕЖЕЙ
        // ==========================================
        private void PatchBlueprints()
        {
            foreach (var bp in ItemManager.GetBlueprints())
            {
                if (bp == null) continue;

                originalWbLevels[bp.targetItem.itemid] = bp.workbenchLevelRequired;
                bp.workbenchLevelRequired = 0; 

                originalCraftTimes[bp.targetItem.itemid] = bp.time;
                bp.time = 0f;
                
                var bpoverride = bp.GetRecipeOverride();
                bpoverride.craftTime = 0f;
            }
        }

        private void RestoreBlueprints()
        {
            foreach (var bp in ItemManager.GetBlueprints())
            {
                if (bp == null) continue;

                if (originalWbLevels.ContainsKey(bp.targetItem.itemid))
                    bp.workbenchLevelRequired = originalWbLevels[bp.targetItem.itemid];

                if (originalCraftTimes.ContainsKey(bp.targetItem.itemid))
                {
                    bp.time = originalCraftTimes[bp.targetItem.itemid];
                    var bpoverride = bp.GetRecipeOverride();
                    bpoverride.craftTime = originalCraftTimes[bp.targetItem.itemid];
                }
            }
        }

        // ==========================================
        // 2. ВЫДАЧА РЕЖИМА, ИЗУЧЕНИЙ И РЕСУРСОВ
        // ==========================================
        private void OnPlayerConnected(BasePlayer player)
        {
            // Ждем завершения загрузки клиента, как это делали в NoWorkbench
            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.In(3, () => OnPlayerConnected(player));
                return;
            }
            SetupPlayer(player);
        }

        private void OnPlayerRespawned(BasePlayer player) => SetupPlayer(player);

        private void SetupPlayer(BasePlayer player)
        {
            if (player == null || !permission.UserHasPermission(player.UserIDString, PermUse)) return;

            // МАГИЯ: Заставляем клиент активировать "свободный" крафт
            player.ClientRPC(RpcTarget.Player("craftMode", player), 1);

            // Изучаем чертежи и обновляем интерфейс
            player.blueprints.UnlockAll();
            player.ClientRPC(RpcTarget.Player("UnlockedBlueprint", player), 0);

            timer.Once(1f, () => SetupHiddenItems(player));
        }

        private void SetupHiddenItems(BasePlayer player)
        {
            if (player == null || player.inventory == null || player.inventory.containerMain == null) return;

            int baseSlots = 24;
            player.inventory.containerMain.capacity = baseSlots + resourceList.Count;
            
            int currentSlot = baseSlots;

            foreach (var entry in resourceList)
            {
                Item existingItem = player.inventory.containerMain.GetSlot(currentSlot);
                if (existingItem != null)
                {
                    if (existingItem.info.shortname == entry.Key)
                    {
                        existingItem.amount = entry.Value;
                        existingItem.MarkDirty();
                        currentSlot++;
                        continue;
                    }
                    existingItem.RemoveFromContainer();
                    existingItem.Remove();
                }

                Item item = ItemManager.CreateByName(entry.Key, entry.Value);
                if (item != null)
                {
                    if (!item.MoveToContainer(player.inventory.containerMain, currentSlot, true, true))
                        item.Remove();
                }
                currentSlot++;
            }
        }

        private void RemoveHiddenItems(BasePlayer player)
        {
            if (player == null || player.inventory == null || player.inventory.containerMain == null) return;
            
            for (int i = 24; i < player.inventory.containerMain.capacity; i++)
            {
                Item item = player.inventory.containerMain.GetSlot(i);
                if (item != null)
                {
                    item.RemoveFromContainer();
                    item.Remove();
                }
            }
            player.inventory.containerMain.capacity = 24;
        }

        // ==========================================
        // 3. ИСТИННЫЙ МОМЕНТАЛЬНЫЙ КРАФТ И АНТИ-АБУЗ
        // ==========================================
        private object OnItemCraft(ItemCraftTask task, BasePlayer player)
        {
            if (player == null || !permission.UserHasPermission(player.UserIDString, PermUse)) return null;

            int totalAmountToCreate = task.amount * task.blueprint.amountToCreate;
            Item craftedItem = ItemManager.Create(task.blueprint.targetItem, totalAmountToCreate, (ulong)task.skinID);
            
            if (craftedItem != null)
            {
                player.GiveItem(craftedItem);
                Interface.CallHook("OnItemCraftFinished", task, craftedItem, player.inventory.crafting);
            }

            task.cancelled = true;

            NextTick(() => {
                if (player != null && player.IsConnected)
                    SetupHiddenItems(player);
            });

            return true; 
        }

        private void OnItemCraftCancelled(ItemCraftTask task)
        {
            if (task.takenItems == null) return;
            
            foreach (var entry in task.takenItems)
            {
                if (entry != null && resourceList.ContainsKey(entry.info.shortname))
                {
                    entry.RemoveFromContainer();
                    entry.Remove();
                }
            }
        }
    }
}