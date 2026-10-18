using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;
using Button = UnityEngine.UIElements.Button;

namespace Spotlight.Tests
{
    /// <summary>
    /// 刷怪技能真实界面回归。输入：演示场景、独立配置与真实 UI 入口；输出：按钮、选格、命中、顶替视图及重开断言。
    /// 不修改 GameSession 私有状态，所有棋盘变化仍经公开命令校验。
    /// </summary>
    public class BossSpawnerPlayModeTests
    {
        /// <summary>当前场景引导器，提供公开规则会话与重开入口。</summary>
        GameBootstrap _bootstrap;
        /// <summary>棋盘视图，核对快照位置和实际父节点。</summary>
        BoardView _board;
        /// <summary>HUD 根，用于真实按钮和文本断言。</summary>
        VisualElement _root;
        /// <summary>隔离的本局配置，重开复用。</summary>
        GameConfiguration _configuration;

        /// <summary>
        /// 让本类用例加载场景后直接进入对局（跳过“携带选择”面板）。
        /// 输入：无；输出：无。
        /// 为什么需要：正式启动流程会先弹出携带选择模态面板并挡住棋盘，
        /// 而本类只验证刷怪技能与按钮；选卡流程本身由 DeckSelectionPlayModeTests 覆盖。
        /// </summary>
        [OneTimeSetUp]
        public void SuppressDeckSelectionOnStart() => GameBootstrap.SuppressDeckSelectionOnStart = true;

        /// <summary>
        /// 恢复正式启动行为。输入：无；输出：无。
        /// 为什么需要：PlayMode 用例在同一进程里顺序执行，静态开关必须还原，否则会让后面的用例看不到选卡界面。
        /// </summary>
        [OneTimeTearDown]
        public void RestoreDeckSelectionOnStart() => GameBootstrap.SuppressDeckSelectionOnStart = false;

        /// <summary>加载真实场景并替换为测试配置。输入：控制模式与玩家边缘；输出：准备好的视图和可交互会话。</summary>
        IEnumerator Load(bool manual = true, string side = "left")
        {
            yield return SceneManager.LoadSceneAsync("Assets/Spotlight/Scenes/Spotlight.unity");
            yield return null;
            _bootstrap = Object.FindObjectOfType<GameBootstrap>(); _board = Object.FindObjectOfType<BoardView>();
            _root = Object.FindObjectOfType<HudView>().Root;
            var d = JsonUtility.FromJson<ConfigDocument>(JsonUtility.ToJson(_bootstrap.CurrentAsset.ToDocument()));
            var r = d.rules[0];
            r.playerControlsBoss = manual; r.firstSide = "player"; r.playerSide = side;
            r.homeRegionWidth = 10; r.playerPlays = 100; r.bossPlays = 100;
            r.drawCount = 60; r.handLimit = 100; r.bossBombInterval = 1000; r.summoningSickness = true;
            r.bossSpawnerAttackThreshold = 12; r.bossSpawnerUnitThreshold = 2; r.bossSpawnerCountFromColumn = 4; r.bossSpawnerDeployFromColumn = 5;
            // 抽牌堆构成规则显式写进夹具：Deck 表已废除，抽牌堆由下面的 WithDeck 给定，
            // 但配置校验仍要求“每阶级至少一种卡”与三比例合法，写死可以不受 Excel 当前取值影响。
            r.drawPileSize = 24; r.tierRatioBasic = 70; r.tierRatioIntermediate = 20; r.tierRatioAdvanced = 10;
            r.maxTypesBasic = 5; r.maxTypesIntermediate = 3; r.maxTypesAdvanced = 2;
            var fighter = d.cards.Single(c => c.id == d.cards.Single(s => s.id == r.bossSpawnerCardType).spawnCardType); fighter.atk = 0; fighter.moveDistance = 0;
            d.cards.Single(c => c.id == "blade1").atk = 6;
            d.presentation[0].moveSeconds = 0.02f; d.presentation[0].feedbackSeconds = 0.02f;
            // 第二个参数 false：60 张的夹具牌堆不等于 drawPileSize（24），走测试夹具专供的豁免路径。
            _configuration = new GameConfiguration(d).WithDeck(
                new[] { new DeckEntry("blade1", 30), new DeckEntry("ge1", 30) }, false);
            _bootstrap.RestartWith(_configuration);
            yield return null;
        }

