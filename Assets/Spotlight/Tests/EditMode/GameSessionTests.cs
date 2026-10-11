// 行为回归测试：通过公开命令推进真实对局，不直接篡改 GameSession 私有状态。
// 测试内数值属于独立夹具，不是正式配置默认值。正式数据只由 Excel 编译产物提供。
using System;
using System.Linq;
using NUnit.Framework;

namespace Spotlight.Tests
{
    /// <summary>覆盖抽牌堆守恒、回合、部署、移动、家、星门、攻击、补兵和输入权限。</summary>
    public class GameSessionTests
    {
        /// <summary>构造足够小且可读的隔离配置，方便每个用例只调整与断言相关的参数。</summary>
        public static ConfigDocument Document()
        {
            return new ConfigDocument
            {
                cards = new[] {
                    new CardRow { id="miner", name="矿机", category="resource", hp=4, moveDistance=0, resourceComponent=true, produce=1, viewKey="default" },
                    new CardRow { id="soldier", name="士兵", category="attack", hp=5, atk=2, range=2, moveDistance=3, viewKey="default" },
                    new CardRow { id="gate", name="星门", category="special", hp=3, moveDistance=1, resourceComponent=true, upkeep=4, teleportComponent=true, permitsAttackDeploy=true, deployHomeOnly=true, avoidAdjacentSameType=true, viewKey="default" },
                    new CardRow { id="upgrade", name="高级矿机", category="resource", hp=6, atk=1, range=1, requiresUpgrade=true, upgradeFrom="miner", viewKey="default" },
                    new CardRow { id="boss", name="敌兵", category="attack", hp=3, atk=1, range=1, moveDistance=1, viewKey="default" }
                },
                deck = new[] { new DeckRow {cardType="miner",count=4},new DeckRow {cardType="soldier",count=4},new DeckRow {cardType="gate",count=4},new DeckRow {cardType="upgrade",count=4} },
                rules = new[] { new RulesRow { rows=5,columns=10,homeRegionWidth=2,playerPlays=20,bossPlays=3,drawCount=16,handLimit=30,initialResources=0,bossRespawnDelay=2,playerSide="left",firstSide="player",bossCardType="boss",playerControlsBoss=true,accumulateResources=true,adjacentSwap=true } },
                homes = new[] {new HomeRow {owner="player",hp=10,produce=2},new HomeRow {owner="boss",hp=10,produce=0}},
                bossSlots = new[] {new BossSlotRow {id="a",rowFraction=0,edgeOffset=1},new BossSlotRow {id="b",rowFraction=.5,edgeOffset=1},new BossSlotRow {id="c",rowFraction=1,edgeOffset=1}},
                presentation = new[] {new PresentationRow {referenceWidth=1920,referenceHeight=1080,fontSize=18,logLimit=100,moveSeconds=.2f,feedbackSeconds=.1f,selectedScale=1.05f,playerColor="#3366FF",bossColor="#FF3333",resourceColor="#33AA66",attackColor="#EE8833",specialColor="#AA55FF",moveColor="#33AAFF",attackTargetColor="#FF3333",swapColor="#AA33FF",deployColor="#33FF99"}}
            };
        }
        static GameSession Start(ConfigDocument d = null) => new GameSession(new GameConfiguration(d ?? Document()), new SeededRandom(42), 100);
        static UnitState Put(GameSession g,string type,int row,int col)
        {
            var card=g.GetSnapshot().Hand.First(x=>x.TypeId==type);
            var r=g.Execute(new GameCommand(CommandKind.Deploy,Side.Player,card.Id,new Cell(row,col)));
            Assert.That(r.Success,Is.True,r.Error);
            return g.GetSnapshot().Units.Single(u=>u.SourceCardId==card.Id);
        }
        static void End(GameSession g) { var s=g.GetSnapshot(); var r=g.Execute(new GameCommand(CommandKind.EndTurn,s.ActiveSide,skipDeployment:true)); Assert.That(r.Success,Is.True,r.Error); }
        [Test] public void StartsWithOnlyHomesAndPlayerDraw() { var s=Start().GetSnapshot(); Assert.That(s.Units,Is.Empty); Assert.That(s.Hand.Count,Is.EqualTo(16)); Assert.That(s.Resources,Is.EqualTo(2)); Assert.That(s.Round,Is.EqualTo(1)); }
        [Test] public void UpgradeRecyclesSourceAndCreatesFullHealthIdentity()
        {
            var g=Start();var old=Put(g,"miner",0,0);var upgraded=Put(g,"upgrade",0,0);var s=g.GetSnapshot();
            Assert.That(upgraded.Id,Is.Not.EqualTo(old.Id));Assert.That(upgraded.Hp,Is.EqualTo(6));Assert.That(s.Graveyard.Count,Is.EqualTo(2));Assert.That(s.Graveyard.Any(c=>c.Id==old.SourceCardId),Is.True);Assert.That(s.Units.Count,Is.EqualTo(1));
        }
        [Test] public void UpgradeCannotDeployOnEmptyCell() { var g=Start();var id=g.GetSnapshot().Hand.First(c=>c.TypeId=="upgrade").Id;Assert.That(g.Execute(new GameCommand(CommandKind.Deploy,Side.Player,id,new Cell(0,0))).Success,Is.False); }
        [Test] public void ZeroMoveUnitsCanSwapAndBothConsumeAction()
        {
            var g=Start();var a=Put(g,"miner",0,0);var b=Put(g,"miner",0,1);
            Assert.That(g.Execute(new GameCommand(CommandKind.Swap,Side.Player,a.Id,targetId:b.Id)).Success,Is.True);
            var units=g.GetSnapshot().Units;Assert.That(units.Single(u=>u.Id==a.Id).Position,Is.EqualTo(new Cell(0,1)));Assert.That(units.All(u=>u.Acted),Is.True);
        }
        [Test] public void DiagonalSwapIsRejected() { var g=Start();var a=Put(g,"miner",0,0);var b=Put(g,"miner",1,1);Assert.That(g.Execute(new GameCommand(CommandKind.Swap,Side.Player,a.Id,targetId:b.Id)).Success,Is.False); }
        [Test] public void MoveUsesShortestUnoccupiedPathAndConsumesAction()
        {
            var g=Start();var u=Put(g,"soldier",0,0);Put(g,"miner",0,1);
            Assert.That(g.Execute(new GameCommand(CommandKind.Move,Side.Player,u.Id,new Cell(0,2))).Success,Is.False);
            Assert.That(g.Execute(new GameCommand(CommandKind.Move,Side.Player,u.Id,new Cell(1,2))).Success,Is.True);
            Assert.That(g.Execute(new GameCommand(CommandKind.Move,Side.Player,u.Id,new Cell(1,3))).Success,Is.False);
        }
        [Test] public void SummoningSicknessDisablesNewUnitsUntilOwnTurn()
        {
            var d=Document();d.rules[0].summoningSickness=true;var g=Start(d);var u=Put(g,"soldier",0,0);
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit,u.Id)).Move,Is.Empty);End(g);End(g);Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit,u.Id)).Move,Is.Not.Empty);
        }
        [Test] public void GateExpandsDeploymentImmediatelyEvenWhenUnpaid()
        {
            var g=Start();var gate=Put(g,"gate",0,1);Put(g,"soldier",0,2);End(g);End(g);
            Assert.That(g.GetSnapshot().Units.Single(u=>u.Id==gate.Id).UpkeepPaid,Is.True);
            var d=Document();d.homes[0].produce=0;var unpaid=Start(d);var u=Put(unpaid,"gate",0,1);End(unpaid);End(unpaid);
            Assert.That(unpaid.GetSnapshot().Units.Single(x=>x.Id==u.Id).UpkeepPaid,Is.False);Put(unpaid,"soldier",0,2);
        }
        [Test] public void GateCannotUseAnotherGateAndCannotDeployAdjacentSameType()
        {
            var g=Start();Put(g,"gate",0,1);var id=g.GetSnapshot().Hand.First(c=>c.TypeId=="gate").Id;
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy,Side.Player,id,new Cell(0,2))).Success,Is.False);
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy,Side.Player,id,new Cell(1,1))).Success,Is.False);
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy,Side.Player,id,new Cell(1,0))).Success,Is.True);
        }
        [Test] public void HomeCanMoveOnceOnlyWithinFixedRegion()
        {
            var g=Start();Assert.That(g.Execute(new GameCommand(CommandKind.MoveHome,Side.Player,-1,new Cell(2,1))).Success,Is.True);
            Assert.That(g.Execute(new GameCommand(CommandKind.MoveHome,Side.Player,-1,new Cell(1,1))).Success,Is.False);End(g);End(g);
            Assert.That(g.Execute(new GameCommand(CommandKind.MoveHome,Side.Player,-1,new Cell(2,2))).Success,Is.False);
        }
        [Test] public void DiscardPhaseDelaysBossAndRecyclesWithoutCreatingCards()
        {
            var d=Document();d.rules[0].drawCount=5;d.rules[0].handLimit=2;d.rules[0].playerControlsBoss=false;var g=Start(d);End(g);
            Assert.That(g.GetSnapshot().Phase,Is.EqualTo(MatchPhase.Discard));Assert.That(g.GetSnapshot().Units,Is.Empty);
            for(var i=0;i<3;i++) Assert.That(g.Execute(new GameCommand(CommandKind.Discard,Side.Player,g.GetSnapshot().Hand[0].Id)).Success,Is.True);
            var s=g.GetSnapshot();Assert.That(s.Round,Is.EqualTo(2));Assert.That(s.Units.Count,Is.EqualTo(3));Assert.That(s.Hand.Count+s.DrawPile.Count+s.Graveyard.Count,Is.EqualTo(16));
        }
        [Test] public void DrawCanShuffleMidDrawAndNeverDuplicateHandOrBoard()
        {
            var d=Document();d.deck=new[]{new DeckRow{cardType="miner",count=3}};d.rules[0].drawCount=2;d.rules[0].handLimit=1;var g=Start(d);End(g);
            var id=g.GetSnapshot().Hand[0].Id;g.Execute(new GameCommand(CommandKind.Discard,Side.Player,id));End(g);var s=g.GetSnapshot();
            Assert.That(s.Hand.Count,Is.EqualTo(3));Assert.That(s.Hand.Select(c=>c.Id).Distinct().Count(),Is.EqualTo(3));Assert.That(s.Graveyard,Is.Empty);Assert.That(s.DrawPile,Is.Empty);
        }
        [Test] public void SnapshotCannotModifyLiveState() { var g=Start();var u=Put(g,"soldier",0,0);var s=g.GetSnapshot();g.Execute(new GameCommand(CommandKind.Move,Side.Player,u.Id,new Cell(1,0)));Assert.That(s.Units[0].Position,Is.EqualTo(new Cell(0,0))); }
        [TestCase("left",9)] [TestCase("right",0)] public void PlayerSideMirrorsHomes(string side,int bossCol) {var d=Document();d.rules[0].playerSide=side;Assert.That(Start(d).GetSnapshot().Homes.Single(h=>h.Owner==Side.Boss).Position.Col,Is.EqualTo(bossCol));}
        [TestCase(2)] [TestCase(6)] public void EvenBoardMiddleSlotRoundsUp(int rows)
        {
            // 只校验兵位首选行：偶数行时中间兵位取 floor((rows-1)/2 + 0.5)。
            // 不在此断言“所有单位都已行动”：窄棋盘（如 2 行）上后行动的单位可能被同伴堵住，
            // 按规则此时应当待机，待机不消耗也不标记行动机会，因此该断言并不成立。
            var d=Document();d.rules[0].rows=rows;d.rules[0].firstSide="boss";d.rules[0].playerControlsBoss=false;
            var s=Start(d).GetSnapshot();Assert.That(s.BossReplacements.Single(x=>x.SlotId=="b").PreferredCell.Row,Is.EqualTo(rows/2));
        }
        [Test] public void BossFirstDeploymentActsImmediatelyOnRoomyBoard()
        {
            // 首次成功部署可以立即行动（不受召唤失调限制），棋盘足够大时三个兵位都应完成行动。
            var d=Document();d.rules[0].firstSide="boss";d.rules[0].playerControlsBoss=false;d.rules[0].summoningSickness=true;
            var s=Start(d).GetSnapshot();
            Assert.That(s.Units.Count,Is.EqualTo(3));
            Assert.That(s.Units.All(u=>u.Acted),Is.True,string.Join("\n",s.Log.Select(e=>e.Message)));
        }
        [Test] public void SmallBoardKeepsUnspawnedSlotsWaitingWithoutOverwritingHomes()
        {
            var d=Document();d.rules[0].rows=1;d.rules[0].columns=2;d.rules[0].firstSide="boss";d.rules[0].playerControlsBoss=false;
            var s=Start(d).GetSnapshot();Assert.That(s.Units,Is.Empty);Assert.That(s.BossReplacements.All(x=>x.WaitingForSpace),Is.True);Assert.That(s.Homes.Count,Is.EqualTo(2));
        }
        [Test] public void AiFirstTurnWaitsUntilPlayerEndsAndOnlyMovesOnce()
        {
            var d=Document();d.rules[0].playerControlsBoss=false;d.rules[0].summoningSickness=true;var g=Start(d);End(g);var s=g.GetSnapshot();
            Assert.That(s.ActiveSide,Is.EqualTo(Side.Player));Assert.That(s.Units.Count,Is.EqualTo(3));Assert.That(s.Units.All(u=>u.Acted && u.Position.Distance(s.BossReplacements.Single(x=>x.SlotId==u.BossSlotId).PreferredCell)==1),Is.True,string.Join("\n",s.Log.Select(e=>e.Message)));
        }
        [Test] public void AttacksDoNotDamageAttackerAndUseManhattanRange()
        {
            var d=Document();d.cards.Single(c=>c.id=="miner").atk=2;d.cards.Single(c=>c.id=="miner").range=2;var g=Start(d);var attacker=Put(g,"miner",0,7);End(g);
            Assert.That(g.Execute(new GameCommand(CommandKind.BossDeploy,Side.Boss,target:new Cell(0,8))).Success,Is.True);End(g);
            var enemy=g.GetSnapshot().Units.Single(u=>u.Owner==Side.Boss);Assert.That(g.Execute(new GameCommand(CommandKind.Attack,Side.Player,attacker.Id,targetId:enemy.Id)).Success,Is.True);
            Assert.That(g.GetSnapshot().Units.Single(u=>u.Id==attacker.Id).Hp,Is.EqualTo(4));Assert.That(g.GetSnapshot().Units.Single(u=>u.Id==enemy.Id).Hp,Is.EqualTo(1));
        }
        [Test] public void BlockingPreventsStraightAttackThroughOccupiedCell()
        {
            var d=Document();d.rules[0].lineBlocking=true;var miner=d.cards.Single(c=>c.id=="miner");miner.atk=2;miner.range=10;var g=Start(d);var a=Put(g,"miner",2,6);Put(g,"miner",2,8);
            Assert.That(g.Execute(new GameCommand(CommandKind.Attack,Side.Player,a.Id,targetId:-2)).Success,Is.False);
        }
        [Test] public void HomeDestructionStopsMatchImmediately()
        {
            var d=Document();var miner=d.cards.Single(c=>c.id=="miner");miner.atk=10;miner.range=2;var g=Start(d);var a=Put(g,"miner",2,8);
            Assert.That(g.Execute(new GameCommand(CommandKind.Attack,Side.Player,a.Id,targetId:-2)).Success,Is.True);Assert.That(g.GetSnapshot().Outcome,Is.EqualTo(MatchOutcome.PlayerWins));Assert.That(g.Execute(new GameCommand(CommandKind.EndTurn,Side.Player)).Success,Is.False);
        }
        [Test] public void BossDeathRespawnsAtDueRoundAndRespectsSickness()
        {
            var d=Document();d.rules[0].playerControlsBoss=false;d.rules[0].summoningSickness=true;var miner=d.cards.Single(c=>c.id=="miner");miner.atk=3;miner.range=3;miner.hp=100;
            var g=Start(d);var a=Put(g,"miner",0,6);End(g);var victim=g.GetSnapshot().Units.First(u=>u.BossSlotId=="a");
            Assert.That(g.Execute(new GameCommand(CommandKind.Attack,Side.Player,a.Id,targetId:victim.Id)).Success,Is.True);
            Assert.That(g.GetSnapshot().BossReplacements.Single(s=>s.SlotId=="a").DueRound,Is.EqualTo(4));End(g);Assert.That(g.GetSnapshot().BossReplacements.Single(s=>s.SlotId=="a").UnitId,Is.Null);End(g);Assert.That(g.GetSnapshot().BossReplacements.Single(s=>s.SlotId=="a").UnitId,Is.Null);End(g);
            var replacement=g.GetSnapshot().Units.Single(u=>u.BossSlotId=="a");Assert.That(replacement.Id,Is.Not.EqualTo(victim.Id));Assert.That(replacement.Hp,Is.EqualTo(3));Assert.That(replacement.Acted,Is.True);Assert.That(replacement.Position,Is.EqualTo(new Cell(0,8)));
        }
        [Test] public void ModalBlocksCommandsAndInvalidCommandsDoNotMutateState() {var g=Start();g.SetInputBlocked(true);Assert.That(g.Execute(new GameCommand(CommandKind.EndTurn,Side.Player)).Success,Is.False);Assert.That(g.GetSnapshot().Round,Is.EqualTo(1));Assert.That(g.GetLegalActions(new Selection(SelectionKind.Hand,g.GetSnapshot().Hand[0].Id)).Deploy,Is.Empty);}
        [Test] public void DuplicateNamesAndInvalidUpgradeAreRejected() {var d=Document();d.cards[1].name=" 矿机 ";d.cards[3].upgradeFrom="upgrade";Assert.That(ConfigValidation.Validate(d).Count,Is.GreaterThanOrEqualTo(2));}
        [Test] public void DeployMovesPlayerCardToGraveyardWithoutChangingTotalCount()
        {
            // 部署成功即进入墓地：卡牌从手牌转入墓地，牌堆总数不变，场上单位与墓地卡牌互不影响。
            var d=Document();d.deck=new[]{new DeckRow{cardType="miner",count=1}};d.rules[0].drawCount=1;var g=Start(d);
            var card=g.GetSnapshot().Hand.Single();
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy,Side.Player,card.Id,new Cell(0,0))).Success,Is.True);
            var s=g.GetSnapshot();
            Assert.That(s.Units.Count,Is.EqualTo(1));
            Assert.That(s.Hand,Is.Empty);
            Assert.That(s.Graveyard.Select(c=>c.Id),Is.EquivalentTo(new[]{card.Id}));
            Assert.That(s.Hand.Count+s.DrawPile.Count+s.Graveyard.Count,Is.EqualTo(1));
        }
        [Test] public void FailedDeployDoesNotPutCardIntoGraveyard()
        {
            // 校验失败时状态不得变化：升级卡不能放在空格，墓地保持为空。
            var g=Start();var id=g.GetSnapshot().Hand.First(c=>c.TypeId=="upgrade").Id;
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy,Side.Player,id,new Cell(0,0))).Success,Is.False);
            Assert.That(g.GetSnapshot().Graveyard,Is.Empty);
        }
        [Test] public void GraveyardShuffleLetsSameCardCreateSecondIndependentUnit()
        {
            // 方案 A：墓地洗回后可再次部署同一张卡，生成第二个满血、独立身份的单位，牌堆总数仍为 1。
            var d=Document();d.deck=new[]{new DeckRow{cardType="miner",count=1}};d.rules[0].drawCount=1;var g=Start(d);
            var first=g.GetSnapshot().Hand.Single();
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy,Side.Player,first.Id,new Cell(0,0))).Success,Is.True);
            var firstUnit=g.GetSnapshot().Units.Single();
            End(g);End(g);
            Assert.That(g.GetSnapshot().Hand.Count,Is.EqualTo(1),string.Join("\n",g.GetSnapshot().Log.Select(e=>e.Message)));
            var second=g.GetSnapshot().Hand.Single();
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy,Side.Player,second.Id,new Cell(0,1))).Success,Is.True);
            var s=g.GetSnapshot();
            Assert.That(s.Units.Count,Is.EqualTo(2));
            Assert.That(s.Units.Select(u=>u.Id).Distinct().Count(),Is.EqualTo(2));
            Assert.That(s.Units.Any(u=>u.Id==firstUnit.Id),Is.True);
            Assert.That(s.Units.All(u=>u.Hp==4),Is.True);
            Assert.That(s.Hand.Count+s.DrawPile.Count+s.Graveyard.Count,Is.EqualTo(1));
        }
        [Test] public void PlayerUnitDeathDoesNotRecycleCardAgain()
        {
            // 玩家单位阵亡只移除场上单位；来源卡已在部署时入墓地，不得重复回收导致牌堆膨胀。
            var d=Document();d.cards.Single(c=>c.id=="miner").hp=1;var g=Start(d);
            Put(g,"miner",0,8);
            Assert.That(g.GetSnapshot().Graveyard.Count(c=>c.TypeId=="miner"),Is.EqualTo(1));
            End(g);
            Assert.That(g.Execute(new GameCommand(CommandKind.BossDeploy,Side.Boss,target:new Cell(0,9))).Success,Is.True);
            var boss=g.GetSnapshot().Units.Single(u=>u.Owner==Side.Boss);
            var victim=g.GetSnapshot().Units.Single(u=>u.Owner==Side.Player);
            Assert.That(g.Execute(new GameCommand(CommandKind.Attack,Side.Boss,boss.Id,targetId:victim.Id)).Success,Is.True);
            var s=g.GetSnapshot();
            Assert.That(s.Units.Any(u=>u.Owner==Side.Player),Is.False);
            Assert.That(s.Graveyard.Count(c=>c.TypeId=="miner"),Is.EqualTo(1));
            Assert.That(s.Hand.Count+s.DrawPile.Count+s.Graveyard.Count,Is.EqualTo(16));
        }
    }
}
