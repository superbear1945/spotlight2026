using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using TMPro;

namespace Spotlight.Tests
{
    /// <summary>
    /// 炸弹实际帧循环回归。输入：真实演示场景与独立运行配置；输出：UI、命中、表现与重开清理断言。
    /// 通过引导器提交公开规则命令，不修改 GameSession 私有状态。
    /// </summary>
    public class BossBombPlayModeTests
    {
        /// <summary>本用例加载的真实对局引导器。</summary>
        GameBootstrap _bootstrap;
        /// <summary>本用例棋盘，用于核对内部坐标和实际矩形。</summary>
        BoardView _board;
        /// <summary>本用例的独立配置，重开时继续使用。</summary>
        GameConfiguration _configuration;

        /// <summary>加载真实场景和独立夹具。输入：模式、先手、间隔及表现时长；输出：已开局的可交互界面。</summary>
        IEnumerator Load(bool manual = true, string first = "player", int interval = 1, float seconds = 0.12f)
        {
            yield return SceneManager.LoadSceneAsync("Assets/Spotlight/Scenes/Spotlight.unity");
            yield return null;
            _bootstrap = Object.FindObjectOfType<GameBootstrap>();
            _board = Object.FindObjectOfType<BoardView>();
            Assert.That(_bootstrap, Is.Not.Null);
            var d = JsonUtility.FromJson<ConfigDocument>(JsonUtility.ToJson(_bootstrap.CurrentAsset.ToDocument()));
            d.rules[0].bossBombInterval = interval; d.rules[0].bossBombDamage = 3; d.rules[0].bossBombColumn = 5;
            d.rules[0].playerControlsBoss = manual; d.rules[0].firstSide = first;
            d.rules[0].bossPlays = 0; d.rules[0].handLimit = 100; d.rules[0].drawCount = 24;
            d.rules[0].playerSide = "left";
            d.presentation[0].feedbackSeconds = seconds; d.presentation[0].moveSeconds = seconds;
            if (!manual)
            {
                var boss = d.cards.Single(c => c.id == d.rules[0].bossCardType);
                boss.atk = 0; boss.moveDistance = 0;
            }
            _configuration = new GameConfiguration(d);
            _bootstrap.RestartWith(_configuration);
            yield return null;
        }

        /// <summary>模拟已有界面事件入口。输入：方法名及参数；输出：同步刷新，规则仍经 Execute 复验。</summary>
        void Invoke(string method, params object[] args) => typeof(GameBootstrap).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_bootstrap, args);

        /// <summary>等待自动回合展示结束。输入：当前引导器；输出：有超时断言的帧等待，防止取消/零时长造成卡死。</summary>
        IEnumerator WaitForPresentation()
        {
            var deadline = Time.realtimeSinceStartup + 10f;
            while (_bootstrap.IsPresentingTurn && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(_bootstrap.IsPresentingTurn, Is.False, "自动回合展示未结束");
        }

        /// <summary>读取真实 HUD 的回合文本。输入：无；输出：玩家实际看到的标题，不使用规则最终快照代替。</summary>
        string TurnText() => UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Label>(
            Object.FindObjectOfType<HudView>().Root, "turn-label").text;

        /// <summary>等待指定回合的真实炸弹反馈。输入：预期回合号；输出：帧等待，容许前一 Boss 行动先播放但禁止跳过技能画面。</summary>
        IEnumerator WaitForBombFeedback(int round)
        {
            var deadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (TurnText() == $"第 {round} 回合 · 玩家行动" &&
                    Object.FindObjectsOfType<Image>().Any(i => i.name == "BossBombFlash")) yield break;
                yield return null;
            }
            Assert.Fail($"未展示第 {round} 回合玩家行动前的炸弹反馈");
        }

