using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using BepInEx;
using Mono.Cecil;

namespace ModSync
{
    internal static class ModPolicyLoader
    {
        private static readonly Regex ManifestVersionPattern = new Regex(
            "\"version_number\"\\s*:\\s*\"(?<version>[^\"]+)\"",
            RegexOptions.CultureInvariant);

        private const string RequiredDirectoryName = "required_mods";
        private const string OptionalDirectoryName = "optional_mods";

        internal static string RequiredDirectory(string configRoot)
        {
            return Path.Combine(configRoot, "ModSync", RequiredDirectoryName);
        }

        internal static string OptionalDirectory(string configRoot)
        {
            return Path.Combine(configRoot, "ModSync", OptionalDirectoryName);
        }

        internal static ModPolicy Load(string configRoot, string selfAssemblyPath)
        {
            ModPolicyBootstrap.EnsureDirectoriesAndCopySelf(configRoot, selfAssemblyPath);
            List<ModPackagePolicy> packages = new List<ModPackagePolicy>();
            List<ModDescriptor> required =
                LoadDirectory(RequiredDirectory(configRoot), true, packages);
            List<ModDescriptor> optional =
                LoadDirectory(OptionalDirectory(configRoot), false, packages);
            return ModPolicy.Create(required, optional, packages);
        }

        private static List<ModDescriptor> LoadDirectory(
            string directory,
            bool required,
            List<ModPackagePolicy> packages)
        {
            List<ModDescriptor> mods = new List<ModDescriptor>();
            string[] paths = Directory.GetFiles(
                directory, "*.dll", SearchOption.TopDirectoryOnly);
            Array.Sort(paths, StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < paths.Length; index++)
            {
                mods.AddRange(ReadDescriptors(paths[index]));
            }

            string[] packageDirectories = Directory.GetDirectories(
                directory, "*", SearchOption.TopDirectoryOnly);
            Array.Sort(packageDirectories, StringComparer.OrdinalIgnoreCase);
            for (int directoryIndex = 0;
                 directoryIndex < packageDirectories.Length;
                 directoryIndex++)
            {
                string packageName = ResolvePackageName(packageDirectories[directoryIndex]);

                string[] packagePaths = Directory.GetFiles(
                    packageDirectories[directoryIndex],
                    "*.dll",
                    SearchOption.AllDirectories);
                Array.Sort(packagePaths, StringComparer.OrdinalIgnoreCase);
                if (packagePaths.Length == 0)
                {
                    throw new InvalidDataException(
                        $"The Thunderstore package '{packageName}' does not contain approved DLLs.");
                }

                List<ModDescriptor> packageMods = new List<ModDescriptor>();
                for (int pathIndex = 0; pathIndex < packagePaths.Length; pathIndex++)
                {
                    packageMods.AddRange(ReadDescriptors(packagePaths[pathIndex]));
                }

                mods.AddRange(packageMods);
                packages.Add(new ModPackagePolicy(
                    packageName, required, packageMods.AsReadOnly()));
            }

            return mods;
        }

        // Acepta 'Owner-Package-Version' o, como BepInEx\plugins, 'Owner-Package'
        // con la versión tomada del manifest.json de la carpeta.
        private static string ResolvePackageName(string directory)
        {
            string folderName = Path.GetFileName(directory);
            if (ThunderstorePackageIdentity.HasVersionSuffix(folderName))
            {
                return folderName;
            }

            string manifestPath = Path.Combine(directory, "manifest.json");
            if (File.Exists(manifestPath))
            {
                Match match = ManifestVersionPattern.Match(File.ReadAllText(manifestPath));
                string fullName = match.Success
                    ? folderName + "-" + match.Groups["version"].Value
                    : null;
                if (fullName != null && ThunderstorePackageIdentity.HasVersionSuffix(fullName))
                {
                    return fullName;
                }
            }

            throw new InvalidDataException(
                $"The policy folder '{folderName}' must be named owner-package-version, or owner-package with a manifest.json that has a valid version_number.");
        }

        private static IReadOnlyList<ModDescriptor> ReadDescriptors(string path)
        {
            string hash = PluginInventory.Sha256File(path);
            AssemblyDefinition assembly;
            try
            {
                assembly = AssemblyDefinition.ReadAssembly(path);
            }
            catch (BadImageFormatException)
            {
                return new[]
                {
                    new ModDescriptor(
                        CreateLibraryGuid(Path.GetFileNameWithoutExtension(path)),
                        "0.0.0.0",
                        hash,
                        Path.GetFileNameWithoutExtension(path))
                };
            }

            using (assembly)
            {
                return ReadDescriptors(assembly, hash);
            }
        }

        internal static IReadOnlyList<ModDescriptor> ReadDescriptors(
            string path,
            string hash)
        {
            try
            {
                using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(path))
                {
                    return ReadDescriptors(assembly, hash);
                }
            }
            catch (BadImageFormatException)
            {
                return new[]
                {
                    new ModDescriptor(
                        CreateLibraryGuid(Path.GetFileNameWithoutExtension(path)),
                        "0.0.0.0",
                        hash,
                        Path.GetFileNameWithoutExtension(path))
                };
            }
        }

        private static IReadOnlyList<ModDescriptor> ReadDescriptors(
            AssemblyDefinition assembly,
            string hash)
        {
            List<ModDescriptor> plugins = new List<ModDescriptor>();
            for (int moduleIndex = 0; moduleIndex < assembly.Modules.Count; moduleIndex++)
            {
                AddPluginDescriptors(
                    assembly.Modules[moduleIndex].Types,
                    hash,
                    plugins);
            }

            if (plugins.Count > 0)
            {
                return plugins;
            }

            string assemblyName = assembly.Name.Name;
            string version = assembly.Name.Version == null
                ? "0.0.0.0"
                : assembly.Name.Version.ToString();
            return new[]
            {
                new ModDescriptor(
                    CreateLibraryGuid(assemblyName), version, hash, assemblyName)
            };
        }

        private static void AddPluginDescriptors(
            IList<TypeDefinition> types,
            string hash,
            List<ModDescriptor> plugins)
        {
            for (int typeIndex = 0; typeIndex < types.Count; typeIndex++)
            {
                TypeDefinition type = types[typeIndex];
                for (int attributeIndex = 0;
                     attributeIndex < type.CustomAttributes.Count;
                     attributeIndex++)
                {
                    CustomAttribute attribute = type.CustomAttributes[attributeIndex];
                    if (!string.Equals(
                            attribute.AttributeType.FullName,
                            typeof(BepInPlugin).FullName,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (attribute.ConstructorArguments.Count < 3)
                    {
                        throw new InvalidDataException(
                            $"BepInPlugin metadata is incomplete in '{type.FullName}'.");
                    }

                    string guid = attribute.ConstructorArguments[0].Value as string;
                    string displayName = attribute.ConstructorArguments[1].Value as string;
                    string version = attribute.ConstructorArguments[2].Value as string;
                    ModDescriptor descriptor = new ModDescriptor(
                        guid, version, hash, displayName);
                    if (!descriptor.IsValid())
                    {
                        throw new InvalidDataException(
                            $"BepInPlugin metadata is invalid in '{type.FullName}'.");
                    }

                    plugins.Add(descriptor);
                }

                if (type.NestedTypes.Count > 0)
                {
                    AddPluginDescriptors(type.NestedTypes, hash, plugins);
                }
            }
        }

        private static string CreateLibraryGuid(string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                throw new InvalidDataException("A library DLL has no assembly name.");
            }

            return $"ModSync.Library.{assemblyName}";
        }
    }
}
