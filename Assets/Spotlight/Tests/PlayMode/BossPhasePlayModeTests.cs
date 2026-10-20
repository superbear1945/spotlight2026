using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;
using Button = UnityEngine.UIElements.Button;

namespace Spotlight.Tests
{
    /// <summary>阶段状态与真实帧循环集成回归；只通过命令和已有 UI 事件入口推进对局，不修改规则私有状态。</summary>
    public class BossPhasePlayModeTests
    {
        /// <summary>当前场景引导器，提供真实命令和重开入口。</summary>
        GameBootstrap _bootstrap;
        /// <summary>棋盘视图，用于核对位置与点击穿透。</summary>
        BoardView _board;
        /// <summary>隔离配置，重开时复用而不修改正式资产。</summary>
        GameConfiguration _configuration;
        /// <summary>测试前的正式配置快照，供清理阶段检查整份资产未被测试参数污染。</summary>
        string _sourceDocumentJson;
        /// <summary>保留正式资产引用，避免场景重开后无法检查源数据。</summary>
        SpotlightConfigAsset _sourceAsset;
        /// <summary>加载带音乐的正式场景前保留全局输出和时间缩放，避免影响其他帧循环测试。</summary>
        float _listenerVolume, _timeScale;

        /// <summary>记录全局状态；本夹具只通过独立对局配置推进阶段，不写音量偏好。</summary>
        [SetUp]
        public void SetUp() { _listenerVolume = AudioListener.volume; _timeScale = Time.timeScale; }

        /// <summary>加载演示场景并创建隔离配置。输出为可触发阶段、快速播放动画的手动对局。</summary>
        IEnumerator Load()
        {
            GameBootstrap.SuppressDeckSelectionOnStart = true;
            yield return SceneManager.LoadSceneAsync("Assets/Spotlight/Scenes/Spotlight.unity");
            yield return null;
            _bootstrap = Object.FindObjectOfType<GameBootstrap>(); _board = Object.FindObjectOfType<BoardView>();
            _sourceAsset = _bootstrap.CurrentAsset;
            _sourceDocumentJson = JsonUtility.ToJson(_sourceAsset.ToDocument());
            // 与已有 PlayMode 夹具一致：测试参数只写入独立文档，绝不写入加载中的正式 SO。
            var d = JsonUtility.FromJson<ConfigDocument>(_sourceDocumentJson);
            var r = d.rules[0]; r.playerControlsBoss = true; r.firstSide = "player";
            r.homeRegionWidth = 10; r.playerPlays = 30; r.bossPlays = 10;
            r.drawCount = 20; r.handLimit = 50; r.bossBombInterval = 1000;
            r.bossSpawnerAttackThreshold = 10000; r.summoningSickness = false;
            var soldier = d.cards.Single(c => c.id == "blade1"); soldier.atk = 20; soldier.range = 30; soldier.upkeep = 0;
            d.presentation[0].moveSeconds = .03f; d.presentation[0].feedbackSeconds = .01f;
            d.presentation[0].musicFadeOutSeconds = .03f; d.presentation[0].musicPhaseGapSeconds = .03f;
            _configuration = new GameConfiguration(d).WithDeck(new[] { new DeckEntry(soldier.id, 20) }, false); _bootstrap.RestartWith(_configuration);
            yield return null;
        }

        /// <summary>每个测试结束（包括中途断言失败）时核对所有正式表行，防止后续对局继承测试参数。</summary>
        [TearDown]
        public void FormalConfigurationRemainsUnchanged()
        {
            if (_bootstrap != null) _bootstrap.GetComponent<BossMusicController>()?.StopImmediately();
            AudioListener.volume = _listenerVolume;
            Time.timeScale = _timeScale;
            if (_sourceAsset != null && _sourceDocumentJson != null)
                Assert.That(JsonUtility.ToJson(_sourceAsset.ToDocument()), Is.EqualTo(_sourceDocumentJson),
                    "阶段测试污染了正式配置资产");
            GameBootstrap.SuppressDeckSelectionOnStart = false;
            _sourceAsset = null;
            _sourceDocumentJson = null;
        }

        /// <summary>提交到真实 UI 入口。输入为命令；输出为事件、规则状态与动画队列，并断言命令成功。</summary>
        void Submit(GameCommand command)
        {
            var result = (CommandResult)typeof(GameBootstrap).GetMethod("Submit", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_bootstrap, new object[] { command });
            Assert.That(result, Is.Not.Null);
            Assert.That(result.Success, Is.True, result.Error);
        }

