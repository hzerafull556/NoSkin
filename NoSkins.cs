using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Rocket.API;
using Rocket.Core.Plugins;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Events;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace NoSkins
{
    public class NoSkinsPlugin : RocketPlugin<NoSkinsConfiguration>
    {
        private const string BypassPermission = "noskins.bypass";
        private readonly HashSet<ulong> _initializedPlayers = new HashSet<ulong>();
        private readonly object _sync = new object();
        private readonly object _respawnSync = new object();
        private readonly Dictionary<ulong, bool> _deadState = new Dictionary<ulong, bool>();
        private readonly Dictionary<ulong, float> _respawnApplyUntil = new Dictionary<ulong, float>();
        private readonly object _cosmeticCacheSync = new object();
        private readonly Dictionary<ushort, bool> _cosmeticCache = new Dictionary<ushort, bool>();
        private readonly object _initSync = new object();
        private readonly HashSet<ulong> _initializingPlayers = new HashSet<ulong>();
        private readonly object _sessionSync = new object();
        private readonly HashSet<ulong> _sessionInitializedPlayers = new HashSet<ulong>();
        private Coroutine _monitorCoroutine;
        private string _firstJoinDataPath;
        private bool _coreEventsRegistered;
        private bool _isShuttingDown;
        private readonly object _saveSync = new object();
        private bool _pendingSave;
        private DateTime _nextSaveAtUtc;
        private static readonly object _clothingGetterSync = new object();
        private static Dictionary<string, Func<PlayerClothing, ushort>> _clothingGetters;
        private static readonly string[] DefaultCosmeticFlags = { "isCosmetic", "isSkin", "isSkinned", "isPro", "isMythic", "isMythical", "isWorkshop" };
        private static readonly string[] DefaultCosmeticNameKeywords = { "cosmetic", "skin", "premium", "dlc", "workshop", "twitch", "mythic" };

        protected override void Load()
        {
            _isShuttingDown = false;
            string baseDir = string.IsNullOrWhiteSpace(Directory)
                ? System.IO.Directory.GetCurrentDirectory()
                : Directory;
            _firstJoinDataPath = Path.Combine(baseDir, "first_join_players.txt");

            try
            {
                LoadFirstJoinData();
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[NoSkins] Erro ao carregar dados de first join.");
            }

            RegisterHooks();
            StartMonitor();

            Logger.Log($"[NoSkins] Carregado. Jogadores ja inicializados: {_initializedPlayers.Count}.");
        }

        protected override void Unload()
        {
            try
            {
                _isShuttingDown = true;
                StopMonitor();
                UnregisterHooks();
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[NoSkins] Erro ao remover eventos.");
            }

            SaveFirstJoinData();
            Logger.Log("[NoSkins] Descarregado.");
        }

        private void RegisterHooks()
        {
            try
            {
                if (_coreEventsRegistered)
                    return;

                UnturnedPlayerEvents.OnPlayerWear += OnPlayerWear;
                _coreEventsRegistered = true;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[NoSkins] Erro ao registrar eventos base.");
                _coreEventsRegistered = false;
            }
        }

        private void UnregisterHooks()
        {
            if (!_coreEventsRegistered)
                return;

            UnturnedPlayerEvents.OnPlayerWear -= OnPlayerWear;
            _coreEventsRegistered = false;
        }

        private void StartMonitor()
        {
            if (_monitorCoroutine != null)
                return;
            _monitorCoroutine = StartCoroutine(MonitorRoutine());
        }

        private void StopMonitor()
        {
            if (_monitorCoroutine == null)
                return;
            StopCoroutine(_monitorCoroutine);
            _monitorCoroutine = null;
        }

        private IEnumerator MonitorRoutine()
        {
            yield return new WaitForSeconds(1f);

            while (true)
            {
                if (_isShuttingDown)
                    yield break;

                var online = new HashSet<ulong>();
                var clients = Provider.clients;

                if (clients != null && clients.Count > 0)
                {
                    List<SteamPlayer> snapshot = null;
                    try
                    {
                        snapshot = new List<SteamPlayer>(clients);
                    }
                    catch
                    {
                        snapshot = null;
                    }

                    if (snapshot != null)
                    {
                        foreach (var client in snapshot)
                        {
                            if (_isShuttingDown)
                                yield break;

                            var player = client?.player;
                            if (player == null) continue;

                            var unturnedPlayer = UnturnedPlayer.FromPlayer(player);
                            if (unturnedPlayer == null) continue;

                            ulong steamId = unturnedPlayer.CSteamID.m_SteamID;
                            online.Add(steamId);

                            TryQueueInitialize(unturnedPlayer);
                            TrackRespawnTransition(unturnedPlayer);

                            if (unturnedPlayer.Player?.clothing != null)
                            {
                                EnforceVisualRestrictions(unturnedPlayer);
                                // So remove cosmeticos se NAO estiver usando apenas StarterOutfit
                                if (!IsWearingOnlyStarterOutfit(unturnedPlayer))
                                {
                                    EnforceCosmeticRemoval(unturnedPlayer);
                                }
                            }

                            TryApplyRespawnOutfit(unturnedPlayer);
                        }
                    }
                    else
                    {
                        int count = 0;
                        try
                        {
                            count = clients.Count;
                        }
                        catch
                        {
                            count = 0;
                        }

                        for (int i = 0; i < count; i++)
                        {
                            if (_isShuttingDown)
                                yield break;

                            SteamPlayer client = null;
                            try
                            {
                                client = clients[i];
                            }
                            catch
                            {
                                continue;
                            }

                            var player = client?.player;
                            if (player == null) continue;

                            var unturnedPlayer = UnturnedPlayer.FromPlayer(player);
                            if (unturnedPlayer == null) continue;

                            ulong steamId = unturnedPlayer.CSteamID.m_SteamID;
                            online.Add(steamId);

                            TryQueueInitialize(unturnedPlayer);
                            TrackRespawnTransition(unturnedPlayer);

                            if (unturnedPlayer.Player?.clothing != null)
                            {
                                EnforceVisualRestrictions(unturnedPlayer);
                                // So remove cosmeticos se NAO estiver usando apenas StarterOutfit
                                if (!IsWearingOnlyStarterOutfit(unturnedPlayer))
                                {
                                    EnforceCosmeticRemoval(unturnedPlayer);
                                }
                            }

                            TryApplyRespawnOutfit(unturnedPlayer);
                        }
                    }
                }

                CleanupOfflinePlayers(online);
                TryFlushPendingSave();
                yield return new WaitForSeconds(GetMonitorIntervalSeconds());
            }
        }

        private float GetMonitorIntervalSeconds()
        {
            float value = 1f;
            try
            {
                value = Configuration?.Instance?.MonitorIntervalSeconds ?? 1f;
            }
            catch
            {
                value = 1f;
            }

            if (value <= 0f)
                value = 1f;
            if (value < 0.25f)
                value = 0.25f;
            if (value > 10f)
                value = 10f;
            return value;
        }

        private float GetSaveIntervalSeconds()
        {
            float value = 15f;
            try
            {
                value = Configuration?.Instance?.SaveIntervalSeconds ?? 15f;
            }
            catch
            {
                value = 15f;
            }

            if (value <= 0f)
                value = 15f;
            if (value < 1f)
                value = 1f;
            if (value > 120f)
                value = 120f;
            return value;
        }

        private void QueueSaveFirstJoinData()
        {
            lock (_saveSync)
            {
                _pendingSave = true;
                if (_nextSaveAtUtc == DateTime.MinValue || _nextSaveAtUtc <= DateTime.UtcNow)
                {
                    _nextSaveAtUtc = DateTime.UtcNow.AddSeconds(GetSaveIntervalSeconds());
                }
            }
        }

        private void TryFlushPendingSave()
        {
            if (_isShuttingDown)
                return;

            bool shouldSave = false;
            lock (_saveSync)
            {
                if (_pendingSave && _nextSaveAtUtc != DateTime.MinValue && DateTime.UtcNow >= _nextSaveAtUtc)
                {
                    shouldSave = true;
                    _pendingSave = false;
                    _nextSaveAtUtc = DateTime.MinValue;
                }
            }

            if (shouldSave)
                SaveFirstJoinData();
        }

        private void TryQueueInitialize(UnturnedPlayer player)
        {
            if (player == null || _isShuttingDown) return;

            ulong steamId = player.CSteamID.m_SteamID;

            lock (_sessionSync)
            {
                if (_sessionInitializedPlayers.Contains(steamId))
                    return;
            }

            lock (_initSync)
            {
                if (_initializingPlayers.Contains(steamId))
                    return;
            }

            StartCoroutine(InitializePlayerRoutine(player));
        }

        private void CleanupOfflinePlayers(HashSet<ulong> online)
        {
            if (online == null) return;

            lock (_sessionSync)
            {
                var remove = _sessionInitializedPlayers.Where(id => !online.Contains(id)).ToList();
                foreach (var id in remove)
                    _sessionInitializedPlayers.Remove(id);
            }

            lock (_respawnSync)
            {
                var removeDead = _deadState.Keys.Where(id => !online.Contains(id)).ToList();
                foreach (var id in removeDead)
                    _deadState.Remove(id);

                var removeRespawn = _respawnApplyUntil.Keys.Where(id => !online.Contains(id)).ToList();
                foreach (var id in removeRespawn)
                    _respawnApplyUntil.Remove(id);
            }
        }

        private void TrackRespawnTransition(UnturnedPlayer player)
        {
            if (_isShuttingDown) return;
            if (player?.Player == null) return;

            ulong steamId = player.CSteamID.m_SteamID;
            bool isDead = player.Player.life == null || player.Player.life.isDead;
            float now = Time.realtimeSinceStartup;

            lock (_respawnSync)
            {
                _deadState.TryGetValue(steamId, out bool wasDead);

                if (isDead)
                {
                    _deadState[steamId] = true;
                    return;
                }

                if (wasDead)
                {
                    _deadState[steamId] = false;
                    _respawnApplyUntil[steamId] = now + 8f;
                }
                else if (!_deadState.ContainsKey(steamId))
                {
                    _deadState[steamId] = false;
                }
            }
        }

        private bool IsInRespawnApplyWindow(ulong steamId)
        {
            float now = Time.realtimeSinceStartup;

            lock (_respawnSync)
            {
                if (!_respawnApplyUntil.TryGetValue(steamId, out float until))
                    return false;

                if (now > until)
                {
                    _respawnApplyUntil.Remove(steamId);
                    return false;
                }

                return true;
            }
        }

        private void ClearRespawnApplyWindow(ulong steamId)
        {
            lock (_respawnSync)
            {
                _respawnApplyUntil.Remove(steamId);
            }
        }

        private void TryApplyRespawnOutfit(UnturnedPlayer player)
        {
            if (_isShuttingDown) return;
            if (player?.Player == null) return;
            if (HasBypassPermission(player)) return;

            bool isDead = player.Player.life == null || player.Player.life.isDead;
            if (isDead) return;

            ulong steamId = player.CSteamID.m_SteamID;
            if (!IsInRespawnApplyWindow(steamId)) return;

            if (player.Player.clothing == null) return;

            // Requisito: aplicar apenas no respawn e apenas se nascer pelado.
            if (!IsPlayerNaked(player))
            {
                ClearRespawnApplyWindow(steamId);
                return;
            }

            EnsureOutfitIfNaked(player);

            if (!IsPlayerNaked(player))
            {
                EnforceVisualRestrictions(player);
                ClearRespawnApplyWindow(steamId);
            }
        }


        private IEnumerator InitializePlayerRoutine(UnturnedPlayer player)
        {
            if (player == null) yield break;
            if (_isShuttingDown) yield break;

            ulong steamId = player.CSteamID.m_SteamID;
            lock (_initSync)
            {
                if (_initializingPlayers.Contains(steamId))
                    yield break;
                _initializingPlayers.Add(steamId);
            }

            bool initialized = false;

            try
            {
                const float step = 0.25f;
                const float timeout = 6f;
                float waited = 0f;

                while (waited < timeout)
                {
                    if (_isShuttingDown) yield break;
                    if (IsDisconnected(player)) yield break;
                    if (player?.Player?.clothing != null) break;
                    yield return new WaitForSeconds(step);
                    waited += step;
                }

                if (player?.Player?.clothing == null)
                {
                    Logger.LogWarning($"[NoSkins] Clothing nao disponivel para {player.CharacterName}. Inicializacao ignorada.");
                    yield break;
                }

                try
                {
                    if (_isShuttingDown) yield break;
                    // Aplica bloqueios imediatamente
                    EnforceVisualRestrictions(player);

                    // Verifica se e first join
                    bool isFirstJoin = TryMarkFirstJoin(steamId);

                    if (isFirstJoin)
                    {
                        Logger.Log($"[NoSkins] Primeira entrada detectada: {player.CharacterName}");
                        StartCoroutine(HandleFirstJoinRoutine(player));
                    }
                    else
                    {
                        Logger.Log($"[NoSkins] Jogador existente conectado: {player.CharacterName}");
                    }

                    initialized = true;
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex, "[NoSkins] Erro ao inicializar jogador.");
                }
            }
            finally
            {
                lock (_initSync)
                {
                    _initializingPlayers.Remove(steamId);
                }

                if (initialized)
                {
                    lock (_sessionSync)
                    {
                        _sessionInitializedPlayers.Add(steamId);
                    }
                }
            }
        }


        private IEnumerator HandleFirstJoinRoutine(UnturnedPlayer player)
        {
            if (player?.Player?.clothing == null) yield break;
            if (_isShuttingDown) yield break;

            var outfit = Configuration.Instance?.StarterOutfit;
            bool hasAnyOutfit = HasAnyOutfitItem(outfit);
            bool hasValidOutfit = HasAnyValidOutfitItem(outfit);

            if (Configuration.Instance.RemoveWearablesOnFirstJoin)
            {
                if (!hasAnyOutfit || !hasValidOutfit)
                {
                    Logger.LogWarning("[NoSkins] StarterOutfit vazio/invalido. Remocao de roupas ignorada para evitar jogador pelado.");
                }
                else
                {
                    RemoveAllWearables(player);
                    yield return new WaitForSeconds(0.2f); // Pequeno delay apos remocao
                }
            }

            if (_isShuttingDown) yield break;
            if (IsDisconnected(player)) yield break;
            if (player?.Player?.clothing == null) yield break;

            if (hasAnyOutfit && hasValidOutfit)
                EnsureOutfitIfNaked(player);

            if (!string.IsNullOrWhiteSpace(Configuration.Instance.FirstJoinMessage))
            {
                UnturnedChat.Say(player, Configuration.Instance.FirstJoinMessage, Color.yellow);
            }
        }
        private void OnPlayerWear(UnturnedPlayer player, UnturnedPlayerEvents.Wearables wear, ushort id, byte? quality)
        {
            try
            {
                if (player?.Player?.clothing == null) return;
                if (HasBypassPermission(player)) return;

                EnforceVisualRestrictions(player);

                // ? CORRECAO 4: Verifica se e skin/cosmetico e remove se necessario
                if (Configuration.Instance.BlockSkins || Configuration.Instance.BlockCosmetics)
                {
                    if (IsCosmeticItem(id))
                    {
                        Logger.Log($"[NoSkins] Skin detectada em {player.CharacterName}, removendo...");

                        // Remove imediatamente e reforca no proximo frame
                        RemoveCosmeticForWear(player, wear);
                        StartCoroutine(RemoveCosmeticNextFrame(player, wear));
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "[NoSkins] Erro no OnPlayerWear.");
            }
        }

        // ? NOVO: Verifica se item e cosmetico/skin
        private bool IsCosmeticItem(ushort id)
        {
            if (id == 0) return false;

            // Nunca bloqueia itens do StarterOutfit
            var outfit = Configuration.Instance?.StarterOutfit;
            if (outfit != null)
            {
                if (id == outfit.ShirtId || id == outfit.PantsId || id == outfit.HatId ||
                    id == outfit.BackpackId || id == outfit.VestId || id == outfit.MaskId ||
                    id == outfit.GlassesId)
                {
                    return false;
                }
            }

            lock (_cosmeticCacheSync)
            {
                if (_cosmeticCache.TryGetValue(id, out bool cached))
                    return cached;
            }

            var asset = Assets.find(EAssetType.ITEM, id) as ItemAsset;
            if (asset == null)
            {
                CacheCosmetic(id, false);
                return false;
            }

            if (!(asset is ItemClothingAsset))
            {
                CacheCosmetic(id, false);
                return false;
            }

            bool result = false;

            string[] flags = Configuration.Instance?.CosmeticFlags;
            if (flags == null || flags.Length == 0) flags = DefaultCosmeticFlags;

            foreach (var flag in flags)
            {
                if (TryGetBoolMember(asset, flag, out bool flagValue) && flagValue)
                {
                    result = true;
                    break;
                }
            }

            string name = asset.name?.ToLowerInvariant() ?? "";
            string[] keywords = Configuration.Instance?.CosmeticNameKeywords;
            if (keywords == null || keywords.Length == 0) keywords = DefaultCosmeticNameKeywords;

            if (!result)
            {
                foreach (var keyword in keywords)
                {
                    if (name.Contains(keyword))
                    {
                        result = true;
                        break;
                    }
                }
            }

            CacheCosmetic(id, result);
            return result;
        }

        private static bool TryGetBoolMember(object target, string name, out bool value)
        {
            value = false;

            if (target == null || string.IsNullOrEmpty(name)) return false;

            var type = target.GetType();
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(bool))
            {
                value = (bool)field.GetValue(target);
                return true;
            }

            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(bool) && prop.GetIndexParameters().Length == 0)
            {
                value = (bool)prop.GetValue(target, null);
                return true;
            }

            return false;
        }

        private IEnumerator RemoveCosmeticNextFrame(UnturnedPlayer player, UnturnedPlayerEvents.Wearables wear)
        {
            if (_isShuttingDown) yield break;
            yield return null; // Aguarda proximo frame

            if (IsDisconnected(player)) yield break;
            if (player?.Player?.clothing == null) yield break;

            RemoveCosmeticForWear(player, wear);

            if (!string.IsNullOrWhiteSpace(Configuration.Instance.SkinBlockedMessage))
            {
                UnturnedChat.Say(player, Configuration.Instance.SkinBlockedMessage, Color.red);
            }
        }

        private void RemoveCosmeticForWear(UnturnedPlayer player, UnturnedPlayerEvents.Wearables wear)
        {
            if (IsDisconnected(player)) return;
            if (player?.Player?.clothing == null) return;

            var clothing = player.Player.clothing;
            byte[] emptyState = new byte[0];
            var outfit = Configuration.Instance.StarterOutfit;
            byte quality = ClampByte(outfit?.Quality ?? 100);
            bool playEffect = outfit?.PlayEffect ?? false;

            switch (wear)
            {
                case UnturnedPlayerEvents.Wearables.Shirt:
                    if (IsValidClothingItem(outfit?.ShirtId ?? 0, typeof(ItemShirtAsset)))
                    {
                        clothing.askWearShirt(0, 0, emptyState, false);
                        TryWearShirt(clothing, outfit?.ShirtId ?? 0, quality, emptyState, playEffect);
                    }
                    break;
                case UnturnedPlayerEvents.Wearables.Pants:
                    if (IsValidClothingItem(outfit?.PantsId ?? 0, typeof(ItemPantsAsset)))
                    {
                        clothing.askWearPants(0, 0, emptyState, false);
                        TryWearPants(clothing, outfit?.PantsId ?? 0, quality, emptyState, playEffect);
                    }
                    break;
                case UnturnedPlayerEvents.Wearables.Hat:
                    clothing.askWearHat(0, 0, emptyState, false);
                    break;
                case UnturnedPlayerEvents.Wearables.Backpack:
                    clothing.askWearBackpack(0, 0, emptyState, false);
                    break;
                case UnturnedPlayerEvents.Wearables.Vest:
                    clothing.askWearVest(0, 0, emptyState, false);
                    break;
                case UnturnedPlayerEvents.Wearables.Mask:
                    clothing.askWearMask(0, 0, emptyState, false);
                    break;
                case UnturnedPlayerEvents.Wearables.Glasses:
                    clothing.askWearGlasses(0, 0, emptyState, false);
                    break;
            }
        }

        private void EnforceVisualRestrictions(UnturnedPlayer player)
        {
            var config = Configuration?.Instance;
            if (config == null) return;
            if (HasBypassPermission(player)) return;

            PlayerClothing clothing = player.Player.clothing;
            if (clothing == null) return;

            // ? Aplica todos os bloqueios de uma vez
            if (config.BlockCosmetics)
                clothing.ServerSetVisualToggleState(EVisualToggleType.COSMETIC, false);

            if (config.BlockSkins)
                clothing.ServerSetVisualToggleState(EVisualToggleType.SKIN, false);

            if (config.BlockMythics)
                clothing.ServerSetVisualToggleState(EVisualToggleType.MYTHIC, false);
        }

        private void EnforceCosmeticRemoval(UnturnedPlayer player)
        {
            var config = Configuration?.Instance;
            if (config == null) return;
            if (HasBypassPermission(player)) return;
            if (!config.BlockSkins && !config.BlockCosmetics) return;

            var clothing = player?.Player?.clothing;
            if (clothing == null) return;

            CheckCosmeticSlot(player, UnturnedPlayerEvents.Wearables.Shirt, GetClothingId(clothing, "shirt"));
            CheckCosmeticSlot(player, UnturnedPlayerEvents.Wearables.Pants, GetClothingId(clothing, "pants"));
            CheckCosmeticSlot(player, UnturnedPlayerEvents.Wearables.Hat, GetClothingId(clothing, "hat"));
            CheckCosmeticSlot(player, UnturnedPlayerEvents.Wearables.Backpack, GetClothingId(clothing, "backpack"));
            CheckCosmeticSlot(player, UnturnedPlayerEvents.Wearables.Vest, GetClothingId(clothing, "vest"));
            CheckCosmeticSlot(player, UnturnedPlayerEvents.Wearables.Mask, GetClothingId(clothing, "mask"));
            CheckCosmeticSlot(player, UnturnedPlayerEvents.Wearables.Glasses, GetClothingId(clothing, "glasses"));
        }

        private void EnsureOutfitIfNaked(UnturnedPlayer player)
        {
            if (player?.Player?.clothing == null) return;

            var outfit = Configuration.Instance?.StarterOutfit;
            if (!HasAnyValidOutfitItem(outfit)) return;

            if (IsPlayerNaked(player))
                ApplyStarterOutfit(player);
        }

        private bool IsPlayerNaked(UnturnedPlayer player)
        {
            var clothing = player?.Player?.clothing;
            if (clothing == null) return false;

            ushort shirt = GetClothingId(clothing, "shirt");
            ushort pants = GetClothingId(clothing, "pants");
            return shirt == 0 && pants == 0;
        }

        private bool IsWearingOnlyStarterOutfit(UnturnedPlayer player)
        {
            var clothing = player?.Player?.clothing;
            if (clothing == null) return false;

            var outfit = Configuration.Instance?.StarterOutfit;
            if (outfit == null) return false;

            ushort shirt = GetClothingId(clothing, "shirt");
            ushort pants = GetClothingId(clothing, "pants");
            ushort hat = GetClothingId(clothing, "hat");
            ushort backpack = GetClothingId(clothing, "backpack");
            ushort vest = GetClothingId(clothing, "vest");
            ushort mask = GetClothingId(clothing, "mask");
            ushort glasses = GetClothingId(clothing, "glasses");

            bool onlyStarterItems = true;
            if (shirt != 0 && shirt != outfit.ShirtId) onlyStarterItems = false;
            if (pants != 0 && pants != outfit.PantsId) onlyStarterItems = false;
            if (hat != 0 && hat != outfit.HatId) onlyStarterItems = false;
            if (backpack != 0 && backpack != outfit.BackpackId) onlyStarterItems = false;
            if (vest != 0 && vest != outfit.VestId) onlyStarterItems = false;
            if (mask != 0 && mask != outfit.MaskId) onlyStarterItems = false;
            if (glasses != 0 && glasses != outfit.GlassesId) onlyStarterItems = false;

            return onlyStarterItems;
        }

        private void CheckCosmeticSlot(UnturnedPlayer player, UnturnedPlayerEvents.Wearables wear, ushort id)
        {
            if (id == 0) return;
            if (!IsCosmeticItem(id)) return;

            RemoveCosmeticForWear(player, wear);
        }

        private static ushort GetClothingId(PlayerClothing clothing, string name)
        {
            if (clothing == null || string.IsNullOrEmpty(name)) return 0;

            Func<PlayerClothing, ushort> getter;
            lock (_clothingGetterSync)
            {
                if (_clothingGetters == null)
                    _clothingGetters = new Dictionary<string, Func<PlayerClothing, ushort>>(StringComparer.OrdinalIgnoreCase);

                if (!_clothingGetters.TryGetValue(name, out getter))
                {
                    getter = BuildClothingGetter(name);
                    _clothingGetters[name] = getter;
                }
            }

            try
            {
                return getter(clothing);
            }
            catch
            {
                return 0;
            }
        }

        private static Func<PlayerClothing, ushort> BuildClothingGetter(string name)
        {
            var type = typeof(PlayerClothing);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(ushort))
                return clothing => (ushort)field.GetValue(clothing);

            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null && prop.PropertyType == typeof(ushort) && prop.GetIndexParameters().Length == 0)
                return clothing => (ushort)prop.GetValue(clothing, null);

            return clothing => 0;
        }

        private void RemoveAllWearables(UnturnedPlayer player)
        {
            PlayerClothing clothing = player.Player.clothing;
            if (clothing == null) return;

            byte[] emptyState = new byte[0]; // ? Mantido, mas testar se null funciona melhor

            // Remove em ordem especifica (corpo primeiro, acessorios depois)
            clothing.askWearShirt(0, 0, emptyState, false);
            clothing.askWearPants(0, 0, emptyState, false);
            clothing.askWearVest(0, 0, emptyState, false);
            clothing.askWearBackpack(0, 0, emptyState, false);
            clothing.askWearHat(0, 0, emptyState, false);
            clothing.askWearMask(0, 0, emptyState, false);
            clothing.askWearGlasses(0, 0, emptyState, false);
        }

        private void ApplyStarterOutfit(UnturnedPlayer player)
        {
            PlayerClothing clothing = player.Player.clothing;
            if (clothing == null) return;

            var outfit = Configuration.Instance.StarterOutfit ?? new StarterOutfitConfiguration();
            byte quality = ClampByte(outfit.Quality);
            bool playEffect = outfit.PlayEffect;
            byte[] state = new byte[0];
            bool appliedAny = false;

            // ? ORDEM CORRIGIDA: Vestes de baixo primeiro, depois de cima
            appliedAny |= TryWearPants(clothing, outfit.PantsId, quality, state, playEffect);
            appliedAny |= TryWearShirt(clothing, outfit.ShirtId, quality, state, playEffect);
            appliedAny |= TryWearVest(clothing, outfit.VestId, quality, state, playEffect);
            appliedAny |= TryWearBackpack(clothing, outfit.BackpackId, quality, state, playEffect);
            appliedAny |= TryWearHat(clothing, outfit.HatId, quality, state, playEffect);
            appliedAny |= TryWearMask(clothing, outfit.MaskId, quality, state, playEffect);
            appliedAny |= TryWearGlasses(clothing, outfit.GlassesId, quality, state, playEffect);

            if (!appliedAny && HasAnyOutfitItem(outfit))
            {
                Logger.LogWarning("[NoSkins] StarterOutfit nao aplicou nenhum item. Verifique IDs no config.");
            }
        }

        private static bool TryWearShirt(PlayerClothing clothing, ushort id, byte quality, byte[] state, bool playEffect)
        {
            if (!IsValidClothingItem(id, typeof(ItemShirtAsset))) return false;
            clothing.askWearShirt(id, quality, state, playEffect);
            return true;
        }

        private static bool TryWearPants(PlayerClothing clothing, ushort id, byte quality, byte[] state, bool playEffect)
        {
            if (!IsValidClothingItem(id, typeof(ItemPantsAsset))) return false;
            clothing.askWearPants(id, quality, state, playEffect);
            return true;
        }

        private static bool TryWearHat(PlayerClothing clothing, ushort id, byte quality, byte[] state, bool playEffect)
        {
            if (!IsValidClothingItem(id, typeof(ItemHatAsset))) return false;
            clothing.askWearHat(id, quality, state, playEffect);
            return true;
        }

        private static bool TryWearBackpack(PlayerClothing clothing, ushort id, byte quality, byte[] state, bool playEffect)
        {
            if (!IsValidClothingItem(id, typeof(ItemBackpackAsset))) return false;
            clothing.askWearBackpack(id, quality, state, playEffect);
            return true;
        }

        private static bool TryWearVest(PlayerClothing clothing, ushort id, byte quality, byte[] state, bool playEffect)
        {
            if (!IsValidClothingItem(id, typeof(ItemVestAsset))) return false;
            clothing.askWearVest(id, quality, state, playEffect);
            return true;
        }

        private static bool TryWearMask(PlayerClothing clothing, ushort id, byte quality, byte[] state, bool playEffect)
        {
            if (!IsValidClothingItem(id, typeof(ItemMaskAsset))) return false;
            clothing.askWearMask(id, quality, state, playEffect);
            return true;
        }

        private static bool TryWearGlasses(PlayerClothing clothing, ushort id, byte quality, byte[] state, bool playEffect)
        {
            if (!IsValidClothingItem(id, typeof(ItemGlassesAsset))) return false;
            clothing.askWearGlasses(id, quality, state, playEffect);
            return true;
        }

        private static bool HasAnyOutfitItem(StarterOutfitConfiguration outfit)
        {
            if (outfit == null) return false;
            return outfit.ShirtId != 0
                || outfit.PantsId != 0
                || outfit.HatId != 0
                || outfit.BackpackId != 0
                || outfit.VestId != 0
                || outfit.MaskId != 0
                || outfit.GlassesId != 0;
        }

        private static bool HasAnyValidOutfitItem(StarterOutfitConfiguration outfit)
        {
            if (outfit == null) return false;
            return IsValidClothingItem(outfit.ShirtId, typeof(ItemShirtAsset))
                || IsValidClothingItem(outfit.PantsId, typeof(ItemPantsAsset))
                || IsValidClothingItem(outfit.HatId, typeof(ItemHatAsset))
                || IsValidClothingItem(outfit.BackpackId, typeof(ItemBackpackAsset))
                || IsValidClothingItem(outfit.VestId, typeof(ItemVestAsset))
                || IsValidClothingItem(outfit.MaskId, typeof(ItemMaskAsset))
                || IsValidClothingItem(outfit.GlassesId, typeof(ItemGlassesAsset));
        }

        private static bool IsValidClothingItem(ushort id, Type expectedAssetType)
        {
            if (id == 0) return false;

            try
            {
                ItemAsset item = Assets.find(EAssetType.ITEM, id) as ItemAsset;
                if (item == null)
                {
                    Logger.LogWarning($"[NoSkins] Item ID {id} nao encontrado nos assets!");
                    return false;
                }

                return expectedAssetType.IsInstanceOfType(item);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[NoSkins] Erro ao validar item {id}: {ex.Message}");
                return false;
            }
        }

        private static byte ClampByte(byte value)
        {
            return value > 100 ? (byte)100 : value;
        }

        private bool TryMarkFirstJoin(ulong steamId)
        {
            lock (_sync)
            {
                if (_initializedPlayers.Contains(steamId))
                    return false;

                _initializedPlayers.Add(steamId);
                // ? CORRECAO 5: I/O fora do lock
            }

            // Salva fora do lock para nao bloquear thread (debounced)
            QueueSaveFirstJoinData();
            return true;
        }

        private void LoadFirstJoinData()
        {
            lock (_sync)
            {
                _initializedPlayers.Clear();

                if (string.IsNullOrWhiteSpace(_firstJoinDataPath) || !File.Exists(_firstJoinDataPath))
                    return;

                foreach (string line in File.ReadAllLines(_firstJoinDataPath))
                {
                    if (ulong.TryParse(line?.Trim(), out ulong steamId))
                        _initializedPlayers.Add(steamId);
                }
            }
        }

        private void SaveFirstJoinData()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_firstJoinDataPath))
                    return;

                string folder = Path.GetDirectoryName(_firstJoinDataPath);
                if (!string.IsNullOrWhiteSpace(folder))
                    System.IO.Directory.CreateDirectory(folder);

                // ? CORRECAO 6: Copia dos dados para evitar lock durante I/O
                ulong[] playersCopy;
                lock (_sync)
                {
                    playersCopy = _initializedPlayers.ToArray();
                }

                File.WriteAllLines(_firstJoinDataPath, playersCopy.OrderBy(x => x).Select(x => x.ToString()));
            }
            catch (Exception ex)
            {
                Logger.LogError($"[NoSkins] Erro ao salvar dados: {ex.Message}");
            }
        }

        private bool IsDisconnected(UnturnedPlayer player)
        {
            try
            {
                if (player == null) return true;
                ulong steamId;
                try
                {
                    steamId = player.CSteamID.m_SteamID;
                }
                catch
                {
                    return true;
                }

                var clients = Provider.clients;
                if (clients == null || clients.Count == 0)
                    return true;

                for (int i = 0; i < clients.Count; i++)
                {
                    var c = clients[i];
                    try
                    {
                        var pid = c?.playerID;
                        if (pid != null && pid.steamID.m_SteamID == steamId)
                            return false;
                    }
                    catch
                    {
                        // ignore invalid client entry
                    }
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        private void CacheCosmetic(ushort id, bool value)
        {
            lock (_cosmeticCacheSync)
            {
                _cosmeticCache[id] = value;
            }
        }

        private bool HasBypassPermission(UnturnedPlayer player)
        {
            if (player == null) return false;

            try
            {
                return player.HasPermission(BypassPermission);
            }
            catch
            {
                return false;
            }
        }
    }

    public class NoSkinsConfiguration : IRocketPluginConfiguration
    {
        public bool BlockCosmetics { get; set; }
        public bool BlockSkins { get; set; }
        public bool BlockMythics { get; set; }
        public bool RemoveWearablesOnFirstJoin { get; set; }
        public float MonitorIntervalSeconds { get; set; }
        public float SaveIntervalSeconds { get; set; }
        public string FirstJoinMessage { get; set; }
        public string SkinBlockedMessage { get; set; } // ? NOVO
        public string[] CosmeticFlags { get; set; }
        public string[] CosmeticNameKeywords { get; set; }
        public StarterOutfitConfiguration StarterOutfit { get; set; }

        public void LoadDefaults()
        {
            BlockCosmetics = true;
            BlockSkins = true;
            BlockMythics = true;
            RemoveWearablesOnFirstJoin = true;
            MonitorIntervalSeconds = 1f;
            SaveIntervalSeconds = 15f;
            FirstJoinMessage = "Seus cosmeticos foram desativados e a roupa inicial foi aplicada.";
            SkinBlockedMessage = "Skins/Cosmeticos de inventario estao bloqueados neste servidor!"; // ? NOVO
            CosmeticFlags = new[]
            {
                "isCosmetic",
                "isSkin",
                "isSkinned",
                "isPro",
                "isMythic",
                "isMythical",
                "isWorkshop"
            };
            CosmeticNameKeywords = new[]
            {
                "cosmetic",
                "skin",
                "premium",
                "dlc",
                "workshop",
                "twitch",
                "mythic"
            };
            StarterOutfit = new StarterOutfitConfiguration
            {
                ShirtId = 211,  // ? Padrao: Plaid Shirt
                PantsId = 212,  // ? Padrao: Khaki Pants
                HatId = 0,
                BackpackId = 0,
                VestId = 0,
                MaskId = 0,
                GlassesId = 0,
                Quality = 100,
                PlayEffect = true
            };
        }
    }

    public class StarterOutfitConfiguration
    {
        public ushort ShirtId { get; set; }
        public ushort PantsId { get; set; }
        public ushort HatId { get; set; }
        public ushort BackpackId { get; set; }
        public ushort VestId { get; set; }
        public ushort MaskId { get; set; }
        public ushort GlassesId { get; set; }
        public byte Quality { get; set; }
        public bool PlayEffect { get; set; }
    }
}
