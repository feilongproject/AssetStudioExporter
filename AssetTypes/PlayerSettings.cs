using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetStudioExporter.AssetTypes.Feature;
using System.Text.Json;

namespace AssetStudioExporter.AssetTypes;

/// <summary>
/// PlayerSettings（ClassID 129），位于globalgamemanagers中。
/// 字段数量庞大且随Unity版本变化，因此不逐字段读取，直接将整棵TypeTree导出为JSON。
/// </summary>
public class PlayerSettings : IAssetType, IAssetTypeReader<PlayerSettings>, IAssetTypeExporter
{
    public static AssetClassID AssetClassID { get; } = AssetClassID.PlayerSettings;

    /// <summary>
    /// classdata.tpk中2021.3的模板只到2021.3.10f1，而碧蓝档案PC端（Yostar启动器）使用2021.3.56f2（xLTS），
    /// 期间的Unity补丁为PlayerSettings新增了下列字段，导致旧模板解析时错位（GetBaseField抛EndOfStreamException）。
    /// 字段名与顺序对照 AssetRipper/TypeTreeDumps 的 release/2021.3.56f2.dump。
    /// </summary>
    static readonly (string Anchor, string Name, string Type, AssetValueType ValueType)[] LatePatch2021_3Fields =
    {
        ("m_ActiveColorSpace",               "unsupportedMSAAFallback",      "int",  AssetValueType.Int32),
        ("androidFullscreenMode",            "androidAutoRotationBehavior",  "int",  AssetValueType.Int32),
        ("androidAutoRotationBehavior",      "androidPredictiveBackSupport", "bool", AssetValueType.Bool),
        ("switchNVNMaxPublicSamplerIDCount", "switchMaxWorkerMultiple",      "int",  AssetValueType.Int32),
        ("activeInputHandler",               "windowsGamepadBackendHint",    "int",  AssetValueType.Int32),
    };

    readonly AssetTypeValueField value;

    public PlayerSettings(AssetTypeValueField value)
    {
        this.value = value;
    }

    public static PlayerSettings Read(AssetTypeValueField value, UnityVersion version)
    {
        return new PlayerSettings(value);
    }

    /// <summary>
    /// 读取PlayerSettings的完整TypeTree。
    /// 不能直接用<see cref="AssetsManager.GetBaseField"/>：模板过旧时它会解析错位并抛异常，
    /// 这里自行解析并校验是否恰好消耗资产的全部字节，失败则按已知版本差异补丁模板后重试。
    /// </summary>
    public static AssetTypeValueField ReadBaseField(AssetsManager am, AssetsFileInstance inst, AssetFileInfo info)
    {
        var db = am.ClassDatabase
            ?? throw new InvalidOperationException("ClassDatabase not loaded, call LoadClassDatabaseFromPackage first");
        var clsType = db.Classes.FirstOrDefault(c => c.ClassId == info.TypeId)
            ?? throw new NotSupportedException($"No PlayerSettings definition in class database for unity {inst.file.Metadata.UnityVersion}");

        var template = new AssetTypeTemplateField();
        template.FromClassDatabase(db, clsType, false);

        var start = inst.file.Header.DataOffset + info.ByteOffset;
        var end = start + info.ByteSize;
        var reader = inst.file.Reader;

        if (TryMakeValue(template, reader, start, end, out var value))
        {
            return value;
        }

        PatchTemplate(template);
        if (TryMakeValue(template, reader, start, end, out value))
        {
            return value;
        }

        throw new NotSupportedException(
            $"PlayerSettings layout mismatch (unity {inst.file.Metadata.UnityVersion}, {info.ByteSize} bytes), please update classdata.tpk");
    }

