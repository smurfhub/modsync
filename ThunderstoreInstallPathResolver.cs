using System;
using System.Collections.Generic;
using System.IO;

namespace ModSync
{
    internal sealed class ThunderstoreInstallPath
    {
        internal ThunderstoreInstallPath(string profilePath, bool tracked)
        {
            ProfilePath = profilePath;
            Tracked = tracked;
        }

        internal string ProfilePath { get; }
        internal bool Tracked { get; }
    }

    internal static class ThunderstoreInstallPathResolver
    {
        private static readonly Dictionary<string, InstallRule> DirectoryRules =
            new Dictionary<string, InstallRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["plugins"] = new InstallRule("BepInEx/plugins", true),
                ["core"] = new InstallRule("BepInEx/core", true),
                ["patchers"] = new InstallRule("BepInEx/patchers", true),
                ["monomod"] = new InstallRule("BepInEx/monomod", true),
                ["config"] = new InstallRule("BepInEx/config", false),
                ["slimvml"] = new InstallRule("BepInEx/SlimVML", true)
            };

        internal static ThunderstoreInstallPath Resolve(
            string packageName,
            string archiveRelativePath)
        {
            if (string.IsNullOrWhiteSpace(packageName) ||
                string.IsNullOrWhiteSpace(archiveRelativePath) ||
                Path.IsPathRooted(archiveRelativePath) ||
                archiveRelativePath.IndexOf(':') >= 0)
            {
                throw new InvalidDataException("The package path is not valid.");
            }

            string normalized = archiveRelativePath.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            for (int index = 0; index < segments.Length; index++)
            {
                if (segments[index].Length == 0 ||
                    segments[index] == "." ||
                    segments[index] == "..")
                {
                    throw new InvalidDataException("The package path is not valid.");
                }
            }

            InstallRule rule = null;
            int matchedDirectory = -1;
            for (int index = segments.Length - 2; index >= 0; index--)
            {
                if (DirectoryRules.TryGetValue(segments[index], out rule))
                {
                    matchedDirectory = index;
                    break;
                }
            }

            string relativeToRule;
            if (rule != null)
            {
                relativeToRule = JoinSegments(segments, matchedDirectory + 1);
            }
            else
            {
                string fileName = segments[segments.Length - 1];
                rule = GetFileRule(fileName);
                relativeToRule = fileName;
            }

            if (relativeToRule.Length == 0)
            {
                throw new InvalidDataException(
                    "The package path does not contain an installable file.");
            }

            string installedPath = rule.Tracked
                ? rule.Route + "/" + packageName + "/" + relativeToRule
                : rule.Route + "/" + relativeToRule;
            return new ThunderstoreInstallPath(installedPath, rule.Tracked);
        }

        private static InstallRule GetFileRule(string fileName)
        {
            if (fileName.EndsWith(".mm.dll", StringComparison.OrdinalIgnoreCase))
            {
                return DirectoryRules["monomod"];
            }

            return DirectoryRules["plugins"];
        }

        private static string JoinSegments(string[] segments, int startIndex)
        {
            if (startIndex >= segments.Length)
            {
                return string.Empty;
            }

            return string.Join("/", segments, startIndex, segments.Length - startIndex);
        }

        private sealed class InstallRule
        {
            internal InstallRule(string route, bool tracked)
            {
                Route = route;
                Tracked = tracked;
            }

            internal string Route { get; }
            internal bool Tracked { get; }
        }
    }
}