        /// <summary>两种先手均在第十回合玩家操作前显示部署、第十一回合操作前显示爆炸，反馈结束才开放输入。</summary>
        [UnityTest]
        public IEnumerator AiBombFeedbackUsesItsOwnRoundForBothFirstSides()
        {
            foreach (var first in new[] { "player", "boss" })
            {
                yield return Load(false, first, 10, 0.2f);
                yield return WaitForPresentation();
                for (var guard = 0; guard < 12 && _bootstrap.Session.GetSnapshot().BossBombs.Count == 0; guard++)
                {
                    Invoke("OnEndTurnRequested");
                    if (_bootstrap.Session.GetSnapshot().BossBombs.Count == 0) yield return WaitForPresentation();
                }
                var bomb = _bootstrap.Session.GetSnapshot().BossBombs.Single();
                yield return WaitForBombFeedback(10);
                Assert.That(TurnText(), Is.EqualTo("第 10 回合 · 玩家行动"));
                Assert.That(bomb.DeployedRound, Is.EqualTo(10));
                Assert.That(_board.CellRect(bomb.Position).Find("BossBombMarker"), Is.Not.Null);
                Assert.That(_board.CellRect(bomb.Position).GetComponentInChildren<TMP_Text>().text, Is.Not.Empty);
                var settled = _bootstrap.Session.GetSnapshot();
                Invoke("OnEndTurnRequested");
                Assert.That(_bootstrap.Session.GetSnapshot().Round, Is.EqualTo(settled.Round), "播放期间不能结束未来玩家回合");
                Assert.That(_bootstrap.Session.Execute(new GameCommand(CommandKind.EndTurn, Side.Player)).Success, Is.False);
                yield return WaitForPresentation();
                Assert.That(TurnText(), Is.EqualTo("第 10 回合 · 玩家行动"));
                Invoke("OnEndTurnRequested");
                yield return WaitForBombFeedback(11);
                yield return null;
                Assert.That(TurnText(), Is.EqualTo("第 11 回合 · 玩家行动"));
                Assert.That(_board.CellRect(bomb.Position).Find("BossBombMarker"), Is.Null);
                Assert.That(Object.FindObjectsOfType<Image>().Any(i => i.name == "BossBombFlash"), Is.True);
                yield return WaitForPresentation();
                Assert.That(TurnText(), Is.EqualTo("第 11 回合 · 玩家行动"));
            }
        }

