using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ModSync
{
    internal sealed class ThunderstorePackageSummary
    {
        internal ThunderstorePackageSummary(string name, string version)
        {
            Name = name;
            Version = version;
        }

        internal string Name { get; }
        internal string Version { get; }
    }

    internal static class ThunderstorePackageInstaller
    {
        private const int RequestTimeoutMilliseconds = 30000;
        private const int MaximumMetadataBytes = 256 * 1024;
        private const int MaximumArchiveBytes = 400 * 1024 * 1024;
        private const long MaximumExtractedBytes = 1024L * 1024 * 1024;
        private const int MaximumArchiveEntries = 10000;
        private const int MaximumResolvedPackages = 64;
        private static readonly Regex PackageComponentPattern = new Regex(
            @"^[A-Za-z0-9_.-]+$",
            RegexOptions.CultureInvariant);

        internal static InstallPlan PrepareInstall(
            string pluginDirectory,
            ThunderstoreSyncManifest manifest,
            CancellationToken cancellationToken)
        {
            string profileRoot = GetProfileRoot(pluginDirectory);
            string modsFile = Path.Combine(profileRoot, "mods.yml");
            if (!File.Exists(modsFile))
            {
                throw new InvalidOperationException(
                    Loc.T("installer.profile_mods_yml_missing"));
            }

            Dictionary<string, ModPackagePolicy> available =
                manifest.AvailablePackages.ToDictionary(
                    package => package.FullName,
                    StringComparer.OrdinalIgnoreCase);
            List<ModPackagePolicy> requested = ValidateRequestedPackages(
                manifest, available);
            List<ResolvedPackage> packages = ResolvePackages(
                requested, cancellationToken);
            List<ThunderstorePackageSummary> preview = packages
                .Select(package => new ThunderstorePackageSummary(
                    package.Version.PackageName,
                    package.Version.VersionNumber))
                .ToList();
            return new InstallPlan(profileRoot, modsFile, manifest, packages, preview);
        }

        internal static void Install(
            string pluginDirectory,
            ThunderstoreSyncManifest manifest,
            CancellationToken cancellationToken)
        {
            Install(PrepareInstall(pluginDirectory, manifest, cancellationToken),
                cancellationToken);
        }

        internal static void Install(
            InstallPlan plan,
            CancellationToken cancellationToken)
        {
            string profileRoot = plan.ProfileRoot;
            string modsFile = plan.ModsFile;
            IReadOnlyList<ResolvedPackage> packages = plan.Packages;
            string temporaryRoot = Path.Combine(
                Path.GetTempPath(), "ModSync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                for (int index = 0; index < packages.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PreparePackage(
                        packages[index],
                        temporaryRoot,
                        cancellationToken);
                }

                ApplyPackages(
                    profileRoot,
                    modsFile,
                    packages,
                    plan.Manifest.ApprovedMods,
                    plan.Manifest.DisallowedGuids,
                    temporaryRoot,
                    cancellationToken);
            }
            finally
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, true);
                }
            }
        }

        private static string GetProfileRoot(string pluginDirectory)
        {
            if (string.IsNullOrWhiteSpace(pluginDirectory))
            {
                throw new InvalidOperationException(
                    Loc.T("installer.plugins_path_not_detected"));
            }

            string plugins = Path.GetFullPath(pluginDirectory);
            DirectoryInfo pluginsDirectory = new DirectoryInfo(plugins);
            DirectoryInfo bepinexDirectory = pluginsDirectory.Parent;
            DirectoryInfo profileDirectory = bepinexDirectory?.Parent;
            if (!string.Equals(
                    pluginsDirectory.Name, "plugins", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    bepinexDirectory?.Name, "BepInEx", StringComparison.OrdinalIgnoreCase) ||
                profileDirectory == null ||
                !File.Exists(Path.Combine(profileDirectory.FullName, "mods.yml")))
            {
                throw new InvalidOperationException(
                    Loc.T("installer.profile_not_identified"));
            }

            return profileDirectory.FullName;
        }

        private static List<ModPackagePolicy> ValidateRequestedPackages(
            ThunderstoreSyncManifest manifest,
            Dictionary<string, ModPackagePolicy> available)
        {
            if (manifest.InstallPackages.Count > 32)
            {
                throw new InvalidOperationException(
                    Loc.T("installer.too_many_packages"));
            }

            List<ModPackagePolicy> requested = new List<ModPackagePolicy>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModPackagePolicy package in manifest.InstallPackages)
            {
                if (!available.TryGetValue(package.FullName, out ModPackagePolicy approved) ||
                    !PackagePoliciesMatch(package, approved) ||
                    !names.Add(package.FullName))
                {
                    throw new InvalidOperationException(
                        Loc.T("installer.package_not_authorized"));
                }

                requested.Add(approved);
            }

            return requested;
        }

        private static bool PackagePoliciesMatch(
            ModPackagePolicy left,
            ModPackagePolicy right)
        {
            if (left.Required != right.Required ||
                left.Mods.Count != right.Mods.Count)
            {
                return false;
            }

            Dictionary<string, ModDescriptor> rightMods =
                right.Mods.ToDictionary(mod => mod.Guid, StringComparer.Ordinal);
            foreach (ModDescriptor mod in left.Mods)
            {
                if (!rightMods.TryGetValue(mod.Guid, out ModDescriptor approved) ||
                    !string.Equals(mod.Version, approved.Version, StringComparison.Ordinal) ||
                    !string.Equals(
                        mod.Sha256, approved.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<ResolvedPackage> ResolvePackages(
            IReadOnlyList<ModPackagePolicy> requested,
            CancellationToken cancellationToken)
        {
            List<ResolvedPackage> resolved = new List<ResolvedPackage>();
            for (int index = 0; index < requested.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (resolved.Count >= MaximumResolvedPackages)
                {
                    throw new InvalidOperationException(
                        Loc.T("installer.max_packages_exceeded",
                            MaximumResolvedPackages));
                }

                ModPackagePolicy policy = requested[index];
                ThunderstorePackageVersion version =
                    LoadPackageVersion(policy.FullName, cancellationToken);
                resolved.Add(new ResolvedPackage(policy, version));
            }

            return resolved;
        }

        private static ThunderstorePackageVersion LoadPackageVersion(
            string fullName,
            CancellationToken cancellationToken)
        {
            if (!ThunderstorePackageIdentity.TryParse(
                    fullName, out string owner, out string package, out string version))
            {
                throw new InvalidDataException(
                    Loc.T("installer.invalid_identifier", fullName));
            }

            string metadataUrl = ThunderstorePackageIdentity.VersionMetadataUrl(
                owner, package, version);
            ThunderstorePackageVersion metadata = Deserialize<ThunderstorePackageVersion>(
                DownloadBytes(metadataUrl, MaximumMetadataBytes, cancellationToken));
            if (!ThunderstorePackageIdentity.MatchesVersion(
                    fullName,
                    owner,
                    package,
                    version,
                    metadata.Namespace,
                    metadata.Name,
                    metadata.VersionNumber,
                    metadata.FullName))
            {
                throw new InvalidDataException(
                    Loc.T("installer.metadata_mismatch",
                        fullName));
            }

            if (!IsSafePackageComponent(metadata.Namespace) ||
                !IsSafePackageComponent(metadata.Name) ||
                !ThunderstorePackageIdentity.HasVersionSuffix(metadata.FullName) ||
                string.IsNullOrWhiteSpace(metadata.DownloadUrl) ||
                metadata.FileSize < 0)
            {
                throw new InvalidDataException(
                    Loc.T("installer.metadata_invalid", fullName));
            }

            metadata.Owner = metadata.Namespace;
            metadata.PackageName = metadata.Name;
            metadata.PackageUrl = metadata.WebsiteUrl ??
                $"https://thunderstore.io/c/valheim/p/{metadata.Namespace}/{metadata.Name}/";
            metadata.Dependencies = metadata.Dependencies ?? Array.Empty<string>();
            return metadata;
        }

        private static bool IsSafePackageComponent(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value != "." &&
                   value != ".." &&
                   PackageComponentPattern.IsMatch(value);
        }

        private static bool IsThunderstoreHost(string host)
        {
            return string.Equals(host, "thunderstore.io", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".thunderstore.io", StringComparison.OrdinalIgnoreCase);
        }

        private static void PreparePackage(
            ResolvedPackage package,
            string temporaryRoot,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThunderstorePackageVersion version = package.Version;
            Uri downloadUri;
            if (!Uri.TryCreate(version.DownloadUrl, UriKind.Absolute, out downloadUri) ||
                !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !IsThunderstoreHost(downloadUri.Host))
            {
                throw new InvalidDataException(
                    Loc.T("installer.unsafe_download_url",
                        version.FullName));
            }

            if (version.FileSize > MaximumArchiveBytes)
            {
                throw new InvalidDataException(
                    Loc.T("installer.package_too_large", version.FullName));
            }

            byte[] archive = DownloadBytes(
                version.DownloadUrl, MaximumArchiveBytes, cancellationToken);
            if (version.FileSize > 0 && archive.Length != version.FileSize)
            {
                throw new InvalidDataException(
                    Loc.T("installer.package_size_mismatch",
                        version.FullName));
            }

            string stagePath = Path.Combine(
                temporaryRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagePath);
            ExtractArchive(archive, stagePath, cancellationToken);
            VerifyPackage(package, stagePath);
            package.StagePath = stagePath;
        }

        private static void ExtractArchive(
            byte[] archive,
            string destination,
            CancellationToken cancellationToken)
        {
            using (MemoryStream stream = new MemoryStream(archive, false))
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                if (zip.Entries.Count == 0 || zip.Entries.Count > MaximumArchiveEntries)
                {
                    throw new InvalidDataException(
                        Loc.T("installer.zip_empty_or_too_many_files"));
                }

                string prefix = FindManifestPrefix(zip);
                long extractedBytes = 0;
                HashSet<string> paths = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string normalized = ArchivePath.Normalize(entry.FullName);
                    if (normalized.Length == 0 ||
                        (prefix.Length > 0 &&
                         !normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (prefix.Length > 0)
                    {
                        normalized = normalized.Substring(prefix.Length);
                    }

                    normalized = ArchivePath.Normalize(normalized);
                    if (normalized.Length == 0)
                    {
                        continue;
                    }

                    string fullPath = Path.GetFullPath(
                        Path.Combine(destination, normalized.Replace('/', Path.DirectorySeparatorChar)));
                    string destinationPrefix = Path.GetFullPath(destination) +
                                               Path.DirectorySeparatorChar;
                    if (!fullPath.StartsWith(destinationPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            Loc.T("installer.zip_path_outside"));
                    }

                    if (!paths.Add(normalized))
                    {
                        throw new InvalidDataException(
                            Loc.T("installer.zip_duplicate_file", normalized));
                    }

                    if (normalized.EndsWith("/", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(fullPath);
                        continue;
                    }

                    extractedBytes += entry.Length;
                    if (entry.Length < 0 || extractedBytes > MaximumExtractedBytes)
                    {
                        throw new InvalidDataException(
                            Loc.T("installer.zip_content_too_large"));
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(
                        fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        private static string FindManifestPrefix(ZipArchive archive)
        {
            List<string> candidates = new List<string>();
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string path = ArchivePath.Normalize(entry.FullName);
                if (path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(path.Substring(
                        0, path.Length - "manifest.json".Length));
                }
            }

            if (candidates.Count != 1)
            {
                throw new InvalidDataException(
                    Loc.T("installer.zip_manifest_missing_or_multiple"));
            }

            return candidates[0];
        }

        private static void VerifyPackage(ResolvedPackage package, string stagePath)
        {
            string manifestPath = Path.Combine(stagePath, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                throw new InvalidDataException(
                    Loc.T("installer.zip_manifest_not_at_root",
                        package.Version.FullName));
            }

            ThunderstorePackageManifest manifest =
                Deserialize<ThunderstorePackageManifest>(File.ReadAllBytes(manifestPath));
            if (!string.Equals(
                    manifest.Name, package.Version.PackageName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    manifest.VersionNumber, package.Version.VersionNumber, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    Loc.T("installer.zip_metadata_mismatch", package.Version.FullName));
            }

            List<ModDescriptor> actual = new List<ModDescriptor>();
            string[] dllPaths = Directory.GetFiles(
                stagePath, "*.dll", SearchOption.AllDirectories);
            for (int index = 0; index < dllPaths.Length; index++)
            {
                string hash = PluginInventory.Sha256File(dllPaths[index]);
                actual.AddRange(ModPolicyLoader.ReadDescriptors(dllPaths[index], hash));
            }

            ComparePackageMods(package.Policy, actual);
        }

        private static void ComparePackageMods(
            ModPackagePolicy expected,
            IReadOnlyList<ModDescriptor> actual)
        {
            if (expected.Mods.Count != actual.Count)
            {
                throw new InvalidDataException(
                    Loc.T("installer.dlls_policy_mismatch",
                        expected.FullName));
            }

            Dictionary<string, ModDescriptor> byGuid =
                new Dictionary<string, ModDescriptor>(StringComparer.Ordinal);
            for (int index = 0; index < actual.Count; index++)
            {
                if (!byGuid.TryAdd(actual[index].Guid, actual[index]))
                {
                    throw new InvalidDataException(
                        Loc.T("installer.duplicate_guid",
                        expected.FullName, actual[index].Guid));
                }
            }

            for (int index = 0; index < expected.Mods.Count; index++)
            {
                ModDescriptor expectedMod = expected.Mods[index];
                if (!byGuid.TryGetValue(expectedMod.Guid, out ModDescriptor actualMod) ||
                    !string.Equals(
                        expectedMod.Version, actualMod.Version, StringComparison.Ordinal) ||
                    !string.Equals(
                        expectedMod.Sha256, actualMod.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        Loc.T("installer.mod_approval_mismatch",
                            expectedMod.Guid, expected.FullName));
                }
            }
        }

        private static void ApplyPackages(
            string profileRoot,
            string modsFile,
            IReadOnlyList<ResolvedPackage> packages,
            IReadOnlyList<ModDescriptor> approvedMods,
            IReadOnlyList<string> disallowedGuids,
            string temporaryRoot,
            CancellationToken cancellationToken)
        {
            string safeModsFile = GetSafeProfilePath(
                profileRoot, Path.GetRelativePath(profileRoot, modsFile));
            string cacheRoot = GetCacheRoot(profileRoot);
            string stateRoot = Path.Combine(profileRoot, "_state");
            Dictionary<string, string> filesToInstall =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> stateFiles =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<ResolvedPackage> packagesWithFiles = new List<ResolvedPackage>();
            HashSet<string> installedPackageNames =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            DisallowedModCleanup cleanup = FindDisallowedMods(
                profileRoot,
                Path.Combine(profileRoot, "BepInEx", "plugins"),
                approvedMods,
                disallowedGuids,
                cancellationToken);

            foreach (ResolvedPackage package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!installedPackageNames.Add(package.Version.PackageName))
                {
                    throw new InvalidOperationException(
                        Loc.T("installer.duplicate_package_name",
                            package.Version.PackageName));
                }

                string packageIdentity =
                    package.Version.Owner + "-" + package.Version.PackageName;
                string cacheDirectory = Path.Combine(
                    cacheRoot,
                    packageIdentity,
                    package.Version.VersionNumber);
                CopyDirectory(package.StagePath, cacheDirectory);

                List<KeyValuePair<string, string>> packageFiles =
                    FindInstallFiles(
                        package.StagePath,
                        cacheDirectory,
                        package.Version.PackageName,
                        out List<KeyValuePair<string, string>> trackedFiles);
                if (packageFiles.Count == 0)
                {
                    throw new InvalidDataException(
                        Loc.T("installer.package_no_installable_files", package.Version.FullName));
                }

                foreach (KeyValuePair<string, string> file in packageFiles)
                {
                    if (!filesToInstall.TryAdd(file.Key, file.Value))
                    {
                        throw new InvalidDataException(
                            Loc.T("installer.file_conflict", file.Key));
                    }
                }

                string statePath = Path.Combine(
                    stateRoot, package.Version.PackageName + "-state.yml");
                string legacyStatePath = Path.Combine(
                    stateRoot, packageIdentity + "-state.yml");
                if (trackedFiles.Count > 0)
                {
                    stateFiles[statePath] = CreateStateFile(
                        package.Version.PackageName, trackedFiles);
                }

                if (!string.Equals(
                        statePath, legacyStatePath, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(legacyStatePath))
                {
                    stateFiles[legacyStatePath] = null;
                }

                package.StatePath = statePath;
                package.LegacyStatePath = legacyStatePath;
                package.PackageName = package.Version.PackageName;
                package.InstallFiles = packageFiles;
                package.TrackedInstallFiles = trackedFiles;
                packagesWithFiles.Add(package);
            }

            foreach (string path in cleanup.FilesToDelete)
            {
                if (filesToInstall.ContainsKey(path))
                {
                    throw new InvalidOperationException(
                        Loc.T("installer.cleanup_conflict", path));
                }
            }

            foreach (KeyValuePair<string, string> state in cleanup.StateFiles)
            {
                stateFiles[state.Key] = state.Value;
            }

            string updatedMods = UpdateProfileModList(
                File.ReadAllText(safeModsFile),
                packagesWithFiles,
                cleanup.RemovedPackageNames);
            ValidateExistingFiles(profileRoot, packagesWithFiles, filesToInstall);

            Dictionary<string, string> backups =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string relativePath in cleanup.FilesToDelete)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string destination = GetSafeProfilePath(profileRoot, relativePath);
                    Snapshot(destination, temporaryRoot, backups);
                    if (File.Exists(destination))
                    {
                        File.Delete(destination);
                    }
                }

                foreach (string directory in cleanup.DirectoriesToDelete)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, true);
                    }
                }

                foreach (string relativePath in filesToInstall.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string destination = GetSafeProfilePath(profileRoot, relativePath);
                    Snapshot(destination, temporaryRoot, backups);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(filesToInstall[relativePath], destination, true);
                }

                foreach (ResolvedPackage package in packagesWithFiles)
                {
                    foreach (string stalePath in package.StaleFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string destination = GetSafeProfilePath(profileRoot, stalePath);
                        Snapshot(destination, temporaryRoot, backups);
                        if (File.Exists(destination))
                        {
                            File.Delete(destination);
                        }
                    }
                }

                Snapshot(safeModsFile, temporaryRoot, backups);
                File.WriteAllText(safeModsFile, updatedMods, new UTF8Encoding(false));

                foreach (KeyValuePair<string, string> state in stateFiles)
                {
                    string safeStatePath = GetSafeProfilePath(
                        profileRoot, Path.GetRelativePath(profileRoot, state.Key));
                    Snapshot(safeStatePath, temporaryRoot, backups);
                    if (state.Value == null)
                    {
                        File.Delete(safeStatePath);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(safeStatePath));
                        File.WriteAllText(
                            safeStatePath, state.Value, new UTF8Encoding(false));
                    }
                }
            }
            catch
            {
                RestoreSnapshots(backups);
                throw;
            }
        }

        private static DisallowedModCleanup FindDisallowedMods(
            string profileRoot,
            string pluginRoot,
            IReadOnlyList<ModDescriptor> approvedMods,
            IReadOnlyList<string> disallowedGuids,
            CancellationToken cancellationToken)
        {
            DisallowedModCleanup cleanup = new DisallowedModCleanup();
            if (disallowedGuids.Count == 0)
            {
                return cleanup;
            }

            HashSet<string> approved = new HashSet<string>(
                approvedMods.Select(mod => mod.Guid),
                StringComparer.Ordinal);
            HashSet<string> disallowed = new HashSet<string>(
                disallowedGuids, StringComparer.Ordinal);
            if (disallowed.Any(approved.Contains))
            {
                throw new InvalidDataException(
                    Loc.T("installer.removal_of_approved_mod"));
            }

            if (!Directory.Exists(pluginRoot))
            {
                throw new DirectoryNotFoundException(
                    Loc.T("installer.plugins_folder_missing", pluginRoot));
            }

            GetSafeProfilePath(profileRoot, Path.GetRelativePath(profileRoot, pluginRoot));
            HashSet<string> foundDisallowed = new HashSet<string>(StringComparer.Ordinal);
            string stateRoot = Path.Combine(profileRoot, "_state");
            GetSafeProfilePath(profileRoot, Path.GetRelativePath(profileRoot, stateRoot));
            string[] packageDirectories = Directory.GetDirectories(
                pluginRoot, "*", SearchOption.TopDirectoryOnly);
            for (int directoryIndex = 0;
                 directoryIndex < packageDirectories.Length;
                 directoryIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string packageDirectory = packageDirectories[directoryIndex];
                if ((File.GetAttributes(packageDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        Loc.T("installer.cleanup_symlink_folder"));
                }

                string packageName = Path.GetFileName(packageDirectory);
                List<string> packageFiles = EnumerateFilesNoReparse(packageDirectory);
                List<string> dllsToRemove = new List<string>();
                bool hasApprovedMod = false;
                for (int fileIndex = 0; fileIndex < packageFiles.Count; fileIndex++)
                {
                    string dllPath = packageFiles[fileIndex];
                    if (!dllPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    IReadOnlyList<ModDescriptor> descriptors =
                        ModPolicyLoader.ReadDescriptors(
                            dllPath, PluginInventory.Sha256File(dllPath));
                    bool fileHasDisallowed = false;
                    bool fileHasApproved = false;
                    for (int descriptorIndex = 0;
                         descriptorIndex < descriptors.Count;
                         descriptorIndex++)
                    {
                        ModDescriptor descriptor = descriptors[descriptorIndex];
                        if (approved.Contains(descriptor.Guid))
                        {
                            fileHasApproved = true;
                            hasApprovedMod = true;
                        }

                        if (disallowed.Contains(descriptor.Guid))
                        {
                            fileHasDisallowed = true;
                            foundDisallowed.Add(descriptor.Guid);
                        }
                    }

                    if (fileHasDisallowed && fileHasApproved)
                    {
                        throw new InvalidOperationException(
                            Loc.T("installer.cleanup_mixed_plugins", dllPath));
                    }

                    if (fileHasDisallowed)
                    {
                        dllsToRemove.Add(dllPath);
                    }
                }

                if (dllsToRemove.Count == 0)
                {
                    continue;
                }

                List<string> packageStatePaths =
                    GetPackageStatePaths(stateRoot, packageName);
                if (hasApprovedMod)
                {
                    HashSet<string> removedRelativePaths = new HashSet<string>(
                        dllsToRemove.Select(path => ToProfileRelativePath(profileRoot, path)),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (string statePath in packageStatePaths)
                    {
                        Dictionary<string, string> trackedFiles =
                            ReadStateFiles(statePath, profileRoot);
                        List<KeyValuePair<string, string>> retained =
                            new List<KeyValuePair<string, string>>();
                        foreach (KeyValuePair<string, string> file in trackedFiles)
                        {
                            if (removedRelativePaths.Contains(file.Value))
                            {
                                cleanup.FilesToDelete.Add(file.Value);
                            }
                            else
                            {
                                retained.Add(new KeyValuePair<string, string>(
                                    file.Value, file.Key));
                            }
                        }

                        cleanup.StateFiles[statePath] =
                            CreateStateFile(packageName, retained);
                    }
                }
                else
                {
                    foreach (string file in packageFiles)
                    {
                        cleanup.FilesToDelete.Add(
                            ToProfileRelativePath(profileRoot, file));
                    }

                    foreach (string statePath in packageStatePaths)
                    {
                        Dictionary<string, string> trackedFiles =
                            ReadStateFiles(statePath, profileRoot);
                        foreach (KeyValuePair<string, string> trackedFile in trackedFiles)
                        {
                            cleanup.FilesToDelete.Add(trackedFile.Value);
                        }

                        cleanup.StateFiles[statePath] = null;
                    }

                    cleanup.DirectoriesToDelete.Add(packageDirectory);
                    cleanup.RemovedPackageNames.Add(packageName);
                }
            }

            string[] rootDlls = Directory.GetFiles(
                pluginRoot, "*.dll", SearchOption.TopDirectoryOnly);
            for (int index = 0; index < rootDlls.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(rootDlls[index]) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        Loc.T("installer.cleanup_symlink_file"));
                }

                IReadOnlyList<ModDescriptor> descriptors =
                    ModPolicyLoader.ReadDescriptors(
                        rootDlls[index], PluginInventory.Sha256File(rootDlls[index]));
                bool fileHasDisallowed = descriptors.Any(
                    descriptor => disallowed.Contains(descriptor.Guid));
                bool fileHasApproved = descriptors.Any(
                    descriptor => approved.Contains(descriptor.Guid));
                if (fileHasDisallowed && fileHasApproved)
                {
                    throw new InvalidOperationException(
                        Loc.T("installer.cleanup_mixed_plugins", rootDlls[index]));
                }

                if (fileHasDisallowed)
                {
                    foreach (ModDescriptor descriptor in descriptors)
                    {
                        if (disallowed.Contains(descriptor.Guid))
                        {
                            foundDisallowed.Add(descriptor.Guid);
                        }
                    }

                    cleanup.FilesToDelete.Add(
                        ToProfileRelativePath(profileRoot, rootDlls[index]));
                }
            }

            if (!disallowed.SetEquals(foundDisallowed))
            {
                throw new InvalidOperationException(
                    Loc.T("installer.cleanup_not_all_located"));
            }

            return cleanup;
        }

        private static List<string> GetPackageStatePaths(
            string stateRoot,
            string packageName)
        {
            List<string> result = new List<string>();
            if (!Directory.Exists(stateRoot))
            {
                return result;
            }

            string currentName = packageName + "-state.yml";
            string legacySuffix = "-" + currentName;
            string[] stateFiles = Directory.GetFiles(
                stateRoot, "*-state.yml", SearchOption.TopDirectoryOnly);
            for (int index = 0; index < stateFiles.Length; index++)
            {
                string fileName = Path.GetFileName(stateFiles[index]);
                if (string.Equals(
                        fileName, currentName, StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(
                        legacySuffix, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(stateFiles[index]);
                }
            }

            return result;
        }

        private static List<string> EnumerateFilesNoReparse(string directory)
        {
            List<string> files = new List<string>();
            Stack<string> pending = new Stack<string>();
            pending.Push(directory);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        Loc.T("installer.cleanup_symlink_folder"));
                }

                string[] entries = Directory.GetFileSystemEntries(current);
                for (int index = 0; index < entries.Length; index++)
                {
                    FileAttributes attributes = File.GetAttributes(entries[index]);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidDataException(
                            Loc.T("installer.cleanup_symlink_folder"));
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entries[index]);
                    }
                    else
                    {
                        files.Add(entries[index]);
                    }
                }
            }

            return files;
        }

        private static string ToProfileRelativePath(string profileRoot, string path)
        {
            return Path.GetRelativePath(profileRoot, path)
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static void ValidateExistingFiles(
            string profileRoot,
            IReadOnlyList<ResolvedPackage> packages,
            Dictionary<string, string> filesToInstall)
        {
            HashSet<string> expectedPaths =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in filesToInstall.Keys)
            {
                expectedPaths.Add(path);
            }

            foreach (ResolvedPackage package in packages)
            {
                package.StaleFiles = new List<string>();
                string statePath = File.Exists(package.StatePath)
                    ? package.StatePath
                    : package.LegacyStatePath;
                Dictionary<string, string> previousFiles =
                    ReadStateFiles(statePath, profileRoot);
                foreach (KeyValuePair<string, string> oldFile in previousFiles)
                {
                    string relative = oldFile.Value;
                    string destination = GetSafeProfilePath(profileRoot, relative);
                    if (!File.Exists(destination))
                    {
                        continue;
                    }

                    if (!File.Exists(oldFile.Key) ||
                        !string.Equals(
                            PluginInventory.Sha256File(oldFile.Key),
                            PluginInventory.Sha256File(destination),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            Loc.T("installer.file_modified_locally", relative));
                    }

                    if (!expectedPaths.Contains(relative))
                    {
                        package.StaleFiles.Add(relative);
                    }
                }

                foreach (KeyValuePair<string, string> newFile in package.InstallFiles)
                {
                    string destination = GetSafeProfilePath(profileRoot, newFile.Key);
                    if (!File.Exists(destination) ||
                        previousFiles.Values.Contains(
                            newFile.Key, StringComparer.OrdinalIgnoreCase) ||
                        !package.TrackedInstallFiles.Any(
                            tracked => string.Equals(
                                tracked.Key,
                                newFile.Key,
                                StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        Loc.T("installer.file_untracked_existing", newFile.Key));
                }
            }
        }

        private static Dictionary<string, string> ReadStateFiles(
            string statePath,
            string profileRoot)
        {
            string safeStatePath = GetSafeProfilePath(
                profileRoot, Path.GetRelativePath(profileRoot, statePath));
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(safeStatePath))
            {
                return result;
            }

            string[] lines = File.ReadAllLines(safeStatePath);
            bool inFiles = false;
            for (int index = 0; index < lines.Length; index++)
            {
                if (string.Equals(lines[index].Trim(), "files:", StringComparison.Ordinal))
                {
                    inFiles = true;
                    continue;
                }

                if (!inFiles || !lines[index].StartsWith("  - - ", StringComparison.Ordinal))
                {
                    continue;
                }

                string source = ParseYamlScalar(lines[index].Substring(6).Trim());
                if (index + 1 >= lines.Length ||
                    !lines[index + 1].StartsWith("    - ", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        Loc.T("installer.state_unreadable", safeStatePath));
                }

                string destination = ParseYamlScalar(lines[++index].Substring(6).Trim());
                GetSafeProfilePath(profileRoot, destination);
                result[NormalizeProfilePath(destination)] = source;
            }

            if (!inFiles && !string.IsNullOrWhiteSpace(File.ReadAllText(statePath)))
            {
                throw new InvalidDataException(
                    Loc.T("installer.state_invalid_file_list",
                    safeStatePath));
            }

            return result.ToDictionary(
                entry => entry.Value,
                entry => entry.Key,
                StringComparer.OrdinalIgnoreCase);
        }

        private static List<KeyValuePair<string, string>> FindInstallFiles(
            string stagePath,
            string cachePath,
            string packageName,
            out List<KeyValuePair<string, string>> trackedFiles)
        {
            List<KeyValuePair<string, string>> result =
                new List<KeyValuePair<string, string>>();
            trackedFiles = new List<KeyValuePair<string, string>>();
            string[] files = Directory.GetFiles(
                stagePath, "*", SearchOption.AllDirectories);
            for (int index = 0; index < files.Length; index++)
            {
                string relativeArchivePath = Path.GetRelativePath(
                    stagePath, files[index]).Replace('\\', '/');
                ThunderstoreInstallPath resolved =
                    ThunderstoreInstallPathResolver.Resolve(
                        packageName, relativeArchivePath);
                string cacheFile = Path.Combine(
                    cachePath,
                    relativeArchivePath.Replace(
                        '/', Path.DirectorySeparatorChar));
                KeyValuePair<string, string> installFile =
                    new KeyValuePair<string, string>(
                        resolved.ProfilePath.Replace(
                            '/', Path.DirectorySeparatorChar),
                        cacheFile);
                result.Add(installFile);
                if (resolved.Tracked)
                {
                    trackedFiles.Add(installFile);
                }
            }

            return result;
        }

        private static string CreateStateFile(
            string packageName,
            IReadOnlyList<KeyValuePair<string, string>> files)
        {
            StringBuilder output = new StringBuilder();
            output.Append("modName: ").AppendLine(QuoteYaml(packageName));
            output.AppendLine("files:");
            for (int index = 0; index < files.Count; index++)
            {
                output.Append("  - - ")
                    .AppendLine(QuoteYaml(files[index].Value));
                output.Append("    - ")
                    .AppendLine(QuoteYaml(files[index].Key.Replace('\\', '/')));
            }

            return output.ToString();
        }

        private static string UpdateProfileModList(
            string original,
            IReadOnlyList<ResolvedPackage> packages,
            IReadOnlyCollection<string> removedPackageNames)
        {
            ReadModList(original, out List<string> prefix, out List<List<string>> blocks);

            HashSet<string> packageIds = new HashSet<string>(
                packages.Select(package =>
                    package.Version.Owner + "-" + package.PackageName),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> legacyPackageNames = new HashSet<string>(
                packages.Select(package =>
                    package.Version.Owner + "-" + package.Version.PackageName),
                StringComparer.OrdinalIgnoreCase);
            blocks.RemoveAll(block =>
            {
                string packageName = ReadPackageName(block);
                string owner = ReadPackageOwner(block);
                string packageId = owner == null
                    ? packageName
                    : owner + "-" + packageName;
                return packageIds.Contains(packageId) ||
                       removedPackageNames.Contains(
                           packageName, StringComparer.OrdinalIgnoreCase) ||
                       legacyPackageNames.Contains(packageName);
            });

            StringBuilder output = new StringBuilder();
            for (int index = 0; index < prefix.Count; index++)
            {
                output.AppendLine(prefix[index]);
            }

            foreach (List<string> block in blocks)
            {
                for (int index = 0; index < block.Count; index++)
                {
                    output.AppendLine(block[index]);
                }
            }

            for (int index = 0; index < packages.Count; index++)
            {
                AppendManifestEntry(output, packages[index]);
            }

            return output.ToString();
        }

        private static string ReadPackageVersion(List<string> block)
        {
            int major = -1;
            int minor = -1;
            int patch = -1;
            for (int index = 0; index < block.Count; index++)
            {
                Match match = Regex.Match(
                    block[index],
                    @"^\s+(?<part>major|minor|patch):\s*(?<value>\d+)\s*$",
                    RegexOptions.CultureInvariant);
                if (!match.Success ||
                    !int.TryParse(match.Groups["value"].Value, out int value))
                {
                    continue;
                }

                switch (match.Groups["part"].Value)
                {
                    case "major":
                        major = value;
                        break;
                    case "minor":
                        minor = value;
                        break;
                    case "patch":
                        patch = value;
                        break;
                }
            }

            return major < 0 || minor < 0 || patch < 0
                ? null
                : $"{major}.{minor}.{patch}";
        }

        private static void ReadModList(
            string original,
            out List<string> prefix,
            out List<List<string>> blocks)
        {
            List<string> lines = original
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .ToList();
            if (string.Equals(original.Trim(), "[]", StringComparison.Ordinal))
            {
                lines.Clear();
            }

            blocks = new List<List<string>>();
            prefix = new List<string>();
            List<string> current = null;
            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index];
                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    current = new List<string> { line };
                    blocks.Add(current);
                }
                else if (current == null)
                {
                    if (!string.IsNullOrWhiteSpace(line) &&
                        !line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            Loc.T("installer.mods_yml_unsupported_format"));
                    }

                    prefix.Add(line);
                }
                else
                {
                    current.Add(line);
                }
            }
        }

        private static string ReadPackageName(List<string> block)
        {
            for (int index = 0; index < block.Count; index++)
            {
                Match match = Regex.Match(
                    block[index],
                    @"^\s*(?:-\s+)?name:\s*(?<value>.*?)\s*$",
                    RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    return ParseYamlScalar(match.Groups["value"].Value);
                }
            }

            return null;
        }

        private static string ReadPackageOwner(List<string> block)
        {
            for (int index = 0; index < block.Count; index++)
            {
                Match match = Regex.Match(
                    block[index],
                    @"^\s*(?:-\s+)?authorName:\s*(?<value>.*?)\s*$",
                    RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    return ParseYamlScalar(match.Groups["value"].Value);
                }
            }

            return null;
        }

        private static void AppendManifestEntry(
            StringBuilder output,
            ResolvedPackage package)
        {
            System.Version version;
            try
            {
                version = new System.Version(package.Version.VersionNumber);
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException(
                    Loc.T("installer.version_not_writable", package.Version.VersionNumber));
            }
            catch (FormatException)
            {
                throw new InvalidDataException(
                    Loc.T("installer.version_not_writable", package.Version.VersionNumber));
            }

            if (version.Build < 0 || version.Revision > 0)
            {
                throw new InvalidDataException(
                    Loc.T("installer.version_not_writable", package.Version.VersionNumber));
            }

            output.Append("- manifestVersion: 1").AppendLine();
            AppendYamlProperty(output, "name", package.PackageName);
            AppendYamlProperty(output, "authorName", package.Version.Owner);
            AppendYamlProperty(
                output, "websiteUrl", package.Version.PackageUrl ??
                    $"https://thunderstore.io/c/valheim/p/{package.Version.Owner}/{package.Version.PackageName}/");
            AppendYamlProperty(output, "displayName", package.Version.PackageName);
            AppendYamlProperty(output, "description", package.Version.Description ?? string.Empty);
            AppendYamlProperty(output, "gameVersion", "0");
            AppendYamlProperty(output, "networkMode", "both");
            AppendYamlProperty(output, "packageType", "other");
            AppendYamlProperty(output, "installMode", "managed");
            output.AppendLine("  loaders: []");
            output.AppendLine("  dependencies:");
            string[] dependencies = package.Version.Dependencies ?? Array.Empty<string>();
            if (dependencies.Length == 0)
            {
                output.AppendLine("    []");
            }
            else
            {
                for (int index = 0; index < dependencies.Length; index++)
                {
                    output.Append("    - ")
                        .AppendLine(QuoteYaml(dependencies[index]));
                }
            }

            output.AppendLine("  incompatibilities: []");
            output.AppendLine("  optionalDependencies: []");
            output.AppendLine("  versionNumber:");
            output.Append("    major: ").AppendLine(version.Major.ToString());
            output.Append("    minor: ").AppendLine(version.Minor.ToString());
            output.Append("    patch: ").AppendLine(version.Build.ToString());
            output.Append("  installedAtTime: ")
                .AppendLine(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            output.AppendLine("  enabled: true");
        }

        private static void AppendYamlProperty(
            StringBuilder output,
            string name,
            string value)
        {
            output.Append("  ")
                .Append(name)
                .Append(": ")
                .AppendLine(QuoteYaml(value));
        }

        private static string QuoteYaml(string value)
        {
            return "\"" + (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t") + "\"";
        }

        private static string ParseYamlScalar(string value)
        {
            if (value.Length >= 2 &&
                value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2)
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\")
                    .Replace("\\n", "\n")
                    .Replace("\\r", "\r")
                    .Replace("\\t", "\t");
            }

            if (value.Length >= 2 &&
                value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            }

            return value;
        }

        private static string GetCacheRoot(string profileRoot)
        {
            DirectoryInfo profile = new DirectoryInfo(profileRoot);
            DirectoryInfo profiles = profile.Parent;
            DirectoryInfo gameRoot = profiles?.Parent;
            if (gameRoot == null ||
                !string.Equals(profiles.Name, "profiles", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    Loc.T("installer.data_folder_not_found"));
            }

            return Path.Combine(gameRoot.FullName, "cache");
        }

        private static string GetSafeProfilePath(string profileRoot, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                Path.IsPathRooted(relativePath) ||
                relativePath.IndexOf(':') >= 0)
            {
                throw new InvalidDataException(
                    Loc.T("installer.state_invalid_profile_path"));
            }

            string root = Path.GetFullPath(profileRoot);
            string normalizedRelative = relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));
            string prefix = root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    Loc.T("installer.state_escapes_profile"));
            }

            string current = root;
            string[] segments = normalizedRelative.Split(Path.DirectorySeparatorChar);
            for (int index = 0; index < segments.Length; index++)
            {
                if (segments[index].Length == 0 || segments[index] == "." ||
                    segments[index] == "..")
                {
                    throw new InvalidDataException(
                        Loc.T("installer.state_invalid_relative_path"));
                }

                current = Path.Combine(current, segments[index]);
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        Loc.T("installer.package_symlink_write"));
                }
            }

            return fullPath;
        }

        private static string NormalizeProfilePath(string relativePath)
        {
            return relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
        }

        private static void Snapshot(
            string path,
            string backupRoot,
            Dictionary<string, string> backups)
        {
            if (backups.ContainsKey(path))
            {
                return;
            }

            if (!File.Exists(path))
            {
                backups.Add(path, null);
                return;
            }

            string backup = Path.Combine(
                backupRoot, "backup-" + backups.Count.ToString());
            File.Copy(path, backup, true);
            backups.Add(path, backup);
        }

        private static void RestoreSnapshots(Dictionary<string, string> backups)
        {
            foreach (KeyValuePair<string, string> backup in backups)
            {
                if (backup.Value == null)
                {
                    if (File.Exists(backup.Key))
                    {
                        File.Delete(backup.Key);
                    }

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(backup.Key));
                File.Copy(backup.Value, backup.Key, true);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            string[] directories = Directory.GetDirectories(
                source, "*", SearchOption.AllDirectories);
            for (int index = 0; index < directories.Length; index++)
            {
                string relative = Path.GetRelativePath(source, directories[index]);
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }

            string[] files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            for (int index = 0; index < files.Length; index++)
            {
                string relative = Path.GetRelativePath(source, files[index]);
                string target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(files[index], target, true);
            }
        }

        private static byte[] DownloadBytes(
            string address,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(address);
            request.Timeout = RequestTimeoutMilliseconds;
            request.ReadWriteTimeout = RequestTimeoutMilliseconds;
            request.UserAgent = "ModSync/0.1.0";
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                if (!string.Equals(
                        response.ResponseUri.Scheme,
                        Uri.UriSchemeHttps,
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsThunderstoreHost(response.ResponseUri.Host) ||
                    response.ContentLength > maximumBytes)
                {
                    throw new InvalidDataException(
                        Loc.T("installer.download_not_https") +
                        " [" + address + " -> " + response.ResponseUri +
                        ", " + response.ContentLength + " bytes]");
                }

                using (Stream input = response.GetResponseStream())
                using (MemoryStream output = new MemoryStream())
                {
                    byte[] buffer = new byte[32768];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (output.Length + read > maximumBytes)
                        {
                            throw new InvalidDataException(
                                Loc.T("installer.download_too_large"));
                        }

                        output.Write(buffer, 0, read);
                    }

                    return output.ToArray();
                }
            }
        }

        private static T Deserialize<T>(byte[] content)
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(
                typeof(T),
                new DataContractJsonSerializerSettings
                {
                    MaxItemsInObjectGraph = 500000
                });
            // El serializador exige UTF-8 sin BOM; se decodifica según la
            // codificación declarada por el BOM (UTF-8/UTF-16) y se recodifica.
            string json;
            using (StreamReader reader = new StreamReader(
                       new MemoryStream(content, false),
                       new UTF8Encoding(false),
                       true))
            {
                json = reader.ReadToEnd();
            }

            using (MemoryStream stream = new MemoryStream(
                       new UTF8Encoding(false).GetBytes(json), false))
            {
                T value = (T)serializer.ReadObject(stream);
                if (value == null)
                {
                    throw new InvalidDataException(
                        Loc.T("installer.metadata_empty_json"));
                }

                return value;
            }
        }

        internal sealed class InstallPlan
        {
            internal InstallPlan(
                string profileRoot,
                string modsFile,
                ThunderstoreSyncManifest manifest,
                List<ResolvedPackage> packages,
                IReadOnlyList<ThunderstorePackageSummary> preview)
            {
                ProfileRoot = profileRoot;
                ModsFile = modsFile;
                Manifest = manifest;
                Packages = packages;
                Preview = preview;
            }

            internal string ProfileRoot { get; }
            internal string ModsFile { get; }
            internal ThunderstoreSyncManifest Manifest { get; }
            internal IReadOnlyList<ThunderstorePackageSummary> Preview { get; }
            internal IReadOnlyList<ResolvedPackage> Packages { get; }
        }

        internal sealed class ResolvedPackage
        {
            internal ResolvedPackage(
                ModPackagePolicy policy,
                ThunderstorePackageVersion version)
            {
                Policy = policy;
                Version = version;
            }

            internal ModPackagePolicy Policy { get; }
            internal ThunderstorePackageVersion Version { get; }
            internal string StagePath { get; set; }
            internal string PackageName { get; set; }
            internal string StatePath { get; set; }
            internal string LegacyStatePath { get; set; }
            internal List<KeyValuePair<string, string>> InstallFiles { get; set; }
            internal List<KeyValuePair<string, string>> TrackedInstallFiles { get; set; }
            internal List<string> StaleFiles { get; set; }
        }

        private sealed class DisallowedModCleanup
        {
            internal HashSet<string> FilesToDelete { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal List<string> DirectoriesToDelete { get; } =
                new List<string>();
            internal HashSet<string> RemovedPackageNames { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal Dictionary<string, string> StateFiles { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        [DataContract]
        internal sealed class ThunderstorePackageVersion
        {
            [DataMember(Name = "namespace")]
            public string Namespace { get; set; }

            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "full_name")]
            public string FullName { get; set; }

            [DataMember(Name = "version_number")]
            public string VersionNumber { get; set; }

            [DataMember(Name = "dependencies")]
            public string[] Dependencies { get; set; }

            [DataMember(Name = "download_url")]
            public string DownloadUrl { get; set; }

            [DataMember(Name = "file_size")]
            public long FileSize { get; set; }

            [DataMember(Name = "description")]
            public string Description { get; set; }

            [DataMember(Name = "website_url")]
            public string WebsiteUrl { get; set; }

            public string Owner { get; set; }
            public string PackageName { get; set; }
            public string PackageUrl { get; set; }
        }

        [DataContract]
        private sealed class ThunderstorePackageManifest
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "version_number")]
            public string VersionNumber { get; set; }

            [DataMember(Name = "dependencies")]
            public string[] Dependencies { get; set; }
        }
    }
}
