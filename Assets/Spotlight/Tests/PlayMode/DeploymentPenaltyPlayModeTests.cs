// 一次性部署减攻（京都壹号）与场上属性的真实帧循环回归。
// 为什么必须在 PlayMode 验证：卡面文本、UI Toolkit 详情与 UGUI 点击穿透都依赖真实布局与帧循环，
// EditMode 只能证明规则数值正确，证明不了“棋盘卡面显示减攻后的数值”“点卡牌仍能穿透到格子”。
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

namespace Spotlight.Tests
{
    /// <summary>
    /// 一次性部署减攻的视图契约测试。
    /// 输入：演示场景 + 隔离配置（京都壹号作为来源、尖刀1作为目标）；输出：卡面/详情/组件查询与点击命中断言。
    /// 不修改 GameSession 私有状态：全部棋盘变化仍经 GameBootstrap 的公开输入入口提交命令。
    /// </summary>
    public class DeploymentPenaltyPlayModeTests
    {
        /// <summary>演示场景路径（与 SpotlightMenu.DemoScenePath 一致）。</summary>
        const string ScenePath = "Assets/Spotlight/Scenes/Spotlight.unity";

        /// <summary>对局引导器，提供公开会话与重开入口。</summary>
        GameBootstrap _bootstrap;
        /// <summary>棋盘视图，用于取卡牌与格子。</summary>
        BoardView _board;
        /// <summary>HUD 根元素，用于读取真实详情文本。</summary>
        VisualElement _root;
        /// <summary>隔离的本局配置，重开复用。</summary>
        GameConfiguration _configuration;

        /// <summary>
        /// 加载真实场景并替换为测试配置。
        /// 输入：目标卡（尖刀1）的攻击力，用于控制减攻后的结果；输出：准备好的视图与可交互会话。
        /// 配置要点：homeRegionWidth 设为整块棋盘，攻击卡可部署在任意空格，避免测试被部署区域限制干扰。
        /// </summary>
        IEnumerator Load(int targetAttack = 4)
        {
            yield return SceneManager.LoadSceneAsync(ScenePath);
            yield return null;
            _bootstrap = Object.FindObjectOfType<GameBootstrap>();
            _board = Object.FindObjectOfType<BoardView>();
            _root = Object.FindObjectOfType<HudView>().Root;
            var d = JsonUtility.FromJson<ConfigDocument>(JsonUtility.ToJson(_bootstrap.CurrentAsset.ToDocument()));
            var r = d.rules[0];
            r.playerControlsBoss = true; r.firstSide = "player"; r.playerSide = "left";
            r.homeRegionWidth = 10; r.playerPlays = 100; r.bossPlays = 100;
            r.drawCount = 60; r.handLimit = 100; r.bossBombInterval = 1000; r.summoningSickness = true;
            r.bossSpawnerAttackThreshold = 1000;
            d.cards.Single(c => c.id == "blade1").atk = targetAttack;
            d.deck = new[] { new DeckRow { cardType = "kyoto_01", count = 30 }, new DeckRow { cardType = "blade1", count = 30 } };
            d.presentation[0].moveSeconds = 0.02f; d.presentation[0].feedbackSeconds = 0.02f;
            _configuration = new GameConfiguration(d);
            _bootstrap.RestartWith(_configuration);
            yield return null;
            Assert.That(_bootstrap.Session, Is.Not.Null, "对局未启动（配置门禁或配置资产缺失）");
        }

