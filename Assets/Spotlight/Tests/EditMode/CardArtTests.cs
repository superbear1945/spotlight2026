// 卡图配置的回归测试。
// 为什么需要这些用例：卡图是“有则显示、无则回退纯色矩形”的软特性。
// 路径解析写错、SO 往返丢字段、或者卡视图没清图，表现上都只是“图没出来”，
// 编译与运行都不会报错，靠肉眼才能发现。因此这里把四类行为钉死：
// 1) cardArt / cardArtUnit 的写法规则（留空合法 / 纯文件名 / Assets/ 完整路径 / 各类非法写法）；
// 2) 两列的默认目录互不串线（手牌 CardArt、场地 Units）；
// 3) 默认工作簿确实带上了两列，且 SO 与表行之间能往返；
// 4) 卡视图按场景取图（手牌取 cardArt、场上取 cardArtUnit 并回退），以及“家”与解绑必须清掉卡图。
using System.Linq;
using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Spotlight.Tests
{
    /// <summary>
    /// 卡图链路测试：路径解析、默认工作簿列、SO 往返、卡视图应用与回退。
    /// 输入：无（自带夹具）；输出：NUnit 断言结果。
    /// </summary>
    public sealed class CardArtTests
    {
        /// <summary>
        /// 路径解析：留空（含 null 与纯空格）表示“没有卡图”，不是错误。
        /// 为什么单独测：调用方要靠“返回值 false + error null”判断回退成纯色，而不是当成失败。
        /// </summary>
        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Resolve_EmptyValue_MeansNoArt(string value)
        {
            var hasArt = CardArtResolver.TryResolve(value, out var assetPath, out var error);
            Assert.That(hasArt, Is.False);
            Assert.That(assetPath, Is.Null);
            Assert.That(error, Is.Null, "留空是合法状态，不能报错");
        }

        /// <summary>路径解析：只写文件名时落到默认卡图目录。</summary>
        [TestCase("ge1.png")]
        [TestCase("  ge1.png  ")]
        public void Resolve_BareFileName_UsesDefaultFolder(string value)
        {
            var hasArt = CardArtResolver.TryResolve(value, out var assetPath, out var error);
            Assert.That(hasArt, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(assetPath, Is.EqualTo(CardArtResolver.DefaultFolder + "/ge1.png"));
        }

        /// <summary>路径解析：扩展名大小写不敏感，但路径本身原样保留（不做大小写改写）。</summary>
        [Test]
        public void Resolve_UppercaseExtension_IsAccepted()
        {
            var hasArt = CardArtResolver.TryResolve("ge1.PNG", out var assetPath, out var error);
            Assert.That(hasArt, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(assetPath, Is.EqualTo(CardArtResolver.DefaultFolder + "/ge1.PNG"));
        }

        /// <summary>路径解析：以 Assets/ 开头的完整路径原样使用。</summary>
        [Test]
        public void Resolve_FullProjectPath_IsKeptAsIs()
        {
            const string full = "Assets/Imported/SpotlightArt/CardArt/ge1.png";
            var hasArt = CardArtResolver.TryResolve(full, out var assetPath, out var error);
            Assert.That(hasArt, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(assetPath, Is.EqualTo(full));
        }

        /// <summary>
        /// 路径解析：非法写法必须给出错误，而不是静默当成“没有卡图”。
        /// 静默回退会让策划以为图已经生效，是最难排查的一类问题。
        /// </summary>
        [TestCase("art/ge1.png")]          // 相对子目录：解析目标不确定
        [TestCase("ge1.jpg")]              // 非 png
        [TestCase("ge1")]                  // 缺少扩展名
        [TestCase("..\\ge1.png")]          // 反斜杠 + 跳出目录
        [TestCase("Assets/../ge1.png")]    // 跳出目录
        public void Resolve_InvalidValue_ReturnsError(string value)
        {
            var hasArt = CardArtResolver.TryResolve(value, out var assetPath, out var error);
            Assert.That(hasArt, Is.False);
            Assert.That(assetPath, Is.Null);
            Assert.That(error, Is.Not.Null.And.Not.Empty, "非法写法必须带错误说明");
        }

        /// <summary>
        /// 默认工作簿必须同时带 cardArt 与 cardArtUnit 两列，且全部默认行为空（默认没有卡图）。
        /// 为什么用真实读写工作簿：列的写法、表头与行宽必须真的能过一遍 Excel，
        /// 只在内存里造 CardRow 无法发现“表头拼错/行少一格”这类问题。
        /// </summary>
        [Test]
        public void DefaultWorkbook_CarriesEmptyCardArtColumns()
        {
            var path = WriteDefaultWorkbook();
            try
            {
                var read = WorkbookReader.Read(path);
                Assert.That(read.Issues, Is.Empty, string.Join("\n", read.Issues.Select(i => i.Reason)));
                var cards = read.Data.GetSheet("Cards");
                foreach (var columnName in new[] { "cardArt", "cardArtUnit" })
                {
                    var headerColumn = -1;
                    for (var column = 0; column < cards.GetLength(1); column++)
                        if (cards[0, column] == columnName) headerColumn = column;
                    Assert.That(headerColumn, Is.GreaterThanOrEqualTo(0), "Cards 表头缺少 " + columnName + " 列");
                    for (var row = 1; row < cards.GetLength(0); row++)
                    {
                        if (string.IsNullOrWhiteSpace(cards[row, 0])) continue;
                        Assert.That(cards[row, headerColumn] ?? string.Empty, Is.Empty,
                            $"第 {row + 1} 行的 {columnName} 默认必须留空");
                    }
                }
            }
            finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        }

        /// <summary>默认工作簿（两列卡图都为空）必须整体通过校验。</summary>
        [Test]
        public void DefaultWorkbook_WithEmptyCardArt_IsValid()
        {
            var document = CompileDefaultDocument();
            Assert.That(ConfigValidation.Validate(document), Is.Empty);
            Assert.That(document.cards.All(c => string.IsNullOrEmpty(c.cardArt)), Is.True);
            Assert.That(document.cards.All(c => string.IsNullOrEmpty(c.cardArtUnit)), Is.True);
        }

        /// <summary>合法写法（纯文件名 / Assets/ 完整路径）必须通过业务校验。</summary>
        [TestCase("ge1.png")]
        [TestCase("Assets/Imported/SpotlightArt/CardArt/ge1.png")]
        public void Validate_AcceptsSupportedCardArtFormats(string value)
        {
            var document = CompileDefaultDocument();
            document.cards[0].cardArt = value;
            Assert.That(ConfigValidation.Validate(document), Is.Empty);
        }

        /// <summary>非法写法必须被 ConfigValidation 拦下，并指明是哪一列的错。</summary>
        [TestCase("ge1.jpg")]
        [TestCase("art/ge1.png")]
        [TestCase("art\\ge1.png")]
        [TestCase("Assets/../ge1.png")]
        public void Validate_RejectsMalformedCardArt(string value)
        {
            var document = CompileDefaultDocument();
            document.cards[0].cardArt = value;
            var errors = ConfigValidation.Validate(document);
            Assert.That(errors.Any(e => e.Contains("cardArt")), Is.True, "错误信息必须点出 cardArt 列：" + string.Join(" | ", errors));
        }

        /// <summary>
        /// SO 与表行之间必须能往返 cardArt。
        /// 为什么重要：发布器用 EditorApply 写 SO，重新编译时又从 SO 读回行对象做校验；
        /// 一旦往返丢字段，第二次编译就会把卡图当成没配置，界面静默变回纯色矩形。
        /// </summary>
        [Test]
        public void Definition_RoundTripsCardArtPath()
        {
            var definition = ScriptableObject.CreateInstance<CardDefinitionSO>();
            try
            {
                var row = new CardRow
                {
                    id = "ge1", name = "GE-1开采机", category = "resource", tier = "basic",
                    viewKey = "default", hp = 1, cardArt = "ge1.png"
                };
                // 故意不传 artPath：来源文本必须能从行里的 cardArt 自动落到 SO 上。
                definition.EditorApply(row, null, null, null, null, null);
                Assert.That(definition.ArtPath, Is.EqualTo("ge1.png"));
                Assert.That(definition.Art, Is.Null, "没有传入 Sprite 时 Art 必须为 null（回退纯色）");
                Assert.That(definition.ToCardRow().cardArt, Is.EqualTo("ge1.png"));
            }
            finally { UnityEngine.Object.DestroyImmediate(definition); }
        }

        /// <summary>
        /// 路径解析：场地列只写文件名时落到场地卡图目录（Units），不会串到手牌目录。
        /// 为什么单独测：两列的解析规则相同但默认目录不同，
        /// 一旦有人把 Units 的图按 CardArt 的规则解析，场上的图会静默变成手牌图。
        /// </summary>
        [Test]
        public void Resolve_UnitBareFileName_UsesUnitFolder()
        {
            Assert.That(CardArtResolver.UnitFolder, Is.EqualTo("Assets/Imported/SpotlightArt/Units"),
                "场地卡图目录是策划约定的落图位置，改动必须同步 Units/README.md");
            var hasArt = CardArtResolver.TryResolve("ge1.png", CardArtResolver.UnitFolder, out var assetPath, out var error);
            Assert.That(hasArt, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(assetPath, Is.EqualTo(CardArtResolver.UnitFolder + "/ge1.png"));
        }

        /// <summary>场地列的合法写法（纯文件名 / Assets/ 完整路径）必须通过业务校验。</summary>
        [TestCase("ge1.png")]
        [TestCase("Assets/Imported/SpotlightArt/Units/ge1.png")]
        public void Validate_AcceptsSupportedUnitArtFormats(string value)
        {
            var document = CompileDefaultDocument();
            document.cards[0].cardArtUnit = value;
            Assert.That(ConfigValidation.Validate(document), Is.Empty);
        }

        /// <summary>场地列的非法写法必须被拦下，且错误信息点明是 cardArtUnit，而不是笼统的“卡图有问题”。</summary>
        [TestCase("Units/ge1.png")]
        [TestCase("ge1.jpg")]
        [TestCase("..\\ge1.png")]
        public void Validate_RejectsMalformedUnitArt(string value)
        {
            var document = CompileDefaultDocument();
            document.cards[0].cardArtUnit = value;
            var errors = ConfigValidation.Validate(document);
            Assert.That(errors.Any(e => e.Contains("cardArtUnit")), Is.True,
                "错误信息必须点出 cardArtUnit 列：" + string.Join(" | ", errors));
            // 只填场地列出错的用例里，手牌列是空的，不应被牵连报错。
            Assert.That(errors.Any(e => e.Contains("cardArt 不能") || e.Contains("cardArt 必须")), Is.False,
                "手牌列留空是合法状态，不能因为场地列写错而报手牌列：" + string.Join(" | ", errors));
        }

        /// <summary>
        /// SO 与表行之间必须能往返 cardArtUnit。
        /// 为什么重要：与 cardArt 同理，往返丢字段会让“场地图”在第二次编译时被当成没配置，
        /// 表现上只是“图又变回手牌图了”，没有任何报错。
        /// </summary>
        [Test]
        public void Definition_RoundTripsUnitArtPath()
        {
            var definition = ScriptableObject.CreateInstance<CardDefinitionSO>();
            try
            {
                var row = new CardRow
                {
                    id = "ge1", name = "GE-1开采机", category = "resource", tier = "basic",
                    viewKey = "default", hp = 1, cardArt = "ge1.png", cardArtUnit = "ge1.png"
                };
                // 故意不传 unitArtPath：来源文本必须能从行里的 cardArtUnit 自动落到 SO 上。
                definition.EditorApply(row, null, null, null, null, null);
                Assert.That(definition.UnitArtPath, Is.EqualTo("ge1.png"));
                Assert.That(definition.UnitArt, Is.Null, "没有传入场地 Sprite 时 UnitArt 必须为 null");
                Assert.That(definition.ToCardRow().cardArtUnit, Is.EqualTo("ge1.png"));
            }
            finally { UnityEngine.Object.DestroyImmediate(definition); }
        }

        /// <summary>
        /// 场上取图规则：优先场地卡图，没配就沿用手牌卡图，两列都空时没有卡图。
        /// 为什么把回退钉在 BoardArt 上：视图、测试与将来的其它场上显示都读它，
        /// 回退只实现一次才能保证“某张卡漏配场地图”不会变成纯色块。
        /// </summary>
        [Test]
        public void Definition_BoardArt_FallsBackToHandArt()
        {
            var texture = new Texture2D(4, 4);
            var handSprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f));
            var fieldSprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f));
            var definition = ScriptableObject.CreateInstance<CardDefinitionSO>();
            var row = new CardRow
            {
                id = "ge1", name = "GE-1开采机", category = "resource", tier = "basic",
                viewKey = "default", hp = 1, cardArt = "ge1.png"
            };
            try
            {
                definition.EditorApply(row, null, null, null, null, null, art: handSprite, artPath: "ge1.png");
                Assert.That(definition.BoardArt, Is.SameAs(handSprite), "只配手牌图时场上必须沿用同一张");

                definition.EditorApply(row, null, null, null, null, null,
                    art: handSprite, artPath: "ge1.png", unitArt: fieldSprite, unitArtPath: "ge1.png");
                Assert.That(definition.BoardArt, Is.SameAs(fieldSprite), "配了场地图时场上必须用场地图");
                Assert.That(definition.Art, Is.SameAs(handSprite), "场地图不能覆盖手牌图");

                definition.EditorApply(row, null, null, null, null, null);
                Assert.That(definition.BoardArt, Is.Null, "两列都留空时没有卡图");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(definition);
                UnityEngine.Object.DestroyImmediate(handSprite);
                UnityEngine.Object.DestroyImmediate(fieldSprite);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// 卡视图行为：手牌绑定时显示 cardArt，棋盘绑定时显示 cardArtUnit。
        /// 为什么必须测视图而不是只测 SO：手牌与棋盘共用同一个 Card 实例类型，
        /// 取图分支写在 BindHand / BindUnit 里，只测 SO 无法发现“两边都取了同一张”。
        /// </summary>
        [Test]
        public void Card_BindHand_UsesHandArt_AndBindUnit_UsesFieldArt()
        {
            var texture = new Texture2D(4, 4);
            var handSprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f));
            var fieldSprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f));
            var definition = ScriptableObject.CreateInstance<CardDefinitionSO>();
            var cardObject = new GameObject("CardArtModeTest", typeof(RectTransform), typeof(Image), typeof(Card));
            var artObject = new GameObject("Art", typeof(RectTransform), typeof(Image));
            var row = new CardRow
            {
                id = "ge1", name = "GE-1开采机", category = "resource", tier = "basic",
                viewKey = "default", hp = 1, cardArt = "ge1.png", cardArtUnit = "ge1.png"
            };
            try
            {
                artObject.transform.SetParent(cardObject.transform, false);
                var artImage = artObject.GetComponent<Image>();
                var card = cardObject.GetComponent<Card>();
                var serialized = new SerializedObject(card);
                serialized.FindProperty("_art").objectReferenceValue = artImage;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                definition.EditorApply(row, null, null, null, null, null,
                    art: handSprite, artPath: "ge1.png", unitArt: fieldSprite, unitArtPath: "ge1.png");

                card.BindHand(definition, new CardRecord(1, "ge1", Side.Player));
                Assert.That(artImage.sprite, Is.SameAs(handSprite), "手牌必须显示手牌卡图");
                Assert.That(artImage.enabled, Is.True);

                card.BindUnit(definition, new UnitState());
                Assert.That(artImage.sprite, Is.SameAs(fieldSprite), "棋盘单位必须显示场地卡图");
                Assert.That(artImage.enabled, Is.True);

                // 同一个 Card 实例从棋盘回到手牌，必须换回手牌图，不能残留场地卡图。
                card.BindHand(definition, new CardRecord(2, "ge1", Side.Player));
                Assert.That(artImage.sprite, Is.SameAs(handSprite), "复用的实例回到手牌时必须换回手牌卡图");

                // 漏配场地图的卡种：场上回退手牌图，而不是变成纯色块。
                definition.EditorApply(row, null, null, null, null, null, art: handSprite, artPath: "ge1.png");
                card.BindUnit(definition, new UnitState());
                Assert.That(artImage.sprite, Is.SameAs(handSprite), "没有场地图时场上必须回退手牌图");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cardObject);
                UnityEngine.Object.DestroyImmediate(definition);
                UnityEngine.Object.DestroyImmediate(handSprite);
                UnityEngine.Object.DestroyImmediate(fieldSprite);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// 卡视图行为：有图时显示卡图；绑定为“家”或解绑时必须清掉卡图。
        /// 为什么需要：家与卡牌共用同一个 Card 模板，残留立绘会让“家”显示成某张卡。
        /// </summary>
        [Test]
        public void Card_AppliesArt_AndClearsForHomeAndUnbind()
        {
            var texture = new Texture2D(4, 4);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f));
            var definition = ScriptableObject.CreateInstance<CardDefinitionSO>();
            var cardObject = new GameObject("CardArtTest", typeof(RectTransform), typeof(Image), typeof(Card));
            var artObject = new GameObject("Art", typeof(RectTransform), typeof(Image));
            try
            {
                artObject.transform.SetParent(cardObject.transform, false);
                var artImage = artObject.GetComponent<Image>();
                var card = cardObject.GetComponent<Card>();
                // _art 是私有序列化字段，测试按编辑器生成器的做法注入引用。
                var serialized = new SerializedObject(card);
                serialized.FindProperty("_art").objectReferenceValue = artImage;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                var row = new CardRow
                {
                    id = "ge1", name = "GE-1开采机", category = "resource", tier = "basic",
                    viewKey = "default", hp = 1, cardArt = "ge1.png"
                };
                definition.EditorApply(row, null, null, null, null, null, art: sprite, artPath: "ge1.png");

                card.Configure(definition);
                Assert.That(artImage.sprite, Is.SameAs(sprite));
                Assert.That(artImage.enabled, Is.True);

                card.BindHome(Side.Player, null, Color.white);
                Assert.That(artImage.sprite, Is.Null, "家没有卡种定义，不能残留立绘");
                Assert.That(artImage.enabled, Is.False);

                card.Configure(definition);
                Assert.That(artImage.enabled, Is.True, "重新绑定卡种后卡图必须恢复");

                card.Configure(null);
                Assert.That(artImage.enabled, Is.False, "未注册卡种回退模板时必须关掉卡图");

                card.Configure(definition);
                card.Unbind();
                Assert.That(artImage.enabled, Is.False, "解绑后必须清掉卡图");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cardObject);
                UnityEngine.Object.DestroyImmediate(definition);
                UnityEngine.Object.DestroyImmediate(sprite);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>写出默认工作簿到临时文件。输入：无；输出：临时 xlsx 路径（调用方负责删除）。</summary>
        static string WriteDefaultWorkbook()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "spotlight-cardart-" + System.Guid.NewGuid().ToString("N") + ".xlsx");
            DefaultWorkbookWriter.WriteDefault(path);
            return path;
        }

        /// <summary>
        /// 用默认工作簿建立内存文档（只校验不发布资产）。
        /// 输入：无；输出：已通过校验的 ConfigDocument。
        /// </summary>
        static ConfigDocument CompileDefaultDocument()
        {
            var path = WriteDefaultWorkbook();
            try
            {
                var report = ConfigCompiler.Compile(path, false);
                Assert.That(report.Success, Is.True, string.Join("\n", report.Issues.Select(i => i.Reason)));
                return report.Document;
            }
            finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        }
    }
}
