using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ModSync
{
    internal sealed class InventoryPacket
    {
        internal InventoryPacket(string nonce, List<ModDescriptor> mods)
        {
            Nonce = nonce;
            Mods = mods;
        }

        internal string Nonce { get; }
        internal IReadOnlyList<ModDescriptor> Mods { get; }
    }

    internal static class InventoryPacketCodec
    {
        internal const int ProtocolVersion = 1;
        private const int Magic = 0x4D53594E;
        private const int MaximumPayloadBytes = 128 * 1024;
        private const int MaximumEncodedLength = ((MaximumPayloadBytes + 2) / 3) * 4;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        internal static bool IsNonce(string value)
        {
            return ModDescriptor.IsSha256(value);
        }

        internal static string EncodeToBase64(
            string nonce,
            IReadOnlyList<ModDescriptor> mods)
        {
            if (!IsNonce(nonce) || mods == null || mods.Count > ModPolicyValidator.MaximumMods)
            {
                throw new InvalidDataException("Invalid mod inventory.");
            }

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream, StrictUtf8, true))
            {
                writer.Write(Magic);
                writer.Write(ProtocolVersion);
                WriteText(writer, nonce, 64);
                writer.Write(mods.Count);

                for (int index = 0; index < mods.Count; index++)
                {
                    ModDescriptor mod = mods[index];
                    if (mod == null || !mod.IsValid())
                    {
                                            throw new InvalidDataException("Invalid mod descriptor.");
                    }

                    WriteText(writer, mod.Guid, 512);
                    WriteText(writer, mod.Version, 64);
                    WriteText(writer, mod.Sha256, 64);
                    if (stream.Length > MaximumPayloadBytes)
                    {
                        throw new InvalidDataException("The inventory exceeds the allowed size.");
                    }
                }

                writer.Flush();
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        internal static InventoryPacket DecodeFromBase64(string encoded)
        {
            if (string.IsNullOrEmpty(encoded) || encoded.Length > MaximumEncodedLength)
            {
                throw new InvalidDataException("The inventory message is empty or too large.");
            }

            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(encoded);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("The inventory message is not valid Base64.", exception);
            }

            if (payload.Length > MaximumPayloadBytes)
            {
                throw new InvalidDataException("The inventory message exceeds the allowed size.");
            }

            try
            {
                using (MemoryStream stream = new MemoryStream(payload, false))
                using (BinaryReader reader = new BinaryReader(stream, StrictUtf8, true))
                {
                    if (reader.ReadInt32() != Magic || reader.ReadInt32() != ProtocolVersion)
                    {
                        throw new InvalidDataException("Invalid protocol version or signature.");
                    }

                    string nonce = ReadText(reader, 64);
                    if (!IsNonce(nonce))
                    {
                        throw new InvalidDataException("Invalid nonce.");
                    }

                    int count = reader.ReadInt32();
                    if (count < 0 || count > ModPolicyValidator.MaximumMods)
                    {
                        throw new InvalidDataException("The mod count is out of range.");
                    }

                    List<ModDescriptor> mods = new List<ModDescriptor>(count);
                    for (int index = 0; index < count; index++)
                    {
                        ModDescriptor mod = new ModDescriptor(
                            ReadText(reader, 512),
                            ReadText(reader, 64),
                            ReadText(reader, 64));
                        if (!mod.IsValid())
                        {
                                                    throw new InvalidDataException("Invalid mod descriptor.");
                        }

                        mods.Add(mod);
                    }

                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("The message contains leftover data.");
                    }

                    return new InventoryPacket(nonce, mods);
                }
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("The inventory message is truncated.", exception);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("The message contains invalid UTF-8 text.", exception);
            }
        }

        private static void WriteText(BinaryWriter writer, string value, int maximumLength)
        {
            byte[] bytes = StrictUtf8.GetBytes(value);
            if (bytes.Length == 0 || bytes.Length > maximumLength)
            {
                throw new InvalidDataException("Inventory field is empty or too long.");
            }

            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadText(BinaryReader reader, int maximumLength)
        {
            int length = reader.ReadInt32();
            if (length <= 0 || length > maximumLength)
            {
                throw new InvalidDataException("Invalid inventory field length.");
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
