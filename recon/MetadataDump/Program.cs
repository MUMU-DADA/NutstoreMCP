using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

// Metadata-only dump of .NET assemblies: no dependency resolution, no execution.
// Usage: MetadataDump <assembly> [namespaceFilterSubstring] [typeNameFilterSubstring]

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: MetadataDump <assembly> [nsFilter] [typeFilter]");
    Console.Error.WriteLine("       MetadataDump --us <assembly> [filter]     (dump #US string literals)");
    return 2;
}

// ---- mode: user-string literal heap (#US), parsed from the raw metadata root ----
if (args[0] == "--us")
{
    string asm = args[1];
    string flt = args.Length > 2 ? args[2] : "";
    byte[] file = File.ReadAllBytes(asm);

    int peOff = BitConverter.ToInt32(file, 0x3C);
    int numSections = BitConverter.ToUInt16(file, peOff + 6);
    int optSize = BitConverter.ToUInt16(file, peOff + 20);
    int optOff = peOff + 24;
    int sectionsOff = optOff + optSize;

    // CLI header is data directory #14; its offset differs between PE32 and PE32+
    ushort magic = BitConverter.ToUInt16(file, optOff);
    int dirsBase = optOff + (magic == 0x20b ? 112 : 96);
    int cliRva = BitConverter.ToInt32(file, dirsBase + 14 * 8);
    int cliOff = RvaToOff(file, sectionsOff, numSections, cliRva);
    int mdRva = BitConverter.ToInt32(file, cliOff + 8);
    int mdOff = RvaToOff(file, sectionsOff, numSections, mdRva);

    // metadata root
    int p = mdOff + 4 + 2 + 2 + 4;
    int verLen = BitConverter.ToInt32(file, p); p += 4;
    p += (verLen + 3) & ~3;
    p += 2; // flags
    int streams = BitConverter.ToUInt16(file, p); p += 2;

    int usOff = -1, usSize = 0;
    for (int i = 0; i < streams; i++)
    {
        int sOff = BitConverter.ToInt32(file, p); p += 4;
        int sSize = BitConverter.ToInt32(file, p); p += 4;
        int nameStart = p;
        while (file[p] != 0) p++;
        string sname = System.Text.Encoding.ASCII.GetString(file, nameStart, p - nameStart);
        p++;
        p = (p + 3) & ~3;
        if (Environment.GetEnvironmentVariable("MD_DEBUG") == "1")
            Console.Error.WriteLine($"  stream {sname} off={sOff} size={sSize} (abs={mdOff + sOff})");
        if (sname == "#US") { usOff = mdOff + sOff; usSize = sSize; }
    }
    if (Environment.GetEnvironmentVariable("MD_DEBUG") == "1")
        Console.Error.WriteLine($"  mdRva={mdRva} mdOff={mdOff} usOff={usOff} usSize={usSize} streams={streams}");
    if (usOff < 0) { Console.Error.WriteLine("no #US heap"); return 1; }

    var seen = new HashSet<string>();
    int q = usOff + 1;                     // offset 0 is the empty-string sentinel
    int end = usOff + usSize;
    while (q < end)
    {
        int b0 = file[q];
        int len, prefix;
        if ((b0 & 0x80) == 0) { len = b0; prefix = 1; }
        else if ((b0 & 0xC0) == 0x80) { len = ((b0 & 0x3F) << 8) | file[q + 1]; prefix = 2; }
        else { len = ((b0 & 0x1F) << 24) | (file[q + 1] << 16) | (file[q + 2] << 8) | file[q + 3]; prefix = 4; }
        if (len == 0) { q++; continue; }
        int strBytes = len - 1;            // last byte is the non-ASCII flag
        string s = System.Text.Encoding.Unicode.GetString(file, q + prefix, strBytes);
        q += prefix + len;
        if (string.IsNullOrWhiteSpace(s)) continue;
        s = s.Trim();
        if (flt.Length > 0 && !s.Contains(flt, StringComparison.OrdinalIgnoreCase)) continue;
        if (seen.Add(s)) Console.WriteLine(s);
    }
    return 0;
}

