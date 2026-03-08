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
        if (addons.Count == 0) return;

        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player == null) return;

        var clientInfo = GetClientInfo((long)player.UnauthorizedSteamID);

        if (!string.IsNullOrEmpty(clientInfo.CurrentPendingAddon))
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - clientInfo.LastActiveTime > Config.CurrentValue.ExtraAddonsTimeoutInSeconds * 1000)
            {
                Core.Logger.LogDebug("Client {SteamID} has reconnected after the timeout or did not receive the addon message, will not add addon {addonId} to the downloaded list.", player.UnauthorizedSteamID, clientInfo.CurrentPendingAddon);
            }
            else
            {
                Core.Logger.LogDebug("Client {SteamID} has connected within the interval with the pending addon {addonId}, will send next addon in SendNetMessage hook.", player.UnauthorizedSteamID, clientInfo.CurrentPendingAddon);
                clientInfo.DownloadedAddons.Add(clientInfo.CurrentPendingAddon);
            }

            clientInfo.CurrentPendingAddon = string.Empty;
        }

        clientInfo.LastActiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    [EventListener<EventDelegates.OnClientDisconnected>]
    public void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player == null) return;

        var clientInfo = GetClientInfo((long)player.UnauthorizedSteamID);
        clientInfo.LastActiveTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    [EventListener<EventDelegates.OnClientPutInServer>]
    public void OnClientPutInServer(IOnClientPutInServerEvent @event)
    {
        var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
        if (player == null) return;

        var clientInfo = GetClientInfo((long)player.UnauthorizedSteamID);
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