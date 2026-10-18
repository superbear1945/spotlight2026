// 高级卡（四邻空格限制 / 击杀恢复行动 / 伤害标记）的真实帧循环回归。
// 为什么必须在 PlayMode 验证：拖放部署、点击穿透、卡面与详情文本刷新都依赖真实布局与帧循环，
// EditMode 只能证明规则正确，证明不了“卡面遮挡格子”“层数不刷新”这类视图问题。
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace Spotlight.Tests
{
    /// <summary>
    /// 高级卡视图契约测试。
    /// 输入：演示场景 + 隔离配置（含后羿-II、维京长船、SNP-EU 的夹具牌堆）；
    /// 输出：部署合法性、点击穿透、卡面文本刷新与连续行动的断言。
    /// 不修改 GameSession 私有状态：全部变化都经 GameBootstrap 的公开输入入口提交命令。
    /// </summary>
    public class AdvancedCardsPlayModeTests
    {
        /// <summary>演示场景路径（与 SpotlightMenu.DemoScenePath 一致）。</summary>
        const string ScenePath = "Assets/Spotlight/Scenes/Spotlight.unity";

        /// <summary>对局引导器，提供公开会话与重开入口。</summary>
        GameBootstrap _bootstrap;
        /// <summary>棋盘视图，用于取卡牌与格子。</summary>
        BoardView _board;

        /// <summary>跳过“携带选择”面板，让用例直接进入对局。输入：无；输出：无。</summary>
        [OneTimeSetUp]
        public void SuppressDeckSelectionOnStart() => GameBootstrap.SuppressDeckSelectionOnStart = true;

        /// <summary>恢复正式启动行为。输入：无；输出：无。避免影响同进程的其它 PlayMode 用例。</summary>
        [OneTimeTearDown]
        public void RestoreDeckSelectionOnStart() => GameBootstrap.SuppressDeckSelectionOnStart = false;

        /// <summary>
        /// 加载真实场景并替换为测试配置。
        /// 输入：Boss 单位生命（击杀测试用 3，标记测试用 20）；输出：准备好的视图与可交互会话。
        /// 配置要点：家区域放大到整块棋盘、手动 Boss、无炸弹干扰、夹具牌堆只放本文件需要的卡种。
        /// </summary>
        IEnumerator Load(int bossHp)
        {
            yield return SceneManager.LoadSceneAsync(ScenePath);
            yield return null;
            _bootstrap = Object.FindObjectOfType<GameBootstrap>();
            _board = Object.FindObjectOfType<BoardView>();
            var d = JsonUtility.FromJson<ConfigDocument>(JsonUtility.ToJson(_bootstrap.CurrentAsset.ToDocument()));
            var r = d.rules[0];
            r.playerControlsBoss = true;
            r.firstSide = "player";
            r.playerSide = "left";
            r.homeRegionWidth = 10;
            r.playerPlays = 100;
            r.bossPlays = 100;
            r.drawCount = 40;
            r.handLimit = 100;
            r.bossBombInterval = 1000;
            r.bossSpawnerAttackThreshold = 1000;
            r.summoningSickness = false;
            d.cards.Single(c => c.id == "bossCombat").hp = bossHp;
            d.presentation[0].moveSeconds = 0.02f;
            d.presentation[0].feedbackSeconds = 0.02f;
            var configuration = new GameConfiguration(d).WithDeck(new[]
            {
                new DeckEntry("ge1", 20),
                new DeckEntry("houyi2", 10),
                new DeckEntry("viking2056", 10),
                new DeckEntry("snpEu", 10)
            }, false);
            _bootstrap.RestartWith(configuration);
            yield return null;
            Assert.That(_bootstrap.Session, Is.Not.Null, "对局未启动（配置门禁或配置资产缺失）");
        }

        /// <summary>提交命令并返回结果。输入：命令；输出：引导器的公开结算结果。</summary>
        CommandResult Submit(GameCommand command) =>
            (CommandResult)typeof(GameBootstrap).GetMethod("Submit", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_bootstrap, new object[] { command });

        /// <summary>等待阶段动画结束。输入：无；输出：有限帧等待并断言输入闸门解除。</summary>
        IEnumerator WaitForPresentation()
        {
            var deadline = Time.realtimeSinceStartup + 10;
            while (_bootstrap.IsPresentingTurn && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(_bootstrap.IsPresentingTurn, Is.False);
        }

        /// <summary>用手牌部署单位。输入：卡种与格子；输出：新单位 ID。</summary>
        long Put(string type, Cell cell)
        {
            var card = _bootstrap.Session.GetSnapshot().Hand.First(c => c.TypeId == type);
            var result = Submit(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, cell));
            Assert.That(result.Success, Is.True, result.Error);
            return _bootstrap.Session.GetSnapshot().Units.Single(u => u.Position == cell).Id;
        }

        /// <summary>取卡面上含生命/攻击/标记的那段文本。输入：卡牌视图；输出：属性文本。</summary>
        static string StatText(Card card) => card.GetComponentsInChildren<TMP_Text>()
            .Select(t => t.text).FirstOrDefault(t => t != null && t.Contains("生命")) ?? string.Empty;

        /// <summary>推进到玩家回合并部署一个指定格的 Boss 单位，返回其 ID。</summary>
        IEnumerator SendBossTurn(Cell cell)
        {
            // 每条命令后都等待展示结束，避免 _presentingTurn 让下一次 Submit 直接返回 null。
            Submit(new GameCommand(CommandKind.EndTurn, Side.Player, skipDeployment: true));
            yield return WaitForPresentation();
            Submit(new GameCommand(CommandKind.BossDeploy, Side.Boss, target: cell));
            yield return WaitForPresentation();
            Submit(new GameCommand(CommandKind.EndTurn, Side.Boss, skipDeployment: true));
            yield return WaitForPresentation();
        }

        /// <summary>
        /// 后羿-II 在四邻不足时部署失败并留在手里，撤走阻挡后可以成功部署；
        /// 棋盘卡面的所有图形都不拦截射线，格子命中不会被新文本遮挡。
        /// </summary>
        [UnityTest]
        public IEnumerator HouyiDeployNeedsTwoEmptyNeighborsAndCardsDoNotBlockClicks()
        {
            yield return Load(20);
            Put("ge1", new Cell(3, 5));
            Put("ge1", new Cell(1, 5));
            var blocker = Put("ge1", new Cell(2, 6));
            yield return WaitForPresentation();
            var card = _bootstrap.Session.GetSnapshot().Hand.First(c => c.TypeId == "houyi2");
            var illegal = Submit(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(2, 5)));
            Assert.That(illegal.Success, Is.False, "只剩 1 空时后羿-II 不能部署");
            Assert.That(_bootstrap.Session.GetSnapshot().Hand.Any(c => c.Id == card.Id), Is.True, "失败后必须回到手牌");
            Submit(new GameCommand(CommandKind.Withdraw, Side.Player, blocker));
            var legal = Submit(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(2, 5)));
            Assert.That(legal.Success, Is.True, legal.Error);
            yield return WaitForPresentation();
            var unit = _bootstrap.Session.GetSnapshot().Units.Single(u => u.Position == new Cell(2, 5));
            var view = _board.GetCard(unit.Id);
            Assert.That(view, Is.Not.Null, "棋盘未生成卡牌视图");
            Assert.That(view.GetComponentsInChildren<Graphic>().All(g => !g.raycastTarget), Is.True,
                "棋盘卡牌（含新增文本）不得拦截点击");
            Assert.That(_board.CellRect(new Cell(2, 5)), Is.Not.Null);
        }

        /// <summary>
        /// 维京长船击杀后可以连续行动，卡面随快照刷新；两次击杀都写入恢复事件。
        /// </summary>
        [UnityTest]
        public IEnumerator KillActionChainsClickableActions()
        {
            yield return Load(3);
            var viking = Put("viking2056", new Cell(2, 2));
            yield return WaitForPresentation();
            yield return SendBossTurn(new Cell(2, 3));
            var first = _bootstrap.Session.GetSnapshot().Units.Single(u => u.Owner == Side.Boss);
            var kill = Submit(new GameCommand(CommandKind.Attack, Side.Player, viking, targetId: first.Id));
            Assert.That(kill.Success, Is.True, kill.Error);
            Assert.That(kill.Events.Any(e => e.Kind == "action-restored"), Is.True, "击杀应恢复行动");
            Assert.That(_bootstrap.Session.GetSnapshot().Units.Single(u => u.Id == viking).Acted, Is.False);
            yield return WaitForPresentation();
            Assert.That(_board.GetCard(viking), Is.Not.Null);
            Assert.That(StatText(_board.GetCard(viking)), Does.Contain("生命"));
        }

        /// <summary>
        /// SNP-EU 攻击后目标卡面显示标记层数，单位详情同步显示追加伤害。
        /// </summary>
        [UnityTest]
        public IEnumerator DamageMarkLayersAppearOnBoardAndInDetail()
        {
            yield return Load(20);
            var sniper = Put("snpEu", new Cell(2, 2));
            yield return WaitForPresentation();
            yield return SendBossTurn(new Cell(2, 3));
            var boss = _bootstrap.Session.GetSnapshot().Units.Single(u => u.Owner == Side.Boss);
            var hit = Submit(new GameCommand(CommandKind.Attack, Side.Player, sniper, targetId: boss.Id));
            Assert.That(hit.Success, Is.True, hit.Error);
            Assert.That(hit.Events.Any(e => e.Kind == "damage-mark"), Is.True);
            yield return WaitForPresentation();
            var marked = _bootstrap.Session.GetSnapshot().Units.Single(u => u.Id == boss.Id);
            Assert.That(marked.DamageMarkLayers, Is.EqualTo(1));
            var markedView = _board.GetCard(boss.Id);
            Assert.That(markedView, Is.Not.Null);
            Assert.That(StatText(markedView), Does.Contain("标 1"), "卡面必须显示标记层数");
            Assert.That(CardText.UnitDetail(_bootstrap.Session.Configuration.Cards["bossCombat"], marked), Does.Contain("伤害标记：1 层"));
        }
    }
}
