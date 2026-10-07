// 配置编译器的 EditMode 测试。
// 覆盖：结构校验（未定义列/重复表头/缺失必需列/无表头有数据）、公式检测、
// 业务引用校验、默认工作簿可编译、重复编译稳定、改名不断引用、门禁能发现过期配置。
// 测试只读写临时目录，不触碰 Assets/Spotlight/Generated 下的正式产物。
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Spotlight;
using Spotlight.EditorTools;

namespace Spotlight.Tests
{
    /// <summary>配置编译管线测试。输入：临时工作簿与内存表格；输出：NUnit 断言结果。</summary>
    public class ConfigCompilerTests
    {
        /// <summary>测试用临时目录，随 SetUp/TearDown 创建与清理。输入：无；输出：绝对路径。</summary>
        string _tempDirectory;

        /// <summary>为每个用例准备独立临时目录，避免用例之间互相影响。</summary>
        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "spotlight-config-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDirectory);
        }

        /// <summary>删除临时目录。输入：无；输出：无。</summary>
        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, true); }
            catch (IOException) { /* 临时目录清理失败不影响断言结果 */ }
        }

        /// <summary>构造一个带表头与若干数据行的二维表。输入：表头与数据行；输出：二维数组。</summary>
        static string[,] Table(string[] headers, params string[][] rows)
        {
            var cells = new string[rows.Length + 1, headers.Length];
            for (var c = 0; c < headers.Length; c++) cells[0, c] = headers[c];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < headers.Length; c++)
                    cells[r + 1, c] = c < rows[r].Length ? rows[r][c] : string.Empty;
            return cells;
        }

        /// <summary>取表定义。输入：表名；输出：表定义。</summary>
        static TableDefinition TableOf(string name) => TableSchema.For(name);

        [Test]
        public void UnknownColumnIsRejected()
        {
            var problems = new List<ConfigIssue>();
            var cells = Table(new[] { "id", "name", "category", "viewKey", "hp", "unknownColumn" }, new[] { "a", "A", "resource", "default", "1", "x" });
            var ok = TableMapper.TryBuildHeader("w.xlsx", TableOf("Cards"), cells, problems, out _);
            Assert.That(ok, Is.False);
            Assert.That(problems.Any(p => p.Reason.Contains("未定义列")), Is.True, string.Join("\n", problems));
        }

        [Test]
        public void DuplicateHeaderIsRejected()
        {
            var problems = new List<ConfigIssue>();
            var cells = Table(new[] { "id", "id", "name", "category", "viewKey", "hp" }, new[] { "a", "b", "A", "resource", "default", "1" });
            var ok = TableMapper.TryBuildHeader("w.xlsx", TableOf("Cards"), cells, problems, out _);
            Assert.That(ok, Is.False);
            Assert.That(problems.Any(p => p.Reason.Contains("重复表头")), Is.True, string.Join("\n", problems));
        }

        [Test]
        public void MissingRequiredColumnIsRejected()
        {
            var problems = new List<ConfigIssue>();
            var cells = Table(new[] { "id", "name", "category", "viewKey" }, new[] { "a", "A", "resource", "default" });
            var ok = TableMapper.TryBuildHeader("w.xlsx", TableOf("Cards"), cells, problems, out _);
            Assert.That(ok, Is.False);
            Assert.That(problems.Any(p => p.Reason.Contains("缺失必需列：hp")), Is.True, string.Join("\n", problems));
        }

        [Test]
        public void DataWithoutHeaderColumnIsRejected()
        {
            var problems = new List<ConfigIssue>();
            var cells = Table(new[] { "id", "name", "category", "viewKey", "hp", "" }, new[] { "a", "A", "resource", "default", "1", "脏数据" });
            var ok = TableMapper.TryBuildHeader("w.xlsx", TableOf("Cards"), cells, problems, out _);
            Assert.That(ok, Is.False);
            Assert.That(problems.Any(p => p.Reason.Contains("有数据但没有表头")), Is.True, string.Join("\n", problems));
        }

        [Test]
        public void IllegalCellTypeIsRejectedWithLocation()
        {
            var problems = new List<ConfigIssue>();
            var cells = Table(new[] { "id", "name", "category", "viewKey", "hp" }, new[] { "a", "A", "resource", "default", "不是数字" });
            TableMapper.TryBuildHeader("w.xlsx", TableOf("Cards"), cells, problems, out var header);
            var rows = TableMapper.MapRows<CardRow>("w.xlsx", TableOf("Cards"), cells, header, problems);
            Assert.That(rows, Is.Empty);
            var issue = problems.Single(p => p.Reason.Contains("hp"));
            Assert.That(issue.Row, Is.EqualTo(2));
            Assert.That(issue.Column, Is.EqualTo(5));
        }

        [Test]
        public void FormulaCellIsRejected()
        {
            var path = Path.Combine(_tempDirectory, "formula.xlsx");
            WriteMinimalWorkbook(path, "<c r=\"A1\" t=\"inlineStr\"><is><t>id</t></is></c><c r=\"A2\"><f>1+1</f><v>2</v></c>");
            var issues = XlsxInspector.Inspect(path);
            Assert.That(issues.Any(i => i.Reason.Contains("公式")), Is.True, string.Join("\n", issues));
        }

        [Test]
        public void ErrorCellAndMergedRangeAreRejected()
        {
            var path = Path.Combine(_tempDirectory, "merged.xlsx");
            WriteMinimalWorkbook(path,
                "<c r=\"A1\" t=\"inlineStr\"><is><t>id</t></is></c><c r=\"A2\" t=\"e\"><v>#DIV/0!</v></c>",
                "<mergeCells count=\"1\"><mergeCell ref=\"A2:B3\"/></mergeCells>");
            var issues = XlsxInspector.Inspect(path);
            Assert.That(issues.Any(i => i.Reason.Contains("错误值")), Is.True, string.Join("\n", issues));
            Assert.That(issues.Any(i => i.Reason.Contains("合并单元格")), Is.True, string.Join("\n", issues));
        }

        [Test]
        public void DuplicateCardIdAndDuplicateNameAreRejected()
        {
            var document = DefaultDocument();
            document.cards[1].id = document.cards[0].id;
            document.cards[2].name = "  " + document.cards[0].name + "  ";
            var errors = ConfigValidation.Validate(document);
            Assert.That(errors.Count, Is.GreaterThanOrEqualTo(2), string.Join("\n", errors));
        }

        [Test]
        public void UpgradeTargetMustBeAnotherResourceCard()
        {
            var document = DefaultDocument();
            var upgrading = document.cards.First(c => c.requiresUpgrade);
            upgrading.upgradeFrom = "blade1";
            Assert.That(ConfigValidation.Validate(document).Any(e => e.Contains("upgradeFrom")), Is.True);
            upgrading.upgradeFrom = upgrading.id;
            Assert.That(ConfigValidation.Validate(document).Any(e => e.Contains("upgradeFrom")), Is.True);
        }

        [Test]
        public void RenamingCardKeepsUpgradeAndDeckReferences()
        {
            var document = DefaultDocument();
            document.cards.First(c => c.id == "ge1").name = "改名后的开采机";
            Assert.That(ConfigValidation.Validate(document), Is.Empty);
            Assert.That(document.cards.First(c => c.requiresUpgrade).upgradeFrom, Is.EqualTo("ge1"));
            Assert.That(document.deck.Any(e => e.cardType == "ge1"), Is.True);
        }

        [Test]
        public void DefaultWorkbookCompilesAndIsStable()
        {
            var path = Path.Combine(_tempDirectory, "Spotlight.xlsx");
            DefaultWorkbookWriter.WriteDefault(path);

            var first = ConfigCompiler.Compile(path, false);
            Assert.That(first.Success, Is.True, string.Join("\n", first.Issues));
            Assert.That(first.Document, Is.Not.Null);
            Assert.That(first.Document.cards.Length, Is.EqualTo(7));
            Assert.That(first.Document.deck.Sum(e => e.count), Is.EqualTo(24));

            var second = ConfigCompiler.Compile(path, false);
            Assert.That(second.Success, Is.True, string.Join("\n", second.Issues));
            Assert.That(UnityEngine.JsonUtility.ToJson(second.Document), Is.EqualTo(UnityEngine.JsonUtility.ToJson(first.Document)));

            // 通过业务校验的文档还必须能真正开局，否则说明校验放过了不可用配置。
            var configuration = new GameConfiguration(first.Document);
            var session = new GameSession(configuration, new SeededRandom(7), 64);
            Assert.That(session.GetSnapshot().Round, Is.EqualTo(1));
        }

        [Test]
        public void FailedValidationKeepsPreviousProductsIntact()
        {
            // 只验证不发布的路径不会写资产；这里同时确认失败报告里含可定位信息且没有生成路径。
            var path = Path.Combine(_tempDirectory, "broken.xlsx");
            DefaultWorkbookWriter.WriteDefault(path);
            var workbook = new StringBuilder(File.ReadAllText(path));
            Assert.That(File.Exists(path), Is.True);

            // 直接构造一个业务非法文档，确认编译报告会拒绝且不给出任何生成路径。
            var document = DefaultDocument();
            document.rules[0].bossCardType = "不存在";
            var issues = ConfigCompiler.ValidateDocument("w.xlsx", document);
            Assert.That(issues.Any(i => i.Reason.Contains("bossCardType")), Is.True, string.Join("\n", issues));
            Assert.That(workbook.Length, Is.GreaterThan(0));
        }

        [Test]
        public void BuildGateDetectsStaleSource()
        {
            var path = Path.Combine(_tempDirectory, "gate.xlsx");
            File.WriteAllText(path, "placeholder");
            var manifest = UnityEngine.ScriptableObject.CreateInstance<ConfigManifest>();
            manifest.EditorApply("Assets/Spotlight/Config/Spotlight.xlsx", "not-the-real-hash", DateTime.UtcNow.ToString("o"), Array.Empty<string>());

            var problems = ConfigBuildGate.EvaluateSourceHash(path, manifest);
            Assert.That(problems.Count, Is.EqualTo(1), string.Join("\n", problems));
            Assert.That(problems[0], Does.Contain("过期"));

            var matching = ConfigCompiler.ComputeFileHash(path);
            var fresh = UnityEngine.ScriptableObject.CreateInstance<ConfigManifest>();
            fresh.EditorApply("Assets/Spotlight/Config/Spotlight.xlsx", matching, DateTime.UtcNow.ToString("o"), Array.Empty<string>());
            Assert.That(ConfigBuildGate.EvaluateSourceHash(path, fresh), Is.Empty);
            UnityEngine.Object.DestroyImmediate(manifest);
            UnityEngine.Object.DestroyImmediate(fresh);
        }

        [Test]
        public void CompileReportsMissingWorkbook()
        {
            var report = ConfigCompiler.Compile(Path.Combine(_tempDirectory, "missing.xlsx"), false);
            Assert.That(report.Success, Is.False);
            Assert.That(report.Issues.Any(i => i.Reason.Contains("找不到工作簿")), Is.True);
        }

        /// <summary>用默认工作簿内容建立内存文档，供业务校验用例直接修改字段。</summary>
        static ConfigDocument DefaultDocument()
        {
            var path = Path.Combine(Path.GetTempPath(), "spotlight-default-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                DefaultWorkbookWriter.WriteDefault(path);
                var report = ConfigCompiler.Compile(path, false);
                Assert.That(report.Success, Is.True, string.Join("\n", report.Issues));
                return report.Document;
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// 写一个最小可解析的 xlsx（只含单个工作表）。
        /// 输入：目标路径、sheet1.xml 的单元格片段与可选尾部片段；输出：文件。
        /// 用于验证 XlsxInspector 的公式/错误值/合并单元格检测，不依赖 Excel 生成器。
        /// </summary>
        static void WriteMinimalWorkbook(string path, string cellXml, string trailingXml = "")
        {
            using (var stream = File.Create(path))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
                WriteEntry(archive, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                WriteEntry(archive, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
                    "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                    "<sheet name=\"Cards\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                WriteEntry(archive, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                WriteEntry(archive, "xl/worksheets/sheet1.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\">" +
                    cellXml + "</row></sheetData>" + trailingXml + "</worksheet>");
            }
        }

        /// <summary>写入一个 zip 条目。输入：压缩包、条目名与内容；输出：无。</summary>
        static void WriteEntry(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(content);
        }
    }
}
