using System;
using System.IO;

namespace ModSync
{
    internal static class ModPolicyBootstrap
    {
        internal static bool EnsureDirectoriesAndCopySelf(
            string configRoot,
            string selfAssemblyPath)
        {
            string policyRoot = Path.Combine(configRoot, "ModSync");
            string requiredDirectory = Path.Combine(policyRoot, "required_mods");
            string optionalDirectory = Path.Combine(policyRoot, "optional_mods");
            Directory.CreateDirectory(requiredDirectory);
            Directory.CreateDirectory(optionalDirectory);

            if (string.IsNullOrWhiteSpace(selfAssemblyPath) ||
                !File.Exists(selfAssemblyPath))
            {
                throw new FileNotFoundException(
                    "Could not locate the loaded ModSync assembly.", selfAssemblyPath);
            }

            string destination = Path.Combine(
                requiredDirectory, Path.GetFileName(selfAssemblyPath));
            if (string.Equals(
                    Path.GetFullPath(selfAssemblyPath),
                    Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase) ||
                File.Exists(destination) ||
                Directory.GetFiles(
                    requiredDirectory,
                    Path.GetFileName(selfAssemblyPath),
                    SearchOption.AllDirectories).Length > 0)
            {
                return false;
            }

            File.Copy(selfAssemblyPath, destination);
            return true;
        }
    }
}