        /// <summary>有限等待当前动画队列完成，断言规则输入闸门已恢复，避免测试无限挂起。</summary>
        IEnumerator WaitForPresentation()
        {
            var deadline = Time.realtimeSinceStartup + 10;
            while (_bootstrap.IsPresentingTurn && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(_bootstrap.IsPresentingTurn, Is.False);
            yield return null;
        }

        /// <summary>三阶段顶替删除旧卡面、生成九艘可点击穿透的新护卫舰，属性来自隔离配置；重开清除阶段视图。</summary>
        [UnityTest]
        public IEnumerator PhaseThreeReplacesViewsAndUsesDebugAttributes()
        {
            yield return Load();
            var d = _sourceAsset.ToDocument();
            d.rules[0].playerControlsBoss = true; d.rules[0].homeRegionWidth = 10;
            d.rules[0].playerPlays = 30; d.rules[0].drawCount = 20; d.rules[0].handLimit = 50;
            d.rules[0].summoningSickness = false; d.rules[0].bossBombInterval = 1000;
            d.rules[0].bossSpawnerAttackThreshold = 10000;
            var soldier = d.cards.Single(c => c.id == "blade1"); soldier.atk = 20; soldier.range = 30; soldier.upkeep = 0;
            var guard = d.cards.Single(c => c.id == d.rules[0].bossPhaseThreeFrontCardType);
            guard.hp = 12; guard.atk = 4; guard.range = 3;
            d.presentation[0].moveSeconds = .01f; d.presentation[0].feedbackSeconds = .01f;
            d.presentation[0].musicFadeOutSeconds = .03f; d.presentation[0].musicPhaseGapSeconds = .03f;
            _configuration = new GameConfiguration(d).WithDeck(new[] { new DeckEntry(soldier.id, 20) }, false);
            _bootstrap.RestartWith(_configuration); yield return WaitForPresentation();
            var attackers = new long[4];
            for (var i = 0; i < 4; i++)
            {
                var card = _bootstrap.Session.GetSnapshot().Hand.First();
                var cell = i == 3 ? new Cell(0, 8) : new Cell(0, i);
                Submit(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, cell));
                yield return WaitForPresentation();
                attackers[i] = _bootstrap.Session.GetSnapshot().Units.Single(u => u.SourceCardId == card.Id).Id;
            }
            Submit(new GameCommand(CommandKind.Attack, Side.Player, attackers[0], targetId: -2));
            yield return WaitForPresentation();
            var ships = _bootstrap.Session.GetSnapshot().BossWarships.ToArray();
            for (var i = 0; i < 2; i++)
            {
                Submit(new GameCommand(CommandKind.Attack, Side.Player, attackers[i + 1], targetId: ships[i].UnitId.Value));
                yield return WaitForPresentation();
            }
            var state = _bootstrap.Session.GetSnapshot();
            var guards = state.Units.Where(u => u.AiTargetPolicy == AiTargetPolicy.BossGuard).ToArray();
            Assert.That(guards.Length, Is.EqualTo(9));
            Assert.That(state.BossInvulnerable, Is.False);
            Assert.That(_board.GetCard(attackers[3]), Is.Null);
            foreach (var unit in guards)
            {
                var view = _board.GetCard(unit.Id);
                Assert.That(view, Is.Not.Null);
                Assert.That(view.transform.parent, Is.EqualTo(_board.CellRect(unit.Position)));
                Assert.That(view.GetComponentsInChildren<Graphic>().All(g => !g.raycastTarget), Is.True);
                if (unit.TypeId == guard.id)
                {
                    Assert.That(new[] { unit.Hp, unit.Attack, unit.Range }, Is.EqualTo(new[] { 12, 4, 3 }));
                    var text = string.Join(" ", view.GetComponentsInChildren<TMPro.TMP_Text>().Select(t => t.text));
                    Assert.That(text, Does.Contain("12"));
                }
            }
            Assert.That(Object.FindObjectOfType<HudView>().Root.Q<Label>("boss-phase-label").text, Does.Contain("第 3 阶段"));
            var music = _bootstrap.GetComponent<BossMusicController>();
            Assert.That(music.TargetPhase, Is.EqualTo(BossPhase.PhaseThree));
            Submit(new GameCommand(CommandKind.EndTurn, Side.Player, skipDeployment: true));
            yield return WaitForPresentation();
            Submit(new GameCommand(CommandKind.EndTurn, Side.Boss, skipDeployment: true));
            yield return WaitForPresentation();
            Submit(new GameCommand(CommandKind.Attack, Side.Player, attackers[0], targetId: -2));
            yield return WaitForPresentation();
            Assert.That(_bootstrap.Session.GetSnapshot().Phase, Is.EqualTo(MatchPhase.Finished));
            yield return new WaitForSecondsRealtime(.1f);
            Assert.That(music.State, Is.EqualTo(MusicPlaybackState.Stopped));
            _bootstrap.RestartWith(_configuration); yield return WaitForPresentation();
            Assert.That(music.TargetPhase, Is.EqualTo(BossPhase.PhaseOne));
            Assert.That(_bootstrap.Session.GetSnapshot().BossPhase, Is.EqualTo(BossPhase.PhaseOne));
            foreach (var unit in guards) Assert.That(_board.GetCard(unit.Id), Is.Null);
            LogAssert.NoUnexpectedReceived();
        }