        /// <summary>调用引导器已有的输入入口。输入：方法名与参数；输出：无（复现界面点击）。</summary>
        void Invoke(string name, params object[] args) => typeof(GameBootstrap).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_bootstrap, args);

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
            Invoke("Submit", new GameCommand(CommandKind.Deploy, Side.Player, card.Id, cell));
            return _bootstrap.Session.GetSnapshot().Units.Single(u => u.Position == cell).Id;
        }

        /// <summary>取卡面上含生命/攻击的那段文本。输入：卡牌视图；输出：属性文本（缺失时为空串）。</summary>
        static string StatText(Card card) => card.GetComponentsInChildren<TMP_Text>().Select(t => t.text).FirstOrDefault(t => t != null && t.Contains("生命")) ?? string.Empty;

        /// <summary>
        /// 棋盘卡面与单位详情显示减攻后的数值，同卡种手牌仍显示基础配置；来源显示已消耗。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [UnityTest]
        public IEnumerator BoardCardAndDetailShowCurrentAttackWhileHandShowsBase()
        {
            yield return Load();
            var sourceId = Put("kyoto_01", new Cell(0, 3));
            var targetId = Put("blade1", new Cell(0, 4));
            yield return WaitForPresentation();
            yield return null;

            var snapshot = _bootstrap.Session.GetSnapshot();
            var target = snapshot.Units.Single(u => u.Id == targetId);
            var source = snapshot.Units.Single(u => u.Id == sourceId);
            Assert.That(target.Attack, Is.EqualTo(2), "配置攻击 4 被减攻 2 后应为 2");
            Assert.That(source.DeploymentPenaltyPending, Is.False, "来源应已消耗");

            var targetCard = _board.GetCard(targetId);
            Assert.That(targetCard, Is.Not.Null);
            Assert.That(StatText(targetCard), Does.Contain("攻 2"), "棋盘卡面必须显示当前攻击力");
            Assert.That(targetCard.Combat.Attack, Is.EqualTo(2), "战斗组件必须读单位快照");
            Assert.That(targetCard.DeploymentPenalty, Is.Null, "目标卡种没有该能力，Prefab 上不应残留组件");

            // 同卡种手牌仍显示卡种基础配置（手牌读卡种资产，不读本局草稿），不受场上减攻影响。
            var handCard = Object.FindObjectOfType<HandView>().GetCard(snapshot.Hand.First(c => c.TypeId == "blade1").Id);
            Assert.That(handCard, Is.Not.Null);
            var baseAttack = _bootstrap.CurrentAsset.Find("blade1").Combat.Attack;
            Assert.That(StatText(handCard), Does.Contain("攻 " + baseAttack), "手牌显示卡种基础配置");
            Assert.That(baseAttack, Is.Not.EqualTo(target.Attack), "基础值与场上值应当可区分");

            // 选中来源单位：详情区显示“已消耗”，组件查询也一致。
            Invoke("OnCellClicked", source.Position);
            yield return null;
            Assert.That(_root.Q<Label>("unit-detail").text, Does.Contain("已消耗"), _root.Q<Label>("unit-detail").text);
            var sourceCard = _board.GetCard(sourceId);
            Assert.That(sourceCard.DeploymentPenalty, Is.Not.Null, "来源卡种应有一次性部署减攻组件");
            Assert.That(sourceCard.DeploymentPenalty.Consumed, Is.True);
            Assert.That(sourceCard.DeploymentPenalty.ReduceAmount, Is.EqualTo(2));

            // 选中目标：详情区显示当前攻击 2。
            Invoke("OnCellClicked", target.Position);
            yield return null;
            Assert.That(_root.Q<Label>("unit-detail").text, Does.Contain("当前攻击 2"), _root.Q<Label>("unit-detail").text);
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>
        /// 攻击降到 0 时卡面明确显示 0；重开后不残留旧单位的攻击或能力状态；棋盘卡牌不抢点击。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [UnityTest]
        public IEnumerator ZeroAttackIsVisibleAndRestartLeavesNoResidualState()
        {
            yield return Load(targetAttack: 2);
            var targetCell = new Cell(1, 4);
            Put("kyoto_01", new Cell(1, 3));
            var targetId = Put("blade1", targetCell);
            yield return WaitForPresentation();
            yield return null;

            var target = _bootstrap.Session.GetSnapshot().Units.Single(u => u.Id == targetId);
            Assert.That(target.Attack, Is.Zero, "配置攻击 2 被减攻 2 后应为 0");
            // 卡面必须能看到 0（不能退回显示基础值而让玩家以为还能攻击）。
            Assert.That(StatText(_board.GetCard(targetId)), Does.Contain("攻 0"));
            // 攻击为 0 时规则层不再提供攻击目标。
            Assert.That(_bootstrap.Session.GetLegalActions(new Selection(SelectionKind.Unit, targetId)).Attack, Is.Empty);

            // 棋盘卡牌不参与射线检测，点击仍穿透到格子。
            var card = _board.GetCard(targetId);
            Assert.That(card.GetComponentsInChildren<Graphic>().All(g => !g.raycastTarget), Is.True);
            var cell = _board.CellRect(targetCell);
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current)
            {
                position = RectTransformUtility.WorldToScreenPoint(null, cell.position)
            }, hits);
            Assert.That(hits.Any(h => h.gameObject.GetComponentInParent<BoardCellView>()?.Coordinate == targetCell), Is.True);

            // 重开后棋盘清空，不残留旧单位的攻击与能力状态。
            _bootstrap.RestartWith(_configuration);
            yield return null;
            yield return new WaitForSecondsRealtime(0.2f);
            var fresh = _bootstrap.Session.GetSnapshot();
            Assert.That(fresh.Units, Is.Empty);
            Assert.That(_board.GetCard(targetId), Is.Null);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