static int RvaToOff(byte[] f, int sectionsOff, int numSections, int rva)
{
    for (int i = 0; i < numSections; i++)
    {
        int s = sectionsOff + i * 40;
        int va = BitConverter.ToInt32(f, s + 12);
        int vsize = BitConverter.ToInt32(f, s + 8);
        int praw = BitConverter.ToInt32(f, s + 20);
        if (rva >= va && rva < va + Math.Max(vsize, BitConverter.ToInt32(f, s + 16)))
            return rva - va + praw;
    }
    return rva;
}

string path = args[0];
string nsFilter = args.Length > 1 ? args[1] : "";
string typeFilter = args.Length > 2 ? args[2] : "";

using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
if (!pe.HasMetadata)
{
    Console.Error.WriteLine($"{path}: no managed metadata");
    return 1;
}

MetadataReader r = pe.GetMetadataReader();

Console.WriteLine($"### {Path.GetFileName(path)}  ({r.TypeDefinitions.Count} types)");

foreach (TypeDefinitionHandle tdh in r.TypeDefinitions)
{
    TypeDefinition td = r.GetTypeDefinition(tdh);
    string ns = r.GetString(td.Namespace);
    string name = r.GetString(td.Name);
    if (name == "<Module>") continue;
    if (nsFilter.Length > 0 && !ns.Contains(nsFilter, StringComparison.OrdinalIgnoreCase)) continue;
    if (typeFilter.Length > 0 && !name.Contains(typeFilter, StringComparison.OrdinalIgnoreCase)) continue;

    var flags = td.Attributes;
    string baseName = td.BaseType.IsNil ? "" : TypeRefName(r, td.BaseType);
    bool isEnum = baseName.EndsWith(".Enum");
    bool isDelegate = baseName.EndsWith(".MulticastDelegate") || baseName.EndsWith(".Delegate");
    string kind = (flags & TypeAttributes.Interface) != 0 ? "interface"
                : isEnum ? "enum"
                : isDelegate ? "delegate"
                : (baseName.EndsWith(".ValueType")) ? "struct"
                : "class";
    string vis = (flags & TypeAttributes.VisibilityMask) switch
    {
        TypeAttributes.Public => "public",
        TypeAttributes.NotPublic => "internal",
        TypeAttributes.NestedPublic => "public(nested)",
        TypeAttributes.NestedPrivate => "private(nested)",
        _ => "nested"
    };

    string guid = "";
    foreach (CustomAttributeHandle cah in td.GetCustomAttributes())
    {
        CustomAttribute ca = r.GetCustomAttribute(cah);
        string an = AttrName(r, ca);
        if (an == "GuidAttribute") guid = " [" + DecodeSerString(r, ca.Value) + "]";
    }

    string bases = "";
    if (!td.BaseType.IsNil) bases = " : " + TypeRefName(r, td.BaseType);
    var ifaces = new List<string>();
    foreach (InterfaceImplementationHandle ih in td.GetInterfaceImplementations())
    {
        InterfaceImplementation ii = r.GetInterfaceImplementation(ih);
        ifaces.Add(TypeRefName(r, ii.Interface));
    }
    if (ifaces.Count > 0) bases += (bases.Length > 0 ? ", " : " : ") + string.Join(", ", ifaces);

    Console.WriteLine($"\n=== {vis} {kind} {ns}.{name}{guid}{bases}");

    // enum members with values
    if (isEnum)
    {
        foreach (FieldDefinitionHandle fdh in td.GetFields())
        {
            FieldDefinition fd = r.GetFieldDefinition(fdh);
            if ((fd.Attributes & FieldAttributes.Static) == 0) continue;
            string fname = r.GetString(fd.Name);
            if (fname == "value__") continue;
            string val = "";
            ConstantHandle ch = fd.GetDefaultValue();
            if (!ch.IsNil)
            {
                Constant c = r.GetConstant(ch);
                var br = r.GetBlobReader(c.Value);
                try
                {
                    val = c.TypeCode switch
                    {
                        ConstantTypeCode.Int32 => br.ReadInt32().ToString(),
                        ConstantTypeCode.Byte => br.ReadByte().ToString(),
                        ConstantTypeCode.UInt32 => br.ReadUInt32().ToString(),
                        ConstantTypeCode.String => "\"" + br.ReadSerializedString() + "\"",
                        _ => "?"
                    };
                }
                catch { val = "?"; }
            }
            Console.WriteLine($"      {fname} = {val}");
        }
        continue;
    }

    foreach (MethodDefinitionHandle mdh in td.GetMethods())
    {
        MethodDefinition md = r.GetMethodDefinition(mdh);
        string mname = r.GetString(md.Name);
        if (mname.StartsWith("get_") || mname.StartsWith("set_") || mname.StartsWith("add_") || mname.StartsWith("remove_")) continue;
        if (mname == ".ctor" || mname == ".cctor") continue;
        var ps = new List<string>();
        foreach (ParameterHandle ph in md.GetParameters())
        {
            Parameter p = r.GetParameter(ph);
            if (p.SequenceNumber == 0) continue;
            ps.Add(SigType(r, p.Name.IsNil ? "" : r.GetString(p.Name)));
        }
        Console.WriteLine($"    {SigType(r, mname)}({string.Join(", ", ps)})");
    }

    foreach (PropertyDefinitionHandle pdh in td.GetProperties())
    {
        PropertyDefinition pd = r.GetPropertyDefinition(pdh);
        Console.WriteLine($"    prop {r.GetString(pd.Name)}");
    }

    foreach (FieldDefinitionHandle fdh in td.GetFields())
    {
        FieldDefinition fd = r.GetFieldDefinition(fdh);
        if ((fd.Attributes & FieldAttributes.Static) == 0) continue;
        string fname = r.GetString(fd.Name);
        if (fname.Contains("k__BackingField")) continue;
        string val = "";
        ConstantHandle ch = fd.GetDefaultValue();
        if (!ch.IsNil)
        {
            Constant c = r.GetConstant(ch);
            var br = r.GetBlobReader(c.Value);
            try
            {
                if (c.TypeCode == ConstantTypeCode.String) val = " = \"" + br.ReadSerializedString() + "\"";
                else if (c.TypeCode == ConstantTypeCode.Int32) val = " = " + br.ReadInt32();
            }
            catch { }
        }
        Console.WriteLine($"    const {fname}{val}");
    }
}

