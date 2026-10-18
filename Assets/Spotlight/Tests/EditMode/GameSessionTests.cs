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
        /// <summary>
        /// 构造足够小且可读的隔离配置，方便每个用例只调整与断言相关的参数。
        /// 说明：Deck 表已废除，本方法不再登记抽牌堆；用例需要固定牌堆时用 <see cref="DefaultDeck"/> 或自行传入 DeckEntry。
        /// </summary>
        public static ConfigDocument Document()
        {
            return new ConfigDocument
            {
                cards = new[] {
                    new CardRow { tier = "basic", id = "miner", name="矿机", category="resource", hp=4, moveDistance=0, resourceComponent=true, produce=1, viewKey="default" },
                    new CardRow { tier = "basic", id = "soldier", name="士兵", category="attack", hp=5, atk=2, range=2, moveDistance=3, viewKey="default" },
                    new CardRow { tier = "advanced", id = "gate", name="星门", category="special", hp=3, moveDistance=1, resourceComponent=true, upkeep=4, teleportComponent=true, permitsAttackDeploy=true, deployHomeOnly=true, avoidAdjacentSameType=true, viewKey="default" },
                    new CardRow { tier = "intermediate", id = "upgrade", name="高级矿机", category="resource", hp=6, atk=1, range=1, requiresUpgrade=true, upgradeFrom="miner", viewKey="default" },
                    new CardRow { tier = "none", id = "boss", name="敌兵", category="attack", hp=3, atk=1, range=1, moveDistance=1, viewKey="default" },
                    new CardRow { tier = "none", id = "spawner", name="刷怪卡", category="special", hp=1, viewKey="default" , canSwap = false, spawnComponent = true, spawnCardType = "boss" },
                    new CardRow { tier = "none", id = "warship", name="战舰", category="attack", hp=15, atk=2, range=1, moveDistance=1, canAttackResources=false, spawnComponent=true, spawnCardType="boss", viewKey="default" }
                },
                rules = new[] { new RulesRow { drawPileSize=24, tierRatioBasic=70, tierRatioIntermediate=20, tierRatioAdvanced=10, maxTypesBasic=5, maxTypesIntermediate=3, maxTypesAdvanced=2, bossSpawnerAttackThreshold = 1000, bossSpawnerCountFromColumn = 4, bossSpawnerUnitThreshold = 2, bossSpawnerDeployFromColumn = 5, bossSpawnerCardType = "spawner", bossPhaseTwoHp = 1, bossWarshipColumn = 1, bossWarshipCardType = "warship", bossBombInterval = 10, bossBombDamage = 3, bossBombColumn = 5, rows=5,columns=10,homeRegionWidth=2,playerPlays=20,bossPlays=3,drawCount=16,handLimit=30,initialResources=0,bossRespawnDelay=2,playerSide="left",firstSide="player",bossCardType="boss",playerControlsBoss=true,accumulateResources=true,adjacentSwap=true } },
                homes = new[] {new HomeRow {owner="player",hp=10,produce=2},new HomeRow {owner="boss",hp=10,produce=0}},
                bossSlots = new[] {new BossSlotRow {id="a",rowFraction=0,edgeOffset=1},new BossSlotRow {id="b",rowFraction=.5,edgeOffset=1},new BossSlotRow {id="c",rowFraction=1,edgeOffset=1}},
                presentation = new[] {new PresentationRow {referenceWidth=1920,referenceHeight=1080,fontSize=18,logLimit=100,moveSeconds=.2f,feedbackSeconds=.1f,selectedScale=1.05f,playerColor="#3366FF",bossColor="#FF3333",resourceColor="#33AA66",attackColor="#EE8833",specialColor="#AA55FF",moveColor="#33AAFF",attackTargetColor="#FF3333",swapColor="#AA33FF",deployColor="#33FF99"}}
            };
        }
        /// <summary>左右镜像下核对两列区域、拒绝命令与星门四邻例外；只使用公开部署和高亮入口。</summary>
        [TestCase("left")]
        [TestCase("right")]
        public void TwoColumnDeploymentBoundaryAndGateExtensionArePreserved(string playerSide)
        {
            var d = Document(); d.rules[0].playerSide = playerSide;
            var g = Start(d);
            var inner = playerSide == "left" ? 1 : 8;
            var outer = playerSide == "left" ? 2 : 7;
            foreach (var typeId in new[] { "soldier", "gate" })
            {
                var card = g.GetSnapshot().Hand.First(c => c.TypeId == typeId);
                var legal = g.GetLegalActions(new Selection(SelectionKind.Hand, card.Id)).Deploy;
                Assert.That(legal, Does.Contain(new Cell(0, inner)));
                Assert.That(legal.All(p => playerSide == "left" ? p.Col < 2 : p.Col >= 8), Is.True);
                var before = g.GetSnapshot();
                Assert.That(g.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(0, outer))).Success, Is.False);
                Assert.That(g.GetSnapshot().Hand.Count, Is.EqualTo(before.Hand.Count));
                Assert.That(g.GetSnapshot().PlaysRemaining, Is.EqualTo(before.PlaysRemaining));
                Assert.That(g.GetSnapshot().Units, Is.Empty);
            }
            Put(g, "gate", 0, inner);
            Put(g, "soldier", 0, outer);
            var gate = g.GetSnapshot().Hand.First(c => c.TypeId == "gate");
            Assert.That(g.Execute(new GameCommand(CommandKind.Deploy, Side.Player, gate.Id, new Cell(1, outer))).Success, Is.False);
            Put(g, "miner", 3, 5); // 资源卡不受攻击卡的区域限制。
        }

        /// <summary>堵住 Boss 首选格并保留一个或零个区域空位，验证一阶段补兵只在己方区域寻找替代格。</summary>
        [TestCase("left", true)]
        [TestCase("right", true)]
        [TestCase("left", false)]
        [TestCase("right", false)]
        public void BossReinforcementFallbackNeverEscapesHomeRegion(string playerSide, bool leaveSpace)
        {
            var d = Document();
            d.rules[0].playerSide = playerSide;
            d.rules[0].playerControlsBoss = false;
            d.rules[0].drawCount = 20;
            var boss = d.cards.Single(c => c.id == "boss"); boss.moveDistance = 0; boss.atk = 0;
            var g = Start(d, new DeckEntry("miner", 20));
            var bossHome = g.GetSnapshot().Homes.Single(h => h.Owner == Side.Boss).Position;
            var firstCol = playerSide == "left" ? 8 : 0;
            var free = new Cell(1, firstCol);
            for (var row = 0; row < 5; row++)
                for (var col = firstCol; col < firstCol + 2; col++)
                {
                    var cell = new Cell(row, col);
                    if (cell == bossHome || (leaveSpace && cell == free)) continue;
                    Put(g, "miner", row, col);
                }
            End(g);
            var spawned = g.GetSnapshot().Units.Where(u => u.Owner == Side.Boss).ToArray();
            Assert.That(spawned.Length, Is.EqualTo(leaveSpace ? 1 : 0));
            if (leaveSpace) Assert.That(spawned[0].Position, Is.EqualTo(free));
            Assert.That(g.GetSnapshot().Log.Any(e => e.Kind == "spawn-wait"), Is.True);
        }

        /// <summary>
        /// 本文件默认抽牌堆：矿机/士兵/星门/高级矿机 各 4 张，共 16 张（等价于 Deck 表废除前的夹具卡组）。
        /// 输入：无；输出：DeckEntry 清单。
        /// 为什么需要：其它测试文件的夹具复用本夹具，必须能拿到同一份牌堆以保证抽牌顺序不变。
        /// </summary>
        public static DeckEntry[] DefaultDeck() => new[]
        {
            new DeckEntry("miner", 4), new DeckEntry("soldier", 4), new DeckEntry("gate", 4), new DeckEntry("upgrade", 4)
        };

        /// <summary>
        /// 建立对局。输入：可选文档与可选的显式抽牌堆（省略时用 <see cref="DefaultDeck"/>）；输出：固定种子的对局。
        /// 为什么需要：抽牌堆已改由 WithDeck 显式覆盖，测试不再能通过文档声明卡组；
        /// requireDrawPileSize=false 表示夹具不要求凑满 drawPileSize（正式配置仍由校验强制）。
        /// </summary>
        static GameSession Start(ConfigDocument d = null, params DeckEntry[] deck)
        {
            var configuration = new GameConfiguration(d ?? Document());
            var pile = deck != null && deck.Length > 0 ? deck : DefaultDeck();
            return new GameSession(configuration.WithDeck(pile, false), new SeededRandom(42), 100);
        }
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
            // 互换只排除资源卡，因此把士兵的移动距离改成 0 后仍然可以互换，双方同时消耗行动机会。
            var d=Document();d.cards.Single(c=>c.id=="soldier").moveDistance=0;var g=Start(d);var a=Put(g,"soldier",0,0);var b=Put(g,"soldier",0,1);
            Assert.That(g.Execute(new GameCommand(CommandKind.Swap,Side.Player,a.Id,targetId:b.Id)).Success,Is.True);
            var units=g.GetSnapshot().Units;Assert.That(units.Single(u=>u.Id==a.Id).Position,Is.EqualTo(new Cell(0,1)));Assert.That(units.All(u=>u.Acted),Is.True);
        }
        [Test] public void DiagonalSwapIsRejected() { var g=Start();var a=Put(g,"soldier",0,0);var b=Put(g,"soldier",1,1);Assert.That(g.Execute(new GameCommand(CommandKind.Swap,Side.Player,a.Id,targetId:b.Id)).Success,Is.False); }
        [Test] public void ResourceCardsCannotSwapAndAreNotSwapTargets()
        {
            // 资源卡不参与互换：既不能发起（对手是资源卡或战斗卡都不行），也不能作为目标，
            // 且高亮（GetLegalActions）与结算（ExecuteCommand）必须给出同一结论。
            var g=Start();
            var minerA=Put(g,"miner",0,0);      // 资源卡，与 minerB 相邻
            var minerB=Put(g,"miner",1,0);      // 资源卡，与 minerA、soldierA 相邻
            var soldierA=Put(g,"soldier",1,1);  // 战斗卡，与 minerB、soldierB 相邻
            var soldierB=Put(g,"soldier",2,1);  // 战斗卡，只与 soldierA 相邻
            Assert.That(g.Execute(new GameCommand(CommandKind.Swap,Side.Player,minerA.Id,targetId:minerB.Id)).Success,Is.False,"资源卡之间不允许互换");
            Assert.That(g.Execute(new GameCommand(CommandKind.Swap,Side.Player,minerB.Id,targetId:soldierA.Id)).Success,Is.False,"资源卡不能发起互换");
            Assert.That(g.Execute(new GameCommand(CommandKind.Swap,Side.Player,soldierA.Id,targetId:minerB.Id)).Success,Is.False,"资源卡不能作为互换目标");
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit,minerA.Id)).Swap,Is.Empty,"资源卡选中时不应出现互换高亮");
            var targets=g.GetLegalActions(new Selection(SelectionKind.Unit,soldierA.Id)).Swap;
            Assert.That(targets.Any(c=>c==new Cell(2,1)),Is.True,"非资源单位之间仍应可以互换");
            Assert.That(targets.Any(c=>c==new Cell(1,0)),Is.False,"资源卡不应作为互换目标出现在高亮里");
            Assert.That(g.GetSnapshot().Units.Single(u=>u.Id==minerA.Id).Position,Is.EqualTo(new Cell(0,0)),"被拒绝的互换不得改变位置");
        }

        [Test] public void WithdrawRemovesOwnUnitWithoutConsumingAction()
        {
            // 离场不是行动：先把行动用掉（移动），仍然可以离场；来源卡不回收，牌区总数不变。
            var g=Start();var u=Put(g,"soldier",0,0);
            Assert.That(g.Execute(new GameCommand(CommandKind.Move,Side.Player,u.Id,new Cell(1,0))).Success,Is.True);
            var acted=g.GetLegalActions(new Selection(SelectionKind.Unit,u.Id));
            Assert.That(g.GetSnapshot().Units.Single(x=>x.Id==u.Id).Acted,Is.True);
            Assert.That(acted.Move,Is.Empty,"行动已经用完");
            Assert.That(acted.CanWithdraw,Is.True,"已行动的单位仍然可以离场");
            var before=g.GetSnapshot();
            Assert.That(g.Execute(new GameCommand(CommandKind.Withdraw,Side.Player,u.Id)).Success,Is.True);
            var after=g.GetSnapshot();
            Assert.That(after.Units.Any(x=>x.Id==u.Id),Is.False,"离场后单位仍在场上");
            Assert.That(after.Graveyard.Count,Is.EqualTo(before.Graveyard.Count),"离场不应回收来源卡");
            Assert.That(after.Hand.Count+after.DrawPile.Count+after.Graveyard.Count,Is.EqualTo(16),"离场改变了牌区总数");
            Assert.That(after.Log.Any(e=>e.Kind=="withdraw"),Is.True,"离场应写入一条 withdraw 日志事件");
        }
        [Test] public void WithdrawIsRejectedForBossUnitsHomesAndWrongTurn()
        {
            var g=Start();var mine=Put(g,"soldier",0,0);
            // 家不是单位，不能离场
            Assert.That(g.Execute(new GameCommand(CommandKind.Withdraw,Side.Player,-1)).Success,Is.False);
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Home,-1)).CanWithdraw,Is.False);
            // 交出回合，由手动 Boss 放一个 Boss 单位
            End(g);
            Assert.That(g.Execute(new GameCommand(CommandKind.BossDeploy,Side.Boss,target:new Cell(0,8))).Success,Is.True);
            var boss=g.GetSnapshot().Units.Single(x=>x.Owner==Side.Boss);
            Assert.That(g.Execute(new GameCommand(CommandKind.Withdraw,Side.Boss,boss.Id)).Success,Is.False,"Boss 单位不可离场");
            Assert.That(g.Execute(new GameCommand(CommandKind.Withdraw,Side.Player,mine.Id)).Success,Is.False,"不是己方回合");
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit,boss.Id)).CanWithdraw,Is.False,"Boss 单位没有离场入口");
        }
        [Test] public void WithdrawIsBlockedByModalAndDiscardPhase()
        {
            var g=Start();var u=Put(g,"soldier",0,0);
            g.SetInputBlocked(true);
            Assert.That(g.Execute(new GameCommand(CommandKind.Withdraw,Side.Player,u.Id)).Success,Is.False,"模态阻塞时不能离场");
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit,u.Id)).CanWithdraw,Is.False,"模态下没有离场入口");
            // 弃牌阶段只能弃牌：换一份手牌上限很小的配置，先部署再结束回合进入弃牌阶段。
            var d=Document();d.rules[0].drawCount=5;d.rules[0].handLimit=2;var g2=Start(d);
            var record=g2.GetSnapshot().Hand.First(c=>g2.GetLegalActions(new Selection(SelectionKind.Hand,c.Id)).Deploy.Count>0);
            var cell=g2.GetLegalActions(new Selection(SelectionKind.Hand,record.Id)).Deploy[0];
            Assert.That(g2.Execute(new GameCommand(CommandKind.Deploy,Side.Player,record.Id,cell)).Success,Is.True);
            var unit=g2.GetSnapshot().Units.Single();
            Assert.That(g2.Execute(new GameCommand(CommandKind.EndTurn,Side.Player)).Success,Is.True);
            Assert.That(g2.GetSnapshot().Phase,Is.EqualTo(MatchPhase.Discard));
            Assert.That(g2.Execute(new GameCommand(CommandKind.Withdraw,Side.Player,unit.Id)).Success,Is.False,"弃牌阶段不能离场");
            Assert.That(g2.GetLegalActions(new Selection(SelectionKind.Unit,unit.Id)).CanWithdraw,Is.False,"弃牌阶段没有离场入口");
        }

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
            var d=Document();d.rules[0].drawCount=2;d.rules[0].handLimit=1;var g=Start(d,new DeckEntry("miner",3));End(g);
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
            var d=Document(); d.rules[0].homeRegionWidth=10;
            var miner=d.cards.Single(c=>c.id=="miner"); miner.atk=100; miner.range=30;
            var g=Start(d); var a=Put(g,"miner",2,8);
            Assert.That(g.Execute(new GameCommand(CommandKind.Attack,Side.Player,a.Id,targetId:-2)).Success,Is.True);
            var ships=g.GetSnapshot().BossWarships.ToArray();
            for(var i=0;i<2;i++)
            {
                var attacker=Put(g,"miner",0,i+3);
                Assert.That(g.Execute(new GameCommand(CommandKind.Attack,Side.Player,attacker.Id,targetId:ships[i].UnitId.Value)).Success,Is.True);
            }
            var finisher=Put(g,"miner",0,5);
            Assert.That(g.Execute(new GameCommand(CommandKind.Attack,Side.Player,finisher.Id,targetId:-2)).Success,Is.True);
            Assert.That(g.GetSnapshot().Outcome,Is.EqualTo(MatchOutcome.PlayerWins));
            Assert.That(g.Execute(new GameCommand(CommandKind.EndTurn,Side.Player)).Success,Is.False);
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
            var d=Document();d.rules[0].drawCount=1;var g=Start(d,new DeckEntry("miner",1));
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
            var d=Document();d.rules[0].drawCount=1;var g=Start(d,new DeckEntry("miner",1));
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
