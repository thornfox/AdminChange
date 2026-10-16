using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Plugins;
using UnityEngine;

/*
 * AdminChange – Changelog
 *
 * 1.0.1  Switched status framework to AdvancedStatus.
 * 1.0.2  Optimized code, added /radmin_chests.
 * 1.0.3  Configurable groups.
 * 1.0.4  Automatic deactivation timer.
 * 1.0.5  Configurable AdvancedStatus messages.
 * 1.0.6  AdvancedStatus API: CreateBar/DeleteBar with dictionary parameters.
 * 1.0.7  AdvancedStatus 'TimeProgressCounter' countdown.
 * 1.0.8  /radmin_menu executed on (de)activation.
 * 1.0.9  Renamed from AdminStatus to AdminChange.
 * 1.1.0  Compatibility and safety update (Rust Livestock update / Oxide 2.0.x):
 *        - Class name matches file name (AdminChange); one data file name (migration from AdminStatus.json).
 *        - Typed configuration with version and automatic migration of the legacy format.
 *        - Data stores chest IDs as plain numbers (migration of legacy NetworkableId entries).
 *        - Activation is atomic: all four chests are checked first, inventory is rolled back on failure.
 *        - Deactivation never deletes the player's own items when chests are missing (only staff items are removed).
 *        - Staff items are tagged and only tagged items are destroyed when dropped.
 *        - player.userID.Get() / BasePlayer passed to external APIs (AdvancedStatus, Backpacks).
 *        - AdvancedStatus readiness check and OnAdvancedStatusLoaded support.
 *        - RealPVE loot toggle is tracked, so it is only switched off if this plugin switched it on.
 *        - Backpacks gather mode is paused during item transfers.
 *        - Active state, timer and status bar are restored after plugin reload / server restart.
 *        - Map wipe handling (OnNewSave): chest assignments and active modes are reset.
 *        - Permissions adminchange.admin / adminchange.mod (groups still supported).
 *        - /aclear requires admin access; chests cannot be assigned twice or changed while active.
 *        - Skin IDs as ulong; all chat texts via lang (en + de).
 * 1.1.1  RealPVE loot access:
 *        - New admin command /radmin_loot_sync [steamID|_self] flips the RealPVE loot access once
 *          to re-align it when it is out of sync (granted in player mode, revoked in staff mode).
 *        - Every loot toggle is logged to the server console.
 */

namespace Oxide.Plugins
{
    [Info("AdminChange", "ThornFox", "1.1.1")]
    [Description("Toggles admins and moderators between player mode and a temporary staff mode with its own loadout, safe inventory storage and automatic deactivation.")]
    public class AdminChange : RustPlugin
    {
        #region Fields

        [PluginReference] private Plugin AdvancedStatus, AdminRadar, Backpacks, Godmode, Vanish;

        private const string PermAdmin = "adminchange.admin";
        private const string PermMod = "adminchange.mod";

        private const string ModeAdmin = "admin";
        private const string ModeMod = "mod";

        private const string BarIdAdmin = "AdminStatusMode";
        private const string BarIdMod = "ModStatusMode";

        private const string StaffItemTag = "[Staff]";
        private const string LegacyDataName = "AdminStatus";
        private const string LargeBoxPrefab = "assets/prefabs/deployable/large wood storage/box.wooden.large.prefab";
        private const ulong AdminChestSkin = 2872680572UL;

        private const int ChestCount = 4;
        private const int SlotWear = 0;
        private const int SlotMain = 1;
        private const int SlotBelt = 2;
        private const int SlotBackpack = 3;

        private const float LoadoutDelay = 0.5f;
        private const float FollowUpDelay = 1.0f;
        private const double BackupMaxAgeMinutes = 1440d;

        private Configuration _config;
        private StoredData _data;
        private bool _dataLoadFailed;
        private readonly Dictionary<ulong, Timer> _deactivationTimers = new Dictionary<ulong, Timer>();

        private static double Now => Network.TimeEx.currentTimestamp;

        #endregion

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Groups")]
            public GroupSettings Groups;

            [JsonProperty("Auto deactivation time in minutes (0 = disabled)")]
            public int AutoDeactivationMinutes = 30;

            [JsonProperty("Admin mode")]
            public ModeSettings Admin;

            [JsonProperty("Moderator mode")]
            public ModeSettings Mod;

            [JsonProperty("RealPVE integration")]
            public RealPveSettings RealPve;

            [JsonProperty("Chat command executed for the player after (de)activation (empty = disabled)")]
            public string FollowUpChatCommand = "/radmin_menu";

            [JsonProperty("Configuration version")]
            public string Version = "1.1.0";

            public static Configuration CreateDefault()
            {
                var config = new Configuration();
                config.Normalize();
                return config;
            }

            public void Normalize()
            {
                if (Groups == null) Groups = new GroupSettings();
                if (Admin == null) Admin = ModeSettings.DefaultAdmin();
                if (Mod == null) Mod = ModeSettings.DefaultMod();
                Admin.Normalize("ADMIN MODE ACTIVE", "#FF0000");
                Mod.Normalize("MODERATOR MODE ACTIVE", "#800080");
                if (RealPve == null) RealPve = new RealPveSettings();
                if (AutoDeactivationMinutes < 0) AutoDeactivationMinutes = 0;
                if (FollowUpChatCommand == null) FollowUpChatCommand = string.Empty;
            }
        }

        private class GroupSettings
        {
            [JsonProperty("Admin group (alternative to permission adminchange.admin)")]
            public string Admin = "admin";

            [JsonProperty("Moderator group (alternative to permission adminchange.mod)")]
            public string Mod = "mod";

            [JsonProperty("Active admin group")]
            public string AdminActive = "admin_active";

            [JsonProperty("Active moderator group")]
            public string ModActive = "mod_active";
        }

        private class ModeSettings
        {
            [JsonProperty("Status bar text")]
            public string BarText;

            [JsonProperty("Status bar color (HTML hex)")]
            public string BarColor;

            [JsonProperty("Wear items")]
            public List<LoadoutItem> Wear;

            [JsonProperty("Belt items")]
            public List<LoadoutItem> Belt;

            public static ModeSettings DefaultAdmin() => CreateWithDefaultWear("ADMIN MODE ACTIVE", "#FF0000");

            public static ModeSettings DefaultMod() => CreateWithDefaultWear("MODERATOR MODE ACTIVE", "#800080");

            private static ModeSettings CreateWithDefaultWear(string text, string color)
            {
                return new ModeSettings
                {
                    BarText = text,
                    BarColor = color,
                    Wear = new List<LoadoutItem>
                    {
                        new LoadoutItem("metal.plate.torso", 2128463741UL),
                        new LoadoutItem("tshirt", 2585609965UL)
                    },
                    Belt = new List<LoadoutItem>()
                };
            }

            public void Normalize(string defaultText, string defaultColor)
            {
                if (string.IsNullOrEmpty(BarText)) BarText = defaultText;
                if (string.IsNullOrEmpty(BarColor)) BarColor = defaultColor;
                Wear = (Wear ?? new List<LoadoutItem>()).Where(x => x != null && !string.IsNullOrEmpty(x.ShortName)).ToList();
                Belt = (Belt ?? new List<LoadoutItem>()).Where(x => x != null && !string.IsNullOrEmpty(x.ShortName)).ToList();
            }
        }

        private class LoadoutItem
        {
            [JsonProperty("Short name")]
            public string ShortName;

            [JsonProperty("Skin ID")]
            public ulong SkinId;

            [JsonProperty("Amount")]
            public int Amount = 1;

            public LoadoutItem() { }

            public LoadoutItem(string shortName, ulong skinId, int amount = 1)
            {
                ShortName = shortName;
                SkinId = skinId;
                Amount = amount;
            }
        }

        private class RealPveSettings
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Plugin name")]
            public string PluginName = "RealPVE";

