﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using VChat.Data.Messages;
using VChat.Extensions;
using VChat.Helpers;
using VChat.Messages;

namespace VChat.Services
{
    public static class ServerChannelManager
    {
        private static List<ServerChannelInfo> ServerChannelInfo { get; set; }
        private static List<ServerChannelInviteInfo> ChannelInviteInfo { get; set; }
        private static readonly object _lockChannelInfo = new();
        private static readonly object _lockChannelInviteInfo = new();
        public const ulong ServerOwnerId = 0ul;

        // TODO: Setting
        public const bool CanUsersCreateChannels = true;

        static ServerChannelManager()
        {
            ServerChannelInfo = new List<ServerChannelInfo>();
            ChannelInviteInfo = new List<ServerChannelInviteInfo>();

            // Add VChat channel, this is used to send informational messages.
            ServerChannelInfo.Add(new ServerChannelInfo()
            {
                Name = VChatPlugin.Name,
                ReadOnly = true,
                Color = new Color(0.035f, 0.714f, 0.902f),
                OwnerId = ServerOwnerId,
                IsPublic = true,
            });
        }

        public static bool DoesChannelExist(string name)
        {
            lock (_lockChannelInfo)
            {
                foreach (var channel in ServerChannelInfo)
                {
                    if (name.Equals(channel.Name, StringComparison.CurrentCultureIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Creates a copy of the current channel list
        /// </summary>
        public static List<ServerChannelInfo> GetServerChannelInfoCopy()
        {
            lock (_lockChannelInfo)
            {
                return ServerChannelInfo.ToList();
            }
        }

        /// <summary>
        /// Returns the list of available channels for the user with steamId
        /// </summary>
        public static IEnumerable<ServerChannelInfo> GetChannelsForUser(ulong steamId)
        {
            lock (_lockChannelInfo)
            {
                return ServerChannelInfo.Where(x => x.IsPublic || x.OwnerId == steamId || x.Invitees.Contains(steamId));
            }
        }

        /// <summary>
        /// Returns true if the user has special permissions for that channel.
        /// This means that the user can kick playerss and disband the channel.
        /// </summary>
        public static bool CanModerateChannel(ulong steamId, string channelName)
        {
            lock (_lockChannelInfo)
            {
                var channel = FindChannel(channelName);
                if(channel == null)
                {
                    return false;
                }
                else
                {
                    if (ValheimHelper.IsAdministrator(steamId))
                    {
                        return true;
                    }
                    else
                    {
                        return channel.OwnerId == steamId;
                    }
                }
            }
        }

        /// <summary>
        /// Returns a list of known invites for the provided user.
        /// </summary>
        public static IEnumerable<ServerChannelInviteInfo> GetChannelInvitesForUser(ulong steamId)
        {
            lock (_lockChannelInviteInfo)
            {
                return ChannelInviteInfo.Where(x => x.InviteeId == steamId);
            }
        }

        /// <summary>
        /// Adds a channel to the server.
        /// </summary>
        public static bool AddChannel(string name, ulong ownerSteamId, bool isPublic)
        {
            if (DoesChannelExist(name))
            {
                return false;
            }

            var channelInfo = new ServerChannelInfo()
            {
                Name = name,
                OwnerId = ownerSteamId,
                IsPublic = isPublic,
                ServerCommandName = name.ToLower().Replace(" ", ""),
            };

            lock (_lockChannelInfo)
            {
                ServerChannelInfo.Add(channelInfo);
            }

            // Re-init the server commands so that this one is added to it, might be a better way of doing it but so far valheim doesn't do much multithreading anyway.
            VChatPlugin.InitialiseServerCommands();

            // Send update message to relevant players
            foreach (var peer in ZNet.instance.GetConnectedPeers())
            {
                if (peer.m_socket is ZSteamSocket steamSocket
                    && steamSocket.GetPeerID().m_SteamID == ownerSteamId)
                {
                    SendChannelInformationToClient(peer.m_uid);
                    SendMessageToClient_ChannelConnected(peer.m_uid, channelInfo);
                }
            }
            return false;
        }

        internal static ServerChannelInfo FindChannel(string channelName)
        {
            lock (_lockChannelInfo)
            {
                return ServerChannelInfo.FirstOrDefault(x => string.Equals(channelName, x.Name, StringComparison.CurrentCultureIgnoreCase));
            }
        }

        /// <summary>
        /// Add an invite to the collection and sends the request to the peer
        /// </summary>
        public static void AddInvite(long peerId, ServerChannelInviteInfo inviteInfo)
        {
            lock (_lockChannelInviteInfo)
            {
                ChannelInviteInfo.Add(inviteInfo);
            }
        }

        public static void AddInvite(ZNetPeer peer, ServerChannelInviteInfo inviteInfo)
            => AddInvite(peer?.m_uid ?? long.MaxValue, inviteInfo);

        public static bool RemoveInvite(ulong steamId, string channelName)
        {
            lock (_lockChannelInviteInfo)
            {
                var inviteInfo = ServerChannelManager.ChannelInviteInfo.FirstOrDefault(x =>
                       x.InviteeId == steamId
                    && string.Equals(x.ChannelName, channelName, StringComparison.CurrentCultureIgnoreCase)
                );

                if (inviteInfo != null)
                {
                    ChannelInviteInfo.Remove(inviteInfo);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Add a player to the invitee list of an existing channel.
        /// </summary>
        public static bool AddPlayerToChannelInviteeList(string channelName, ulong inviteeId)
        {
            lock (_lockChannelInfo)
            {
                var channelInfo = ServerChannelInfo.FirstOrDefault(x => string.Equals(channelName, x.Name, StringComparison.CurrentCultureIgnoreCase));
                if (channelInfo != null && !channelInfo.ReadOnly)
                {
                    if (!channelInfo.Invitees.Contains(inviteeId))
                    {
                        channelInfo.Invitees.Add(inviteeId);
                    }
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Remove a player from an existing channel, if the user is the owner, the ownership will be passed on, or the channel will disband if it's empty.
        /// </summary>
        public static bool RemovePlayerFromChannel(long peerId, ulong steamId, string channelName)
        {
            lock (_lockChannelInfo)
            {
                var channelInfo = ServerChannelInfo.FirstOrDefault(x => string.Equals(channelName, x.Name, StringComparison.CurrentCultureIgnoreCase));
                if (channelInfo != null)
                {
                    if(channelInfo.OwnerId == steamId)
                    {
                        if (channelInfo.Invitees.Count == 0)
                        {
                            // Disband
                            DisbandChannel(peerId, steamId, channelName);
                            return true;
                        }
                        else
                        {
                            // Retrieve the list of active steam ids
                            var steamIds = ZNet.instance.GetConnectedPeers().Select(peer =>
                            {
                                if (ValheimHelper.GetSteamIdFromPeer(peer, out ulong peerSteamId))
                                {
                                    return peerSteamId;
                                }
                                return ulong.MaxValue;
                            }).Where(x => x != ulong.MaxValue).Distinct();

                            // Get the first active player in that collection
                            ulong newOwnerId = 0UL;
                            foreach(var inviteeId in channelInfo.Invitees)
                            {
                                if(steamIds.Contains(inviteeId))
                                {
                                    newOwnerId = inviteeId;
                                    break;
                                }
                            }

                            // If we cannot find an online player, simply pass it on to the first invitee.
                            if(newOwnerId == 0UL)
                            {
                                newOwnerId = channelInfo.Invitees.FirstOrDefault();
                                VChatPlugin.Log($"Passing the channel '{channelInfo.Name}' to the first invitee because nobody else is online.");
                            }

                            // Retrieve the owner peer
                            var ownerPeer = ValheimHelper.GetPeerFromSteamId(newOwnerId);
                            var ownerPlayerName = ownerPeer?.m_playerName ?? $"{newOwnerId}";

                            // Apply the owner
                            channelInfo.OwnerId = newOwnerId;
                            channelInfo.Invitees.Remove(newOwnerId);
                            VChatPlugin.Log($"Owner has been removed from the channel, passing the channel '{channelInfo.Name}' to {ownerPeer?.m_playerName}, id {newOwnerId}.");

                            // Send a message to every online player in that channel
                            var channelInviteeSteamIds = channelInfo.Invitees.Concat(new[] { channelInfo.OwnerId });
                            foreach (var inviteeSteamId in steamIds.Where(x => channelInviteeSteamIds.Contains(x)))
                            {
                                if(ValheimHelper.GetPeerIdFromSteamId(inviteeSteamId, out long inviteePeerId))
                                {
                                    SendMessageToPeerInChannel(inviteePeerId, channelName, $"<i>Channel '{channelName}' has been passed on to '{ownerPlayerName}'</i>", Color.gray);
                                }
                            }
                            SendChannelInformationToClient(peerId);
                            return true;
                        }
                    }
                    else if(channelInfo.Invitees.Contains(steamId))
                    {
                        VChatPlugin.Log($"Player '{ValheimHelper.GetPeer(peerId)?.m_playerName}' has been removed from the channel '{channelName}'.");
                        channelInfo.Invitees.Remove(steamId);
                        SendChannelInformationToClient(peerId);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Returns true if the channel can be found and if the user has permission to invite.
        /// </summary>
        public static bool CanInvite(ulong steamId, string channelName)
        {
            lock (_lockChannelInfo)
            {
                var channel = ServerChannelInfo.FirstOrDefault(x => string.Equals(x.Name, channelName, StringComparison.CurrentCultureIgnoreCase));
                return CanInvite(steamId, channel);
            }
        }

        /// <summary>
        /// Returns true if the channel can be found and if the user has permission to invite.
        /// </summary>
        public static bool CanInvite(ulong steamId, ServerChannelInfo channelInfo)
        {
            return channelInfo != null
                && (channelInfo.OwnerId == steamId
                && !channelInfo.ReadOnly
                || channelInfo.Invitees.Contains(steamId)
                || ValheimHelper.IsAdministrator(steamId));
        }

        /// <summary>
        /// Sends the accessible channel information to the peer.
        /// </summary>
        public static bool SendChannelInformationToClient(long peerId)
        {
            if (ZNet.m_isServer)
            {
                var peer = ZNet.instance.GetPeer(peerId);
                var steamId = (peer.m_socket as ZSteamSocket)?.GetPeerID().m_SteamID;
                if (steamId != null)
                {
                    var channels = GetChannelsForUser(steamId.Value);
                    ChannelInfoMessage.SendToPeer(peerId, channels);
                    return true;
                }

                VChatPlugin.LogError($"Steam ID is undefined for {peerId}");
            }

            return false;
        }

        /// <summary>
        /// Sends a request to the server to create a channel.
        /// </summary>
        public static bool DisbandChannel(long peerId, ulong steamId, string channelName)
        {
            var peer = ValheimHelper.GetPeer(peerId);
            if(!DoesChannelExist(channelName))
            {
                ChannelDisbandMessage.SendResponseToPeer(peerId, ChannelDisbandMessage.ChannelDisbandResponseType.ChannelNotFound, channelName);
            }
            else if (!CanModerateChannel(steamId, channelName))
            {
                ChannelDisbandMessage.SendResponseToPeer(peerId, ChannelDisbandMessage.ChannelDisbandResponseType.NoPermission, channelName);
            }
            else
            {
                VChatPlugin.LogWarning($"Disbanding channel named {channelName}.");

                ServerChannelInfo channelInfo;
                lock(_lockChannelInfo)
                {
                    channelInfo = ServerChannelInfo.FirstOrDefault(x => string.Equals(x.Name, channelName, StringComparison.CurrentCultureIgnoreCase));
                    if(channelInfo != null)
                    {
                        ServerChannelInfo.Remove(channelInfo);
                    }
                    else
                    {
                        ChannelDisbandMessage.SendResponseToPeer(peerId, ChannelDisbandMessage.ChannelDisbandResponseType.ChannelNotFound, channelName);
                        return false;
                    }
                }

                // Re-init the server commands so that this one is added to it, might be a better way of doing it but so far valheim doesn't do much multithreading anyway.
                VChatPlugin.InitialiseServerCommands();

                foreach (var targetPeer in ZNet.instance.GetConnectedPeers())
                {
                    if(ValheimHelper.GetSteamIdFromPeer(targetPeer, out ulong targetSteamId))
                    {
                        if (channelInfo.OwnerId == targetSteamId || channelInfo.Invitees.Contains(targetSteamId))
                        {
                            // Notify all connected peers with the player that disbanded it.
                            var text = $"Channel '{channelInfo.Name}' has been disbanded by {peer.m_playerName}.";

                            MessageHelper.SendMessageToPeer(targetPeer.m_uid, VChatPlugin.Name, null, text, () =>
                            {
                                SendMessageToPeerInChannel(targetPeer.m_uid, VChatPlugin.Name, text);
                            }, new System.Version(2,0,0));

                            // Update channel information for VChat clients.
                            SendChannelInformationToClient(targetPeer.m_uid);
                        }
                    }
                }

                return true;
            }

            return false;
        }


        /// <summary>
        /// Send a message to all connected peers within the provided channel.
        /// </summary>
        public static bool SendMessageToAllUsersInChannel(long senderPeerId, string channelName, string callerName, string text, Color? customColor = null)
        {
            var senderPeer = ValheimHelper.GetPeer(senderPeerId);
            if (senderPeer != null && ValheimHelper.GetSteamIdFromPeer(senderPeer, out ulong senderSteamId))
            {
                foreach (var channel in GetChannelsForUser(senderSteamId))
                {
                    if (string.Equals(channel.Name, channelName, StringComparison.CurrentCultureIgnoreCase))
                    {
                        var steamIds = channel.Invitees.Concat(new[] { channel.OwnerId });
                        foreach (var channelUserSteamId in steamIds)
                        {
                            var targetPeer = ValheimHelper.GetPeerFromSteamId(channelUserSteamId);
                            if (targetPeer != null)
                            {
                                MessageHelper.SendMessageToPeer(targetPeer.m_uid, channel.Name, senderPeer?.m_playerName ?? callerName, text, () =>
                                {
                                    ChannelChatMessage.SendToPeer(targetPeer.m_uid, channel.Name, senderPeer?.m_refPos ?? new Vector3(), senderPeer?.m_playerName ?? callerName, text, customColor);
                                }, new System.Version(2,0,0));
                            }
                        }
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Send a message to a peer in the provided channel, note that this will not send it to any other user within that channel.
        /// </summary>
        public static bool SendMessageToPeerInChannel(long targetPeerId, string channelName, string text, Color? color = null)
        {
            var peer = ValheimHelper.GetPeer(targetPeerId);
            if (peer != null && ValheimHelper.GetSteamIdFromPeer(peer, out ulong steamId))
            {
                var knownChannels = GetChannelsForUser(steamId);
                foreach (var channel in knownChannels)
                {
                    if (string.Equals(channel.Name, channelName, StringComparison.CurrentCultureIgnoreCase))
                    {
                        ChannelChatMessage.SendToPeer(peer.m_uid, channel.Name, peer.m_refPos, null, text, color);
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Send a message to the provided channel to all active players that have access to it, without using a player-name.
        /// </summary>
        public static bool SendMessageToAllPeersInChannel(string channelName, string text, Color? color = null)
        {
            var channelInfo = FindChannel(channelName);
            if (channelInfo == null)
            {
                // List of players ids to send that message to.
                var steamIds = channelInfo.Invitees.Concat(new[] { channelInfo.OwnerId });

                // Send a message to every online player in that channel
                var channelInviteeSteamIds = channelInfo.Invitees.Concat(new[] { channelInfo.OwnerId });
                foreach (var inviteeSteamId in steamIds.Where(x => channelInviteeSteamIds.Contains(x)))
                {
                    if (ValheimHelper.GetPeerIdFromSteamId(inviteeSteamId, out long inviteePeerId))
                    {
                        SendMessageToPeerInChannel(inviteePeerId, channelName, text, color);
                    }
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Sends an success message in the VChat channel to a user, colors everything green.
        /// </summary>
        /// <returns></returns>
        public static bool SendVChatSuccessMessageToPeer(long targetPeerId, string text)
        {
            return SendMessageToPeerInChannel(targetPeerId, VChatPlugin.Name, text, new Color(0.137f, 1f, 0f));
        }

        /// <summary>
        /// Sends an error message in the VChat channel to a user, colors everything red.
        /// </summary>
        /// <returns></returns>
        public static bool SendVChatErrorMessageToPeer(long targetPeerId, string text)
        {
            return SendMessageToPeerInChannel(targetPeerId, VChatPlugin.Name, text, Color.red);
        }

        public static bool DeclineChannelInvite(long peerId, string channelName)
        {
            if (ZNet.m_isServer)
            {
                if (ValheimHelper.GetSteamIdFromPeer(peerId, out ulong steamId, out ZNetPeer peer))
                {
                    // Find the invite and remove it if present
                    var foundInvite = false;
                    lock (_lockChannelInviteInfo)
                    {
                        var inviteInfo = ChannelInviteInfo.FirstOrDefault(x =>
                               x.InviteeId == steamId
                            && string.Equals(x.ChannelName, channelName, StringComparison.CurrentCultureIgnoreCase)
                        );
                        if (inviteInfo != null)
                        {
                            ChannelInviteInfo.Remove(inviteInfo);
                            foundInvite = true;
                        }
                    }

                    // Handle the invite
                    if (foundInvite)
                    {
                        VChatPlugin.LogWarning($"User '{peer.m_playerName}' declined channel invite '{channelName}'.");
                        return true;
                    }
                    else
                    {
                        ChannelInviteMessage.SendToPeer(peerId, ChannelInviteMessage.ChannelInviteType.Decline, ChannelInviteMessage.ChannelInviteResponseType.NoInviteFound, channelName);
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Sends a message to the client that it's connected to a channel.
        /// </summary>
        public static void SendMessageToClient_ChannelConnected(long peerId, ServerChannelInfo channelInfo)
        {
            if (ZNet.m_isServer && !channelInfo.ReadOnly)
            {
                var peer = ZNet.instance?.GetPeer(peerId);
                if (peer != null)
                {
                    var message = $"Successfully connected to the {channelInfo.Name} channel.";
                    SendMessageToPeerInChannel(peerId, VChatPlugin.Name, message);

                    // Only send if command name is set, otherwise it's considered a read-only channel.
                    VChatPlugin.LogWarning($"{peerId} connected to the channel {channelInfo.Name}, commandName: /{channelInfo.ServerCommandName}");
                    if (!string.IsNullOrEmpty(channelInfo.ServerCommandName))
                    {
                        VChatPlugin.LogWarning($"Sending command info");
                        message = $"Type {VChatPlugin.Settings.CommandPrefix}{channelInfo.ServerCommandName} [text] to send a message in the {channelInfo.Name} chat.";
                        SendMessageToPeerInChannel(peerId, VChatPlugin.Name, message);
                    }
                }
            }
        }
    }
}
