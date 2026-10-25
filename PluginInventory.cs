using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BepInEx.Bootstrap;

namespace ModSync
{
    internal sealed class PluginFile
    {
        internal PluginFile(string guid, string version, string path, string displayName)
        {
            Guid = guid;
            Version = version;
            Path = path;
            DisplayName = displayName;
        }

        internal string Guid { get; }
        internal string Version { get; }
        internal string Path { get; }
        internal string DisplayName { get; }
    }

    internal static class PluginInventory
    {
        internal static PluginFile[] Snapshot()
        {
            PluginFile[] files = new PluginFile[Chainloader.PluginInfos.Count];
            int index = 0;
            foreach (var plugin in Chainloader.PluginInfos.Values)
            {
                files[index++] = new PluginFile(
                    plugin.Metadata.GUID,
                    plugin.Metadata.Version.ToString(),
                    plugin.Location,
                    plugin.Metadata.Name);
            }

            return files;
        }

        internal static IReadOnlyList<ModDescriptor> Hash(
            PluginFile[] plugins,
            string pluginRoot)
        {
            Dictionary<string, string> hashes =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<ModDescriptor> result = new List<ModDescriptor>(plugins.Length);

            for (int index = 0; index < plugins.Length; index++)
            {
                PluginFile plugin = plugins[index];
                if (string.IsNullOrWhiteSpace(plugin.Path))
                {
                    throw new InvalidDataException(
                        $"The mod file for '{plugin.Guid}' was not found.");
                }

                string path = Path.GetFullPath(plugin.Path);
                if (!hashes.TryGetValue(path, out string hash))
                {
                    hash = Sha256File(path);
                    hashes.Add(path, hash);
                }

                result.Add(new ModDescriptor(
                    plugin.Guid, plugin.Version, hash, plugin.DisplayName));
            }

            if (string.IsNullOrEmpty(pluginRoot) || !Directory.Exists(pluginRoot))
            {
                throw new DirectoryNotFoundException(
                    $"The BepInEx plugins folder was not found: {pluginRoot}");
            }

            string root = Path.GetFullPath(pluginRoot);
            string[] dllPaths = Directory.GetFiles(
                root, "*.dll", SearchOption.AllDirectories);
            for (int index = 0; index < dllPaths.Length; index++)
            {
                string path = Path.GetFullPath(dllPaths[index]);
                if (hashes.ContainsKey(path))
                {
                    continue;
                }

                string hash = Sha256File(path);
                hashes.Add(path, hash);
                result.AddRange(ModPolicyLoader.ReadDescriptors(path, hash));
            }

            return result;
        }

        internal static string Sha256File(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] digest = algorithm.ComputeHash(stream);
                char[] characters = new char[digest.Length * 2];
                const string hexadecimal = "0123456789abcdef";

                for (int index = 0; index < digest.Length; index++)
                {
                    characters[index * 2] = hexadecimal[digest[index] >> 4];
                    characters[index * 2 + 1] = hexadecimal[digest[index] & 0x0f];
                }

                return new string(characters);
            }
        }
    }
}
