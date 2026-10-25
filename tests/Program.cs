using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ModSync;

internal static class Program
{
    private static int _passed;

    private static int Main()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "lang", "English.json")))
        {
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        }

        Loc.LoadDirectory(Path.Combine(root ?? string.Empty, "lang"));
        Run("required mod and absent optional accepted", RequiredAndOptional);
        Run("unsafe zip paths rejected", UnsafeArchivePaths);
        Run("present optional accepted", PresentOptional);
        Run("missing required rejected", MissingRequired);
        Run("additional mod rejected", AdditionalMod);
        Run("incorrect version rejected", IncorrectVersion);
        Run("incorrect hash rejected", IncorrectHash);
        Run("malformed descriptors rejected", MalformedDescriptors);
        Run("inventory codec round-trip", CodecRoundTrip);
        Run("truncated and trailing packets rejected", MalformedPackets);
        Run("challenge is one-use and connection-bound", ReplayRejected);
        Run("expired challenge rejected", ExpiredChallenge);
        Run("repair extends challenge deadline", RepairDeadlineExtension);
        Run("Thunderstore package repair only covers approved mods", PackageRepairSelection);
        Run("Thunderstore package version metadata URL is exact and validated", ThunderstoreVersionMetadata);
        Run("Thunderstore files follow Valheim r2modman install rules", ThunderstoreInstallPaths);
        Run("Thunderstore sync packet round-trip and validation", ThunderstoreSyncPacket);
        Run("first server start creates policy folders and copies ModSync", BootstrapCopiesSelf);
        Run("policy bootstrap preserves an existing approved DLL", BootstrapPreservesExistingCopy);
        Run("messages default to English and translate to Spanish", LocalizationFallbacks);
        Run("external JSON translations load and validate placeholders", ExternalTranslations);
        Run("language files and code keys are consistent", LanguageFilesAreConsistent);
        Console.WriteLine($"{_passed} pruebas superadas.");
        return 0;
    }

    private static void ExternalTranslations()
    {
        string directory = Path.Combine(Path.GetTempPath(), "modsync-loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "French.json"),
                "\uFEFF{ \"policy.missing_required\": \"Mod obligatoire '{0}' absent.\"," +
                " \"policy.wrong_version\": \"Mauvaise \\u00e9dition\"," +
                " \"policy.unauthorized_mod\": \"Interdit {1}\"," +
                " \"unknown.key\": \"x\" }");
            File.WriteAllText(Path.Combine(directory, "Broken.json"), "{ \"a\": ");
            List<string> warnings = new List<string>();
            int loaded = Loc.LoadDirectory(directory, warnings.Add);
            Assert(loaded == 1, "Only valid entries must load, got " + loaded);
            Assert(warnings.Count == 4, "Bad placeholders, unknown keys and broken files must warn.");
            Assert(Loc.Decode(Loc.Encode("policy.missing_required", "x"), "French") ==
                   "Mod obligatoire 'x' absent.", "The external translation must be used.");
            Assert(Loc.Decode(Loc.Encode("policy.unauthorized_mod", "x", "1", "h"), "French") ==
                   "Unauthorized mod: 'x' (version 1, SHA-256 h).",
                "Mismatched placeholders must fall back to English.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void LocalizationFallbacks()
    {
        string encoded = Loc.Encode("policy.missing_required", "a.b");
        Assert(Loc.Decode(encoded, null) == "Missing required mod 'a.b'.",
            "Without a known language the English text must be used.");
        Assert(Loc.Decode(encoded, "German") == "Missing required mod 'a.b'.",
            "An unsupported language must fall back to English.");
        Assert(Loc.Decode(encoded, "Spanish") == "Falta el mod obligatorio 'a.b'.",
            "Spanish must translate the message with its arguments.");
        Assert(Loc.Decode("unknown.key", "Spanish") == "unknown.key",
            "An unknown key must be shown as is.");
    }

    private static void LanguageFilesAreConsistent()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "lang", "English.json")))
        {
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        }

        Assert(root != null, "lang\\English.json was not found.");
        Dictionary<string, string> english = Loc.ParseFlatJson(
            File.ReadAllText(Path.Combine(root, "lang", "English.json")));
        Dictionary<string, string> spanish = Loc.ParseFlatJson(
            File.ReadAllText(Path.Combine(root, "lang", "Spanish.json")));
        Assert(english.Keys.OrderBy(k => k).SequenceEqual(spanish.Keys.OrderBy(k => k)),
            "English.json and Spanish.json must define the same keys.");

        System.Text.RegularExpressions.Regex placeholder =
            new System.Text.RegularExpressions.Regex(@"\{(\d+)");
        foreach (KeyValuePair<string, string> entry in english)
        {
            Assert(placeholder.Matches(entry.Value).Select(m => m.Value).OrderBy(v => v)
                       .SequenceEqual(placeholder.Matches(spanish[entry.Key]).Select(m => m.Value).OrderBy(v => v)),
                "Placeholders differ for " + entry.Key);
        }

        System.Text.RegularExpressions.Regex used = new System.Text.RegularExpressions.Regex(
            "\"((?:installer|handshake|repair|dialog|policy)\\.[a-z_]+)\"");
        HashSet<string> keys = new HashSet<string>();
        foreach (string file in new[] { "ThunderstorePackageInstaller.cs", "ModSyncHandshake.cs", "ModPolicy.cs", "ArchivePath.cs" })
        {
            foreach (System.Text.RegularExpressions.Match match in used.Matches(
                         File.ReadAllText(Path.Combine(root, file))))
            {
                keys.Add(match.Groups[1].Value);
            }
        }

        Assert(keys.IsSubsetOf(english.Keys), "Keys used in code are missing: " +
            string.Join(", ", keys.Except(english.Keys)));
        Assert(english.Keys.ToList().TrueForAll(keys.Contains), "Unused keys: " +
            string.Join(", ", english.Keys.Except(keys)));
    }

    private static void UnsafeArchivePaths()
    {
        foreach (string bad in new[]
                 { "../x", "a/../x", ".. /x", "a/.../x", "a\\.. \\x", "/x", "C:/x", "a\0b" })
        {
            bool rejected = false;
            try { ArchivePath.Normalize(bad); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "Unsafe zip path must be rejected: " + bad);
        }

        Assert(ArchivePath.Normalize("BepInEx\\plugins\\a.dll") == "BepInEx/plugins/a.dll",
            "Safe paths are normalized to '/'.");
        Assert(ArchivePath.Normalize("dir/.hidden/file.txt") == "dir/.hidden/file.txt",
            "Dotfiles are valid.");
    }

    private static void RequiredAndOptional()
    {
        ModPolicy policy = Policy();
        PolicyValidationResult result = ModPolicyValidator.Validate(policy,
            new[] { RequiredMod() });
        Assert(result.Accepted, result.Message);
    }

    private static void PresentOptional()
    {
        PolicyValidationResult result = ModPolicyValidator.Validate(Policy(),
            new[] { RequiredMod(), OptionalMod() });
        Assert(result.Accepted, result.Message);
    }

    private static void MissingRequired()
    {
        PolicyValidationResult result = ModPolicyValidator.Validate(Policy(),
            Array.Empty<ModDescriptor>());
        Assert(!result.Accepted && Loc.Decode(result.Message, "English").Contains("Missing", StringComparison.Ordinal),
            "El mod obligatorio ausente debe rechazarse.");
    }

    private static void AdditionalMod()
    {
        PolicyValidationResult result = ModPolicyValidator.Validate(Policy(),
            new[] { RequiredMod(), Mod("unapproved", "1.0.0", HashC) });
        Assert(!result.Accepted && Loc.Decode(result.Message, "English").Contains("Unauthorized", StringComparison.Ordinal),
            "Un mod adicional debe rechazarse.");
    }

    private static void IncorrectVersion()
    {
        PolicyValidationResult result = ModPolicyValidator.Validate(Policy(),
            new[] { Mod("required", "2.0.0", HashA) });
        Assert(!result.Accepted && Loc.Decode(result.Message, "English").Contains("Wrong version", StringComparison.Ordinal),
            "Una versión diferente debe rechazarse.");
    }

    private static void IncorrectHash()
    {
        PolicyValidationResult result = ModPolicyValidator.Validate(Policy(),
            new[] { Mod("required", "1.0.0", HashB) });
        Assert(!result.Accepted && Loc.Decode(result.Message, "English").Contains("SHA-256", StringComparison.Ordinal),
            "Un hash diferente debe rechazarse.");
    }

    private static void MalformedDescriptors()
    {
        PolicyValidationResult invalidHash = ModPolicyValidator.Validate(Policy(),
            new[] { Mod("required", "1.0.0", "xyz") });
        PolicyValidationResult duplicate = ModPolicyValidator.Validate(Policy(),
            new[] { RequiredMod(), RequiredMod() });
        Assert(!invalidHash.Accepted, "Un hash inválido debe rechazarse.");
        Assert(!duplicate.Accepted, "Un GUID duplicado debe rechazarse.");
        Throws<InvalidOperationException>(() => ModPolicy.Create(
            new[] { RequiredMod(), Mod("required", "2.0.0", HashB) },
            Array.Empty<ModDescriptor>()));
        Throws<InvalidOperationException>(() => ModPolicy.Create(
            new[] { RequiredMod() },
            new[] { RequiredMod() }));
        Throws<InvalidOperationException>(() => ModPolicy.Create(
            CreateMods(ModPolicyValidator.MaximumMods + 1),
            Array.Empty<ModDescriptor>()));
    }

    private static void CodecRoundTrip()
    {
        string encoded = InventoryPacketCodec.EncodeToBase64(
            Nonce, new[] { RequiredMod(), OptionalMod() });
        InventoryPacket packet = InventoryPacketCodec.DecodeFromBase64(encoded);
        Assert(packet.Nonce == Nonce, "El nonce debe conservarse.");
        Assert(packet.Mods.Count == 2, "Deben conservarse ambos mods.");
        Assert(packet.Mods[1].Guid == "optional", "El orden debe conservarse.");
    }

    private static void MalformedPackets()
    {
        string valid = InventoryPacketCodec.EncodeToBase64(Nonce,
            new[] { RequiredMod() });
        byte[] payload = Convert.FromBase64String(valid);
        Throws<InvalidDataException>(() =>
            InventoryPacketCodec.DecodeFromBase64(Convert.ToBase64String(
                payload, 0, payload.Length - 1)));

        byte[] trailing = new byte[payload.Length + 1];
        Array.Copy(payload, trailing, payload.Length);
        Throws<InvalidDataException>(() =>
            InventoryPacketCodec.DecodeFromBase64(Convert.ToBase64String(trailing)));
        Throws<InvalidDataException>(() => InventoryPacketCodec.DecodeFromBase64("not-base64!"));

        byte[] wrongVersion = (byte[])payload.Clone();
        wrongVersion[4] = (byte)(InventoryPacketCodec.ProtocolVersion + 1);
        Throws<InvalidDataException>(() =>
            InventoryPacketCodec.DecodeFromBase64(Convert.ToBase64String(wrongVersion)));

        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(0x4D53594E);
            writer.Write(InventoryPacketCodec.ProtocolVersion);
            writer.Write(64);
            writer.Write(System.Text.Encoding.UTF8.GetBytes(Nonce));
            writer.Write(-1);
            Throws<InvalidDataException>(() =>
                InventoryPacketCodec.DecodeFromBase64(
                    Convert.ToBase64String(stream.ToArray())));
        }
    }

    private static void ReplayRejected()
    {
        ChallengeRegistry<object> registry = new ChallengeRegistry<object>();
        object firstConnection = new object();
        object secondConnection = new object();
        Assert(registry.Register(firstConnection, 100), "La conexión debe registrarse.");
        Assert(registry.Register(secondConnection, 100), "La segunda conexión debe registrarse.");
        Assert(registry.TryIssue(firstConnection, Nonce, 50), "Debe emitirse el nonce.");
        Assert(registry.TryIssue(secondConnection, OtherNonce, 50),
            "La segunda conexión debe tener un desafío distinto.");
        Assert(!registry.TryConsume(secondConnection, Nonce, 10),
            "Un nonce no debe autorizar otra conexión.");
        Assert(!registry.TryConsume(secondConnection, OtherNonce, 10),
            "Una respuesta con nonce incorrecto debe consumir y rechazar el desafío.");
        Assert(registry.TryConsume(firstConnection, Nonce, 10),
            "La respuesta válida debe consumirse.");
        Assert(registry.Accept(firstConnection), "La conexión validada debe aceptarse.");
        Assert(!registry.TryConsume(firstConnection, Nonce, 11),
            "Una respuesta consumida no debe reutilizarse.");
        Assert(!registry.IsAccepted(secondConnection),
            "La segunda conexión debe permanecer sin autorizar.");
    }

    private static void ExpiredChallenge()
    {
        ChallengeRegistry<object> registry = new ChallengeRegistry<object>();
        object connection = new object();
        List<object> expired = new List<object>();
        registry.Register(connection, 100);
        registry.TryIssue(connection, Nonce, 10);
        Assert(!registry.TryConsume(connection, Nonce, 10),
            "Un desafío debe caducar en su fecha límite.");
        Assert(!registry.IsAccepted(connection),
            "Una respuesta tardía no debe autorizar la conexión.");

        object secondConnection = new object();
        registry.Register(secondConnection, 20);
        registry.CollectExpired(20, expired);
        Assert(expired.Count == 1 && ReferenceEquals(expired[0], secondConnection),
            "La conexión sin respuesta debe expirar.");
    }

    private static void RepairDeadlineExtension()
    {
        ChallengeRegistry<object> registry = new ChallengeRegistry<object>();
        object connection = new object();
        List<object> expired = new List<object>();
        Assert(registry.Register(connection, 10), "La conexión debe registrarse.");
        Assert(registry.ExtendDeadline(connection, 20),
            "La instalación debe ampliar el plazo de validación.");
        Assert(!registry.ExtendDeadline(connection, 15),
            "La ampliación no debe acortar el plazo.");
        registry.CollectExpired(10, expired);
        Assert(expired.Count == 0, "La conexión no debe caducar con el plazo inicial.");
        registry.CollectExpired(20, expired);
        Assert(expired.Count == 1 && ReferenceEquals(expired[0], connection),
            "La conexión debe caducar al alcanzar el plazo ampliado.");
    }

    private static void PackageRepairSelection()
    {
        ModPackagePolicy package = new ModPackagePolicy(
            "owner-package-1.2.3", true, new[] { RequiredMod() });
        ModPolicy policy = ModPolicy.Create(
            new[] { RequiredMod() },
            Array.Empty<ModDescriptor>(),
            new[] { package });

        Assert(ModPolicyValidator.TryGetPackageRepairs(
                policy,
                Array.Empty<ModDescriptor>(),
                out IReadOnlyList<ModPackagePolicy> repairs,
                out IReadOnlyList<string> noRemovals),
            "Debe proponerse el paquete aprobado cuando falta un mod obligatorio.");
        Assert(repairs.Count == 1 && repairs[0].FullName == package.FullName,
            "La reparación debe seleccionar el paquete que contiene el GUID faltante.");
        Assert(noRemovals.Count == 0,
            "La reparación por mod ausente no debe solicitar eliminaciones.");
        bool canRemoveUnauthorized = ModPolicyValidator.TryGetPackageRepairs(
                policy,
                new[] { RequiredMod(), Mod("unapproved", "1.0.0", HashC) },
                out IReadOnlyList<ModPackagePolicy> repairWithRemoval,
                out IReadOnlyList<string> removals);
        Assert(canRemoveUnauthorized &&
               repairWithRemoval.Count == 0 &&
               removals.Count == 1 &&
               removals[0] == "unapproved",
            "Los mods no autorizados deben solicitarse para eliminación.");
        Assert(ModPolicyValidator.TryGetPackageRepairs(
                policy,
                new[] { Mod("unapproved", "1.0.0", HashC) },
                out IReadOnlyList<ModPackagePolicy> repairAndRemove,
                out IReadOnlyList<string> missingAndExtra),
            "Se deben combinar reparación de obligatorios y limpieza de mods sobrantes.");
        Assert(repairAndRemove.Count == 1 &&
               missingAndExtra.Count == 1 &&
               missingAndExtra[0] == "unapproved",
            "La misma operación debe instalar lo aprobado y eliminar lo no permitido.");
        Assert(ThunderstorePackageIdentity.HasVersionSuffix(package.FullName),
            "El nombre owner-package-version debe reconocerse.");
        Assert(!ThunderstorePackageIdentity.HasVersionSuffix("not-a-package"),
            "Un nombre sin versión semántica debe rechazarse.");
    }

    private static void ThunderstoreSyncPacket()
    {
        ModPackagePolicy package = new ModPackagePolicy(
            "owner-package-1.2.3", true, new[] { RequiredMod() });
        ThunderstoreSyncManifest manifest = new ThunderstoreSyncManifest(
            Nonce,
            new[] { package },
            new[] { package },
            new[] { RequiredMod() },
            new[] { "unapproved" });
        ThunderstoreSyncManifest decoded =
            ThunderstoreSyncPacketCodec.DecodeFromBase64(
                ThunderstoreSyncPacketCodec.EncodeToBase64(manifest));

        Assert(decoded.Nonce == Nonce, "El nonce debe conservarse.");
        Assert(decoded.InstallPackages.Count == 1, "Debe conservarse el paquete a instalar.");
        Assert(decoded.AvailablePackages[0].Mods[0].Guid == "required",
            "Deben conservarse los GUID aprobados del paquete.");
        Assert(decoded.ApprovedMods.Count == 1 &&
               decoded.ApprovedMods[0].Guid == "required",
            "El cliente debe recibir el conjunto completo de GUID aprobados.");
        Assert(decoded.DisallowedGuids.Count == 1 &&
               decoded.DisallowedGuids[0] == "unapproved",
            "El cliente debe recibir los GUID que debe retirar.");
        Throws<InvalidDataException>(() =>
            ThunderstoreSyncPacketCodec.DecodeFromBase64("bad-packet"));

        ThunderstoreSyncManifest cleanupOnly = new ThunderstoreSyncManifest(
            Nonce,
            Array.Empty<ModPackagePolicy>(),
            Array.Empty<ModPackagePolicy>(),
            new[] { RequiredMod() },
            new[] { "unapproved" });
        ThunderstoreSyncManifest decodedCleanupOnly =
            ThunderstoreSyncPacketCodec.DecodeFromBase64(
                ThunderstoreSyncPacketCodec.EncodeToBase64(cleanupOnly));
        Assert(decodedCleanupOnly.InstallPackages.Count == 0 &&
               decodedCleanupOnly.DisallowedGuids.Count == 1,
            "Un manifiesto puede solicitar solo la eliminación de mods no autorizados.");
        Throws<InvalidDataException>(() =>
            ThunderstoreSyncPacketCodec.EncodeToBase64(
                new ThunderstoreSyncManifest(
                    Nonce,
                    Array.Empty<ModPackagePolicy>(),
                    Array.Empty<ModPackagePolicy>(),
                    new[] { RequiredMod() },
                    new[] { "required" })));
    }

    private static void ThunderstoreVersionMetadata()
    {
        const string packageId = "McLovin-McPortalMetal-1.0.0";
        Assert(ThunderstorePackageIdentity.TryParse(
                packageId, out string owner, out string package, out string version),
            "El identificador del paquete debe descomponerse.");
        Assert(owner == "McLovin" && package == "McPortalMetal" && version == "1.0.0",
            "La identidad debe conservar owner, paquete y versión.");
        Assert(
            ThunderstorePackageIdentity.VersionMetadataUrl(owner, package, version) ==
            "https://thunderstore.io/api/experimental/package/McLovin/McPortalMetal/1.0.0/",
            "Debe consultarse metadata de esa versión, no el catálogo global.");
        Assert(ThunderstorePackageIdentity.MatchesVersion(
                packageId,
                owner,
                package,
                version,
                "McLovin",
                "McPortalMetal",
                "1.0.0",
                packageId),
            "La metadata de la identidad solicitada debe validarse.");
        Assert(!ThunderstorePackageIdentity.MatchesVersion(
                packageId,
                owner,
                package,
                version,
                "OtherOwner",
                package,
                version,
                packageId),
            "Debe rechazarse metadata de otro paquete aunque su full_name coincida.");
        Assert(!ThunderstorePackageIdentity.TryParse(
                "McLovin-McPortalMetal-not-a-version", out _, out _, out _),
            "Un identificador sin versión válida debe rechazarse.");
    }

    private static void ThunderstoreInstallPaths()
    {
        ThunderstoreInstallPath rootDll = ThunderstoreInstallPathResolver.Resolve(
            "McPortalMetal", "McPortalMetal.dll");
        Assert(rootDll.ProfilePath ==
               "BepInEx/plugins/McPortalMetal/McPortalMetal.dll" &&
               rootDll.Tracked,
            "Una DLL en la raíz debe instalarse dentro de plugins/<paquete>.");

        ThunderstoreInstallPath packagedPlugin = ThunderstoreInstallPathResolver.Resolve(
            "McPortalMetal", "BepInEx/plugins/Extra/McPortalMetal.dll");
        Assert(packagedPlugin.ProfilePath ==
               "BepInEx/plugins/McPortalMetal/Extra/McPortalMetal.dll" &&
               packagedPlugin.Tracked,
            "El contenido bajo BepInEx/plugins debe conservar su estructura dentro del paquete.");

        ThunderstoreInstallPath patcher = ThunderstoreInstallPathResolver.Resolve(
            "McPortalMetal", "BepInEx/patchers/Patcher.dll");
        Assert(patcher.ProfilePath ==
               "BepInEx/patchers/McPortalMetal/Patcher.dll" &&
               patcher.Tracked,
            "Los patchers deben seguir su ruta de instalación por paquete.");

        ThunderstoreInstallPath monoMod = ThunderstoreInstallPathResolver.Resolve(
            "McPortalMetal", "BepInEx/monomod/Module.mm.dll");
        Assert(monoMod.ProfilePath ==
               "BepInEx/monomod/McPortalMetal/Module.mm.dll" &&
               monoMod.Tracked,
            "Los archivos MonoMod deben seguir su ruta dedicada.");

        ThunderstoreInstallPath config = ThunderstoreInstallPathResolver.Resolve(
            "McPortalMetal", "BepInEx/config/joaorodrigues.cfg");
        Assert(config.ProfilePath == "BepInEx/config/joaorodrigues.cfg" &&
               !config.Tracked,
            "La configuración debe seguir la regla no rastreada de r2modman.");

        ThunderstoreInstallPath auxiliary = ThunderstoreInstallPathResolver.Resolve(
            "McPortalMetal", "docs/readme.txt");
        Assert(auxiliary.ProfilePath == "BepInEx/plugins/McPortalMetal/readme.txt",
            "Los archivos sin regla deben ir a la ubicación predeterminada de plugins.");
        Throws<InvalidDataException>(() =>
            ThunderstoreInstallPathResolver.Resolve(
                "McPortalMetal", "../outside.dll"));
    }

    private static void BootstrapCopiesSelf()
    {
        string root = Path.Combine(Path.GetTempPath(), "ModSyncTests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "ModSync.dll");
        byte[] contents = { 1, 3, 5, 7 };
        File.WriteAllBytes(source, contents);
        string configRoot = Path.Combine(root, "BepInEx", "config");

        try
        {
            bool copied = ModPolicyBootstrap.EnsureDirectoriesAndCopySelf(configRoot, source);
            string required = Path.Combine(configRoot, "ModSync", "required_mods");
            string optional = Path.Combine(configRoot, "ModSync", "optional_mods");
            string destination = Path.Combine(required, "ModSync.dll");
            Assert(copied, "La primera instalación debe copiar ModSync.");
            Assert(Directory.Exists(required), "Debe crearse required_mods.");
            Assert(Directory.Exists(optional), "Debe crearse optional_mods.");
            Assert(File.ReadAllBytes(destination).Length == contents.Length,
                "La DLL copiada debe conservar el contenido.");
            Assert(!ModPolicyBootstrap.EnsureDirectoriesAndCopySelf(configRoot, source),
                "Una segunda ejecución no debe volver a copiar la DLL.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void BootstrapPreservesExistingCopy()
    {
        string root = Path.Combine(Path.GetTempPath(), "ModSyncTests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "ModSync.dll");
        byte[] sourceContents = { 1, 2, 3 };
        File.WriteAllBytes(source, sourceContents);
        string configRoot = Path.Combine(root, "BepInEx", "config");
        string required = Path.Combine(configRoot, "ModSync", "required_mods");
        Directory.CreateDirectory(required);
        string destination = Path.Combine(required, "ModSync.dll");
        byte[] approvedContents = { 9, 8, 7 };
        File.WriteAllBytes(destination, approvedContents);

        try
        {
            Assert(!ModPolicyBootstrap.EnsureDirectoriesAndCopySelf(configRoot, source),
                "La copia existente debe conservarse.");
            byte[] actual = File.ReadAllBytes(destination);
            Assert(actual.Length == approvedContents.Length &&
                   actual[0] == approvedContents[0],
                "El bootstrap no debe sobrescribir la DLL existente.");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ModPolicy Policy()
    {
        return ModPolicy.Create(
            new[] { RequiredMod() },
            new[] { OptionalMod() });
    }

    private static ModDescriptor[] CreateMods(int count)
    {
        ModDescriptor[] mods = new ModDescriptor[count];
        for (int index = 0; index < count; index++)
        {
            mods[index] = Mod($"mod-{index}", "1.0.0", HashA);
        }

        return mods;
    }

    private static ModDescriptor RequiredMod()
    {
        return Mod("required", "1.0.0", HashA);
    }

    private static ModDescriptor OptionalMod()
    {
        return Mod("optional", "1.5.0", HashB);
    }

    private static ModDescriptor Mod(string guid, string version, string hash)
    {
        return new ModDescriptor(guid, version, hash);
    }

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Se esperaba {typeof(TException).Name}.");
    }

    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string HashC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string Nonce = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherNonce = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
}
