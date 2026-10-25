using System;
using System.Collections.Generic;

namespace ModSync
{
    internal sealed class ModPackagePolicy
    {
        internal ModPackagePolicy(
            string fullName,
            bool required,
            IReadOnlyList<ModDescriptor> mods)
        {
            FullName = fullName;
            Required = required;
            Mods = mods;
        }

        internal string FullName { get; }
        internal bool Required { get; }
        internal IReadOnlyList<ModDescriptor> Mods { get; }
    }

    internal sealed class ModDescriptor
    {
        internal ModDescriptor(
            string guid,
            string version,
            string sha256,
            string displayName = null)
        {
            Guid = guid;
            Version = version;
            Sha256 = sha256;
            DisplayName = displayName;
        }

        internal string Guid { get; }
        internal string Version { get; }
        internal string Sha256 { get; }
        internal string DisplayName { get; }

        internal bool IsValid()
        {
            return IsSafeText(Guid, 512) && IsSafeText(Version, 64) &&
                   IsSha256(Sha256);
        }

        internal static bool IsSafeText(string value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsControl(value[index]) || value[index] == '|')
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f') ||
                      (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class ModPolicy
    {
        private ModPolicy(
            Dictionary<string, ModDescriptor> required,
            Dictionary<string, ModDescriptor> optional,
            IReadOnlyList<ModPackagePolicy> requiredPackages,
            IReadOnlyList<ModPackagePolicy> optionalPackages)
        {
            Required = required;
            Optional = optional;
            RequiredPackages = requiredPackages;
            OptionalPackages = optionalPackages;
        }

        internal IReadOnlyDictionary<string, ModDescriptor> Required { get; }
        internal IReadOnlyDictionary<string, ModDescriptor> Optional { get; }
        internal IReadOnlyList<ModPackagePolicy> RequiredPackages { get; }
        internal IReadOnlyList<ModPackagePolicy> OptionalPackages { get; }

        internal static ModPolicy Create(
            IEnumerable<ModDescriptor> requiredMods,
            IEnumerable<ModDescriptor> optionalMods,
            IEnumerable<ModPackagePolicy> packages = null)
        {
            Dictionary<string, ModDescriptor> required = ToDictionary(requiredMods, "required");
            Dictionary<string, ModDescriptor> optional = ToDictionary(optionalMods, "optional");
            List<ModPackagePolicy> requiredPackages = new List<ModPackagePolicy>();
            List<ModPackagePolicy> optionalPackages = new List<ModPackagePolicy>();
            HashSet<string> packageGuids = new HashSet<string>(StringComparer.Ordinal);
            if (required.Count + optional.Count > ModPolicyValidator.MaximumMods)
            {
                throw new InvalidOperationException(
                    $"The policy contains more than {ModPolicyValidator.MaximumMods} mods.");
            }

            foreach (string guid in required.Keys)
            {
                if (optional.ContainsKey(guid))
                {
                    throw new InvalidOperationException(
                        $"Mod '{guid}' is configured as both required and optional.");
                }
            }

            if (packages != null)
            {
                foreach (ModPackagePolicy package in packages)
                {
                    if (package == null ||
                        !ModDescriptor.IsSafeText(package.FullName, 512) ||
                        package.Mods == null ||
                        package.Mods.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "A Thunderstore package policy is invalid.");
                    }

                    foreach (ModDescriptor descriptor in package.Mods)
                    {
                        Dictionary<string, ModDescriptor> category =
                            package.Required ? required : optional;
                        if (descriptor == null ||
                            !category.TryGetValue(descriptor.Guid, out ModDescriptor approved) ||
                            !string.Equals(approved.Version, descriptor.Version, StringComparison.Ordinal) ||
                            !string.Equals(approved.Sha256, descriptor.Sha256, StringComparison.OrdinalIgnoreCase) ||
                            !packageGuids.Add(descriptor.Guid))
                        {
                            throw new InvalidOperationException(
                                $"Thunderstore package '{package.FullName}' does not match its approved mod policy.");
                        }
                    }

                    (package.Required ? requiredPackages : optionalPackages).Add(package);
                }
            }

            return new ModPolicy(required, optional, requiredPackages, optionalPackages);
        }

        private static Dictionary<string, ModDescriptor> ToDictionary(
            IEnumerable<ModDescriptor> mods,
            string kind)
        {
            Dictionary<string, ModDescriptor> result =
                new Dictionary<string, ModDescriptor>(StringComparer.Ordinal);

            if (mods == null)
            {
                throw new ArgumentNullException(nameof(mods));
            }

            foreach (ModDescriptor descriptor in mods)
            {
                if (descriptor == null || !descriptor.IsValid())
                {
                    throw new InvalidOperationException(
                        $"The {kind} mod policy contains an invalid descriptor.");
                }

                if (!result.TryAdd(descriptor.Guid, descriptor))
                {
                    throw new InvalidOperationException(
                        $"Duplicate {kind} mod GUID '{descriptor.Guid}'.");
                }
            }

            return result;
        }
    }

    internal sealed class PolicyValidationResult
    {
        private PolicyValidationResult(bool accepted, string message)
        {
            Accepted = accepted;
            Message = message;
        }

        internal bool Accepted { get; }
        internal string Message { get; }

        internal static PolicyValidationResult Accept()
        {
            return new PolicyValidationResult(true, "Inventario autorizado.");
        }

        internal static PolicyValidationResult Reject(string message)
        {
            return new PolicyValidationResult(false, message);
        }
    }

    internal static class ModPolicyValidator
    {
        internal const int MaximumMods = 256;

