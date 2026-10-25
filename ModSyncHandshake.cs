using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ModSync
{
    internal static class ModSyncHandshake
    {
        private const string RequestRpc = "ModSync_Request_v1";
        private const string ChallengeRpc = "ModSync_Challenge_v1";
        private const string InventoryRpc = "ModSync_Inventory_v1";
        private const string AcceptedRpc = "ModSync_Accepted_v1";
        private const string RejectedRpc = "ModSync_Rejected_v1";
        private const string RejectionAckRpc = "ModSync_RejectionAck_v1";
        private const string RepairRpc = "ModSync_Repair_v1";
        private const string RepairFinishedRpc = "ModSync_RepairFinished_v1";
        private const float TimeoutSeconds = 30f;
        private const float RepairTimeoutSeconds = 600f;
        private const float RejectionAckTimeoutSeconds = 2f;
        private const float DisconnectTimeoutSeconds = 2f;
        private const float ClientDisconnectFallbackSeconds = 5f;
        private static readonly ChallengeRegistry<ZRpc> ServerConnections =
            new ChallengeRegistry<ZRpc>();
        private static readonly Dictionary<ZRpc, ClientConnection> ClientConnections =
            new Dictionary<ZRpc, ClientConnection>();
        private static readonly Dictionary<ZRpc, float> DisconnectDeadlines =
            new Dictionary<ZRpc, float>();
        private static readonly Dictionary<ZRpc, bool> ServerDisconnects =
            new Dictionary<ZRpc, bool>();
        private static readonly HashSet<ZRpc> PendingRequests = new HashSet<ZRpc>();
        private static readonly HashSet<ZRpc> RepairConnections = new HashSet<ZRpc>();
        private static readonly List<ZRpc> ExpiredConnections = new List<ZRpc>();
        private static readonly List<ZRpc> DueDisconnects = new List<ZRpc>();
        private static readonly List<ZRpc> ReadyRequests = new List<ZRpc>();
        private static readonly MethodInfo SendPeerInfoMethod =
            AccessTools.Method(typeof(ZNet), "SendPeerInfo", new[] { typeof(ZRpc), typeof(string) });
        private static ModPolicy _policy;
        private static Task<ModPolicy> _policyLoadTask;
        private static string _configRoot;
        private static string _selfAssemblyPath;
        private static bool _policyLoaded;
        private static string _pendingConnectionFailureMessage;
        private static string _pendingRepairConfirmationMessage;
        private static ThunderstorePackageInstaller.InstallPlan _deferredPlan;
        private static Task _deferredTask;
        private static CancellationTokenSource _deferredCancellation;
        private static float _deferredDeadline;
        private static ZRpc _deferredRpc;
        private static float _logoutAt;

        internal static void Configure(string configRoot, string selfAssemblyPath)
        {
            _configRoot = configRoot;
            _selfAssemblyPath = selfAssemblyPath;
            _policy = null;
            _policyLoadTask = null;
            _policyLoaded = false;
        }

        internal static void Register(ZNet network, ZNetPeer peer)
        {
            if (network == null || peer == null || peer.m_rpc == null)
            {
                return;
            }

            ZRpc rpc = peer.m_rpc;
            if (network.IsServer())
            {
                if (!ServerConnections.Register(rpc, Time.unscaledTime + TimeoutSeconds))
                {
                    ModSyncPlugin.Log.LogWarning(
                        "Ignored a duplicate server connection registration.");
                    return;
                }

                rpc.Register<int>(RequestRpc, ReceiveRequest);
                rpc.Register<string>(InventoryRpc, ReceiveInventory);
                rpc.Register(RejectionAckRpc, ReceiveRejectionAck);
                rpc.Register<string>(RepairFinishedRpc, ReceiveRepairFinished);
                return;
            }

            if (ClientConnections.ContainsKey(rpc))
            {
                ModSyncPlugin.Log.LogWarning(
                    "Ignored a duplicate client connection registration.");
                return;
            }

            ClientConnection connection = new ClientConnection(network);
            ClientConnections[rpc] = connection;
            _pendingConnectionFailureMessage = null;
            _pendingRepairConfirmationMessage = null;
            rpc.Register<int, string>(ChallengeRpc, ReceiveChallenge);
            rpc.Register<string>(AcceptedRpc, ReceiveAccepted);
            rpc.Register<string>(RejectedRpc, ReceiveRejected);
            rpc.Register<string>(RepairRpc, ReceiveRepair);
        }

        internal static bool AllowPeerInfo(ZNet network, ZRpc rpc, string password)
        {
            if (network == null || rpc == null)
            {
                return false;
            }

            if (network.IsServer())
            {
                return true;
            }

            if (!ClientConnections.TryGetValue(rpc, out ClientConnection connection) ||
                connection.Failed)
            {
                return false;
            }

            if (connection.Verified)
            {
                return true;
            }

            if (!connection.Requested)
            {
                connection.Requested = true;
                connection.Password = password;
                connection.Deadline = Time.unscaledTime + TimeoutSeconds;

                try
                {
                    PluginFile[] snapshot = PluginInventory.Snapshot();
                    string pluginRoot = Paths.PluginPath;
                    connection.InventoryTask = Task.Run(
                        () => PluginInventory.Hash(snapshot, pluginRoot));
                    rpc.Invoke(RequestRpc, InventoryPacketCodec.ProtocolVersion);
                }
                catch (Exception exception)
                {
                    FailClient(rpc, connection,
                        Loc.T("handshake.inventory_prepare_failed"), exception);
                }
            }

            return false;
        }

        internal static bool AdmitPeerInfo(ZRpc rpc)
        {
            if (ServerConnections.IsAccepted(rpc))
            {
                return true;
            }

            RejectServer(rpc, "handshake.client_incomplete");
            return false;
        }

        internal static void Tick()
        {
            float now = Time.unscaledTime;
            ExpiredConnections.Clear();
            ServerConnections.CollectExpired(now, ExpiredConnections);
            for (int index = 0; index < ExpiredConnections.Count; index++)
            {
                RejectServer(ExpiredConnections[index],
                    "handshake.validation_timeout");
            }

            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                EnsurePolicyLoaded();
                if (_policyLoaded)
                {
                    ReadyRequests.Clear();
                    foreach (ZRpc rpc in PendingRequests)
                    {
                        ReadyRequests.Add(rpc);
                    }

                    for (int index = 0; index < ReadyRequests.Count; index++)
                    {
                        ZRpc rpc = ReadyRequests[index];
                        PendingRequests.Remove(rpc);
                        ProcessRequest(rpc);
                    }
                }
            }

            foreach (KeyValuePair<ZRpc, ClientConnection> item in ClientConnections)
            {
                TickClient(item.Key, item.Value, now);
            }

            TickDeferred(now);

            DueDisconnects.Clear();
            foreach (KeyValuePair<ZRpc, float> item in DisconnectDeadlines)
            {
                if (now >= item.Value)
                {
                    DueDisconnects.Add(item.Key);
                }
            }

            for (int index = 0; index < DueDisconnects.Count; index++)
            {
                ZRpc rpc = DueDisconnects[index];
                if (ServerDisconnects.TryGetValue(rpc, out bool disconnectRequested))
                {
                    if (disconnectRequested)
                    {
                        ModSyncPlugin.Log.LogWarning(
                            "The rejected client did not close the connection; it will be closed from the server.");
                        Disconnect(rpc);
                    }
                    else
                    {
                        RequestServerDisconnect(rpc);
                    }
                }
                else
                {
                    Disconnect(rpc);
                }
            }
        }

        internal static void Remove(ZRpc rpc)
        {
            ServerConnections.Remove(rpc);
            if (ClientConnections.TryGetValue(rpc, out ClientConnection connection))
            {
                connection.RepairCancellation?.Cancel();
                connection.RepairCancellation?.Dispose();
                ClientConnections.Remove(rpc);
            }

            DisconnectDeadlines.Remove(rpc);
            ServerDisconnects.Remove(rpc);
            PendingRequests.Remove(rpc);
            RepairConnections.Remove(rpc);
        }

        internal static void Clear()
        {
            foreach (ClientConnection connection in ClientConnections.Values)
            {
                connection.RepairCancellation?.Cancel();
                connection.RepairCancellation?.Dispose();
            }

            ServerConnections.Clear();
            ClientConnections.Clear();
            DisconnectDeadlines.Clear();
            ServerDisconnects.Clear();
            PendingRequests.Clear();
            RepairConnections.Clear();
            ExpiredConnections.Clear();
            DueDisconnects.Clear();
            ReadyRequests.Clear();
            _policy = null;
            _policyLoadTask = null;
            _configRoot = null;
            _selfAssemblyPath = null;
            _policyLoaded = false;
            _pendingConnectionFailureMessage = null;
            _pendingRepairConfirmationMessage = null;
            _deferredPlan = null;
            _deferredTask = null;
            _deferredCancellation?.Cancel();
            _deferredCancellation = null;
            _deferredRpc = null;
            _logoutAt = 0f;
        }

        private static void ReceiveRequest(ZRpc rpc, int protocolVersion)
        {
            if (ServerConnections.IsAccepted(rpc))
            {
                return;
            }

            if (PendingRequests.Contains(rpc))
            {
                RejectServer(rpc, "handshake.duplicate_request");
                return;
            }

            if (protocolVersion != InventoryPacketCodec.ProtocolVersion)
            {
                RejectServer(rpc, "handshake.protocol_mismatch");
                return;
            }

            StartPolicyLoad();
            CompletePolicyLoad();
            if (!_policyLoaded)
            {
                PendingRequests.Add(rpc);
                return;
            }

            ProcessRequest(rpc);
        }

        private static void ProcessRequest(ZRpc rpc)
        {
            if (_policy == null)
            {
                RejectServer(rpc, "handshake.policy_unavailable");
                return;
            }

            string nonce;
            try
            {
                nonce = CreateNonce();
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogError($"Could not generate the random challenge: {exception}");
                RejectServer(rpc, "handshake.server_start_failed");
                return;
            }

            if (!ServerConnections.TryIssue(
                    rpc, nonce, Time.unscaledTime + TimeoutSeconds))
            {
                RejectServer(rpc, "handshake.duplicate_challenge");
                return;
            }

            try
            {
                rpc.Invoke(ChallengeRpc, InventoryPacketCodec.ProtocolVersion, nonce);
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogWarning(
                    $"Could not send the challenge to the client: {exception}");
                RejectServer(rpc, "handshake.start_failed");
            }
        }

        private static void EnsurePolicyLoaded()
        {
            StartPolicyLoad();
            CompletePolicyLoad();
        }

        private static void StartPolicyLoad()
        {
            if (_policyLoaded || _policyLoadTask != null)
            {
                return;
            }

            try
            {
                string configRoot = _configRoot;
                string selfAssemblyPath = _selfAssemblyPath;
                _policyLoadTask = Task.Run(
                    () => ModPolicyLoader.Load(configRoot, selfAssemblyPath));
            }
            catch (Exception exception)
            {
                _policyLoaded = true;
                ModSyncPlugin.Log.LogError(
                    $"Could not start loading the mod policy: {exception}");
            }
        }

        private static void CompletePolicyLoad()
        {
            if (_policyLoaded || _policyLoadTask == null || !_policyLoadTask.IsCompleted)
            {
                return;
            }

            _policyLoaded = true;
            try
            {
                _policy = _policyLoadTask.GetAwaiter().GetResult();
                ModSyncPlugin.Log.LogInfo(
                    $"Policy loaded: {_policy.Required.Count} required mods, " +
                    $"{_policy.Optional.Count} optional.");
                if (_policy.Required.Count == 0 && _policy.Optional.Count == 0)
                {
                    ModSyncPlugin.Log.LogWarning(
                        "The policy is empty: every mod installed on clients will be rejected.");
                }
            }
            catch (Exception exception)
            {
                _policy = null;
                ModSyncPlugin.Log.LogError(
                    $"Could not load the mod policy; every connection will be rejected: {exception}");
            }
            finally
            {
                _policyLoadTask = null;
            }
        }

        private static void ReceiveInventory(ZRpc rpc, string encodedInventory)
        {
            if (ServerConnections.IsAccepted(rpc))
            {
                return;
            }

            InventoryPacket packet;
            try
            {
                packet = InventoryPacketCodec.DecodeFromBase64(encodedInventory);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException ||
                exception is System.IO.InvalidDataException ||
                exception is ArgumentException)
            {
                ModSyncPlugin.Log.LogWarning(
                    $"A malformed inventory was discarded: {exception.Message}");
                ServerConnections.Reject(rpc);
                RejectServer(rpc, "handshake.inventory_malformed");
                return;
            }

            if (!ServerConnections.TryConsume(rpc, packet.Nonce, Time.unscaledTime))
            {
                RejectServer(rpc, "handshake.challenge_invalid");
                return;
            }

            PolicyValidationResult result = ModPolicyValidator.Validate(_policy, packet.Mods);
            if (!result.Accepted)
            {
                ModSyncPlugin.Log.LogWarning(Loc.Decode(result.Message, "English"));
                if (ModPolicyValidator.TryGetPackageRepairs(
                        _policy,
                        packet.Mods,
                        out IReadOnlyList<ModPackagePolicy> installPackages,
                        out IReadOnlyList<string> disallowedGuids))
                {
                    SendRepairManifest(
                        rpc, packet.Nonce, installPackages, disallowedGuids);
                    return;
                }

                RejectServer(rpc, result.Message);
                return;
            }

            if (!ServerConnections.Accept(rpc))
            {
                RejectServer(rpc, "handshake.state_invalid");
                return;
            }

            try
            {
                rpc.Invoke(AcceptedRpc, packet.Nonce);
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogWarning(
                    $"Could not confirm the validation to the client: {exception}");
                ScheduleDisconnect(rpc);
                return;
            }

            ModSyncPlugin.Log.LogInfo("Client authorized: valid mod inventory.");
        }

        private static void ReceiveChallenge(ZRpc rpc, int protocolVersion, string nonce)
        {
            if (!ClientConnections.TryGetValue(rpc, out ClientConnection connection) ||
                !connection.Requested || connection.Failed)
            {
                return;
            }

            if (protocolVersion != InventoryPacketCodec.ProtocolVersion ||
                !InventoryPacketCodec.IsNonce(nonce) ||
                connection.Nonce != null ||
                Time.unscaledTime >= connection.Deadline)
            {
                FailClient(rpc, connection, Loc.T("handshake.challenge_late"), null);
                return;
            }

            connection.Nonce = nonce;
        }

        private static void SendRepairManifest(
            ZRpc rpc,
            string nonce,
            IReadOnlyList<ModPackagePolicy> installPackages,
            IReadOnlyList<string> disallowedGuids)
        {
            List<ModPackagePolicy> availablePackages =
                new List<ModPackagePolicy>(_policy.RequiredPackages.Count +
                                           _policy.OptionalPackages.Count);
            availablePackages.AddRange(_policy.RequiredPackages);
            availablePackages.AddRange(_policy.OptionalPackages);
            List<ModDescriptor> approvedMods =
                new List<ModDescriptor>(_policy.Required.Count + _policy.Optional.Count);
            approvedMods.AddRange(_policy.Required.Values);
            approvedMods.AddRange(_policy.Optional.Values);

            try
            {
                string encoded = ThunderstoreSyncPacketCodec.EncodeToBase64(
                    new ThunderstoreSyncManifest(
                        nonce,
                        installPackages,
                        availablePackages,
                        approvedMods,
                        disallowedGuids));
                if (!ServerConnections.ExtendDeadline(
                        rpc, Time.unscaledTime + RepairTimeoutSeconds))
                {
                    RejectServer(rpc, "repair.timeout_extend_failed");
                    return;
                }

                RepairConnections.Add(rpc);
                rpc.Invoke(RepairRpc, encoded);
                ModSyncPlugin.Log.LogInfo(
                    $"The client was asked to repair {installPackages.Count} package(s) " +
                    $"and remove {disallowedGuids.Count} unauthorized mod(s).");
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogWarning(
                    $"Could not send the repair manifest: {exception}");
                RejectServer(rpc, "repair.sync_start_failed");
            }
        }

        private static void ReceiveRepair(
            ZRpc rpc,
            string encodedManifest)
        {
            if (!ClientConnections.TryGetValue(rpc, out ClientConnection connection) ||
                !connection.Requested ||
                !connection.InventorySent ||
                connection.Failed ||
                connection.Verified ||
                connection.RepairAttempted)
            {
                return;
            }

            ThunderstoreSyncManifest manifest;
            try
            {
                manifest = ThunderstoreSyncPacketCodec.DecodeFromBase64(encodedManifest);
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is ArgumentException)
            {
                FailClient(rpc, connection,
                    Loc.T("repair.manifest_invalid"),
                    exception,
                    ClientDisconnectFallbackSeconds);
                NotifyRepairFinished(rpc, "failed");
                return;
            }

            if (connection.Nonce == null ||
                !string.Equals(connection.Nonce, manifest.Nonce, StringComparison.Ordinal) ||
                Time.unscaledTime >= connection.Deadline)
            {
                FailClient(rpc, connection,
                    Loc.T("repair.manifest_mismatch"),
                    null,
                    ClientDisconnectFallbackSeconds);
                NotifyRepairFinished(rpc, "failed");
                return;
            }

            connection.RepairAttempted = true;
            try
            {
                connection.RepairCancellation = new CancellationTokenSource();
                connection.Deadline = Time.unscaledTime + RepairTimeoutSeconds;
                string pluginDirectory = Paths.PluginPath;
                CancellationToken cancellationToken =
                    connection.RepairCancellation.Token;
                connection.RepairManifest = manifest;
                connection.RepairPreviewTask = Task.Run(() =>
                    ThunderstorePackageInstaller.PrepareInstall(
                        pluginDirectory,
                        manifest,
                        cancellationToken));
            }
            catch (Exception exception)
            {
                FailClient(rpc, connection,
                    Loc.T("repair.start_failed"),
                    exception,
                    ClientDisconnectFallbackSeconds);
                NotifyRepairFinished(rpc, "failed");
            }
        }

        private static void ReceiveRepairFinished(ZRpc rpc, string status)
        {
            if (!RepairConnections.Remove(rpc))
            {
                return;
            }

            string message;
            if (string.Equals(status, "updated", StringComparison.Ordinal))
            {
                message = "repair.applied";
            }
            else if (string.Equals(status, "confirming", StringComparison.Ordinal))
            {
                message = "repair.confirming";
            }
            else if (string.Equals(status, "cancelled", StringComparison.Ordinal))
            {
                message = "repair.cancelled";
            }
            else
            {
                message = "repair.incomplete";
            }

            ModSyncPlugin.Log.LogInfo(Loc.Decode(message, "English"));
            RejectServer(rpc, message);
        }

        internal static string PendingRepairConfirmationMessage =>
            _pendingRepairConfirmationMessage;

        // The confirmation is shown in the main menu: in the game scene Valheim's
        // black loading screen covers any UI.
        private static void BeginMenuConfirmation(
            ZRpc rpc,
            ClientConnection connection,
            ThunderstorePackageInstaller.InstallPlan plan)
        {
            string message = BuildRepairConfirmationMessage(connection, plan.Preview);
            connection.Failed = true;
            connection.Password = null;
            connection.RepairManifest = null;
            connection.RepairPlan = null;
            connection.RepairCancellation?.Dispose();
            connection.RepairCancellation = null;
            _deferredPlan = plan;
            _deferredRpc = rpc;
            _pendingRepairConfirmationMessage = message;
            NotifyRepairFinished(rpc, "confirming");
            _logoutAt = Time.unscaledTime + 0.5f;
        }

        private static void LogoutToMenu()
        {
            try
            {
                if (Game.instance != null && ZNet.instance != null)
                {
                    Game.instance.Logout(false);
                    return;
                }
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogWarning(
                    $"Could not return to the main menu: {exception.Message}");
            }

            if (_deferredRpc != null)
            {
                Disconnect(_deferredRpc);
            }
        }

        private static void TickDeferred(float now)
        {
            if (_logoutAt > 0f && now >= _logoutAt)
            {
                _logoutAt = 0f;
                LogoutToMenu();
            }

            if (_deferredTask == null)
            {
                return;
            }

            string message;
            if (now >= _deferredDeadline)
            {
                _deferredCancellation?.Cancel();
                message = Loc.T("repair.install_timeout");
            }
            else if (!_deferredTask.IsCompleted)
            {
                return;
            }
            else
            {
                try
                {
                    _deferredTask.GetAwaiter().GetResult();
                    message = Loc.T("repair.completed");
                }
                catch (Exception exception)
                {
                    message = Loc.T("repair.install_failed",
                        exception.Message);
                    ModSyncPlugin.Log.LogError($"{message} {exception}");
                }
            }

            _deferredTask = null;
            _deferredCancellation?.Dispose();
            _deferredCancellation = null;
            _pendingConnectionFailureMessage = message;
        }

        internal static void AcceptPendingRepair()
        {
            ThunderstorePackageInstaller.InstallPlan plan = _deferredPlan;
            if (plan == null)
            {
                return;
            }

            _deferredPlan = null;
            _pendingRepairConfirmationMessage = null;
            try
            {
                _deferredCancellation = new CancellationTokenSource();
                CancellationToken cancellationToken = _deferredCancellation.Token;
                _deferredDeadline = Time.unscaledTime + RepairTimeoutSeconds;
                _deferredTask = Task.Run(() =>
                    ThunderstorePackageInstaller.Install(plan, cancellationToken));
                _pendingConnectionFailureMessage =
                    Loc.T("repair.installing");
            }
            catch (Exception exception)
            {
                _pendingConnectionFailureMessage =
                    Loc.T("repair.install_start_failed",
                        exception.Message);
            }
        }

        internal static void CancelPendingRepair()
        {
            if (_deferredPlan == null)
            {
                return;
            }

            _deferredPlan = null;
            _pendingRepairConfirmationMessage = null;
            _pendingConnectionFailureMessage =
                Loc.T("repair.cancelled_by_user");
        }

        private static string BuildRepairConfirmationMessage(
            ClientConnection connection,
            IReadOnlyList<ThunderstorePackageSummary> packages)
        {
            if (connection.Inventory == null)
            {
                throw new InvalidDataException(
                    "The local inventory was not kept to show the repair.");
            }

            List<string> lines = new List<string>();
            if (packages.Count > 0)
            {
                lines.Add(Loc.T("dialog.install"));
                for (int index = 0; index < packages.Count; index++)
                {
                    lines.Add(packages[index].Name + " - " + packages[index].Version);
                }
            }

            IReadOnlyList<string> removals = connection.RepairManifest.DisallowedGuids;
            if (removals.Count > 0)
            {
                if (lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.Add(Loc.T("dialog.remove"));
                for (int index = 0; index < removals.Count; index++)
                {
                    ModDescriptor descriptor = null;
                    for (int inventoryIndex = 0;
                         inventoryIndex < connection.Inventory.Count;
                         inventoryIndex++)
                    {
                        if (string.Equals(
                                connection.Inventory[inventoryIndex].Guid,
                                removals[index],
                                StringComparison.Ordinal))
                        {
                            descriptor = connection.Inventory[inventoryIndex];
                            break;
                        }
                    }

                    if (descriptor == null)
                    {
                        throw new InvalidDataException(
                            $"The local version of mod '{removals[index]}' was not found.");
                    }

                    lines.Add(
                        GetDisplayName(descriptor) +
                        " - " + descriptor.Version);
                }
            }

            return string.Join("\n", lines);
        }

        private static string GetDisplayName(ModDescriptor descriptor)
        {
            if (!string.IsNullOrWhiteSpace(descriptor.DisplayName))
            {
                return descriptor.DisplayName;
            }

            int separator = descriptor.Guid.LastIndexOf('.');
            return separator >= 0 && separator < descriptor.Guid.Length - 1
                ? descriptor.Guid.Substring(separator + 1)
                : descriptor.Guid;
        }

        private static void NotifyRepairFinished(ZRpc rpc, string status)
        {
            try
            {
                rpc.Invoke(RepairFinishedRpc, status);
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogWarning(
                    $"Could not report the repair result to the server: {exception.Message}");
            }
        }

        private static void ReceiveAccepted(ZRpc rpc, string nonce)
        {
            if (!ClientConnections.TryGetValue(rpc, out ClientConnection connection) ||
                connection.Failed || !connection.InventorySent)
            {
                return;
            }

            if (connection.Nonce == null ||
                !string.Equals(connection.Nonce, nonce, StringComparison.Ordinal) ||
                Time.unscaledTime >= connection.Deadline)
            {
                FailClient(rpc, connection, Loc.T("handshake.acceptance_mismatch"), null);
                return;
            }

            connection.Verified = true;
            connection.Nonce = null;
            string password = connection.Password;
            connection.Password = null;

            if (SendPeerInfoMethod == null)
            {
                FailClient(rpc, connection,
                    Loc.T("handshake.connect_method_missing"), null);
                return;
            }

            try
            {
                SendPeerInfoMethod.Invoke(
                    connection.Network, new object[] { rpc, password });
            }
            catch (Exception exception)
            {
                FailClient(rpc, connection,
                    Loc.T("handshake.resume_failed"), exception);
            }
        }

        private static void ReceiveRejected(ZRpc rpc, string message)
        {
            if (ClientConnections.TryGetValue(rpc, out ClientConnection connection))
            {
                FailClient(rpc, connection,
                    string.IsNullOrWhiteSpace(message)
                        ? Loc.T("handshake.rejected_default")
                        : Loc.Decode(message),
                    null,
                    ClientDisconnectFallbackSeconds);
                try
                {
                    rpc.Invoke(RejectionAckRpc);
                }
                catch (Exception exception)
                {
                    ModSyncPlugin.Log.LogWarning(
                        $"Could not confirm receipt of the rejection: {exception.Message}");
                }
            }
        }

        private static void ReceiveRejectionAck(ZRpc rpc)
        {
            if (ServerDisconnects.ContainsKey(rpc))
            {
                RequestServerDisconnect(rpc);
            }
        }

        private static void TickClient(ZRpc rpc, ClientConnection connection, float now)
        {
            if (!connection.Requested || connection.Failed || connection.Verified)
            {
                return;
            }

            if (connection.RepairPreviewTask != null)
            {
                if (now >= connection.Deadline)
                {
                    connection.RepairCancellation?.Cancel();
                    FailClient(rpc, connection,
                        Loc.T("handshake.prepare_timeout"),
                        null,
                        ClientDisconnectFallbackSeconds);
                    NotifyRepairFinished(rpc, "failed");
                    return;
                }

                if (!connection.RepairPreviewTask.IsCompleted)
                {
                    return;
                }

                try
                {
                    ThunderstorePackageInstaller.InstallPlan plan =
                        connection.RepairPreviewTask.GetAwaiter().GetResult();
                    BeginMenuConfirmation(rpc, connection, plan);
                }
                catch (Exception exception)
                {
                    FailClient(rpc, connection,
                        Loc.T("handshake.list_prepare_failed", exception.Message),
                        exception,
                        ClientDisconnectFallbackSeconds);
                    NotifyRepairFinished(rpc, "failed");
                }

                return;
            }

            if (now >= connection.Deadline)
            {
                FailClient(rpc, connection,
                    Loc.T("handshake.validation_timeout"), null);
                return;
            }

            if (connection.Nonce == null ||
                connection.InventoryTask == null ||
                !connection.InventoryTask.IsCompleted ||
                connection.InventorySent)
            {
                return;
            }

            try
            {
                IReadOnlyList<ModDescriptor> inventory =
                    connection.InventoryTask.GetAwaiter().GetResult();
                string payload = InventoryPacketCodec.EncodeToBase64(
                    connection.Nonce, inventory);
                connection.Inventory = inventory;
                connection.InventorySent = true;
                rpc.Invoke(InventoryRpc, payload);
            }
            catch (Exception exception)
            {
                FailClient(rpc, connection,
                    Loc.T("handshake.inventory_send_failed"), exception);
            }
        }

        private static void RejectServer(ZRpc rpc, string message)
        {
            if (rpc == null || ServerConnections.IsAccepted(rpc))
            {
                return;
            }

            PendingRequests.Remove(rpc);
            RepairConnections.Remove(rpc);
            ServerConnections.Reject(rpc);
            try
            {
                rpc.Invoke(RejectedRpc, message);
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogWarning(
                    $"Could not send the rejection reason: {exception.Message}");
            }

            ScheduleServerDisconnect(rpc);
        }

        private static void FailClient(
            ZRpc rpc,
            ClientConnection connection,
            string message,
            Exception exception,
            float disconnectDelay = 0.5f)
        {
            if (connection.Failed)
            {
                return;
            }

            connection.Failed = true;
            connection.Password = null;
            connection.RepairManifest = null;
            connection.RepairPlan = null;
            if (connection.RepairPreviewTask != null &&
                !connection.RepairPreviewTask.IsCompleted)
            {
                connection.RepairCancellation?.Cancel();
            }
            _pendingConnectionFailureMessage = message;
            if (exception == null)
            {
                ModSyncPlugin.Log.LogWarning(message);
            }
            else
            {
                ModSyncPlugin.Log.LogError($"{message} {exception}");
            }

            ScheduleDisconnect(rpc, disconnectDelay);
        }

        private static void ScheduleDisconnect(ZRpc rpc, float delay = 0.5f)
        {
            if (!DisconnectDeadlines.ContainsKey(rpc))
            {
                DisconnectDeadlines.Add(rpc, Time.unscaledTime + delay);
            }
        }

        private static void ScheduleServerDisconnect(ZRpc rpc)
        {
            ServerDisconnects[rpc] = false;
            DisconnectDeadlines[rpc] = Time.unscaledTime + RejectionAckTimeoutSeconds;
        }

        private static void RequestServerDisconnect(ZRpc rpc)
        {
            if (!ServerDisconnects.ContainsKey(rpc) ||
                ServerDisconnects[rpc])
            {
                return;
            }

            ServerDisconnects[rpc] = true;
            DisconnectDeadlines[rpc] = Time.unscaledTime + DisconnectTimeoutSeconds;
            try
            {
                rpc.Invoke("Disconnect");
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogWarning(
                    $"Could not request the disconnection of the rejected client: {exception.Message}");
            }
        }

        internal static string PendingConnectionFailureMessage =>
            _pendingConnectionFailureMessage;

        internal static void ClearConnectionFailureMessage()
        {
            _pendingConnectionFailureMessage = null;
        }

        // Sin un estado de error, el menú se queda en la transición de conexión (negro).
        private static void MarkClientConnectionFailed()
        {
            try
            {
                if (ZNet.instance == null || ZNet.instance.IsServer())
                {
                    return;
                }

                Type statusType = typeof(ZNet).GetNestedType(
                    "ConnectionStatus", BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo statusField = typeof(ZNet).GetField(
                    "m_connectionStatus",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (statusType != null && statusField != null)
                {
                    statusField.SetValue(
                        null, Enum.Parse(statusType, "ErrorDisconnected"));
                }
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogWarning(
                    $"Could not mark the connection as failed: {exception.Message}");
            }
        }

        private static void Disconnect(ZRpc rpc)
        {
            DisconnectDeadlines.Remove(rpc);
            ServerDisconnects.Remove(rpc);
            if (!string.IsNullOrWhiteSpace(_pendingConnectionFailureMessage))
            {
                MarkClientConnectionFailed();
            }

            try
            {
                ZNet network = ZNet.instance;
                if (network != null)
                {
                    List<ZNetPeer> peers = network.GetPeers();
                    for (int index = 0; index < peers.Count; index++)
                    {
                        if (ReferenceEquals(peers[index].m_rpc, rpc))
                        {
                            network.Disconnect(peers[index]);
                            return;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogWarning(
                    $"Could not close the connection locally: {exception}");
            }

            try
            {
                rpc.Invoke("Disconnect");
            }
            catch (Exception exception)
            {
                ModSyncPlugin.Log.LogError(
                    $"Could not close the rejected connection: {exception}");
            }
        }

        private static string CreateNonce()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            char[] nonce = new char[bytes.Length * 2];
            const string hexadecimal = "0123456789abcdef";
            for (int index = 0; index < bytes.Length; index++)
            {
                nonce[index * 2] = hexadecimal[bytes[index] >> 4];
                nonce[index * 2 + 1] = hexadecimal[bytes[index] & 0x0f];
            }

            return new string(nonce);
        }

        private sealed class ClientConnection
        {
            internal ClientConnection(ZNet network)
            {
                Network = network;
            }

            internal readonly ZNet Network;
            internal Task<IReadOnlyList<ModDescriptor>> InventoryTask;
            internal IReadOnlyList<ModDescriptor> Inventory;
            internal string Password;
            internal string Nonce;
            internal float Deadline;
            internal Task<ThunderstorePackageInstaller.InstallPlan> RepairPreviewTask;
            internal CancellationTokenSource RepairCancellation;
            internal ThunderstoreSyncManifest RepairManifest;
            internal ThunderstorePackageInstaller.InstallPlan RepairPlan;
            internal bool Requested;
            internal bool InventorySent;
            internal bool Verified;
            internal bool Failed;
            internal bool RepairAttempted;
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal static class ModSyncNewConnectionPatch
    {
        private static void Postfix(ZNet __instance, ZNetPeer peer)
        {
            ModSyncHandshake.Register(__instance, peer);
        }
    }

    [HarmonyPatch(typeof(ZNet), "SendPeerInfo")]
    internal static class ModSyncSendPeerInfoPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ZNet __instance, ZRpc rpc, string __1)
        {
            return ModSyncHandshake.AllowPeerInfo(__instance, rpc, __1);
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    internal static class ModSyncAdmitPeerPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ZNet __instance, ZRpc rpc)
        {
            return __instance != null &&
                   (!__instance.IsServer() ||
                    ModSyncHandshake.AdmitPeerInfo(rpc));
        }
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    internal static class ModSyncDisconnectPatch
    {
        private static void Prefix(ZNetPeer peer)
        {
            if (peer != null && peer.m_rpc != null)
            {
                ModSyncHandshake.Remove(peer.m_rpc);
            }
        }
    }

    [HarmonyPatch]
    internal static class ModSyncConnectionFailurePatch
    {
        private static readonly FieldInfo ConnectionFailedErrorField =
            AccessTools.Field(typeof(FejdStartup), "m_connectionFailedError");
        private static readonly FieldInfo ConnectionFailedPanelField =
            AccessTools.Field(typeof(FejdStartup), "m_connectionFailedPanel");
        private static Button _repairOriginalButton;
        private static GameObject _repairAcceptButtonObject;
        private static Vector2 _repairOriginalButtonPosition;
        private static string _repairOriginalButtonLabel;
        private static string _repairOriginalErrorText;
        private static FejdStartup _repairStartup;
        private static RectTransform _repairDialogRect;
        private static Vector2 _repairDialogSizeDelta;
        private static object _repairCancelLabel;
        private static PropertyInfo _repairCancelLabelProperty;
        private static object _repairAcceptLabel;
        private static PropertyInfo _repairAcceptLabelProperty;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FejdStartup), "ShowConnectError")]
        private static void ShowConnectError(FejdStartup __instance)
        {
            ShowPendingMessage(__instance, false);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FejdStartup), "Start")]
        private static void Start(FejdStartup __instance)
        {
            ShowPendingMessage(__instance, true);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FejdStartup), "OnConnectionFailedOk")]
        private static void OnConnectionFailedOk(FejdStartup __instance)
        {
            ShowPendingMessage(__instance, true);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FejdStartup), "Update")]
        private static void Update(FejdStartup __instance)
        {
            ShowRepairConfirmation(__instance);
            if (string.IsNullOrWhiteSpace(ModSyncHandshake.PendingRepairConfirmationMessage))
            {
                ShowPendingMessage(__instance, true);
            }
        }

        internal static void ClearRepairConfirmation()
        {
            RestoreRepairConfirmation(_repairStartup);
        }

        private static void ShowRepairConfirmation(FejdStartup startup)
        {
            string message = ModSyncHandshake.PendingRepairConfirmationMessage;
            if (string.IsNullOrWhiteSpace(message))
            {
                RestoreRepairConfirmation(startup);
                return;
            }

            if (_repairStartup == startup &&
                _repairAcceptButtonObject != null)
            {
                GameObject activePanel =
                    ConnectionFailedPanelField?.GetValue(startup) as GameObject;
                activePanel?.SetActive(true);
                EnforceLabels();
                return;
            }

            RestoreRepairConfirmation(startup);
            GameObject panel = ConnectionFailedPanelField?.GetValue(startup) as GameObject;
            object errorText = ConnectionFailedErrorField?.GetValue(startup);
            if (panel == null || errorText == null)
            {
                ModSyncPlugin.Log.LogError(
                    "The connection error panel was not found to confirm the synchronization.");
                ModSyncHandshake.CancelPendingRepair();
                return;
            }

            Button[] buttons = panel.GetComponentsInChildren<Button>(true);
            Button originalButton = null;
            for (int index = 0; index < buttons.Length; index++)
            {
                if (buttons[index] != null && buttons[index].gameObject.activeSelf)
                {
                    originalButton = buttons[index];
                    break;
                }
            }

            if (originalButton == null)
            {
                ModSyncPlugin.Log.LogError(
                    "The confirmation button was not found in Valheim's error panel.");
                ModSyncHandshake.CancelPendingRepair();
                return;
            }

            PropertyInfo errorTextProperty =
                errorText.GetType().GetProperty("text");
            string originalErrorText = errorTextProperty?.GetValue(errorText, null) as string;
            PropertyInfo buttonLabelProperty = FindTextProperty(originalButton.gameObject);
            object originalButtonLabelComponent =
                FindTextComponent(originalButton.gameObject);
            string originalButtonLabel =
                buttonLabelProperty?.GetValue(originalButtonLabelComponent, null) as string;
            if (errorTextProperty == null || !errorTextProperty.CanWrite ||
                originalButtonLabelComponent == null ||
                buttonLabelProperty == null || !buttonLabelProperty.CanWrite)
            {
                ModSyncPlugin.Log.LogError(
                    "Could not access the text of Valheim's error panel and button.");
                ModSyncHandshake.CancelPendingRepair();
                return;
            }

            RectTransform originalTransform =
                originalButton.transform as RectTransform;
            if (originalTransform == null)
            {
                ModSyncPlugin.Log.LogError(
                    "Valheim's error button has no RectTransform.");
                ModSyncHandshake.CancelPendingRepair();
                return;
            }

            Vector2 originalPosition = originalTransform.anchoredPosition;
            _repairStartup = startup;
            _repairOriginalButton = originalButton;
            _repairOriginalButtonPosition = originalPosition;
            _repairOriginalButtonLabel = originalButtonLabel;
            _repairOriginalErrorText = originalErrorText;
            try
            {
                RectTransform scrollView = MakeTextScrollable(errorText);
                if (scrollView != null)
                {
                    _repairViewRect = scrollView;
                }
                _repairOriginalButtonState = SaveRect(originalTransform);

                errorTextProperty.SetValue(
                    errorText,
                    Loc.T("dialog.header") +
                    "\n\n" + message,
                    null);
                buttonLabelProperty.SetValue(
                    originalButtonLabelComponent, Loc.T("dialog.cancel"), null);

                float buttonWidth = originalTransform.rect.width;
                float gap = 32f;
                float padding = 42f;
                float buttonOffset = buttonWidth * 0.5f + gap * 0.5f;
                originalTransform.anchoredPosition =
                    originalPosition + Vector2.right * buttonOffset;

                RectTransform dialogRect = originalTransform.parent as RectTransform;
                if (dialogRect != null)
                {
                    _repairDialogRect = dialogRect;
                    _repairDialogSizeDelta = dialogRect.sizeDelta;
                    float requiredWidth = buttonWidth * 2f + gap + padding * 2f;
                    float extraWidth = Mathf.Max(0f, requiredWidth - dialogRect.rect.width);
                    dialogRect.sizeDelta = _repairDialogSizeDelta +
                        new Vector2(extraWidth, padding * 2f);
                    originalTransform.anchoredPosition +=
                        Vector2.down * padding * 0.5f;
                }

                GameObject acceptButtonObject = UnityEngine.Object.Instantiate(
                    originalButton.gameObject,
                    originalButton.transform.parent,
                    false);
                _repairAcceptButtonObject = acceptButtonObject;
                acceptButtonObject.name = "ModSyncAcceptRepairButton";
                Button acceptButton = acceptButtonObject.GetComponent<Button>();
                RectTransform acceptTransform =
                    acceptButtonObject.transform as RectTransform;
                if (acceptButton == null || acceptTransform == null)
                {
                    UnityEngine.Object.Destroy(acceptButtonObject);
                    throw new InvalidOperationException(
                        "Could not create the Accept button for the synchronization.");
                }

                acceptTransform.anchoredPosition =
                    originalTransform.anchoredPosition +
                    Vector2.left * (buttonOffset * 2f);
                FitDialogToText(
                    scrollView,
                    dialogRect,
                    new[] { originalTransform, acceptTransform });
                PropertyInfo acceptLabelProperty =
                    FindTextProperty(acceptButtonObject);
                object acceptLabel = FindTextComponent(acceptButtonObject);
                if (acceptLabelProperty == null || acceptLabel == null ||
                    !acceptLabelProperty.CanWrite)
                {
                    UnityEngine.Object.Destroy(acceptButtonObject);
                    throw new InvalidOperationException(
                        "Could not set the text of the Accept button.");
                }

                _repairCancelLabel = originalButtonLabelComponent;
                _repairCancelLabelProperty = buttonLabelProperty;
                _repairAcceptLabel = acceptLabel;
                _repairAcceptLabelProperty = acceptLabelProperty;
                EnforceLabels();
                originalButton.onClick.AddListener(
                    ModSyncHandshake.CancelPendingRepair);
                acceptButton.onClick.AddListener(
                    ModSyncHandshake.AcceptPendingRepair);
                panel.SetActive(true);
            }
            catch (Exception exception)
            {
                RestoreRepairConfirmation(startup);
                ModSyncPlugin.Log.LogError(
                    $"Could not prepare the confirmation in Valheim's menu: {exception}");
                ModSyncHandshake.CancelPendingRepair();
            }
        }

        // Valheim re-localizes the panel texts and would overwrite "Cancel".
        private static void EnforceLabels()
        {
            SetLabel(_repairCancelLabel,             _repairCancelLabelProperty, Loc.T("dialog.cancel"));
                        SetLabel(_repairAcceptLabel, _repairAcceptLabelProperty, Loc.T("dialog.accept"));
        }

        private static void SetLabel(object component, PropertyInfo property, string value)
        {
            if (component != null && property != null &&
                !string.Equals(
                    property.GetValue(component, null) as string, value,
                    StringComparison.Ordinal))
            {
                property.SetValue(component, value, null);
            }
        }

        private static void RestoreRepairConfirmation(FejdStartup startup)
        {
            if (_repairStartup != null)
            {
                if (_repairDialogRect != null)
                {
                    _repairDialogRect.sizeDelta = _repairDialogSizeDelta;
                }

                _repairDialogRect = null;
                RestoreRect(_repairViewRect, _repairViewState);
                _repairViewRect = null;
                _repairViewState = null;
                _repairCancelLabel = null;
                _repairAcceptLabel = null;

                if (_repairOriginalButton != null)
                {
                    _repairOriginalButton.onClick.RemoveListener(
                        ModSyncHandshake.CancelPendingRepair);
                    RectTransform originalTransform =
                        _repairOriginalButton.transform as RectTransform;
                    if (originalTransform != null)
                    {
                        RestoreRect(originalTransform, _repairOriginalButtonState);
                        originalTransform.anchoredPosition =
                            _repairOriginalButtonPosition;
                    }

                    SetText(_repairOriginalButton.gameObject, _repairOriginalButtonLabel);
                }

                if (_repairAcceptButtonObject != null)
                {
                    UnityEngine.Object.Destroy(_repairAcceptButtonObject);
                }

                GameObject panel =
                    ConnectionFailedPanelField?.GetValue(_repairStartup) as GameObject;
                if (panel == null || !panel.activeSelf)
                {
                    object errorText =
                        ConnectionFailedErrorField?.GetValue(_repairStartup);
                    PropertyInfo textProperty = errorText?.GetType().GetProperty("text");
                    if (textProperty != null && textProperty.CanWrite)
                    {
                        textProperty.SetValue(
                            errorText, _repairOriginalErrorText, null);
                    }
                }
            }

            _repairStartup = null;
            _repairOriginalButton = null;
            _repairAcceptButtonObject = null;
            _repairOriginalButtonLabel = null;
            _repairOriginalErrorText = null;
        }

        private const string ScrollViewName = "ModSyncScrollView";
        private const float ReadableFontSize = 18f;
        private const float MaxDialogHeightRatio = 0.8f;
        private const float MaxDialogWidthRatio = 0.7f;
        private static RectTransform _repairViewRect;
        private static Vector2[] _repairViewState;
        private static Vector2[] _repairOriginalButtonState;

        // Fixed font size plus a vertical scroll view, so long messages scroll
        // instead of shrinking through the auto-sizing of the original panel.
        private static RectTransform MakeTextScrollable(object textObject)
        {
            Component text = textObject as Component;
            RectTransform textRect = text != null ? text.transform as RectTransform : null;
            if (textRect == null)
            {
                return null;
            }

            TrySetProperty(text, "enableAutoSizing", false);
            TrySetProperty(text, "fontSize", ReadableFontSize);
            TrySetProperty(text, "enableWordWrapping", true);
            PropertyInfo overflow = text.GetType().GetProperty("overflowMode");
            if (overflow != null && overflow.CanWrite && overflow.PropertyType.IsEnum)
            {
                overflow.SetValue(text, Enum.ToObject(overflow.PropertyType, 0), null);
            }

            // TextAlignmentOptions.TopLeft
            PropertyInfo alignment = text.GetType().GetProperty("alignment");
            if (alignment != null && alignment.CanWrite && alignment.PropertyType.IsEnum)
            {
                alignment.SetValue(text, Enum.ToObject(alignment.PropertyType, 257), null);
            }

            RectTransform parent = textRect.parent as RectTransform;
            if (parent == null)
            {
                return null;
            }

            if (parent.name == ScrollViewName)
            {
                return parent;
            }

            GameObject viewObject = new GameObject(ScrollViewName, typeof(RectTransform));
            RectTransform view = (RectTransform)viewObject.transform;
            view.SetParent(parent, false);
            view.SetSiblingIndex(textRect.GetSiblingIndex());
            view.anchorMin = textRect.anchorMin;
            view.anchorMax = textRect.anchorMax;
            view.pivot = textRect.pivot;
            view.anchoredPosition = textRect.anchoredPosition;
            view.sizeDelta = textRect.sizeDelta;

            Image raycast = viewObject.AddComponent<Image>();
            raycast.color = new Color(0f, 0f, 0f, 0.45f);
            viewObject.AddComponent<RectMask2D>();

            const float textPadding = 12f;
            const float scrollbarWidth = 10f;
            textRect.SetParent(view, false);
            textRect.anchorMin = new Vector2(0f, 1f);
            textRect.anchorMax = new Vector2(1f, 1f);
            textRect.pivot = new Vector2(0.5f, 1f);
            textRect.anchoredPosition = Vector2.zero;
            textRect.offsetMin = new Vector2(textPadding, 0f);
            textRect.offsetMax = new Vector2(-(textPadding + scrollbarWidth), 0f);
            ContentSizeFitter fitter = text.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            GameObject barObject = new GameObject("ModSyncScrollbar", typeof(RectTransform));
            RectTransform barRect = (RectTransform)barObject.transform;
            barRect.SetParent(view, false);
            barRect.anchorMin = new Vector2(1f, 0f);
            barRect.anchorMax = new Vector2(1f, 1f);
            barRect.pivot = new Vector2(1f, 0.5f);
            barRect.sizeDelta = new Vector2(scrollbarWidth, -8f);
            barRect.anchoredPosition = new Vector2(-4f, 0f);
            Image track = barObject.AddComponent<Image>();
            track.color = new Color(1f, 1f, 1f, 0.12f);

            GameObject handleObject = new GameObject("Handle", typeof(RectTransform));
            RectTransform handleRect = (RectTransform)handleObject.transform;
            handleRect.SetParent(barRect, false);
            handleRect.sizeDelta = Vector2.zero;
            Image handle = handleObject.AddComponent<Image>();
            handle.color = new Color(1f, 0.78f, 0.35f, 0.85f);

            Scrollbar scrollbar = barObject.AddComponent<Scrollbar>();
            scrollbar.targetGraphic = handle;
            scrollbar.handleRect = handleRect;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            ScrollRect scroll = viewObject.AddComponent<ScrollRect>();
            scroll.viewport = view;
            scroll.content = textRect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility =
                ScrollRect.ScrollbarVisibility.AutoHide;
            return view;
        }

        // The dialog takes a large share of the screen and the scroll view fills it
        // (small margin), leaving only a bottom strip for the buttons.
        private static void FitDialogToText(
            RectTransform view,
            RectTransform dialog,
            RectTransform[] buttons)
        {
            RectTransform canvasRect = dialog != null ? dialog.root as RectTransform : null;
            if (view == null || dialog == null || canvasRect == null || buttons.Length == 0)
            {
                return;
            }

            _repairViewState = SaveRect(view);

            float width = Mathf.Max(
                dialog.rect.width, canvasRect.rect.width * MaxDialogWidthRatio);
            float height = Mathf.Max(
                dialog.rect.height, canvasRect.rect.height * MaxDialogHeightRatio);
            dialog.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            dialog.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);

            const float margin = 24f;
            float buttonHeight = buttons[0].rect.height;
            float buttonWidth = buttons[0].rect.width;
            float buttonRowHeight = buttonHeight + margin * 2f;
            float offset = (buttonWidth + 32f) * 0.5f;
            for (int index = 0; index < buttons.Length; index++)
            {
                float side = buttons[index].anchoredPosition.x >= 0f ? 1f : -1f;
                buttons[index].anchorMin = new Vector2(0.5f, 0f);
                buttons[index].anchorMax = new Vector2(0.5f, 0f);
                buttons[index].pivot = new Vector2(0.5f, 0.5f);
                buttons[index].anchoredPosition =
                    new Vector2(side * offset, margin + buttonHeight * 0.5f);
            }

            view.anchorMin = Vector2.zero;
            view.anchorMax = Vector2.one;
            view.pivot = new Vector2(0.5f, 0.5f);
            view.offsetMin = new Vector2(margin, buttonRowHeight + margin * 0.5f);
            view.offsetMax = new Vector2(-margin, -margin);
        }

        private static Vector2[] SaveRect(RectTransform rect)
        {
            return new[]
            {
                rect.anchorMin, rect.anchorMax, rect.pivot,
                rect.anchoredPosition, rect.sizeDelta
            };
        }

        private static void RestoreRect(RectTransform rect, Vector2[] state)
        {
            if (rect == null || state == null)
            {
                return;
            }

            rect.anchorMin = state[0];
            rect.anchorMax = state[1];
            rect.pivot = state[2];
            rect.anchoredPosition = state[3];
            rect.sizeDelta = state[4];
        }

        private static void TrySetProperty(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(name);
            if (property != null && property.CanWrite &&
                property.PropertyType.IsInstanceOfType(value))
            {
                property.SetValue(target, value, null);
            }
        }

        private static object FindTextComponent(GameObject target)
        {
            Type textType = AccessTools.TypeByName("TMPro.TMP_Text");
            return textType == null
                ? null
                : target.GetComponentInChildren(textType, true);
        }

        private static PropertyInfo FindTextProperty(GameObject target)
        {
            return FindTextComponent(target)?.GetType().GetProperty("text");
        }

        private static void SetText(GameObject target, string value)
        {
            object component = FindTextComponent(target);
            PropertyInfo textProperty = component?.GetType().GetProperty("text");
            if (textProperty != null && textProperty.CanWrite)
            {
                textProperty.SetValue(component, value, null);
            }
        }

        private static void ShowPendingMessage(FejdStartup startup, bool activatePanel)
        {
            string message = ModSyncHandshake.PendingConnectionFailureMessage;
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            object errorText = ConnectionFailedErrorField?.GetValue(startup);
            PropertyInfo textProperty = errorText?.GetType().GetProperty("text");
            if (textProperty == null || !textProperty.CanWrite)
            {
                ModSyncPlugin.Log.LogWarning(
                    "The text of Valheim's connection error panel was not found.");
                return;
            }

            try
            {
                MakeTextScrollable(errorText);
                textProperty.SetValue(errorText, message, null);
            }
            catch (Exception exception) when (
                exception is TargetException ||
                exception is TargetInvocationException ||
                exception is ArgumentException ||
                exception is MethodAccessException)
            {
                ModSyncPlugin.Log.LogError(
                    $"Could not show the rejection reason in Valheim: {exception}");
                return;
            }

            if (activatePanel)
            {
                GameObject panel = ConnectionFailedPanelField?.GetValue(startup) as GameObject;
                if (panel == null)
                {
                    ModSyncPlugin.Log.LogWarning(
                        "Valheim's connection error panel was not found.");
                    return;
                }

                panel.SetActive(true);
            }

            ModSyncHandshake.ClearConnectionFailureMessage();
        }
    }
}
