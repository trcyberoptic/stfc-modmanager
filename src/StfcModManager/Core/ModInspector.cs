using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace StfcModManager.Core;

public sealed record PluginInfo(
    string Guid,
    string Name,
    string Version,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Incompatibilities);

/// <summary>
/// Liest BepInEx-Metadaten aus einer DLL, ohne sie zu laden. Damit unterscheidet
/// der Manager echte Plugins von geteilten Bibliotheken (Newtonsoft, protobuf-net,
/// UniverseLib) im selben plugins-Ordner.
/// </summary>
public static class ModInspector
{
    public static PluginInfo? Read(string dllPath)
    {
        try
        {
            using var fs = File.OpenRead(dllPath);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();

            string? guid = null, name = null, version = null;
            var deps = new List<string>();
            var incompat = new List<string>();

            foreach (var handle in md.CustomAttributes)
            {
                var attr = md.GetCustomAttribute(handle);
                switch (AttributeTypeName(md, attr))
                {
                    case "BepInPlugin":
                        var a = DecodeStringArgs(md.GetBlobBytes(attr.Value), 3);
                        if (a.Count == 3) { guid = a[0]; name = a[1]; version = a[2]; }
                        break;
                    case "BepInDependency":
                        var d = DecodeStringArgs(md.GetBlobBytes(attr.Value), 1);
                        if (d.Count == 1) deps.Add(d[0]);
                        break;
                    case "BepInIncompatibility":
                        var i = DecodeStringArgs(md.GetBlobBytes(attr.Value), 1);
                        if (i.Count == 1) incompat.Add(i[0]);
                        break;
                }
            }

            return guid is null
                ? null
                : new PluginInfo(guid, string.IsNullOrEmpty(name) ? guid : name,
                                 version ?? "0.0.0", deps, incompat);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException
                                    or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attr)
    {
        switch (attr.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var mr = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (mr.Parent.Kind != HandleKind.TypeReference) return null;
                return md.GetString(md.GetTypeReference((TypeReferenceHandle)mr.Parent).Name);
            case HandleKind.MethodDefinition:
                var mdef = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor);
                return md.GetString(md.GetTypeDefinition(mdef.GetDeclaringType()).Name);
            default:
                return null;
        }
    }

    /// <summary>
    /// Dekodiert die festen Zeichenketten-Argumente eines Attribut-Blobs
    /// (ECMA-335 II.23.3). Bricht ab, sobald ein Argument kein String ist.
    /// </summary>
    public static IReadOnlyList<string> DecodeStringArgs(byte[] blob, int max)
    {
        var result = new List<string>();
        if (blob.Length < 2 || blob[0] != 0x01 || blob[1] != 0x00) return result;

        var pos = 2;
        for (var i = 0; i < max; i++)
        {
            if (pos >= blob.Length) break;
            if (blob[pos] == 0xFF) break;                       // null-String: Ende
            if (!TryReadCompressedUInt(blob, ref pos, out var len)) break;
            if (pos + len > blob.Length) break;
            result.Add(Encoding.UTF8.GetString(blob, pos, (int)len));
            pos += (int)len;
        }
        return result;
    }

    private static bool TryReadCompressedUInt(byte[] b, ref int pos, out uint value)
    {
        value = 0;
        if (pos >= b.Length) return false;
        var b0 = b[pos];
        if ((b0 & 0x80) == 0) { value = b0; pos += 1; return true; }
        if ((b0 & 0xC0) == 0x80)
        {
            if (pos + 1 >= b.Length) return false;
            value = (uint)(((b0 & 0x3F) << 8) | b[pos + 1]); pos += 2; return true;
        }
        if ((b0 & 0xE0) == 0xC0)
        {
            if (pos + 3 >= b.Length) return false;
            value = (uint)(((b0 & 0x1F) << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3]);
            pos += 4; return true;
        }
        return false;
    }
}
