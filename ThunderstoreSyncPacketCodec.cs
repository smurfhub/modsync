using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ModSync
{
    internal sealed class ThunderstoreSyncManifest
    {
        internal ThunderstoreSyncManifest(
            string nonce,
            IReadOnlyList<ModPackagePolicy> installPackages,
            IReadOnlyList<ModPackagePolicy> availablePackages,
            IReadOnlyList<ModDescriptor> approvedMods,
            IReadOnlyList<string> disallowedGuids)
        {
            Nonce = nonce;
            InstallPackages = installPackages;
            AvailablePackages = availablePackages;
            ApprovedMods = approvedMods;
            DisallowedGuids = disallowedGuids;
        }

        internal string Nonce { get; }
        internal IReadOnlyList<ModPackagePolicy> InstallPackages { get; }
        internal IReadOnlyList<ModPackagePolicy> AvailablePackages { get; }
        internal IReadOnlyList<ModDescriptor> ApprovedMods { get; }
        internal IReadOnlyList<string> DisallowedGuids { get; }
    }

    internal static class ThunderstorePackageIdentity
    {
        private static readonly Regex PackageFolderPattern = new Regex(
            @"^(?<owner>[A-Za-z0-9_.]+)-(?<package>[A-Za-z0-9_.-]+)-(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?)$",
            RegexOptions.CultureInvariant);

        internal static bool HasVersionSuffix(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   PackageFolderPattern.IsMatch(value);
        }

        internal static bool TryParse(
            string value,
            out string owner,
            out string package,
            out string version)
        {
            Match match = string.IsNullOrWhiteSpace(value)
                ? Match.Empty
                : PackageFolderPattern.Match(value);
            owner = match.Success ? match.Groups["owner"].Value : null;
            package = match.Success ? match.Groups["package"].Value : null;
            version = match.Success ? match.Groups["version"].Value : null;
            return match.Success;
        }

        internal static string VersionMetadataUrl(
            string owner,
            string package,
            string version)
        {
            return "https://thunderstore.io/api/experimental/package/" +
                   Uri.EscapeDataString(owner) + "/" +
                   Uri.EscapeDataString(package) + "/" +
                   Uri.EscapeDataString(version) + "/";
        }

        internal static bool MatchesVersion(
            string fullName,
            string owner,
            string package,
            string version,
            string returnedOwner,
            string returnedPackage,
            string returnedVersion,
            string returnedFullName)
        {
            return string.Equals(owner, returnedOwner, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(package, returnedPackage, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(version, returnedVersion, StringComparison.Ordinal) &&
                   string.Equals(fullName, returnedFullName, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class ThunderstoreSyncPacketCodec
    {
        private const int Magic = 0x4D535450;
        private const int ProtocolVersion = 2;
        private const int MaximumPayloadBytes = 256 * 1024;
        private const int MaximumPackages = 64;
        private const int MaximumMods = ModPolicyValidator.MaximumMods;
        private const int MaximumEncodedLength = ((MaximumPayloadBytes + 2) / 3) * 4;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static string EncodeToBase64(ThunderstoreSyncManifest manifest)
        {
            if (manifest == null ||
                !InventoryPacketCodec.IsNonce(manifest.Nonce) ||
                manifest.InstallPackages == null ||
                manifest.AvailablePackages == null ||
                manifest.ApprovedMods == null ||
                manifest.DisallowedGuids == null ||
                (manifest.InstallPackages.Count == 0 &&
                 manifest.DisallowedGuids.Count == 0) ||
                manifest.InstallPackages.Count > MaximumPackages ||
                manifest.AvailablePackages.Count > MaximumPackages ||
                manifest.ApprovedMods.Count > MaximumMods ||
                manifest.DisallowedGuids.Count > MaximumMods)
            {
                throw new InvalidDataException("Invalid synchronization manifest.");
            }

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, StrictUtf8, true))
            {
                writer.Write(Magic);
                writer.Write(ProtocolVersion);
                WriteText(writer, manifest.Nonce, 64);
                WritePackages(writer, manifest.InstallPackages);
                WritePackages(writer, manifest.AvailablePackages);
                WriteApprovedMods(writer, manifest.ApprovedMods);
                WriteDisallowedGuids(writer, manifest.DisallowedGuids, manifest.ApprovedMods);
                writer.Flush();

                if (stream.Length > MaximumPayloadBytes)
                {
                    throw new InvalidDataException(
                        "The synchronization manifest exceeds the allowed size.");
                }

                return Convert.ToBase64String(stream.ToArray());
            }
        }

        internal static ThunderstoreSyncManifest DecodeFromBase64(string encoded)
        {
            if (string.IsNullOrEmpty(encoded) || encoded.Length > MaximumEncodedLength)
            {
                throw new InvalidDataException(
                    "The manifest is empty or exceeds the allowed size.");
            }

            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(encoded);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "The manifest is not valid Base64.", exception);
            }

            if (payload.Length > MaximumPayloadBytes)
            {
                throw new InvalidDataException(
                    "The manifest exceeds the allowed size.");
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(payload, false))
                using (BinaryReader reader = new BinaryReader(stream, StrictUtf8, true))
                {
                    if (reader.ReadInt32() != Magic ||
                        reader.ReadInt32() != ProtocolVersion)
                    {
                        throw new InvalidDataException(
                            "The manifest version or signature is not valid.");
                    }

                    string nonce = ReadText(reader, 64);
                    if (!InventoryPacketCodec.IsNonce(nonce))
                    {
                        throw new InvalidDataException("The manifest nonce is not valid.");
                    }

                    List<ModPackagePolicy> installPackages = ReadPackages(reader);
                    List<ModPackagePolicy> availablePackages = ReadPackages(reader);
                    List<ModDescriptor> approvedMods = ReadApprovedMods(reader);
                    List<string> disallowedGuids =
                        ReadDisallowedGuids(reader, approvedMods);
                    if (stream.Position != stream.Length ||
                        (installPackages.Count == 0 && disallowedGuids.Count == 0))
                    {
                        throw new InvalidDataException(
                            "The manifest is incomplete or contains leftover data.");
                    }

                    return new ThunderstoreSyncManifest(
                        nonce,
                        installPackages.AsReadOnly(),
                        availablePackages.AsReadOnly(),
                        approvedMods.AsReadOnly(),
                        disallowedGuids.AsReadOnly());
                }
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("The manifest is truncated.", exception);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    "The manifest contains invalid UTF-8 text.", exception);
            }
        }

        private static void WritePackages(
            BinaryWriter writer,
            IReadOnlyList<ModPackagePolicy> packages)
        {
            writer.Write(packages.Count);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> guids = new HashSet<string>(StringComparer.Ordinal);
            int totalMods = 0;
            for (int packageIndex = 0; packageIndex < packages.Count; packageIndex++)
            {
                ModPackagePolicy package = packages[packageIndex];
                if (package == null ||
                    !ThunderstorePackageIdentity.HasVersionSuffix(package.FullName) ||
                    package.Mods == null ||
                    package.Mods.Count == 0 ||
                    !names.Add(package.FullName))
                {
                    throw new InvalidDataException(
                        "The manifest contains an invalid or duplicate package.");
                }

                totalMods += package.Mods.Count;
                if (totalMods > MaximumMods)
                {
                    throw new InvalidDataException(
                        "The manifest contains too many mods.");
                }

                WriteText(writer, package.FullName, 512);
                writer.Write(package.Required);
                writer.Write(package.Mods.Count);
                for (int modIndex = 0; modIndex < package.Mods.Count; modIndex++)
                {
                    ModDescriptor mod = package.Mods[modIndex];
                    if (mod == null || !mod.IsValid() || !guids.Add(mod.Guid))
                    {
                        throw new InvalidDataException(
                            "The manifest contains an invalid or duplicate mod.");
                    }

                    WriteText(writer, mod.Guid, 512);
                    WriteText(writer, mod.Version, 64);
                    WriteText(writer, mod.Sha256, 64);
                }
            }
        }

        private static List<ModPackagePolicy> ReadPackages(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumPackages)
            {
                throw new InvalidDataException(
                    "The package count is out of range.");
            }

            List<ModPackagePolicy> packages = new List<ModPackagePolicy>(count);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> guids = new HashSet<string>(StringComparer.Ordinal);
            int totalMods = 0;
            for (int packageIndex = 0; packageIndex < count; packageIndex++)
            {
                string name = ReadText(reader, 512);
                bool required = reader.ReadBoolean();
                int modCount = reader.ReadInt32();
                if (!ThunderstorePackageIdentity.HasVersionSuffix(name) ||
                    !names.Add(name) ||
                    modCount <= 0 ||
                    modCount > MaximumMods ||
                    totalMods + modCount > MaximumMods)
                {
                    throw new InvalidDataException(
                        "The manifest contains invalid package metadata.");
                }

                totalMods += modCount;
                List<ModDescriptor> mods = new List<ModDescriptor>(modCount);
                for (int modIndex = 0; modIndex < modCount; modIndex++)
                {
                    ModDescriptor mod = new ModDescriptor(
                        ReadText(reader, 512),
                        ReadText(reader, 64),
                        ReadText(reader, 64));
                    if (!mod.IsValid() || !guids.Add(mod.Guid))
                    {
                        throw new InvalidDataException(
                            "The manifest contains an invalid or duplicate mod.");
                    }

                    mods.Add(mod);
                }

                packages.Add(new ModPackagePolicy(
                    name, required, mods.AsReadOnly()));
            }

            return packages;
        }

        private static void WriteApprovedMods(
            BinaryWriter writer,
            IReadOnlyList<ModDescriptor> mods)
        {
            writer.Write(mods.Count);
            HashSet<string> guids = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < mods.Count; index++)
            {
                ModDescriptor mod = mods[index];
                if (mod == null || !mod.IsValid() || !guids.Add(mod.Guid))
                {
                    throw new InvalidDataException(
                        "The manifest contains an invalid approved policy.");
                }

                WriteText(writer, mod.Guid, 512);
                WriteText(writer, mod.Version, 64);
                WriteText(writer, mod.Sha256, 64);
            }
        }

        private static List<ModDescriptor> ReadApprovedMods(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumMods)
            {
                throw new InvalidDataException(
                    "The approved mod count is out of range.");
            }

            List<ModDescriptor> mods = new List<ModDescriptor>(count);
            HashSet<string> guids = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < count; index++)
            {
                ModDescriptor mod = new ModDescriptor(
                    ReadText(reader, 512),
                    ReadText(reader, 64),
                    ReadText(reader, 64));
                if (!mod.IsValid() || !guids.Add(mod.Guid))
                {
                    throw new InvalidDataException(
                        "The manifest contains an invalid or duplicate approved mod.");
                }

                mods.Add(mod);
            }

            return mods;
        }

        private static void WriteDisallowedGuids(
            BinaryWriter writer,
            IReadOnlyList<string> guids,
            IReadOnlyList<ModDescriptor> approvedMods)
        {
            HashSet<string> approved =
                new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < approvedMods.Count; index++)
            {
                approved.Add(approvedMods[index].Guid);
            }

            writer.Write(guids.Count);
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < guids.Count; index++)
            {
                string guid = guids[index];
                if (!ModDescriptor.IsSafeText(guid, 512) ||
                    approved.Contains(guid) ||
                    !unique.Add(guid))
                {
                    throw new InvalidDataException(
                        "The manifest contains invalid removal GUIDs.");
                }

                WriteText(writer, guid, 512);
            }
        }

        private static List<string> ReadDisallowedGuids(
            BinaryReader reader,
            IReadOnlyList<ModDescriptor> approvedMods)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumMods)
            {
                throw new InvalidDataException(
                    "The unauthorized GUID count is out of range.");
            }

            HashSet<string> approved =
                new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < approvedMods.Count; index++)
            {
                approved.Add(approvedMods[index].Guid);
            }

            List<string> guids = new List<string>(count);
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < count; index++)
            {
                string guid = ReadText(reader, 512);
                if (!ModDescriptor.IsSafeText(guid, 512) ||
                    approved.Contains(guid) ||
                    !unique.Add(guid))
                {
                    throw new InvalidDataException(
                        "The manifest contains invalid removal GUIDs.");
                }

                guids.Add(guid);
            }

            return guids;
        }

        private static void WriteText(
            BinaryWriter writer,
            string value,
            int maximumLength)
        {
            byte[] bytes = StrictUtf8.GetBytes(value);
            if (bytes.Length == 0 || bytes.Length > maximumLength)
            {
                throw new InvalidDataException(
                    "Manifest field is empty or too long.");
            }

            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadText(BinaryReader reader, int maximumLength)
        {
            int length = reader.ReadInt32();
            if (length <= 0 || length > maximumLength)
            {
                throw new InvalidDataException(
                    "Invalid manifest field length.");
            }

            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException();
            }

            return StrictUtf8.GetString(bytes);
        }
    }
}
