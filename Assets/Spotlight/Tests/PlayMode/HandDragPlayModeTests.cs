using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Spotlight.Tests
{
    /// <summary>
    /// 手牌拖动的帧循环回归。输入：真实演示场景和独立确定性配置；输出：射线、视图及规则快照断言。
    /// 所有对局变化经 Execute 或界面事件发生，不修改 GameSession 私有状态。
    /// </summary>
    public class HandDragPlayModeTests
    {
        GameBootstrap _bootstrap;
        HandView _hand;
        BoardView _board;
        GameConfiguration _configuration;

        /// <summary>
        /// 让本类用例加载场景后直接进入对局（跳过“携带选择”面板）。
        /// 输入：无；输出：无。
        /// 为什么需要：正式启动流程会先弹出携带选择模态面板（全屏遮罩会吃掉手牌上的射线），
        /// 而本类只验证拖动、命中与回位；选卡流程本身由 DeckSelectionPlayModeTests 覆盖。
        /// </summary>
        [OneTimeSetUp]
        public void SuppressDeckSelectionOnStart() => GameBootstrap.SuppressDeckSelectionOnStart = true;

        /// <summary>
        /// 恢复正式启动行为。输入：无；输出：无。
        /// 为什么需要：PlayMode 用例在同一进程里顺序执行，静态开关必须还原，否则会让后面的用例看不到选卡界面。
        /// </summary>
        [OneTimeTearDown]
        public void RestoreDeckSelectionOnStart() => GameBootstrap.SuppressDeckSelectionOnStart = false;

        /// <summary>加载场景并以固定种子创建五张手牌。输入：显示侧及出牌次数；输出：无 AI 干扰的真实 UI。</summary>
        IEnumerator Load(string side = "left", int plays = 20)
        {
            yield return SceneManager.LoadSceneAsync("Assets/Spotlight/Scenes/Spotlight.unity");
            yield return null;
            _bootstrap = Object.FindObjectOfType<GameBootstrap>();
            _hand = Object.FindObjectOfType<HandView>();
            _board = Object.FindObjectOfType<BoardView>();
            var d = JsonUtility.FromJson<ConfigDocument>(JsonUtility.ToJson(_bootstrap.CurrentAsset.ToDocument()));
            d.rules[0].firstSide = "player"; d.rules[0].playerSide = side;
            d.rules[0].playerControlsBoss = true; d.rules[0].playerPlays = plays;
            d.rules[0].drawCount = 5; d.rules[0].handLimit = 10; d.rules[0].homeRegionWidth = 2;
            d.rules[0].bossBombInterval = 1000; d.rules[0].bossSpawnerAttackThreshold = 1000;
            // 抽牌堆构成规则显式写进夹具：Deck 表已废除，抽牌堆由下面的 WithDeck 给定，
            // 但配置校验仍要求“每阶级至少一种卡”与三比例合法，写死可以不受 Excel 当前取值影响。
            d.rules[0].drawPileSize = 24; d.rules[0].tierRatioBasic = 70;
            d.rules[0].tierRatioIntermediate = 20; d.rules[0].tierRatioAdvanced = 10;
            d.rules[0].maxTypesBasic = 5; d.rules[0].maxTypesIntermediate = 3; d.rules[0].maxTypesAdvanced = 2;
            d.presentation[0].moveSeconds = 0.08f; d.presentation[0].feedbackSeconds = 0.02f;
            typeof(GameBootstrap).GetField("_seed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_bootstrap, 7);
            // 第二个参数 false：5 张的夹具牌堆不等于 drawPileSize（24），走测试夹具专供的豁免路径。
            _configuration = new GameConfiguration(d).WithDeck(
                new[]
                {
                    new DeckEntry("blade1", 2), new DeckEntry("ge1", 1),
                    new DeckEntry("ge12", 1), new DeckEntry("gate", 1)
                }, false);
            _bootstrap.RestartWith(_configuration);
            yield return null;
            Canvas.ForceUpdateCanvases();
        }

        /// <summary>按测试卡种取得真实手牌。输入：卡种 ID；输出：仍在手中的视图。</summary>
        Card Hand(string type = "blade1") => _hand.GetCard(_bootstrap.Session.GetSnapshot().Hand.First(c => c.TypeId == type).Id);

        /// <summary>取得规则计算的首个合法格。输入：手牌；输出：内部坐标，夹具错误时直接断言。</summary>
        Cell Target(Card card) => _bootstrap.Session.GetLegalActions(new Selection(SelectionKind.Hand, card.Record.Id)).Deploy.First();

        /// <summary>模拟一次普通左键点击。输入：按钮对象；输出：真实 Button 回调，供点选和格子点击回归。</summary>
        static void Click(GameObject go)
        {
            var data = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.Execute(go, data, ExecuteEvents.pointerClickHandler);
        }

        /// <summary>调用宿主生命周期或 HUD 事件。输入：方法及参数；输出：等价宿主通知，不触碰规则私有状态。</summary>
        void Notify(string method, params object[] args) => typeof(GameBootstrap)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_bootstrap, args);

        /// <summary>点选只显示范围，点击格子不得出牌，第二次点选取消。输入：真实 UI；输出：身份与手牌数不变。</summary>
        [UnityTest]
        public IEnumerator ClickSelectsButNeverDeploys()
        {
            yield return Load();
            var card = Hand(); var id = card.Record.Id; var target = Target(card);
            Click(card.gameObject);
            Assert.That(card.IsSelected, Is.True);
            Click(_board.CellRect(target).gameObject);
            Assert.That(_bootstrap.Session.GetSnapshot().Hand.Any(c => c.Id == id), Is.True);
            Assert.That(_bootstrap.Session.GetSnapshot().Units.Any(u => u.SourceCardId == id), Is.False);
            Click(card.gameObject); Click(card.gameObject);
            Assert.That(card.IsSelected, Is.False);
        }

        /// <summary>直接拖动选中并只部署一次。输入：标准手势；输出：墓地、单位、指针穿透及范围/描边同时正确。</summary>
        [UnityTest]
        public IEnumerator DirectDragHighlightsAndDeploysExactlyOnce()
        {
            yield return Load();
            var card = Hand(); var id = card.Record.Id; var target = Target(card);
            var before = _bootstrap.Session.GetSnapshot();
            var data = HandDragInput.Begin(card);
            Assert.That(card.IsSelected, Is.True);
            HandDragInput.Move(card, data, HandDragInput.Center(_board.CellRect(target)));
            Assert.That(_board.HoveredDeploymentCell, Is.EqualTo(target));
            Assert.That(_board.TryGetPointerCell(data, out var hit) && hit == target, Is.True);
            var outlineCanvas = _board.CellRect(target).Find("DeploymentHover").GetComponent<Canvas>();
            Assert.That(outlineCanvas.overrideSorting, Is.True);
            Assert.That(outlineCanvas.sortingOrder, Is.GreaterThan(_hand.GetComponentInParent<Canvas>().rootCanvas.sortingOrder),
                "当前格描边必须显示在大尺寸拖动卡面之上");
            var other = _bootstrap.Session.GetLegalActions(new Selection(SelectionKind.Hand, id)).Deploy.First(c => c != target);
            var highlight = (Image)typeof(BoardCellView).GetField("_highlight", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_board.CellRect(other).GetComponent<BoardCellView>());
            Assert.That(highlight.enabled, Is.True, "悬停不能清掉其它合法格的范围提示");
            HandDragInput.End(card, data);
            HandDragInput.End(card, data);
            var after = _bootstrap.Session.GetSnapshot();
            Assert.That(after.Hand.Count, Is.EqualTo(before.Hand.Count - 1));
            Assert.That(after.Graveyard.Count(c => c.Id == id), Is.EqualTo(1));
            Assert.That(after.Units.Count(u => u.SourceCardId == id && u.Position == target), Is.EqualTo(1));
            Assert.That(_board.HoveredDeploymentCell, Is.Null);
            yield return null;
            Assert.That(Object.FindObjectsOfType<HandCardDrag>().All(d => !d.IsDragging), Is.True);
        }

        /// <summary>非法格、格子间隙及屏幕外回位且保留选择。输入：三种无效落点；输出：排版和对局均恢复。</summary>
        [UnityTest]
        public IEnumerator InvalidDropsRestoreOriginalSlotAndKeepSelection()
        {
            yield return Load();
            var card = Hand(); var id = card.Record.Id;
            var parent = card.transform.parent; var index = card.transform.GetSiblingIndex();
            var original = card.transform.position;
            var occupied = _bootstrap.Session.GetSnapshot().Homes.First().Position;
            var a = _board.CellRect(new Cell(0, 0)); var b = _board.CellRect(new Cell(0, 1));
            var gap = (HandDragInput.Center(a) + HandDragInput.Center(b)) * 0.5f;
            foreach (var point in new[] { HandDragInput.Center(_board.CellRect(occupied)), gap, new Vector2(-100, -100) })
            {
                var data = HandDragInput.Begin(card);
                HandDragInput.Move(card, data, point);
                Assert.That(_board.HoveredDeploymentCell, Is.Null);
                HandDragInput.End(card, data);
                // 即使错误地收到 PointerClick，也不得取消选择或弃牌。
                ExecuteEvents.Execute(card.gameObject, data, ExecuteEvents.pointerClickHandler);
                yield return new WaitForSecondsRealtime(0.15f);
                Assert.That(card.transform.parent, Is.EqualTo(parent));
                Assert.That(card.transform.GetSiblingIndex(), Is.EqualTo(index));
                Assert.That(Vector3.Distance(card.transform.position, original), Is.LessThan(1f));
                Assert.That(card.IsSelected, Is.True);
                Assert.That(card.GetComponent<CanvasGroup>().blocksRaycasts, Is.True);
                Assert.That(_bootstrap.Session.GetSnapshot().Hand.Any(c => c.Id == id), Is.True);
                Assert.That(_bootstrap.Session.GetSnapshot().Graveyard, Is.Empty);
            }
        }

        /// <summary>回位补间中再次抓取仍能部署。输入：一次失败手势及立即重抓；输出：无重复占位与旧补间干扰。</summary>
        [UnityTest]
        public IEnumerator ReturningCardCanBeGrabbedAgain()
        {
            yield return Load();
            var card = Hand(); var id = card.Record.Id; var target = Target(card);
            var data = HandDragInput.Begin(card);
            HandDragInput.Move(card, data, HandDragInput.Center(_board.CellRect(new Cell(3, 5))));
            HandDragInput.End(card, data);
            yield return null;
            var grabbedPosition = card.transform.position;
            data = HandDragInput.Begin(card);
            Assert.That(Vector3.Distance(card.transform.position, grabbedPosition), Is.LessThan(0.5f), "回位中重抓不能跳回手牌");
            HandDragInput.Move(card, data, HandDragInput.Center(_board.CellRect(target)));
            HandDragInput.End(card, data);
            yield return new WaitForSecondsRealtime(0.15f);
            Assert.That(_bootstrap.Session.GetSnapshot().Units.Count(u => u.SourceCardId == id), Is.EqualTo(1));
            Assert.That(GameObject.Find("HandDragPlaceholder"), Is.Null);
        }

        /// <summary>覆盖升级和星门扩展必须使用规则给出的目标。输入：对应手牌；输出：升级占位可命中且扩展格可部署。</summary>
        [UnityTest]
        public IEnumerator UpgradeAndGateDeploymentUseRuleTargets()
        {
            yield return Load();
            var miner = Hand("ge1"); var target = new Cell(0, 0);
            HandDragInput.Deploy(miner, _board, target);
            yield return null;
            var upgraded = Hand("ge12"); var upgradeId = upgraded.Record.Id;
            var data = HandDragInput.Begin(upgraded);
            HandDragInput.Move(upgraded, data, HandDragInput.Center(_board.CellRect(target)));
            Assert.That(_board.HoveredDeploymentCell, Is.EqualTo(target));
            HandDragInput.End(upgraded, data);
            Assert.That(_bootstrap.Session.GetSnapshot().Units.Single(u => u.Position == target).SourceCardId, Is.EqualTo(upgradeId));
            yield return null;
            HandDragInput.Deploy(Hand("gate"), _board, new Cell(0, 1));
            yield return null;
            var fighter = Hand(); var fighterId = fighter.Record.Id;
            HandDragInput.Deploy(fighter, _board, new Cell(0, 2));
            Assert.That(_bootstrap.Session.GetSnapshot().Units.Single(u => u.SourceCardId == fighterId).Position, Is.EqualTo(new Cell(0, 2)));
        }

        /// <summary>悬停后目标被占用必须拒绝旧目标。输入：另一个公开部署命令；输出：原卡回位、不消耗第二次出牌。</summary>
        [UnityTest]
        public IEnumerator StaleTargetIsRecheckedOnRelease()
        {
            yield return Load();
            var card = Hand(); var id = card.Record.Id; var target = Target(card);
            var data = HandDragInput.Begin(card);
            HandDragInput.Move(card, data, HandDragInput.Center(_board.CellRect(target)));
            var other = _bootstrap.Session.GetSnapshot().Hand.First(c => c.TypeId == "blade1" && c.Id != id);
            Assert.That(_bootstrap.Session.Execute(new GameCommand(CommandKind.Deploy, Side.Player, other.Id, target)).Success, Is.True);
            HandDragInput.End(card, data);
            yield return new WaitForSecondsRealtime(0.15f);
            Assert.That(_bootstrap.Session.GetSnapshot().Hand.Any(c => c.Id == id), Is.True);
            Assert.That(_bootstrap.Session.GetSnapshot().Graveyard.Count, Is.EqualTo(1));
        }

        /// <summary>出牌次数用尽仍允许查看/拖动，但没有合法落点。输入：最后一次出牌后再拖；输出：回位且规则不变。</summary>
        [UnityTest]
        public IEnumerator ExhaustedPlaysCannotDeploy()
        {
            yield return Load(plays: 1);
            var first = Hand(); HandDragInput.Deploy(first, _board, Target(first));
            yield return null;
            var card = Hand(); var id = card.Record.Id;
            var data = HandDragInput.Begin(card);
            HandDragInput.Move(card, data, HandDragInput.Center(_board.CellRect(new Cell(4, 0))));
            HandDragInput.End(card, data);
            yield return new WaitForSecondsRealtime(0.15f);
            Assert.That(_bootstrap.Session.GetSnapshot().Hand.Any(c => c.Id == id), Is.True);
            Assert.That(_bootstrap.Session.GetSnapshot().Graveyard.Count, Is.EqualTo(1));
            Assert.That(_board.HoveredDeploymentCell, Is.Null);
        }

        /// <summary>遮挡 UI 不得被穿透。输入：覆盖棋盘的可命中 UGUI 面板；输出：不高亮、不部署。</summary>
        [UnityTest]
        public IEnumerator OverlayBlocksDrop()
        {
            yield return Load();
            var card = Hand(); var id = card.Record.Id; var target = Target(card);
            var data = HandDragInput.Begin(card);
            var overlay = new GameObject("TestOverlay", typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)overlay.transform;
            rect.SetParent(_hand.GetComponentInParent<Canvas>().transform, false);
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            Canvas.ForceUpdateCanvases();
            yield return null;
            HandDragInput.Move(card, data, HandDragInput.Center(_board.CellRect(target)));
            Assert.That(_board.HoveredDeploymentCell, Is.Null);
            HandDragInput.End(card, data);
            Object.Destroy(overlay);
            yield return new WaitForSecondsRealtime(0.15f);
            Assert.That(_bootstrap.Session.GetSnapshot().Hand.Any(c => c.Id == id), Is.True);
        }

        /// <summary>缩放及左右镜像不改变内部落点。输入：不同显示侧与画布缩放；输出：实际命中坐标等于命令目标。</summary>
        [UnityTest]
        public IEnumerator ScaledAndMirroredBoardsHitActualCells()
        {
            foreach (var side in new[] { "left", "right" })
            {
                yield return Load(side);
                // 单独改变 UGUI 缩放时隔离 HUD 的固定布局；HUD 遮挡在独立用例覆盖。
                Object.FindObjectOfType<UnityEngine.UIElements.UIDocument>().rootVisualElement.style.display = UnityEngine.UIElements.DisplayStyle.None;
                var scaler = _hand.GetComponentInParent<CanvasScaler>();
                scaler.referenceResolution = new Vector2(1600, 900);
                yield return null;
                Canvas.ForceUpdateCanvases();
                var card = Hand(); var id = card.Record.Id;
                var target = _bootstrap.Session.GetLegalActions(new Selection(SelectionKind.Hand, id)).Deploy.First(c => c.Row == 4);
                HandDragInput.Deploy(card, _board, target);
                Assert.That(_bootstrap.Session.GetSnapshot().Units.Single(u => u.SourceCardId == id).Position, Is.EqualTo(target));
            }
        }

        /// <summary>暂停、失焦和禁用均取消手势，旧松手无效。输入：三种宿主中断；输出：原位、无悬停且不部署。</summary>
        [UnityTest]
        public IEnumerator InterruptionsCancelAndIgnoreLateRelease()
        {
            yield return Load();
            var card = Hand(); var id = card.Record.Id; var parent = card.transform.parent;
            for (var mode = 0; mode < 3; mode++)
            {
                var data = HandDragInput.Begin(card);
                HandDragInput.Move(card, data, HandDragInput.Center(_board.CellRect(Target(card))));
                if (mode == 0) _bootstrap.SetModalOpen(true);
                else if (mode == 1) Notify("OnApplicationFocus", false);
                else _hand.enabled = false;
                HandDragInput.End(card, data);
                yield return null;
                Assert.That(card.transform.parent, Is.EqualTo(parent));
                Assert.That(_board.HoveredDeploymentCell, Is.Null);
                Assert.That(_bootstrap.Session.GetSnapshot().Hand.Any(c => c.Id == id), Is.True);
                _bootstrap.SetModalOpen(false); _hand.enabled = true;
                yield return null;
            }
        }

        /// <summary>弃牌阶段拖动不能触发点击弃牌，结束回合和重开清理旧手势。输入：公开回合命令与 UI；输出：身份数量正确。</summary>
        [UnityTest]
        public IEnumerator EndTurnDiscardAndRestartCleanUpGestures()
        {
            yield return Load();
            var d = JsonUtility.FromJson<ConfigDocument>(JsonUtility.ToJson(_bootstrap.CurrentAsset.ToDocument()));
            d.rules[0].firstSide = "player"; d.rules[0].drawCount = 5; d.rules[0].handLimit = 1;
            d.rules[0].bossBombInterval = 1000; d.rules[0].playerControlsBoss = true;
            // 抽牌堆由 WithDeck 给定；比例与上限仍要合法，夹具不依赖 Excel 当前取值。
            d.rules[0].drawPileSize = 24; d.rules[0].tierRatioBasic = 70;
            d.rules[0].tierRatioIntermediate = 20; d.rules[0].tierRatioAdvanced = 10;
            d.rules[0].maxTypesBasic = 5; d.rules[0].maxTypesIntermediate = 3; d.rules[0].maxTypesAdvanced = 2;
            _configuration = new GameConfiguration(d).WithDeck(new[] { new DeckEntry("blade1", 5) }, false);
            _bootstrap.RestartWith(_configuration);
            yield return null;
            var card = Hand(); var data = HandDragInput.Begin(card);
            Notify("OnEndTurnRequested");
            HandDragInput.End(card, data);
            Assert.That(_bootstrap.Session.GetSnapshot().Phase, Is.EqualTo(MatchPhase.Discard));
            var count = _bootstrap.Session.GetSnapshot().Hand.Count;
            data = HandDragInput.Begin(card);
            HandDragInput.End(card, data);
            ExecuteEvents.Execute(card.gameObject, data, ExecuteEvents.pointerClickHandler);
            Assert.That(_bootstrap.Session.GetSnapshot().Hand.Count, Is.EqualTo(count));
            Click(card.gameObject);
            Assert.That(_bootstrap.Session.GetSnapshot().Hand.Count, Is.EqualTo(count - 1));
            _bootstrap.RestartWith(_configuration);
            yield return null;
            card = Hand(); data = HandDragInput.Begin(card);
            _bootstrap.RestartWith(_configuration);
            HandDragInput.End(card, data);
            yield return null;
            Assert.That(GameObject.Find("HandDragPlaceholder"), Is.Null);
            Assert.That(_bootstrap.Session.GetSnapshot().Graveyard, Is.Empty);
        }

        /// <summary>整块画布关闭时不能在禁用回调重挂卡牌。输入：活动拖动与画布开关；输出：恢复后原位、无残留手势。</summary>
        [UnityTest]
        public IEnumerator CanvasDisableDefersLayoutUntilSafeFrame()
        {
            yield return Load();
            var card = Hand(); var parent = card.transform.parent;
            var data = HandDragInput.Begin(card);
            var canvas = _hand.GetComponentInParent<Canvas>().gameObject;
            canvas.SetActive(false);
            yield return null;
            canvas.SetActive(true);
            yield return null;
            HandDragInput.End(card, data);
            Assert.That(card.transform.parent, Is.EqualTo(parent));
            Assert.That(_board.HoveredDeploymentCell, Is.Null);
            Assert.That(card.GetComponent<CanvasGroup>().blocksRaycasts, Is.True);
        }

        /// <summary>活动拖动直接切换场景时也必须安全销毁。输入：拖动中的旧场景；输出：新场景无旧占位或补间错误。</summary>
        [UnityTest]
        public IEnumerator SceneUnloadDuringDragDoesNotReparentDestroyedObjects()
        {
            yield return Load();
            HandDragInput.Begin(Hand());
            yield return Load();
            Assert.That(GameObject.Find("HandDragPlaceholder"), Is.Null);
            Assert.That(_bootstrap.Session.GetSnapshot().Graveyard, Is.Empty);
        }
    }
}