        internal static PolicyValidationResult Validate(
            ModPolicy policy,
            IReadOnlyList<ModDescriptor> inventory)
        {
            if (policy == null || inventory == null || inventory.Count > MaximumMods)
            {
                return PolicyValidationResult.Reject("policy.malformed_inventory");
            }

            Dictionary<string, ModDescriptor> installed =
                new Dictionary<string, ModDescriptor>(StringComparer.Ordinal);

            for (int index = 0; index < inventory.Count; index++)
            {
                ModDescriptor mod = inventory[index];
                if (mod == null || !mod.IsValid())
                {
                    return PolicyValidationResult.Reject("policy.malformed_inventory");
                }

                if (installed.ContainsKey(mod.Guid))
                {
                    return PolicyValidationResult.Reject(
                        Loc.Encode("policy.duplicate_mod", mod.Guid));
                }

                installed.Add(mod.Guid, mod);
            }

            foreach (KeyValuePair<string, ModDescriptor> required in policy.Required)
            {
                if (!installed.TryGetValue(required.Key, out ModDescriptor actual))
                {
                    return PolicyValidationResult.Reject(
                        Loc.Encode("policy.missing_required", required.Key));
                }

                PolicyValidationResult mismatch = Compare(required.Value, actual);
                if (mismatch != null)
                {
                    return mismatch;
                }
            }

            foreach (KeyValuePair<string, ModDescriptor> installedMod in installed)
            {
                if (policy.Required.ContainsKey(installedMod.Key))
                {
                    continue;
                }

                if (!policy.Optional.TryGetValue(installedMod.Key, out ModDescriptor approved))
                {
                    return PolicyValidationResult.Reject(
                        Loc.Encode("policy.unauthorized_mod",
                            installedMod.Key,
                            installedMod.Value.Version,
                            installedMod.Value.Sha256));
                }

                PolicyValidationResult mismatch = Compare(approved, installedMod.Value);
                if (mismatch != null)
                {
                    return mismatch;
                }
            }

            return PolicyValidationResult.Accept();
        }

        internal static bool TryGetPackageRepairs(
            ModPolicy policy,
            IReadOnlyList<ModDescriptor> inventory,
            out IReadOnlyList<ModPackagePolicy> packages,
            out IReadOnlyList<string> disallowedGuids)
        {
            packages = null;
            disallowedGuids = null;
            if (policy == null || inventory == null || inventory.Count > MaximumMods)
            {
                return false;
            }

            Dictionary<string, ModDescriptor> installed =
                new Dictionary<string, ModDescriptor>(StringComparer.Ordinal);
            for (int index = 0; index < inventory.Count; index++)
            {
                ModDescriptor descriptor = inventory[index];
                if (descriptor == null || !descriptor.IsValid() ||
                    !installed.TryAdd(descriptor.Guid, descriptor))
                {
                    return false;
                }
            }

            Dictionary<string, ModPackagePolicy> packageByGuid =
                new Dictionary<string, ModPackagePolicy>(StringComparer.Ordinal);
            List<ModPackagePolicy> availablePackages = new List<ModPackagePolicy>(
                policy.RequiredPackages.Count + policy.OptionalPackages.Count);
            availablePackages.AddRange(policy.RequiredPackages);
            availablePackages.AddRange(policy.OptionalPackages);
            foreach (ModPackagePolicy package in availablePackages)
            {
                for (int index = 0; index < package.Mods.Count; index++)
                {
                    packageByGuid[package.Mods[index].Guid] = package;
                }
            }

            HashSet<string> repairNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<ModPackagePolicy> repairs = new List<ModPackagePolicy>();
            List<string> removals = new List<string>();
            foreach (KeyValuePair<string, ModDescriptor> expected in policy.Required)
            {
                if (installed.TryGetValue(expected.Key, out ModDescriptor actual) &&
                    Compare(expected.Value, actual) == null)
                {
                    continue;
                }

                if (!packageByGuid.TryGetValue(expected.Key, out ModPackagePolicy package) ||
                    !package.Required)
                {
                    return false;
                }

                if (repairNames.Add(package.FullName))
                {
                    repairs.Add(package);
                }
            }

            foreach (KeyValuePair<string, ModDescriptor> actual in installed)
            {
                if (policy.Required.ContainsKey(actual.Key))
                {
                    continue;
                }

                if (policy.Optional.TryGetValue(actual.Key, out ModDescriptor optional))
                {
                    if (Compare(optional, actual.Value) == null)
                    {
                        continue;
                    }

                    if (!packageByGuid.TryGetValue(actual.Key, out ModPackagePolicy package))
                    {
                        return false;
                    }

                    if (repairNames.Add(package.FullName))
                    {
                        repairs.Add(package);
                    }

                    continue;
                }

                removals.Add(actual.Key);
            }

            if (repairs.Count == 0 && removals.Count == 0)
            {
                return false;
            }

            packages = repairs;
            disallowedGuids = removals;
            return true;
        }

        private static PolicyValidationResult Compare(
            ModDescriptor approved,
            ModDescriptor installed)
        {
            if (!string.Equals(approved.Version, installed.Version, StringComparison.Ordinal))
            {
                return PolicyValidationResult.Reject(
                    Loc.Encode("policy.wrong_version",
                        approved.Guid, approved.Version));
            }

            if (!string.Equals(approved.Sha256, installed.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return PolicyValidationResult.Reject(
                    Loc.Encode("policy.wrong_integrity",
                        approved.Guid));
            }

            return null;
        }
    }
}
