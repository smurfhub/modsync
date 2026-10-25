using System;
using System.IO;

namespace ModSync
{
    internal static class ArchivePath
    {
        // Devuelve la ruta de un entry del zip con '/' como separador o lanza si no es segura.
        internal static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                path.IndexOf(':') >= 0 ||
                path.IndexOf('\0') >= 0 ||
                path.StartsWith("/", StringComparison.Ordinal) ||
                path.StartsWith("\\", StringComparison.Ordinal))
            {
                throw new InvalidDataException(Loc.T("installer.zip_invalid_path"));
            }

            string normalized = path.Replace('\\', '/');
            foreach (string segment in normalized.Split('/'))
            {
                // Win32 recorta espacios y puntos finales: ".. " se resolvería como "..".
                if (segment.Length > 0 && segment.TrimEnd(' ', '.').Length == 0)
                {
                    throw new InvalidDataException(
                        Loc.T("installer.zip_disallowed_path"));
                }
            }

            return normalized;
        }
    }
}
