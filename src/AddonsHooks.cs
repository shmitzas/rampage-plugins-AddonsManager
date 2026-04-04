using AddonsManager.Clients;
using AddonsManager.Config;
using AddonsManager.Extensions;
using AddonsManager.SteamWorkshop;
using AddonsManager.Structs;
using AddonsManager.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Memory;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Natives;
using SwiftlyS2.Shared.NetMessages;
using SwiftlyS2.Shared.ProtobufDefinitions;

namespace AddonsManager.Hooks;

public class AddonsHooks
{
    private ISwiftlyCore Core;
    private AddonsWorkshopManager WorkshopManager;
    private AddonsClients Clients;
    private AddonsUtilities Utilities;

    public unsafe delegate void SetPendingHostStateRequestDelegate(nint hostStateManager, CHostStateRequest* pRequest);
    public unsafe delegate void ReplyConnection(nint server, CServerSideClient* client);
    public delegate ulong ScriptGetAddon();

    private IUnmanagedFunction<SetPendingHostStateRequestDelegate>? _SetPendingHostStateRequestDelegate;
    private IUnmanagedFunction<ReplyConnection>? _ReplyConnection;
    private IUnmanagedFunction<ScriptGetAddon>? _ScriptGetAddon;

    public AddonsHooks(ISwiftlyCore core, AddonsWorkshopManager workshopManager, AddonsClients clients, AddonsUtilities utils, IOptionsMonitor<AddonsConfig> config)
    {
        Core = core;
        WorkshopManager = workshopManager;
        Clients = clients;
        Utilities = utils;
        Core.Registrator.Register(this);

        _SetPendingHostStateRequestDelegate = core.Memory.GetUnmanagedFunctionByAddress<SetPendingHostStateRequestDelegate>(core.GameData.GetSignature("HostStateRequest"));
        _ReplyConnection = core.Memory.GetUnmanagedFunctionByAddress<ReplyConnection>(core.GameData.GetSignature("ReplyConnection"));
        _ScriptGetAddon = core.Memory.GetUnmanagedFunctionByAddress<ScriptGetAddon>(core.GameData.GetSignature("ScriptGetAddon"));

        _SetPendingHostStateRequestDelegate.AddHook((next) =>
        {
            unsafe
            {
                return (hostStateManager, pRequest) =>
                {
                    var kvName = pRequest->m_pKV == null ? "(null)" : pRequest->m_pKV->GetName();
                    Core.Logger.LogDebug("[HostStateRequest] kv={KV} levelName={LevelName} m_Addons={Addons} currentWorkshopMap={WorkshopMap}",
                        kvName, pRequest->m_LevelName.Value, pRequest->m_Addons.Value, Utilities.GetCurrentWorkshopMap());

                    if (pRequest->m_pKV == null)
                    {
                        var bValveMap = Core.GameFileSystem.FileExists($"maps/{pRequest->m_LevelName.Value}.vpk", "MOD");
                        Core.Logger.LogDebug("[HostStateRequest] null KV — bValveMap={ValveMap}", bValveMap);

                        if (!string.IsNullOrEmpty(pRequest->m_Addons.Value))
                            Utilities.SetCurrentWorkshopMap(pRequest->m_Addons.Value);
                        else if (bValveMap)
                            Utilities.ClearCurrentWorkshopMap();
                    }
                    else if (!pRequest->m_pKV->GetName().Equals("changelevel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (pRequest->m_pKV->GetName().Equals("map_workshop", StringComparison.OrdinalIgnoreCase))
                        {
                            var workshopMapId = pRequest->m_pKV->GetString("customgamemode", "");
                            // Only fall back to m_Addons.Value if it looks like a single workshop ID (no commas).
                            // Prevents corruption if the same CHostStateRequest struct is reused and m_Addons
                            // already contains a comma-separated list from a previous hook invocation.
                            if (string.IsNullOrEmpty(workshopMapId) && !pRequest->m_Addons.Value.Contains(','))
                                workshopMapId = pRequest->m_Addons.Value;

                            if (!string.IsNullOrEmpty(workshopMapId))
                                Utilities.SetCurrentWorkshopMap(workshopMapId);
                            else
                                Utilities.ClearCurrentWorkshopMap();
                        }
                        else
                            Utilities.ClearCurrentWorkshopMap();
                    }
                    else
                    {
                        Utilities.ClearCurrentWorkshopMap();
                    }

                    if (!string.IsNullOrEmpty(pRequest->m_Addons.Value) && Core.GameFileSystem.IsDirectory(pRequest->m_Addons.Value, "OFFICIAL_ADDONS"))
                        Utilities.SetCurrentWorkshopMap(pRequest->m_Addons.Value);

                    Core.Logger.LogDebug("[HostStateRequest] after resolution — currentWorkshopMap={WorkshopMap} mountedAddons={MountedAddons}",
                        Utilities.GetCurrentWorkshopMap(), string.Join(",", Utilities.GetMountedAddons()));

                    if (Utilities.GetMountedAddons().Count == 0)
                    {
                        if (!string.IsNullOrEmpty(Utilities.GetCurrentWorkshopMap()))
                            pRequest->m_Addons = Utilities.GetCurrentWorkshopMap();
                        next()(hostStateManager, pRequest);
                        return;
                    }

                    if (string.IsNullOrEmpty(Utilities.GetCurrentWorkshopMap()))
                    {
                        pRequest->m_Addons = Utilities.VectorToString(Utilities.GetMountedAddons());
                    }
                    else
                    {
                        var newAddons = new List<string>(Utilities.GetMountedAddons());
                        newAddons.RemoveAll(a => a.Equals(Utilities.GetCurrentWorkshopMap(), StringComparison.OrdinalIgnoreCase));
                        newAddons.Insert(0, Utilities.GetCurrentWorkshopMap());
                        pRequest->m_Addons = Utilities.VectorToString(newAddons);
                    }

                    Core.Logger.LogDebug("[HostStateRequest] final m_Addons={Addons}", pRequest->m_Addons.Value);
                    next()(hostStateManager, pRequest);
                };
            }
        });

        _ReplyConnection.AddHook((next) =>
        {
            unsafe
            {
                return (server, client) =>
                {
                    var clientPtr = (nint)client;
                    var steamId64 = core.Memory.ToServerSideClient(clientPtr).SteamID.GetSteamID64();
                    var clientInfo = Clients.GetClientInfo((long)steamId64);

                    Core.Logger.LogDebug("[ReplyConnection] steamId={SteamID} downloadedAddons=[{DownloadedAddons}] currentPendingAddon={CurrentPending}",
                        steamId64, string.Join(",", clientInfo.DownloadedAddons), clientInfo.CurrentPendingAddon);

                    if (
                        config.CurrentValue.CacheClientsWithAddons && config.CurrentValue.CacheClientsDurationInSeconds > 0 &&
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - clientInfo.LastActiveTime > config.CurrentValue.CacheClientsDurationInSeconds * 1000
                    )
                    {
                        Core.Logger.LogDebug("[ReplyConnection] Client {SteamID} has not connected for a while, clearing the cache", steamId64);
                        clientInfo.CurrentPendingAddon = string.Empty;
                        clientInfo.DownloadedAddons.Clear();
                    }
                    clientInfo.LastActiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    ref var addons = ref server.AsRef<CUtlString>(core.GameData.GetOffset("NetworkGameServer::Addons"));
                    var originalAddons = addons.Value;

                    var clientAddons = Clients.GetClientAddons();
                    Core.Logger.LogDebug("[ReplyConnection] serverAddons={ServerAddons} clientAddons=[{ClientAddons}]",
                        originalAddons, string.Join(",", clientAddons));

                    if (clientAddons.Count == 0)
                    {
                        Core.Logger.LogDebug("[ReplyConnection] no addons to send, passing through");
                        next()(server, client);
                        return;
                    }

                    if (!clientInfo.DownloadedAddons.Contains(clientAddons.First()))
                    {
                        Core.Logger.LogDebug("[ReplyConnection] first addon {Addon} not downloaded, setting as currentPendingAddon", clientAddons.First());
                        clientInfo.CurrentPendingAddon = clientAddons.First();
                    }
                    else
                    {
                        Core.Logger.LogDebug("[ReplyConnection] first addon {Addon} already downloaded, currentPendingAddon stays as {CurrentPending}", clientAddons.First(), clientInfo.CurrentPendingAddon);
                    }

                    // Keep only already-downloaded addons plus the single current pending one.
                    // Sending undownloaded addons to the client causes CS2 to perform a signature
                    // check that fails instantly, crashing or disconnecting the client before it
                    // can be told to download anything (mirrors MultiAddonManager behaviour).
                    clientAddons.RemoveAll(a =>
                        !clientInfo.DownloadedAddons.Contains(a) &&
                        !a.Equals(clientInfo.CurrentPendingAddon, StringComparison.OrdinalIgnoreCase));

                    addons.Value = Utilities.VectorToString(clientAddons);

                    Core.Logger.LogInformation("[ReplyConnection] sending addons={Addons} to client {SteamID}", addons.Value, steamId64);
                    next()(server, client);

                    addons.Value = originalAddons;
                };
            }
        });

        _ScriptGetAddon.AddHook((next) =>
        {
            return () =>
            {
                if (config.CurrentValue.Addons.Count == 0) return next()();

                var iAddon = ulong.TryParse(Utilities.GetCurrentWorkshopMap(), out var workshopId) ? workshopId : 0;
                if (iAddon == 0) return next()();

                return iAddon;
            };
        });
    }

    [EventListener<EventDelegates.OnStartupServer>]
    public void StartupServer()
    {
        Core.Logger.LogInformation("[StartupServer] fired — mapName={MapName} currentWorkshopMap={WorkshopMap} mountedAddons=[{MountedAddons}]",
            Core.Engine.GlobalVars.MapName.Value, Utilities.GetCurrentWorkshopMap(), string.Join(",", Utilities.GetMountedAddons()));

        foreach (var kvp in Clients.GetClients())
            Core.Logger.LogInformation("[StartupServer] client {SteamID} — downloadedAddons=[{Downloaded}] currentPendingAddon={Pending}",
                kvp.Key, string.Join(",", kvp.Value.DownloadedAddons), kvp.Value.CurrentPendingAddon);

        Core.GameFileSystem.RemoveSearchPath("", "GAME");
        Core.GameFileSystem.RemoveSearchPath("", "DEFAULT_WRITE_PATH");

        // If CurrentWorkshopMap was cleared while the server was downloading the workshop map
        // (e.g. a null-KV SetPendingHostStateRequest fired with empty m_Addons + bValveMap==true
        // for the previous valve map), recover it from the level name before RefreshAddons runs
        // so that GetClientAddons() includes it and clients are prompted to download it.
        if (string.IsNullOrEmpty(Utilities.GetCurrentWorkshopMap()))
        {
            var mapName = Core.Engine.GlobalVars.MapName.Value;
            Core.Logger.LogInformation("[StartupServer] currentWorkshopMap empty, attempting recovery from mapName={MapName}", mapName);
            if (!string.IsNullOrEmpty(mapName))
            {
                var parts = mapName.Split('/');
                if (parts.Length >= 2 &&
                    parts[0].Equals("workshop", StringComparison.OrdinalIgnoreCase) &&
                    ulong.TryParse(parts[1], out _))
                {
                    Core.Logger.LogInformation("[StartupServer] recovered currentWorkshopMap={MapId}", parts[1]);
                    Utilities.SetCurrentWorkshopMap(parts[1]);
                }
                else
                {
                    Core.Logger.LogInformation("[StartupServer] mapName does not match workshop/id format, cannot recover");
                }
            }
        }

        Core.Logger.LogInformation("[StartupServer] after recovery — currentWorkshopMap={WorkshopMap} calling RefreshAddons", Utilities.GetCurrentWorkshopMap());
        WorkshopManager.RefreshAddons();
        Core.Logger.LogInformation("[StartupServer] after RefreshAddons — clientAddons=[{ClientAddons}]", string.Join(",", Clients.GetClientAddons()));
    }

    [ServerNetMessageInternalHandler]
    public HookResult SendNetMessage(CNETMsg_SignonState signonState, int playerid)
    {
        var player = Core.PlayerManager.GetPlayer(playerid);
        long steamId;
        if (player == null)
        {
            var sid = Clients.GetSteamIdBySlot(playerid);
            if (sid == null)
            {
                Core.Logger.LogInformation("[SendNetMessage] playerid={PlayerId} not found in PlayerManager or slot mapping, skipping", playerid);
                return HookResult.Continue;
            }
            Core.Logger.LogInformation("[SendNetMessage] playerid={PlayerId} resolved via slot mapping to steamId={SteamID}", playerid, sid);
            steamId = sid.Value;
        }
        else
        {
            steamId = (long)player.UnauthorizedSteamID;
        }

        var clientInfo = Clients.GetClientInfo(steamId);
        clientInfo.LastActiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clientAddons = Clients.GetClientAddons();

        Core.Logger.LogDebug("[SendNetMessage] steamId={SteamID} signonState={SignonState} addons={Addons} downloadedAddons=[{Downloaded}] currentPendingAddon={Pending}",
            steamId, signonState.SignonState, signonState.Addons,
            string.Join(",", clientInfo.DownloadedAddons), clientInfo.CurrentPendingAddon);

        if (signonState.SignonState == SignonState_t.SIGNONSTATE_CHANGELEVEL)
        {
            var addonsList = Utilities.StringToVector(signonState.Addons);
            Core.Logger.LogDebug("[SendNetMessage] CHANGELEVEL — addonsList=[{AddonsList}] count={Count}",
                string.Join(",", addonsList), addonsList.Count);

            if (addonsList.Count > 1)
            {
                signonState.Addons = addonsList.First();
                clientInfo.CurrentPendingAddon = addonsList.First();
                Core.Logger.LogDebug("[SendNetMessage] trimmed to first addon={Addon}", addonsList.First());
            }
            else if (addonsList.Count == 1)
            {
                clientInfo.CurrentPendingAddon = signonState.Addons;
                Core.Logger.LogDebug("[SendNetMessage] single addon, set currentPendingAddon={Addon}", signonState.Addons);
            }

            return HookResult.Continue;
        }

        foreach (var downloadedAddon in clientInfo.DownloadedAddons)
        {
            clientAddons.Remove(downloadedAddon);
        }

        Core.Logger.LogDebug("[SendNetMessage] after removing downloaded — remaining clientAddons=[{ClientAddons}]",
            string.Join(",", clientAddons));

        if (clientAddons.Count == 0)
        {
            Core.Logger.LogDebug("[SendNetMessage] all addons downloaded, passing through");
            return HookResult.Continue;
        }

        Core.Logger.LogInformation("[SendNetMessage] overriding to CHANGELEVEL with addon={Addon} for steamId={SteamID}", clientAddons.First(), steamId);

        clientInfo.CurrentPendingAddon = clientAddons.First();
        signonState.Addons = clientAddons.First();
        signonState.SignonState = SignonState_t.SIGNONSTATE_CHANGELEVEL;

        return HookResult.Continue;
    }
}
