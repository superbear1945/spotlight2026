// 棋盘（UGUI）与规则快照一致性的 PlayMode 回归测试。
// 为什么必须在 PlayMode 验证：移动是「视图重新挂载 + DOTween 动画」的组合，
// 只有真实帧循环才能暴露“规则层已经移动、但卡牌仍停留在原格 / 被缩放到不可见”这类只在运行时出现的问题。
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Spotlight;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Spotlight.Tests
{
    /// <summary>
    /// 棋盘交互与渲染的端到端测试。
    /// 输入：演示场景（真实配置资产 + 真实视图装配）；输出：卡牌父子关系、坐标、缩放与点击落点断言。
    /// 用例通过手牌 UGUI 拖动及既有棋盘输入入口驱动，复现“拖拽部署 → 结束回合 → 点击单位移动”的路径。
    /// </summary>
    public class BoardMovePlayModeTests
    {
        /// <summary>演示场景路径（与 SpotlightMenu.DemoScenePath 一致）。</summary>
        const string ScenePath = "Assets/Spotlight/Scenes/Spotlight.unity";

        /// <summary>等待动画结束的实时秒数（配置的移动动画为 0.2s，留足余量）。</summary>
        const float SettleSeconds = 1f;

        /// <summary>卡牌偏移小于该值即视为已经落在格子中心。</summary>
        const float CenterTolerance = 0.5f;

        /// <summary>
        /// 移动单位后，卡牌必须挂到目标格、在配置的动画时长内回到格心，并且不会停在旧格。
        /// 输入：无；输出：NUnit 断言结果。
        /// </summary>
        [UnityTest]
        public IEnumerator MovePutsCardInTargetCell()
        {
            var context = new SceneContext();
            yield return context.Load();
            var bootstrap = context.Bootstrap;
            var session = context.Session;
            var board = context.Board;

            var deployed = default(CardRecord);
            var deployCell = default(Cell);
            foreach (var record in session.GetSnapshot().Hand)
            {
                var options = session.GetLegalActions(new Selection(SelectionKind.Hand, record.Id));
                if (options.Deploy.Count <= 0) continue;
                deployed = record;
                deployCell = options.Deploy[0];
                break;
            }
            Assert.That(deployed, Is.Not.Null, "初始手牌里没有可部署的卡");
            HandDragInput.Deploy(context.Hand.GetCard(deployed.Id), board, deployCell);
            yield return WaitSeconds(SettleSeconds);

            var deployedUnit = session.GetSnapshot().Units.FirstOrDefault(u => u.SourceCardId == deployed.Id);
            Assert.That(deployedUnit, Is.Not.Null, "部署后棋盘上没有对应单位");

            // 推进回合直到有己方单位可以移动（覆盖召唤失调与 Boss AI 回合）。
            // 不绑定刚部署的单位：它可能在等待期间被 AI 击杀，因此每轮都补一张带移动距离的牌，
            // 断言只关心“界面上的某个棋子确实跟着规则移动”。
            UnitState live = null;
            for (var turn = 0; turn < 10 && live == null; turn++)
            {
                TryDeploy(bootstrap, session, context.Hand, requireMoveDistance: true);
                yield return WaitSeconds(SettleSeconds);
                live = session.GetSnapshot().Units.FirstOrDefault(u =>
                    u.Owner == Side.Player &&
                    session.GetLegalActions(new Selection(SelectionKind.Unit, u.Id)).Move.Count > 0);
                if (live != null) break;
                Invoke(bootstrap, "OnEndTurnRequested");
                yield return null;
                yield return FinishDiscard(bootstrap, session, context.Hand);
            }

            Assert.That(live, Is.Not.Null, "推进 10 个回合后仍没有可移动的己方单位");
            var moveTargets = session.GetLegalActions(new Selection(SelectionKind.Unit, live.Id)).Move;
            Assert.That(moveTargets.Count, Is.GreaterThan(0), "单位始终无法移动，无法验证移动渲染");

            var from = live.Position;
            var target = moveTargets[0];
            var card = board.GetCard(live.Id);
            Assert.That(card, Is.Not.Null, "棋盘上缺少该单位的卡牌视图");
            Assert.That(card.transform.parent, Is.EqualTo(board.CellRect(from)), "移动前卡牌应挂在起始格");

            Invoke(bootstrap, "OnCellClicked", from);
            Invoke(bootstrap, "OnCellClicked", target);
            var moved = session.GetSnapshot().Units.FirstOrDefault(u => u.Id == live.Id);
            Assert.That(moved, Is.Not.Null, "移动后单位消失了");
            Assert.That(moved.Position, Is.EqualTo(target), "规则层没有移动到目标格");

            var targetRect = board.CellRect(target);
            Assert.That(targetRect, Is.Not.Null, $"棋盘上找不到目标格 {target}");
            var rect = (RectTransform)card.transform;
            // 动画必须真的播放：移动当帧卡牌仍在起点側的偏移位置（否则说明视图只在硬切位置）。
            Assert.That(rect.anchoredPosition.magnitude, Is.GreaterThan(1f), "移动没有播放动画：卡牌在移动当帧就直接落在格心");
            var deadline = Time.realtimeSinceStartup + SettleSeconds;
            while (rect.anchoredPosition.magnitude > CenterTolerance && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.That(card.transform.parent, Is.EqualTo(targetRect), "移动后卡牌没有挂到目标格");
            Assert.That(rect.anchoredPosition.magnitude, Is.LessThan(CenterTolerance), $"移动动画结束后卡牌没有回到格心（偏移 {rect.anchoredPosition}）");
            Assert.That(rect.localScale.x, Is.GreaterThan(0.5f), $"移动后卡牌缩放异常：{rect.localScale}");

            // 再看几帧：动画结束后不允许被补间重新推到旧格附近。
            for (var frame = 0; frame < 10; frame++) yield return null;
            Assert.That(rect.anchoredPosition.magnitude, Is.LessThan(CenterTolerance), "动画结束后卡牌又偏离了格心");
        }

        /// <summary>
        /// 连续推进若干回合，每一步都断言“棋盘上每张卡牌都挂在规则快照对应的格子里”。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：这条不变式同时能抓住“卡牌留在原格”“卡牌消失”“卡牌被缩放到不可见”三类视图失步问题。
        /// </summary>
        [UnityTest]
        public IEnumerator BoardCardsFollowSnapshotAcrossTurns()
        {
            var context = new SceneContext();
            yield return context.Load();
            var bootstrap = context.Bootstrap;
            var session = context.Session;
            var board = context.Board;

            for (var step = 0; step < 10; step++)
            {
                yield return WaitSeconds(SettleSeconds);
                AssertCardsMatchSnapshot(board, session, $"步骤 {step}（回合 {session.GetSnapshot().Round}）");

                TryDeploy(bootstrap, session, context.Hand, requireMoveDistance: false);
                yield return WaitSeconds(SettleSeconds);
                AssertCardsMatchSnapshot(board, session, $"步骤 {step} 部署后");

                if (session.GetSnapshot().Phase == MatchPhase.Finished) break;
                Invoke(bootstrap, "OnEndTurnRequested");
                yield return null;
                yield return FinishDiscard(bootstrap, session, context.Hand);
            }
        }

        /// <summary>
        /// 点击棋盘上的棋子时，UGUI 事件必须落到棋子的格子上。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：卡牌模板自带 Image/文本/Button，默认会抢走射线；
        /// 一旦被抢走，玩家“点棋子选单位 / 点相邻牌换位 / 点自己的家”都会毫无反应。
        /// </summary>
        [UnityTest]
        public IEnumerator ClickingAPieceReachesItsCell()
        {
            var context = new SceneContext();
            yield return context.Load();
            var bootstrap = context.Bootstrap;
            var session = context.Session;
            var board = context.Board;

            TryDeploy(bootstrap, session, context.Hand, requireMoveDistance: true);
            yield return WaitSeconds(SettleSeconds);

            var unit = session.GetSnapshot().Units.FirstOrDefault(u => u.Owner == Side.Player);
            Assert.That(unit, Is.Not.Null, "没有部署成功任何己方单位");
            var card = board.GetCard(unit.Id);
            var cellRect = board.CellRect(unit.Position);
            Assert.That(card, Is.Not.Null, "棋盘上缺少该单位的卡牌视图");
            Assert.That(cellRect, Is.Not.Null, $"棋盘上找不到格子 {unit.Position}");

            var canvas = Object.FindObjectOfType<Canvas>();
            var raycaster = Object.FindObjectOfType<GraphicRaycaster>();
            Assert.That(EventSystem.current, Is.Not.Null, "场景缺少 EventSystem");
            Assert.That(raycaster, Is.Not.Null, "场景缺少 GraphicRaycaster");

            var pointer = new PointerEventData(EventSystem.current)
            {
                position = RectTransformUtility.WorldToScreenPoint(canvas != null ? canvas.worldCamera : null, card.transform.position)
            };
            var hits = new List<RaycastResult>();
            raycaster.Raycast(pointer, hits);
            var names = string.Join(" | ", hits.Select(h => h.gameObject.name));
            Debug.Log($"[点击落点] 屏幕 {Screen.width}x{Screen.height} 命中：{names}");
            Assert.That(hits.Count, Is.GreaterThan(0), "棋子上没有任何可点击的图形");

            var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject);
            Assert.That(handler, Is.Not.Null, "最上层命中对象没有点击处理者");
            Assert.That(handler, Is.EqualTo(cellRect.gameObject),
                $"点击棋子被 {hits[0].gameObject.name} 吞掉了，格子收不到点击（命中顺序：{names}）");
        }

        /// <summary>
        /// 选中我方单位后出现“离场”按钮；点击（走 HUD 同一入口）后单位消失、牌区总数不变、按钮隐藏。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：离场是“规则层新命令 + HUD 按钮”的组合，必须验证按钮显隐来自规则层给出的合法性，
        /// 以及离场不会让来源卡重复入堆（牌区总数守恒）。
        /// </summary>
        [UnityTest]
        public IEnumerator WithdrawButtonRemovesSelectedUnit()
        {
            var context = new SceneContext();
            yield return context.Load();
            var session = context.Session;
            var board = context.Board;
            var hud = Object.FindObjectOfType<HudView>();
            Assert.That(hud, Is.Not.Null, "场景缺少 HudView");
            var button = hud.Root.Q<VisualElement>("withdraw-button");
            Assert.That(button, Is.Not.Null, "HUD 的 UXML 缺少 withdraw-button");
            Assert.That(button.style.display.value, Is.EqualTo(DisplayStyle.None), "未选中单位时不该显示离场按钮");

            Assert.That(TryDeploy(context.Bootstrap, session, context.Hand, requireMoveDistance: false), Is.True, "初始手牌里没有可部署的卡");
            yield return WaitSeconds(SettleSeconds);
            var unit = session.GetSnapshot().Units.FirstOrDefault(u => u.Owner == Side.Player);
            Assert.That(unit, Is.Not.Null, "部署后棋盘上没有己方单位");

            Invoke(context.Bootstrap, "OnCellClicked", unit.Position);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(button.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.Flex), "选中己方单位后没有出现离场按钮");

            var before = session.GetSnapshot();
            Invoke(context.Bootstrap, "OnWithdrawRequested");
            yield return WaitSeconds(SettleSeconds);
            var after = session.GetSnapshot();
            Assert.That(after.Units.Any(u => u.Id == unit.Id), Is.False, "离场后单位仍在场上");
            Assert.That(board.GetCard(unit.Id), Is.Null, "离场后棋盘上仍残留该单位的卡牌视图");
            Assert.That(after.Hand.Count + after.DrawPile.Count + after.Graveyard.Count,
                Is.EqualTo(before.Hand.Count + before.DrawPile.Count + before.Graveyard.Count), "离场改变了牌区总数");
            Assert.That(button.style.display.value, Is.EqualTo(UnityEngine.UIElements.DisplayStyle.None), "离场后离场按钮应隐藏");
        }

        /// <summary>
        /// 齿轮在两套界面之间切换：默认是战斗界面（左右详情区与卡牌计数可见、日志与 Boss 状态隐藏），
        /// 点齿轮后隐藏战斗界面、显示原有面板与「继续 / 重开」，点「继续」恢复；菜单打开期间规则层不接受操作。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：原有面板没有删除，只是移进菜单，必须防止以后误删或忘记恢复，也要确认暂停真的挡住了输入。
        /// </summary>
        [UnityTest]
        public IEnumerator MenuButtonSwapsBattleAndLegacyHud()
        {
            var context = new SceneContext();
            yield return context.Load();
            var hud = Object.FindObjectOfType<HudView>();
            Assert.That(hud, Is.Not.Null, "场景缺少 HudView");
            var root = hud.Root;

            var detailLeft = root.Q<VisualElement>("detail-left");
            var pileLeft = root.Q<VisualElement>("pile-left");
            var logPanel = root.Q<VisualElement>("log-panel");
            var sidePanel = root.Q<VisualElement>("side-panel");
            var menuOverlay = root.Q<VisualElement>("menu-overlay");
            var menuButton = root.Q<VisualElement>("menu-button");
            Assert.That(detailLeft, Is.Not.Null, "UXML 缺少 detail-left");
            Assert.That(pileLeft, Is.Not.Null, "UXML 缺少 pile-left");
            Assert.That(logPanel, Is.Not.Null, "UXML 缺少 log-panel");
            Assert.That(sidePanel, Is.Not.Null, "UXML 缺少 side-panel");
            Assert.That(menuOverlay, Is.Not.Null, "UXML 缺少 menu-overlay");
            Assert.That(menuButton, Is.Not.Null, "UXML 缺少 menu-button");
            // 齿轮按钮的全部外观就是那张背景图标（gear.png，由 .action-button.menu-button 指定）：
            // 图标读不到就会剩下一个空方块，玩家再也找不到菜单入口，所以这里既不让它退回文字，也要求图标真的解析出来。
            var gear = (UnityEngine.UIElements.Button)menuButton;
            Assert.That(gear.text, Is.Empty, "齿轮按钮只显示图标，不应再有文字");
            Assert.That(gear.resolvedStyle.backgroundImage.texture != null || gear.resolvedStyle.backgroundImage.sprite != null,
                "齿轮按钮没有读到背景图标（gear.png），菜单入口会变成一个空方块");
            var debugButton = root.Q<VisualElement>("debug-button");
            Assert.That(debugButton, Is.Not.Null, "UXML 缺少 debug-button");
            Assert.That(menuOverlay.Contains(debugButton), Is.True, "“配置/调试”入口应当位于齿轮菜单里");

            Assert.That(detailLeft.style.display.value, Is.EqualTo(DisplayStyle.Flex), "默认应显示战斗界面的左右详情区");
            Assert.That(pileLeft.style.display.value, Is.EqualTo(DisplayStyle.Flex), "默认应显示卡牌计数区");
            Assert.That(logPanel.style.display.value, Is.EqualTo(DisplayStyle.None), "默认应隐藏对局日志");
            Assert.That(sidePanel.style.display.value, Is.EqualTo(DisplayStyle.None), "默认应隐藏 Boss 状态");
            Assert.That(menuOverlay.style.display.value, Is.EqualTo(DisplayStyle.None), "默认不该显示菜单");

            Invoke(context.Bootstrap, "OnMenuRequested");
            yield return WaitSeconds(SettleSeconds);
            Assert.That(detailLeft.style.display.value, Is.EqualTo(DisplayStyle.None), "菜单打开时应隐藏战斗界面");
            Assert.That(pileLeft.style.display.value, Is.EqualTo(DisplayStyle.None), "菜单打开时应隐藏卡牌计数区");
            Assert.That(logPanel.style.display.value, Is.EqualTo(DisplayStyle.Flex), "菜单打开时应显示对局日志");
            Assert.That(sidePanel.style.display.value, Is.EqualTo(DisplayStyle.Flex), "菜单打开时应显示 Boss 状态");
            Assert.That(menuOverlay.style.display.value, Is.EqualTo(DisplayStyle.Flex), "菜单打开时应显示遮罩与两个按钮");

            // 菜单是暂停状态：结束回合必须被规则层拒绝，回合数不推进。
            var pausedRound = context.Session.GetSnapshot().Round;
            Invoke(context.Bootstrap, "OnEndTurnRequested");
            yield return WaitSeconds(SettleSeconds);
            Assert.That(context.Session.GetSnapshot().Round, Is.EqualTo(pausedRound), "菜单打开期间不应能结束回合");

            Invoke(context.Bootstrap, "OnMenuResumeRequested");
            yield return WaitSeconds(SettleSeconds);
            Assert.That(detailLeft.style.display.value, Is.EqualTo(DisplayStyle.Flex), "继续后应恢复战斗界面");
            Assert.That(logPanel.style.display.value, Is.EqualTo(DisplayStyle.None), "继续后应重新隐藏日志");
            Assert.That(menuOverlay.style.display.value, Is.EqualTo(DisplayStyle.None), "继续后应关闭菜单");
        }

        /// <summary>
        /// 齿轮菜单里的“退出游戏”必须走到宿主退出动作：按钮位于菜单内，点击后只调用退出动作一次，且不改动对局。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：退出是菜单里唯一“离开对局生命周期”的动作（其他项都只是暂停或重建对局），
        /// 一旦接错或被写成“结束回合”这类规则操作，玩家就会在毫不知情的情况下丢掉当前对局。
        /// 为什么要把 QuitAction 换成计数委托：测试不能真的结束运行中的编辑器/播放会话。
        /// </summary>
        [UnityTest]
        public IEnumerator MenuQuitButtonRequestsQuit()
        {
            var context = new SceneContext();
            yield return context.Load();
            var hud = Object.FindObjectOfType<HudView>();
            Assert.That(hud, Is.Not.Null, "场景缺少 HudView");
            var menuOverlay = hud.Root.Q<VisualElement>("menu-overlay");
            Assert.That(menuOverlay, Is.Not.Null, "UXML 缺少 menu-overlay");
            var quitButton = hud.Root.Q<UnityEngine.UIElements.Button>("menu-quit-button");
            Assert.That(quitButton, Is.Not.Null, "UXML 缺少 menu-quit-button");
            Assert.That(menuOverlay.Contains(quitButton), Is.True, "“退出游戏”入口应当位于齿轮菜单里");
            Assert.That(quitButton.text, Is.EqualTo("退出游戏"), "退出按钮的文案必须能看出会离开当前对局");

            // 替换退出动作，避免测试真的结束运行会话。
            var quitCalls = 0;
            context.Bootstrap.QuitAction = () => quitCalls++;

            Invoke(context.Bootstrap, "OnMenuRequested");
            yield return WaitSeconds(SettleSeconds);
            Assert.That(menuOverlay.style.display.value, Is.EqualTo(DisplayStyle.Flex), "菜单打开时才能看到退出按钮");

            var before = context.Session.GetSnapshot();
            Invoke(context.Bootstrap, "OnQuitRequested");
            yield return WaitSeconds(SettleSeconds);
            Assert.That(quitCalls, Is.EqualTo(1), "点击“退出游戏”没有触发宿主退出动作");

            var after = context.Session.GetSnapshot();
            Assert.That(after.Round, Is.EqualTo(before.Round), "退出不该推进对局");
            Assert.That(after.Units.Count, Is.EqualTo(before.Units.Count), "退出不该改动棋盘上的单位");
            Assert.That(after.Hand.Count, Is.EqualTo(before.Hand.Count), "退出不该改动手牌");
        }

        /// <summary>
        /// 左右详情区采用单选中槽：我方手牌/单位只填左侧，敌方单位只填右侧，选中家时两侧都清空。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：详情区是给玩家的信息展示（同时充当后续功能的占位），
        /// 必须保证“同一时刻只有一侧有内容”，否则会误导成两个对象同时被选中。
        /// </summary>
        [UnityTest]
        public IEnumerator DetailPanelsShowFriendlyOnLeftAndEnemyOnRight()
        {
            var context = new SceneContext();
            yield return context.Load();
            var hud = Object.FindObjectOfType<HudView>();
            Assert.That(hud, Is.Not.Null, "场景缺少 HudView");
            var leftBody = hud.Root.Q<Label>("detail-left-body");
            var rightBody = hud.Root.Q<Label>("detail-right-body");
            Assert.That(leftBody, Is.Not.Null, "UXML 缺少 detail-left-body");
            Assert.That(rightBody, Is.Not.Null, "UXML 缺少 detail-right-body");
            Assert.That(leftBody.text, Is.Empty, "未选中时左侧详情应为空");
            Assert.That(rightBody.text, Is.Empty, "未选中时右侧详情应为空");

            var record = context.Session.GetSnapshot().Hand.First();
            Invoke(context.Bootstrap, "OnCardClicked", context.Hand.GetCard(record.Id));
            yield return WaitSeconds(SettleSeconds);
            Assert.That(leftBody.text, Is.Not.Empty, "选中我方手牌后左侧详情应有卡面信息");
            Assert.That(rightBody.text, Is.Empty, "选中我方手牌时右侧应保持为空");

            var home = context.Session.GetSnapshot().Homes.First(h => h.Owner == Side.Player);
            Invoke(context.Bootstrap, "OnCellClicked", home.Position);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(leftBody.text, Is.Empty, "选中家时左侧详情应清空（家不在卡面区显示）");
            Assert.That(rightBody.text, Is.Empty, "选中家时右侧详情应清空");

            // 结束回合让 Boss 部署，等回放结束后选中敌方单位。
            Invoke(context.Bootstrap, "OnEndTurnRequested");
            UnitState enemy = null;
            var deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                enemy = context.Session.GetSnapshot().Units.FirstOrDefault(u => u.Owner == Side.Boss);
                if (enemy != null && !context.Bootstrap.IsPresentingTurn) break;
            }
            Assert.That(enemy, Is.Not.Null, "Boss 回合结束后场上仍没有敌方单位");
            Invoke(context.Bootstrap, "OnCellClicked", enemy.Position);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(rightBody.text, Is.Not.Empty, "选中敌方单位后右侧详情应有卡面信息");
            Assert.That(leftBody.text, Is.Empty, "选中敌方单位时左侧应保持为空");
        }

        /// <summary>
        /// 墓地与抽牌堆的计数必须跟随快照（部署后来源卡进墓地，数字同步变化）。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：计数区目前只显示数字，但它是后续“查看牌区列表”的入口位置，先固定“数字来自快照”这条契约。
        /// </summary>
        [UnityTest]
        public IEnumerator PileCountersFollowSnapshot()
        {
            var context = new SceneContext();
            yield return context.Load();
            var hud = Object.FindObjectOfType<HudView>();
            Assert.That(hud, Is.Not.Null, "场景缺少 HudView");
            var grave = hud.Root.Q<Label>("grave-count");
            var draw = hud.Root.Q<Label>("draw-count");
            Assert.That(grave, Is.Not.Null, "UXML 缺少 grave-count");
            Assert.That(draw, Is.Not.Null, "UXML 缺少 draw-count");

            var start = context.Session.GetSnapshot();
            Assert.That(grave.text, Is.EqualTo(start.Graveyard.Count.ToString()), "墓地计数与快照不一致");
            Assert.That(draw.text, Is.EqualTo(start.DrawPile.Count.ToString()), "抽牌堆计数与快照不一致");

            Assert.That(TryDeploy(context.Bootstrap, context.Session, context.Hand, requireMoveDistance: false), Is.True, "初始手牌里没有可部署的卡");
            yield return WaitSeconds(SettleSeconds);
            var after = context.Session.GetSnapshot();
            Assert.That(after.Graveyard.Count, Is.GreaterThan(start.Graveyard.Count), "部署成功后来源卡应进入墓地");
            Assert.That(grave.text, Is.EqualTo(after.Graveyard.Count.ToString()), "部署后墓地计数没有跟随快照");
            Assert.That(draw.text, Is.EqualTo(after.DrawPile.Count.ToString()), "部署后抽牌堆计数没有跟随快照");
        }

        /// <summary>
        /// 开局第一帧渲染后，家的卡牌必须按所在格的实际尺寸布局，而不是布局完成前的占位尺寸。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么必须放在 PlayMode：格子尺寸由 GridLayoutGroup 在真实 Canvas 布局中算出，
        /// EditMode 里代码搭出来的层级没有 Canvas 布局，算出的格子会被 16 的下限夹住，
        /// 复现不了“Build 用 64×64 占位、真实尺寸稍后才生效”的时序。
        /// 这条用例是“家没有占满所在格”的直接回归防线：修复前卡牌是 56×56，修复后是格子尺寸减 8。
        /// </summary>
        [UnityTest]
        public IEnumerator HomeCardsFillTheirCellRightAfterStart()
        {
            var context = new SceneContext();
            yield return context.Load();
            var homes = context.Session.GetSnapshot().Homes;
            Assert.That(homes.Count, Is.EqualTo(2), "开局应当有两个家");

            foreach (var home in homes)
            {
                var card = context.Board.GetCard(home.Id);
                Assert.That(card, Is.Not.Null, "棋盘上缺少家的卡牌");
                var cell = context.Board.CellRect(home.Position);
                Assert.That(cell, Is.Not.Null, "棋盘上缺少家所在的格子");
                var rect = (RectTransform)card.transform;
                Assert.That(rect.localScale.x, Is.EqualTo(1f).Within(0.01f), $"家不应被缩放：{rect.localScale}");
                Assert.That(rect.sizeDelta.x, Is.EqualTo(cell.rect.size.x - 8f).Within(0.5f),
                    $"家没有按格子宽度布局：卡牌 {rect.sizeDelta.x}，格子 {cell.rect.size.x}");
                Assert.That(rect.sizeDelta.y, Is.EqualTo(cell.rect.size.y - 8f).Within(0.5f),
                    $"家没有按格子高度布局：卡牌 {rect.sizeDelta.y}，格子 {cell.rect.size.y}");
            }
        }

        /// <summary>
        /// 切换选中对象时，上一张卡牌的放大表现必须恢复原状。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：选中表现由「选中标记」（每次渲染全量重算）与「放大补间」（只在点击时播放）两部分组成，
        /// 过去没有任何地方播放取消，于是点第二张牌后第一张永远停在放大状态，看起来像“没有回到手牌里”。
        /// 这里断言的不只是手牌互选，还包括“改选自己的家”这种不播放放大的选择路径。
        /// </summary>
        [UnityTest]
        public IEnumerator SelectingAnotherCardOrHomeRestoresThePreviousCard()
        {
            var context = new SceneContext();
            yield return context.Load();
            var hand = context.Hand;
            var records = context.Session.GetSnapshot().Hand.ToArray();
            Assert.That(records.Length, Is.GreaterThanOrEqualTo(2), "手牌不足两张，无法验证选中切换");

            var first = hand.GetCard(records[0].Id);
            var second = hand.GetCard(records[1].Id);
            Assert.That(first, Is.Not.Null, "第一张手牌缺少视图");
            Assert.That(second, Is.Not.Null, "第二张手牌缺少视图");
            var firstRect = (RectTransform)first.transform;
            var secondRect = (RectTransform)second.transform;

            // 点第一张：它必须放大（这是既有的点击反馈，不能被本次修复破坏）。
            Invoke(context.Bootstrap, "OnCardClicked", first);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(firstRect.localScale.x, Is.GreaterThan(1.01f), $"点击第一张手牌后没有放大：{firstRect.localScale}");

            // 点第二张：第二张放大，第一张必须回到原始缩放。
            Invoke(context.Bootstrap, "OnCardClicked", second);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(secondRect.localScale.x, Is.GreaterThan(1.01f), $"点击第二张手牌后它没有放大：{secondRect.localScale}");
            Assert.That(firstRect.localScale.x, Is.EqualTo(1f).Within(0.01f), $"切换选中后第一张手牌没有恢复原状：{firstRect.localScale}");

            // 改选自己的家：家不参与放大表现，但上一张手牌同样必须恢复。
            var board = context.Board;
            var home = context.Session.GetSnapshot().Homes.First(h => h.Owner == Side.Player);
            Invoke(context.Bootstrap, "OnCellClicked", home.Position);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(secondRect.localScale.x, Is.EqualTo(1f).Within(0.01f), $"改选自己的家之后手牌没有恢复：{secondRect.localScale}");
            var homeRect = board.GetCard(home.Id) != null ? (RectTransform)board.GetCard(home.Id).transform : null;
            Assert.That(homeRect, Is.Not.Null, "棋盘上缺少家的卡牌视图");
            Assert.That(homeRect.localScale.x, Is.EqualTo(1f).Within(0.01f), $"家不应该被放大：{homeRect.localScale}");
        }

        /// <summary>
        /// 再次点击已经选中的手牌必须取消选中。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：过去重复点击同一张手牌没有任何响应，玩家无法主动放弃已选中的牌；
        /// 除了外观复位，这里还断言“取消之后点原来的落点不会落子”，证明选择确实被清空而不是只改了表现。
        /// </summary>
        [UnityTest]
        public IEnumerator ClickingTheSelectedHandCardAgainCancelsTheSelection()
        {
            var context = new SceneContext();
            yield return context.Load();
            var session = context.Session;
            var hand = context.Hand;

            // 找一张当前可部署的手牌，并记下它的一个合法落点（取消选中后该落点必须失效）。
            var chosen = default(CardRecord);
            var target = default(Cell);
            foreach (var record in session.GetSnapshot().Hand)
            {
                var options = session.GetLegalActions(new Selection(SelectionKind.Hand, record.Id));
                if (options.Deploy.Count <= 0) continue;
                chosen = record;
                target = options.Deploy[0];
                break;
            }
            Assert.That(chosen, Is.Not.Null, "初始手牌里没有可部署的卡");
            var card = hand.GetCard(chosen.Id);
            Assert.That(card, Is.Not.Null, "手牌缺少对应视图");
            var rect = (RectTransform)card.transform;

            // 第一次点击：进入选中状态并放大。
            Invoke(context.Bootstrap, "OnCardClicked", card);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(rect.localScale.x, Is.GreaterThan(1.01f), $"首次点击没有进入选中状态：{rect.localScale}");

            // 第二次点击同一张：取消选中并恢复原状。
            Invoke(context.Bootstrap, "OnCardClicked", card);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(rect.localScale.x, Is.EqualTo(1f).Within(0.01f), $"重复点击没有取消选中：{rect.localScale}");

            // 取消之后点击原先的落点不得落子：牌仍在手上，也没有产生部署结算。
            Invoke(context.Bootstrap, "OnCellClicked", target);
            yield return WaitSeconds(SettleSeconds);
            var snapshot = session.GetSnapshot();
            Assert.That(snapshot.Hand.Any(c => c.Id == chosen.Id), Is.True, "取消选中后仍然把牌打了出去");
            Assert.That(snapshot.Graveyard, Is.Empty, "取消选中后仍然产生了部署结算");
            Assert.That(snapshot.Units.Any(u => u.SourceCardId == chosen.Id), Is.False, "取消选中后棋盘上出现了该卡的单位");
        }

        /// <summary>
        /// 再次点击已经选中的棋盘单位必须取消选中。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：单位与手牌共用“点第二次取消”的交互，除了选中标记必须消失，
        /// 放大补间也必须复位，否则取消后棋子仍然比别的棋子大一圈。
        /// </summary>
        [UnityTest]
        public IEnumerator ClickingTheSelectedUnitAgainCancelsTheSelection()
        {
            var context = new SceneContext();
            yield return context.Load();
            var session = context.Session;
            var board = context.Board;

            Assert.That(TryDeploy(context.Bootstrap, session, context.Hand, requireMoveDistance: false), Is.True, "初始手牌里没有可部署的卡");
            yield return WaitSeconds(SettleSeconds);
            var unit = session.GetSnapshot().Units.FirstOrDefault(u => u.Owner == Side.Player);
            Assert.That(unit, Is.Not.Null, "部署后棋盘上没有己方单位");
            var card = board.GetCard(unit.Id);
            Assert.That(card, Is.Not.Null, "棋盘上缺少该单位的卡牌视图");
            var rect = (RectTransform)card.transform;

            // 第一次点击：选中并放大。
            Invoke(context.Bootstrap, "OnCellClicked", unit.Position);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(card.IsSelected, Is.True, "首次点击没有选中单位");
            Assert.That(rect.localScale.x, Is.GreaterThan(1.01f), $"选中单位后没有放大：{rect.localScale}");

            // 第二次点击同一格：取消选中，标记与放大都要复位。
            Invoke(context.Bootstrap, "OnCellClicked", unit.Position);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(card.IsSelected, Is.False, "重复点击没有取消单位的选中状态");
            Assert.That(rect.localScale.x, Is.EqualTo(1f).Within(0.01f), $"取消选中后单位没有恢复原状：{rect.localScale}");
        }

        /// <summary>
        /// 再次点击自己的家必须取消选中，并且取消之后不能再执行家移动。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：家没有放大补间、也没有专属选中标记，它的“已选中”只能通过“点相邻格能否移动”来观察，
        /// 因此这里用“取消后点击原合法格不再移动 / 重新选中后点击同一格必定移动”这一对断言来证明选中状态真的被清空。
        /// </summary>
        [UnityTest]
        public IEnumerator ClickingTheSelectedHomeAgainCancelsTheSelection()
        {
            var context = new SceneContext();
            yield return context.Load();
            var session = context.Session;
            var board = context.Board;

            var home = session.GetSnapshot().Homes.First(h => h.Owner == Side.Player);
            var homeCard = board.GetCard(home.Id);
            Assert.That(homeCard, Is.Not.Null, "棋盘上缺少家的卡牌视图");
            var moves = session.GetLegalActions(new Selection(SelectionKind.Home, home.Id)).Move;
            Assert.That(moves.Count, Is.GreaterThan(0), "玩家家没有可移动的相邻格，无法验证取消选中");
            var target = moves[0];

            // 第一次点击家：选中。
            Invoke(context.Bootstrap, "OnCellClicked", home.Position);
            yield return WaitSeconds(SettleSeconds);

            // 第二次点击家：取消选中。
            Invoke(context.Bootstrap, "OnCellClicked", home.Position);
            yield return WaitSeconds(SettleSeconds);

            // 取消之后点击原本合法的相邻格：家不得移动（证明选择确实被清空，而不是只改了外观）。
            Invoke(context.Bootstrap, "OnCellClicked", target);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(PlayerHome(session).Position, Is.EqualTo(home.Position), "取消选中后家仍然移动了");

            // 对照：重新选中家之后点击同一格，家必须移动（证明该目标本来就是合法的，前一步的失败不是目标非法导致的）。
            Invoke(context.Bootstrap, "OnCellClicked", home.Position);
            Invoke(context.Bootstrap, "OnCellClicked", target);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(PlayerHome(session).Position, Is.EqualTo(target), "重新选中家后没有执行移动，说明前面的断言不成立");
        }

        /// <summary>取得玩家家。输入：对局；输出：玩家家状态。</summary>
        static HomeState PlayerHome(GameSession session) => session.GetSnapshot().Homes.First(h => h.Owner == Side.Player);

        /// <summary>
        /// 点击“结束回合”必须同时清空场上与手牌的选中状态。
        /// 输入：无；输出：NUnit 断言结果。
        /// 为什么需要：回合切换后若还留着上一回合的选中，玩家会看到某张牌仍在放大状态或某个棋子仍带高亮，
        /// 误以为它仍是当前选择。断言顺序为先单位后手牌：单位在己方区域，第一个 AI 回合不可能打到它；
        /// 而每次结束回合都会因抽牌超上限进入弃牌阶段，所以手牌那次必须放在弃牌处理之后。
        /// </summary>
        [UnityTest]
        public IEnumerator EndingTurnClearsBoardAndHandSelection()
        {
            var context = new SceneContext();
            yield return context.Load();
            var session = context.Session;
            var board = context.Board;

            // 先部署一个己方单位，用来验证“场上的选中”也会被清空。
            Assert.That(TryDeploy(context.Bootstrap, session, context.Hand, requireMoveDistance: false), Is.True, "初始手牌里没有可部署的卡");
            yield return WaitSeconds(SettleSeconds);
            var unit = session.GetSnapshot().Units.FirstOrDefault(u => u.Owner == Side.Player);
            Assert.That(unit, Is.Not.Null, "部署后棋盘上没有己方单位");
            var unitCard = board.GetCard(unit.Id);
            Assert.That(unitCard, Is.Not.Null, "棋盘上缺少该单位的卡牌视图");
            var unitRect = (RectTransform)unitCard.transform;

            // 选中单位 → 结束回合：单位必须取消选中并复位。
            Invoke(context.Bootstrap, "OnCellClicked", unit.Position);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(unitCard.IsSelected, Is.True, "点击棋子没有选中单位");
            Invoke(context.Bootstrap, "OnEndTurnRequested");
            yield return WaitSeconds(SettleSeconds);
            Assert.That(unitCard.IsSelected, Is.False, "结束回合后棋盘单位仍处于选中状态");
            Assert.That(unitRect.localScale.x, Is.EqualTo(1f).Within(0.01f), $"结束回合后棋盘单位没有复位：{unitRect.localScale}");

            // 结束回合会让玩家抽牌超过上限而进入弃牌阶段，先弃到上限才能继续操作手牌。
            yield return FinishDiscard(context.Bootstrap, session, context.Hand);

            // 选中一张手牌 → 结束回合：手牌必须取消选中并复位。
            var record = session.GetSnapshot().Hand.First();
            var handCard = context.Hand.GetCard(record.Id);
            Assert.That(handCard, Is.Not.Null, "手牌缺少对应视图");
            var handRect = (RectTransform)handCard.transform;
            Invoke(context.Bootstrap, "OnCardClicked", handCard);
            yield return WaitSeconds(SettleSeconds);
            Assert.That(handCard.IsSelected, Is.True, "点击手牌没有进入选中状态");
            Assert.That(handRect.localScale.x, Is.GreaterThan(1.01f), $"选中的手牌没有放大：{handRect.localScale}");

            Invoke(context.Bootstrap, "OnEndTurnRequested");
            yield return WaitSeconds(SettleSeconds);
            Assert.That(handCard.IsSelected, Is.False, "结束回合后手牌仍处于选中状态");
            Assert.That(handRect.localScale.x, Is.EqualTo(1f).Within(0.01f), $"结束回合后手牌没有复位：{handRect.localScale}");
        }

        /// <summary>
        /// 部署第一张可部署的手牌。
        /// 输入：引导器、对局、手牌视图，以及是否只考虑带移动距离的卡；输出：是否成功部署。
        /// 为什么需要：移动相关用例必须保证场上有“能移动”的己方单位，
        /// 而资源卡移动距离为 0，拿来验证移动渲染会得到“没有可移动单位”的假失败。
        /// </summary>
        static bool TryDeploy(GameBootstrap bootstrap, GameSession session, HandView hand, bool requireMoveDistance)
        {
            foreach (var record in session.GetSnapshot().Hand)
            {
                if (requireMoveDistance && !HasMoveDistance(session, record.TypeId)) continue;
                var options = session.GetLegalActions(new Selection(SelectionKind.Hand, record.Id));
                if (options.Deploy.Count <= 0) continue;
                HandDragInput.Deploy(hand.GetCard(record.Id), Object.FindObjectOfType<BoardView>(), options.Deploy[0]);
                return session.GetSnapshot().Units.Any(u => u.SourceCardId == record.Id);
            }
            return false;
        }

        /// <summary>判断某卡种是否具备移动能力。输入：对局与卡种 ID；输出：是否配置了移动距离。</summary>
        static bool HasMoveDistance(GameSession session, string typeId)
        {
            return session.Configuration.Cards.TryGetValue(typeId, out var archetype) &&
                   archetype.Combat != null && archetype.Combat.MoveDistance > 0;
        }

        /// <summary>
        /// 断言棋盘上每个单位与家的卡牌都挂在快照坐标对应的格子里，且没有被缩放到不可见。
        /// 输入：棋盘视图、对局与步骤说明；输出：NUnit 断言结果（失败信息包含完整棋盘对照表）。
        /// </summary>
        static void AssertCardsMatchSnapshot(BoardView board, GameSession session, string step)
        {
            var snapshot = session.GetSnapshot();
            var report = new StringBuilder();
            var problems = new List<string>();
            report.Append($"[棋盘对照] {step}｜单位 {snapshot.Units.Count} 个｜家 {snapshot.Homes.Count} 个");

            foreach (var unit in snapshot.Units)
            {
                var card = board.GetCard(unit.Id);
                var expected = board.CellRect(unit.Position);
                var actualParent = card != null ? card.transform.parent : null;
                var rect = card != null ? card.transform as RectTransform : null;
                report.Append($"\n  单位 {unit.TypeId}#{unit.Id} 快照={unit.Position} 格子={expected?.name} 卡牌父节点={actualParent?.name} 偏移={rect?.anchoredPosition} 缩放={rect?.localScale}");
                if (card == null) { problems.Add($"单位 {unit.Id} 在棋盘上没有卡牌"); continue; }
                if (actualParent != expected) problems.Add($"单位 {unit.Id}（{unit.TypeId}）卡牌挂在 {actualParent?.name}，快照坐标是 {unit.Position}（应为 {expected?.name}）");
                if (rect != null && rect.anchoredPosition.magnitude > CenterTolerance) problems.Add($"单位 {unit.Id} 卡牌偏移未归零：{rect.anchoredPosition}");
                if (rect != null && rect.localScale.x < 0.5f) problems.Add($"单位 {unit.Id} 卡牌被缩放到不可见：{rect.localScale}");
            }

            foreach (var home in snapshot.Homes)
            {
                var card = board.GetCard(home.Id);
                var expected = board.CellRect(home.Position);
                var actualParent = card != null ? card.transform.parent : null;
                var rect = card != null ? card.transform as RectTransform : null;
                report.Append($"\n  家 {home.Owner} 快照={home.Position} 格子={expected?.name} 卡牌父节点={actualParent?.name} 偏移={rect?.anchoredPosition} 缩放={rect?.localScale}");
                if (card == null) { problems.Add($"家 {home.Owner} 在棋盘上没有卡牌"); continue; }
                if (actualParent != expected) problems.Add($"家 {home.Owner} 卡牌挂在 {actualParent?.name}，快照坐标是 {home.Position}（应为 {expected?.name}）");
                if (rect != null && rect.anchoredPosition.magnitude > CenterTolerance) problems.Add($"家 {home.Owner} 卡牌偏移未归零：{rect.anchoredPosition}");
            }

            Debug.Log(report.ToString());
            Assert.That(problems, Is.Empty, step + "\n" + string.Join("\n", problems) + "\n" + report);
        }

        /// <summary>
        /// 完成超限弃牌阶段。
        /// 输入：引导器、对局与手牌视图；输出：协程（弃到上限为止）。
        /// </summary>
        static IEnumerator FinishDiscard(GameBootstrap bootstrap, GameSession session, HandView hand)
        {
            for (var guard = 0; guard < 12 && session.GetSnapshot().Phase == MatchPhase.Discard; guard++)
            {
                var discard = session.GetSnapshot().Hand.FirstOrDefault();
                if (discard == null) break;
                Invoke(bootstrap, "OnCardClicked", hand.GetCard(discard.Id));
                yield return null;
            }
        }

        /// <summary>按真实时间等待若干秒。输入：秒数；输出：协程。</summary>
        static IEnumerator WaitSeconds(float seconds)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline) yield return null;
        }

        /// <summary>
        /// 调用 GameBootstrap 的私有输入入口。
        /// 输入：目标对象、方法名与参数；输出：无（方法缺失时断言失败）。
        /// 为什么要这样做：输入入口是私有的，测试只能以“和 UGUI 事件一样的方式”调用它，避免为了测试放宽可见性。
        /// </summary>
        static void Invoke(object target, string methodName, params object[] args)
        {
            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"{target.GetType().Name} 缺少方法 {methodName}");
            method.Invoke(target, args);
        }

        /// <summary>
        /// 演示场景上下文：负责加载场景并在加载完成后取出引导器、对局与视图。
        /// 输入：Load 协程；输出：四个引用。为什么需要：加载场景必须跨帧等待，无法写在静态工具函数里。
        /// </summary>
        sealed class SceneContext
        {
            /// <summary>对局引导器。</summary>
            public GameBootstrap Bootstrap { get; private set; }
            /// <summary>当前对局（唯一状态所有者）。</summary>
            public GameSession Session { get; private set; }
            /// <summary>棋盘视图。</summary>
            public BoardView Board { get; private set; }
            /// <summary>手牌视图。</summary>
            public HandView Hand { get; private set; }

            /// <summary>加载演示场景并等待 Start 完成。输入：无；输出：协程。</summary>
            public IEnumerator Load()
            {
                SceneManager.LoadScene(ScenePath);
                yield return null;
                yield return null;
                Bootstrap = Object.FindObjectOfType<GameBootstrap>();
                Assert.That(Bootstrap, Is.Not.Null, "演示场景未装配 GameBootstrap");
                Session = Bootstrap.Session;
                Assert.That(Session, Is.Not.Null, "对局未启动（配置门禁或配置资产缺失）");
                Board = Object.FindObjectOfType<BoardView>();
                Hand = Object.FindObjectOfType<HandView>();
                Assert.That(Board, Is.Not.Null, "场景缺少 BoardView");
                Assert.That(Hand, Is.Not.Null, "场景缺少 HandView");
            }
        }
    }
}