return 0;

static string SigType(MetadataReader r, string name) => name;

static string AttrName(MetadataReader r, CustomAttribute ca)
{
    try
    {
        if (ca.Constructor.Kind == HandleKind.MemberReference)
        {
            MemberReference mr = r.GetMemberReference((MemberReferenceHandle)ca.Constructor);
            return r.GetString(mr.Name);
        }
        if (ca.Constructor.Kind == HandleKind.MethodDefinition)
        {
            MethodDefinition md = r.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor);
            return r.GetString(md.Name);
        }
    }
    catch { }
    return "?";
}

static string DecodeSerString(MetadataReader r, BlobHandle bh)
{
    try
    {
        BlobReader br = r.GetBlobReader(bh);
        br.ReadUInt16(); // prolog
        return br.ReadSerializedString() ?? "";
    }
    catch { return "?"; }
}

static string TypeRefName(MetadataReader r, EntityHandle h)
{
    try
    {
        switch (h.Kind)
        {
            case HandleKind.TypeReference:
                TypeReference tr = r.GetTypeReference((TypeReferenceHandle)h);
                return r.GetString(tr.Namespace) + "." + r.GetString(tr.Name);
            case HandleKind.TypeDefinition:
                TypeDefinition td = r.GetTypeDefinition((TypeDefinitionHandle)h);
                return r.GetString(td.Namespace) + "." + r.GetString(td.Name);
            case HandleKind.TypeSpecification:
                return "typespec";
        }
    }
    catch { }
    return "?";
}
