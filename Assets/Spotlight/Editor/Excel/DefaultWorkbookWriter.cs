// 默认 Excel 工作簿生成器。
// 为什么需要：工程运行期配置来自 Assets/Spotlight/Config/Spotlight.xlsx，
// 仓库初始化或误删该文件后需要一条不依赖第三方写入库的恢复路径，
// 这里用 System.IO.Compression 与 System.Xml 直接拼装最小可用的 OOXML(xlsx) 包，
// 保证 ExcelDataReader 能按固定列名读取默认数值。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 默认工作簿生成器。
    /// 输入：目标 .xlsx 文件路径，以及可选的表结构 <see cref="SheetSpec"/> 列表；
    /// 输出：写出的 .xlsx 文件（一个 ZIP 包，内含 6 张工作表）。
    /// 为什么需要：让工程在没有任何 xlsx 资源时也能一键生成与 docs/玩法.md 一致的默认配置表，
    /// 表头固定，便于 ExcelDataReader 按列名解析；不依赖任何第三方写入库。
    /// </summary>
    public static class DefaultWorkbookWriter
    {
        /// <summary>SpreadsheetML 主命名空间。</summary>
        private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        /// <summary>Office 文档关系命名空间。</summary>
        private const string DocumentRelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        /// <summary>OOXML 包内容类型命名空间。</summary>
        private const string ContentTypeNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
        /// <summary>OOXML 包关系命名空间。</summary>
        private const string PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
        /// <summary>核心属性命名空间。</summary>
        private const string CorePropertiesNamespace = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        /// <summary>Dublin Core 命名空间。</summary>
        private const string DublinCoreNamespace = "http://purl.org/dc/elements/1.1/";
        /// <summary>Dublin Core 术语命名空间。</summary>
        private const string DublinCoreTermsNamespace = "http://purl.org/dc/terms/";
        /// <summary>XML Schema 实例命名空间。</summary>
        private const string XmlSchemaInstanceNamespace = "http://www.w3.org/2001/XMLSchema-instance";
        /// <summary>扩展属性命名空间。</summary>
        private const string ExtendedPropertiesNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
        /// <summary>扩展属性值类型命名空间。</summary>
        private const string DocPropsVTypesNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";

        /// <summary>Cards 表内容（首行为表头，其余为数据行，列以逗号分隔）。</summary>
        private const string CardsSheetText =
            "id,name,category,viewKey,hp,atk,range,moveDistance,produce,upkeep,resourceComponent,teleportComponent,permitsAttackDeploy,deployHomeOnly,avoidAdjacentSameType,requiresUpgrade,upgradeFrom\n" +
            "ge1,GE-1开采机,resource,default,1,0,0,0,1,0,true,false,false,false,false,false,\n" +
            "ge12,GE-12开采机,resource,default,1,1,1,0,1,0,true,false,false,false,false,true,ge1\n" +
            "blade1,尖刀1,attack,default,2,1,1,1,0,2,true,false,false,false,false,false,\n" +
            "rabbit2,灵兔2,attack,default,1,1,2,1,0,2,true,false,false,false,false,false,\n" +
            "odysseus1,奥德修斯-1,special,default,2,0,0,1,0,1,true,false,false,false,false,false,\n" +
            "gate,星门,special,default,2,0,0,1,0,4,true,true,true,true,true,false,\n" +
            "bossCombat,Boss敌兵,attack,default,3,1,1,1,0,0,false,false,false,false,false,false,";

        /// <summary>Deck 表内容（首行为表头，其余为数据行，列以逗号分隔）。</summary>
        private const string DeckSheetText =
            "cardType,count\n" +
            "ge1,4\n" +
            "ge12,4\n" +
            "blade1,4\n" +
            "rabbit2,4\n" +
            "odysseus1,4\n" +
            "gate,4";

        /// <summary>Rules 表内容（首行为表头，第二行为唯一数据行，列以逗号分隔）。</summary>
        private const string RulesSheetText =
            "rows,columns,homeRegionWidth,playerPlays,bossPlays,drawCount,handLimit,initialResources,bossRespawnDelay,playerSide,firstSide,bossCardType,playerControlsBoss,accumulateResources,summoningSickness,adjacentSwap,lineBlocking\n" +
            "5,10,2,5,1,5,5,0,5,left,player,bossCombat,false,true,true,true,false";

        /// <summary>Homes 表内容（首行为表头，其余为数据行，列以逗号分隔）。</summary>
        private const string HomesSheetText =
            "owner,hp,produce\n" +
            "player,10,2\n" +
            "boss,10,0";

        /// <summary>BossSlots 表内容（首行为表头，其余为数据行，列以逗号分隔）。</summary>
        private const string BossSlotsSheetText =
            "id,rowFraction,edgeOffset\n" +
            "a,0,1\n" +
            "b,0.5,1\n" +
            "c,1,1";

        /// <summary>Presentation 表内容（首行为表头，第二行为唯一数据行，列以逗号分隔）。</summary>
        private const string PresentationSheetText =
            "referenceWidth,referenceHeight,fontSize,logLimit,moveSeconds,feedbackSeconds,selectedScale,playerColor,bossColor,resourceColor,attackColor,specialColor,moveColor,attackTargetColor,swapColor,deployColor\n" +
            "1920,1080,18,200,0.2,0.12,1.05,#3366FF,#FF3333,#33AA66,#EE8833,#AA55FF,#33AAFF,#FF3333,#AA33FF,#33FF99";

        /// <summary>
        /// 生成完整默认工作簿。
        /// 输入：目标 .xlsx 文件路径；输出：无（结果写入文件）。
        /// 为什么需要：调用方只需给一个路径即可得到与玩法文档一致的全部默认数值，
        /// 无需自行组装六张表的表头与数据行。
        /// </summary>
        /// <param name="path">目标 .xlsx 文件路径，不能为空。</param>
        public static void WriteDefault(string path)
        {
            WriteWorkbook(path, BuildDefaultSheets());
        }

        /// <summary>
        /// 通用工作簿写出。
        /// 输入：目标 .xlsx 文件路径与表结构列表；输出：无（结果写入文件）。
        /// 为什么需要：把 OOXML 组包逻辑与具体默认数值解耦，
        /// 便于测试或后续工具用同一实现写出任意表结构。
        /// </summary>
        /// <param name="path">目标 .xlsx 文件路径，不能为空。</param>
        /// <param name="sheets">按顺序写出的工作表定义，每个元素对应一张工作表。</param>
        public static void WriteWorkbook(string path, IReadOnlyList<SheetSpec> sheets)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("工作簿路径不能为空。", nameof(path));
            }

            if (sheets == null)
            {
                throw new ArgumentNullException(nameof(sheets));
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(fileStream, ZipArchiveMode.Create))
            {
                WriteContentTypes(archive, sheets.Count);
                WritePackageRelationships(archive);
                WriteWorkbookPart(archive, sheets);
                WriteWorkbookRelationships(archive, sheets.Count);
                for (var index = 0; index < sheets.Count; index++)
                {
                    WriteWorksheetPart(archive, index, sheets[index]);
                }

                WriteCoreProperties(archive);
                WriteExtendedProperties(archive, sheets);
            }
        }

        /// <summary>
        /// 一张工作表的结构定义。
        /// 输入：表名、表头列名、数据行；输出：供 <see cref="WriteWorkbook"/> 写出。
        /// 为什么需要：把“表长什么样”和“表如何序列化”分开，
        /// 让默认数值与任意自定义表都能复用同一写出流程。
        /// </summary>
        public sealed class SheetSpec
        {
            /// <summary>工作表名（写入 workbook.xml 的 sheet name）。</summary>
            public string Name;
            /// <summary>第 1 行的表头列名，按列顺序排列。</summary>
            public IReadOnlyList<string> Headers;
            /// <summary>第 2 行起的数据行，每行按列顺序与表头一一对应，缺失或为空时写空字符串。</summary>
            public IReadOnlyList<IReadOnlyList<string>> Rows;
        }

        /// <summary>
        /// 构建默认工作簿的六张表定义。
        /// 输入：无；输出：顺序固定为 Cards、Deck、Rules、Homes、BossSlots、Presentation 的表定义列表。
        /// 为什么需要：把玩法文档中的默认数值集中在一处，避免写出流程里散落硬编码。
        /// </summary>
        private static IReadOnlyList<SheetSpec> BuildDefaultSheets()
        {
            return new[]
            {
                CreateSheetSpec("Cards", CardsSheetText),
                CreateSheetSpec("Deck", DeckSheetText),
                CreateSheetSpec("Rules", RulesSheetText),
                CreateSheetSpec("Homes", HomesSheetText),
                CreateSheetSpec("BossSlots", BossSlotsSheetText),
                CreateSheetSpec("Presentation", PresentationSheetText),
            };
        }

        /// <summary>
        /// 把“首行表头 + 后续数据行”的逗号分隔文本解析为表定义。
        /// 输入：表名与文本；输出：对应的 <see cref="SheetSpec"/>。
        /// 为什么需要：默认数值以贴近文档的纯文本常量书写，便于逐行比对，
        /// 由本函数统一转换为结构化行数据。
        /// </summary>
        /// <param name="name">工作表名。</param>
        /// <param name="text">首行为表头、其余行为数据行的文本，行内以逗号分隔列。</param>
        private static SheetSpec CreateSheetSpec(string name, string text)
        {
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var headers = lines.Length > 0 ? lines[0].Split(',') : Array.Empty<string>();
            var rows = new List<IReadOnlyList<string>>();
            for (var index = 1; index < lines.Length; index++)
            {
                rows.Add(lines[index].Split(','));
            }

            return new SheetSpec { Name = name, Headers = headers, Rows = rows };
        }

        /// <summary>
        /// 写出 [Content_Types].xml，声明包内各部件的内容类型。
        /// 输入：ZIP 包与工作表数量；输出：无（写入条目）。
        /// 为什么需要：OOXML 读取器（含 ExcelDataReader）靠它判断每个部件的类型，缺少会导致包无法解析。
        /// </summary>
        private static void WriteContentTypes(ZipArchive archive, int sheetCount)
        {
            WriteXmlEntry(archive, "[Content_Types].xml", writer =>
            {
                writer.WriteStartElement("Types", ContentTypeNamespace);

                WriteDefaultContentType(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                WriteDefaultContentType(writer, "xml", "application/xml");

                WriteOverrideContentType(
                    writer,
                    "/xl/workbook.xml",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");

                for (var index = 0; index < sheetCount; index++)
                {
                    WriteOverrideContentType(
                        writer,
                        "/xl/worksheets/sheet" + ToInvariant(index + 1) + ".xml",
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                }

                WriteOverrideContentType(
                    writer,
                    "/docProps/core.xml",
                    "application/vnd.openxmlformats-package.core-properties+xml");
                WriteOverrideContentType(
                    writer,
                    "/docProps/app.xml",
                    "application/vnd.openxmlformats-officedocument.extended-properties+xml");

                writer.WriteEndElement();
            });
        }

        /// <summary>
        /// 写出一个 Default 内容类型节点。
        /// 输入：XmlWriter、扩展名与内容类型；输出：无。
        /// 为什么需要：把重复的 Default 节点写出抽成一处，避免遗漏属性。
        /// </summary>
        private static void WriteDefaultContentType(XmlWriter writer, string extension, string contentType)
        {
            writer.WriteStartElement("Default", ContentTypeNamespace);
            writer.WriteAttributeString("Extension", extension);
            writer.WriteAttributeString("ContentType", contentType);
            writer.WriteEndElement();
        }

        /// <summary>
        /// 写出一个 Override 内容类型节点。
        /// 输入：XmlWriter、部件路径与内容类型；输出：无。
        /// 为什么需要：工作表等部件必须逐个声明类型，抽成一处保证路径写法一致。
        /// </summary>
        private static void WriteOverrideContentType(XmlWriter writer, string partName, string contentType)
        {
            writer.WriteStartElement("Override", ContentTypeNamespace);
            writer.WriteAttributeString("PartName", partName);
            writer.WriteAttributeString("ContentType", contentType);
            writer.WriteEndElement();
        }

        /// <summary>
        /// 写出 _rels/.rels，声明包级关系。
        /// 输入：ZIP 包；输出：无（写入条目）。
        /// 为什么需要：读取器从这里找到 workbook 与 docProps 部件，是包的入口关系表。
        /// </summary>
        private static void WritePackageRelationships(ZipArchive archive)
        {
            WriteXmlEntry(archive, "_rels/.rels", writer =>
            {
                writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
                WriteRelationship(
                    writer,
                    "rId1",
                    DocumentRelationshipNamespace + "/officeDocument",
                    "xl/workbook.xml");
                WriteRelationship(
                    writer,
                    "rId2",
                    "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties",
                    "docProps/core.xml");
                WriteRelationship(
                    writer,
                    "rId3",
                    DocumentRelationshipNamespace + "/extended-properties",
                    "docProps/app.xml");
                writer.WriteEndElement();
            });
        }

        /// <summary>
        /// 写出 xl/workbook.xml，列出全部工作表名与顺序。
        /// 输入：ZIP 包与表定义列表；输出：无（写入条目）。
        /// 为什么需要：设置工作表顺序（Cards、Deck、Rules、Homes、BossSlots、Presentation）与名称，
        /// 读取器按名称取表时依赖这里。
        /// </summary>
        private static void WriteWorkbookPart(ZipArchive archive, IReadOnlyList<SheetSpec> sheets)
        {
            WriteXmlEntry(archive, "xl/workbook.xml", writer =>
            {
                writer.WriteStartElement("workbook", SpreadsheetNamespace);
                writer.WriteAttributeString("xmlns", "r", null, DocumentRelationshipNamespace);
                writer.WriteStartElement("sheets", SpreadsheetNamespace);
                for (var index = 0; index < sheets.Count; index++)
                {
                    writer.WriteStartElement("sheet", SpreadsheetNamespace);
                    writer.WriteAttributeString("name", sheets[index].Name ?? string.Empty);
                    writer.WriteAttributeString("sheetId", ToInvariant(index + 1));
                    writer.WriteAttributeString("r", "id", DocumentRelationshipNamespace, "rId" + ToInvariant(index + 1));
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            });
        }

        /// <summary>
        /// 写出 xl/_rels/workbook.xml.rels，把 workbook 关联到各工作表部件。
        /// 输入：ZIP 包与工作表数量；输出：无（写入条目）。
        /// 为什么需要：workbook.xml 里的 r:id 必须能解析到实际部件，否则读取器找不到表内容。
        /// </summary>
        private static void WriteWorkbookRelationships(ZipArchive archive, int sheetCount)
        {
            WriteXmlEntry(archive, "xl/_rels/workbook.xml.rels", writer =>
            {
                writer.WriteStartElement("Relationships", PackageRelationshipNamespace);
                for (var index = 0; index < sheetCount; index++)
                {
                    WriteRelationship(
                        writer,
                        "rId" + ToInvariant(index + 1),
                        DocumentRelationshipNamespace + "/worksheet",
                        "worksheets/sheet" + ToInvariant(index + 1) + ".xml");
                }

                writer.WriteEndElement();
            });
        }

        /// <summary>
        /// 写出单张工作表部件，所有单元格使用内联字符串。
        /// 输入：ZIP 包、工作表从 0 开始的序号、表定义；输出：无（写入条目）。
        /// 为什么需要：内联字符串无需 sharedStrings.xml，也无需任何样式和公式，
        /// 能让包保持最小且被 ExcelDataReader 直接解析。
        /// </summary>
        private static void WriteWorksheetPart(ZipArchive archive, int sheetIndex, SheetSpec sheet)
        {
            var entryName = "xl/worksheets/sheet" + ToInvariant(sheetIndex + 1) + ".xml";
            WriteXmlEntry(archive, entryName, writer =>
            {
                writer.WriteStartElement("worksheet", SpreadsheetNamespace);
                writer.WriteStartElement("sheetData", SpreadsheetNamespace);

                WriteRow(writer, 1, sheet.Headers);
                var rows = sheet.Rows ?? Array.Empty<IReadOnlyList<string>>();
                for (var index = 0; index < rows.Count; index++)
                {
                    WriteRow(writer, index + 2, rows[index]);
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            });
        }

        /// <summary>
        /// 写出一行单元格，全部使用内联字符串（t="inlineStr"）。
        /// 输入：XmlWriter、1 开始的行号、该行按列顺序排列的取值；输出：无。
        /// 为什么需要：统一单元格格式，保证列名与列序和表头一致，且不写出任何公式。
        /// 空字符串会写成内容为空的 &lt;t/&gt;（t="inlineStr"），读取端可能将其视为 null 或空字符串，需要自行兜底。
        /// </summary>
        private static void WriteRow(XmlWriter writer, int rowNumber, IReadOnlyList<string> values)
        {
            writer.WriteStartElement("row", SpreadsheetNamespace);
            writer.WriteAttributeString("r", ToInvariant(rowNumber));

            var columnCount = values?.Count ?? 0;
            for (var column = 0; column < columnCount; column++)
            {
                writer.WriteStartElement("c", SpreadsheetNamespace);
                writer.WriteAttributeString("r", GetColumnName(column) + ToInvariant(rowNumber));
                writer.WriteAttributeString("t", "inlineStr");
                writer.WriteStartElement("is", SpreadsheetNamespace);
                writer.WriteStartElement("t", SpreadsheetNamespace);
                writer.WriteString(values[column] ?? string.Empty);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        /// <summary>
        /// 把 0 开始的列序号转换为 Excel 列名。
        /// 输入：0 开始的列序号（0 对应 A）；输出：对应列名（0→A、25→Z、26→AA）。
        /// 为什么需要：单元格引用 r="A1" 必须由列名拼出，硬编码列名无法适配任意表宽。
        /// </summary>
        private static string GetColumnName(int zeroBasedIndex)
        {
            var name = string.Empty;
            var remaining = zeroBasedIndex;
            while (true)
            {
                name = (char)('A' + (remaining % 26)) + name;
                remaining = (remaining / 26) - 1;
                if (remaining < 0)
                {
                    break;
                }
            }

            return name;
        }

        /// <summary>
        /// 写出 docProps/core.xml，记录文档标题与创建者等核心属性。
        /// 输入：ZIP 包；输出：无（写入条目）。
        /// 为什么需要：OOXML 包的标准组成部件，缺失时部分工具会提示文件损坏；
        /// 使用固定创建时间以保证输出可复现。
        /// </summary>
        private static void WriteCoreProperties(ZipArchive archive)
        {
            WriteXmlEntry(archive, "docProps/core.xml", writer =>
            {
                writer.WriteStartElement("cp", "coreProperties", CorePropertiesNamespace);
                writer.WriteElementString("dc", "title", DublinCoreNamespace, "Spotlight 默认配置工作簿");
                writer.WriteElementString(
                    "dc",
                    "creator",
                    DublinCoreNamespace,
                    "Spotlight.EditorTools.DefaultWorkbookWriter");
                writer.WriteStartElement("dcterms", "created", DublinCoreTermsNamespace);
                writer.WriteAttributeString("xsi", "type", XmlSchemaInstanceNamespace, "dcterms:W3CDTF");
                writer.WriteString("2024-01-01T00:00:00Z");
                writer.WriteEndElement();
                writer.WriteEndElement();
            });
        }

        /// <summary>
        /// 写出 docProps/app.xml，记录应用信息与工作表清单。
        /// 输入：ZIP 包与表定义列表；输出：无（写入条目）。
        /// 为什么需要：OOXML 包的标准组成部件，同时便于在资源管理器中预览表数量与表名。
        /// </summary>
        private static void WriteExtendedProperties(ZipArchive archive, IReadOnlyList<SheetSpec> sheets)
        {
            WriteXmlEntry(archive, "docProps/app.xml", writer =>
            {
                writer.WriteStartElement("Properties", ExtendedPropertiesNamespace);
                writer.WriteElementString(
                    "Application",
                    ExtendedPropertiesNamespace,
                    "Spotlight.EditorTools.DefaultWorkbookWriter");
                writer.WriteElementString("DocSecurity", ExtendedPropertiesNamespace, "0");
                writer.WriteElementString("ScaleCrop", ExtendedPropertiesNamespace, "false");

                writer.WriteStartElement("HeadingPairs", ExtendedPropertiesNamespace);
                writer.WriteStartElement("vt", "vector", DocPropsVTypesNamespace);
                writer.WriteAttributeString("size", "2");
                writer.WriteAttributeString("baseType", "variant");
                writer.WriteStartElement("vt", "variant", DocPropsVTypesNamespace);
                writer.WriteElementString("vt", "lpstr", DocPropsVTypesNamespace, "Worksheets");
                writer.WriteEndElement();
                writer.WriteStartElement("vt", "variant", DocPropsVTypesNamespace);
                writer.WriteElementString("vt", "i4", DocPropsVTypesNamespace, ToInvariant(sheets.Count));
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();

                writer.WriteStartElement("TitlesOfParts", ExtendedPropertiesNamespace);
                writer.WriteStartElement("vt", "vector", DocPropsVTypesNamespace);
                writer.WriteAttributeString("size", ToInvariant(sheets.Count));
                writer.WriteAttributeString("baseType", "lpstr");
                for (var index = 0; index < sheets.Count; index++)
                {
                    writer.WriteElementString(
                        "vt",
                        "lpstr",
                        DocPropsVTypesNamespace,
                        sheets[index].Name ?? string.Empty);
                }

                writer.WriteEndElement();
                writer.WriteEndElement();

                writer.WriteElementString("LinksUpToDate", ExtendedPropertiesNamespace, "false");
                writer.WriteElementString("SharedDoc", ExtendedPropertiesNamespace, "false");
                writer.WriteElementString("HyperlinksChanged", ExtendedPropertiesNamespace, "false");
                writer.WriteElementString("AppVersion", ExtendedPropertiesNamespace, "16.0300");
                writer.WriteEndElement();
            });
        }

        /// <summary>
        /// 写出一个关系（Relationship）节点。
        /// 输入：XmlWriter、关系 Id、关系类型与目标路径；输出：无。
        /// 为什么需要：包级与 workbook 级关系表结构相同，抽成一处避免属性写错。
        /// </summary>
        private static void WriteRelationship(XmlWriter writer, string id, string type, string target)
        {
            writer.WriteStartElement("Relationship", PackageRelationshipNamespace);
            writer.WriteAttributeString("Id", id);
            writer.WriteAttributeString("Type", type);
            writer.WriteAttributeString("Target", target);
            writer.WriteEndElement();
        }

        /// <summary>
        /// 在 ZIP 包内新建一个 XML 条目，并交给回调写入内容。
        /// 输入：ZIP 包、条目名、写 XML 的回调；输出：无。
        /// 为什么需要：统一处理条目创建、UTF-8 无 BOM 编码与 XML 声明，
        /// 让各部分只需关注自己的节点结构，同时保证字符串自动转义。
        /// </summary>
        private static void WriteXmlEntry(ZipArchive archive, string entryName, Action<XmlWriter> writeContent)
        {
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
            using (var entryStream = entry.Open())
            {
                var settings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false),
                    Indent = false,
                    OmitXmlDeclaration = false,
                };

                using (var writer = XmlWriter.Create(entryStream, settings))
                {
                    writer.WriteStartDocument();
                    writeContent(writer);
                    writer.WriteEndDocument();
                    writer.Flush();
                }
            }
        }

        /// <summary>
        /// 按不变区域性把整数转为字符串。
        /// 输入：整数；输出：与当前系统语言无关的十进制字符串。
        /// 为什么需要：工作表名、行号、列引用等必须是 ASCII 数字，避免受区域设置影响。
        /// </summary>
        private static string ToInvariant(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