    static bool TryMakeValue(AssetTypeTemplateField template, AssetsFileReader reader, long start, long end, out AssetTypeValueField value)
    {
        value = null!;
        try
        {
            value = template.MakeValue(reader, start, null!);
            // 必须恰好读完整个资产，否则说明模板与数据布局不符
            return reader.Position == end;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 按已知的Unity后期补丁差异，在模板中插入缺失的字段
    /// </summary>
    static void PatchTemplate(AssetTypeTemplateField template)
    {
        foreach (var (anchor, name, type, valueType) in LatePatch2021_3Fields)
        {
            var children = template.Children;
            if (children.Any(c => c.Name == name))
            {
                continue;
            }
            var index = children.FindIndex(c => c.Name == anchor);
            if (index < 0)
            {
                continue;
            }
            children.Insert(index + 1, new AssetTypeTemplateField
            {
                Name = name,
                Type = type,
                ValueType = valueType,
                IsArray = false,
                IsAligned = false,
                HasValue = true,
                Children = new List<AssetTypeTemplateField>(),
            });
        }
    }

    public string GetFileExtension(string name)
    {
        return ".playerSettings.json";
    }

    public bool Export(AssetsFileInstance assetsFile, Stream stream)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        WriteValue(writer, value);
        writer.Flush();
        return true;
    }

    static void WriteValue(Utf8JsonWriter writer, AssetTypeValueField field)
    {
        if (field.IsDummy)
        {
            writer.WriteNullValue();
            return;
        }

        var value = field.Value;

        // vector等数组字段外面包了一层名为"Array"的节点，为JSON可读性直接展开
        if ((value is null || value.ValueType == AssetValueType.None) &&
            field.Children.Count == 1 &&
            field.Children[0].FieldName == "Array" &&
            field.Children[0].Value?.ValueType == AssetValueType.Array)
        {
            WriteArray(writer, field.Children[0]);
            return;
        }

        if (value is null)
        {
            WriteObject(writer, field);
            return;
        }

        switch (value.ValueType)
        {
            case AssetValueType.Bool:
                writer.WriteBooleanValue(value.AsBool);
                break;
            case AssetValueType.Int8:
                writer.WriteNumberValue(value.AsSByte);
                break;
            case AssetValueType.UInt8:
                writer.WriteNumberValue(value.AsByte);
                break;
            case AssetValueType.Int16:
                writer.WriteNumberValue(value.AsShort);
                break;
            case AssetValueType.UInt16:
                writer.WriteNumberValue(value.AsUShort);
                break;
            case AssetValueType.Int32:
                writer.WriteNumberValue(value.AsInt);
                break;
            case AssetValueType.UInt32:
                writer.WriteNumberValue(value.AsUInt);
                break;
            case AssetValueType.Int64:
                writer.WriteNumberValue(value.AsLong);
                break;
            case AssetValueType.UInt64:
                writer.WriteNumberValue(value.AsULong);
                break;
            case AssetValueType.Float:
                var f = value.AsFloat;
                // NaN/Infinity不是合法的JSON数字，转为字符串
                if (float.IsFinite(f)) writer.WriteNumberValue(f);
                else writer.WriteStringValue(f.ToString());
                break;
            case AssetValueType.Double:
                var d = value.AsDouble;
                if (double.IsFinite(d)) writer.WriteNumberValue(d);
                else writer.WriteStringValue(d.ToString());
                break;
            case AssetValueType.String:
                writer.WriteStringValue(value.AsString);
                break;
            case AssetValueType.Array:
                WriteArray(writer, field);
                break;
            case AssetValueType.ByteArray:
                writer.WriteBase64StringValue(value.AsByteArray);
                break;
            default:
                WriteObject(writer, field);
                break;
        }
    }

    static void WriteArray(Utf8JsonWriter writer, AssetTypeValueField field)
    {
        writer.WriteStartArray();
        foreach (var child in field.Children)
        {
            WriteValue(writer, child);
        }
        writer.WriteEndArray();
    }

    static void WriteObject(Utf8JsonWriter writer, AssetTypeValueField field)
    {
        writer.WriteStartObject();
        foreach (var child in field.Children)
        {
            writer.WritePropertyName(child.FieldName);
            WriteValue(writer, child);
        }
        writer.WriteEndObject();
    }
}