        /// <summary>自动部署战舰、锁血提示、手动放牌与真实移动动画完整连接；重开清除阶段和旧视图。</summary>
        [UnityTest]
        public IEnumerator PhaseTransitionShipsMoveAndRestartWithoutStaleViews()
        {
            yield return Load();
            var card = _bootstrap.Session.GetSnapshot().Hand.First();
            Submit(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(0, 0)));
            yield return WaitForPresentation();
            var attacker = _bootstrap.Session.GetSnapshot().Units.Single().Id;
            Submit(new GameCommand(CommandKind.Attack, Side.Player, attacker, targetId: -2));
            yield return WaitForPresentation();
            var root = Object.FindObjectOfType<HudView>().Root;
            var state = _bootstrap.Session.GetSnapshot();
            Assert.That(root.Q<Label>("boss-info-label"), Is.Null, "旧侧栏已废弃：Boss 阶段与战舰统计只出现在顶部摘要");
            var summary = root.Q<Label>("boss-phase-label");
            Assert.That(summary.text, Does.Contain("第 2 阶段").And.Contain("锁血").And.Contain("存活 2"));
            Assert.That(summary.visible, Is.True);
            Assert.That(_bootstrap.GetComponent<BossMusicController>().TargetPhase, Is.EqualTo(BossPhase.PhaseTwo));
            Assert.That(root.Q<Button>("spawner-deploy-button").resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            foreach (var record in state.BossWarships)
            {
                var view = _board.GetCard(record.UnitId.Value);
                Assert.That(view.Spawn, Is.Not.Null);
                Assert.That(view.Spawn.State.Id, Is.EqualTo(record.UnitId));
                Assert.That(view.GetComponentsInChildren<Graphic>().All(g => !g.raycastTarget), Is.True);
            }
            Submit(new GameCommand(CommandKind.EndTurn, Side.Player, skipDeployment: true));
            yield return WaitForPresentation();
            Assert.That(_bootstrap.Session.GetSnapshot().PlaysRemaining, Is.EqualTo(10));
            var ship = state.BossWarships[0].UnitId.Value;
            var destination = _bootstrap.Session.GetLegalActions(new Selection(SelectionKind.Unit, ship)).Move.First();
            Submit(new GameCommand(CommandKind.Move, Side.Boss, ship, destination));
            yield return new WaitForSeconds(.15f);
            yield return WaitForPresentation();
            Assert.That(_board.GetCard(ship).transform.parent, Is.EqualTo(_board.CellRect(destination)));
            var free = _bootstrap.Session.GetLegalActions(new Selection(SelectionKind.BossDeployment)).Deploy.First();
            Submit(new GameCommand(CommandKind.BossDeploy, Side.Boss, target: free));
            yield return WaitForPresentation();
            Assert.That(_bootstrap.Session.GetSnapshot().PlaysRemaining, Is.EqualTo(9));
            _bootstrap.RestartWith(_configuration); yield return WaitForPresentation();
            Assert.That(_bootstrap.Session.GetSnapshot().BossPhase, Is.EqualTo(BossPhase.PhaseOne));
            Assert.That(_bootstrap.Session.GetSnapshot().BossWarships, Is.Empty);
            Assert.That(_bootstrap.Session.GetSnapshot().Units, Is.Empty);
            Assert.That(_board.GetCard(ship), Is.Null);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
