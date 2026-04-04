using System.Collections.Concurrent;
using AddonsManager.Config;
using AddonsManager.Structs;
using AddonsManager.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.GameEvents;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.ProtobufDefinitions;

namespace AddonsManager.Clients;

public class AddonsClients
{
    private ISwiftlyCore Core;
    private AddonsUtilities Utilities;
    private ConcurrentDictionary<long, ClientAddonInfo> Clients = [];
    private IOptionsMonitor<AddonsConfig> Config;
    // Persists slot→steamId across disconnects so SendNetMessage can find client state
    // even when the player is no longer in PlayerManager (e.g. map-change CHANGELEVEL phase).
    private ConcurrentDictionary<int, long> _slotToSteamId = [];

    public AddonsClients(ISwiftlyCore core, AddonsUtilities utils, IOptionsMonitor<AddonsConfig> config)
    {
        Core = core;
        Utilities = utils;
        Config = config;
        Core.Registrator.Register(this);
    }

    public ConcurrentDictionary<long, ClientAddonInfo> GetClients()
    {
        return Clients;
    }

    public long? GetSteamIdBySlot(int slot)
    {
        return _slotToSteamId.TryGetValue(slot, out var steamId) ? steamId : null;
    }

    public ClientAddonInfo GetClientInfo(long steamId)
    {
        return Clients.GetOrAdd(steamId, _ => new ClientAddonInfo
        {
            LastActiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DownloadedAddons = [],
            CurrentPendingAddon = string.Empty
        });
    }

    public List<string> GetClientAddons()
    {
        var clientAddons = new List<string>();

        if (Utilities.GetCurrentWorkshopMap().Length > 0)
        {
            clientAddons.Add(Utilities.GetCurrentWorkshopMap());
        }

        clientAddons.AddRange(Utilities.GetMountedAddons());

        return clientAddons;
    }

    [EventListener<EventDelegates.OnClientConnected>]
    public void OnClientConnected(IOnClientConnectedEvent @event)
    {
        var addons = GetClientAddons();
        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player == null) return;

        var clientInfo = GetClientInfo((long)player.UnauthorizedSteamID);
        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - clientInfo.LastActiveTime;

        Core.Logger.LogInformation("[OnClientConnected] steamId={SteamID} currentPendingAddon={Pending} downloadedAddons=[{Downloaded}] elapsedMs={Elapsed} timeoutMs={Timeout} serverAddons=[{ServerAddons}]",
            player.UnauthorizedSteamID, clientInfo.CurrentPendingAddon, string.Join(",", clientInfo.DownloadedAddons),
            elapsed, Config.CurrentValue.ExtraAddonsTimeoutInSeconds * 1000, string.Join(",", addons));

        if (addons.Count == 0)
        {
            Core.Logger.LogDebug("[OnClientConnected] no server addons configured, skipping");
            return;
        }

        if (!string.IsNullOrEmpty(clientInfo.CurrentPendingAddon))
        {
            if (elapsed > Config.CurrentValue.ExtraAddonsTimeoutInSeconds * 1000)
            {
                Core.Logger.LogInformation("[OnClientConnected] client {SteamID} reconnected after timeout ({Elapsed}ms > {Timeout}ms), not crediting addon {Addon}",
                    player.UnauthorizedSteamID, elapsed, Config.CurrentValue.ExtraAddonsTimeoutInSeconds * 1000, clientInfo.CurrentPendingAddon);
            }
            else
            {
                Core.Logger.LogInformation("[OnClientConnected] client {SteamID} reconnected within interval, crediting addon {Addon}",
                    player.UnauthorizedSteamID, clientInfo.CurrentPendingAddon);
                clientInfo.DownloadedAddons.Add(clientInfo.CurrentPendingAddon);
            }

            clientInfo.CurrentPendingAddon = string.Empty;
        }
        else
        {
            Core.Logger.LogInformation("[OnClientConnected] client {SteamID} has no pending addon", player.UnauthorizedSteamID);
        }

        clientInfo.LastActiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    [EventListener<EventDelegates.OnClientDisconnected>]
    public void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player == null) return;

        var clientInfo = GetClientInfo((long)player.UnauthorizedSteamID);
        Core.Logger.LogInformation("[OnClientDisconnected] steamId={SteamID} currentPendingAddon={Pending} downloadedAddons=[{Downloaded}]",
            player.UnauthorizedSteamID, clientInfo.CurrentPendingAddon, string.Join(",", clientInfo.DownloadedAddons));
        // Store slot→steamId so SendNetMessage can find the client after PM removal.
        _slotToSteamId[@event.PlayerId] = (long)player.UnauthorizedSteamID;
        clientInfo.LastActiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    [EventListener<EventDelegates.OnClientPutInServer>]
    public void OnClientPutInServer(IOnClientPutInServerEvent @event)
    {
        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player == null) return;

        var clientInfo = GetClientInfo((long)player.UnauthorizedSteamID);
        Core.Logger.LogDebug("[OnClientPutInServer] steamId={SteamID} cacheEnabled={Cache} downloadedAddons=[{Downloaded}]",
            player.UnauthorizedSteamID, Config.CurrentValue.CacheClientsWithAddons, string.Join(",", clientInfo.DownloadedAddons));

        if (Config.CurrentValue.CacheClientsWithAddons) return;

        clientInfo.DownloadedAddons.Clear();
    }

    [GameEventHandler(HookMode.Pre)]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event)
    {
        if (Config.CurrentValue.BlockDisconnectMessages == false) return HookResult.Continue;

        if (@event.Reason == (short)ENetworkDisconnectionReason.NETWORK_DISCONNECT_LOOPSHUTDOWN) return HookResult.Stop;
        return HookResult.Continue;
    }
}