        /// <summary>调用已有界面事件入口。输入：方法名及参数；输出：规则结算和真实 UI 刷新，用于模拟棋盘点击。</summary>
        void Invoke(string name, params object[] args) => typeof(GameBootstrap).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_bootstrap, args);

        /// <summary>等待阶段动画完成。输入：当前引导器；输出：有限帧等待并断言输入闸门解除。</summary>
        IEnumerator WaitForPresentation()
        {
            var deadline = Time.realtimeSinceStartup + 10;
            while (_bootstrap.IsPresentingTurn && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(_bootstrap.IsPresentingTurn, Is.False);
        }

        /// <summary>用手牌部署玩家单位。输入：卡种和格子；输出：新单位 ID，以真实命令和展示进入棋盘。</summary>
        long Put(string type, Cell cell)
        {
            var card = _bootstrap.Session.GetSnapshot().Hand.First(c => c.TypeId == type);
            Invoke("Submit", new GameCommand(CommandKind.Deploy, Side.Player, card.Id, cell));
            return _bootstrap.Session.GetSnapshot().Units.Single(u => u.Position == cell).Id;
        }

        /// <summary>建立触发条件并进入 Boss。输入：玩家边缘；输出：手动技能窗口或自动技能展示。</summary>
        void Arm(string side = "left")
        {
            var col = side == "left" ? 3 : 6;
            Put("blade1", new Cell(0,col)); Put("blade1", new Cell(4,col)); Invoke("OnEndTurnRequested");
        }

        /// <summary>发送按钮实际支持的键盘提交事件。输入：按钮名；输出：触发绑定的点击回调。</summary>
        void ClickButton(string name)
        {
            var button = _root.Q<Button>(name);
            Assert.That(button.enabledInHierarchy, Is.True);
            using (var evt = NavigationSubmitEvent.GetPooled())
            {
                evt.target = button;
                button.SendEvent(evt);
            }
        }

        /// <summary>两侧玩家视角均显示正确技能窗口，非法格不部署，合法部署不扣普通放牌次数；棋盘卡牌不抢点击。</summary>
        [UnityTest]
        public IEnumerator ManualButtonsSelectionAndClickThroughWorkOnBothSides()
        {
            foreach (var side in new[] { "left", "right" })
            {
                yield return Load(side:side); Arm(side); yield return null;
                Assert.That(_root.Q<Button>("spawner-deploy-button").resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(_root.Q<Button>("end-turn-button").enabledInHierarchy, Is.False);
                Assert.That(_root.Q<Button>("skip-deploy-button").enabledInHierarchy, Is.False);
                var plays = _bootstrap.Session.GetSnapshot().PlaysRemaining;
                ClickButton("spawner-deploy-button");
                var legal = _bootstrap.Session.GetLegalActions(new Selection(SelectionKind.BossSpawnerDeployment)).Deploy;
                foreach (var cell in _board.GetComponentsInChildren<BoardCellView>())
                    Assert.That(cell.transform.Find("Highlight").GetComponent<UnityEngine.UI.Image>().enabled, Is.EqualTo(legal.Contains(cell.Coordinate)), cell.Coordinate.ToString());
                Invoke("OnCellClicked", new Cell(1, side == "left" ? 2 : 7));
                Assert.That(_bootstrap.Session.GetSnapshot().BossSpawnerPending, Is.True);
                var position = new Cell(2, side == "left" ? 5 : 4);
                ExecuteEvents.Execute(_board.CellRect(position).gameObject,
                    new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left }, ExecuteEvents.pointerClickHandler);
                yield return WaitForPresentation(); yield return null;
                var unit = _bootstrap.Session.GetSnapshot().Units.Single(u => u.IsBossSpawner);
                Assert.That(unit.Position, Is.EqualTo(position));
                Assert.That(_bootstrap.Session.GetSnapshot().PlaysRemaining, Is.EqualTo(plays));
                Assert.That(_root.Q<Button>("spawner-deploy-button").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
                var card = _board.GetCard(unit.Id); Assert.That(card, Is.Not.Null);
                Assert.That(card.GetComponentsInChildren<Graphic>().All(g => !g.raycastTarget), Is.True);
                Assert.That(card.transform.parent, Is.EqualTo(_board.CellRect(position)));
                var pointer = new PointerEventData(EventSystem.current) { position = RectTransformUtility.WorldToScreenPoint(null, _board.CellRect(position).position) };
                var hits = new List<RaycastResult>(); EventSystem.current.RaycastAll(pointer, hits);
                Assert.That(hits.Any(h => h.gameObject.GetComponentInParent<BoardCellView>()?.Coordinate == position), Is.True);
                Invoke("OnCellClicked", position);
                Assert.That(_root.Q<Label>("unit-detail").text, Does.Contain("不能攻击、移动或互换"));
                Assert.That(_root.Q<Label>("boss-info-label").text, Does.Contain("刷怪卡"));
            }
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>跳过按钮释放普通操作；重开清理机会、据点、选择与旧动画，不留下刷怪卡按钮。</summary>
        [UnityTest]
        public IEnumerator SkipAndRestartClearOpportunityAndViews()
        {
            yield return Load(); Arm(); yield return null;
            ClickButton("spawner-skip-button");
            Assert.That(_bootstrap.Session.GetSnapshot().BossSpawnerPending, Is.False);
            Assert.That(_root.Q<Button>("end-turn-button").enabledInHierarchy, Is.True);
            Invoke("OnSkipDeployRequested"); Invoke("OnEndTurnRequested");
            ClickButton("spawner-deploy-button"); Invoke("OnCellClicked",new Cell(2,5));
            _bootstrap.RestartWith(_configuration); yield return null; yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(_bootstrap.Session.GetSnapshot().Units, Is.Empty);
            Assert.That(_bootstrap.Session.GetSnapshot().BossSpawnerPending, Is.False);
            Assert.That(_root.Q<Button>("spawner-deploy-button").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(_bootstrap.IsPresentingTurn, Is.False);
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>八邻占满时视图先移除被顶替玩家单位再生成战机，最终位置跟随快照且动画不改结算。</summary>
        [UnityTest]
        public IEnumerator ReplacementAndSpawnSynchronizeViewsAcrossFrames()
        {
            yield return Load();
            for (var row=1; row<=3; row++) for(var col=4; col<=6; col++)
                if(row!=2 || col!=5) Put("ge1",new Cell(row,col));
            Arm(); ClickButton("spawner-deploy-button"); Invoke("OnCellClicked",new Cell(2,5));
            yield return WaitForPresentation();
            Invoke("OnSkipDeployRequested");
            var before = _bootstrap.Session.GetSnapshot();
            Invoke("OnEndTurnRequested");
            var settled = _bootstrap.Session.GetSnapshot();
            var removed = before.Units.Single(u => settled.Units.All(n => n.Id != u.Id));
            var fighter = settled.Units.Single(u => u.Position == removed.Position);
            Assert.That(fighter.Acted, Is.True);
            yield return WaitForPresentation(); yield return null;
            Assert.That(_board.GetCard(removed.Id), Is.Null);
            Assert.That(_board.GetCard(fighter.Id).transform.parent, Is.EqualTo(_board.CellRect(fighter.Position)));
            Assert.That(_board.CellRect(fighter.Position).GetComponentsInChildren<Card>().Length, Is.EqualTo(1));
            Assert.That(_bootstrap.Session.GetSnapshot().Units.Select(u=>u.Hp), Is.EqualTo(settled.Units.Select(u=>u.Hp)));
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>AI 技能阶段以 Boss 回合显示，展示完成后交回玩家并恢复输入，生成单位可见。</summary>
        [UnityTest]
        public IEnumerator AiSkillPresentationCompletesAndReturnsPlayerControl()
        {
            yield return Load(false); Arm(); yield return WaitForPresentation();
            var spawner = _bootstrap.Session.GetSnapshot().Units.Single(u=>u.IsBossSpawner);
            Assert.That(_board.GetCard(spawner.Id), Is.Not.Null);
            Invoke("OnEndTurnRequested"); yield return WaitForPresentation();
            Assert.That(_bootstrap.Session.GetSnapshot().ActiveSide, Is.EqualTo(Side.Player));
            Assert.That(_root.Q<Button>("end-turn-button").enabledInHierarchy, Is.True);
            var generated = _bootstrap.Session.GetSnapshot().Units.Single(u=>u.Owner==Side.Boss && !u.IsBossSpawner && u.BossSlotId==null);
            Assert.That(_board.GetCard(generated.Id).transform.parent, Is.EqualTo(_board.CellRect(generated.Position)));
            LogAssert.NoUnexpectedReceived();
        }
    }
}
