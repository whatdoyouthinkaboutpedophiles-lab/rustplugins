using System;
using System.IO;
using System.Net;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries.Covalence;
using UnityEngine;
using Network;

namespace Oxide.Plugins
{
    [Info("Admin Utilities", "dFxPhoeniX", "2.6.2")]
    [Description("Toggle NoClip, teleport Under Terrain and more")]
    public class AdminUtilities : RustPlugin
    {
        private const string permTerrain = "adminutilities.disconnectteleport";
        private const string permInventory = "adminutilities.saveinventory";
        private const string permHHT = "adminutilities.maxhht";
        private const string permNoClip = "adminutilities.nocliptoggle";
        private const string permGodMode = "adminutilities.godmodetoggle";
        private const string permKick = "adminutilities.kick";
        private const string permBan = "adminutilities.ban";
        private const string permBanIp = "adminutilities.banip";
        private const string permUnban = "adminutilities.unban";
        private const string permBanList = "adminutilities.banlist";
        private const string permPreventKick = "adminutilities.preventkick";
        private const string permPreventBan = "adminutilities.preventban";
        private const string permPreventBanIp = "adminutilities.preventbanip";
        private const string permGive = "adminutilities.give";
        private const string permGiveTo = "adminutilities.giveto";
        private const string permGiveAll = "adminutilities.giveall";
        private const string permSpawn = "adminutilities.spawn";
        private const string permSpawnTo = "adminutilities.spawnto";
        private const string permSpawnAll = "adminutilities.spawnall";
        private const string permDespawn = "adminutilities.despawn";
        private const string permBypassBlacklist = "adminutilities.bypassblacklist";
        private const string permBypassDisabledPlayerConsoleCommands = "adminutilities.bypassplayerconsolecommands";
        private const string permBypassDisabledChatCommands = "adminutilities.bypasschatcommands";

        private DataFileSystem dataFile;
        private DataFileSystem dataFileItems;

        private ModerationData moderationData = new ModerationData();
        private readonly HashSet<ulong> pendingForceNoClip = new HashSet<ulong>();

        private readonly HashSet<string> allPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> prefabLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private bool newSave;

        ////////////////////////////////////////////////////////////
        // Files
        ////////////////////////////////////////////////////////////

        private class PlayerInfo
        {
            public string Teleport { get; set; } = Vector3.zero.ToString();
            public bool SaveInventory { get; set; } = true;
            public bool UnderTerrain { get; set; } = false;
            public bool NoClip { get; set; } = false;
            public bool GodMode { get; set; } = false;
        }

        private Dictionary<string, PlayerInfo> playerInfoCache = new Dictionary<string, PlayerInfo>();

        private class PlayerInfoItems
        {
            public List<AdminUtilitiesItem> Items { get; set; } = new List<AdminUtilitiesItem>();
            public bool SnapshotOnly { get; set; } = false;
        }

        private Dictionary<string, PlayerInfoItems> playerInfoItemsCache = new Dictionary<string, PlayerInfoItems>();

        private class AdminUtilitiesItem
        {
            public List<AdminUtilitiesItem> contents { get; set; }
            public string container { get; set; } = "main";
            public int ammo { get; set; }
            public int amount { get; set; }
            public int flags { get; set; }
            public int containerSlots { get; set; }
            public bool hasInstanceData { get; set; }
            public string ammoType { get; set; }
            public float condition { get; set; }
            public float fuel { get; set; }
            public int frequency { get; set; }
            public int itemid { get; set; }
            public float maxCondition { get; set; }
            public string name { get; set; }
            public int position { get; set; } = -1;
            public ulong skin { get; set; }
            public string text { get; set; }
            public int blueprintAmount { get; set; }
            public int blueprintTarget { get; set; }
            public int dataInt { get; set; }
            public ulong subEntity { get; set; }
            public bool shouldPool { get; set; }

            public AdminUtilitiesItem() { }

            public AdminUtilitiesItem(string container, Item item)
            {
                this.container = container;
                itemid = item.info.itemid;
                name = item.name;
                text = item.text;
                amount = item.amount;
                condition = item.condition;
                maxCondition = item.maxCondition;
                fuel = item.fuel;
                position = item.position;
                skin = item.skin;
                flags = (int)item.flags;

                hasInstanceData = item.instanceData != null;
                if (item.instanceData != null)
                {
                    dataInt = item.instanceData.dataInt;
                    blueprintAmount = item.instanceData.blueprintAmount;
                    blueprintTarget = item.instanceData.blueprintTarget;
                    subEntity = item.instanceData.subEntity.Value;
                    shouldPool = item.instanceData.ShouldPool;
                }

                if (item.contents != null)
                {
                    containerSlots = item.contents.capacity;

                    if (item.contents.itemList != null && item.contents.itemList.Count > 0)
                    {
                        contents = new List<AdminUtilitiesItem>();
                        foreach (var mod in item.contents.itemList)
                            contents.Add(new AdminUtilitiesItem("default", mod));
                    }
                }

                if (item.GetHeldEntity() is HeldEntity e)
                {
                    if (e is BaseProjectile baseProjectile)
                    {
                        ammo = baseProjectile.primaryMagazine.contents;
                        ammoType = baseProjectile.primaryMagazine.ammoType?.shortname;
                    }
                    else if (e is FlameThrower flameThrower)
                    {
                        ammo = flameThrower.ammo;
                    }
                }

                if (ItemModAssociatedEntity<PagerEntity>.GetAssociatedEntity(item) is PagerEntity pagerEntity)
                {
                    frequency = pagerEntity.GetFrequency();
                }
            }

            public static Item Create(AdminUtilitiesItem aui, bool restoreContents = true)
            {
                if (aui.itemid == 0 || string.IsNullOrEmpty(aui.container))
                    return null;

                Item item;
                if (aui.blueprintTarget != 0)
                {
                    item = ItemManager.Create(Workbench.GetBlueprintTemplate());
                    item.blueprintTarget = aui.blueprintTarget;
                    item.amount = aui.blueprintAmount;
                }
                else item = ItemManager.CreateByItemID(aui.itemid, aui.amount, aui.skin);

                if (item == null) return null;

                item.flags = (Item.Flag)aui.flags;

                if (aui.hasInstanceData)
                {
                    item.instanceData = aui.shouldPool ? Pool.Get<ProtoBuf.Item.InstanceData>() : new ProtoBuf.Item.InstanceData();
                    item.instanceData.ShouldPool = aui.shouldPool;
                    item.instanceData.blueprintAmount = aui.blueprintAmount;
                    item.instanceData.blueprintTarget = aui.blueprintTarget;
                    item.instanceData.dataInt = aui.dataInt;
                    item.instanceData.subEntity = new NetworkableId(aui.subEntity);
                }

                if (!string.IsNullOrEmpty(aui.name)) item.name = aui.name;
                if (!string.IsNullOrEmpty(aui.text)) item.text = aui.text;

                if (item.GetHeldEntity() is HeldEntity e)
                {
                    if (item.skin != 0) e.skinID = item.skin;

                    if (e is BaseProjectile bp)
                    {
                        bp.primaryMagazine.contents = aui.ammo;
                        if (!string.IsNullOrEmpty(aui.ammoType))
                            bp.primaryMagazine.ammoType = ItemManager.FindItemDefinition(aui.ammoType);
                        bp.DelayedModsChanged();
                    }
                    else if (e is FlameThrower ft) ft.ammo = aui.ammo;
                    else if (e is Chainsaw cs) cs.ammo = aui.ammo;

                    e.SendNetworkUpdate();
                }

                if (aui.frequency > 0 && item.info.GetComponentInChildren<ItemModRFListener>() != null)
                {
                    if (item.instanceData != null && item.instanceData.subEntity.IsValid &&
                        BaseNetworkable.serverEntities.Find(item.instanceData.subEntity) is PagerEntity pagerEntity)
                    {
                        pagerEntity.ChangeFrequency(aui.frequency);
                    }
                }

                if (restoreContents)
                {
                    var slots = System.Math.Max(aui.containerSlots, aui.contents?.Count ?? 0);
                    EnsureContainer(item, slots);
                    RestoreContents(null, item, aui.contents);
                }

                if (item.hasCondition)
                {
                    item._maxCondition = aui.maxCondition;
                    item._condition = aui.condition;
                }

                item.fuel = aui.fuel;
                item.MarkDirty();
                return item;
            }

            private static T FindItemMod<T>(Item item) where T : ItemMod
            {
                var mods = item?.info?.itemMods;
                if (mods == null) return null;

                foreach (var m in mods)
                    if (m is T t) return t;

                return null;
            }

            private static void EnsureContainer(Item item, int slots)
            {
                if (item == null || slots <= 0) return;

                // Armor inserts container
                var armorSlot = FindItemMod<ItemModContainerArmorSlot>(item);
                if (armorSlot != null)
                {
                    armorSlot.CreateAtCapacity(slots, item);
                    return;
                }

                // fallback generic container
                if (item.contents == null)
                {
                    item.contents = Pool.Get<ItemContainer>();
                    item.contents.ServerInitialize(item, slots);
                    if (!item.contents.uid.IsValid)
                        item.contents.GiveUID();
                }
            }

            private static void RestoreContents(BasePlayer player, Item parent, List<AdminUtilitiesItem> saved)
            {
                if (saved == null || saved.Count == 0) return;
                if (parent?.contents == null) return;

                foreach (var aum in saved)
                {
                    var mod = Create(aum, true);
                    if (mod == null) continue;

                    if (mod.MoveToContainer(parent.contents, aum.position, true) || mod.MoveToContainer(parent.contents))
                        continue;

                    if (player != null) player.GiveItem(mod);
                    else mod.Remove();
                }

                parent.contents.MarkDirty();
            }

            public static void Restore(BasePlayer player, AdminUtilitiesItem aui)
            {
                Item item = Create(aui, restoreContents: false);
                if (item == null) return;

                ItemContainer target;
                switch (aui.container)
                {
                    case "belt":
                        target = player.inventory.containerBelt;
                        break;
                    case "wear":
                        target = player.inventory.containerWear;
                        break;
                    case "main":
                    default:
                        target = player.inventory.containerMain;
                        break;
                }

                bool moved = item.MoveToContainer(target, aui.position, true);
                if (!moved) player.GiveItem(item);

                var slots = System.Math.Max(aui.containerSlots, aui.contents?.Count ?? 0);
                EnsureContainer(item, slots);
                RestoreContents(player, item, aui.contents);

                if (item.GetHeldEntity() is BaseProjectile bp)
                {
                    bp.DelayedModsChanged();
                    bp.SendNetworkUpdate();
                }

                item.MarkDirty();
            }
        }

        private class ModerationData
        {
            public Dictionary<string, BanRecord> PlayerBans { get; set; } = new Dictionary<string, BanRecord>();
            public Dictionary<string, BanRecord> IpBans { get; set; } = new Dictionary<string, BanRecord>();
            public Dictionary<string, string> LastKnownIps { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, string> LastKnownNames { get; set; } = new Dictionary<string, string>();
        }

        private class BanRecord
        {
            public string Target { get; set; }
            public string DisplayName { get; set; }
            public string Reason { get; set; }
            public string Source { get; set; }
            public long CreatedAt { get; set; }
            public long ExpiresAt { get; set; } // 0 = permanent
        }

        private PlayerInfo LoadPlayerInfo(BasePlayer player)
        {
            if (dataFile == null || player == null) return null;

            if (playerInfoCache.TryGetValue(player.UserIDString, out var cached))
                return cached;

            var user = dataFile.ReadObject<PlayerInfo>(player.UserIDString) ?? new PlayerInfo();
            playerInfoCache[player.UserIDString] = user;
            return user;
        }

        private void SavePlayerInfo(BasePlayer player, PlayerInfo playerInfo)
        {
            if (dataFile == null) return;

            dataFile.WriteObject($"{player.UserIDString}", playerInfo);
            playerInfoCache[player.UserIDString] = playerInfo;
        }

        private PlayerInfoItems LoadPlayerInfoItems(BasePlayer player)
        {
            if (dataFileItems == null || player == null) return null;

            if (playerInfoItemsCache.TryGetValue(player.UserIDString, out var cached))
                return cached;

            var userItems = dataFileItems.ReadObject<PlayerInfoItems>(player.UserIDString) ?? new PlayerInfoItems();
            playerInfoItemsCache[player.UserIDString] = userItems;
            return userItems;
        }

        private void SavePlayerInfoItems(BasePlayer player, PlayerInfoItems playerInfoItems)
        {
            if (dataFileItems == null) return;

            dataFileItems.WriteObject($"{player.UserIDString}", playerInfoItems);
            playerInfoItemsCache[player.UserIDString] = playerInfoItems;
        }

        private void LoadModerationData()
        {
            if (dataFile == null)
                return;

            moderationData = dataFile.ReadObject<ModerationData>("global") ?? new ModerationData();
        }

        private void SaveModerationData()
        {
            if (dataFile == null)
                return;

            dataFile.WriteObject("global", moderationData);
        }

        ////////////////////////////////////////////////////////////
        // Oxide Hooks
        ////////////////////////////////////////////////////////////

        private void OnNewSave()
        {
            newSave = true;
        }

        private void Init()
        {
            InitConfig();

            dataFile = new DataFileSystem($"{Interface.Oxide.DataDirectory}\\AdminUtilities\\Settings");
            dataFileItems = new DataFileSystem($"{Interface.Oxide.DataDirectory}\\AdminUtilities\\Items");
            LoadModerationData();
        }

        private void Loaded()
        {
            permission.RegisterPermission(permTerrain, this);
            permission.RegisterPermission(permInventory, this);
            permission.RegisterPermission(permHHT, this);
            permission.RegisterPermission(permNoClip, this);
            permission.RegisterPermission(permGodMode, this);
            permission.RegisterPermission(permKick, this);
            permission.RegisterPermission(permBan, this);
            permission.RegisterPermission(permBanIp, this);
            permission.RegisterPermission(permUnban, this);
            permission.RegisterPermission(permBanList, this);
            permission.RegisterPermission(permPreventKick, this);
            permission.RegisterPermission(permPreventBan, this);
            permission.RegisterPermission(permPreventBanIp, this);
            permission.RegisterPermission(permGive, this);
            permission.RegisterPermission(permGiveTo, this);
            permission.RegisterPermission(permGiveAll, this);
            permission.RegisterPermission(permSpawn, this);
            permission.RegisterPermission(permSpawnTo, this);
            permission.RegisterPermission(permSpawnAll, this);
            permission.RegisterPermission(permDespawn, this);
            permission.RegisterPermission(permBypassBlacklist, this);
            permission.RegisterPermission(permBypassDisabledPlayerConsoleCommands, this);
            permission.RegisterPermission(permBypassDisabledChatCommands, this);
        }

        private void OnServerInformationUpdated()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;

                bool relevant = player.IsFlying || player.IsGod() || player.IsDeveloper;
                if (!relevant) continue;

                var user = LoadPlayerInfo(player);
                if (user == null) continue;

                if (!player.IsFlying && user.NoClip)
                {
                    user.NoClip = false;
                    SavePlayerInfo(player, user);
                }

                if (!player.IsGod() && user.GodMode)
                {
                    user.GodMode = false;
                    SavePlayerInfo(player, user);
                }

