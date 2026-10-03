using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// 用法: metadump <dll> <typeFilter> [typeFilter...]
// 打印匹配类型的：是否值类型 / 基类 / 字段(名字+类型) / 属性(名字+类型+可读可写)
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: metadump <assembly.dll> <typeFilter> [typeFilter...]");
    return 2;
}

var path = args[0];
// 以 @ 开头的过滤项 = "成员过滤"：只看名字或类型里含该串的字段/属性（用来定位某个字段是属性还是字段）
var memberFilters = args.Skip(1).Where(a => a.StartsWith('@')).Select(a => a[1..].ToLowerInvariant()).ToArray();
var filters = args.Skip(1).Where(a => !a.StartsWith('@')).Select(s => s.ToLowerInvariant()).ToArray();

using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
var prov = new Prov(md);

string FullName(TypeDefinition td)
{
    var ns = md.GetString(td.Namespace);
    var name = md.GetString(td.Name);
    return ns.Length > 0 ? ns + "." + name : name;
}

string HandleName(EntityHandle h)
{
    switch (h.Kind)
    {
        case HandleKind.TypeDefinition:
            return FullName(md.GetTypeDefinition((TypeDefinitionHandle)h));
        case HandleKind.TypeReference:
        {
            var tr = md.GetTypeReference((TypeReferenceHandle)h);
            var ns = md.GetString(tr.Namespace);
            var n = md.GetString(tr.Name);
            return ns.Length > 0 ? ns + "." + n : n;
        }
        case HandleKind.TypeSpecification:
            return md.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(prov, null);
        default:
            return "<" + h.Kind + ">";
    }
}

int matched = 0;
foreach (var tdh in md.TypeDefinitions)
{
    var td = md.GetTypeDefinition(tdh);
    var full = FullName(td);
    if (filters.Length > 0 && !filters.Any(f => full.ToLowerInvariant().Contains(f))) continue;

    bool MemOk(string line) => memberFilters.Length == 0 || memberFilters.Any(m => line.ToLowerInvariant().Contains(m));

    var fieldLines = new List<string>();
    foreach (var fh in td.GetFields())
    {
        var fd = md.GetFieldDefinition(fh);
        var sig = fd.DecodeSignature(prov, null);
        var line = "    field: " + sig + " " + md.GetString(fd.Name) + "   attr=" + fd.Attributes;
        if (MemOk(line)) fieldLines.Add(line);
    }

    var propLines = new List<string>();
    foreach (var ph in td.GetProperties())
    {
        var pd = md.GetPropertyDefinition(ph);
        var sig = pd.DecodeSignature(prov, null);   // MethodSignature<TType>：属性用 ReturnType
        var acc = pd.GetAccessors();
        var getter = acc.Getter.IsNil ? "-" : md.GetMethodDefinition(acc.Getter).Attributes.ToString();
        var setter = acc.Setter.IsNil ? "-" : md.GetMethodDefinition(acc.Setter).Attributes.ToString();
        var line = "    prop : " + sig.ReturnType + " " + md.GetString(pd.Name) + "  get=" + getter + " set=" + setter;
        if (MemOk(line)) propLines.Add(line);
    }

    if (memberFilters.Length > 0 && fieldLines.Count == 0 && propLines.Count == 0) continue;

    matched++;
    var baseName = td.BaseType.IsNil ? "<none>" : HandleName(td.BaseType);
    var isValueType = baseName is "System.ValueType" or "System.Enum";
    Console.WriteLine("=== " + full + (isValueType ? "  [值类型]" : "  [引用类型]") + "  基类=" + baseName
                      + "  attr=" + td.Attributes);
    foreach (var l in fieldLines) Console.WriteLine(l);
    foreach (var l in propLines) Console.WriteLine(l);
}

Console.WriteLine("matched types = " + matched);
return 0;

sealed class Prov : ISignatureTypeProvider<string, object?>
{
    private readonly MetadataReader _md;
    public Prov(MetadataReader md) => _md = md;

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "IntPtr",
        PrimitiveTypeCode.UIntPtr => "UIntPtr",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => typeCode.ToString()
    };
    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var td = reader.GetTypeDefinition(handle);
        var ns = reader.GetString(td.Namespace);
        var n = reader.GetString(td.Name);
        return ns.Length > 0 ? ns + "." + n : n;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        var ns = reader.GetString(tr.Namespace);
        var n = reader.GetString(tr.Name);
        return ns.Length > 0 ? ns + "." + n : n;
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext,
        TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