        /// <summary>Boss 先手首回合部署可见；面板闸门与展示闸门互不覆盖，重开不会恢复旧标题或炸弹。</summary>
        [UnityTest]
        public IEnumerator InitialAiPresentationRespectsModalAndRestart()
        {
            yield return Load(false, "boss", 1, 0.25f);
            yield return WaitForBombFeedback(1);
            Assert.That(_bootstrap.IsPresentingTurn, Is.True);
            Assert.That(TurnText(), Is.EqualTo("第 1 回合 · 玩家行动"));
            _bootstrap.SetModalOpen(true);
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(TurnText(), Is.EqualTo("第 1 回合 · 玩家行动"));
            Assert.That(_bootstrap.Session.Execute(new GameCommand(CommandKind.EndTurn, Side.Player)).Success, Is.False);
            _bootstrap.SetModalOpen(false);
            Assert.That(_bootstrap.Session.Execute(new GameCommand(CommandKind.EndTurn, Side.Player)).Success, Is.False);
            yield return WaitForPresentation();
            Invoke("OnEndTurnRequested");
            yield return WaitForBombFeedback(2);
            Assert.That(TurnText(), Is.EqualTo("第 2 回合 · 玩家行动"));
            _bootstrap.RestartWith(_configuration);
            yield return WaitForPresentation();
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.That(TurnText(), Is.EqualTo("第 1 回合 · 玩家行动"));
            Assert.That(_bootstrap.Session.GetSnapshot().BossBombs.Single().DeployedRound, Is.EqualTo(1));
            Assert.That(Object.FindObjectsOfType<Image>().Any(i => i.name == "BossBombFlash"), Is.False);
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>零时长时仍完成阶段、恢复操作；间隔一先显示无新炸弹的爆炸阶段，再显示新部署。</summary>
        [UnityTest]
        public IEnumerator ZeroDurationAiFeedbackCompletesWithoutSkippingRules()
        {
            yield return Load(false, "player", 1, 0f);
            yield return WaitForPresentation();
            Invoke("OnEndTurnRequested");
            yield return WaitForPresentation();
            Invoke("OnEndTurnRequested");
            yield return WaitForPresentation();
            Assert.That(TurnText(), Is.EqualTo("第 3 回合 · 玩家行动"));
            Assert.That(_bootstrap.Session.GetSnapshot().BossBombs.Single().DeployedRound, Is.EqualTo(3));
            Assert.That(_bootstrap.Session.Execute(new GameCommand(CommandKind.EndTurn, Side.Player)).Success, Is.True);
        }

        /// <summary>玩家行动前使用手动技能按钮选择炸弹格。输入：坐标；输出：已部署标记，等待展示后才允许操作。</summary>
        void Deploy(Cell position)
        {
            Assert.That(_bootstrap.Session.GetSnapshot().ActiveSide, Is.EqualTo(Side.Player));
            Assert.That(_bootstrap.Session.GetSnapshot().BossBombPending, Is.True);
            Invoke("OnBombDeployRequested");
            Invoke("OnCellClicked", position);
            Assert.That(_bootstrap.Session.GetSnapshot().BossBombs.Single().Position, Is.EqualTo(position));
        }

        /// <summary>同格炸弹不抢点击，倒计时跟随快照，爆炸播放真实路径且结束后无残留。</summary>
        [UnityTest]
        public IEnumerator MarkerPassesClicksAndExplosionClearsItWithoutChangingRules()
        {
            yield return Load();
            var cell = new Cell(2, 4);
            Deploy(cell);
            yield return WaitForPresentation();
            var record = _bootstrap.Session.GetSnapshot().Hand.First(c => _configuration.Cards[c.TypeId].Category == CardCategory.Resource && !_configuration.Cards[c.TypeId].RequiresUpgrade);
            Invoke("Submit", new GameCommand(CommandKind.Deploy, Side.Player, record.Id, cell));
            yield return null;
            var marker = _board.CellRect(cell).Find("BossBombMarker");
            Assert.That(marker, Is.Not.Null);
            Assert.That(marker.GetComponentsInChildren<Graphic>().All(g => !g.raycastTarget), Is.True);
            Assert.That(marker.GetComponentInChildren<TMP_Text>().text, Does.Contain("1"));
            var pointer = new PointerEventData(EventSystem.current) { position = RectTransformUtility.WorldToScreenPoint(null, _board.CellRect(cell).position) };
            var hits = new System.Collections.Generic.List<RaycastResult>();
            EventSystem.current.RaycastAll(pointer, hits);
            Assert.That(hits.Any(h => h.gameObject.GetComponentInParent<BoardCellView>()?.Coordinate == cell), Is.True);
            Assert.That(hits.Any(h => h.gameObject.name == "BossBombMarker" || h.gameObject.name == "BombCountdown"), Is.False);
            Invoke("OnEndTurnRequested");
            Assert.That(marker.GetComponentInChildren<TMP_Text>().text, Does.Contain("1"));
            Invoke("OnEndTurnRequested");
            var settled = _bootstrap.Session.GetSnapshot();
            Assert.That(settled.BossBombs, Is.Empty);
            yield return WaitForBombFeedback(2);
            yield return null;
            Assert.That(_board.CellRect(cell).Find("BossBombMarker"), Is.Null);
            Assert.That(Object.FindObjectsOfType<Image>().Any(i => i.name == "BossBombFlash"), Is.True);
            yield return new WaitForSecondsRealtime(3f);
            Assert.That(Object.FindObjectsOfType<Image>().Any(i => i.name == "BossBombFlash"), Is.False);
            Assert.That(_bootstrap.Session.GetSnapshot().Units.Select(u => u.Hp), Is.EqualTo(settled.Units.Select(u => u.Hp)));
        }

        /// <summary>爆炸进行中重开取消旧队列和所有临时对象，后续帧不会重新生成旧效果。</summary>
        [UnityTest]
        public IEnumerator RestartDuringExplosionCancelsFutureStepsAndMarkers()
        {
            yield return Load(); Deploy(new Cell(0, 4));
            yield return WaitForPresentation();
            Invoke("OnEndTurnRequested"); Invoke("OnEndTurnRequested");
            yield return WaitForBombFeedback(2);
            _bootstrap.RestartWith(_configuration);
            yield return null;
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(_bootstrap.Session.GetSnapshot().BossBombs, Is.Empty);
            Assert.That(Object.FindObjectsOfType<Image>().Any(i => i.name == "BossBombFlash" || i.name == "BossBombMarker"), Is.False);
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>跳过按钮解除普通操作闸门，未部署时棋盘不产生炸弹标记。</summary>
        [UnityTest]
        public IEnumerator SkipButtonReleasesManualTurn()
        {
            yield return Load();
            Invoke("OnEndTurnRequested");
            Assert.That(_bootstrap.Session.GetSnapshot().ActiveSide, Is.EqualTo(Side.Player));
            Assert.That(_bootstrap.Session.GetSnapshot().BossBombPending, Is.True);
            Invoke("OnBombSkipRequested");
            Assert.That(_bootstrap.Session.GetSnapshot().BossBombPending, Is.False);
            Assert.That(_bootstrap.Session.GetSnapshot().BossBombs, Is.Empty);
            Invoke("OnEndTurnRequested");
            Assert.That(_bootstrap.Session.GetSnapshot().ActiveSide, Is.EqualTo(Side.Boss));
        }
    }
}