            [JsonProperty("Server command that toggles unrestricted loot access ({0} = Steam ID)")]
            public string LootToggleCommand = "adminpve loot {0}";
        }

        protected override void LoadDefaultConfig() => _config = Configuration.CreateDefault();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                if (IsLegacyConfig())
                {
                    _config = MigrateLegacyConfig();
                    PrintWarning($"Legacy configuration migrated. A copy of the old values was written to data/{Name}_LegacyConfigBackup.json.");
                }
                else
                {
                    _config = Config.ReadObject<Configuration>();
                    if (_config == null) throw new JsonException("Configuration is empty.");
                }

                _config.Normalize();
                _config.Version = Version.ToString();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Configuration file is invalid and was NOT overwritten. Default values are used until it is fixed. Error: {ex.Message}");
                _config = Configuration.CreateDefault();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private bool IsLegacyConfig() => Config["Settings"] != null && Config["Configuration version"] == null;

        private Configuration MigrateLegacyConfig()
        {
            var config = Configuration.CreateDefault();
            var settings = Config["Settings"] as Dictionary<string, object>;
            var groups = Config["Groups"] as Dictionary<string, object>;

            Interface.Oxide.DataFileSystem.WriteObject($"{Name}_LegacyConfigBackup", new Dictionary<string, object>
            {
                { "Settings", settings },
                { "Groups", groups }
            });

            if (settings != null)
            {
                config.Admin.Wear = ReadLegacyItems(settings, "Admin Wear List") ?? config.Admin.Wear;
                config.Admin.Belt = ReadLegacyItems(settings, "Admin Belt List") ?? config.Admin.Belt;
                config.Mod.Wear = ReadLegacyItems(settings, "Mod Wear List") ?? config.Mod.Wear;
                config.Mod.Belt = ReadLegacyItems(settings, "Mod Belt List") ?? config.Mod.Belt;
                config.Admin.BarText = ReadLegacyString(settings, "Admin Status Message", config.Admin.BarText);
                config.Mod.BarText = ReadLegacyString(settings, "Mod Status Message", config.Mod.BarText);

                int minutes;
                if (int.TryParse(ReadLegacyString(settings, "Auto Deactivation Time (Minutes)", null), out minutes))
                    config.AutoDeactivationMinutes = minutes;
            }

            if (groups != null)
            {
                config.Groups.Admin = ReadLegacyString(groups, "AdminGroup", config.Groups.Admin);
                config.Groups.Mod = ReadLegacyString(groups, "ModGroup", config.Groups.Mod);
                config.Groups.AdminActive = ReadLegacyString(groups, "AdminActiveGroup", config.Groups.AdminActive);
                config.Groups.ModActive = ReadLegacyString(groups, "ModActiveGroup", config.Groups.ModActive);
            }

            Config.Clear();
            return config;
        }

        private static string ReadLegacyString(Dictionary<string, object> source, string key, string fallback)
        {
            object value;
            return source.TryGetValue(key, out value) && value != null ? value.ToString() : fallback;
        }

        private static List<LoadoutItem> ReadLegacyItems(Dictionary<string, object> source, string key)
        {
            object raw;
            if (!source.TryGetValue(key, out raw)) return null;

            var dict = raw as Dictionary<string, object>;
            if (dict == null) return null;

            var list = new List<LoadoutItem>();
            foreach (var entry in dict)
            {
                ulong skin;
                ulong.TryParse(entry.Value?.ToString(), out skin);
                list.Add(new LoadoutItem(entry.Key, skin, entry.Key == "ammo.rifle.hv" ? 512 : 1));
            }
            return list;
        }

        #endregion

        #region Data

        private class StoredData
        {
            [JsonProperty("Data version")]
            public int DataVersion = 2;

            [JsonProperty("Players")]
            public Dictionary<ulong, PlayerInfo> Players = new Dictionary<ulong, PlayerInfo>();

            [JsonProperty("Backups")]
            public Dictionary<ulong, PlayerBackup> Backups = new Dictionary<ulong, PlayerBackup>();
        }

        private class PlayerInfo
        {
            [JsonProperty("Name")]
            public string Name;

            [JsonProperty("Chest IDs (1 wear, 2 main, 3 belt, 4 backpack)")]
            public ulong[] Chests = new ulong[ChestCount];

            [JsonProperty("Active")]
            public bool Active;

            [JsonProperty("Mode")]
            public string Mode;

            [JsonProperty("Activation timestamp")]
            public double ActivationTime;

            [JsonProperty("Deactivation timestamp")]
            public double DeactivationTime;

            [JsonProperty("RealPVE loot toggled by this plugin")]
            public bool LootToggled;
        }

        private class PlayerBackup
        {
            [JsonProperty("itemInfos")]
            public Dictionary<long, ItemInfos> Entries = new Dictionary<long, ItemInfos>();

            [JsonProperty("tmp_itemInfosKeys")]
            public List<long> ListedKeys = new List<long>();
        }

        private class ItemInfos
        {
            [JsonProperty("itemInfo")]
            public List<ItemInfo> Items = new List<ItemInfo>();
        }

        private class ItemInfo
        {
            public string shortname;
            public string name;
            public int amount;
            public Dictionary<string, float> attachments;
            public string text;
            public ulong skinID;
            public string container;
            public int container_slot;
        }

        private void SaveData()
        {
            if (_data == null || _dataLoadFailed) return;
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        private void LoadData()
        {
            var fs = Interface.Oxide.DataFileSystem;
            JObject current = null;

            if (fs.ExistsDatafile(Name))
            {
                current = ReadJObject(Name);
                if (current == null)
                {
                    _dataLoadFailed = true;
                    _data = new StoredData();
                    PrintError($"Data file data/{Name}.json could not be read. Saving is DISABLED to protect the file. Fix or remove the file and reload the plugin.");
                    return;
                }
            }

            if (current != null && current["Data version"] != null)
            {
                _data = current.ToObject<StoredData>() ?? new StoredData();
            }
            else
            {
                _data = new StoredData();

                JObject legacy = IsLegacyData(current) ? current
                    : fs.ExistsDatafile(LegacyDataName) ? ReadJObject(LegacyDataName) : null;

                if (IsLegacyData(legacy))
                {
                    fs.WriteObject($"{Name}_LegacyDataBackup", legacy);
                    MigrateLegacyData(legacy);
                    PrintWarning($"Legacy data migrated ({_data.Players.Count} staff entries, {_data.Backups.Count} backup owners). A copy was written to data/{Name}_LegacyDataBackup.json.");
                }
            }

            NormalizeData();
            SaveData();
        }

        private JObject ReadJObject(string fileName)
        {
            try
            {
                return Interface.Oxide.DataFileSystem.ReadObject<JObject>(fileName);
            }
            catch (Exception ex)
            {
                PrintError($"Could not read data file '{fileName}': {ex.Message}");
                return null;
            }
        }

        private static bool IsLegacyData(JObject data) => data != null && (data["AllPlayerInfos"] != null || data["playerBackup"] != null);

        private void MigrateLegacyData(JObject legacy)
        {
            var players = legacy["AllPlayerInfos"] as JObject;
            if (players != null)
            {
                foreach (var property in players.Properties())
                {
                    ulong id;
                    var source = property.Value as JObject;
                    if (source == null || !ulong.TryParse(property.Name, out id)) continue;

                    var info = new PlayerInfo
                    {
                        Name = source.Value<string>("RPName"),
                        Active = source.Value<bool?>("Active") ?? false,
                        DeactivationTime = source.Value<double?>("DeactivationTime") ?? 0d
                    };

                    for (int i = 0; i < ChestCount; i++)
                        info.Chests[i] = ReadLegacyNetId(source[$"AdminChestID{i + 1}"]);

                    if (info.Active)
                    {
                        info.Mode = ResolveModeFromGroups(id.ToString());
                        if (info.DeactivationTime > 0)
                            info.ActivationTime = info.DeactivationTime - _config.AutoDeactivationMinutes * 60d;
                    }

                    _data.Players[id] = info;
                }
            }

            var backups = legacy["playerBackup"];
            if (backups != null && backups.Type == JTokenType.Object)
            {
                try
                {
                    _data.Backups = backups.ToObject<Dictionary<ulong, PlayerBackup>>() ?? new Dictionary<ulong, PlayerBackup>();
                }
                catch (Exception ex)
                {
                    PrintError($"Legacy backups could not be migrated: {ex.Message}");
                }
            }
        }

        private static ulong ReadLegacyNetId(JToken token)
        {
            if (token == null) return 0UL;
            if (token.Type == JTokenType.Integer) return token.Value<ulong>();
            if (token.Type == JTokenType.Object)
            {
                var value = token["Value"] ?? token["value"];
                if (value != null && value.Type == JTokenType.Integer) return value.Value<ulong>();
            }
            return 0UL;
        }

        private void NormalizeData()
        {
            if (_data.Players == null) _data.Players = new Dictionary<ulong, PlayerInfo>();
            if (_data.Backups == null) _data.Backups = new Dictionary<ulong, PlayerBackup>();

            foreach (var info in _data.Players.Values)
            {
                if (info == null) continue;
                if (info.Chests == null || info.Chests.Length != ChestCount)
                {
                    var chests = new ulong[ChestCount];
                    if (info.Chests != null) Array.Copy(info.Chests, chests, Math.Min(info.Chests.Length, ChestCount));
                    info.Chests = chests;
                }
            }

            foreach (var key in _data.Players.Where(x => x.Value == null).Select(x => x.Key).ToList())
                _data.Players.Remove(key);

            foreach (var backup in _data.Backups.Values)
            {
                if (backup == null) continue;
                if (backup.Entries == null) backup.Entries = new Dictionary<long, ItemInfos>();
                if (backup.ListedKeys == null) backup.ListedKeys = new List<long>();
            }
        }

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "You do not have permission to use this command.",
                ["ChestsMissing"] = "Staff mode cannot be activated: admin chest(s) {0} are missing or not assigned. Use /radmin_as.",
                ["InventoryStoreFailed"] = "Your inventory could not be stored completely in your admin chests. Activation cancelled, your items were returned.",
                ["AdminActivated"] = "Admin mode active!",
                ["ModActivated"] = "Moderator mode active!",
                ["AdminDeactivated"] = "Admin mode deactivated.",
                ["ModDeactivated"] = "Moderator mode deactivated.",
                ["AutoDeactivationInfo"] = "Your staff mode will be deactivated automatically in {0} minutes.",
                ["AutoDeactivated"] = "Your staff mode was deactivated automatically after {0} minutes.",
                ["ExpiredOnConnect"] = "Your staff mode had expired and was deactivated when you connected.",
                ["RestoreFailedChests"] = "Admin chest(s) {0} are missing: your items could not be returned. Staff items were removed. Use /radmin_a_restore _self to restore a backup.",
                ["ItemsDropped"] = "Some items did not fit into your inventory and were dropped: {0}",
                ["UsageAssign"] = "Usage: /radmin_as <steamID|_self> <chest number 1-4>",
                ["InvalidSteamId"] = "Invalid Steam ID.",
                ["InvalidChestNumber"] = "The chest number must be between 1 and 4.",
                ["LookAtLargeBox"] = "You must look at a large wooden box to use this command.",
                ["ChestAlreadyAssigned"] = "This box is already assigned (player {0}, chest {1}).",
                ["ChestAssignWhileActive"] = "Chests of {0} cannot be changed while staff mode is active.",
                ["PlayerInitialized"] = "Staff data for {0} has been initialized.",
                ["ChestAssigned"] = "Chest {0} has been assigned to {1} and emptied.",
                ["CapacityReset"] = "Main inventory capacity set to 24. Current items: {0}.",
                ["NoChestsAssigned"] = "No admin chests have been assigned yet.",
                ["ChestListHeader"] = "--- Assigned admin chests ---",
                ["ChestListPlayer"] = "Player: {0} (ID: {1})",
                ["ChestListEntry"] = "  Chest {0} ({1}): {2}",
                ["ChestFound"] = "{0} – found",
                ["ChestNotFound"] = "{0} – NOT found",
                ["ChestUnassigned"] = "not assigned",
                ["ChestListSeparator"] = "--------------------",
                ["SlotWear"] = "wear",
                ["SlotMain"] = "main",
                ["SlotBelt"] = "belt",
                ["SlotBackpack"] = "backpack",
                ["Unknown"] = "Unknown",
                ["BackupsReduced"] = "Backups older than 24 hours have been removed.",
                ["BackupsCleared"] = "All player backups have been deleted.",
                ["RestoreUsage"] = "Usage: /radmin_a_restore <steamID|_self> [backup index] [confirm]",
                ["RestoreWhileActive"] = "Please deactivate your staff mode before restoring a backup.",
                ["NoBackups"] = "No backups found for player ID {0}.",
                ["BackupListHeader"] = "Available backups for player {0}:",
                ["BackupListEntry"] = "[{0}]: {1}",
                ["BackupListHintDetails"] = "Use /radmin_a_restore <steamID|_self> <index> to show details.",
                ["BackupListHintConfirm"] = "Use /radmin_a_restore <steamID|_self> <index> confirm to restore the backup into YOUR inventory.",
                ["InvalidBackupIndex"] = "Invalid backup index. Allowed: 0 to {0}. List the backups first.",
                ["BackupNotFound"] = "Backup not found – the data may be corrupt.",
                ["BackupDetailsHeader"] = "Details for backup [{0}] ({1}):",
                ["BackupDetailsEntry"] = "- {0} x{1} (container: {2}, slot: {3})",
                ["RestoreStarted"] = "Restoring inventory...",
                ["RestoreDone"] = "Inventory restored.",
                ["Position"] = "Your position: X:{0:F2} | Y:{1:F2} | Z:{2:F2}",
                ["EntityInfo"] = "Entity ID: {0}\nName: {1}\nPrefab: {2}\nShort prefab: {3}\nSkin: {4}\nOwner: {5}",
                ["BackpackInfo"] = "Backpack items (API, first page): {0}",
                ["BackpackUnavailable"] = "Backpack API not available or no backpack found.",
                ["NotLookingAtEntity"] = "You are not looking at an entity.",
                ["HandItemInfo"] = "Short name: {0}\nItem ID: {1}\nSkin: {2}\nCategory: {3}\nCondition: {4}\nAmount: {5}",
                ["NoItemInHand"] = "You are not holding an item.",
                ["LootSyncUnavailable"] = "{0} is not loaded or the integration is disabled.",
                ["LootSyncDone"] = "RealPVE loot access for {0} was flipped once. It should now be {1}. Check the server console for the RealPVE reply.",
                ["LootExpectedOn"] = "GRANTED (staff mode active)",
                ["LootExpectedOff"] = "REVOKED (player mode)"
            }, this, "en");

            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NoPermission"] = "Du hast keine Berechtigung, diesen Befehl zu verwenden.",
                ["ChestsMissing"] = "Teammodus kann nicht aktiviert werden: Admin-Kiste(n) {0} fehlen oder sind nicht zugewiesen. Nutze /radmin_as.",
                ["InventoryStoreFailed"] = "Dein Inventar konnte nicht vollständig in deinen Admin-Kisten gesichert werden. Aktivierung abgebrochen, deine Items wurden zurückgegeben.",
                ["AdminActivated"] = "Als Admin aktiv!",
                ["ModActivated"] = "Als Mod aktiv!",
                ["AdminDeactivated"] = "Als Admin NICHT aktiv!",
                ["ModDeactivated"] = "Als Mod NICHT aktiv!",
                ["AutoDeactivationInfo"] = "Dein Teammodus wird in {0} Minuten automatisch beendet.",
                ["AutoDeactivated"] = "Dein Teammodus wurde nach {0} Minuten automatisch beendet.",
                ["ExpiredOnConnect"] = "Dein Teammodus war abgelaufen und wurde beim Verbinden automatisch beendet.",
                ["RestoreFailedChests"] = "Admin-Kiste(n) {0} fehlen: Deine Items konnten nicht zurückgegeben werden. Team-Items wurden entfernt. Nutze /radmin_a_restore _self, um ein Backup wiederherzustellen.",
                ["ItemsDropped"] = "Einige Items passten nicht ins Inventar und wurden fallen gelassen: {0}",
                ["UsageAssign"] = "Verwendung: /radmin_as <steamID|_self> <Kistennummer 1-4>",
                ["InvalidSteamId"] = "Ungültige Spieler-ID!",
                ["InvalidChestNumber"] = "Die Kistennummer muss zwischen 1 und 4 liegen.",
                ["LookAtLargeBox"] = "Du musst eine große Holzkiste ansehen, um diesen Befehl zu verwenden!",
                ["ChestAlreadyAssigned"] = "Diese Kiste ist bereits zugewiesen (Spieler {0}, Kiste {1}).",
                ["ChestAssignWhileActive"] = "Die Kisten von {0} können nicht geändert werden, solange der Teammodus aktiv ist.",
                ["PlayerInitialized"] = "Spieler-Informationen für {0} wurden initialisiert.",
                ["ChestAssigned"] = "Kiste {0} erfolgreich für {1} zugewiesen und geleert.",
                ["CapacityReset"] = "Inventarkapazität auf 24 gesetzt. Aktuelle Items: {0}.",
                ["NoChestsAssigned"] = "Es sind noch keine Admin-Kisten zugewiesen.",
                ["ChestListHeader"] = "--- Zugewiesene Admin-Kisten ---",
                ["ChestListPlayer"] = "Spieler: {0} (ID: {1})",
                ["ChestListEntry"] = "  Kiste {0} ({1}): {2}",
                ["ChestFound"] = "{0} – gefunden",
                ["ChestNotFound"] = "{0} – NICHT gefunden",
                ["ChestUnassigned"] = "nicht zugewiesen",
                ["ChestListSeparator"] = "--------------------",
                ["SlotWear"] = "Kleidung",
                ["SlotMain"] = "Inventar",
                ["SlotBelt"] = "Gürtel",
                ["SlotBackpack"] = "Rucksack",
                ["Unknown"] = "Unbekannt",
                ["BackupsReduced"] = "Backups älter als 24 Stunden wurden gelöscht.",
                ["BackupsCleared"] = "Alle Spieler-Backups wurden gelöscht.",
                ["RestoreUsage"] = "Verwendung: /radmin_a_restore <steamID|_self> [Backup-Index] [confirm]",
                ["RestoreWhileActive"] = "Bitte beende zuerst deinen Teammodus, bevor du ein Backup wiederherstellst.",
                ["NoBackups"] = "Für Spieler-ID {0} wurden keine Backups gefunden.",
                ["BackupListHeader"] = "Verfügbare Backups für Spieler {0}:",
                ["BackupListEntry"] = "[{0}]: {1}",
                ["BackupListHintDetails"] = "Verwende /radmin_a_restore <steamID|_self> <Index>, um Details zu sehen.",
                ["BackupListHintConfirm"] = "Verwende /radmin_a_restore <steamID|_self> <Index> confirm, um das Backup in DEIN Inventar wiederherzustellen.",
                ["InvalidBackupIndex"] = "Ungültiger Backup-Index. Erlaubt: 0 bis {0}. Liste die Backups zuerst auf.",
                ["BackupNotFound"] = "Backup nicht gefunden – Daten möglicherweise beschädigt.",
                ["BackupDetailsHeader"] = "Details für Backup [{0}] ({1}):",
                ["BackupDetailsEntry"] = "- {0} x{1} (Container: {2}, Slot: {3})",
                ["RestoreStarted"] = "Beginne mit der Wiederherstellung des Inventars...",
                ["RestoreDone"] = "Inventar erfolgreich wiederhergestellt!",
                ["Position"] = "Deine Position: X:{0:F2} | Y:{1:F2} | Z:{2:F2}",
                ["EntityInfo"] = "Entity-ID: {0}\nName: {1}\nPrefab: {2}\nKurz-Prefab: {3}\nSkin: {4}\nBesitzer: {5}",
                ["BackpackInfo"] = "Rucksack-Items (API, erste Seite): {0}",
                ["BackpackUnavailable"] = "Rucksack-API nicht verfügbar oder kein Rucksack gefunden.",
                ["NotLookingAtEntity"] = "Du schaust kein Entity an.",
                ["HandItemInfo"] = "Shortname: {0}\nItem-ID: {1}\nSkin: {2}\nKategorie: {3}\nZustand: {4}\nAnzahl: {5}",
                ["NoItemInHand"] = "Du hältst kein Item in der Hand.",
                ["LootSyncUnavailable"] = "{0} ist nicht geladen oder die Anbindung ist deaktiviert.",
                ["LootSyncDone"] = "Die RealPVE-Plünderrechte für {0} wurden einmal umgeschaltet. Sie sollten jetzt {1} sein. Die Antwort von RealPVE steht in der Server-Konsole.",
                ["LootExpectedOn"] = "ERTEILT (Teammodus aktiv)",
                ["LootExpectedOff"] = "ENTZOGEN (Spielermodus)"
            }, this, "de");
        }

        private string Msg(string key, string userId, params object[] args)
        {
            string format = lang.GetMessage(key, this, userId);
            return args != null && args.Length > 0 ? string.Format(format, args) : format;
        }

        private void Reply(BasePlayer player, string key, params object[] args)
        {
            if (player == null || !player.IsConnected) return;
            player.ChatMessage(Msg(key, player.UserIDString, args));
        }

        #endregion

        #region Oxide Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            permission.RegisterPermission(PermMod, this);
            EnsureGroup(_config.Groups.AdminActive);
            EnsureGroup(_config.Groups.ModActive);
            LoadData();
        }

        private void OnServerInitialized()
        {
            foreach (var player in BasePlayer.activePlayerList.ToList())
                RestoreActiveState(player);
        }

        private void Unload()
        {
            foreach (var entry in _deactivationTimers.Values)
                entry?.Destroy();
            _deactivationTimers.Clear();
            SaveData();
        }

        private void OnNewSave(string filename)
        {
            if (_data == null) LoadData();

            var lootToggled = new List<string>();
            foreach (var entry in _data.Players)
            {
                var info = entry.Value;
                string id = entry.Key.ToString();

                permission.RemoveUserGroup(id, _config.Groups.AdminActive);
                permission.RemoveUserGroup(id, _config.Groups.ModActive);

                if (info.LootToggled) lootToggled.Add(id);

                info.Chests = new ulong[ChestCount];
                info.Active = false;
                info.Mode = null;
                info.ActivationTime = 0d;
                info.DeactivationTime = 0d;
                info.LootToggled = false;
            }

            SaveData();
            PrintWarning("Map wipe detected: all admin chest assignments and active staff modes were reset. Reassign the chests with /radmin_as.");

            if (lootToggled.Count > 0)
                PrintWarning($"RealPVE loot access was toggled on by this plugin for: {string.Join(", ", lootToggled)}. Check it with 'adminpve loot <id>' if RealPVE keeps this setting across wipes.");
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            if (HasStaffAccess(player))
            {
                bool created;
                GetOrCreateInfo(player.userID.Get(), player.displayName, out created);
            }
            RestoreActiveState(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            StopDeactivationTimer(player.userID.Get());
        }

        private void OnPlayerDeath(BasePlayer player, HitInfo hitInfo)
        {
            if (player == null || player.IsNpc) return;
            if (!IsActive(player.userID.Get())) return;

            // Staff loadout must never end up in a corpse. Own items are stored in the admin chests.
            ClearInventory(player);
            GetBackpackContainer(player.userID.Get())?.Clear();
            ItemManager.DoRemoves();
        }

        private void OnItemDropped(Item item, BaseEntity entity)
        {
            if (IsStaffItem(item)) item.Remove();
        }

        private void OnAdvancedStatusLoaded()
        {
            foreach (var player in BasePlayer.activePlayerList.ToList())
            {
                PlayerInfo info;
                if (_data.Players.TryGetValue(player.userID.Get(), out info) && info.Active)
                    ShowBar(player, info);
            }
        }

        #endregion

        #region Staff Mode

        private void ToggleMode(BasePlayer player, string mode)
        {
            ulong id = player.userID.Get();
            bool created;
            var info = GetOrCreateInfo(id, player.displayName, out created);

            if (info.Active)
            {
                Deactivate(player, ResolveMode(info, player.UserIDString) == ModeAdmin ? "AdminDeactivated" : "ModDeactivated");
                return;
            }

            Activate(player, info, mode);
        }

        private void Activate(BasePlayer player, PlayerInfo info, string mode)
        {
            ulong id = player.userID.Get();

            StorageContainer[] chests;
            string missing;
            if (!TryGetChests(info, out chests, out missing))
            {
                Reply(player, "ChestsMissing", missing);
                return;
            }

            BackupItems(player, id);
            PauseBackpackGather(id);

            foreach (var chest in chests)
                chest.inventory.Clear();
            ItemManager.DoRemoves();

            var inventory = player.inventory;
            var backpack = GetBackpackContainer(id);

            bool stored = MoveAll(inventory.containerWear, chests[SlotWear].inventory)
                        && MoveAll(inventory.containerMain, chests[SlotMain].inventory)
                        && MoveAll(inventory.containerBelt, chests[SlotBelt].inventory)
                        && MoveAll(backpack, chests[SlotBackpack].inventory);

            if (!stored)
            {
                var dropped = new List<string>();
                ReturnChestContents(player, chests, backpack, dropped);
                Reply(player, "InventoryStoreFailed");
                if (dropped.Count > 0) Reply(player, "ItemsDropped", string.Join(", ", dropped));
                PrintWarning($"Activation for {player.displayName} ({id}) cancelled: inventory did not fit into the admin chests.");
                return;
            }

            info.Name = player.displayName;
            info.Active = true;
            info.Mode = mode;
            info.ActivationTime = Now;
            info.DeactivationTime = _config.AutoDeactivationMinutes > 0 ? info.ActivationTime + _config.AutoDeactivationMinutes * 60d : 0d;

            permission.AddUserGroup(player.UserIDString, ActiveGroup(mode));
            SaveData();

            Godmode?.Call("EnableGodmode", player.UserIDString);
            if (mode == ModeAdmin) Vanish?.Call("Disappear", player);

            Reply(player, mode == ModeAdmin ? "AdminActivated" : "ModActivated");
            Puts($"Staff mode '{mode}' activated: {player.displayName} ({id})");

            ShowBar(player, info);
            StartDeactivationTimer(player, info, true);

            timer.Once(LoadoutDelay, () =>
            {
                if (!IsStillActive(player, id)) return;
                GiveLoadout(player, GetModeSettings(mode));

                timer.Once(FollowUpDelay, () =>
                {
                    if (!IsStillActive(player, id)) return;
                    SetRealPveLoot(player, info, true);
                    RunFollowUpCommand(player);
                });
            });
        }

        private void Deactivate(BasePlayer player, string messageKey, params object[] args)
        {
            ulong id = player.userID.Get();
            PlayerInfo info;
            if (!_data.Players.TryGetValue(id, out info) || !info.Active) return;

            StopDeactivationTimer(id);
            string mode = ResolveMode(info, player.UserIDString);

            var radarResult = AdminRadar?.Call("IsRadar", player.UserIDString);
            bool radarActive = radarResult is bool && (bool)radarResult;

            PauseBackpackGather(id);

            StorageContainer[] chests;
            string missing;
            if (TryGetChests(info, out chests, out missing))
            {
                var backpack = GetBackpackContainer(id);
                ClearInventory(player);
                backpack?.Clear();
                ItemManager.DoRemoves();

                var dropped = new List<string>();
                ReturnChestContents(player, chests, backpack, dropped);
                if (dropped.Count > 0) Reply(player, "ItemsDropped", string.Join(", ", dropped));
            }
            else
            {
                // Never wipe the player's inventory without being able to give the own items back.
                RemoveStaffItems(player);
                Reply(player, "RestoreFailedChests", missing);
                PrintWarning($"Deactivation for {player.displayName} ({id}): chest(s) {missing} missing, own items not returned. Backups are available via /radmin_a_restore.");
            }

            permission.RemoveUserGroup(player.UserIDString, _config.Groups.AdminActive);
            permission.RemoveUserGroup(player.UserIDString, _config.Groups.ModActive);
            HideBars(id);

            Godmode?.Call("DisableGodmode", player.UserIDString);
            if (mode == ModeAdmin) Vanish?.Call("Reappear", player);
            player.SendConsoleCommand("admintime -1");
            if (radarActive) AdminRadar?.Call("RadarCommand", player.IPlayer);

            SetRealPveLoot(player, info, false);

            info.Active = false;
            info.Mode = null;
            info.ActivationTime = 0d;
            info.DeactivationTime = 0d;
            SaveData();

            Reply(player, messageKey, args);
            Puts($"Staff mode '{mode}' deactivated: {player.displayName} ({id})");

            timer.Once(FollowUpDelay, () =>
            {
                if (player != null && player.IsConnected) RunFollowUpCommand(player);
            });
        }

        private void RestoreActiveState(BasePlayer player)
        {
            if (player == null || player.IsNpc) return;

            PlayerInfo info;
            if (!_data.Players.TryGetValue(player.userID.Get(), out info) || !info.Active) return;

            if (string.IsNullOrEmpty(info.Mode)) info.Mode = ResolveModeFromGroups(player.UserIDString);

            if (info.DeactivationTime > 0 && info.DeactivationTime <= Now)
            {
                Deactivate(player, "ExpiredOnConnect");
                return;
            }

            ShowBar(player, info);
            StartDeactivationTimer(player, info, false);
        }

        private void StartDeactivationTimer(BasePlayer player, PlayerInfo info, bool announce)
        {
            ulong id = player.userID.Get();
            StopDeactivationTimer(id);

            if (info.DeactivationTime <= 0) return;

            double remaining = info.DeactivationTime - Now;
            if (remaining <= 0) return;

            int minutes = _config.AutoDeactivationMinutes;
            _deactivationTimers[id] = timer.Once((float)remaining, () =>
            {
                _deactivationTimers.Remove(id);
                var target = BasePlayer.FindByID(id);
                if (target == null || !target.IsConnected) return;
                Deactivate(target, "AutoDeactivated", minutes);
            });

            if (announce) Reply(player, "AutoDeactivationInfo", minutes);
        }

        private void StopDeactivationTimer(ulong id)
        {
            Timer existing;
            if (!_deactivationTimers.TryGetValue(id, out existing)) return;
            existing?.Destroy();
            _deactivationTimers.Remove(id);
        }

        #endregion

        #region Integrations

        private bool IsAdvancedStatusReady()
        {
            if (AdvancedStatus == null || !AdvancedStatus.IsLoaded) return false;
            var result = AdvancedStatus.Call("IsReady");
            return result is bool && (bool)result;
        }

        private void ShowBar(BasePlayer player, PlayerInfo info)
        {
            if (!IsAdvancedStatusReady()) return;

            string mode = ResolveMode(info, player.UserIDString);
            var settings = GetModeSettings(mode);

            var parameters = new Dictionary<string, object>
            {
                { "Id", mode == ModeAdmin ? BarIdAdmin : BarIdMod },
                { "Plugin", Name },
                { "Text", settings.BarText },
                { "Main_Color", settings.BarColor },
                { "Order", 10 }
            };

            if (info.DeactivationTime > 0)
            {
                double start = info.ActivationTime > 0 ? info.ActivationTime : info.DeactivationTime - _config.AutoDeactivationMinutes * 60d;
                parameters["BarType"] = "TimeProgressCounter";
                parameters["TimeStampStart"] = start;
                parameters["TimeStamp"] = info.DeactivationTime;
            }
            else
            {
                parameters["BarType"] = "Default";
            }

            AdvancedStatus.Call("CreateBar", player, parameters);
        }

        private void HideBars(ulong id)
        {
            if (AdvancedStatus == null || !AdvancedStatus.IsLoaded) return;
            AdvancedStatus.Call("DeleteBar", id, BarIdAdmin, Name);
            AdvancedStatus.Call("DeleteBar", id, BarIdMod, Name);
        }

        private ItemContainer GetBackpackContainer(ulong id)
        {
            // Deprecated in Backpacks, but still the only container API. Covers the first backpack page only.
            if (Backpacks == null || !Backpacks.IsLoaded) return null;
            return Backpacks.Call("API_GetBackpackContainer", id) as ItemContainer;
        }

        private void PauseBackpackGather(ulong id)
        {
            if (Backpacks == null || !Backpacks.IsLoaded) return;
            Backpacks.Call("API_PauseBackpackGatherMode", id, 0f);
        }

        private void SetRealPveLoot(BasePlayer player, PlayerInfo info, bool enable)
        {
            // 'adminpve loot <id>' is a toggle: only switch it off if this plugin switched it on.
            if (!_config.RealPve.Enabled || info.LootToggled == enable) return;

            var realPve = plugins.Find(_config.RealPve.PluginName);
            if (realPve == null || !realPve.IsLoaded)
            {
                PrintWarning($"{_config.RealPve.PluginName} is not loaded – loot access for {player.displayName} was not {(enable ? "enabled" : "disabled")}.");
                return;
            }

            if (!RunRealPveLootToggle(player.UserIDString)) return;

            info.LootToggled = enable;
            SaveData();
            Puts($"RealPVE loot access {(enable ? "granted to" : "revoked from")} {player.displayName} ({player.UserIDString}).");
        }

        private bool IsRealPveLoaded()
        {
            var realPve = plugins.Find(_config.RealPve.PluginName);
            return realPve != null && realPve.IsLoaded;
        }

        private bool RunRealPveLootToggle(string userId)
        {
            try
            {
                rust.RunServerCommand(string.Format(_config.RealPve.LootToggleCommand, userId));
                return true;
            }
            catch (FormatException)
            {
                PrintError("Invalid 'Server command that toggles unrestricted loot access' in the configuration.");
                return false;
            }
        }

        private void RunFollowUpCommand(BasePlayer player)
        {
            if (string.IsNullOrEmpty(_config.FollowUpChatCommand)) return;
            rust.RunClientCommand(player, "chat.say", _config.FollowUpChatCommand);
        }

        #endregion

        #region Items

        private static bool IsStaffItem(Item item) => item?.name != null && item.name.EndsWith(StaffItemTag, StringComparison.Ordinal);

        private static void TagItem(Item item)
        {
            item.name = $"{item.info.displayName.english} {StaffItemTag}";
            item.MarkDirty();
        }

        private Item CreateStaffItem(LoadoutItem entry)
        {
            var item = ItemManager.CreateByName(entry.ShortName, Math.Max(1, entry.Amount), entry.SkinId);
            if (item == null)
            {
                PrintWarning($"Loadout item '{entry.ShortName}' could not be created.");
                return null;
            }
            TagItem(item);
            return item;
        }

        private void GiveLoadout(BasePlayer player, ModeSettings settings)
        {
            PauseBackpackGather(player.userID.Get());

            foreach (var entry in settings.Wear)
            {
                var item = CreateStaffItem(entry);
                if (item != null && !item.MoveToContainer(player.inventory.containerWear)) item.Remove();
            }

            foreach (var entry in settings.Belt)
            {
                var item = CreateStaffItem(entry);
                if (item == null) continue;
                PrepareWeapon(item);
                if (!item.MoveToContainer(player.inventory.containerBelt) && !item.MoveToContainer(player.inventory.containerMain))
                    item.Remove();
            }
        }

        private static void PrepareWeapon(Item item)
        {
            switch (item.info.shortname)
            {
                case "rifle.l96":
                    AddAttachments(item, "weapon.mod.small.scope", "weapon.mod.lasersight");
                    SetMagazine(item, 50);
                    break;
                case "rifle.lr300":
                    AddAttachments(item, "weapon.mod.holosight", "weapon.mod.flashlight");
                    SetMagazine(item, 512);
                    break;
            }
        }

        private static void AddAttachments(Item weapon, params string[] shortNames)
        {
            if (weapon.contents == null) return;
            foreach (var shortName in shortNames)
            {
                var attachment = ItemManager.CreateByName(shortName);
                if (attachment == null) continue;
                TagItem(attachment);
                if (!attachment.MoveToContainer(weapon.contents)) attachment.Remove();
            }
        }

        private static void SetMagazine(Item weapon, int amount)
        {
            var projectile = weapon.GetHeldEntity() as BaseProjectile;
            if (projectile == null) return;
            projectile.primaryMagazine.capacity = amount;
            projectile.primaryMagazine.contents = amount;
            projectile.SendNetworkUpdateImmediate();
        }

        private static void RemoveStaffItems(BasePlayer player)
        {
            var inventory = player.inventory;
            foreach (var container in new[] { inventory.containerWear, inventory.containerMain, inventory.containerBelt })
            {
                foreach (var item in container.itemList.ToList())
                    if (IsStaffItem(item)) item.Remove();
            }
            ItemManager.DoRemoves();
        }

        private static void ClearInventory(BasePlayer player)
        {
            player.inventory.containerWear.Clear();
            player.inventory.containerMain.Clear();
            player.inventory.containerBelt.Clear();
        }

        private static bool MoveAll(ItemContainer source, ItemContainer target)
        {
            if (source == null) return true;
            if (target == null) return source.itemList.Count == 0;

            foreach (var item in source.itemList.ToList())
            {
                if (item.MoveToContainer(target, item.position)) continue;
                if (item.MoveToContainer(target)) continue;
                return false;
            }
            return true;
        }

        private static void MoveOrDrop(BasePlayer player, ItemContainer source, ItemContainer target, List<string> dropped)
        {
            if (source == null) return;

            foreach (var item in source.itemList.ToList())
            {
                if (target != null && (item.MoveToContainer(target, item.position) || item.MoveToContainer(target))) continue;
                if (player.inventory.GiveItem(item)) continue;

                item.Drop(player.transform.position + Vector3.up, player.transform.forward * 2f);
                dropped.Add(item.info.shortname);
            }
        }

        private static void ReturnChestContents(BasePlayer player, StorageContainer[] chests, ItemContainer backpack, List<string> dropped)
        {
            MoveOrDrop(player, chests[SlotWear].inventory, player.inventory.containerWear, dropped);
            MoveOrDrop(player, chests[SlotBelt].inventory, player.inventory.containerBelt, dropped);
            MoveOrDrop(player, chests[SlotMain].inventory, player.inventory.containerMain, dropped);
            MoveOrDrop(player, chests[SlotBackpack].inventory, backpack ?? player.inventory.containerMain, dropped);
        }

        private void BackupItems(BasePlayer player, ulong id)
        {
            PlayerBackup backup;
            if (!_data.Backups.TryGetValue(id, out backup))
                _data.Backups[id] = backup = new PlayerBackup();

            long key = DateTime.Now.ToFileTime();
            while (backup.Entries.ContainsKey(key)) key++;

            var entry = new ItemInfos();
            AddBackupItems(entry, player.inventory.containerWear, "wear");
            AddBackupItems(entry, player.inventory.containerBelt, "belt");
            AddBackupItems(entry, player.inventory.containerMain, "main");
            AddBackupItems(entry, GetBackpackContainer(id), "backpack");

            backup.Entries[key] = entry;
            SaveData();
        }

        private static void AddBackupItems(ItemInfos target, ItemContainer container, string containerName)
        {
            if (container == null) return;

            foreach (var item in container.itemList)
            {
                if (IsStaffItem(item)) continue;

                var attachments = new Dictionary<string, float>();
                if (item.contents != null)
                {
                    foreach (var content in item.contents.itemList)
                        attachments[content.info.shortname] = 1f;
                }

                target.Items.Add(new ItemInfo
                {
                    container = containerName,
                    container_slot = item.position,
                    shortname = item.info.shortname,
                    name = item.info.displayName.english,
                    amount = item.amount,
                    skinID = item.skin,
                    text = item.text,
                    attachments = attachments
                });
            }
        }

        #endregion

        #region Helpers

        private bool IsAdminUser(BasePlayer player) =>
            permission.UserHasPermission(player.UserIDString, PermAdmin) || permission.UserHasGroup(player.UserIDString, _config.Groups.Admin);

        private bool IsModUser(BasePlayer player) =>
            permission.UserHasPermission(player.UserIDString, PermMod) || permission.UserHasGroup(player.UserIDString, _config.Groups.Mod);

        private bool HasStaffAccess(BasePlayer player) => IsAdminUser(player) || IsModUser(player);

        private bool IsActive(ulong id)
        {
            PlayerInfo info;
            return _data.Players.TryGetValue(id, out info) && info.Active;
        }

        private bool IsStillActive(BasePlayer player, ulong id) => player != null && player.IsConnected && IsActive(id);

        private string ActiveGroup(string mode) => mode == ModeAdmin ? _config.Groups.AdminActive : _config.Groups.ModActive;

        private ModeSettings GetModeSettings(string mode) => mode == ModeAdmin ? _config.Admin : _config.Mod;

        private string ResolveModeFromGroups(string userId) =>
            permission.UserHasGroup(userId, _config.Groups.AdminActive) ? ModeAdmin : ModeMod;

        private string ResolveMode(PlayerInfo info, string userId) =>
            string.IsNullOrEmpty(info.Mode) ? ResolveModeFromGroups(userId) : info.Mode;

        private void EnsureGroup(string group)
        {
            if (!string.IsNullOrEmpty(group) && !permission.GroupExists(group))
                permission.CreateGroup(group, group, 0);
        }

        private PlayerInfo GetOrCreateInfo(ulong id, string name, out bool created)
        {
            PlayerInfo info;
            created = false;

            if (_data.Players.TryGetValue(id, out info))
            {
                if (!string.IsNullOrEmpty(name)) info.Name = name;
                return info;
            }

            info = new PlayerInfo { Name = name };
            _data.Players[id] = info;
            created = true;
            SaveData();
            Puts($"Staff data initialized for {name} ({id}).");
            return info;
        }

        private bool TryGetChests(PlayerInfo info, out StorageContainer[] chests, out string missing)
        {
            chests = new StorageContainer[ChestCount];
            var missingList = new List<int>();

            for (int i = 0; i < ChestCount; i++)
            {
                chests[i] = FindChest(info.Chests[i]);
                if (chests[i] == null) missingList.Add(i + 1);
            }

            missing = string.Join(", ", missingList);
            return missingList.Count == 0;
        }

        private static StorageContainer FindChest(ulong netId)
        {
            if (netId == 0) return null;
            var chest = BaseNetworkable.serverEntities.Find(new NetworkableId(netId)) as StorageContainer;
            return chest == null || chest.IsDestroyed || chest.inventory == null ? null : chest;
        }

        private string SlotName(int index, string userId)
        {
            switch (index)
            {
                case SlotWear: return Msg("SlotWear", userId);
                case SlotMain: return Msg("SlotMain", userId);
                case SlotBelt: return Msg("SlotBelt", userId);
                default: return Msg("SlotBackpack", userId);
            }
        }

        private string FindPlayerName(ulong id)
        {
            var online = BasePlayer.FindByID(id);
            if (online != null) return online.displayName;
            return covalence.Players.FindPlayerById(id.ToString())?.Name;
        }

        private static bool TryParseTarget(BasePlayer player, string arg, out ulong id)
        {
            if (arg.Equals("_self", StringComparison.OrdinalIgnoreCase))
            {
                id = player.userID.Get();
                return true;
            }
            return ulong.TryParse(arg, out id);
        }

        private static BaseEntity Look(BasePlayer player)
        {
            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return null;
            return hit.GetEntity();
        }

        #endregion

        #region Chat Commands

        [ChatCommand("radmin_a")]
        private void CmdToggleAdmin(BasePlayer player, string command, string[] args)
        {
            if (!IsAdminUser(player)) { Reply(player, "NoPermission"); return; }
            ToggleMode(player, ModeAdmin);
        }

        [ChatCommand("radminm")]
        private void CmdToggleMod(BasePlayer player, string command, string[] args)
        {
            if (!IsModUser(player)) { Reply(player, "NoPermission"); return; }
            ToggleMode(player, ModeMod);
        }

        [ChatCommand("radmin_as")]
        private void CmdAssignChest(BasePlayer player, string command, string[] args)
        {
            if (!HasStaffAccess(player)) { Reply(player, "NoPermission"); return; }
            if (args.Length != 2) { Reply(player, "UsageAssign"); return; }

            ulong targetId;
            if (!TryParseTarget(player, args[0], out targetId)) { Reply(player, "InvalidSteamId"); return; }

            int chestNumber;
            if (!int.TryParse(args[1], out chestNumber) || chestNumber < 1 || chestNumber > ChestCount)
            {
                Reply(player, "InvalidChestNumber");
                return;
            }

            var chest = Look(player) as StorageContainer;
            if (chest == null || chest.PrefabName != LargeBoxPrefab)
            {
                Reply(player, "LookAtLargeBox");
                return;
            }

            ulong netId = chest.net.ID.Value;
            int slot = chestNumber - 1;

            foreach (var entry in _data.Players)
            {
                for (int i = 0; i < ChestCount; i++)
                {
                    if (entry.Value.Chests[i] != netId || (entry.Key == targetId && i == slot)) continue;
                    Reply(player, "ChestAlreadyAssigned", entry.Key, i + 1);
                    return;
                }
            }

            string targetName = FindPlayerName(targetId) ?? Msg("Unknown", player.UserIDString);
            bool created;
            var info = GetOrCreateInfo(targetId, targetName, out created);
            if (created) Reply(player, "PlayerInitialized", targetId);

            if (info.Active)
            {
                Reply(player, "ChestAssignWhileActive", targetName);
                return;
            }

            info.Chests[slot] = netId;

            chest.skinID = AdminChestSkin;
            chest.SendNetworkUpdateImmediate();
            chest.inventory.Clear();
            ItemManager.DoRemoves();

            SaveData();
            Reply(player, "ChestAssigned", chestNumber, targetId);
        }

        [ChatCommand("radmin_chests")]
        private void CmdListChests(BasePlayer player, string command, string[] args)
        {
            if (!IsAdminUser(player)) { Reply(player, "NoPermission"); return; }

            if (_data.Players.Count == 0)
            {
                Reply(player, "NoChestsAssigned");
                return;
            }

            Reply(player, "ChestListHeader");
            foreach (var entry in _data.Players)
            {
                var info = entry.Value;
                if (string.IsNullOrEmpty(info.Name)) info.Name = FindPlayerName(entry.Key);
                Reply(player, "ChestListPlayer", info.Name ?? Msg("Unknown", player.UserIDString), entry.Key);

                for (int i = 0; i < ChestCount; i++)
                {
                    ulong netId = info.Chests[i];
                    string status = netId == 0
                        ? Msg("ChestUnassigned", player.UserIDString)
                        : Msg(FindChest(netId) != null ? "ChestFound" : "ChestNotFound", player.UserIDString, netId);
                    Reply(player, "ChestListEntry", i + 1, SlotName(i, player.UserIDString), status);
                }
                Reply(player, "ChestListSeparator");
            }
        }

        [ChatCommand("aclear")]
        private void CmdResetCapacity(BasePlayer player, string command, string[] args)
        {
            if (!IsAdminUser(player)) { Reply(player, "NoPermission"); return; }
            player.inventory.containerMain.capacity = 24;
            Reply(player, "CapacityReset", player.inventory.containerMain.itemList.Count);
        }

        [ChatCommand("radmin_r_m")]
        private void CmdReduceBackups(BasePlayer player, string command, string[] args)
        {
            if (!IsAdminUser(player)) { Reply(player, "NoPermission"); return; }

            var now = DateTime.Now;
            foreach (var owner in _data.Backups.Keys.ToList())
            {
                var backup = _data.Backups[owner];
                foreach (var key in backup.Entries.Keys.ToList())
                {
                    if ((now - DateTime.FromFileTime(key)).TotalMinutes > BackupMaxAgeMinutes)
                        backup.Entries.Remove(key);
                }
                backup.ListedKeys.RemoveAll(k => !backup.Entries.ContainsKey(k));
                if (backup.Entries.Count == 0) _data.Backups.Remove(owner);
            }

            SaveData();
            Reply(player, "BackupsReduced");
        }

        [ChatCommand("radmin_r_c")]
        private void CmdClearBackups(BasePlayer player, string command, string[] args)
        {
            if (!IsAdminUser(player)) { Reply(player, "NoPermission"); return; }
            _data.Backups.Clear();
            SaveData();
            Reply(player, "BackupsCleared");
        }

        [ChatCommand("radmin_a_restore")]
        private void CmdRestoreBackup(BasePlayer player, string command, string[] args)
        {
            if (!IsAdminUser(player)) { Reply(player, "NoPermission"); return; }
            if (args.Length == 0) { Reply(player, "RestoreUsage"); return; }

            ulong ownerId;
            if (!TryParseTarget(player, args[0], out ownerId)) { Reply(player, "InvalidSteamId"); return; }

            PlayerBackup backup;
            if (!_data.Backups.TryGetValue(ownerId, out backup) || backup.Entries.Count == 0)
            {
                Reply(player, "NoBackups", ownerId);
                return;
            }

            if (args.Length == 1)
            {
                Reply(player, "BackupListHeader", ownerId);
                backup.ListedKeys.Clear();

                int index = 0;
                foreach (var key in backup.Entries.Keys.OrderByDescending(x => x))
                {
                    backup.ListedKeys.Add(key);
                    Reply(player, "BackupListEntry", index++, DateTime.FromFileTime(key).ToString("dd.MM.yyyy HH:mm:ss"));
                }

                SaveData();
                Reply(player, "BackupListHintDetails");
                Reply(player, "BackupListHintConfirm");
                return;
            }

            int backupIndex;
            if (!int.TryParse(args[1], out backupIndex) || backupIndex < 0 || backupIndex >= backup.ListedKeys.Count)
            {
                Reply(player, "InvalidBackupIndex", Math.Max(0, backup.ListedKeys.Count - 1));
                return;
            }

            long selectedKey = backup.ListedKeys[backupIndex];
            ItemInfos entry;
            if (!backup.Entries.TryGetValue(selectedKey, out entry))
            {
                Reply(player, "BackupNotFound");
                return;
            }

            if (args.Length == 2)
            {
                Reply(player, "BackupDetailsHeader", backupIndex, DateTime.FromFileTime(selectedKey).ToString("dd.MM.yyyy HH:mm:ss"));
                foreach (var item in entry.Items)
                    Reply(player, "BackupDetailsEntry", item.shortname, item.amount, item.container, item.container_slot);
                Reply(player, "BackupListHintConfirm");
                return;
            }

            if (args.Length != 3 || !args[2].Equals("confirm", StringComparison.OrdinalIgnoreCase))
            {
                Reply(player, "RestoreUsage");
                return;
            }

            // Intended behaviour: the backup is restored into the inventory of the executing admin.
            if (IsActive(player.userID.Get()))
            {
                Reply(player, "RestoreWhileActive");
                return;
            }

            Reply(player, "RestoreStarted");
            RestoreBackupInto(player, entry);
        }

        private void RestoreBackupInto(BasePlayer player, ItemInfos entry)
        {
            ulong id = player.userID.Get();
            var backpack = GetBackpackContainer(id);

            PauseBackpackGather(id);
            ClearInventory(player);
            backpack?.Clear();
            ItemManager.DoRemoves();

            var dropped = new List<string>();
            foreach (var info in entry.Items)
            {
                var item = ItemManager.CreateByName(info.shortname, Math.Max(1, info.amount), info.skinID);
                if (item == null)
                {
                    PrintWarning($"Backup item '{info.shortname}' could not be created.");
                    continue;
                }

                if (info.attachments != null && item.contents != null)
                {
                    foreach (var attachmentName in info.attachments.Keys)
                    {
                        var attachment = ItemManager.CreateByName(attachmentName);
                        if (attachment != null && !attachment.MoveToContainer(item.contents)) attachment.Remove();
                    }
                }

                if (info.text != null) item.text = info.text;
                item.MarkDirty();

                ItemContainer target;
                switch (info.container)
                {
                    case "wear": target = player.inventory.containerWear; break;
                    case "belt": target = player.inventory.containerBelt; break;
                    case "backpack": target = backpack ?? player.inventory.containerMain; break;
                    default: target = player.inventory.containerMain; break;
                }

                if (item.MoveToContainer(target, info.container_slot) || item.MoveToContainer(target) || player.inventory.GiveItem(item)) continue;

                item.Drop(player.transform.position + Vector3.up, player.transform.forward * 2f);
                dropped.Add(info.shortname);
            }

            if (dropped.Count > 0) Reply(player, "ItemsDropped", string.Join(", ", dropped));
            Reply(player, "RestoreDone");
        }

        [ChatCommand("radmin_loot_sync")]
        private void CmdLootSync(BasePlayer player, string command, string[] args)
        {
            if (!IsAdminUser(player)) { Reply(player, "NoPermission"); return; }

            ulong targetId = player.userID.Get();
            if (args.Length > 0 && !TryParseTarget(player, args[0], out targetId))
            {
                Reply(player, "InvalidSteamId");
                return;
            }

            if (!_config.RealPve.Enabled || !IsRealPveLoaded())
            {
                Reply(player, "LootSyncUnavailable", _config.RealPve.PluginName);
                return;
            }

            // Flips the real RealPVE state once without changing what this plugin tracks.
            if (!RunRealPveLootToggle(targetId.ToString())) return;

            PlayerInfo info;
            bool active = _data.Players.TryGetValue(targetId, out info) && info.Active;
            Puts($"RealPVE loot access flipped manually for {targetId} by {player.displayName} (staff mode {(active ? "active" : "inactive")}).");
            Reply(player, "LootSyncDone", targetId, Msg(active ? "LootExpectedOn" : "LootExpectedOff", player.UserIDString));
        }

        [ChatCommand("radmin_ei")]
        private void CmdEntityInfo(BasePlayer player, string command, string[] args)
        {
            if (!IsAdminUser(player)) { Reply(player, "NoPermission"); return; }

            var position = player.transform.position;
            Reply(player, "Position", position.x, position.y, position.z);

            var entity = Look(player);
            if (entity == null)
            {
                Reply(player, "NotLookingAtEntity");
                return;
            }

            Reply(player, "EntityInfo", entity.net.ID.Value, entity.name ?? "N/A", entity.PrefabName ?? "N/A", entity.ShortPrefabName ?? "N/A", entity.skinID, entity.OwnerID);

            var backpack = GetBackpackContainer(player.userID.Get());
            if (backpack != null) Reply(player, "BackpackInfo", backpack.itemList.Count);
            else Reply(player, "BackpackUnavailable");
        }

        [ChatCommand("radmin_hand")]
        private void CmdHandInfo(BasePlayer player, string command, string[] args)
        {
            if (!IsAdminUser(player)) { Reply(player, "NoPermission"); return; }

            var item = player.GetActiveItem();
            if (item == null)
            {
                Reply(player, "NoItemInHand");
                return;
            }

            Reply(player, "HandItemInfo", item.info.shortname, item.uid.Value, item.skin, item.info.category, item.condition, item.amount);
        }

        #endregion
    }
}