                if (player.IsDeveloper && !player.IsFlying && !player.IsGod())
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper, false);
            }
        }

        private object OnClientCommand(Connection connection, string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return null;

            BasePlayer player = BasePlayer.FindByID(connection.userid);
            if (player == null)
                return null;

            PlayerInfo user = LoadPlayerInfo(player);
            if (user == null)
                return null;

            string lowerCommand = command.ToLowerInvariant();

            if (lowerCommand.Contains("setinfo \"global.god\" \"true\""))
            {
                if (!HasPermission(player, permGodMode) && !user.GodMode && !player.IsGod())
                    return false;
            }

            string playerConsoleCommand = ExtractPlayerConsoleCommandName(lowerCommand);
            if (IsDisabledPlayerConsoleCommand(playerConsoleCommand) &&
                !HasPermission(player, permBypassDisabledPlayerConsoleCommands))
                return false;

            string chatCommand = ExtractChatCommandName(command);
            if (IsDisabledChatCommand(chatCommand) &&
                !HasPermission(player, permBypassDisabledChatCommands))
                return false;

            return null;
        }

        private object OnServerCommand(ConsoleSystem.Arg arg)
        {
            if (arg?.cmd == null || arg.Connection != null)
                return null;

            string cmd = arg.cmd.FullName.ToLowerInvariant();

            string[] args = arg.Args ?? Array.Empty<string>();
            return TryHandleServerConsoleCommand(arg, cmd, args) ? (object)true : null;
        }

        private object OnRconCommand(IPAddress ipAddress, string command, string[] args)
        {
            if (string.IsNullOrWhiteSpace(command))
                return null;

            return TryHandleServerConsoleCommand(null, command.ToLowerInvariant(), args ?? Array.Empty<string>()) ? (object)true : null;
        }

        private void OnBroadcastCommand(string command, object[] args)
        {
            TryApplyGlobalServerIcon(command, args);
        }

        private void OnSendCommand(Connection cn, string command, object[] args)
        {
            TryApplyGlobalServerIcon(command, args);
        }

        private void OnSendCommand(List<Connection> cn, string command, object[] args)
        {
            TryApplyGlobalServerIcon(command, args);
        }

        private void OnPlayerTick(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            if (player.IsNpc || player is NPCPlayer) return;

            if (!player.IsFlying) return;

            if (HasPermission(player, permNoClip)) return;

            if (pendingForceNoClip.Contains(player.userID))
                return;

            var user = LoadPlayerInfo(player);
            if (user == null) return;

            if (user.NoClip) return;

            pendingForceNoClip.Add(player.userID);

            timer.Once(0.05f, () =>
            {
                if (player == null || !player.IsConnected)
                {
                    pendingForceNoClip.Remove(player.userID);
                    return;
                }

                pendingForceNoClip.Remove(player.userID);

                if (!HasPermission(player, permNoClip) && player.IsFlying)
                    player.SendConsoleCommand("noclip");
            });
        }

        private void OnServerInitialized()
        {
            if (GetSave())
            {
                if (wipeSettings)
                {
                    string folderPath = $"{Interface.Oxide.DataDirectory}/AdminUtilities/Settings";

                    if (Directory.Exists(folderPath))
                    {
                        foreach (string file in Directory.GetFiles(folderPath))
                            File.Delete(file);
                    }
                }

                if (wipeItems)
                {
                    string folderItemsPath = $"{Interface.Oxide.DataDirectory}/AdminUtilities/Items";

                    if (Directory.Exists(folderItemsPath))
                    {
                        foreach (string file in Directory.GetFiles(folderItemsPath))
                            File.Delete(file);
                    }
                }

                newSave = false;
            }

            CachePrefabs();
            CleanupExpiredBans();
        }

        private void OnUserBanned(string name, string id, string ipAddress, string reason, long expiry)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            long now = UnixNow();
            long newExpiry = expiry > 0 ? expiry : 0;
            bool changed = false;

            if (!moderationData.PlayerBans.TryGetValue(id, out var existing) || existing == null)
            {
                moderationData.PlayerBans[id] = new BanRecord
                {
                    Target = id,
                    DisplayName = string.IsNullOrWhiteSpace(name) ? id : name,
                    Reason = string.IsNullOrWhiteSpace(reason) ? defaultBanReason : reason,
                    Source = "Native",
                    CreatedAt = now,
                    ExpiresAt = newExpiry
                };

                changed = true;
            }
            else
            {
                string newName = string.IsNullOrWhiteSpace(name) ? existing.DisplayName : name;
                string newReason = string.IsNullOrWhiteSpace(reason) ? existing.Reason : reason;

                if (existing.DisplayName != newName)
                {
                    existing.DisplayName = newName;
                    changed = true;
                }

                if (existing.Reason != newReason)
                {
                    existing.Reason = newReason;
                    changed = true;
                }

                if (existing.Source != "Native")
                {
                    existing.Source = "Native";
                    changed = true;
                }

                // Native event should be authoritative for the current player-ban state
                if (existing.ExpiresAt != newExpiry)
                {
                    existing.ExpiresAt = newExpiry;
                    changed = true;
                }

                // Optional: refresh CreatedAt when the native ban is re-applied/changed
                if (existing.CreatedAt <= 0)
                {
                    existing.CreatedAt = now;
                    changed = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(ipAddress) && ipAddress != "0")
            {
                if (IsValidStoredIp(ipAddress))
                {
                    if (!moderationData.LastKnownIps.TryGetValue(id, out var oldIp) || oldIp != ipAddress)
                    {
                        moderationData.LastKnownIps[id] = ipAddress;
                        changed = true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                if (!moderationData.LastKnownNames.TryGetValue(id, out var oldName) || oldName != name)
                {
                    moderationData.LastKnownNames[id] = name;
                    changed = true;
                }
            }

            if (changed)
                SaveModerationData();
        }

        private void OnUserUnbanned(string name, string id, string ipAddress)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            bool changed = false;

            if (moderationData.PlayerBans.Remove(id))
                changed = true;

            if (changed)
                SaveModerationData();
        }

        private void OnEntityTakeDamage(BasePlayer player, HitInfo info)
        {
            if (player == null || info == null)
                return;

            if (player.IsNpc || (player is NPCPlayer))
                return;

            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return;

            if (user.UnderTerrain)
            {
                info.damageTypes = new Rust.DamageTypeList();
            }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            pendingForceNoClip.Remove(player.userID);

            var user = LoadPlayerInfo(player);
            if (user == null)
            {
                playerInfoCache.Remove(player.UserIDString);
                playerInfoItemsCache.Remove(player.UserIDString);
                return;
            }

            if (!HasPermission(player, permNoClip) && user.NoClip)
            {
                user.NoClip = false;
                SavePlayerInfo(player, user);
            }

            if (!HasPermission(player, permGodMode) && user.GodMode)
            {
                user.GodMode = false;
                SavePlayerInfo(player, user);
            }

            if (!persistentNoClip && user.NoClip)
            {
                user.NoClip = false;
                SavePlayerInfo(player, user);
            }

            if (!persistentGodMode && user.GodMode)
            {
                user.GodMode = false;
                SavePlayerInfo(player, user);
            }

            if (!player.IsDead())
            {
                if (HasPermission(player, permTerrain))
                    DisconnectTeleport(player);

                if (HasPermission(player, permInventory) && user.SaveInventory)
                    SaveInventory(player, player.IsSleeping());
            }

            playerInfoCache.Remove(player.UserIDString);
            playerInfoItemsCache.Remove(player.UserIDString);
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            UpdateLastKnownPlayerIdentity(player);

            PlayerInfo user = LoadPlayerInfo(player);
            if (user == null)
                return;

            if (!HasPermission(player, permTerrain) && user.UnderTerrain)
            {
                NoUnderTerrain(player);
            }

            if (persistentNoClip && HasPermission(player, permNoClip) && user.NoClip)
            {
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper, true);

                timer.Once(0.2f, () =>
                {
                    if (player == null || !player.IsConnected) return;
                    player.SendConsoleCommand("noclip");
                });
            }

            if (persistentGodMode && HasPermission(player, permGodMode) && user.GodMode)
            {
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper, true);

                timer.Once(0.2f, () =>
                {
                    if (player == null || !player.IsConnected) return;
                    player.SendConsoleCommand("setinfo \"global.god\" \"true\"");
                });
            }

            timer.Once(0.5f, () =>
            {
                if (player == null || !player.IsConnected) return;
                RestoreSavedInventory(player);
            });
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (HasPermission(player, permHHT))
            {
                player.health = 100f;
                player.metabolism.hydration.value = player.metabolism.hydration.max;
                player.metabolism.calories.value = player.metabolism.calories.max;
            }
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (!player || !player.IsConnected)
                return;

            PlayerInfo user = LoadPlayerInfo(player);
            if (user == null)
                return;

            if (HasPermission(player, permTerrain) && user.UnderTerrain)
            {
                NoUnderTerrain(player);
            }

            RestoreSavedInventory(player);
        }

        private object OnPlayerViolation(BasePlayer player, AntiHackType type)
        {
            if (player.IsSleeping() && (type == AntiHackType.InsideTerrain) && HasPermission(player, permTerrain))
                return true;

            if (player.IsFlying && (type == AntiHackType.FlyHack || type == AntiHackType.InsideTerrain || type == AntiHackType.NoClip) && HasPermission(player, permNoClip))
                return true;

            return null;
        }

        private object CanUserLogin(string name, string id, string ip)
        {
            string message = BuildLoginBanMessage(id, ip, id);

            if (!string.IsNullOrWhiteSpace(message))
                return message;

            return null;
        }

        ////////////////////////////////////////////////////////////
        // Commands
        ////////////////////////////////////////////////////////////

        [ChatCommand("disconnectteleport")]
        private void cmdDisconnectTeleport(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permTerrain))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return;

            if (args.Length >= 1)
            {
                switch (args[0].ToLower())
                {
                    case "set":
                        {
                            if (args.Length == 4 && float.TryParse(args[1], out float x) && float.TryParse(args[2], out float y) && float.TryParse(args[3], out float z))
                            {
                                var customPos = new Vector3(x, y, z);

                                if (Vector3.Distance(customPos, Vector3.zero) <= TerrainMeta.Size.x / 1.5f && customPos.y > -100f && customPos.y < 4400f)
                                {
                                    user.Teleport = customPos.ToString();
                                    ReplyPlayerLocalized(player, "PositionAdded", FormatPosition(customPos));
                                }
                                else
                                {
                                    ReplyPlayerLocalized(player, "OutOfBounds");
                                }
                            }
                            else
                            {
                                ReplyPlayerLocalized(player, "DisconnectTeleportSet", "/disconnectteleport", FormatPosition(user.Teleport.ToVector3()));
                            }

                            SavePlayerInfo(player, user);
                            return;
                        }
                    case "reset":
                        {
                            user.Teleport = defaultPos.ToString();
                            if (defaultPos != Vector3.zero)
                                ReplyPlayerLocalized(player, "PositionRemoved2", FormatPosition(defaultPos));
                            else
                                ReplyPlayerLocalized(player, "PositionRemoved1");
                            SavePlayerInfo(player, user);
                            return;
                        }
                }
            }

            string teleportPos = FormatPosition(user.Teleport.ToVector3() == Vector3.zero ? defaultPos : user.Teleport.ToVector3());
            ReplyPlayerLocalized(player, "DisconnectTeleportSet", "/disconnectteleport", teleportPos);
            ReplyPlayerLocalized(player, "DisconnectTeleportReset", "/disconnectteleport");
        }

        [ChatCommand("saveinventory")]
        private void cmdSaveInventory(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permInventory))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return;

            user.SaveInventory = !user.SaveInventory;
            ReplyPlayerLocalized(player, user.SaveInventory ? "SavingInventory" : "NotSavingInventory");
            SavePlayerInfo(player, user);
        }

        [ChatCommand("noclip")]
        private void cmdNoClip(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permNoClip))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            ToggleNoClip(player);
        }

        [ChatCommand("nocliptoggle")]
        private void cmdNoClipAlias(BasePlayer player, string command, string[] args)
        {
            cmdNoClip(player, command, args);
        }

        [ConsoleCommand("nocliptoggle")]
        private void cmdNoClipConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "nocliptoggle");
                return;
            }

            if (!HasPermission(player, permNoClip))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            ToggleNoClip(player, true);
        }

        [ChatCommand("god")]
        private void cmdGodMode(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permGodMode))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            ToggleGodMode(player);
        }

        [ChatCommand("godmode")]
        private void cmdGodModeAlias(BasePlayer player, string command, string[] args)
        {
            cmdGodMode(player, command, args);
        }

        [ConsoleCommand("godmode")]
        private void cmdGodModeConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "godmode");
                return;
            }

            if (!HasPermission(player, permGodMode))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            ToggleGodMode(player, true);
        }

        [ChatCommand("kick")]
        private void cmdKick(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permKick))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            if (args.Length < 1)
            {
                ReplyPlayerLocalized(player, "KickUsage", "/kick");
                return;
            }

            var target = FindOnlinePlayer(args[0]);
            if (target == null)
            {
                ReplyPlayerLocalized(player, "PlayerNotFound");
                return;
            }

            if (permission.UserHasPermission(target.UserIDString, permPreventKick))
            {
                ReplyPlayerLocalized(player, "TargetProtectedFromKick", target.displayName);
                return;
            }

            string reason = string.Join(" ", args.Skip(1));
            if (string.IsNullOrWhiteSpace(reason))
                reason = defaultKickReason;

            target.IPlayer.Kick(reason);
            NotifyKickEvent(target.displayName, target.UserIDString, reason, true);
            ReplyPlayerLocalized(player, "KickSuccess", target.displayName);
        }

        [ChatCommand("ban")]
        private void cmdBan(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permBan))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            if (!TryParseModerationArgs(args, out var targetInput, out var duration, out var reason))
            {
                ReplyPlayerLocalized(player, "BanUsage", "/ban");
                return;
            }

            if (IsIpAddress(targetInput))
            {
                AddIpBan(targetInput, targetInput, reason, duration, player.UserIDString);
                KickAllPlayersByIp(targetInput);
                NotifyBanEvent(targetInput, null, targetInput, duration, reason, false, false, 0, true);

                if (duration.HasValue)
                    ReplyPlayerLocalized(player, "TempIpOnlyBanSuccess", targetInput, FormatDuration(duration.Value));
                else
                    ReplyPlayerLocalized(player, "IpOnlyBanSuccess", targetInput);

                return;
            }

            if (!TryResolveSteamId(targetInput, out var steamId, out var displayName, out var ip, out var onlinePlayer))
            {
                ReplyPlayerLocalized(player, "PlayerNotFound");
                return;
            }

            if (permission.UserHasPermission(steamId, permPreventBan))
            {
                ReplyPlayerLocalized(player, "TargetProtectedFromBan", displayName ?? steamId);
                return;
            }

            AddOrUpdatePlayerBan(steamId, displayName, reason, duration, player.UserIDString);
            SetNativeBan(steamId, displayName ?? steamId, reason, duration);

            onlinePlayer?.IPlayer?.Kick(BuildLoginBanMessage(steamId, ip, onlinePlayer?.UserIDString));
            NotifyBanEvent(displayName ?? steamId, steamId, ip, duration, reason, false, false, 0, true);

            if (duration.HasValue)
                ReplyPlayerLocalized(player, "TempBanSuccess", displayName ?? steamId, FormatDuration(duration.Value));
            else
                ReplyPlayerLocalized(player, "BanSuccess", displayName ?? steamId);
        }

        [ChatCommand("banip")]
        private void cmdBanIp(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permBanIp))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            if (!TryParseModerationArgs(args, out var targetInput, out var duration, out var reason))
            {
                ReplyPlayerLocalized(player, "BanIpUsage", "/banip");
                return;
            }

            if (IsIpAddress(targetInput))
            {
                AddIpBan(targetInput, targetInput, reason, duration, player.UserIDString);
                KickAllPlayersByIp(targetInput);

                int bannedAccounts = BanKnownAccountsOnIp(targetInput, duration, reason, player.UserIDString);
                NotifyBanEvent(targetInput, null, targetInput, duration, reason, true, true, bannedAccounts, true);

                if (duration.HasValue)
                    ReplyPlayerLocalized(player, "TempIpAndAccountsBanSuccess", targetInput, FormatDuration(duration.Value), bannedAccounts);
                else
                    ReplyPlayerLocalized(player, "IpAndAccountsBanSuccess", targetInput, bannedAccounts);

                return;
            }

            if (!TryResolveSteamId(targetInput, out var steamId, out var displayName, out var ip, out var onlinePlayer))
            {
                ReplyPlayerLocalized(player, "PlayerNotFound");
                return;
            }

            if (permission.UserHasPermission(steamId, permPreventBanIp))
            {
                ReplyPlayerLocalized(player, "TargetProtectedFromBanIp", displayName ?? steamId);
                return;
            }

            AddOrUpdatePlayerBan(steamId, displayName, reason, duration, player.UserIDString);
            SetNativeBan(steamId, displayName ?? steamId, reason, duration);

            if (IsValidStoredIp(ip))
            {
                AddIpBan(ip, displayName ?? ip, reason, duration, player.UserIDString);
                KickAllPlayersByIp(ip);
            }
            else if (onlinePlayer != null)
            {
                onlinePlayer.IPlayer.Kick(BuildLoginBanMessage(steamId, null, onlinePlayer.UserIDString));
            }

            NotifyBanEvent(displayName ?? steamId, steamId, ip, duration, reason, true, false, 0, true);

            string shownIp = IsValidStoredIp(ip) ? ip : "unknown";

            if (duration.HasValue)
                ReplyPlayerLocalized(player, "TempBanIpSuccess", displayName ?? steamId, shownIp, FormatDuration(duration.Value));
            else
                ReplyPlayerLocalized(player, "BanIpSuccess", displayName ?? steamId, shownIp);
        }

        [ChatCommand("unban")]
        private void cmdUnban(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permUnban))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            if (args.Length != 1)
            {
                ReplyPlayerLocalized(player, "UnbanUsage", "/unban");
                return;
            }

            if (!TryResolveUnbanTarget(args[0], out var steamId, out var ip))
            {
                ReplyPlayerLocalized(player, "NotBanned");
                return;
            }

            if (!string.IsNullOrWhiteSpace(ip))
            {
                bool removedIpBan = moderationData.IpBans.Remove(ip);
                if (!removedIpBan)
                {
                    ReplyPlayerLocalized(player, "NotBanned");
                    return;
                }

                SaveModerationData();
                NotifyUnbanEvent(ip, true, true);
                ReplyPlayerLocalized(player, "UnbanIpSuccess", ip);
                return;
            }

            if (!string.IsNullOrWhiteSpace(steamId))
            {
                moderationData.PlayerBans.TryGetValue(steamId, out var banRecordBeforeRemove);

                string displayName =
                    banRecordBeforeRemove != null &&
                    !string.IsNullOrWhiteSpace(banRecordBeforeRemove.DisplayName) &&
                    !string.Equals(banRecordBeforeRemove.DisplayName, steamId, StringComparison.OrdinalIgnoreCase)
                        ? banRecordBeforeRemove.DisplayName
                        : GetKnownDisplayName(steamId);

                bool removedPlayerBan = moderationData.PlayerBans.Remove(steamId);
                bool removedIpBan = false;
                string linkedIp = null;

                if (moderationData.LastKnownIps.TryGetValue(steamId, out linkedIp) && IsValidStoredIp(linkedIp))
                    removedIpBan = moderationData.IpBans.Remove(linkedIp);
                else
                    linkedIp = null;

                if (!removedPlayerBan && !removedIpBan)
                {
                    ReplyPlayerLocalized(player, "NotBanned");
                    return;
                }

                if (removedPlayerBan)
                    RemoveNativeBan(steamId);

                SaveModerationData();

                if (removedPlayerBan && removedIpBan)
                    NotifyUnbanPlayerAndIpEvent(displayName, steamId, linkedIp, true);
                else if (removedPlayerBan)
                    NotifyUnbanEvent(displayName, false, true, steamId);
                else if (removedIpBan)
                    NotifyUnbanEvent(linkedIp, true, true);

                if (removedPlayerBan && removedIpBan)
                    ReplyPlayerLocalized(player, "UnbanPlayerAndIpSuccess", displayName, linkedIp);
                else if (removedPlayerBan)
                    ReplyPlayerLocalized(player, "UnbanPlayerSuccess", displayName);
                else if (removedIpBan)
                    ReplyPlayerLocalized(player, "UnbanIpSuccess", linkedIp);
            }
        }

        [ChatCommand("banlist")]
        private void cmdBanList(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permBanList))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            string mode = "all";
            int page = 1;

            if (args.Length >= 1)
                mode = args[0].ToLower();

            if (args.Length >= 2 && !int.TryParse(args[1], out page))
            {
                ReplyPlayerLocalized(player, "BanListUsage", "/banlist");
                return;
            }

            if (page < 1)
                page = 1;

            var entries = GetBanListEntries(mode).ToList();
            if (entries.Count == 0)
            {
                ReplyPlayerLocalized(player, "BanListEmpty");
                return;
            }

            int totalPages = (int)Math.Ceiling(entries.Count / (double)banListPageSize);
            page = Math.Min(page, totalPages);

            ReplyPlayerLocalized(player, "BanListHeader", mode, page, totalPages);

            int start = (page - 1) * banListPageSize;
            var pageEntries = entries.Skip(start).Take(banListPageSize).ToList();

            for (int i = 0; i < pageEntries.Count; i++)
            {
                var entry = pageEntries[i];
                var ban = entry.Value;
                int index = start + i + 1;

                if (ban.ExpiresAt <= 0)
                {
                    ReplyPlayerLocalized(player, "BanListEntryPermanent", index,
                        ban.DisplayName,
                        ban.Target,
                        ban.Reason);
                }
                else
                {
                    var remaining = TimeSpan.FromSeconds(Math.Max(0, ban.ExpiresAt - UnixNow()));
                    ReplyPlayerLocalized(player, "BanListEntryTemporary", index,
                        ban.DisplayName,
                        ban.Target,
                        FormatDuration(remaining),
                        ban.Reason);
                }
            }
        }

        [ChatCommand("spawn")]
        private void cmdSpawn(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permSpawn))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            if (args == null || args.Length < 1)
            {
                ReplyPlayerLocalized(player, "UsageSpawn", "spawn");
                return;
            }

            string entityInput = args[0];
            string resolved = ResolveEntity(entityInput, player, (k, a) => ReplyPlayerLocalized(player, k, a));
            if (string.IsNullOrEmpty(resolved))
                return;

            bool spawned = SpawnForPlayer(player, resolved);
            ReplyPlayerLocalized(player, spawned ? "SpawnSuccess" : "SpawnFail", entityInput);

            if (spawned)
                LogSpawnAction(player.displayName, player.displayName, entityInput);
        }

        [ConsoleCommand("spawn")]
        private void cmdSpawnConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] args = arg.Args ?? Array.Empty<string>();

            if (player != null)
                args = NormalizePlayerConsoleArgs(args);

            if (player == null)
            {
                if (args.Length < 1)
                {
                    ReplyConsoleLocalized(arg, "UsageSpawn", "spawn");
                    return;
                }

                if (args.Length == 1)
                {
                    ReplyConsoleLocalized(arg, "PlayersOnly", "spawn");
                    return;
                }

                ReplyConsoleLocalized(arg, "UsageSpawnTo", "spawnto");
                return;
            }

            if (!HasPermission(player, permSpawn))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            if (args.Length < 1)
            {
                ReplyPlayerConsoleLocalized(player, "UsageSpawn", "spawn");
                return;
            }

            string entityArg = args[0];
            string resolvedSelf = ResolveEntity(entityArg, player, (k, a) => ReplyPlayerConsoleLocalized(player, k, a));
            if (string.IsNullOrEmpty(resolvedSelf))
                return;

            bool spawnedSelf = SpawnForPlayer(player, resolvedSelf);
            ReplyPlayerConsoleLocalized(player, spawnedSelf ? "SpawnSuccess" : "SpawnFail", entityArg);

            if (spawnedSelf)
                LogSpawnAction(player.displayName, player.displayName, entityArg);
        }

        [ChatCommand("spawnto")]
        private void cmdSpawnTo(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permSpawnTo))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            if (args == null || args.Length < 2)
            {
                ReplyPlayerLocalized(player, "UsageSpawnTo", "spawnto");
                return;
            }

            string targetInput = args[0];
            string entityInput = string.Join(" ", args.Skip(1));

            BasePlayer target = FindPlayerExtended(targetInput);
            if (target == null)
            {
                ReplyPlayerLocalized(player, "NoPlayersFound", targetInput);
                return;
            }

            string resolved = ResolveEntity(entityInput, player, (k, a) => ReplyPlayerLocalized(player, k, a));
            if (string.IsNullOrEmpty(resolved))
                return;

            bool spawned = SpawnForPlayer(target, resolved);
            ReplyPlayerLocalized(player, spawned ? "SpawnToSuccess" : "SpawnToFail", entityInput, target.displayName);

            if (spawned)
                LogSpawnAction(player.displayName, target.displayName, entityInput);
        }

        [ConsoleCommand("spawnto")]
        private void cmdSpawnToConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] args = arg.Args ?? Array.Empty<string>();

            if (player != null)
                args = NormalizePlayerConsoleArgs(args);

            if (args.Length < 2)
            {
                if (player != null)
                    ReplyPlayerConsoleLocalized(player, "UsageSpawnTo", "spawnto");
                else
                    ReplyConsoleLocalized(arg, "UsageSpawnTo", "spawnto");
                return;
            }

            string targetInput = args[0];
            string entityInput = string.Join(" ", args.Skip(1));

            BasePlayer target = FindPlayerExtended(targetInput);
            if (target == null)
            {
                if (player != null)
                    ReplyPlayerConsoleLocalized(player, "NoPlayersFound", targetInput);
                else
                    ReplyConsoleLocalized(arg, "NoPlayersFound", targetInput);
                return;
            }

            if (player != null && !HasPermission(player, permSpawnTo))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            string resolved = ResolveEntity(entityInput, player, (k, a) =>
            {
                if (player != null)
                    ReplyPlayerConsoleLocalized(player, k, a);
                else
                    ReplyConsoleLocalized(arg, k, a);
            });

            if (string.IsNullOrEmpty(resolved))
                return;

            bool spawned = SpawnForPlayer(target, resolved);

            if (player != null)
            {
                ReplyPlayerConsoleLocalized(player, spawned ? "SpawnToSuccess" : "SpawnToFail", entityInput, target.displayName);
                if (spawned)
                    LogSpawnAction(player.displayName, target.displayName, entityInput);
            }
            else
            {
                ReplyConsoleLocalized(arg, spawned ? "SpawnToSuccess" : "SpawnToFail", entityInput, target.displayName);
            }
        }

        [ChatCommand("spawnall")]
        private void cmdSpawnAll(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permSpawnAll))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            if (args == null || args.Length < 1)
            {
                ReplyPlayerLocalized(player, "UsageSpawnAll", "spawnall");
                return;
            }

            if (!BasePlayer.activePlayerList.Any())
            {
                ReplyPlayerLocalized(player, "NoPlayersConnected");
                return;
            }

            string resolved = ResolveEntity(args[0], player, (k, a) => ReplyPlayerLocalized(player, k, a));
            if (string.IsNullOrEmpty(resolved))
                return;

            int spawnedCount = 0;
            int connectedCount = 0;

            foreach (BasePlayer target in BasePlayer.activePlayerList.ToArray())
            {
                connectedCount++;
                if (SpawnForPlayer(target, resolved))
                    spawnedCount++;
            }

            ReplyPlayerLocalized(player, spawnedCount > 0 ? "SpawnAllSuccess" : "SpawnAllFail", args[0], connectedCount);

            if (spawnedCount > 0)
                LogSpawnAction(player.displayName, $"{connectedCount} player(s)", args[0]);
        }

        [ConsoleCommand("spawnall")]
        private void cmdSpawnAllConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] args = arg.Args ?? Array.Empty<string>();

            if (player != null)
                args = NormalizePlayerConsoleArgs(args);

            if (player == null)
            {
                if (args.Length < 1)
                {
                    ReplyConsoleLocalized(arg, "UsageSpawnAll", "spawnall");
                    return;
                }

                if (!BasePlayer.activePlayerList.Any())
                {
                    ReplyConsoleLocalized(arg, "NoPlayersConnected");
                    return;
                }

                string resolved = ResolveEntity(args[0], null, (k, a) => ReplyConsoleLocalized(arg, k, a));
                if (string.IsNullOrEmpty(resolved))
                    return;

                int spawnedCount = 0;
                int connectedCount = 0;

                foreach (BasePlayer target in BasePlayer.activePlayerList.ToArray())
                {
                    connectedCount++;
                    if (SpawnForPlayer(target, resolved))
                        spawnedCount++;
                }

                ReplyConsoleLocalized(arg, spawnedCount > 0 ? "SpawnAllSuccess" : "SpawnAllFail", args[0], connectedCount);

                return;
            }

            if (!HasPermission(player, permSpawnAll))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            if (args.Length < 1)
            {
                ReplyPlayerConsoleLocalized(player, "UsageSpawnAll", "spawnall");
                return;
            }

            if (!BasePlayer.activePlayerList.Any())
            {
                ReplyPlayerConsoleLocalized(player, "NoPlayersConnected");
                return;
            }

            string resolvedSelf = ResolveEntity(args[0], player, (k, a) => ReplyPlayerConsoleLocalized(player, k, a));
            if (string.IsNullOrEmpty(resolvedSelf))
                return;

            int spawnedSelfCount = 0;
            int connectedSelfCount = 0;

            foreach (BasePlayer target in BasePlayer.activePlayerList.ToArray())
            {
                connectedSelfCount++;
                if (SpawnForPlayer(target, resolvedSelf))
                    spawnedSelfCount++;
            }

            ReplyPlayerConsoleLocalized(player, spawnedSelfCount > 0 ? "SpawnAllSuccess" : "SpawnAllFail", args[0], connectedSelfCount);

            if (spawnedSelfCount > 0)
                LogSpawnAction(player.displayName, $"{connectedSelfCount} player(s)", args[0]);
        }

        [ChatCommand("despawn")]
        private void cmdDespawn(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permDespawn))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            BaseEntity entity = GetLookEntity(player, maxDespawnDistance);
            if (entity == null)
            {
                ReplyPlayerLocalized(player, "NoEntityLookedAt");
                return;
            }

            if (entity is BasePlayer)
            {
                ReplyPlayerLocalized(player, "CannotDespawnPlayer");
                return;
            }

            string entityName = !string.IsNullOrEmpty(entity.ShortPrefabName) ? entity.ShortPrefabName : entity.GetType().Name;
            entity.Kill();

            if (logSpawnToConsole)
                Puts($"{player.displayName} despawned '{entityName}'");

            ReplyPlayerLocalized(player, "DespawnSuccess", entityName);
        }

        [ConsoleCommand("despawn")]
        private void cmdDespawnConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "despawn");
                return;
            }

            if (!HasPermission(player, permDespawn))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            BaseEntity entity = GetLookEntity(player, maxDespawnDistance);
            if (entity == null)
            {
                ReplyPlayerConsoleLocalized(player, "NoEntityLookedAt");
                return;
            }

            if (entity is BasePlayer)
            {
                ReplyPlayerConsoleLocalized(player, "CannotDespawnPlayer");
                return;
            }

            string entityName = !string.IsNullOrEmpty(entity.ShortPrefabName) ? entity.ShortPrefabName : entity.GetType().Name;
            entity.Kill();

            if (logSpawnToConsole)
                Puts($"{player.displayName} despawned '{entityName}'");

            ReplyPlayerConsoleLocalized(player, "DespawnSuccess", entityName);
        }

        [ChatCommand("give")]
        private void cmdGive(BasePlayer player, string command, string[] args)
        {
            if (args == null || args.Length < 1)
            {
                if (!HasPermission(player, permGive) && !HasPermission(player, permGiveTo))
                {
                    ReplyPlayerLocalized(player, "NoPermission");
                    return;
                }

                ReplyPlayerLocalized(player, "UsageGiveCombined", "give");
                return;
            }

            if (args.Length == 2 && !int.TryParse(args[1], out _))
            {
                if (!HasPermission(player, permGiveTo))
                {
                    ReplyPlayerLocalized(player, "NoPermission");
                    return;
                }

                string targetInput = args[0];
                string itemInput = args[1];

                BasePlayer target = FindPlayerExtended(targetInput);
                if (target == null)
                {
                    ReplyPlayerLocalized(player, "NoPlayersFound", targetInput);
                    return;
                }

                ItemDefinition def = FindItemDef(itemInput, player);
                if (def == null)
                {
                    if (IsBlacklistedItemInput(itemInput, null, player))
                        ReplyPlayerLocalized(player, "ItemBlacklisted", itemInput);
                    else
                        ReplyPlayerLocalized(player, "ItemNotFound", itemInput);
                    return;
                }

                bool given = GiveItemToPlayer(target, def, 1);
                ReplyPlayerLocalized(player, given ? "GiveToSuccess" : "GiveToFail", def.shortname, 1, target.displayName);

                if (given)
                    LogGiveAction(player.displayName, target.displayName, def.shortname, 1);

                return;
            }

            if (args.Length <= 2)
            {
                if (!HasPermission(player, permGive))
                {
                    ReplyPlayerLocalized(player, "NoPermission");
                    return;
                }

                string itemInput = args[0];
                int amount = 1;

                if (args.Length == 2 && !int.TryParse(args[1], out amount))
                {
                    ReplyPlayerLocalized(player, "InvalidAmount");
                    return;
                }

                ItemDefinition def = FindItemDef(itemInput, player);
                if (def == null)
                {
                    if (IsBlacklistedItemInput(itemInput, null, player))
                        ReplyPlayerLocalized(player, "ItemBlacklisted", itemInput);
                    else
                        ReplyPlayerLocalized(player, "ItemNotFound", itemInput);
                    return;
                }

                bool givenSelf = GiveItemToPlayer(player, def, Math.Max(1, amount));
                ReplyPlayerLocalized(player, givenSelf ? "GiveSuccess" : "GiveFail", def.shortname, Math.Max(1, amount));

                if (givenSelf)
                    LogGiveAction(player.displayName, player.displayName, def.shortname, Math.Max(1, amount));

                return;
            }

            if (!HasPermission(player, permGiveTo))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            int targetAmount = 1;
            string targetItemInput;
            string targetPlayerInput;

            if (int.TryParse(args[args.Length - 1], out targetAmount))
            {
                targetItemInput = args[args.Length - 2];
                targetPlayerInput = string.Join(" ", args.Take(args.Length - 2));
            }
            else
            {
                targetItemInput = args[args.Length - 1];
                targetPlayerInput = string.Join(" ", args.Take(args.Length - 1));
                targetAmount = 1;
            }

            BasePlayer targetPlayer = FindPlayerExtended(targetPlayerInput);
            if (targetPlayer == null)
            {
                ReplyPlayerLocalized(player, "NoPlayersFound", targetPlayerInput);
                return;
            }

            ItemDefinition targetDef = FindItemDef(targetItemInput, player);
            if (targetDef == null)
            {
                if (IsBlacklistedItemInput(targetItemInput, null, player))
                    ReplyPlayerLocalized(player, "ItemBlacklisted", targetItemInput);
                else
                    ReplyPlayerLocalized(player, "ItemNotFound", targetItemInput);
                return;
            }

            bool givenTarget = GiveItemToPlayer(targetPlayer, targetDef, Math.Max(1, targetAmount));
            ReplyPlayerLocalized(player, givenTarget ? "GiveToSuccess" : "GiveToFail", targetDef.shortname, Math.Max(1, targetAmount), targetPlayer.displayName);

            if (givenTarget)
                LogGiveAction(player.displayName, targetPlayer.displayName, targetDef.shortname, Math.Max(1, targetAmount));
        }

        [ConsoleCommand("give")]
        private void cmdGiveConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] args = arg.Args ?? Array.Empty<string>();

            if (player == null)
            {
                if (args.Length < 1)
                {
                    ReplyConsoleLocalized(arg, "UsageGiveCombined", "give");
                    return;
                }

                if (args.Length == 1)
                {
                    ReplyConsoleLocalized(arg, "PlayersOnly", "give");
                    return;
                }

                if (args.Length == 2 && !int.TryParse(args[1], out _))
                {
                    string targetInput = args[0];
                    string itemInput = args[1];

                    BasePlayer target = FindPlayerExtended(targetInput);
                    if (target == null)
                    {
                        ReplyConsoleLocalized(arg, "NoPlayersFound", targetInput);
                        return;
                    }

                    ItemDefinition def = FindItemDef(itemInput, target);
                    if (def == null)
                    {
                        if (IsBlacklistedItemInput(itemInput, null, target))
                            ReplyConsoleLocalized(arg, "ItemBlacklisted", itemInput);
                        else
                            ReplyConsoleLocalized(arg, "ItemNotFound", itemInput);
                        return;
                    }

                    bool given = GiveItemToPlayer(target, def, 1);
                    ReplyConsoleLocalized(arg, given ? "GiveToSuccess" : "GiveToFail", def.shortname, 1, target.displayName);

                    return;
                }

                if (args.Length == 2 && int.TryParse(args[1], out _))
                {
                    ReplyConsoleLocalized(arg, "PlayersOnly", "give");
                    return;
                }

                int amount = 1;
                string itemArg;
                string targetArg;

                if (int.TryParse(args[args.Length - 1], out amount))
                {
                    itemArg = args[args.Length - 2];
                    targetArg = string.Join(" ", args.Take(args.Length - 2));
                }
                else
                {
                    itemArg = args[args.Length - 1];
                    targetArg = string.Join(" ", args.Take(args.Length - 1));
                    amount = 1;
                }

                BasePlayer targetPlayer = FindPlayerExtended(targetArg);
                if (targetPlayer == null)
                {
                    ReplyConsoleLocalized(arg, "NoPlayersFound", targetArg);
                    return;
                }

                ItemDefinition defTarget = FindItemDef(itemArg, targetPlayer);
                if (defTarget == null)
                {
                    if (IsBlacklistedItemInput(itemArg, null, targetPlayer))
                        ReplyConsoleLocalized(arg, "ItemBlacklisted", itemArg);
                    else
                        ReplyConsoleLocalized(arg, "ItemNotFound", itemArg);
                    return;
                }

                bool givenTarget = GiveItemToPlayer(targetPlayer, defTarget, Math.Max(1, amount));
                ReplyConsoleLocalized(arg, givenTarget ? "GiveToSuccess" : "GiveToFail", defTarget.shortname, Math.Max(1, amount), targetPlayer.displayName);

                return;
            }

            if (args.Length < 1)
            {
                if (!HasPermission(player, permGive) && !HasPermission(player, permGiveTo))
                {
                    ReplyPlayerConsoleLocalized(player, "NoPermission");
                    return;
                }

                ReplyPlayerConsoleLocalized(player, "UsageGiveCombined", "give");
                return;
            }

            if (args.Length == 2 && !int.TryParse(args[1], out _))
            {
                if (!HasPermission(player, permGiveTo))
                {
                    ReplyPlayerConsoleLocalized(player, "NoPermission");
                    return;
                }

                string targetInput = args[0];
                string itemInput = args[1];

                BasePlayer target = FindPlayerExtended(targetInput);
                if (target == null)
                {
                    ReplyPlayerConsoleLocalized(player, "NoPlayersFound", targetInput);
                    return;
                }

                ItemDefinition def = FindItemDef(itemInput, player);
                if (def == null)
                {
                    if (IsBlacklistedItemInput(itemInput, null, player))
                        ReplyPlayerConsoleLocalized(player, "ItemBlacklisted", itemInput);
                    else
                        ReplyPlayerConsoleLocalized(player, "ItemNotFound", itemInput);
                    return;
                }

                bool given = GiveItemToPlayer(target, def, 1);
                ReplyPlayerConsoleLocalized(player, given ? "GiveToSuccess" : "GiveToFail", def.shortname, 1, target.displayName);

                if (given)
                    LogGiveAction(player.displayName, target.displayName, def.shortname, 1);

                return;
            }

            if (args.Length <= 2)
            {
                if (!HasPermission(player, permGive))
                {
                    ReplyPlayerConsoleLocalized(player, "NoPermission");
                    return;
                }

                string itemInput = args[0];
                int amount = 1;

                if (args.Length == 2 && !int.TryParse(args[1], out amount))
                {
                    ReplyPlayerConsoleLocalized(player, "InvalidAmount");
                    return;
                }

                ItemDefinition defSelf = FindItemDef(itemInput, player);
                if (defSelf == null)
                {
                    if (IsBlacklistedItemInput(itemInput, null, player))
                        ReplyPlayerConsoleLocalized(player, "ItemBlacklisted", itemInput);
                    else
                        ReplyPlayerConsoleLocalized(player, "ItemNotFound", itemInput);
                    return;
                }

                bool givenSelf = GiveItemToPlayer(player, defSelf, Math.Max(1, amount));
                ReplyPlayerConsoleLocalized(player, givenSelf ? "GiveSuccess" : "GiveFail", defSelf.shortname, Math.Max(1, amount));

                if (givenSelf)
                    LogGiveAction(player.displayName, player.displayName, defSelf.shortname, Math.Max(1, amount));

                return;
            }

            if (!HasPermission(player, permGiveTo))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            int amountTargetPlayer = 1;
            string itemInputTargetPlayer;
            string playerInputTargetPlayer;

            if (int.TryParse(args[args.Length - 1], out amountTargetPlayer))
            {
                itemInputTargetPlayer = args[args.Length - 2];
                playerInputTargetPlayer = string.Join(" ", args.Take(args.Length - 2));
            }
            else
            {
                itemInputTargetPlayer = args[args.Length - 1];
                playerInputTargetPlayer = string.Join(" ", args.Take(args.Length - 1));
                amountTargetPlayer = 1;
            }

            BasePlayer targetP = FindPlayerExtended(playerInputTargetPlayer);
            if (targetP == null)
            {
                ReplyPlayerConsoleLocalized(player, "NoPlayersFound", playerInputTargetPlayer);
                return;
            }

            ItemDefinition defP = FindItemDef(itemInputTargetPlayer, player);
            if (defP == null)
            {
                if (IsBlacklistedItemInput(itemInputTargetPlayer, null, player))
                    ReplyPlayerConsoleLocalized(player, "ItemBlacklisted", itemInputTargetPlayer);
                else
                    ReplyPlayerConsoleLocalized(player, "ItemNotFound", itemInputTargetPlayer);
                return;
            }

            bool givenP = GiveItemToPlayer(targetP, defP, Math.Max(1, amountTargetPlayer));
            ReplyPlayerConsoleLocalized(player, givenP ? "GiveToSuccess" : "GiveToFail", defP.shortname, Math.Max(1, amountTargetPlayer), targetP.displayName);

            if (givenP)
                LogGiveAction(player.displayName, targetP.displayName, defP.shortname, Math.Max(1, amountTargetPlayer));
        }

        [ChatCommand("giveto")]
        private void cmdGiveTo(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permGiveTo))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            if (args == null || args.Length < 2)
            {
                ReplyPlayerLocalized(player, "UsageGiveTo", "giveto");
                return;
            }

            int amount = 1;
            string itemInput;
            string targetInput;

            if (int.TryParse(args[args.Length - 1], out amount))
            {
                itemInput = args[args.Length - 2];
                targetInput = string.Join(" ", args.Take(args.Length - 2));
            }
            else
            {
                itemInput = args[args.Length - 1];
                targetInput = string.Join(" ", args.Take(args.Length - 1));
                amount = 1;
            }

            BasePlayer target = FindPlayerExtended(targetInput);
            if (target == null)
            {
                ReplyPlayerLocalized(player, "NoPlayersFound", targetInput);
                return;
            }

            ItemDefinition def = FindItemDef(itemInput, player);
            if (def == null)
            {
                if (IsBlacklistedItemInput(itemInput, null, player))
                    ReplyPlayerLocalized(player, "ItemBlacklisted", itemInput);
                else
                    ReplyPlayerLocalized(player, "ItemNotFound", itemInput);
                return;
            }

            bool given = GiveItemToPlayer(target, def, Math.Max(1, amount));
            ReplyPlayerLocalized(player, given ? "GiveToSuccess" : "GiveToFail", def.shortname, Math.Max(1, amount), target.displayName);

            if (given)
                LogGiveAction(player.displayName, target.displayName, def.shortname, Math.Max(1, amount));
        }

        [ConsoleCommand("giveto")]
        private void cmdGiveToConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] args = arg.Args ?? Array.Empty<string>();

            if (player == null)
            {
                if (args.Length < 2)
                {
                    ReplyConsoleLocalized(arg, "UsageGiveTo", "giveto");
                    return;
                }

                int amount = 1;
                string itemInput;
                string targetInput;

                if (int.TryParse(args[args.Length - 1], out amount))
                {
                    itemInput = args[args.Length - 2];
                    targetInput = string.Join(" ", args.Take(args.Length - 2));
                }
                else
                {
                    itemInput = args[args.Length - 1];
                    targetInput = string.Join(" ", args.Take(args.Length - 1));
                    amount = 1;
                }

                BasePlayer target = FindPlayerExtended(targetInput);
                if (target == null)
                {
                    ReplyConsoleLocalized(arg, "NoPlayersFound", targetInput);
                    return;
                }

                ItemDefinition def = FindItemDef(itemInput, target);
                if (def == null)
                {
                    if (IsBlacklistedItemInput(itemInput, null, target))
                        ReplyConsoleLocalized(arg, "ItemBlacklisted", itemInput);
                    else
                        ReplyConsoleLocalized(arg, "ItemNotFound", itemInput);
                    return;
                }

                bool given = GiveItemToPlayer(target, def, Math.Max(1, amount));
                ReplyConsoleLocalized(arg, given ? "GiveToSuccess" : "GiveToFail", def.shortname, Math.Max(1, amount), target.displayName);

                return;
            }

            if (!HasPermission(player, permGiveTo))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            if (args.Length < 2)
            {
                ReplyPlayerConsoleLocalized(player, "UsageGiveTo", "giveto");
                return;
            }

            int amountTarget = 1;
            string itemInputTarget;
            string targetInputTarget;

            if (int.TryParse(args[args.Length - 1], out amountTarget))
            {
                itemInputTarget = args[args.Length - 2];
                targetInputTarget = string.Join(" ", args.Take(args.Length - 2));
            }
            else
            {
                itemInputTarget = args[args.Length - 1];
                targetInputTarget = string.Join(" ", args.Take(args.Length - 1));
                amountTarget = 1;
            }

            BasePlayer targetPlayer = FindPlayerExtended(targetInputTarget);
            if (targetPlayer == null)
            {
                ReplyPlayerConsoleLocalized(player, "NoPlayersFound", targetInputTarget);
                return;
            }

            ItemDefinition defTarget = FindItemDef(itemInputTarget, player);
            if (defTarget == null)
            {
                if (IsBlacklistedItemInput(itemInputTarget, null, player))
                    ReplyPlayerConsoleLocalized(player, "ItemBlacklisted", itemInputTarget);
                else
                    ReplyPlayerConsoleLocalized(player, "ItemNotFound", itemInputTarget);
                return;
            }

            bool givenTarget = GiveItemToPlayer(targetPlayer, defTarget, Math.Max(1, amountTarget));
            ReplyPlayerConsoleLocalized(player, givenTarget ? "GiveToSuccess" : "GiveToFail", defTarget.shortname, Math.Max(1, amountTarget), targetPlayer.displayName);

            if (givenTarget)
                LogGiveAction(player.displayName, targetPlayer.displayName, defTarget.shortname, Math.Max(1, amountTarget));
        }

        [ChatCommand("giveall")]
        private void cmdGiveAll(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player, permGiveAll))
            {
                ReplyPlayerLocalized(player, "NoPermission");
                return;
            }

            if (args == null || args.Length < 1)
            {
                ReplyPlayerLocalized(player, "UsageGiveAll", "giveall");
                return;
            }

            string itemInput = args[0];
            int amount = 1;

            if (args.Length > 1 && !int.TryParse(args[1], out amount))
            {
                ReplyPlayerLocalized(player, "InvalidAmount");
                return;
            }

            ItemDefinition def = FindItemDef(itemInput, player);
            if (def == null)
            {
                if (IsBlacklistedItemInput(itemInput, null, player))
                    ReplyPlayerLocalized(player, "ItemBlacklisted", itemInput);
                else
                    ReplyPlayerLocalized(player, "ItemNotFound", itemInput);
                return;
            }

            int givenCount = 0;
            foreach (BasePlayer target in BasePlayer.activePlayerList.ToArray())
            {
                if (GiveItemToPlayer(target, def, Math.Max(1, amount)))
                    givenCount++;
            }

            ReplyPlayerLocalized(player, givenCount > 0 ? "GiveAllSuccess" : "GiveAllFail", def.shortname, Math.Max(1, amount), givenCount);

            if (givenCount > 0)
                LogGiveAction(player.displayName, $"{givenCount} player(s)", def.shortname, Math.Max(1, amount));
        }

        [ConsoleCommand("giveall")]
        private void cmdGiveAllConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] args = arg.Args ?? Array.Empty<string>();

            if (player == null)
            {
                if (args.Length < 1)
                {
                    ReplyConsoleLocalized(arg, "UsageGiveAll", "giveall");
                    return;
                }

                string itemInput = args[0];
                int amount = 1;

                if (args.Length > 1 && !int.TryParse(args[1], out amount))
                {
                    ReplyConsoleLocalized(arg, "InvalidAmount");
                    return;
                }

                ItemDefinition def = FindItemDef(itemInput, null);
                if (def == null)
                {
                    if (IsBlacklistedItemInput(itemInput, null, null))
                        ReplyConsoleLocalized(arg, "ItemBlacklisted", itemInput);
                    else
                        ReplyConsoleLocalized(arg, "ItemNotFound", itemInput);
                    return;
                }

                int givenCount = 0;
                foreach (BasePlayer target in BasePlayer.activePlayerList.ToArray())
                {
                    if (GiveItemToPlayer(target, def, Math.Max(1, amount)))
                        givenCount++;
                }

                ReplyConsoleLocalized(arg, givenCount > 0 ? "GiveAllSuccess" : "GiveAllFail", def.shortname, Math.Max(1, amount), givenCount);

                return;
            }

            if (!HasPermission(player, permGiveAll))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            if (args.Length < 1)
            {
                ReplyPlayerConsoleLocalized(player, "UsageGiveAll", "giveall");
                return;
            }

            string itemArg = args[0];
            int giveAllAmount = 1;

            if (args.Length > 1 && !int.TryParse(args[1], out giveAllAmount))
            {
                ReplyPlayerConsoleLocalized(player, "InvalidAmount");
                return;
            }

            ItemDefinition giveAllDef = FindItemDef(itemArg, player);
            if (giveAllDef == null)
            {
                if (IsBlacklistedItemInput(itemArg, null, player))
                    ReplyPlayerConsoleLocalized(player, "ItemBlacklisted", itemArg);
                else
                    ReplyPlayerConsoleLocalized(player, "ItemNotFound", itemArg);
                return;
            }

            int giveAllCount = 0;
            foreach (BasePlayer target in BasePlayer.activePlayerList.ToArray())
            {
                if (GiveItemToPlayer(target, giveAllDef, Math.Max(1, giveAllAmount)))
                    giveAllCount++;
            }

            ReplyPlayerConsoleLocalized(player, giveAllCount > 0 ? "GiveAllSuccess" : "GiveAllFail", giveAllDef.shortname, Math.Max(1, giveAllAmount), giveAllCount);

            if (giveAllCount > 0)
                LogGiveAction(player.displayName, $"{giveAllCount} player(s)", giveAllDef.shortname, Math.Max(1, giveAllAmount));
        }

        [ConsoleCommand("inventory.give")]
        private void cmdInventoryGive(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "inventory.give");
                return;
            }

            if (!HasPermission(player, permGive))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            string[] args = arg.Args ?? Array.Empty<string>();
            if (args.Length < 1)
            {
                ReplyPlayerConsoleLocalized(player, "UsageGiveSelf", "inventory.give");
                return;
            }

            string itemInput = args[0];
            int amount = 1;

            if (args.Length > 1 && !int.TryParse(args[1], out amount))
            {
                ReplyPlayerConsoleLocalized(player, "InvalidAmount");
                return;
            }

            ItemDefinition def = FindItemDef(itemInput, player);
            if (def == null)
            {
                if (IsBlacklistedItemInput(itemInput, null, player))
                    ReplyPlayerConsoleLocalized(player, "ItemBlacklisted", itemInput);
                else
                    ReplyPlayerConsoleLocalized(player, "ItemNotFound", itemInput);
                return;
            }

            bool given = GiveItemToPlayer(player, def, Math.Max(1, amount));
            ReplyPlayerConsoleLocalized(player, given ? "GiveSuccess" : "GiveFail", def.shortname, Math.Max(1, amount));

            if (given)
                LogGiveAction(player.displayName, player.displayName, def.shortname, Math.Max(1, amount));
        }

        [ConsoleCommand("inventory.giveid")]
        private void cmdInventoryGiveId(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "inventory.giveid");
                return;
            }

            if (!HasPermission(player, permGive))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            string[] args = arg.Args ?? Array.Empty<string>();
            if (args.Length < 1)
            {
                ReplyPlayerConsoleLocalized(player, "UsageGiveSelf", "inventory.giveid");
                return;
            }

            string itemInput = args[0];
            int amount = 1;

            if (args.Length > 1 && !int.TryParse(args[1], out amount))
            {
                ReplyPlayerConsoleLocalized(player, "InvalidAmount");
                return;
            }

            ItemDefinition def = FindItemDef(itemInput, player);
            if (def == null)
            {
                if (IsBlacklistedItemInput(itemInput, null, player))
                    ReplyPlayerConsoleLocalized(player, "ItemBlacklisted", itemInput);
                else
                    ReplyPlayerConsoleLocalized(player, "ItemNotFound", itemInput);
                return;
            }

            bool given = GiveItemToPlayer(player, def, Math.Max(1, amount));
            ReplyPlayerConsoleLocalized(player, given ? "GiveSuccess" : "GiveFail", def.shortname, Math.Max(1, amount));

            if (given)
                LogGiveAction(player.displayName, player.displayName, def.shortname, Math.Max(1, amount));
        }

        [ConsoleCommand("inventory.giveto")]
        private void cmdInventoryGiveTo(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] args = arg.Args ?? Array.Empty<string>();

            if (player == null)
            {
                if (args.Length < 2)
                {
                    ReplyConsoleLocalized(arg, "UsageGiveTo", "inventory.giveto");
                    return;
                }

                int amount = 1;
                string itemInput;
                string targetInput;

                if (args.Length == 2)
                {
                    targetInput = args[0];
                    itemInput = args[1];
                }
                else if (int.TryParse(args[args.Length - 1], out amount))
                {
                    itemInput = args[args.Length - 2];
                    targetInput = string.Join(" ", args.Take(args.Length - 2));
                }
                else
                {
                    itemInput = args[args.Length - 1];
                    targetInput = string.Join(" ", args.Take(args.Length - 1));
                    amount = 1;
                }

                if (string.IsNullOrWhiteSpace(targetInput))
                {
                    ReplyConsoleLocalized(arg, "UsageGiveTo", "inventory.giveto");
                    return;
                }

                BasePlayer target = FindPlayerExtended(targetInput);
                if (target == null)
                {
                    ReplyConsoleLocalized(arg, "NoPlayersFound", targetInput);
                    return;
                }

                ItemDefinition def = FindItemDef(itemInput, target);
                if (def == null)
                {
                    if (IsBlacklistedItemInput(itemInput, null, target))
                        ReplyConsoleLocalized(arg, "ItemBlacklisted", itemInput);
                    else
                        ReplyConsoleLocalized(arg, "ItemNotFound", itemInput);
                    return;
                }

                bool given = GiveItemToPlayer(target, def, Math.Max(1, amount));
                ReplyConsoleLocalized(arg, given ? "GiveToSuccess" : "GiveToFail", def.shortname, Math.Max(1, amount), target.displayName);
                return;
            }

            if (!HasPermission(player, permGiveTo))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            if (args.Length < 2)
            {
                ReplyPlayerConsoleLocalized(player, "UsageGiveTo", "inventory.giveto");
                return;
            }

            int playerAmount = 1;
            string playerItemInput;
            string playerTargetInput;

            if (int.TryParse(args[args.Length - 1], out playerAmount))
            {
                if (args.Length < 3)
                {
                    ReplyPlayerConsoleLocalized(player, "UsageGiveTo", "inventory.giveto");
                    return;
                }

                playerItemInput = args[args.Length - 2];
                playerTargetInput = string.Join(" ", args.Take(args.Length - 2));
            }
            else
            {
                playerItemInput = args[args.Length - 1];
                playerTargetInput = string.Join(" ", args.Take(args.Length - 1));
                playerAmount = 1;
            }

            if (string.IsNullOrWhiteSpace(playerTargetInput))
            {
                ReplyPlayerConsoleLocalized(player, "UsageGiveTo", "inventory.giveto");
                return;
            }

            BasePlayer playerTarget = FindPlayerExtended(playerTargetInput);
            if (playerTarget == null)
            {
                ReplyPlayerConsoleLocalized(player, "NoPlayersFound", playerTargetInput);
                return;
            }

            ItemDefinition playerDef = FindItemDef(playerItemInput, player);
            if (playerDef == null)
            {
                if (IsBlacklistedItemInput(playerItemInput, null, player))
                    ReplyPlayerConsoleLocalized(player, "ItemBlacklisted", playerItemInput);
                else
                    ReplyPlayerConsoleLocalized(player, "ItemNotFound", playerItemInput);
                return;
            }

            bool playerGiven = GiveItemToPlayer(playerTarget, playerDef, Math.Max(1, playerAmount));
            ReplyPlayerConsoleLocalized(player, playerGiven ? "GiveToSuccess" : "GiveToFail", playerDef.shortname, Math.Max(1, playerAmount), playerTarget.displayName);

            if (playerGiven)
                LogGiveAction(player.displayName, playerTarget.displayName, playerDef.shortname, Math.Max(1, playerAmount));
        }

        [ConsoleCommand("inventory.giveall")]
        private void cmdInventoryGiveAll(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] args = arg.Args ?? Array.Empty<string>();

            if (player == null)
            {
                if (args.Length < 1)
                {
                    ReplyConsoleLocalized(arg, "UsageGiveAll", "inventory.giveall");
                    return;
                }

                string itemInput = args[0];
                int amount = 1;

                if (args.Length > 1 && !int.TryParse(args[1], out amount))
                {
                    ReplyConsoleLocalized(arg, "InvalidAmount");
                    return;
                }

                ItemDefinition def = FindItemDef(itemInput, null);
                if (def == null)
                {
                    if (IsBlacklistedItemInput(itemInput, null, null))
                        ReplyConsoleLocalized(arg, "ItemBlacklisted", itemInput);
                    else
                        ReplyConsoleLocalized(arg, "ItemNotFound", itemInput);
                    return;
                }

                int givenCount = 0;
                foreach (BasePlayer target in BasePlayer.activePlayerList.ToArray())
                {
                    if (GiveItemToPlayer(target, def, Math.Max(1, amount)))
                        givenCount++;
                }

                ReplyConsoleLocalized(arg, givenCount > 0 ? "GiveAllSuccess" : "GiveAllFail", def.shortname, Math.Max(1, amount), givenCount);
                return;
            }

            if (!HasPermission(player, permGiveAll))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            if (args.Length < 1)
            {
                ReplyPlayerConsoleLocalized(player, "UsageGiveAll", "inventory.giveall");
                return;
            }

            string playerItemInput = args[0];
            int playerAmount = 1;

            if (args.Length > 1 && !int.TryParse(args[1], out playerAmount))
            {
                ReplyPlayerConsoleLocalized(player, "InvalidAmount");
                return;
            }

            ItemDefinition playerDef = FindItemDef(playerItemInput, player);
            if (playerDef == null)
            {
                if (IsBlacklistedItemInput(playerItemInput, null, player))
                    ReplyPlayerConsoleLocalized(player, "ItemBlacklisted", playerItemInput);
                else
                    ReplyPlayerConsoleLocalized(player, "ItemNotFound", playerItemInput);
                return;
            }

            int playerGivenCount = 0;
            foreach (BasePlayer target in BasePlayer.activePlayerList.ToArray())
            {
                if (GiveItemToPlayer(target, playerDef, Math.Max(1, playerAmount)))
                    playerGivenCount++;
            }

            ReplyPlayerConsoleLocalized(player, playerGivenCount > 0 ? "GiveAllSuccess" : "GiveAllFail", playerDef.shortname, Math.Max(1, playerAmount), playerGivenCount);

            if (playerGivenCount > 0)
                LogGiveAction(player.displayName, $"{playerGivenCount} player(s)", playerDef.shortname, Math.Max(1, playerAmount));
        }

        [ConsoleCommand("disconnectteleport")]
        private void cmdDisconnectTeleportPlayerConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "disconnectteleport");
                return;
            }

            if (!HasPermission(player, permTerrain))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            PlayerInfo user = LoadPlayerInfo(player);
            if (user == null)
                return;

            string[] args = arg.Args ?? Array.Empty<string>();
            if (args.Length >= 1)
            {
                switch (args[0].ToLower())
                {
                    case "set":
                        if (args.Length == 4 && float.TryParse(args[1], out float x) && float.TryParse(args[2], out float y) && float.TryParse(args[3], out float z))
                        {
                            var customPos = new Vector3(x, y, z);

                            if (Vector3.Distance(customPos, Vector3.zero) <= TerrainMeta.Size.x / 1.5f && customPos.y > -100f && customPos.y < 4400f)
                            {
                                user.Teleport = customPos.ToString();
                                ReplyPlayerConsoleLocalized(player, "PositionAdded", FormatPosition(customPos));
                            }
                            else
                            {
                                ReplyPlayerConsoleLocalized(player, "OutOfBounds");
                            }
                        }
                        else
                        {
                            ReplyPlayerConsoleLocalized(player, "DisconnectTeleportSet", "disconnectteleport", FormatPosition(user.Teleport.ToVector3()));
                        }

                        SavePlayerInfo(player, user);
                        return;

                    case "reset":
                        user.Teleport = defaultPos.ToString();
                        if (defaultPos != Vector3.zero)
                            ReplyPlayerConsoleLocalized(player, "PositionRemoved2", FormatPosition(defaultPos));
                        else
                            ReplyPlayerConsoleLocalized(player, "PositionRemoved1");
                        SavePlayerInfo(player, user);
                        return;
                }
            }

            string teleportPos = FormatPosition(user.Teleport.ToVector3() == Vector3.zero ? defaultPos : user.Teleport.ToVector3());
            ReplyPlayerConsoleLocalized(player, "DisconnectTeleportSet", "disconnectteleport", teleportPos);
            ReplyPlayerConsoleLocalized(player, "DisconnectTeleportReset", "disconnectteleport");
        }

        [ConsoleCommand("entity.spawn")]
        private void cmdEntitySpawnConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] args = arg.Args ?? Array.Empty<string>();

            if (player != null)
                args = NormalizePlayerConsoleArgs(args);

            if (player == null)
            {
                if (args.Length < 1)
                {
                    ReplyConsoleLocalized(arg, "UsageSpawn", "entity.spawn");
                    return;
                }

                ReplyConsoleLocalized(arg, "PlayersOnly", "entity.spawn");
                return;
            }

            if (!HasPermission(player, permSpawn))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            if (args.Length < 1)
            {
                ReplyPlayerConsoleLocalized(player, "UsageSpawn", "entity.spawn");
                return;
            }

            string entityArg = args[0];
            string resolvedSelf = ResolveEntity(entityArg, player, (k, a) => ReplyPlayerConsoleLocalized(player, k, a));
            if (string.IsNullOrEmpty(resolvedSelf))
                return;

            bool spawnedSelf = SpawnForPlayer(player, resolvedSelf);
            ReplyPlayerConsoleLocalized(player, spawnedSelf ? "SpawnSuccess" : "SpawnFail", entityArg);

            if (spawnedSelf)
                LogSpawnAction(player.displayName, player.displayName, entityArg);
        }

        [ConsoleCommand("saveinventory")]
        private void cmdSaveInventoryPlayerConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "saveinventory");
                return;
            }

            if (!HasPermission(player, permInventory))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            PlayerInfo user = LoadPlayerInfo(player);
            if (user == null)
                return;

            user.SaveInventory = !user.SaveInventory;
            ReplyPlayerConsoleLocalized(player, user.SaveInventory ? "SavingInventory" : "NotSavingInventory");
            SavePlayerInfo(player, user);
        }

        [ConsoleCommand("kick")]
        private void cmdKickPlayerConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "kick");
                return;
            }

            if (!HasPermission(player, permKick))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            string[] args = arg.Args ?? Array.Empty<string>();
            if (args.Length < 1)
            {
                ReplyPlayerConsoleLocalized(player, "KickUsage", "kick");
                return;
            }

            var target = FindOnlinePlayer(args[0]);
            if (target == null)
            {
                ReplyPlayerConsoleLocalized(player, "PlayerNotFound");
                return;
            }

            if (permission.UserHasPermission(target.UserIDString, permPreventKick))
            {
                ReplyPlayerConsoleLocalized(player, "TargetProtectedFromKick", target.displayName);
                return;
            }

            string reason = string.Join(" ", args.Skip(1));
            if (string.IsNullOrWhiteSpace(reason))
                reason = defaultKickReason;

            target.IPlayer.Kick(reason);
            NotifyKickEvent(target.displayName, target.UserIDString, reason, true);
            ReplyPlayerConsoleLocalized(player, "KickSuccess", target.displayName);
        }

        [ConsoleCommand("ban")]
        private void cmdBanPlayerConsole(ConsoleSystem.Arg arg, bool includeIp)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "ban");
                return;
            }

            if (!HasPermission(player, includeIp ? permBanIp : permBan))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            string[] args = arg.Args ?? Array.Empty<string>();
            if (!TryParseModerationArgs(args, out var targetInput, out var duration, out var reason))
            {
                ReplyPlayerConsoleLocalized(player, includeIp ? "BanIpUsage" : "BanUsage", includeIp ? "banip" : "ban");
                return;
            }

            if (includeIp)
            {
                if (IsIpAddress(targetInput))
                {
                    AddIpBan(targetInput, targetInput, reason, duration, player.UserIDString);
                    KickAllPlayersByIp(targetInput);
                    int bannedAccounts = BanKnownAccountsOnIp(targetInput, duration, reason, player.UserIDString);
                    NotifyBanEvent(targetInput, null, targetInput, duration, reason, true, true, bannedAccounts, true);

                    if (duration.HasValue)
                        ReplyPlayerConsoleLocalized(player, "TempIpAndAccountsBanSuccess", targetInput, FormatDuration(duration.Value), bannedAccounts);
                    else
                        ReplyPlayerConsoleLocalized(player, "IpAndAccountsBanSuccess", targetInput, bannedAccounts);

                    return;
                }

                if (!TryResolveSteamId(targetInput, out var steamId, out var displayName, out var ip, out var onlinePlayer))
                {
                    ReplyPlayerConsoleLocalized(player, "PlayerNotFound");
                    return;
                }

                if (permission.UserHasPermission(steamId, permPreventBanIp))
                {
                    ReplyPlayerConsoleLocalized(player, "TargetProtectedFromBanIp", displayName ?? steamId);
                    return;
                }

                AddOrUpdatePlayerBan(steamId, displayName, reason, duration, player.UserIDString);
                SetNativeBan(steamId, displayName ?? steamId, reason, duration);

                if (IsValidStoredIp(ip))
                {
                    AddIpBan(ip, displayName ?? ip, reason, duration, player.UserIDString);
                    KickAllPlayersByIp(ip);
                }
                else if (onlinePlayer != null)
                {
                    onlinePlayer.IPlayer.Kick(BuildLoginBanMessage(steamId, null, onlinePlayer.UserIDString));
                }

                NotifyBanEvent(displayName ?? steamId, steamId, ip, duration, reason, true, false, 0, true);

                string shownIp = IsValidStoredIp(ip) ? ip : "unknown";
                if (duration.HasValue)
                    ReplyPlayerConsoleLocalized(player, "TempBanIpSuccess", displayName ?? steamId, shownIp, FormatDuration(duration.Value));
                else
                    ReplyPlayerConsoleLocalized(player, "BanIpSuccess", displayName ?? steamId, shownIp);

                return;
            }

            if (IsIpAddress(targetInput))
            {
                AddIpBan(targetInput, targetInput, reason, duration, player.UserIDString);
                KickAllPlayersByIp(targetInput);
                NotifyBanEvent(targetInput, null, targetInput, duration, reason, false, true, 0, true);

                if (duration.HasValue)
                    ReplyPlayerConsoleLocalized(player, "TempIpOnlyBanSuccess", targetInput, FormatDuration(duration.Value));
                else
                    ReplyPlayerConsoleLocalized(player, "IpOnlyBanSuccess", targetInput);

                return;
            }

            if (!TryResolveSteamId(targetInput, out var steamId2, out var displayName2, out var ip2, out var onlinePlayer2))
            {
                ReplyPlayerConsoleLocalized(player, "PlayerNotFound");
                return;
            }

            if (permission.UserHasPermission(steamId2, permPreventBan))
            {
                ReplyPlayerConsoleLocalized(player, "TargetProtectedFromBan", displayName2 ?? steamId2);
                return;
            }

            AddOrUpdatePlayerBan(steamId2, displayName2, reason, duration, player.UserIDString);
            SetNativeBan(steamId2, displayName2 ?? steamId2, reason, duration);
            onlinePlayer2?.IPlayer?.Kick(BuildLoginBanMessage(steamId2, ip2, onlinePlayer2?.UserIDString));
            NotifyBanEvent(displayName2 ?? steamId2, steamId2, ip2, duration, reason, false, false, 0, true);

            if (duration.HasValue)
                ReplyPlayerConsoleLocalized(player, "TempBanSuccess", displayName2 ?? steamId2, FormatDuration(duration.Value));
            else
                ReplyPlayerConsoleLocalized(player, "BanSuccess", displayName2 ?? steamId2);
        }

        [ConsoleCommand("banip")]
        private void cmdBanIpPlayerConsole(ConsoleSystem.Arg arg)
        {
            cmdBanPlayerConsole(arg, true);
        }

        [ConsoleCommand("unban")]
        private void cmdUnbanPlayerConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "unban");
                return;
            }

            if (!HasPermission(player, permUnban))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            string[] args = arg.Args ?? Array.Empty<string>();
            if (args.Length != 1)
            {
                ReplyPlayerConsoleLocalized(player, "UnbanUsage", "unban");
                return;
            }

            if (!TryResolveUnbanTarget(args[0], out var steamId, out var ip))
            {
                ReplyPlayerConsoleLocalized(player, "NotBanned");
                return;
            }

            if (!string.IsNullOrWhiteSpace(ip))
            {
                bool removedIpBan = moderationData.IpBans.Remove(ip);
                if (!removedIpBan)
                {
                    ReplyPlayerConsoleLocalized(player, "NotBanned");
                    return;
                }

                SaveModerationData();
                NotifyUnbanEvent(ip, true, true);
                ReplyPlayerConsoleLocalized(player, "UnbanIpSuccess", ip);
                return;
            }

            if (!string.IsNullOrWhiteSpace(steamId))
            {
                moderationData.PlayerBans.TryGetValue(steamId, out var banRecordBeforeRemove);

                string displayName =
                    banRecordBeforeRemove != null &&
                    !string.IsNullOrWhiteSpace(banRecordBeforeRemove.DisplayName) &&
                    !string.Equals(banRecordBeforeRemove.DisplayName, steamId, StringComparison.OrdinalIgnoreCase)
                        ? banRecordBeforeRemove.DisplayName
                        : GetKnownDisplayName(steamId);

                bool removedPlayerBan = moderationData.PlayerBans.Remove(steamId);
                bool removedIpBan = false;
                string linkedIp = null;

                if (moderationData.LastKnownIps.TryGetValue(steamId, out linkedIp) && IsValidStoredIp(linkedIp))
                    removedIpBan = moderationData.IpBans.Remove(linkedIp);
                else
                    linkedIp = null;

                if (!removedPlayerBan && !removedIpBan)
                {
                    ReplyPlayerConsoleLocalized(player, "NotBanned");
                    return;
                }

                if (removedPlayerBan)
                    RemoveNativeBan(steamId);

                SaveModerationData();

                if (removedPlayerBan && removedIpBan)
                    NotifyUnbanPlayerAndIpEvent(displayName, steamId, linkedIp, true);
                else if (removedPlayerBan)
                    NotifyUnbanEvent(displayName, false, true, steamId);
                else if (removedIpBan)
                    NotifyUnbanEvent(linkedIp, true, true);

                if (removedPlayerBan && removedIpBan)
                    ReplyPlayerConsoleLocalized(player, "UnbanPlayerAndIpSuccess", displayName, linkedIp);
                else if (removedPlayerBan)
                    ReplyPlayerConsoleLocalized(player, "UnbanPlayerSuccess", displayName);
                else if (removedIpBan)
                    ReplyPlayerConsoleLocalized(player, "UnbanIpSuccess", linkedIp);
            }
        }

        [ConsoleCommand("banlist")]
        private void cmdBanListPlayerConsole(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (player == null)
            {
                ReplyConsoleLocalized(arg, "PlayersOnly", "banlist");
                return;
            }

            if (!HasPermission(player, permBanList))
            {
                ReplyPlayerConsoleLocalized(player, "NoPermission");
                return;
            }

            string mode = "all";
            int page = 1;

            string[] args = arg.Args ?? Array.Empty<string>();
            if (args.Length >= 1)
                mode = args[0].ToLower();

            if (args.Length >= 2 && !int.TryParse(args[1], out page))
            {
                ReplyPlayerConsoleLocalized(player, "BanListUsage", "banlist");
                return;
            }

            if (page < 1)
                page = 1;

            var entries = GetBanListEntries(mode).ToList();
            if (entries.Count == 0)
            {
                ReplyPlayerConsoleLocalized(player, "BanListEmpty");
                return;
            }

            int totalPages = (int)Math.Ceiling(entries.Count / (double)banListPageSize);
            page = Math.Min(page, totalPages);

            ReplyPlayerConsoleLocalized(player, "BanListHeader", mode, page, totalPages);

            int start = (page - 1) * banListPageSize;
            var pageEntries = entries.Skip(start).Take(banListPageSize).ToList();

            for (int i = 0; i < pageEntries.Count; i++)
            {
                var entry = pageEntries[i];
                var ban = entry.Value;
                int index = start + i + 1;

                if (ban.ExpiresAt <= 0)
                {
                    ReplyPlayerConsoleLocalized(player, "BanListEntryPermanent", index, ban.DisplayName, ban.Target, ban.Reason);
                }
                else
                {
                    var remaining = TimeSpan.FromSeconds(Math.Max(0, ban.ExpiresAt - UnixNow()));
                    ReplyPlayerConsoleLocalized(player, "BanListEntryTemporary", index, ban.DisplayName, ban.Target, FormatDuration(remaining), ban.Reason);
                }
            }
        }

        ////////////////////////////////////////////////////////////
        // General Methods
        ////////////////////////////////////////////////////////////

        private bool GetSave()
        {
            if (newSave || BuildingManager.server.buildingDictionary.Count == 0)
            {
                return true;
            }

            return false;
        }

        private void ToggleNoClip(BasePlayer player, bool replyToConsole = false)
        {
            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return;

            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper, true);
            if (!player.IsFlying)
            {
                timer.Once(0.2f, () =>
                {
                    if (player == null || !player.IsConnected) return;

                    player.SendConsoleCommand("noclip");

                    if (replyToConsole)
                        ReplyPlayerConsoleLocalized(player, "FlyEnabled");
                    else
                        ReplyPlayerLocalized(player, "FlyEnabled");
                });
            }
            else
            {
                player.SendConsoleCommand("noclip");

                if (replyToConsole)
                    ReplyPlayerConsoleLocalized(player, "FlyDisabled");
                else
                    ReplyPlayerLocalized(player, "FlyDisabled");
            }

            user.NoClip = !player.IsFlying;
            SavePlayerInfo(player, user);
        }

        private void ToggleGodMode(BasePlayer player, bool replyToConsole = false)
        {
            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return;

            player.SetPlayerFlag(BasePlayer.PlayerFlags.IsDeveloper, true);
            if (!player.IsGod())
            {
                timer.Once(0.2f, () =>
                {
                    if (player == null || !player.IsConnected) return;

                    player.SendConsoleCommand("setinfo \"global.god\" \"true\"");

                    if (replyToConsole)
                        ReplyPlayerConsoleLocalized(player, "GodEnabled");
                    else
                        ReplyPlayerLocalized(player, "GodEnabled");
                });
            }
            else
            {
                player.SendConsoleCommand("setinfo \"global.god\" \"false\"");

                if (replyToConsole)
                    ReplyPlayerConsoleLocalized(player, "GodDisabled");
                else
                    ReplyPlayerLocalized(player, "GodDisabled");
            }

            user.GodMode = !player.IsGod();
            SavePlayerInfo(player, user);
        }

        private void DisconnectTeleport(BasePlayer player)
        {
            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return;

            var userTeleport = user.Teleport.ToVector3();
            var position = userTeleport == Vector3.zero ? defaultPos : userTeleport;

            if (position == Vector3.zero)
            {
                position = new Vector3(player.transform.position.x, TerrainMeta.HeightMap.GetHeight(player.transform.position) - 5f, player.transform.position.z);
            }

            player.Teleport(position);

            float terrainHeight = TerrainMeta.HeightMap.GetHeight(player.transform.position);
            bool isUnderTerrain = player.transform.position.y < terrainHeight || player.IsHeadUnderwater();

            if (isUnderTerrain)
            {
                player.metabolism.temperature.min = 20;
                player.metabolism.temperature.max = 20;
                player.metabolism.radiation_poison.max = 0;
                player.metabolism.oxygen.min = 1;
                player.metabolism.wetness.max = 0;
                player.metabolism.calories.min = player.metabolism.calories.value;
                player.metabolism.isDirty = true;
                player.metabolism.SendChangesToClient();
                user.UnderTerrain = true;
                SavePlayerInfo(player, user);
            }
        }

        private void NoUnderTerrain(BasePlayer player)
        {
            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return;

            float terrainHeight = TerrainMeta.HeightMap.GetHeight(player.transform.position);
            bool isUnderTerrain = player.transform.position.y < terrainHeight || player.IsHeadUnderwater();

            if (isUnderTerrain)
            {
                float newY = terrainHeight + 2f;
                player.Teleport(new Vector3(player.transform.position.x, newY, player.transform.position.z));
                player.SendNetworkUpdateImmediate();
                player.metabolism.temperature.min = -100;
                player.metabolism.temperature.max = 100;
                player.metabolism.radiation_poison.max = 500;
                player.metabolism.oxygen.min = 0;
                player.metabolism.calories.min = 0;
                player.metabolism.wetness.max = 1;
                player.metabolism.SendChangesToClient();
                user.UnderTerrain = false;
                SavePlayerInfo(player, user);
            }
        }

        private string FormatPosition(Vector3 position)
        {
            string x = position.x.ToString("N2");
            string y = position.y.ToString("N2");
            string z = position.z.ToString("N2");

            return $"{x} {y} {z}";
        }

        private void SaveInventory(BasePlayer player, bool snapshotOnly = false)
        {
            PlayerInfoItems userItems = LoadPlayerInfoItems(player);

            if (userItems == null)
                return;

            List<Item> itemList = Pool.Get<List<Item>>();
            int num = player.inventory.GetAllItems(itemList);
            Pool.FreeUnmanaged(ref itemList);

            if (num == 0)
            {
                return;
            }

            var items = new List<AdminUtilitiesItem>();

            AddItemsFromContainer(player.inventory.containerWear, "wear", items, snapshotOnly);
            AddItemsFromContainer(player.inventory.containerMain, "main", items, snapshotOnly);
            AddItemsFromContainer(player.inventory.containerBelt, "belt", items, snapshotOnly);

            if (items.Count == 0)
            {
                return;
            }

            if (!snapshotOnly)
                ItemManager.DoRemoves();

            userItems.Items.Clear();
            userItems.Items.AddRange(items);
            userItems.SnapshotOnly = snapshotOnly;
            SavePlayerInfoItems(player, userItems);
        }

        private void RestoreSavedInventory(BasePlayer player)
        {
            if (player == null || !player.IsConnected)
                return;

            PlayerInfo user = LoadPlayerInfo(player);
            if (user == null)
                return;

            PlayerInfoItems userItems = LoadPlayerInfoItems(player);
            if (userItems == null)
                return;

            if (!HasPermission(player, permInventory) || !user.SaveInventory || userItems.Items.Count == 0)
                return;

            List<Item> currentItems = Pool.Get<List<Item>>();
            int currentCount = player.inventory.GetAllItems(currentItems);
            Pool.FreeUnmanaged(ref currentItems);

            bool hasOnlyDefaultItems = HasOnlyDefaultSpawnItems(player);

            if (userItems.SnapshotOnly)
            {
                if (currentCount > 0 && !hasOnlyDefaultItems)
                {
                    userItems.Items.Clear();
                    userItems.SnapshotOnly = false;
                    SavePlayerInfoItems(player, userItems);
                    return;
                }

                if (hasOnlyDefaultItems)
                    player.inventory.Strip();
            }
            else
            {
                if (currentCount > 0 && !hasOnlyDefaultItems)
                    return;

                if (hasOnlyDefaultItems)
                    player.inventory.Strip();
            }

            foreach (var aui in userItems.Items)
            {
                if (aui.amount > 0)
                    AdminUtilitiesItem.Restore(player, aui);
            }

            userItems.Items.Clear();
            userItems.SnapshotOnly = false;
            SavePlayerInfoItems(player, userItems);
        }

        private bool HasOnlyDefaultSpawnItems(BasePlayer player)
        {
            if (player == null)
                return false;

            var rockDef = ItemManager.FindItemDefinition("rock");
            var torchDef = ItemManager.FindItemDefinition("torch");

            if (rockDef == null || torchDef == null)
                return false;

            List<Item> items = Pool.Get<List<Item>>();
            int count = player.inventory.GetAllItems(items);
            Pool.FreeUnmanaged(ref items);

            return count == 2
                && player.inventory.GetAmount(rockDef.itemid) == 1
                && player.inventory.GetAmount(torchDef.itemid) == 1;
        }

        private void AddItemsFromContainer(ItemContainer container, string containerName, List<AdminUtilitiesItem> items, bool snapshotOnly = false)
        {
            foreach (var item in container.itemList.ToList())
            {
                items.Add(new AdminUtilitiesItem(containerName, item));

                if (!snapshotOnly)
                    item.Remove();
            }
        }

        private void UpdateLastKnownPlayerIdentity(BasePlayer player)
        {
            if (player == null)
                return;

            bool changed = false;

            if (!string.IsNullOrWhiteSpace(player.displayName))
            {
                if (!moderationData.LastKnownNames.TryGetValue(player.UserIDString, out var oldName) || oldName != player.displayName)
                {
                    moderationData.LastKnownNames[player.UserIDString] = player.displayName;
                    changed = true;
                }
            }

            if (player.IPlayer != null && IsValidStoredIp(player.IPlayer.Address))
            {
                if (!moderationData.LastKnownIps.TryGetValue(player.UserIDString, out var oldIp) || oldIp != player.IPlayer.Address)
                {
                    moderationData.LastKnownIps[player.UserIDString] = player.IPlayer.Address;
                    changed = true;
                }
            }

            if (changed)
                SaveModerationData();
        }

        private string ExtractPlayerConsoleCommandName(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return string.Empty;

            string cmdName = command;
            int spaceIndex = cmdName.IndexOf(' ');
            if (spaceIndex >= 0)
                cmdName = cmdName.Substring(0, spaceIndex);

            return NormalizeDisabledPlayerConsoleCommand(cmdName);
        }

        private string ExtractChatCommandName(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return string.Empty;

            int quoteStart = command.IndexOf('"');
            if (quoteStart < 0)
                return string.Empty;

            int quoteEnd = command.LastIndexOf('"');
            if (quoteEnd <= quoteStart)
                return string.Empty;

            string message = command.Substring(quoteStart + 1, quoteEnd - quoteStart - 1).Trim();
            if (string.IsNullOrWhiteSpace(message) || !message.StartsWith("/"))
                return string.Empty;

            int spaceIndex = message.IndexOf(' ');
            string cmdName = spaceIndex >= 0 ? message.Substring(0, spaceIndex) : message;
            return NormalizeDisabledChatCommand(cmdName);
        }

        private string NormalizeDisabledPlayerConsoleCommand(string cmdName)
        {
            if (string.IsNullOrWhiteSpace(cmdName))
                return string.Empty;

            return cmdName.Trim().TrimStart('/').ToLowerInvariant();
        }

        private string NormalizeDisabledChatCommand(string cmdName)
        {
            if (string.IsNullOrWhiteSpace(cmdName))
                return string.Empty;

            string normalized = cmdName.Trim().TrimStart('/').ToLowerInvariant();
            return string.IsNullOrWhiteSpace(normalized) ? string.Empty : $"/{normalized}";
        }

        private bool IsDisabledPlayerConsoleCommand(string cmdName)
        {
            string normalized = NormalizeDisabledPlayerConsoleCommand(cmdName);
            return !string.IsNullOrWhiteSpace(normalized) && disabledPlayerConsoleCommands.Contains(normalized);
        }

        private bool IsDisabledChatCommand(string cmdName)
        {
            string normalized = NormalizeDisabledChatCommand(cmdName);
            return !string.IsNullOrWhiteSpace(normalized) && disabledChatCommands.Contains(normalized);
        }

        private long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private bool IsExpired(BanRecord ban)
        {
            return ban != null && ban.ExpiresAt > 0 && UnixNow() >= ban.ExpiresAt;
        }

        private void CleanupExpiredBans()
        {
            bool changed = false;

            foreach (var key in moderationData.PlayerBans.Where(x => IsExpired(x.Value)).Select(x => x.Key).ToList())
            {
                moderationData.PlayerBans.Remove(key);
                changed = true;
            }

            foreach (var key in moderationData.IpBans.Where(x => IsExpired(x.Value)).Select(x => x.Key).ToList())
            {
                moderationData.IpBans.Remove(key);
                changed = true;
            }

            if (changed)
                SaveModerationData();
        }

        private string MaskIp(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return "unknown";

            var parts = ip.Split('.');
            if (parts.Length == 4)
                return $"{parts[0]}.{parts[1]}.xxx.xxx";

            return ip;
        }

        private bool IsValidStoredIp(string ip)
        {
            return !string.IsNullOrWhiteSpace(ip)
                && ip != "0"
                && ip != "0.0.0.0"
                && IsIpAddress(ip);
        }

        private bool TryParseDuration(string input, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            var match = Regex.Match(input.Trim().ToLower(), @"^(?<value>\d+)(?<unit>[smhdw])$");
            if (!match.Success)
                return false;

            if (!int.TryParse(match.Groups["value"].Value, out int value) || value <= 0)
                return false;

            switch (match.Groups["unit"].Value)
            {
                case "s": duration = TimeSpan.FromSeconds(value); return true;
                case "m": duration = TimeSpan.FromMinutes(value); return true;
                case "h": duration = TimeSpan.FromHours(value); return true;
                case "d": duration = TimeSpan.FromDays(value); return true;
                case "w": duration = TimeSpan.FromDays(value * 7); return true;
            }

            return false;
        }

        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalDays >= 1)
                return $"{Math.Floor(duration.TotalDays)}d";
            if (duration.TotalHours >= 1)
                return $"{Math.Floor(duration.TotalHours)}h";
            if (duration.TotalMinutes >= 1)
                return $"{Math.Floor(duration.TotalMinutes)}m";

            return $"{Math.Max(1, Math.Floor(duration.TotalSeconds))}s";
        }

        private BasePlayer FindOnlinePlayer(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            if (ulong.TryParse(input, out ulong userId))
                return BasePlayer.activePlayerList.FirstOrDefault(p => p.userID == userId);

            return BasePlayer.activePlayerList.FirstOrDefault(p =>
                p.UserIDString == input ||
                p.displayName.Equals(input, StringComparison.OrdinalIgnoreCase) ||
                p.displayName.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private string GetKnownDisplayName(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
                return steamId;

            if (moderationData.PlayerBans.TryGetValue(steamId, out var banRecord) &&
                !string.IsNullOrWhiteSpace(banRecord?.DisplayName) &&
                !string.Equals(banRecord.DisplayName, steamId, StringComparison.OrdinalIgnoreCase))
            {
                return banRecord.DisplayName;
            }

            if (moderationData.LastKnownNames.TryGetValue(steamId, out var knownName) &&
                !string.IsNullOrWhiteSpace(knownName) &&
                !string.Equals(knownName, steamId, StringComparison.OrdinalIgnoreCase))
            {
                return knownName;
            }

            var onlinePlayer = BasePlayer.activePlayerList.FirstOrDefault(p => p.UserIDString == steamId);
            if (onlinePlayer != null && !string.IsNullOrWhiteSpace(onlinePlayer.displayName))
                return onlinePlayer.displayName;

            var sleepingPlayer = BasePlayer.sleepingPlayerList.FirstOrDefault(p => p.UserIDString == steamId);
            if (sleepingPlayer != null && !string.IsNullOrWhiteSpace(sleepingPlayer.displayName))
                return sleepingPlayer.displayName;

            if (ulong.TryParse(steamId, out var userId))
            {
                var nativeUser = ServerUsers.Get(userId);
                if (nativeUser != null &&
                    !string.IsNullOrWhiteSpace(nativeUser.username) &&
                    !string.Equals(nativeUser.username, steamId, StringComparison.OrdinalIgnoreCase))
                {
                    return nativeUser.username;
                }
            }

            return steamId;
        }

        private void CacheKnownDisplayName(string steamId, string displayName)
        {
            if (string.IsNullOrWhiteSpace(steamId) || string.IsNullOrWhiteSpace(displayName))
                return;

            if (string.Equals(displayName, steamId, StringComparison.OrdinalIgnoreCase))
                return;

            if (!moderationData.LastKnownNames.TryGetValue(steamId, out var oldName) ||
                !string.Equals(oldName, displayName, StringComparison.Ordinal))
            {
                moderationData.LastKnownNames[steamId] = displayName;
                SaveModerationData();
            }
        }

        private bool IsIpAddress(string input)
        {
            return !string.IsNullOrWhiteSpace(input) && Regex.IsMatch(input, @"^\d{1,3}(\.\d{1,3}){3}$");
        }

        private bool TryResolveSteamId(string input, out string steamId, out string displayName, out string ip, out BasePlayer onlinePlayer)
        {
            steamId = null;
            displayName = null;
            ip = null;
            onlinePlayer = null;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            if (ulong.TryParse(input, out ulong userId))
            {
                steamId = userId.ToString();

                onlinePlayer = BasePlayer.activePlayerList.FirstOrDefault(p => p.userID == userId);
                BasePlayer sleepingPlayer = BasePlayer.sleepingPlayerList.FirstOrDefault(p => p.userID == userId);

                if (moderationData.LastKnownIps.TryGetValue(steamId, out ip) && !IsValidStoredIp(ip))
                    ip = null;

                if (onlinePlayer != null)
                {
                    displayName = onlinePlayer.displayName;
                    ip = onlinePlayer.IPlayer?.Address;
                    CacheKnownDisplayName(steamId, displayName);
                    return true;
                }

                if (sleepingPlayer != null && !string.IsNullOrWhiteSpace(sleepingPlayer.displayName))
                {
                    displayName = sleepingPlayer.displayName;
                    CacheKnownDisplayName(steamId, displayName);
                    return true;
                }

                if (moderationData.LastKnownNames.TryGetValue(steamId, out var knownName) &&
                    !string.IsNullOrWhiteSpace(knownName))
                {
                    displayName = knownName;
                    return true;
                }

                if (moderationData.PlayerBans.TryGetValue(steamId, out var existingBan) &&
                    !string.IsNullOrWhiteSpace(existingBan.DisplayName))
                {
                    displayName = existingBan.DisplayName;
                    return true;
                }

                if (ulong.TryParse(steamId, out var nativeUserId))
                {
                    var nativeUser = ServerUsers.Get(nativeUserId);
                    if (nativeUser != null && !string.IsNullOrWhiteSpace(nativeUser.username))
                    {
                        displayName = nativeUser.username;
                        CacheKnownDisplayName(steamId, displayName);
                        return true;
                    }
                }

                displayName = steamId;
                return true;
            }

            onlinePlayer = FindOnlinePlayer(input);
            if (onlinePlayer == null)
                return false;

            steamId = onlinePlayer.UserIDString;
            displayName = onlinePlayer.displayName;
            ip = onlinePlayer.IPlayer?.Address;
            CacheKnownDisplayName(steamId, displayName);
            return true;
        }

        private void AddIpBan(string ip, string displayName, string reason, TimeSpan? duration, string source)
        {
            moderationData.IpBans[ip] = new BanRecord
            {
                Target = ip,
                DisplayName = displayName ?? ip,
                Reason = string.IsNullOrWhiteSpace(reason) ? defaultBanReason : reason,
                Source = source,
                CreatedAt = UnixNow(),
                ExpiresAt = duration.HasValue ? UnixNow() + (long)duration.Value.TotalSeconds : 0
            };

            SaveModerationData();
        }

        private bool TryGetActiveBan(string steamId, string ip, out BanRecord ban)
        {
            CleanupExpiredBans();

            ban = null;

            if (!string.IsNullOrWhiteSpace(steamId) &&
                moderationData.PlayerBans.TryGetValue(steamId, out ban) &&
                !IsExpired(ban))
                return true;

            if (!string.IsNullOrWhiteSpace(ip) &&
                moderationData.IpBans.TryGetValue(ip, out ban) &&
                !IsExpired(ban))
                return true;

            ban = null;
            return false;
        }

        private bool TryHandleServerConsoleCommand(ConsoleSystem.Arg arg, string cmdName, string[] args)
        {
            switch (cmdName)
            {
                case "kick":
                    HandleConsoleKick(arg, args);
                    return true;

                case "ban":
                case "banid":
                    HandleConsoleBan(arg, args, false);
                    return true;

                case "banip":
                    HandleConsoleBan(arg, args, true);
                    return true;

                case "unban":
                    HandleConsoleUnban(arg, args);
                    return true;

                case "banlist":
                    HandleConsoleBanList(arg, args);
                    return true;
            }

            return false;
        }

        private string BuildBanReasonLine(BanRecord ban, string langId = null)
        {
            if (ban == null || string.IsNullOrWhiteSpace(ban.Reason))
                return msg("BanReasonNoneLine", langId);

            return msg("BanReasonLine", langId, ban.Reason);
        }

        private void KickAllPlayersByIp(string ip)
        {
            foreach (var target in BasePlayer.activePlayerList.ToList())
            {
                if (target?.IPlayer == null)
                    continue;

                if (string.Equals(target.IPlayer.Address, ip, StringComparison.OrdinalIgnoreCase))
                    target.IPlayer.Kick(BuildLoginBanMessage(target.UserIDString, ip, target.UserIDString));
            }
        }

        private IEnumerable<KeyValuePair<string, BanRecord>> GetBanListEntries(string mode)
        {
            CleanupExpiredBans();

            mode = (mode ?? "all").ToLower();

            if (mode == "players")
                return moderationData.PlayerBans.OrderBy(x => x.Value.DisplayName);

            if (mode == "ips")
                return moderationData.IpBans.OrderBy(x => x.Value.DisplayName);

            return moderationData.PlayerBans
                .Concat(moderationData.IpBans)
                .OrderBy(x => x.Value.DisplayName);
        }

        private bool TryParseModerationArgs(string[] args, out string targetInput, out TimeSpan? duration, out string reason)
        {
            targetInput = null;
            duration = null;
            reason = null;

            if (args == null || args.Length < 1)
                return false;

            targetInput = args[0];

            if (args.Length >= 2 && TryParseDuration(args[1], out var parsedDuration))
            {
                duration = parsedDuration;
                reason = string.Join(" ", args.Skip(2));
            }
            else
            {
                reason = string.Join(" ", args.Skip(1));
            }

            if (string.IsNullOrWhiteSpace(reason))
                reason = defaultBanReason;

            return !string.IsNullOrWhiteSpace(targetInput);
        }

        private bool TryResolveUnbanTarget(string input, out string steamId, out string ip)
        {
            steamId = null;
            ip = null;

            if (string.IsNullOrWhiteSpace(input))
                return false;

            if (IsIpAddress(input))
            {
                ip = input;
                return true;
            }

            if (TryResolveSteamId(input, out var resolvedSteamId, out _, out _, out _))
            {
                steamId = resolvedSteamId;
                return true;
            }

            var existing = moderationData.PlayerBans
                .FirstOrDefault(x =>
                    x.Key.Equals(input, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(x.Value.DisplayName) &&
                     x.Value.DisplayName.Equals(input, StringComparison.OrdinalIgnoreCase)));

            if (!string.IsNullOrWhiteSpace(existing.Key))
            {
                steamId = existing.Key;
                return true;
            }

            return false;
        }

        private string BuildLoginBanMessage(string steamId, string ip, string langId = null)
        {
            CleanupExpiredBans();

            moderationData.PlayerBans.TryGetValue(steamId ?? string.Empty, out var playerBan);
            moderationData.IpBans.TryGetValue(ip ?? string.Empty, out var ipBan);

            if (playerBan != null && IsExpired(playerBan)) playerBan = null;
            if (ipBan != null && IsExpired(ipBan)) ipBan = null;

            if (playerBan == null && ipBan == null)
                return null;

            List<string> lines = new List<string>();

            if (playerBan != null && ipBan != null)
                lines.Add(msg("LoginBanAccountAndIp", langId));
            else if (playerBan != null)
                lines.Add(msg("LoginBanAccountOnly", langId));
            else
                lines.Add(msg("LoginBanIpOnly", langId));

            if (playerBan != null)
            {
                if (playerBan.ExpiresAt <= 0)
                    lines.Add(msg("AccountBanLinePermanent", langId));
                else
                    lines.Add(msg("AccountBanLineTemporary", langId, FormatDuration(TimeSpan.FromSeconds(Math.Max(0, playerBan.ExpiresAt - UnixNow())))));

                lines.Add(BuildBanReasonLine(playerBan, langId));
            }

            if (ipBan != null)
            {
                if (ipBan.ExpiresAt <= 0)
                    lines.Add(msg("IpBanLinePermanent", langId));
                else
                    lines.Add(msg("IpBanLineTemporary", langId, FormatDuration(TimeSpan.FromSeconds(Math.Max(0, ipBan.ExpiresAt - UnixNow())))));

                lines.Add(BuildBanReasonLine(ipBan, langId));
            }

            return string.Join("\n", lines);
        }

        private void AddOrUpdatePlayerBan(string steamId, string displayName, string reason, TimeSpan? duration, string source)
        {
            string finalDisplayName = displayName;

            if (string.IsNullOrWhiteSpace(finalDisplayName) ||
                string.Equals(finalDisplayName, steamId, StringComparison.OrdinalIgnoreCase))
            {
                finalDisplayName = GetKnownDisplayName(steamId);
            }

            if (string.IsNullOrWhiteSpace(finalDisplayName))
                finalDisplayName = steamId;

            moderationData.PlayerBans[steamId] = new BanRecord
            {
                Target = steamId,
                DisplayName = finalDisplayName,
                Reason = string.IsNullOrWhiteSpace(reason) ? defaultBanReason : reason,
                Source = source,
                CreatedAt = UnixNow(),
                ExpiresAt = duration.HasValue ? UnixNow() + (long)duration.Value.TotalSeconds : 0
            };

            CacheKnownDisplayName(steamId, finalDisplayName);
            SaveModerationData();
        }

        private int BanKnownAccountsOnIp(string ip, TimeSpan? duration, string reason, string source, string excludeSteamId = null)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return 0;

            int count = 0;

            foreach (var kvp in moderationData.LastKnownIps.ToList())
            {
                string steamId = kvp.Key;
                string knownIp = kvp.Value;

                if (knownIp != ip)
                    continue;

                if (!string.IsNullOrWhiteSpace(excludeSteamId) && steamId == excludeSteamId)
                    continue;

                string knownName = moderationData.LastKnownNames.TryGetValue(steamId, out var cachedName) && !string.IsNullOrWhiteSpace(cachedName)
                    ? cachedName
                    : steamId;

                AddOrUpdatePlayerBan(steamId, knownName, reason, duration, source);
                SetNativeBan(steamId, knownName, reason, duration);
                count++;
            }

            return count;
        }

        private void SetNativeBan(string steamId, string displayName, string reason, TimeSpan? duration = null)
        {
            if (string.IsNullOrWhiteSpace(steamId))
                return;

            if (!ulong.TryParse(steamId, out var userId))
                return;

            string safeName = string.IsNullOrWhiteSpace(displayName) ? steamId : displayName;
            string safeReason = string.IsNullOrWhiteSpace(reason) ? defaultBanReason : reason;

            ServerUsers.Remove(userId);

            if (duration.HasValue)
            {
                long expiry = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)duration.Value.TotalSeconds;
                ServerUsers.Set(userId, ServerUsers.UserGroup.Banned, safeName, safeReason, expiry);
            }
            else
            {
                ServerUsers.Set(userId, ServerUsers.UserGroup.Banned, safeName, safeReason);
            }

            ServerUsers.Save();
        }

        private void RemoveNativeBan(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
                return;

            if (!ulong.TryParse(steamId, out var userId))
                return;

            var user = ServerUsers.Get(userId);
            if (user != null && user.@group == ServerUsers.UserGroup.Banned)
            {
                ServerUsers.Remove(userId);
                ServerUsers.Save();
            }
        }

        private void HandleConsoleKick(ConsoleSystem.Arg arg, string[] args)
        {
            if (args == null || args.Length < 1)
            {
                ReplyConsoleLocalized(arg, "KickUsage", "kick");
                return;
            }

            var target = FindOnlinePlayer(args[0]);
            if (target == null)
            {
                ReplyConsoleLocalized(arg, "PlayerNotFound");
                return;
            }

            string reason = string.Join(" ", args.Skip(1));
            if (string.IsNullOrWhiteSpace(reason))
                reason = defaultKickReason;

            target.IPlayer.Kick(reason);
            NotifyKickEvent(target.displayName, target.UserIDString, reason, false);
            ReplyConsoleLocalized(arg, "KickSuccess", target.displayName);
        }

        private void HandleConsoleBan(ConsoleSystem.Arg arg, string[] args, bool includeIp)
        {
            if (!TryParseModerationArgs(args, out var targetInput, out var duration, out var reason))
            {
                ReplyConsoleLocalized(arg, includeIp ? "BanIpUsage" : "BanUsage", includeIp ? "banip" : "ban");
                return;
            }

            if (includeIp)
            {
                if (IsIpAddress(targetInput))
                {
                    AddIpBan(targetInput, targetInput, reason, duration, "Console");
                    KickAllPlayersByIp(targetInput);
                    int bannedAccounts = BanKnownAccountsOnIp(targetInput, duration, reason, "Console");

                    NotifyBanEvent(targetInput, null, targetInput, duration, reason, true, true, bannedAccounts, false);
                    if (duration.HasValue)
                        ReplyConsoleLocalized(arg, "TempIpAndAccountsBanSuccess", targetInput, FormatDuration(duration.Value), bannedAccounts);
                    else
                        ReplyConsoleLocalized(arg, "IpAndAccountsBanSuccess", targetInput, bannedAccounts);
                    return;
                }

                if (!TryResolveSteamId(targetInput, out var steamId, out var displayName, out var ip, out var onlinePlayer))
                {
                    ReplyConsoleLocalized(arg, "PlayerNotFound");
                    return;
                }

                AddOrUpdatePlayerBan(steamId, displayName, reason, duration, "Console");
                SetNativeBan(steamId, displayName ?? steamId, reason, duration);

                if (!string.IsNullOrWhiteSpace(ip))
                {
                    AddIpBan(ip, displayName ?? ip, reason, duration, "Console");
                    KickAllPlayersByIp(ip);
                }
                else
                {
                    onlinePlayer?.IPlayer?.Kick(BuildLoginBanMessage(steamId, null, onlinePlayer?.UserIDString));
                }

                NotifyBanEvent(displayName ?? steamId, steamId, ip, duration, reason, true, false, 0, false);
                string shownIp = IsValidStoredIp(ip) ? ip : "unknown";

                if (duration.HasValue)
                    ReplyConsoleLocalized(arg, "TempBanIpSuccess", displayName ?? steamId, shownIp, FormatDuration(duration.Value));
                else
                    ReplyConsoleLocalized(arg, "BanIpSuccess", displayName ?? steamId, shownIp);
                return;
            }

            if (IsIpAddress(targetInput))
            {
                AddIpBan(targetInput, targetInput, reason, duration, "Console");
                KickAllPlayersByIp(targetInput);

                NotifyBanEvent(targetInput, null, targetInput, duration, reason, false, true, 0, false);

                if (duration.HasValue)
                    ReplyConsoleLocalized(arg, "TempIpOnlyBanSuccess", targetInput, FormatDuration(duration.Value));
                else
                    ReplyConsoleLocalized(arg, "IpOnlyBanSuccess", targetInput);

                return;
            }

            if (!TryResolveSteamId(targetInput, out var steamId2, out var displayName2, out var ip2, out var onlinePlayer2))
            {
                ReplyConsoleLocalized(arg, "PlayerNotFound");
                return;
            }

            AddOrUpdatePlayerBan(steamId2, displayName2, reason, duration, "Console");
            SetNativeBan(steamId2, displayName2 ?? steamId2, reason, duration);
            onlinePlayer2?.IPlayer?.Kick(BuildLoginBanMessage(steamId2, ip2, onlinePlayer2?.UserIDString));

            NotifyBanEvent(displayName2 ?? steamId2, steamId2, ip2, duration, reason, false, false, 0, false);
            if (duration.HasValue)
                ReplyConsoleLocalized(arg, "TempBanSuccess", displayName2 ?? steamId2, FormatDuration(duration.Value));
            else
                ReplyConsoleLocalized(arg, "BanSuccess", displayName2 ?? steamId2);
        }

        private void HandleConsoleUnban(ConsoleSystem.Arg arg, string[] args)
        {
            if (args == null || args.Length != 1)
            {
                ReplyConsoleLocalized(arg, "UnbanUsage", "unban");
                return;
            }

            if (!TryResolveUnbanTarget(args[0], out var steamId, out var ip))
            {
                ReplyConsoleLocalized(arg, "NotBanned");
                return;
            }

            if (!string.IsNullOrWhiteSpace(ip))
            {
                bool removedIpBan = moderationData.IpBans.Remove(ip);

                if (!removedIpBan)
                {
                    ReplyConsoleLocalized(arg, "NotBanned");
                    return;
                }

                SaveModerationData();
                NotifyUnbanEvent(ip, true, false);
                ReplyConsoleLocalized(arg, "UnbanIpSuccess", ip);
                return;
            }

            if (!string.IsNullOrWhiteSpace(steamId))
            {
                moderationData.PlayerBans.TryGetValue(steamId, out var banRecordBeforeRemove);

                string displayName =
                    banRecordBeforeRemove != null &&
                    !string.IsNullOrWhiteSpace(banRecordBeforeRemove.DisplayName) &&
                    !string.Equals(banRecordBeforeRemove.DisplayName, steamId, StringComparison.OrdinalIgnoreCase)
                        ? banRecordBeforeRemove.DisplayName
                        : GetKnownDisplayName(steamId);

                bool removedPlayerBan = moderationData.PlayerBans.Remove(steamId);
                bool removedIpBan = false;
                string linkedIp = null;

                if (moderationData.LastKnownIps.TryGetValue(steamId, out linkedIp) && IsValidStoredIp(linkedIp))
                    removedIpBan = moderationData.IpBans.Remove(linkedIp);
                else
                    linkedIp = null;

                if (!removedPlayerBan && !removedIpBan)
                {
                    ReplyConsoleLocalized(arg, "NotBanned");
                    return;
                }

                if (removedPlayerBan)
                    RemoveNativeBan(steamId);

                SaveModerationData();

                if (removedPlayerBan && removedIpBan)
                    NotifyUnbanPlayerAndIpEvent(displayName, steamId, linkedIp, false);
                else if (removedPlayerBan)
                    NotifyUnbanEvent(displayName, false, false, steamId);
                else if (removedIpBan)
                    NotifyUnbanEvent(linkedIp, true, false);

                if (removedPlayerBan && removedIpBan)
                    ReplyConsoleLocalized(arg, "UnbanPlayerAndIpSuccess", displayName, linkedIp);
                else if (removedPlayerBan)
                    ReplyConsoleLocalized(arg, "UnbanPlayerSuccess", displayName);
                else if (removedIpBan)
                    ReplyConsoleLocalized(arg, "UnbanIpSuccess", linkedIp);
            }
        }

        private void HandleConsoleBanList(ConsoleSystem.Arg arg, string[] args)
        {
            string mode = "all";
            int page = 1;

            if (args != null && args.Length >= 1)
                mode = args[0].ToLowerInvariant();

            if (args != null && args.Length >= 2 && !int.TryParse(args[1], out page))
            {
                ReplyConsoleLocalized(arg, "BanListUsage", "banlist");
                return;
            }

            if (page < 1)
                page = 1;

            var entries = GetBanListEntries(mode).ToList();
            if (entries.Count == 0)
            {
                ReplyConsoleLocalized(arg, "BanListEmpty");
                return;
            }

            int totalPages = (int)Math.Ceiling(entries.Count / (double)banListPageSize);
            page = Math.Min(page, totalPages);

            ReplyConsoleLocalized(arg, "BanListHeader", mode, page, totalPages);

            int start = (page - 1) * banListPageSize;
            var pageEntries = entries.Skip(start).Take(banListPageSize).ToList();

            for (int i = 0; i < pageEntries.Count; i++)
            {
                var entry = pageEntries[i];
                var ban = entry.Value;
                int index = start + i + 1;

                if (ban.ExpiresAt <= 0)
                {
                    ReplyConsoleLocalized(arg, "BanListEntryPermanent", index, ban.DisplayName, ban.Target, ban.Reason);
                }
                else
                {
                    var remaining = TimeSpan.FromSeconds(Math.Max(0, ban.ExpiresAt - UnixNow()));
                    ReplyConsoleLocalized(arg, "BanListEntryTemporary", index, ban.DisplayName, ban.Target, FormatDuration(remaining), ban.Reason);
                }
            }
        }

        private string FormatDisplayWithSteamId(string displayName, string steamId)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return steamId ?? string.Empty;

            if (string.IsNullOrWhiteSpace(steamId) ||
                string.Equals(displayName, steamId, StringComparison.OrdinalIgnoreCase))
                return displayName;

            return $"{displayName} ({steamId})";
        }

        private void NotifyUnbanPlayerAndIpEvent(string displayName, string steamId, string ip, bool fromChatCommand = true)
        {
            string shownTargetForChat = displayName;
            string shownTargetForLog = FormatDisplayWithSteamId(displayName, steamId);
            string shownIpForLog = FormatIpForLog(ip);

            if (broadcastBanToChat)
                BroadcastLocalized("GlobalUnbanPlayerAndIpAnnouncement", shownTargetForChat, MaskIp(ip));

            if (logBanToConsole && fromChatCommand)
                Puts($"[Unban] {shownTargetForLog} + IP {shownIpForLog}");
        }

        private bool HasPermission(BasePlayer player, string permName)
        {
            return player != null && player.IPlayer.HasPermission(permName);
        }

        private string msg(string key, string id = null, params object[] args)
        {
            string message = lang.GetMessage(key, this, id);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private string msgConsole(string key, string id = null, params object[] args)
        {
            string message = PrepareConsoleMessage(lang.GetMessage(key, this, id));
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private string PrepareConsoleMessage(string source)
        {
            if (string.IsNullOrEmpty(source))
                return source;

            source = RemoveFormatting(source);
            return source.Replace('<', '‹').Replace('>', '›');
        }

        private string RemoveFormatting(string source)
        {
            if (string.IsNullOrEmpty(source) || !source.Contains("<"))
                return source;

            return Regex.Replace(source, @"</?(color|size|b|i|material|alpha)(=[^>]+)?>", string.Empty, RegexOptions.IgnoreCase);
        }

        private void ReplyConsole(ConsoleSystem.Arg arg, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (arg != null && arg.Connection != null)
            {
                arg.ReplyWith(message);
                return;
            }

            Puts(message);
        }

        private void BroadcastLocalized(string key, params object[] args)
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected)
                    continue;

                ReplyPlayerLocalized(player, key, args);
            }
        }

        private string FormatIpForLog(string ip)
        {
            return IsValidStoredIp(ip) ? ip : "unknown";
        }

        private void NotifyKickEvent(string displayName, string steamId, string reason, bool fromChatCommand = true)
        {
            if (broadcastKickToChat)
                BroadcastLocalized("GlobalKickAnnouncement", displayName, reason);

            if (logKickToConsole && fromChatCommand)
                Puts($"[Kick] {displayName} ({steamId}) | Reason: {reason}");
        }

        private void NotifyBanEvent(string displayName, string steamId, string ip, TimeSpan? duration, string reason, bool includesIp, bool ipOnly, int linkedAccounts = 0, bool fromChatCommand = true)
        {
            if (broadcastBanToChat)
            {
                string maskedIp = MaskIp(ip);

                if (ipOnly)
                {
                    if (duration.HasValue)
                        BroadcastLocalized("GlobalTempIpOnlyBanAnnouncement", maskedIp, FormatDuration(duration.Value), reason);
                    else
                        BroadcastLocalized("GlobalIpOnlyBanAnnouncement", maskedIp, reason);
                }
                else if (includesIp)
                {
                    if (duration.HasValue)
                        BroadcastLocalized("GlobalTempBanIpAnnouncement", displayName, maskedIp, FormatDuration(duration.Value), reason);
                    else
                        BroadcastLocalized("GlobalBanIpAnnouncement", displayName, maskedIp, reason);
                }
                else
                {
                    if (duration.HasValue)
                        BroadcastLocalized("GlobalTempBanAnnouncement", displayName, FormatDuration(duration.Value), reason);
                    else
                        BroadcastLocalized("GlobalBanAnnouncement", displayName, reason);
                }
            }

            if (logBanToConsole && fromChatCommand)
            {
                string shownIp = FormatIpForLog(ip);
                string shownDuration = duration.HasValue ? FormatDuration(duration.Value) : "permanent";
                string shownReason = string.IsNullOrWhiteSpace(reason) ? defaultBanReason : reason;

                if (ipOnly)
                {
                    if (linkedAccounts > 0)
                        Puts($"[BanIP] IP: {shownIp} | Duration: {shownDuration} | Reason: {shownReason} | Native banned linked accounts: {linkedAccounts}");
                    else
                        Puts($"[BanIP] IP: {shownIp} | Duration: {shownDuration} | Reason: {shownReason}");
                }
                else if (includesIp)
                {
                    Puts($"[BanIP] {displayName} ({steamId}) | IP: {shownIp} | Duration: {shownDuration} | Reason: {shownReason}");
                }
                else
                {
                    Puts($"[Ban] {displayName} ({steamId}) | Duration: {shownDuration} | Reason: {shownReason}");
                }
            }
        }

        private void NotifyUnbanEvent(string target, bool isIp, bool fromChatCommand = true, string steamId = null)
        {
            string shownTargetForChat = isIp ? MaskIp(target) : target;
            string shownTargetForLog = isIp ? FormatIpForLog(target) : FormatDisplayWithSteamId(target, steamId);

            if (broadcastBanToChat)
            {
                if (isIp)
                    BroadcastLocalized("GlobalUnbanIpAnnouncement", shownTargetForChat);
                else
                    BroadcastLocalized("GlobalUnbanAnnouncement", shownTargetForChat);
            }

            if (logBanToConsole && fromChatCommand)
            {
                if (isIp)
                    Puts($"[UnbanIP] {shownTargetForLog}");
                else
                    Puts($"[Unban] {shownTargetForLog}");
            }
        }

        private void ReplyPlayer(BasePlayer player, string message)
        {
            if (player == null || !player.IsConnected || string.IsNullOrWhiteSpace(message))
                return;

            ulong iconId = GetChatMessageIconId();
            if (iconId != 0)
            {
                Player.Message(player, message, null, iconId);
                return;
            }

            Player.Message(player, message);
        }

        private void ReplyPlayerLocalized(BasePlayer player, string key, params object[] args)
        {
            ReplyPlayer(player, msg(key, player?.UserIDString, args));
        }

        private void ReplyPlayerConsole(BasePlayer player, string message)
        {
            if (player?.net?.connection == null || string.IsNullOrWhiteSpace(message))
                return;

            player.SendConsoleCommand("echo", message);
        }

        private void ReplyPlayerConsoleLocalized(BasePlayer player, string key, params object[] args)
        {
            ReplyPlayerConsole(player, msgConsole(key, player?.UserIDString, args));
        }

        private void ReplyConsoleLocalized(ConsoleSystem.Arg arg, string key, params object[] args)
        {
            ReplyConsole(arg, msgConsole(key, null, args));
        }

        private ulong GetChatMessageIconId()
        {
            if (TryParseSteamIconId(globalServerMessagesIconSteamIdOrGroupId, out ulong iconId))
                return iconId;

            return 0;
        }

        private bool TryParseSteamIconId(string value, out ulong steamId)
        {
            steamId = 0;
            return !string.IsNullOrWhiteSpace(value) && ulong.TryParse(value.Trim(), out steamId) && steamId > 0;
        }

        private void TryApplyGlobalServerIcon(string command, object[] args)
        {
            if (args == null)
                return;

            ulong iconId = GetChatMessageIconId();
            if (iconId == 0)
                return;

            if (args.Length < 2)
                return;

            if (command != "chat.add" && command != "chat.add2")
                return;

            ulong providedId;
            if (ulong.TryParse(args[1]?.ToString(), out providedId) && providedId == 0)
                args[1] = iconId;
        }

        private string[] NormalizePlayerConsoleArgs(string[] args)
        {
            if (args == null || args.Length == 0)
                return Array.Empty<string>();

            List<string> cleaned = new List<string>();

            foreach (string raw in args)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                string value = raw.Trim();

                if (value.Length >= 2 && value.StartsWith("\"") && value.EndsWith("\""))
                    value = value.Substring(1, value.Length - 2).Trim();

                if (value.StartsWith("(") && value.EndsWith(")"))
                    continue;

                if (value.Contains(",") && value.Contains("."))
                    continue;

                cleaned.Add(value);
            }

            return cleaned.ToArray();
        }

        private void CachePrefabs()
        {
            allPrefabs.Clear();
            prefabLookup.Clear();

            foreach (var pooled in GameManifest.Current.pooledStrings)
            {
                string path = pooled.str;
                if (!IsSpawnablePrefab(path))
                    continue;

                allPrefabs.Add(path);

                string fileName = path.Substring(path.LastIndexOf('/') + 1);
                string withoutExt = fileName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                    ? fileName.Substring(0, fileName.Length - 7)
                    : fileName;

                AddPrefabLookup(path, path);
                AddPrefabLookup(fileName, path);
                AddPrefabLookup(withoutExt, path);
            }

            WarnAboutInvalidAliasPrefabs();
        }

        private bool IsSpawnablePrefab(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string p = path.ToLowerInvariant();

            if (!p.StartsWith("assets/") || !p.EndsWith(".prefab"))
                return false;

            if (p.Contains("/effects/")) return false;
            if (p.Contains("/effect/")) return false;
            if (p.Contains("/fx/")) return false;
            if (p.Contains("/sound/")) return false;
            if (p.Contains("/sounds/")) return false;
            if (p.Contains("/ui/")) return false;
            if (p.Contains("/editor/")) return false;
            if (p.Contains("/debug/")) return false;
            if (p.Contains("/test/")) return false;
            if (p.Contains("/_unused/")) return false;

            return true;
        }

        private void AddPrefabLookup(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                return;

            if (!prefabLookup.ContainsKey(key))
                prefabLookup[key] = value;
        }

        private bool IsKnownSpawnPrefab(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && allPrefabs.Contains(path);
        }

        private void WarnAboutInvalidAliasPrefabs()
        {
            if (giveSpawnAliases == null || giveSpawnAliases.Count == 0)
                return;

            List<string> invalidAliases = new List<string>();

            foreach (var entry in giveSpawnAliases)
            {
                if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                    continue;

                string aliasValue = entry.Value.Trim();
                if (!aliasValue.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsKnownSpawnPrefab(aliasValue))
                    continue;

                invalidAliases.Add($"{entry.Key} -> {aliasValue}");
            }

            if (invalidAliases.Count == 0)
                return;

            PrintWarning($"Found {invalidAliases.Count} alias(es) that point to prefab paths missing from the current Rust build or not considered spawnable by this plugin.");
            foreach (string invalidAlias in invalidAliases)
                PrintWarning($"Invalid spawn alias: {invalidAlias}");
        }

        private bool TryResolveAliasEntity(string aliasKey, out string resolvedEntity, out string invalidPrefabPath)
        {
            resolvedEntity = null;
            invalidPrefabPath = null;

            if (string.IsNullOrWhiteSpace(aliasKey) || giveSpawnAliases == null || giveSpawnAliases.Count == 0)
                return false;

            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = aliasKey.Trim();

            while (!string.IsNullOrWhiteSpace(current) && giveSpawnAliases.TryGetValue(current, out string aliasValue))
            {
                if (!visited.Add(current))
                    return false;

                if (string.IsNullOrWhiteSpace(aliasValue))
                    return false;

                aliasValue = aliasValue.Trim();

                if (IsSpawnToken(aliasValue))
                {
                    resolvedEntity = aliasValue;
                    return true;
                }

                if (aliasValue.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                {
                    if (IsKnownSpawnPrefab(aliasValue))
                    {
                        resolvedEntity = aliasValue;
                        return true;
                    }

                    invalidPrefabPath = aliasValue;
                    return false;
                }

                if (prefabLookup.TryGetValue(aliasValue, out string prefab))
                {
                    resolvedEntity = prefab;
                    return true;
                }

                current = aliasValue;
            }

            return false;
        }

        private bool CanBypassBlacklist(BasePlayer player)
        {
            return HasPermission(player, permBypassBlacklist);
        }

        private void LogSpawnAction(string actor, string target, string entity)
        {
            if (logSpawnToConsole)
                Puts($"{actor} spawned '{entity}' for {target}");
        }

        private void LogGiveAction(string actor, string target, string item, int amount)
        {
            if (logGiveToConsole)
                Puts($"{actor} gave '{item}' x{amount} to {target}");
        }

        private BasePlayer FindPlayerExtended(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
                return null;

            string search = nameOrId.Trim();

            List<BasePlayer> players = BasePlayer.activePlayerList
                .Concat(BasePlayer.sleepingPlayerList)
                .Distinct()
                .ToList();

            BasePlayer exact = players.FirstOrDefault(p =>
                p.UserIDString.Equals(search, StringComparison.OrdinalIgnoreCase) ||
                p.displayName.Equals(search, StringComparison.OrdinalIgnoreCase));

            if (exact != null)
                return exact;

            List<BasePlayer> partial = players
                .Where(p => p.displayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            return partial.Count == 1 ? partial[0] : null;
        }

        private bool IsSpawnToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string v = value.Trim().ToLowerInvariant();

            return Regex.IsMatch(v, @"^(?:car_)?2mod_\d+$") ||
                   Regex.IsMatch(v, @"^(?:car_)?3mod_\d+$") ||
                   Regex.IsMatch(v, @"^(?:car_)?4mod_\d+$") ||
                   v == "2modulecar" ||
                   v == "3modulecar" ||
                   v == "4modulecar";
        }

        private string TryResolveSpawnTokenToPrefab(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            string v = token.Trim().ToLowerInvariant();

            if (TryResolveAliasEntity(v, out string aliasResolvedEntity, out string invalidPrefabPath))
            {
                if (!IsSpawnToken(aliasResolvedEntity))
                    return aliasResolvedEntity;

                v = aliasResolvedEntity.Trim().ToLowerInvariant();
            }

            int modules = 0;

            if (Regex.IsMatch(v, @"^(?:car_)?2mod_\d+$") || v == "2modulecar")
                modules = 2;
            else if (Regex.IsMatch(v, @"^(?:car_)?3mod_\d+$") || v == "3modulecar")
                modules = 3;
            else if (Regex.IsMatch(v, @"^(?:car_)?4mod_\d+$") || v == "4modulecar")
                modules = 4;
            else
                return null;

            return ResolveModularCarSpawnedPrefab(modules);
        }

        private string ResolveModularCarSpawnedPrefab(int modules)
        {
            string[] candidates;

            switch (modules)
            {
                case 2:
                    candidates = new[]
                    {
                        "assets/content/vehicles/modularcar/2module_car_spawned.entity.prefab",
                        "2module_car_spawned.entity.prefab",
                        "2module_car_spawned.entity"
                    };
                    break;

                case 3:
                    candidates = new[]
                    {
                        "assets/content/vehicles/modularcar/3module_car_spawned.entity.prefab",
                        "3module_car_spawned.entity.prefab",
                        "3module_car_spawned.entity"
                    };
                    break;

                case 4:
                    candidates = new[]
                    {
                        "assets/content/vehicles/modularcar/4module_car_spawned.entity.prefab",
                        "4module_car_spawned.entity.prefab",
                        "4module_car_spawned.entity"
                    };
                    break;

                default:
                    return null;
            }

            foreach (string candidate in candidates)
            {
                if (prefabLookup.TryGetValue(candidate, out string prefab) && IsKnownSpawnPrefab(prefab))
                    return prefab;

                if (IsKnownSpawnPrefab(candidate))
                    return candidate;
            }

            return null;
        }

        private string ResolveEntity(string input, BasePlayer permissionPlayer, Action<string, object[]> reply)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                reply("EntityNotFound", new object[] { input });
                return null;
            }

            string value = input.Trim();

            if (spawnAliasBlacklist.Contains(value, StringComparer.OrdinalIgnoreCase) && !CanBypassBlacklist(permissionPlayer))
            {
                reply("EntityBlacklisted", new object[] { value });
                return null;
            }

            if (TryResolveAliasEntity(value, out string resolvedAliasEntity, out string invalidPrefabPath))
                return resolvedAliasEntity;

            if (!string.IsNullOrWhiteSpace(invalidPrefabPath))
            {
                reply("EntityAliasInvalid", new object[] { value, invalidPrefabPath });
                return null;
            }

            if (IsSpawnToken(value))
                return value;

            if (prefabLookup.TryGetValue(value, out string prefab))
                return prefab;

            if (allowDirectPrefabPaths &&
                value.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) &&
                value.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                if (IsKnownSpawnPrefab(value))
                    return value;

                reply("EntityPrefabInvalid", new object[] { value });
                return null;
            }

            if (allowPartialPrefabSearch)
            {
                List<string> matches = allPrefabs
                    .Where(p => p.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Take(10)
                    .ToList();

                if (matches.Count == 1)
                    return matches[0];

                if (matches.Count > 1)
                {
                    reply("EntityMultipleFound", new object[] { string.Join(", ", matches.Select(GetPrefabDisplayName).ToArray()) });
                    return null;
                }
            }

            reply("EntityNotFound", new object[] { input });
            return null;
        }

        private string GetPrefabDisplayName(string prefab)
        {
            string fileName = prefab.Substring(prefab.LastIndexOf('/') + 1);
            return fileName.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - 7)
                : fileName;
        }

        private ItemDefinition FindItemDef(string input, BasePlayer permissionPlayer)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            string search = input.Trim();

            if (giveItemBlacklist.Contains(search, StringComparer.OrdinalIgnoreCase) && !CanBypassBlacklist(permissionPlayer))
                return null;

            if (int.TryParse(search, out int itemId))
            {
                ItemDefinition byId = ItemManager.itemList.FirstOrDefault(x => x.itemid == itemId);
                if (byId != null)
                {
                    if (giveItemBlacklist.Contains(byId.shortname, StringComparer.OrdinalIgnoreCase) && !CanBypassBlacklist(permissionPlayer))
                        return null;

                    return byId;
                }
            }

            ItemDefinition def = ItemManager.FindItemDefinition(search);
            if (def != null)
            {
                if (giveItemBlacklist.Contains(def.shortname, StringComparer.OrdinalIgnoreCase) && !CanBypassBlacklist(permissionPlayer))
                    return null;

                return def;
            }

            def = ItemManager.itemList.FirstOrDefault(x =>
                x.shortname.Equals(search, StringComparison.OrdinalIgnoreCase) ||
                x.displayName.english.Equals(search, StringComparison.OrdinalIgnoreCase));

            if (def != null)
            {
                if (giveItemBlacklist.Contains(def.shortname, StringComparer.OrdinalIgnoreCase) && !CanBypassBlacklist(permissionPlayer))
                    return null;

                return def;
            }

            List<ItemDefinition> partial = ItemManager.itemList
                .Where(x =>
                    x.shortname.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.displayName.english.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (partial.Count == 1)
            {
                if (giveItemBlacklist.Contains(partial[0].shortname, StringComparer.OrdinalIgnoreCase) && !CanBypassBlacklist(permissionPlayer))
                    return null;

                return partial[0];
            }

            return null;
        }

        private bool IsBlacklistedItemInput(string input, ItemDefinition def, BasePlayer permissionPlayer)
        {
            if (CanBypassBlacklist(permissionPlayer))
                return false;

            if (!string.IsNullOrWhiteSpace(input) &&
                giveItemBlacklist.Contains(input.Trim(), StringComparer.OrdinalIgnoreCase))
                return true;

            if (def != null &&
                giveItemBlacklist.Contains(def.shortname, StringComparer.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private bool GiveItemToPlayer(BasePlayer target, ItemDefinition def, int amount)
        {
            if (target == null || def == null || amount <= 0)
                return false;

            Item item = ItemManager.Create(def, amount);
            if (item == null)
                return false;

            target.GiveItem(item);
            return true;
        }

        private bool SpawnForPlayer(BasePlayer target, string resolved)
        {
            if (target == null || !target.IsConnected)
                return false;

            Vector3 position = GetSpawnPosition(target);
            Quaternion rotation = Quaternion.Euler(0f, target.eyes.rotation.eulerAngles.y, 0f);

            if (IsSpawnToken(resolved))
            {
                string tokenPrefab = TryResolveSpawnTokenToPrefab(resolved);
                if (string.IsNullOrEmpty(tokenPrefab) || !IsKnownSpawnPrefab(tokenPrefab))
                {
                    PrintWarning($"Failed to resolve a valid prefab for spawn token '{resolved}'.");
                    return false;
                }

                BaseEntity tokenEntity = GameManager.server.CreateEntity(tokenPrefab, position, rotation);
                if (tokenEntity == null)
                {
                    PrintWarning($"CreateEntity returned null for token '{resolved}' using prefab '{tokenPrefab}'.");
                    return false;
                }

                tokenEntity.Spawn();
                return true;
            }

            if (!IsKnownSpawnPrefab(resolved))
            {
                PrintWarning($"Spawn blocked because prefab '{resolved}' is not present in the current cached spawnable prefab list.");
                return false;
            }

            BaseEntity entity = GameManager.server.CreateEntity(resolved, position, rotation);
            if (entity == null)
            {
                PrintWarning($"CreateEntity returned null for prefab '{resolved}'.");
                return false;
            }

            entity.Spawn();
            return true;
        }

        private Vector3 GetSpawnPosition(BasePlayer player)
        {
            if (useCrosshairRaycast)
            {
                Vector3? crosshairPosition = GetCrosshairSpawnPosition(player);
                if (crosshairPosition.HasValue)
                    return crosshairPosition.Value;
            }

            Vector3 pos = player.transform.position + player.eyes.BodyForward() * Mathf.Max(1f, spawnDistance);
            float groundHeight = TerrainMeta.HeightMap.GetHeight(pos);

            if (pos.y < groundHeight + spawnHeightOffset)
                pos.y = groundHeight + spawnHeightOffset;
            else
                pos.y += spawnHeightOffset;

            return pos;
        }

        private Vector3? GetCrosshairSpawnPosition(BasePlayer player)
        {
            Ray ray = new Ray(player.eyes.position, player.eyes.HeadForward());

            if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Max(1f, maxCrosshairDistance)))
                return null;

            Vector3 pos = hit.point;
            float groundHeight = TerrainMeta.HeightMap.GetHeight(pos);

            if (pos.y < groundHeight + spawnHeightOffset)
                pos.y = groundHeight + spawnHeightOffset;
            else
                pos.y += spawnHeightOffset;

            return pos;
        }

        private BaseEntity GetLookEntity(BasePlayer player, float maxDistance)
        {
            Ray ray = new Ray(player.eyes.position, player.eyes.HeadForward());

            if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Max(1f, maxDistance)))
                return null;

            return hit.GetEntity();
        }

        private Dictionary<string, string> GetDefaultAliases()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mini"] = "assets/content/vehicles/minicopter/minicopter.entity.prefab",
                ["minicopter"] = "assets/content/vehicles/minicopter/minicopter.entity.prefab",
                ["scrap"] = "assets/content/vehicles/scrap heli carrier/scraptransporthelicopter.prefab",
                ["scrapheli"] = "assets/content/vehicles/scrap heli carrier/scraptransporthelicopter.prefab",
                ["2mod_01"] = "car_2mod_01",
                ["2mod_02"] = "car_2mod_02",
                ["2mod_03"] = "car_2mod_03",
                ["2mod_04"] = "car_2mod_04",
                ["2mod_05"] = "car_2mod_05",
                ["2mod_06"] = "car_2mod_06",
                ["2mod_07"] = "car_2mod_07",
                ["2mod_08"] = "car_2mod_08",
                ["3mod_01"] = "car_3mod_01",
                ["3mod_02"] = "car_3mod_02",
                ["3mod_03"] = "car_3mod_03",
                ["3mod_04"] = "car_3mod_04",
                ["3mod_05"] = "car_3mod_05",
                ["3mod_06"] = "car_3mod_06",
                ["3mod_07"] = "car_3mod_07",
                ["3mod_08"] = "car_3mod_08",
                ["3mod_09"] = "car_3mod_09",
                ["3mod_10"] = "car_3mod_10",
                ["3mod_11"] = "car_3mod_11",
                ["3mod_12"] = "car_3mod_12",
                ["4mod_01"] = "car_4mod_01",
                ["4mod_02"] = "car_4mod_02",
                ["4mod_03"] = "car_4mod_03",
                ["4mod_04"] = "car_4mod_04",
                ["4mod_05"] = "car_4mod_05",
                ["4mod_06"] = "car_4mod_06",
                ["4mod_07"] = "car_4mod_07",
                ["4mod_08"] = "car_4mod_08",
                ["4mod_09"] = "car_4mod_09",
                ["4mod_10"] = "car_4mod_10",
                ["4mod_11"] = "car_4mod_11"
            };
        }

        ////////////////////////////////////////////////////////////
        // Configs
        ////////////////////////////////////////////////////////////

        private bool ConfigChanged;
        private Vector3 defaultPos;
        private bool wipeItems;
        private bool wipeSettings;
        private bool persistentNoClip;
        private bool persistentGodMode;
        private string defaultKickReason;
        private string defaultBanReason;
        private int banListPageSize;
        private bool broadcastKickToChat;
        private bool broadcastBanToChat;
        private bool logKickToConsole;
        private bool logBanToConsole;
        private string globalServerMessagesIconSteamIdOrGroupId;
        private HashSet<string> disabledPlayerConsoleCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> disabledChatCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private float spawnDistance;
        private bool useCrosshairRaycast;
        private float maxCrosshairDistance;
        private float spawnHeightOffset;
        private bool allowDirectPrefabPaths;
        private bool allowPartialPrefabSearch;
        private float maxDespawnDistance;
        private bool logGiveToConsole;
        private bool logSpawnToConsole;
        private List<string> spawnAliasBlacklist;
        private List<string> giveItemBlacklist;
        private Dictionary<string, string> giveSpawnAliases;

        private class PluginConfigData
        {
            [JsonProperty(PropertyName = "General")]
            public GeneralConfig General { get; set; } = new GeneralConfig();

            [JsonProperty(PropertyName = "NoClip & God")]
            public NoClipGodConfig NoClipAndGod { get; set; } = new NoClipGodConfig();

            [JsonProperty(PropertyName = "Moderation")]
            public ModerationConfig Moderation { get; set; } = new ModerationConfig();

            [JsonProperty(PropertyName = "Give & Spawn")]
            public GiveSpawnConfig GiveAndSpawn { get; set; } = new GiveSpawnConfig();
        }

        private class GeneralConfig
        {
            [JsonProperty(PropertyName = "Default Teleport To Position On Disconnect")]
            public string DefaultTeleportToPositionOnDisconnect { get; set; } = "(0, 0, 0)";

            [JsonProperty(PropertyName = "Disabled Player Console Commands")]
            public List<string> DisabledPlayerConsoleCommands { get; set; } = new List<string> { };

            [JsonProperty(PropertyName = "Disabled Chat Commands")]
            public List<string> DisabledChatCommands { get; set; } = new List<string> { };

            [JsonProperty(PropertyName = "Wipe Saved Inventories On Map Wipe")]
            public bool WipeSavedInventoriesOnMapWipe { get; set; } = false;

            [JsonProperty(PropertyName = "Wipe Players Settings On Map Wipe")]
            public bool WipePlayersSettingsOnMapWipe { get; set; } = false;

            [JsonProperty(PropertyName = "Global Server Messages Icon Steam ID Or Group ID")]
            public string GlobalServerMessagesIconSteamIdOrGroupId { get; set; } = string.Empty;
        }

        private class NoClipGodConfig
        {
            [JsonProperty(PropertyName = "Enable Persistent NoClip")]
            public bool EnablePersistentNoClip { get; set; } = false;

            [JsonProperty(PropertyName = "Enable Persistent GodMode")]
            public bool EnablePersistentGodMode { get; set; } = false;
        }

        private class ModerationConfig
        {
            [JsonProperty(PropertyName = "Default Kick Reason")]
            public string DefaultKickReason { get; set; } = "Unknown reason.";

            [JsonProperty(PropertyName = "Default Ban Reason")]
            public string DefaultBanReason { get; set; } = "Unknown reason.";

            [JsonProperty(PropertyName = "Broadcast Kick To Global Chat")]
            public bool BroadcastKickToGlobalChat { get; set; } = true;

            [JsonProperty(PropertyName = "Broadcast Ban To Global Chat")]
            public bool BroadcastBanToGlobalChat { get; set; } = true;

            [JsonProperty(PropertyName = "Log Kick Events To Console")]
            public bool LogKickEventsToConsole { get; set; } = true;

            [JsonProperty(PropertyName = "Log Ban Events To Console")]
            public bool LogBanEventsToConsole { get; set; } = true;

            [JsonProperty(PropertyName = "BanList Page Size")]
            public int BanListPageSize { get; set; } = 10;
        }

        private class GiveSpawnConfig
        {
            [JsonProperty(PropertyName = "Spawn Distance In Front Of Player")]
            public float SpawnDistanceInFrontOfPlayer { get; set; } = 4f;

            [JsonProperty(PropertyName = "Use Crosshair Raycast For Spawn Position")]
            public bool UseCrosshairRaycastForSpawnPosition { get; set; } = false;

            [JsonProperty(PropertyName = "Maximum Crosshair Spawn Distance")]
            public float MaximumCrosshairSpawnDistance { get; set; } = 25f;

            [JsonProperty(PropertyName = "Raise Spawn Position On Y Axis")]
            public float RaiseSpawnPositionOnYAxis { get; set; } = 0.25f;

            [JsonProperty(PropertyName = "Allow Direct Prefab Paths")]
            public bool AllowDirectPrefabPaths { get; set; } = true;

            [JsonProperty(PropertyName = "Allow Partial Prefab Search")]
            public bool AllowPartialPrefabSearch { get; set; } = true;

            [JsonProperty(PropertyName = "Maximum Despawn Distance")]
            public float MaximumDespawnDistance { get; set; } = 25f;

            [JsonProperty(PropertyName = "Log Give Commands To Console")]
            public bool LogGiveCommandsToConsole { get; set; } = true;

            [JsonProperty(PropertyName = "Log Spawn Commands To Console")]
            public bool LogSpawnCommandsToConsole { get; set; } = true;

            [JsonProperty(PropertyName = "Spawn Alias Blacklist")]
            public List<string> SpawnAliasBlacklist { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Give Item Blacklist")]
            public List<string> GiveItemBlacklist { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Default Aliases")]
            public Dictionary<string, string> DefaultAliases { get; set; } = new Dictionary<string, string>();
        }

        protected override void LoadDefaultConfig() => PrintWarning("Generating default configuration file...");

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["PositionAdded"] = "You will now teleport to <color=orange>{0}</color> on disconnect.",
                ["PositionRemoved1"] = "You will now teleport under ground on disconnect.",
                ["PositionRemoved2"] = "You will now teleport to <color=orange>{0}</color> on disconnect.",
                ["SavingInventory"] = "Your inventory will be saved on disconnect and restored when you wake up.",
                ["NotSavingInventory"] = "Your inventory will no longer be saved.",
                ["DisconnectTeleportSet"] = "{0} set <x y z> - sets your log out position. can specify coordinates <color=orange>{1}</color>",
                ["DisconnectTeleportReset"] = "{0} reset - resets your log out position to be underground unless a position is configured in the config file",
                ["OutOfBounds"] = "The specified coordinates are not within the allowed boundaries of the map.",
                ["FlyDisabled"] = "You switched NoClip off!",
                ["FlyEnabled"] = "You switched NoClip on!",
                ["GodDisabled"] = "You switched GodMode off!",
                ["GodEnabled"] = "You switched GodMode on!",
                ["NoPermission"] = "You do not have permission to use this command.",
                ["PlayerNotFound"] = "Player not found.",
                ["IpUnavailable"] = "No IP could be resolved for that target.",
                ["InvalidDuration"] = "Invalid duration. Use formats like 30m, 2h, 7d, 1w.",
                ["KickUsage"] = "Usage: {0} <name/steamid> [reason]",
                ["BanUsage"] = "Usage: {0} <name/steamid/ip> [duration] [reason]",
                ["BanIpUsage"] = "Usage: {0} <name/steamid/ip> [duration] [reason]",
                ["UnbanUsage"] = "Usage: {0} <name/steamid/ip>",
                ["KickSuccess"] = "You kicked <color=orange>{0}</color>.",
                ["BanSuccess"] = "You permanently banned <color=orange>{0}</color>.",
                ["TempBanSuccess"] = "You banned <color=orange>{0}</color> for <color=orange>{1}</color>.",
                ["BanIpSuccess"] = "You banned <color=orange>{0}</color> and their IP <color=orange>{1}</color>.",
                ["TempBanIpSuccess"] = "You banned <color=orange>{0}</color> and their IP <color=orange>{1}</color> for <color=orange>{2}</color>.",
                ["IpOnlyBanSuccess"] = "You banned IP <color=orange>{0}</color>.",
                ["TempIpOnlyBanSuccess"] = "You banned IP <color=orange>{0}</color> for <color=orange>{1}</color>.",
                ["IpAndAccountsBanSuccess"] = "You banned IP <color=orange>{0}</color> and also native banned <color=orange>{1}</color> known account(s) on that IP.",
                ["TempIpAndAccountsBanSuccess"] = "You banned IP <color=orange>{0}</color> for <color=orange>{1}</color> and also native banned <color=orange>{2}</color> known account(s) on that IP.",
                ["UnbanPlayerSuccess"] = "You unbanned player/SteamID <color=orange>{0}</color>.",
                ["UnbanIpSuccess"] = "You unbanned IP <color=orange>{0}</color>.",
                ["UnbanPlayerAndIpSuccess"] = "You unbanned player/SteamID <color=orange>{0}</color> and their linked IP <color=orange>{1}</color>.",
                ["NotBanned"] = "That target is not banned.",
                ["AccountBanLinePermanent"] = "Account ban: permanent.",
                ["AccountBanLineTemporary"] = "Account ban: temporary, {0} remaining.",
                ["IpBanLinePermanent"] = "IP ban: permanent.",
                ["IpBanLineTemporary"] = "IP ban: temporary, {0} remaining.",
                ["BanReasonLine"] = "Reason: {0}",
                ["BanReasonNoneLine"] = "Reason: not specified.",
                ["LoginBanAccountOnly"] = "Your account is banned from this server.",
                ["LoginBanIpOnly"] = "The IP address used by this connection is banned from this server.",
                ["LoginBanAccountAndIp"] = "Both your account and the IP address used by this connection are banned from this server.",
                ["GlobalKickAnnouncement"] = "<color=orange>{0}</color> a primit kick de pe server. Motiv: <color=orange>{1}</color>",
                ["GlobalBanAnnouncement"] = "<color=orange>{0}</color> a primit ban permanent pe server. Motiv: <color=orange>{1}</color>",
                ["GlobalTempBanAnnouncement"] = "<color=orange>{0}</color> a primit ban pentru <color=orange>{1}</color>. Motiv: <color=orange>{2}</color>",
                ["GlobalIpOnlyBanAnnouncement"] = "IP-ul <color=orange>{0}</color> a fost banat permanent pe server. Motiv: <color=orange>{1}</color>",
                ["GlobalTempIpOnlyBanAnnouncement"] = "IP-ul <color=orange>{0}</color> a fost banat pentru <color=orange>{1}</color>. Motiv: <color=orange>{2}</color>",
                ["GlobalBanIpAnnouncement"] = "<color=orange>{0}</color> și IP-ul său <color=orange>{1}</color> au fost banate permanent. Motiv: <color=orange>{2}</color>",
                ["GlobalTempBanIpAnnouncement"] = "<color=orange>{0}</color> și IP-ul său <color=orange>{1}</color> au fost banate pentru <color=orange>{2}</color>. Motiv: <color=orange>{3}</color>",
                ["GlobalUnbanAnnouncement"] = "<color=orange>{0}</color> a primit unban pe server.",
                ["GlobalUnbanIpAnnouncement"] = "IP-ul <color=orange>{0}</color> a primit unban pe server.",
                ["GlobalUnbanPlayerAndIpAnnouncement"] = "<color=orange>{0}</color> și IP-ul său <color=orange>{1}</color> au primit unban pe server.",
                ["BanListUsage"] = "Usage: {0} [all/players/ips] [page]",
                ["BanListEmpty"] = "There are no banned entries to display.",
                ["BanListHeader"] = "Ban list (<color=orange>{0}</color>) - page <color=orange>{1}</color>/<color=orange>{2}</color>:",
                ["BanListEntryPermanent"] = "<color=orange>{0}.</color> [{1}] <color=orange>{2}</color> - permanent - Reason: <color=orange>{3}</color>",
                ["BanListEntryTemporary"] = "<color=orange>{0}.</color> [{1}] <color=orange>{2}</color> - <color=orange>{3}</color> remaining - Reason: <color=orange>{4}</color>",
                ["TargetProtectedFromKick"] = "<color=orange>{0}</color> is protected from kick.",
                ["TargetProtectedFromBan"] = "<color=orange>{0}</color> is protected from ban.",
                ["TargetProtectedFromBanIp"] = "<color=orange>{0}</color> is protected from banip.",
                ["PlayersOnly"] = "Command '{0}' can only be used by a player",
                ["UsageSpawn"] = "Usage: {0} <entity>",
                ["UsageSpawnTo"] = "Usage: {0} <name/steamid> <entity>",
                ["UsageSpawnAll"] = "Usage: {0} <entity>",
                ["UsageGiveSelf"] = "Usage: {0} <item> [amount]",
                ["UsageGiveTo"] = "Usage: {0} <name/steamid> <item> [amount]",
                ["UsageGiveCombined"] = "Usage: {0} <item> [amount] or {0} <name/steamid> <item> [amount]",
                ["UsageGiveAll"] = "Usage: {0} <item> [amount]",
                ["EntityNotFound"] = "Could not find any entity by name, alias, token, or prefab <color=orange>{0}</color>",
                ["EntityBlacklisted"] = "<color=orange>{0}</color> is blacklisted",
                ["EntityAliasInvalid"] = "Alias <color=orange>{0}</color> points to an invalid or outdated prefab path: <color=orange>{1}</color>",
                ["EntityPrefabInvalid"] = "Prefab path <color=orange>{0}</color> is not valid in the current Rust build or is not spawnable through this command",
                ["EntityMultipleFound"] = "Multiple entities were found, please specify: {0}",
                ["SpawnFail"] = "Could not spawn entity <color=orange>{0}</color>",
                ["SpawnSuccess"] = "Spawned entity <color=orange>{0}</color>",
                ["SpawnToFail"] = "Could not spawn entity <color=orange>{0}</color> for <color=orange>{1}</color>",
                ["SpawnToSuccess"] = "Spawned entity <color=orange>{0}</color> for <color=orange>{1}</color>",
                ["SpawnAllFail"] = "Could not spawn entity <color=orange>{0}</color> for <color=orange>{1}</color> player(s)",
                ["SpawnAllSuccess"] = "Spawned entity <color=orange>{0}</color> for <color=orange>{1}</color> player(s)",
                ["NoPlayersConnected"] = "There are no players connected",
                ["NoPlayersFound"] = "No players found with name or SteamID <color=orange>{0}</color>",
                ["NoEntityLookedAt"] = "You are not looking at a valid entity",
                ["DespawnSuccess"] = "Entity <color=orange>{0}</color> removed successfully",
                ["CannotDespawnPlayer"] = "You cannot despawn a player",
                ["ItemNotFound"] = "Could not find item <color=orange>{0}</color>",
                ["ItemBlacklisted"] = "Item <color=orange>{0}</color> is blacklisted",
                ["InvalidAmount"] = "Invalid amount",
                ["GiveSuccess"] = "Given <color=orange>{0}</color> x<color=orange>{1}</color>",
                ["GiveFail"] = "Could not give <color=orange>{0}</color> x<color=orange>{1}</color>",
                ["GiveToSuccess"] = "Given <color=orange>{0}</color> x<color=orange>{1}</color> to <color=orange>{2}</color>",
                ["GiveToFail"] = "Could not give <color=orange>{0}</color> x<color=orange>{1}</color> to <color=orange>{2}</color>",
                ["GiveAllSuccess"] = "Given <color=orange>{0}</color> x<color=orange>{1}</color> to <color=orange>{2}</color> player(s)",
                ["GiveAllFail"] = "Could not give <color=orange>{0}</color> x<color=orange>{1}</color> to any player"
            }, this);
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["PositionAdded"] = "Te vei teleporta la <color=orange>{0}</color> odată cu deconectarea.",
                ["PositionRemoved1"] = "Te vei teleporta sub pământ odată cu deconectarea.",
                ["PositionRemoved2"] = "Te vei teleporta la <color=orange>{0}</color> odată cu deconectarea.",
                ["SavingInventory"] = "Inventarul tău va fi salvat la deconectare și recuperat la reconectare.",
                ["NotSavingInventory"] = "Inventarul tău nu va mai fi salvat.",
                ["DisconnectTeleportSet"] = "{0} set <x y z> - setează poziția ta de deconectare. poți specifica coordonatele <color=orange>{1}</color>",
                ["DisconnectTeleportReset"] = "{0} reset - resetează poziția ta de deconectare pentru a fi sub pământ, doar dacă o poziție nu este configurată într-o filă de configurare",
                ["OutOfBounds"] = "Coordonatele specificate nu se încadrează în limitele hărții.",
                ["FlyDisabled"] = "Ai dezactivat NoClip-ul!!",
                ["FlyEnabled"] = "Ai activat NoClip-ul!",
                ["GodDisabled"] = "Ai dezactivat GodMode-ul!",
                ["GodEnabled"] = "Ai activat GodMode-ul!",
                ["NoPermission"] = "Nu ai permisiunea de a folosi această comandă.",
                ["PlayerNotFound"] = "Jucătorul nu a fost găsit.",
                ["IpUnavailable"] = "Nu s-a putut rezolva niciun IP pentru acea țintă.",
                ["InvalidDuration"] = "Durată invalidă. Folosește formate precum 30m, 2h, 7d, 1w.",
                ["KickUsage"] = "Utilizare: {0} <nume/steamid> [motiv]",
                ["BanUsage"] = "Utilizare: {0} <nume/steamid/ip> [durată] [motiv]",
                ["BanIpUsage"] = "Utilizare: {0} <nume/steamid/ip> [durată] [motiv]",
                ["UnbanUsage"] = "Utilizare: {0} <nume/steamid/ip>",
                ["KickSuccess"] = "I-ai dat kick lui <color=orange>{0}</color>.",
                ["BanSuccess"] = "I-ai dat ban permanent lui <color=orange>{0}</color>.",
                ["TempBanSuccess"] = "I-ai dat ban lui <color=orange>{0}</color> pentru <color=orange>{1}</color>.",
                ["BanIpSuccess"] = "Ai banat jucătorul <color=orange>{0}</color> și IP-ul lui <color=orange>{1}</color>.",
                ["TempBanIpSuccess"] = "Ai banat jucătorul <color=orange>{0}</color> și IP-ul lui <color=orange>{1}</color> pentru <color=orange>{2}</color>.",
                ["IpOnlyBanSuccess"] = "Ai banat IP-ul <color=orange>{0}</color>.",
                ["TempIpOnlyBanSuccess"] = "Ai banat IP-ul <color=orange>{0}</color> pentru <color=orange>{1}</color>.",
                ["IpAndAccountsBanSuccess"] = "Ai banat IP-ul <color=orange>{0}</color> și ai dat native ban la <color=orange>{1}</color> cont(uri) cunoscute de pe acel IP.",
                ["TempIpAndAccountsBanSuccess"] = "Ai banat IP-ul <color=orange>{0}</color> pentru <color=orange>{1}</color> și ai dat native ban la <color=orange>{2}</color> cont(uri) cunoscute de pe acel IP.",
                ["UnbanPlayerSuccess"] = "I-ai scos banul lui <color=orange>{0}</color>.",
                ["UnbanIpSuccess"] = "Ai scos banul IP-ului <color=orange>{0}</color>.",
                ["UnbanPlayerAndIpSuccess"] = "I-ai scos banul lui <color=orange>{0}</color> și IP-ului asociat <color=orange>{1}</color>.",
                ["NotBanned"] = "Ținta respectivă nu este banată.",
                ["AccountBanLinePermanent"] = "Ban pe cont: permanent.",
                ["AccountBanLineTemporary"] = "Ban pe cont: temporar, au rămas {0}.",
                ["IpBanLinePermanent"] = "Ban pe IP: permanent.",
                ["IpBanLineTemporary"] = "Ban pe IP: temporar, au rămas {0}.",
                ["BanReasonLine"] = "Motiv: {0}",
                ["BanReasonNoneLine"] = "Motiv: nespecificat.",
                ["LoginBanAccountOnly"] = "Contul tău este banat pe acest server.",
                ["LoginBanIpOnly"] = "IP-ul folosit de această conexiune este banat pe acest server.",
                ["LoginBanAccountAndIp"] = "Atât contul tău, cât și IP-ul folosit de această conexiune sunt banate pe acest server.",
                ["GlobalKickAnnouncement"] = "<color=orange>{0}</color> a primit kick de pe server. Motiv: <color=orange>{1}</color>",
                ["GlobalBanAnnouncement"] = "<color=orange>{0}</color> a primit ban permanent pe server. Motiv: <color=orange>{1}</color>",
                ["GlobalTempBanAnnouncement"] = "<color=orange>{0}</color> a primit ban pentru <color=orange>{1}</color>. Motiv: <color=orange>{2}</color>",
                ["GlobalIpOnlyBanAnnouncement"] = "IP-ul <color=orange>{0}</color> a fost banat permanent pe server. Motiv: <color=orange>{1}</color>",
                ["GlobalTempIpOnlyBanAnnouncement"] = "IP-ul <color=orange>{0}</color> a fost banat pentru <color=orange>{1}</color>. Motiv: <color=orange>{2}</color>",
                ["GlobalBanIpAnnouncement"] = "<color=orange>{0}</color> și IP-ul său <color=orange>{1}</color> au fost banate permanent. Motiv: <color=orange>{2}</color>",
                ["GlobalTempBanIpAnnouncement"] = "<color=orange>{0}</color> și IP-ul său <color=orange>{1}</color> au fost banate pentru <color=orange>{2}</color>. Motiv: <color=orange>{3}</color>",
                ["GlobalUnbanAnnouncement"] = "<color=orange>{0}</color> a primit unban pe server.",
                ["GlobalUnbanIpAnnouncement"] = "IP-ul <color=orange>{0}</color> a primit unban pe server.",
                ["GlobalUnbanPlayerAndIpAnnouncement"] = "<color=orange>{0}</color> și IP-ul său <color=orange>{1}</color> au primit unban pe server.",
                ["BanListUsage"] = "Utilizare: {0} [all/players/ips] [pagină]",
                ["BanListEmpty"] = "Nu există niciun ban activ de afișat.",
                ["BanListHeader"] = "Lista de banuri (<color=orange>{0}</color>) - pagina <color=orange>{1}</color>/<color=orange>{2}</color>:",
                ["BanListEntryPermanent"] = "<color=orange>{0}.</color> [{1}] <color=orange>{2}</color> - permanent - Motiv: <color=orange>{3}</color>",
                ["BanListEntryTemporary"] = "<color=orange>{0}.</color> [{1}] <color=orange>{2}</color> - <color=orange>{3}</color> rămas - Motiv: <color=orange>{4}</color>",
                ["TargetProtectedFromKick"] = "<color=orange>{0}</color> este protejat de kick.",
                ["TargetProtectedFromBan"] = "<color=orange>{0}</color> este protejat de ban.",
                ["TargetProtectedFromBanIp"] = "<color=orange>{0}</color> este protejat de banip.",
                ["PlayersOnly"] = "Comanda '{0}' poate fi folosită doar de un jucător",
                ["UsageSpawn"] = "Utilizare: {0} <entitate>",
                ["UsageSpawnTo"] = "Utilizare: {0} <nume/steamid> <entitate>",
                ["UsageSpawnAll"] = "Utilizare: {0} <entitate>",
                ["UsageGiveSelf"] = "Utilizare: {0} <item> [cantitate]",
                ["UsageGiveTo"] = "Utilizare: {0} <nume/steamid> <item> [cantitate]",
                ["UsageGiveCombined"] = "Utilizare: {0} <item> [cantitate] sau {0} <nume/steamid> <item> [cantitate]",
                ["UsageGiveAll"] = "Utilizare: {0} <item> [cantitate]",
                ["EntityNotFound"] = "Nu am găsit nicio entitate după nume, alias, token sau prefab <color=orange>{0}</color>",
                ["EntityBlacklisted"] = "<color=orange>{0}</color> este în blacklist",
                ["EntityAliasInvalid"] = "Aliasul <color=orange>{0}</color> indică spre un path de prefab invalid sau învechit: <color=orange>{1}</color>",
                ["EntityPrefabInvalid"] = "Path-ul de prefab <color=orange>{0}</color> nu este valid în buildul curent Rust sau nu poate fi spawnat prin această comandă",
                ["EntityMultipleFound"] = "Au fost găsite mai multe entități, specifică mai clar: {0}",
                ["SpawnFail"] = "Nu am putut spawna entitatea <color=orange>{0}</color>",
                ["SpawnSuccess"] = "Entitatea <color=orange>{0}</color> a fost spawnată",
                ["SpawnToFail"] = "Nu am putut spawna entitatea <color=orange>{0}</color> pentru <color=orange>{1}</color>",
                ["SpawnToSuccess"] = "Entitatea <color=orange>{0}</color> a fost spawnată pentru <color=orange>{1}</color>",
                ["SpawnAllFail"] = "Nu am putut spawna entitatea <color=orange>{0}</color> pentru <color=orange>{1}</color> jucător(i)",
                ["SpawnAllSuccess"] = "Entitatea <color=orange>{0}</color> a fost spawnată pentru <color=orange>{1}</color> jucător(i)",
                ["NoPlayersConnected"] = "Nu există jucători conectați",
                ["NoPlayersFound"] = "Nu am găsit jucători cu numele sau SteamID-ul <color=orange>{0}</color>",
                ["NoEntityLookedAt"] = "Nu te uiți la nicio entitate validă",
                ["DespawnSuccess"] = "Entitatea <color=orange>{0}</color> a fost ștearsă cu succes",
                ["CannotDespawnPlayer"] = "Nu poți șterge un jucător",
                ["ItemNotFound"] = "Nu am găsit itemul <color=orange>{0}</color>",
                ["ItemBlacklisted"] = "Itemul <color=orange>{0}</color> este în blacklist",
                ["InvalidAmount"] = "Cantitate invalidă",
                ["GiveSuccess"] = "Ai primit <color=orange>{0}</color> x<color=orange>{1}</color>",
                ["GiveFail"] = "Nu am putut da <color=orange>{0}</color> x<color=orange>{1}</color>",
                ["GiveToSuccess"] = "Ai dat <color=orange>{0}</color> x<color=orange>{1}</color> lui <color=orange>{2}</color>",
                ["GiveToFail"] = "Nu am putut da <color=orange>{0}</color> x<color=orange>{1}</color> lui <color=orange>{2}</color>",
                ["GiveAllSuccess"] = "Ai dat <color=orange>{0}</color> x<color=orange>{1}</color> la <color=orange>{2}</color> jucător(i)",
                ["GiveAllFail"] = "Nu am putut da <color=orange>{0}</color> x<color=orange>{1}</color> niciunui jucător"
            }, this, "ro");
        }

        private void InitConfig()
        {
            ConfigChanged = false;

            PluginConfigData config = BuildConfig();
            ApplyConfig(config);

            if (ConfigChanged)
            {
                PrintWarning("Updated configuration file with new/changed values.");
                Config.WriteObject(config, true);
            }
        }

        private PluginConfigData BuildConfig()
        {
            PluginConfigData config = new PluginConfigData();

            config.General.DefaultTeleportToPositionOnDisconnect = ReadConfigValue("(0, 0, 0)",
                new[] { "General", "Default Teleport To Position On Disconnect" },
                new[] { "Settings", "Default Teleport To Position On Disconnect" });

            config.General.DisabledPlayerConsoleCommands = ReadConfigValue(new List<string> { },
                new[] { "General", "Disabled Player Console Commands" }) ?? new List<string> { };

            config.General.DisabledChatCommands = ReadConfigValue(new List<string> { },
                new[] { "General", "Disabled Chat Commands" }) ?? new List<string> { };

            config.General.WipeSavedInventoriesOnMapWipe = ReadConfigValue(false,
                new[] { "General", "Wipe Saved Inventories On Map Wipe" },
                new[] { "Settings", "Wipe Saved Inventories On Map Wipe" });

            config.General.WipePlayersSettingsOnMapWipe = ReadConfigValue(false,
                new[] { "General", "Wipe Players Settings On Map Wipe" },
                new[] { "Settings", "Wipe Players Settings On Map Wipe" });

            config.General.GlobalServerMessagesIconSteamIdOrGroupId = ReadConfigValue(string.Empty,
                new[] { "General", "Global Server Messages Icon Steam ID Or Group ID" });

            config.NoClipAndGod.EnablePersistentNoClip = ReadConfigValue(false,
                new[] { "NoClip & God", "Enable Persistent NoClip" },
                new[] { "Settings", "Enable Persistent NoClip" });

            config.NoClipAndGod.EnablePersistentGodMode = ReadConfigValue(false,
                new[] { "NoClip & God", "Enable Persistent GodMode" },
                new[] { "Settings", "Enable Persistent GodMode" });

            config.Moderation.DefaultKickReason = ReadConfigValue("Unknown reason.",
                new[] { "Moderation", "Default Kick Reason" },
                new[] { "Settings", "Default Kick Reason" });

            config.Moderation.DefaultBanReason = ReadConfigValue("Unknown reason.",
                new[] { "Moderation", "Default Ban Reason" },
                new[] { "Settings", "Default Ban Reason" });

            config.Moderation.BroadcastKickToGlobalChat = ReadConfigValue(true,
                new[] { "Moderation", "Broadcast Kick To Global Chat" },
                new[] { "Settings", "Broadcast Kick To Global Chat" });

            config.Moderation.BroadcastBanToGlobalChat = ReadConfigValue(true,
                new[] { "Moderation", "Broadcast Ban To Global Chat" },
                new[] { "Settings", "Broadcast Ban To Global Chat" });

            config.Moderation.LogKickEventsToConsole = ReadConfigValue(true,
                new[] { "Moderation", "Log Kick Events To Console" },
                new[] { "Settings", "Log Kick Events To Console" });

            config.Moderation.LogBanEventsToConsole = ReadConfigValue(true,
                new[] { "Moderation", "Log Ban Events To Console" },
                new[] { "Settings", "Log Ban Events To Console" });

            config.Moderation.BanListPageSize = ReadConfigValue(10,
                new[] { "Moderation", "BanList Page Size" },
                new[] { "Settings", "BanList Page Size" });

            config.GiveAndSpawn.SpawnDistanceInFrontOfPlayer = ReadConfigValue(4f,
                new[] { "Give & Spawn", "Spawn Distance In Front Of Player" });

            config.GiveAndSpawn.UseCrosshairRaycastForSpawnPosition = ReadConfigValue(false,
                new[] { "Give & Spawn", "Use Crosshair Raycast For Spawn Position" });

            config.GiveAndSpawn.MaximumCrosshairSpawnDistance = ReadConfigValue(25f,
                new[] { "Give & Spawn", "Maximum Crosshair Spawn Distance" });

            config.GiveAndSpawn.RaiseSpawnPositionOnYAxis = ReadConfigValue(0.25f,
                new[] { "Give & Spawn", "Raise Spawn Position On Y Axis" });

            config.GiveAndSpawn.AllowDirectPrefabPaths = ReadConfigValue(true,
                new[] { "Give & Spawn", "Allow Direct Prefab Paths" });

            config.GiveAndSpawn.AllowPartialPrefabSearch = ReadConfigValue(true,
                new[] { "Give & Spawn", "Allow Partial Prefab Search" });

            config.GiveAndSpawn.MaximumDespawnDistance = ReadConfigValue(25f,
                new[] { "Give & Spawn", "Maximum Despawn Distance" });

            config.GiveAndSpawn.LogGiveCommandsToConsole = ReadConfigValue(true,
                new[] { "Give & Spawn", "Log Give Commands To Console" });

            config.GiveAndSpawn.LogSpawnCommandsToConsole = ReadConfigValue(true,
                new[] { "Give & Spawn", "Log Spawn Commands To Console" });

            config.GiveAndSpawn.SpawnAliasBlacklist = ReadConfigValue(new List<string>(),
                new[] { "Give & Spawn", "Spawn Alias Blacklist" }) ?? new List<string>();

            config.GiveAndSpawn.GiveItemBlacklist = ReadConfigValue(new List<string>(),
                new[] { "Give & Spawn", "Give Item Blacklist" }) ?? new List<string>();

            config.GiveAndSpawn.DefaultAliases = ReadConfigValue(GetDefaultAliases(),
                new[] { "Give & Spawn", "Default Aliases" }) ?? GetDefaultAliases();

            if (config.GiveAndSpawn.DefaultAliases.Count == 0)
                config.GiveAndSpawn.DefaultAliases = GetDefaultAliases();

            return config;
        }

        private void ApplyConfig(PluginConfigData config)
        {
            defaultPos = (config.General.DefaultTeleportToPositionOnDisconnect ?? "(0, 0, 0)").ToVector3();
            wipeItems = config.General.WipeSavedInventoriesOnMapWipe;
            wipeSettings = config.General.WipePlayersSettingsOnMapWipe;
            globalServerMessagesIconSteamIdOrGroupId = config.General.GlobalServerMessagesIconSteamIdOrGroupId ?? string.Empty;
            disabledPlayerConsoleCommands = new HashSet<string>(
                (config.General.DisabledPlayerConsoleCommands ?? new List<string>())
                    .Select(NormalizeDisabledPlayerConsoleCommand)
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase
            );
            disabledChatCommands = new HashSet<string>(
                (config.General.DisabledChatCommands ?? new List<string>())
                    .Select(NormalizeDisabledChatCommand)
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase
            );

            persistentNoClip = config.NoClipAndGod.EnablePersistentNoClip;
            persistentGodMode = config.NoClipAndGod.EnablePersistentGodMode;

            defaultKickReason = string.IsNullOrWhiteSpace(config.Moderation.DefaultKickReason) ? "Unknown reason." : config.Moderation.DefaultKickReason;
            defaultBanReason = string.IsNullOrWhiteSpace(config.Moderation.DefaultBanReason) ? "Unknown reason." : config.Moderation.DefaultBanReason;
            broadcastKickToChat = config.Moderation.BroadcastKickToGlobalChat;
            broadcastBanToChat = config.Moderation.BroadcastBanToGlobalChat;
            logKickToConsole = config.Moderation.LogKickEventsToConsole;
            logBanToConsole = config.Moderation.LogBanEventsToConsole;
            banListPageSize = config.Moderation.BanListPageSize <= 0 ? 10 : config.Moderation.BanListPageSize;

            spawnDistance = config.GiveAndSpawn.SpawnDistanceInFrontOfPlayer;
            useCrosshairRaycast = config.GiveAndSpawn.UseCrosshairRaycastForSpawnPosition;
            maxCrosshairDistance = config.GiveAndSpawn.MaximumCrosshairSpawnDistance;
            spawnHeightOffset = config.GiveAndSpawn.RaiseSpawnPositionOnYAxis;
            allowDirectPrefabPaths = config.GiveAndSpawn.AllowDirectPrefabPaths;
            allowPartialPrefabSearch = config.GiveAndSpawn.AllowPartialPrefabSearch;
            maxDespawnDistance = config.GiveAndSpawn.MaximumDespawnDistance;
            logGiveToConsole = config.GiveAndSpawn.LogGiveCommandsToConsole;
            logSpawnToConsole = config.GiveAndSpawn.LogSpawnCommandsToConsole;
            spawnAliasBlacklist = new List<string>(config.GiveAndSpawn.SpawnAliasBlacklist ?? new List<string>());
            giveItemBlacklist = new List<string>(config.GiveAndSpawn.GiveItemBlacklist ?? new List<string>());
            giveSpawnAliases = new Dictionary<string, string>(config.GiveAndSpawn.DefaultAliases ?? GetDefaultAliases(), StringComparer.OrdinalIgnoreCase);
        }

        private T ReadConfigValue<T>(T defaultVal, string[] newPath, params string[][] legacyPaths)
        {
            object data = Config.Get(newPath);
            if (data != null)
                return ConvertConfigValue<T>(data, defaultVal);

            foreach (string[] legacyPath in legacyPaths)
            {
                data = Config.Get(legacyPath);
                if (data != null)
                {
                    ConfigChanged = true;
                    return ConvertConfigValue<T>(data, defaultVal);
                }
            }

            ConfigChanged = true;
            return defaultVal;
        }

        private T ConvertConfigValue<T>(object data, T defaultVal)
        {
            if (data == null)
                return defaultVal;

            try
            {
                return Config.ConvertValue<T>(data);
            }
            catch
            {
                try
                {
                    return JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(data));
                }
                catch
                {
                    return defaultVal;
                }
            }
        }

        ////////////////////////////////////////////////////////////
        // Plugin Hooks
        ////////////////////////////////////////////////////////////

        [HookMethod(nameof(API_ToggleNoClip))]
        public void API_ToggleNoClip(BasePlayer player)
        {
            ToggleNoClip(player);
        }

        [HookMethod(nameof(API_ToggleGodMode))]
        public void API_ToggleGodMode(BasePlayer player)
        {
            ToggleGodMode(player);
        }

        [HookMethod(nameof(API_GetDisconnectTeleportPos))]
        public object API_GetDisconnectTeleportPos(string userid)
        {
            BasePlayer player = BasePlayer.FindByID(Convert.ToUInt64(userid));

            if (player == null)
                return null;

            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return null;

            return user.Teleport;
        }

        [HookMethod(nameof(API_GetSaveInventoryStatus))]
        public bool API_GetSaveInventoryStatus(string userid)
        {
            BasePlayer player = BasePlayer.FindByID(Convert.ToUInt64(userid));

            if (player == null)
                return false;

            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return false;

            return user.SaveInventory;
        }

        [HookMethod(nameof(API_GetUnderTerrainStatus))]
        public bool API_GetUnderTerrainStatus(string userid)
        {
            BasePlayer player = BasePlayer.FindByID(Convert.ToUInt64(userid));

            if (player == null)
                return false;

            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return false;

            return user.UnderTerrain;
        }

        [HookMethod(nameof(API_GetNoClipStatus))]
        public bool API_GetNoClipStatus(string userid)
        {
            BasePlayer player = BasePlayer.FindByID(Convert.ToUInt64(userid));

            if (player == null)
                return false;

            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return false;

            return user.NoClip;
        }

        [HookMethod(nameof(API_GetGodModeStatus))]
        public bool API_GetGodModeStatus(string userid)
        {
            BasePlayer player = BasePlayer.FindByID(Convert.ToUInt64(userid));

            if (player == null)
                return false;

            PlayerInfo user = LoadPlayerInfo(player);

            if (user == null)
                return false;

            return user.GodMode;
        }

        [HookMethod(nameof(API_GetBanStatus))]
        public bool API_GetBanStatus(string steamId, string ip = null)
        {
            return TryGetActiveBan(steamId, ip, out _);
        }

        [HookMethod(nameof(API_GetBanInfo))]
        public object API_GetBanInfo(string steamId, string ip = null)
        {
            if (!TryGetActiveBan(steamId, ip, out var ban))
                return null;

            return new
            {
                Target = ban.Target,
                DisplayName = ban.DisplayName,
                Reason = ban.Reason,
                Source = ban.Source,
                CreatedAt = ban.CreatedAt,
                ExpiresAt = ban.ExpiresAt
            };
        }

        [HookMethod(nameof(API_Kick))]
        public bool API_Kick(BasePlayer actor, string targetInput, string reason = null)
        {
            if (string.IsNullOrWhiteSpace(targetInput))
                return false;

            var target = FindOnlinePlayer(targetInput);
            if (target == null)
            {
                ReplyPlayerLocalized(actor, "PlayerNotFound");
                return false;
            }

            if (permission.UserHasPermission(target.UserIDString, permPreventKick))
            {
                ReplyPlayerLocalized(actor, "TargetProtectedFromKick", target.displayName);
                return false;
            }

            if (string.IsNullOrWhiteSpace(reason))
                reason = defaultKickReason;

            target.IPlayer.Kick(reason);
            NotifyKickEvent(target.displayName, target.UserIDString, reason, true);

            if (actor != null)
                ReplyPlayerLocalized(actor, "KickSuccess", target.displayName);

            return true;
        }

        [HookMethod(nameof(API_Ban))]
        public bool API_Ban(BasePlayer actor, string targetInput, string durationText = null, string reason = null, string source = "API")
        {
            if (string.IsNullOrWhiteSpace(targetInput))
                return false;

            if (string.IsNullOrWhiteSpace(reason))
                reason = defaultBanReason;

            TimeSpan? duration = null;

            if (!string.IsNullOrWhiteSpace(durationText))
            {
                if (!TryParseDuration(durationText, out var parsedDuration))
                {
                    ReplyPlayerLocalized(actor, "InvalidDuration");
                    return false;
                }

                duration = parsedDuration;
            }

            if (IsIpAddress(targetInput))
            {
                AddIpBan(targetInput, targetInput, reason, duration, source);
                KickAllPlayersByIp(targetInput);
                NotifyBanEvent(targetInput, null, targetInput, duration, reason, false, true, 0, true);

                if (actor != null)
                {
                    if (duration.HasValue)
                        ReplyPlayerLocalized(actor, "TempIpOnlyBanSuccess", targetInput, FormatDuration(duration.Value));
                    else
                        ReplyPlayerLocalized(actor, "IpOnlyBanSuccess", targetInput);
                }

                return true;
            }

            if (!TryResolveSteamId(targetInput, out var steamId, out var displayName, out var ip, out var onlinePlayer))
            {
                ReplyPlayerLocalized(actor, "PlayerNotFound");
                return false;
            }

            if (permission.UserHasPermission(steamId, permPreventBan))
            {
                ReplyPlayerLocalized(actor, "TargetProtectedFromBan", displayName ?? steamId);
                return false;
            }

            AddOrUpdatePlayerBan(steamId, displayName, reason, duration, source);
            SetNativeBan(steamId, displayName ?? steamId, reason, duration);

            onlinePlayer?.IPlayer?.Kick(BuildLoginBanMessage(steamId, ip, onlinePlayer?.UserIDString));
            NotifyBanEvent(displayName ?? steamId, steamId, ip, duration, reason, false, false, 0, true);

            if (actor != null)
            {
                if (duration.HasValue)
                    ReplyPlayerLocalized(actor, "TempBanSuccess", displayName ?? steamId, FormatDuration(duration.Value));
                else
                    ReplyPlayerLocalized(actor, "BanSuccess", displayName ?? steamId);
            }

            return true;
        }

        [HookMethod(nameof(API_Unban))]
        public bool API_Unban(BasePlayer actor, string targetInput)
        {
            if (string.IsNullOrWhiteSpace(targetInput))
                return false;

            if (!TryResolveUnbanTarget(targetInput, out var steamId, out var ip))
            {
                ReplyPlayerLocalized(actor, "NotBanned");
                return false;
            }

            if (!string.IsNullOrWhiteSpace(ip))
            {
                bool removedIpBan = moderationData.IpBans.Remove(ip);
                if (!removedIpBan)
                {
                    ReplyPlayerLocalized(actor, "NotBanned");
                    return false;
                }

                SaveModerationData();
                NotifyUnbanEvent(ip, true, true);

                if (actor != null)
                    ReplyPlayerLocalized(actor, "UnbanIpSuccess", ip);

                return true;
            }

            if (!string.IsNullOrWhiteSpace(steamId))
            {
                moderationData.PlayerBans.TryGetValue(steamId, out var banRecordBeforeRemove);

                string displayName =
                    banRecordBeforeRemove != null &&
                    !string.IsNullOrWhiteSpace(banRecordBeforeRemove.DisplayName) &&
                    !string.Equals(banRecordBeforeRemove.DisplayName, steamId, StringComparison.OrdinalIgnoreCase)
                        ? banRecordBeforeRemove.DisplayName
                        : GetKnownDisplayName(steamId);

                bool removedPlayerBan = moderationData.PlayerBans.Remove(steamId);
                bool removedIpBan = false;
                string linkedIp = null;

                if (moderationData.LastKnownIps.TryGetValue(steamId, out linkedIp) && IsValidStoredIp(linkedIp))
                    removedIpBan = moderationData.IpBans.Remove(linkedIp);
                else
                    linkedIp = null;

                if (!removedPlayerBan && !removedIpBan)
                {
                    ReplyPlayerLocalized(actor, "NotBanned");
                    return false;
                }

                if (removedPlayerBan)
                    RemoveNativeBan(steamId);

                SaveModerationData();

                if (removedPlayerBan && removedIpBan)
                    NotifyUnbanPlayerAndIpEvent(displayName, steamId, linkedIp, true);
                else if (removedPlayerBan)
                    NotifyUnbanEvent(displayName, false, true, steamId);
                else if (removedIpBan)
                    NotifyUnbanEvent(linkedIp, true, true);

                if (actor != null)
                {
                    if (removedPlayerBan && removedIpBan)
                        ReplyPlayerLocalized(actor, "UnbanPlayerAndIpSuccess", displayName, linkedIp);
                    else if (removedPlayerBan)
                        ReplyPlayerLocalized(actor, "UnbanPlayerSuccess", displayName);
                    else if (removedIpBan)
                        ReplyPlayerLocalized(actor, "UnbanIpSuccess", linkedIp);
                }

                return true;
            }

            ReplyPlayerLocalized(actor, "NotBanned");
            return false;
        }

        [HookMethod(nameof(API_BanIp))]
        public bool API_BanIp(BasePlayer actor, string targetInput, string durationText = null, string reason = null, string source = "API")
        {
            if (string.IsNullOrWhiteSpace(targetInput))
                return false;

            if (string.IsNullOrWhiteSpace(reason))
                reason = defaultBanReason;

            TimeSpan? duration = null;

            if (!string.IsNullOrWhiteSpace(durationText))
            {
                if (!TryParseDuration(durationText, out var parsedDuration))
                {
                    ReplyPlayerLocalized(actor, "InvalidDuration");
                    return false;
                }

                duration = parsedDuration;
            }

            if (IsIpAddress(targetInput))
            {
                AddIpBan(targetInput, targetInput, reason, duration, source);
                KickAllPlayersByIp(targetInput);

                int bannedAccounts = BanKnownAccountsOnIp(targetInput, duration, reason, source);
                NotifyBanEvent(targetInput, null, targetInput, duration, reason, true, true, bannedAccounts, true);

                if (actor != null)
                {
                    if (duration.HasValue)
                        ReplyPlayerLocalized(actor, "TempIpAndAccountsBanSuccess", targetInput, FormatDuration(duration.Value), bannedAccounts);
                    else
                        ReplyPlayerLocalized(actor, "IpAndAccountsBanSuccess", targetInput, bannedAccounts);
                }

                return true;
            }

            if (!TryResolveSteamId(targetInput, out var steamId, out var displayName, out var ip, out var onlinePlayer))
            {
                ReplyPlayerLocalized(actor, "PlayerNotFound");
                return false;
            }

            if (permission.UserHasPermission(steamId, permPreventBanIp))
            {
                ReplyPlayerLocalized(actor, "TargetProtectedFromBanIp", displayName ?? steamId);
                return false;
            }

            AddOrUpdatePlayerBan(steamId, displayName, reason, duration, source);
            SetNativeBan(steamId, displayName ?? steamId, reason, duration);

            if (IsValidStoredIp(ip))
            {
                AddIpBan(ip, displayName ?? ip, reason, duration, source);
                KickAllPlayersByIp(ip);
            }
            else if (onlinePlayer != null)
            {
                onlinePlayer.IPlayer.Kick(BuildLoginBanMessage(steamId, null, onlinePlayer.UserIDString));
            }

            NotifyBanEvent(displayName ?? steamId, steamId, ip, duration, reason, true, false, 0, true);

            string shownIp = IsValidStoredIp(ip) ? ip : "unknown";

            if (actor != null)
            {
                if (duration.HasValue)
                    ReplyPlayerLocalized(actor, "TempBanIpSuccess", displayName ?? steamId, shownIp, FormatDuration(duration.Value));
                else
                    ReplyPlayerLocalized(actor, "BanIpSuccess", displayName ?? steamId, shownIp);
            }

            return true;
        }
    }
}