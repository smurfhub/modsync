using BepInEx;
using HarmonyLib;

namespace ModSync
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ModSyncPlugin : BaseUnityPlugin
    {
        internal const string PluginGuid = "es.uyimbube.modsync";
        internal const string PluginName = "ModSync";
        internal const string PluginVersion = "0.3.3";

        internal static BepInEx.Logging.ManualLogSource Log { get; private set; }

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            string pluginDirectory = System.IO.Path.GetDirectoryName(typeof(ModSyncPlugin).Assembly.Location);
            Loc.LoadDirectory(System.IO.Path.Combine(pluginDirectory ?? string.Empty, "lang"),
                message => Logger.LogWarning(message));
            Loc.LoadDirectory(System.IO.Path.Combine(Paths.ConfigPath, "ModSync", "lang"),
                message => Logger.LogWarning(message));
            if (!Loc.HasLanguage("English"))
            {
                Logger.LogWarning("lang\\English.json was not found; messages will show their identifiers.");
            }
            ModSyncHandshake.Configure(
                Paths.ConfigPath,
                typeof(ModSyncPlugin).Assembly.Location);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            ModSyncHandshake.Tick();
        }

        private void OnDestroy()
        {
            ModSyncConnectionFailurePatch.ClearRepairConfirmation();
            ModSyncHandshake.Clear();
            _harmony?.UnpatchSelf();
            _harmony = null;
            Log = null;
        }
    }
}
