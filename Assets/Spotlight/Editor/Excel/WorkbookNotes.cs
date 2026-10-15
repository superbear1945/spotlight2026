using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Spotlight.EditorTools
{
    /// <summary>
    /// 默认工作簿的原生 Excel 批注写出器。输入：表结构与源表数据；输出：OOXML comments、关系及隐藏 VML 批注框。
    /// 说明不占数据行或新增工作表，因此不会影响严格的 Excel 读取和校验规则。
    /// </summary>
    internal static class WorkbookNotes
    {
        /// <summary>工作表主命名空间。</summary>
        static readonly XNamespace _sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        /// <summary>包关系命名空间。</summary>
        static readonly XNamespace _rels = "http://schemas.openxmlformats.org/package/2006/relationships";
        /// <summary>Office 关系类型前缀。</summary>
        const string _office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";

        /// <summary>收集单元格说明。输入：表；输出：单元格地址与中文说明，正式表头和数据布局不变。</summary>
        internal static Dictionary<string, string> Collect(DefaultWorkbookWriter.SheetSpec sheet)
        {
            var result = new Dictionary<string, string>();
            var table = TableSchema.For(sheet.Name);
            for (var col = 0; col < sheet.Headers.Count; col++)
            {
                var description = table?.Find(sheet.Headers[col])?.Description;
                if (!string.IsNullOrEmpty(description)) result.Add(Column(col) + "1", description);
            }
            if (sheet.Name == "Cards")
            {
                var headers = sheet.Headers.ToList();
                var id = headers.IndexOf("id");
                var hp = headers.IndexOf("hp");
                if (id >= 0 && hp >= 0 && sheet.Rows != null)
                    for (var row = 0; row < sheet.Rows.Count; row++)
                        if (sheet.Rows[row].Count > id && sheet.Rows[row][id] == "bossSpawner")
                            result.Add(Column(hp) + (row + 2), "刷怪卡初始生命及生命上限，默认 1，可配置为正整数。每次部署创建满血单位；生命归零后停止生成，已生成的战机继续存在。");
            }
            return result;
        }

        /// <summary>写入原生批注部件。输入：ZIP、表序号和表；输出：批注及可被 Excel 显示的隐藏批注框，无说明时不写部件。</summary>
        internal static void Write(ZipArchive archive, int index, DefaultWorkbookWriter.SheetSpec sheet)
        {
            var notes = Collect(sheet);
            if (notes.Count == 0) return;
            var number = index + 1;
            Save(archive, "xl/comments" + number + ".xml", new XElement(_sheet + "comments",
                new XElement(_sheet + "authors", new XElement(_sheet + "author", "Spotlight")),
                new XElement(_sheet + "commentList", notes.Select(note => new XElement(_sheet + "comment",
                    new XAttribute("ref", note.Key), new XAttribute("authorId", 0),
                    new XElement(_sheet + "text", new XElement(_sheet + "t", note.Value)))))));
            Save(archive, "xl/worksheets/_rels/sheet" + number + ".xml.rels", new XElement(_rels + "Relationships",
                new XElement(_rels + "Relationship", new XAttribute("Id", "rIdNotes"), new XAttribute("Type", _office + "comments"), new XAttribute("Target", "../comments" + number + ".xml")),
                new XElement(_rels + "Relationship", new XAttribute("Id", "rIdNotesDrawing"), new XAttribute("Type", _office + "vmlDrawing"), new XAttribute("Target", "../drawings/notes" + number + ".vml"))));
            XNamespace v = "urn:schemas-microsoft-com:vml", o = "urn:schemas-microsoft-com:office", x = "urn:schemas-microsoft-com:office:excel";
            var drawing = new XElement("xml", new XAttribute(XNamespace.Xmlns + "v", v), new XAttribute(XNamespace.Xmlns + "o", o), new XAttribute(XNamespace.Xmlns + "x", x),
                new XElement(v + "shapetype", new XAttribute("id", "_x0000_t202"), new XAttribute("coordsize", "21600,21600"), new XAttribute(o + "spt", 202), new XAttribute("path", "m,l,21600r21600,l21600,xe"), new XElement(v + "stroke", new XAttribute("joinstyle", "miter")), new XElement(v + "path", new XAttribute(o + "connecttype", "rect"))));
            var shape = 1024;
            foreach (var note in notes)
            {
                var letters = new string(note.Key.TakeWhile(char.IsLetter).ToArray());
                var col = 0;
                foreach (var letter in letters) col = col * 26 + letter - 'A' + 1;
                var row = int.Parse(note.Key.Substring(letters.Length)) - 1;
                drawing.Add(new XElement(v + "shape", new XAttribute("id", "_x0000_s" + ++shape), new XAttribute("type", "#_x0000_t202"),
                    new XAttribute("style", "position:absolute;width:320pt;height:120pt;visibility:hidden"), new XAttribute("fillcolor", "#ffffe1"),
                    new XElement(v + "textbox", new XElement("div", new XAttribute("style", "text-align:left"))),
                    new XElement(x + "ClientData", new XAttribute("ObjectType", "Note"), new XElement(x + "MoveWithCells"), new XElement(x + "SizeWithCells"),
                        new XElement(x + "AutoFill", "False"), new XElement(x + "Row", row), new XElement(x + "Column", col - 1))));
            }
            Save(archive, "xl/drawings/notes" + number + ".vml", drawing);
        }

        /// <summary>写入 XML ZIP 条目。输入：归档、路径和根元素；输出：带声明的 UTF-8 XML，统一释放流。</summary>
        static void Save(ZipArchive archive, string path, XElement root)
        {
            using (var stream = archive.CreateEntry(path).Open()) new XDocument(root).Save(stream);
        }

        /// <summary>换算列标签。输入：零基列号；输出：A、B、AA 等 Excel 地址前缀，以支持任意字段数量。</summary>
        static string Column(int index)
        {
            var result = "";
            for (var value = index + 1; value > 0; value = (value - 1) / 26) result = (char)('A' + (value - 1) % 26) + result;
            return result;
        }
    }
}
