// 一次性部署减攻（京都壹号）与场上战斗属性的 EditMode 回归测试。
// 输入：独立内存夹具（每条用例只调整与断言相关的字段）与正式工作簿；输出：NUnit 断言结果。
// 约束：全部通过公开入口推进对局（Execute / GetLegalActions / GetSnapshot），不反射篡改 GameSession 私有状态；
// 夹具使用 SeededRandom 固定种子，减攻量全部来自配置，测试不引用任何“京都壹号”名称分支。
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Spotlight.EditorTools;
using UnityEngine;

namespace Spotlight.Tests
{
    /// <summary>
    /// 一次性部署减攻规则、场上属性一致性与配置管线回归。
    /// 为什么单独成文件：它同时覆盖“单位持有独立战斗属性”与“来源单位提供一次减攻”两件事，
    /// 与既有的部署/移动/攻击回归分开，失败时能直接定位到本次功能。
    /// </summary>
    public class DeploymentPenaltyTests
    {
        /// <summary>
        /// 构造隔离配置。输入：无；输出：可直接构造对局的文档。
        /// 字段说明：资源卡 mine 用来验证“资源卡即使有攻击也不消耗效果”，
        /// sourceOne/Two/Three 是同机制但减攻量不同的来源卡（证明数值完全来自配置），
        /// soldier/heavy/zeroAttack 是不同攻击/射程/移动的攻击卡，bossPenalty 是 Boss 侧的同类来源。
        /// 说明：Deck 表已废除，抽牌堆由 <see cref="Deck"/> 在建局时显式覆盖；
        /// 夹具本身不用中级卡，因此额外放一张仅用于满足“中级至少一种可携带卡种”校验的填充卡。
        /// </summary>
        public static ConfigDocument Document()
        {
            return new ConfigDocument
            {
                cards = new[]
                {
                    new CardRow { tier = "basic", id = "mine", name = "矿机", category = "resource", hp = 4, produce = 1, resourceComponent = true, viewKey = "default" },
                    new CardRow { tier = "basic", id = "kyoto", name = "京都测试卡", category = "resource", hp = 1, produce = 3, resourceComponent = true, viewKey = "default", deploymentPenaltyComponent = true, deploymentAttackReduction = 2 },
                    new CardRow { tier = "basic", id = "sourceOne", name = "减攻一", category = "resource", hp = 3, resourceComponent = true, viewKey = "default", deploymentPenaltyComponent = true, deploymentAttackReduction = 1 },
                    new CardRow { tier = "basic", id = "sourceTwo", name = "减攻二", category = "resource", hp = 3, resourceComponent = true, viewKey = "default", deploymentPenaltyComponent = true, deploymentAttackReduction = 2 },
                    new CardRow { tier = "basic", id = "sourceThree", name = "减攻三", category = "resource", hp = 3, resourceComponent = true, viewKey = "default", deploymentPenaltyComponent = true, deploymentAttackReduction = 3 },
                    new CardRow { tier = "basic", id = "soldier", name = "士兵", category = "attack", hp = 5, atk = 2, range = 2, moveDistance = 3, viewKey = "default" },
                    new CardRow { tier = "advanced", id = "heavy", name = "重兵", category = "attack", hp = 5, atk = 4, range = 3, moveDistance = 1, viewKey = "default" },
                    new CardRow { tier = "basic", id = "zeroAttack", name = "零攻兵", category = "attack", hp = 2, atk = 0, range = 0, moveDistance = 0, viewKey = "default" },
                    new CardRow { tier = "none", id = "boss", name = "敌兵", category = "attack", hp = 3, atk = 1, range = 1, moveDistance = 1, viewKey = "default" },
                    new CardRow { tier = "none", id = "bossPenalty", name = "减攻敌兵", category = "attack", hp = 3, atk = 1, range = 1, moveDistance = 1, viewKey = "default", deploymentPenaltyComponent = true, deploymentAttackReduction = 1 },
                    new CardRow { tier = "none", id = "bossFighter", name = "战机", category = "attack", hp = 3, atk = 2, range = 1, moveDistance = 1, viewKey = "default" },
                    new CardRow { tier = "none", id = "spawner", name = "刷怪卡", category = "special", hp = 1, viewKey = "default" },
                    // 仅用于满足“中级至少一种可携带卡种”的配置校验，不参与任何断言。
                    new CardRow { tier = "intermediate", id = "fillerIntermediate", name = "夹具中级卡", category = "resource", hp = 1, resourceComponent = true, produce = 0, viewKey = "default" }
                },
                rules = new[]
                {
                    new RulesRow
                    {
                        drawPileSize = 24, tierRatioBasic = 70, tierRatioIntermediate = 20, tierRatioAdvanced = 10, maxTypesBasic = 5, maxTypesIntermediate = 3, maxTypesAdvanced = 2,
                        bossSpawnerAttackThreshold = 1000, bossSpawnerCountFromColumn = 1, bossSpawnerUnitThreshold = 3, bossSpawnerDeployFromColumn = 1,
                        bossSpawnerCardType = "spawner", bossSpawnerSpawnCardType = "bossFighter",
                        bossBombInterval = 1000, bossBombDamage = 3, bossBombColumn = 5,
                        rows = 5, columns = 10, homeRegionWidth = 2, playerPlays = 30, bossPlays = 10, drawCount = 40, handLimit = 200,
                        initialResources = 0, bossRespawnDelay = 2, playerSide = "left", firstSide = "player",
                        bossCardType = "bossPenalty", playerControlsBoss = true,
                        accumulateResources = true, summoningSickness = true, adjacentSwap = true
                    }
                },
                homes = new[] { new HomeRow { owner = "player", hp = 30, produce = 2 }, new HomeRow { owner = "boss", hp = 30, produce = 0 } },
                bossSlots = new[] { new BossSlotRow { id = "a", rowFraction = 0, edgeOffset = 1 }, new BossSlotRow { id = "b", rowFraction = .5, edgeOffset = 1 }, new BossSlotRow { id = "c", rowFraction = 1, edgeOffset = 1 } },
                presentation = new[]
                {
                    new PresentationRow
                    {
                        referenceWidth = 1920, referenceHeight = 1080, fontSize = 18, logLimit = 400, moveSeconds = .2f, feedbackSeconds = .1f, selectedScale = 1.05f,
                        playerColor = "#3366FF", bossColor = "#FF3333", resourceColor = "#33AA66", attackColor = "#EE8833", specialColor = "#AA55FF",
                        moveColor = "#33AAFF", attackTargetColor = "#FF3333", swapColor = "#AA33FF", deployColor = "#33FF99"
                    }
                }
            };
        }

        /// <summary>
        /// 本文件默认抽牌堆：八个玩家卡种各 10 张，共 80 张（与原 Deck 表夹具完全一致）。
        /// 输入：无；输出：DeckEntry 清单。
        /// 为什么需要：用例靠“80 张全部进牌区”断言牌区总数恒定，牌堆总量不能变。
        /// </summary>
        static DeckEntry[] Deck() => new[]
        {
            new DeckEntry("mine", 10), new DeckEntry("kyoto", 10), new DeckEntry("sourceOne", 10), new DeckEntry("sourceTwo", 10),
            new DeckEntry("sourceThree", 10), new DeckEntry("soldier", 10), new DeckEntry("heavy", 10), new DeckEntry("zeroAttack", 10)
        };

        /// <summary>
        /// 建立对局。输入：可选文档（默认使用本文件夹具）；输出：固定种子的对局实例。
        /// 说明：抽牌堆已不能写在文档里，统一在这里用 WithDeck 覆盖（requireDrawPileSize=false 表示夹具不凑满张数）。
        /// </summary>
        static GameSession Start(ConfigDocument d = null) => StartWith(d ?? Document(), Deck());

        /// <summary>
        /// 用指定抽牌堆建立对局。输入：文档与抽牌堆条目；输出：固定种子的对局实例。
        /// 为什么需要：个别用例需要“只几张卡”的小牌堆来验证洗回与被重新部署。
        /// </summary>
        static GameSession StartWith(ConfigDocument d, params DeckEntry[] deck)
            => new GameSession(new GameConfiguration(d).WithDeck(deck, false), new SeededRandom(42), 400);

        /// <summary>把手牌中的某卡种部署到指定格。输入：对局、卡种与坐标；输出：该格上的新单位快照。</summary>
        static UnitState Put(GameSession g, string type, int row, int col)
        {
            var card = g.GetSnapshot().Hand.First(x => x.TypeId == type);
            var result = g.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, new Cell(row, col)));
            Assert.That(result.Success, Is.True, result.Error);
            // 按格子取新单位：同一张卡牌记录可能已被洗回并再次部署，SourceCardId 不再唯一。
            return g.GetSnapshot().Units.Single(u => u.Position == new Cell(row, col));
        }

        /// <summary>
        /// 交出一次行动权。输入：对局；输出：无。
        /// 手动 Boss 用跳过放牌结束；若本回合进入弃牌阶段则先弃到上限，避免夹具手牌累积把用例卡在弃牌阶段。
        /// </summary>
        static void End(GameSession g)
        {
            for (var guard = 0; guard < 400; guard++)
            {
                var snapshot = g.GetSnapshot();
                if (snapshot.Phase == MatchPhase.Finished) return;
                if (snapshot.Phase == MatchPhase.Discard)
                {
                    var discard = g.Execute(new GameCommand(CommandKind.Discard, Side.Player, snapshot.Hand[0].Id));
                    Assert.That(discard.Success, Is.True, discard.Error);
                    continue;
                }
                var result = g.Execute(new GameCommand(CommandKind.EndTurn, snapshot.ActiveSide, skipDeployment: true));
                Assert.That(result.Success, Is.True, result.Error);
                return;
            }
            Assert.Fail("结束回合循环未收敛");
        }

        /// <summary>取当前场上指定单位的快照。输入：对局与单位 ID；输出：单位快照（不存在时为 null）。</summary>
        static UnitState Unit(GameSession g, long id) => g.GetSnapshot().Units.FirstOrDefault(u => u.Id == id);

        /// <summary>
        /// 单来源触发一次：下一己方攻击卡按配置减攻，来源标记已消耗，并输出一条减攻日志。
        /// 输入：减攻量与目标攻击力；输出：断言结果。
        /// 为什么用参数化：减攻量必须完全来自配置，而不是代码里写死的 2。
        /// </summary>
        [TestCase("sourceOne", 1, "heavy", 4, 3)]
        [TestCase("sourceTwo", 2, "heavy", 4, 2)]
        [TestCase("sourceThree", 3, "heavy", 4, 1)]
        public void SingleSourceReducesNextAttackUnitByConfiguredAmount(string source, int reduction, string target, int baseAttack, int expected)
        {
            var g = Start();
            var src = Put(g, source, 0, 3);
            Assert.That(src.DeploymentPenaltyPending, Is.True, "具有该能力的单位上场后应立即拥有一次未消耗效果");
            Assert.That(src.DeploymentAttackReduction, Is.EqualTo(reduction), "减攻量必须来自配置");
            var hit = Put(g, target, 0, 0);
            Assert.That(hit.Attack, Is.EqualTo(expected));
            Assert.That(hit.Attack, Is.EqualTo(Math.Max(0, baseAttack - reduction)), "减攻下限应为 0");
            Assert.That(Unit(g, src.Id).DeploymentPenaltyPending, Is.False, "触发后来源应变为已消耗");
            Assert.That(g.GetSnapshot().Log.Any(e => e.Kind == "deployment-penalty"), Is.True, "缺少减攻日志");
        }

        /// <summary>
        /// 攻击为 0 或 1 时仍消耗完整一次效果，不保留“剩余减攻”。
        /// 输入：目标卡种；输出：断言结果。
        /// </summary>
        [TestCase("zeroAttack", 0)]
        [TestCase("soldier", 0)]
        [TestCase("heavy", 2)]
        public void ZeroAndLowAttackTargetsStillConsumeTheWholeEffect(string target, int expected)
        {
            var g = Start();
            var src = Put(g, "sourceTwo", 0, 3);
            var hit = Put(g, target, 0, 0);
            Assert.That(hit.Attack, Is.EqualTo(expected));
            Assert.That(Unit(g, src.Id).DeploymentPenaltyPending, Is.False);
            // 第二次入场不会被同一来源影响：减攻一次就结束，不存在“剩余 1 点”留到下一次。
            var second = Put(g, "heavy", 1, 0);
            Assert.That(second.Attack, Is.EqualTo(4));
        }

        /// <summary>
        /// 多来源按部署顺序依次触发：先上场的来源先消耗，单个目标最多承受一次减攻，效果不叠加。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void MultipleSourcesFireInDeploymentOrderWithoutStacking()
        {
            var g = Start();
            var first = Put(g, "sourceOne", 0, 3);
            var second = Put(g, "sourceThree", 0, 4);
            var a = Put(g, "heavy", 0, 0);
            var b = Put(g, "soldier", 1, 0);
            Assert.That(a.Attack, Is.EqualTo(3), "先上场的来源（减 1）应先消耗，且不与后一个来源叠加");
            Assert.That(b.Attack, Is.EqualTo(0), "第二个来源（减 3）应作用于下一个目标");
            Assert.That(Unit(g, first.Id).DeploymentPenaltyPending, Is.False);
            Assert.That(Unit(g, second.Id).DeploymentPenaltyPending, Is.False);
        }

        /// <summary>
        /// 来源在触发前离场：未触发效果随来源消失，排在其后的来源正确接替。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void SourceLeavingBeforeTriggerLosesItsPendingEffect()
        {
            var g = Start();
            var first = Put(g, "sourceOne", 0, 3);
            var second = Put(g, "sourceThree", 0, 4);
            Assert.That(g.Execute(new GameCommand(CommandKind.Withdraw, Side.Player, first.Id)).Success, Is.True);
            var hit = Put(g, "heavy", 0, 0);
            Assert.That(hit.Attack, Is.EqualTo(1), "只剩减 3 的来源，说明已离场来源的效果不再生效");
            Assert.That(Unit(g, second.Id).DeploymentPenaltyPending, Is.False);
        }

        /// <summary>
        /// 目标属性持续：跨回合、来源离场都不恢复目标攻击力。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void ReducedAttackPersistsAcrossTurnsAndAfterSourceLeaves()
        {
            var g = Start();
            var src = Put(g, "sourceTwo", 0, 3);
            var hit = Put(g, "heavy", 0, 0);
            Assert.That(hit.Attack, Is.EqualTo(2));
            End(g); End(g);
            Assert.That(Unit(g, hit.Id).Attack, Is.EqualTo(2), "回合切换不恢复攻击力");
            Assert.That(g.Execute(new GameCommand(CommandKind.Withdraw, Side.Player, src.Id)).Success, Is.True);
            Assert.That(Unit(g, hit.Id).Attack, Is.EqualTo(2), "来源离场不恢复目标攻击力");
        }

        /// <summary>
        /// 同一卡牌记录再次部署时创建独立单位并独立获得一次效果，旧单位状态不变。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void RedeployingTheSameCardRecordGainsAFreshIndependentEffect()
        {
            var d = Document();
            d.rules[0].drawCount = 5;
            var g = StartWith(d, new DeckEntry("sourceOne", 1), new DeckEntry("heavy", 4));
            var first = Put(g, "sourceOne", 0, 3);
            var hitA = Put(g, "heavy", 0, 0);
            Assert.That(hitA.Attack, Is.EqualTo(3));
            Assert.That(Unit(g, first.Id).DeploymentPenaltyPending, Is.False);
            // 推进到同一张卡洗回手牌，再部署成第二个来源单位。
            End(g); End(g);
            Assert.That(g.GetSnapshot().Hand.Count(c => c.TypeId == "sourceOne"), Is.GreaterThan(0), "来源卡应已洗回手牌");
            var second = Put(g, "sourceOne", 0, 4);
            Assert.That(second.Id, Is.Not.EqualTo(first.Id));
            Assert.That(second.DeploymentPenaltyPending, Is.True, "新单位独立获得一次效果");
            var hitB = Put(g, "heavy", 1, 0);
            Assert.That(hitB.Attack, Is.EqualTo(3), "第二个来源同样按配置减攻");
            Assert.That(Unit(g, first.Id).DeploymentPenaltyPending, Is.False, "旧来源的消耗状态不被恢复");
        }

        /// <summary>
        /// 目标范围：资源卡即使有攻击也不触发，特殊卡与敌方单位同样不消耗次数。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void OnlySameSideAttackUnitsConsumeTheEffect()
        {
            var d = Document();
            d.cards.Single(c => c.id == "mine").atk = 2;
            d.cards.Single(c => c.id == "mine").range = 1;
            var g = Start(d);
            var src = Put(g, "sourceTwo", 0, 3);
            var resource = Put(g, "mine", 1, 0);
            Assert.That(resource.Attack, Is.EqualTo(2), "资源卡不承受减攻");
            Assert.That(Unit(g, src.Id).DeploymentPenaltyPending, Is.True, "资源卡入场不消耗效果");
            // 敌方攻击卡入场同样不消耗我方来源。
            End(g);
            Assert.That(g.Execute(new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(0, 8))).Success, Is.True);
            Assert.That(Unit(g, src.Id).DeploymentPenaltyPending, Is.True, "敌方单位入场不消耗我方效果");
            End(g);
            var hit = Put(g, "soldier", 1, 1);
            Assert.That(hit.Attack, Is.EqualTo(0));
            Assert.That(Unit(g, src.Id).DeploymentPenaltyPending, Is.False);
        }

        /// <summary>
        /// 召唤失调、回合开始与欠费都不关闭被动效果，效果跨回合保留且产出不补充次数。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void PassiveStaysAvailableAcrossTurnsAndIgnoresSicknessAndUpkeep()
        {
            var d = Document();
            d.homes[0].produce = 0;
            d.cards.Single(c => c.id == "sourceOne").upkeep = 1;
            d.cards.Single(c => c.id == "sourceOne").resourceComponent = true;
            var g = Start(d);
            var src = Put(g, "sourceOne", 0, 3);
            Assert.That(Unit(g, src.Id).Acted, Is.True, "召唤失调下刚上场不能行动");
            End(g); End(g);
            var afterTurn = Unit(g, src.Id);
            Assert.That(afterTurn.UpkeepPaid, Is.False, "维持费不足时本回合不能行动");
            Assert.That(afterTurn.DeploymentPenaltyPending, Is.True, "欠费与回合开始不消耗或关闭效果");
            var hit = Put(g, "soldier", 0, 0);
            Assert.That(hit.Attack, Is.EqualTo(1), "被动照常触发");
        }

        /// <summary>
        /// 失败原子性：非法部署被拒绝后，来源次数、手牌、墓地与日志都不改变。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void FailedDeploymentKeepsSourceAndStateUntouched()
        {
            var g = Start();
            var src = Put(g, "sourceTwo", 0, 3);
            var before = g.GetSnapshot();
            // 用占用格（来源所在格）部署攻击卡：普通卡不能覆盖已有单位。
            var card = before.Hand.First(c => c.TypeId == "soldier");
            var result = g.Execute(new GameCommand(CommandKind.Deploy, Side.Player, card.Id, src.Position));
            Assert.That(result.Success, Is.False);
            var after = g.GetSnapshot();
            Assert.That(after.Units.Single(u => u.Id == src.Id).DeploymentPenaltyPending, Is.True);
            Assert.That(after.Hand.Count, Is.EqualTo(before.Hand.Count));
            Assert.That(after.Graveyard.Count, Is.EqualTo(before.Graveyard.Count));
            Assert.That(result.Events.Any(e => e.Kind == "deployment-penalty"), Is.False);
        }

        /// <summary>
        /// 手动 Boss 部署同样只结算一次：第三个 Boss 单位会被前一个 Boss 来源减攻。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void ManualBossDeployUsesBossSideSourcesInOrder()
        {
            var g = Start();
            End(g);
            Assert.That(g.Execute(new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(0, 8))).Success, Is.True);
            Assert.That(g.Execute(new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(1, 8))).Success, Is.True);
            var boss = g.GetSnapshot().Units.Where(u => u.Owner == Side.Boss).OrderBy(u => u.DeploymentOrder).ToArray();
            Assert.That(boss[0].Attack, Is.EqualTo(1), "第一个来源没有更早的来源可用");
            Assert.That(boss[1].Attack, Is.EqualTo(0), "第二个单位被前一个来源减 1");
            Assert.That(boss[0].DeploymentPenaltyPending, Is.False);
            Assert.That(boss[1].DeploymentPenaltyPending, Is.True);
        }

        /// <summary>
        /// AI 首次部署三个兵位时按部署顺序结算，只影响后续单位。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void AiFirstDeploymentConsumesSourcesInSlotOrder()
        {
            var d = Document();
            d.rules[0].firstSide = "boss";
            d.rules[0].playerControlsBoss = false;
            var snapshot = Start(d).GetSnapshot();
            var boss = snapshot.Units.Where(u => u.Owner == Side.Boss).OrderBy(u => u.DeploymentOrder).ToArray();
            Assert.That(boss.Length, Is.EqualTo(3));
            Assert.That(boss.Select(u => u.Attack).ToArray(), Is.EqualTo(new[] { 1, 0, 0 }));
            Assert.That(boss.Select(u => u.DeploymentPenaltyPending).ToArray(), Is.EqualTo(new[] { false, false, true }));
        }

        /// <summary>
        /// 刷怪卡生成的战机同样参与结算（技能生成路径不经过玩家命令）。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void SpawnerFighterAlsoTriggersDeploymentPenalty()
        {
            var d = Document();
            d.rules[0].playerControlsBoss = false;
            d.rules[0].bossSpawnerAttackThreshold = 1;
            d.rules[0].bossSpawnerUnitThreshold = 1;
            d.rules[0].bossSpawnerCountFromColumn = 1;
            d.rules[0].bossSpawnerDeployFromColumn = 1;
            var g = Start(d);
            Put(g, "soldier", 0, 0);
            End(g);   // Boss 回合 1：部署刷怪卡、三个兵位与普通行动
            End(g);   // 玩家回合 2 结束 → Boss 回合 2 生成战机
            var fighter = g.GetSnapshot().Units.FirstOrDefault(u => u.TypeId == "bossFighter");
            Assert.That(fighter, Is.Not.Null, string.Join("\n", g.GetSnapshot().Log.Select(e => e.Message)));
            Assert.That(fighter.Attack, Is.EqualTo(1), "战机应按 Boss 侧最后一个可用来源减 1（配置攻击 2）");
        }

        /// <summary>
        /// 当前攻击力驱动伤害与合法目标：减攻后伤害降低、攻击归零时没有攻击高亮也不能攻击。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void CurrentAttackDrivesDamageAndLegalActions()
        {
            // homeRegionWidth 设为整块棋盘，让 Boss 攻击卡可以紧邻玩家单位部署（否则只能落在右侧两列）。
            var d = Document();
            d.rules[0].homeRegionWidth = 10;
            var g = Start(d);
            Put(g, "sourceTwo", 0, 3);
            var attacker = Put(g, "heavy", 0, 0);   // 4 - 2 = 2
            End(g);
            Assert.That(g.Execute(new GameCommand(CommandKind.BossDeploy, Side.Boss, target: new Cell(0, 1))).Success, Is.True);
            var enemyId = g.GetSnapshot().Units.Single(u => u.Owner == Side.Boss).Id;
            End(g);
            var attack = g.Execute(new GameCommand(CommandKind.Attack, Side.Player, attacker.Id, targetId: enemyId));
            Assert.That(attack.Success, Is.True, attack.Error);
            Assert.That(g.GetSnapshot().Units.Single(u => u.Id == enemyId).Hp, Is.EqualTo(1), "伤害应使用当前攻击 2（敌兵 3 血）");
            End(g);
            End(g);
            // 攻击归零的攻击卡：不再有攻击高亮，也不能攻击。
            Put(g, "sourceTwo", 2, 3);
            var zeroed = Put(g, "soldier", 1, 0);
            Assert.That(zeroed.Attack, Is.EqualTo(0));
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit, zeroed.Id)).Attack, Is.Empty, "攻击为 0 时不应有攻击目标");
            var liveId = g.GetSnapshot().Units.Single(u => u.Owner == Side.Boss).Id;
            Assert.That(g.Execute(new GameCommand(CommandKind.Attack, Side.Player, zeroed.Id, targetId: liveId)).Success, Is.False);
        }

        /// <summary>
        /// 刷怪条件读取当前攻击力：减攻会压低总攻击力，而攻击为 0 的单位仍计入攻击类数量。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void SpawnerThresholdUsesCurrentAttackButCountsZeroAttackUnits()
        {
            var d = Document();
            d.rules[0].bossSpawnerAttackThreshold = 1;
            d.rules[0].bossSpawnerUnitThreshold = 3;
            d.rules[0].bossSpawnerCountFromColumn = 1;
            d.rules[0].bossSpawnerDeployFromColumn = 1;
            var g = Start(d);
            Put(g, "sourceTwo", 0, 3);
            var reduced = Put(g, "soldier", 0, 0);   // 2 - 2 = 0
            Put(g, "zeroAttack", 1, 0);              // 配置就是 0 攻击
            Assert.That(reduced.Attack, Is.EqualTo(0));
            var second = Put(g, "soldier", 0, 1);    // 来源已消耗，保持 2
            Assert.That(second.Attack, Is.EqualTo(2));
            End(g);
            Assert.That(g.GetSnapshot().BossSpawnerPending, Is.True, "总攻击力 2 ≥ 1 且攻击类数量 3 ≥ 3，应当触发刷怪卡");

            // 反例：没有第三个单位时数量不足（零攻单位也算一个，但减攻后总攻击力仍为 0）。
            var d2 = Document();
            d2.rules[0].bossSpawnerAttackThreshold = 1;
            d2.rules[0].bossSpawnerUnitThreshold = 2;
            var g2 = Start(d2);
            Put(g2, "sourceTwo", 0, 3);
            Put(g2, "soldier", 0, 0);
            Assert.That(g2.GetSnapshot().Units.Sum(u => u.Attack), Is.EqualTo(0), "减攻后总攻击力应为 0");
            End(g2);
            Assert.That(g2.GetSnapshot().BossSpawnerPending, Is.False, "总攻击力不足时不应触发");
        }

        /// <summary>
        /// 配置草稿隔离：创建对局后修改原文档，不影响已有单位与本局后续部署的初值。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void MutatingTheDraftAfterStartDoesNotAffectTheSession()
        {
            var d = Document();
            var g = Start(d);
            Put(g, "sourceTwo", 0, 3);
            var hit = Put(g, "heavy", 0, 0);
            Assert.That(hit.Attack, Is.EqualTo(2));
            d.cards.Single(c => c.id == "heavy").atk = 9;
            d.cards.Single(c => c.id == "sourceTwo").deploymentAttackReduction = 5;
            Assert.That(Unit(g, hit.Id).Attack, Is.EqualTo(2), "已有单位不应受草稿修改影响");
            var later = Put(g, "heavy", 1, 0);
            Assert.That(later.Attack, Is.EqualTo(4), "后续部署仍应使用建局时的配置值");
        }

        /// <summary>
        /// 新资源卡的基础行为：部署当下不立即产出、下一己方回合开始时按配置产出，零移动且不能互换。
        /// 输入：无；输出：断言结果。
        /// </summary>
        [Test]
        public void NewResourceCardProducesNextTurnAndCannotMoveOrSwap()
        {
            var g = Start();
            var before = g.GetSnapshot();
            var unit = Put(g, "kyoto", 0, 9);
            Assert.That(g.GetSnapshot().Resources, Is.EqualTo(before.Resources), "部署当下不立即获得产出");
            Assert.That(unit.MoveDistance, Is.EqualTo(0));
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit, unit.Id)).Move, Is.Empty);
            Assert.That(g.GetLegalActions(new Selection(SelectionKind.Unit, unit.Id)).Swap, Is.Empty);
            Assert.That(g.GetSnapshot().Graveyard.Any(c => c.TypeId == "kyoto"), Is.True, "成功部署即进入墓地");
            End(g); End(g);
            Assert.That(g.GetSnapshot().Resources, Is.EqualTo(before.Resources + 2 + 3), "下一己方回合开始按配置产出 3（家 +2）");
            Assert.That(g.GetSnapshot().Hand.Count + g.GetSnapshot().DrawPile.Count + g.GetSnapshot().Graveyard.Count, Is.EqualTo(80), "牌堆三区总数应恒定");
        }

        /// <summary>
        /// 配置校验：新字段的矛盾值必须全部报出，合法值通过。
        /// 输入：字段修改方式；输出：断言结果。
        /// </summary>
        [Test]
        public void DeploymentPenaltyFieldsAreValidated()
        {
            var enabledWithoutAmount = Document();
            enabledWithoutAmount.cards.Single(c => c.id == "kyoto").deploymentAttackReduction = 0;
            var errors = ConfigValidation.Validate(enabledWithoutAmount);
            Assert.That(errors.Any(e => e.Contains("deploymentAttackReduction")), Is.True, string.Join("\n", errors));

            var disabledWithAmount = Document();
            disabledWithAmount.cards.Single(c => c.id == "kyoto").deploymentPenaltyComponent = false;
            Assert.That(ConfigValidation.Validate(disabledWithAmount).Any(e => e.Contains("deploymentAttackReduction")), Is.True);

            var negative = Document();
            negative.cards.Single(c => c.id == "kyoto").deploymentAttackReduction = -2;
            Assert.That(ConfigValidation.Validate(negative).Any(e => e.Contains("deploymentAttackReduction")), Is.True);

            Assert.That(ConfigValidation.Validate(Document()), Is.Empty);
            Assert.That(TableSchema.For("Cards").Find("deploymentPenaltyComponent").Required, Is.True);
            Assert.That(TableSchema.For("Cards").Find("deploymentAttackReduction").Required, Is.True);
            Assert.That(TableSchema.For("Cards").Find("deploymentPenaltyComponent").Description, Does.Contain("默认"));
        }

        /// <summary>
        /// 旧 JSON 缺少两个新字段时按“无能力”读取（false / 0），不自动补卡也不改卡组数量。
        /// 输入：去掉新字段的 JSON；输出：断言结果。
        /// </summary>
        [Test]
        public void LegacyJsonWithoutNewFieldsReadsAsDisabled()
        {
            var draft = new DebugConfigDraft(null);
            // 两个新列是 CardRow 的最后两个字段，因此总带前导逗号；逐个删除所有卡种的这两个字段，
            // 模拟“旧版 JSON 完全没有这两列”的真实情况。
            var json = System.Text.RegularExpressions.Regex.Replace(JsonUtility.ToJson(Document()), ",\"deploymentPenaltyComponent\":(true|false)", string.Empty);
            json = System.Text.RegularExpressions.Regex.Replace(json, ",\"deploymentAttackReduction\":\\d+", string.Empty);
            Assert.That(json, Does.Not.Contain("deploymentPenaltyComponent"), "夹具未能移除新字段");
            Assert.That(json, Does.Not.Contain("deploymentAttackReduction"), "夹具未能移除新字段");
            Assert.That(draft.Import(json, out var error, out _), Is.True, error);
            var kyoto = draft.Document.cards.Single(c => c.id == "kyoto");
            Assert.That(kyoto.deploymentPenaltyComponent, Is.False);
            Assert.That(kyoto.deploymentAttackReduction, Is.Zero);
            Assert.That(draft.Validate(), Is.Empty);
            Assert.That(draft.TryCreateConfiguration(out var configuration, out error), Is.True, error);
            Assert.That(configuration.Cards["kyoto"].DeploymentPenalty, Is.Null, "缺少字段不得凭空获得能力");
            // 抽牌堆改由张数与阶级比例推导，总张数恒等于 drawPileSize（不再是 Deck 表里的 80 张）。
            Assert.That(configuration.Deck.Sum(e => e.Count), Is.EqualTo(configuration.DeckConfig.DrawPileSize), "抽牌堆总量应等于配置张数");

            // JSON 往返不丢新字段（旧 JSON 读成禁用后，面板重新启用也应能完整导出与导入）。
            var restored = draft.Document.cards.Single(c => c.id == "kyoto");
            restored.deploymentPenaltyComponent = true;
            restored.deploymentAttackReduction = 3;
            Assert.That(draft.Import(draft.ExportJson(), out error, out _), Is.True, error);
            Assert.That(draft.Document.cards.Single(c => c.id == "kyoto").deploymentPenaltyComponent, Is.True);
            Assert.That(draft.Document.cards.Single(c => c.id == "kyoto").deploymentAttackReduction, Is.EqualTo(3));
        }

        /// <summary>
        /// 卡种 SO 与表行/运行配置三路一致：启用时带减攻量，禁用时引用为空且减攻量为 0。
        /// 输入：内存创建的 CardDefinitionSO 与子配置；输出：断言结果。
        /// </summary>
        [Test]
        public void CardDefinitionSoRoundTripsDeploymentPenalty()
        {
            var definition = ScriptableObject.CreateInstance<CardDefinitionSO>();
            var combat = ScriptableObject.CreateInstance<CombatConfigSO>();
            var resource = ScriptableObject.CreateInstance<ResourceConfigSO>();
            var penalty = ScriptableObject.CreateInstance<DeploymentPenaltyConfigSO>();
            try
            {
                combat.EditorApply(1, 0, 0, 0);
                resource.EditorApply(3, 0);
                penalty.EditorApply(2);
                var row = new CardRow { tier = "basic", id = "kyoto", name = "京都壹号", category = "resource", viewKey = "default", hp = 1, produce = 3, resourceComponent = true, deploymentPenaltyComponent = true, deploymentAttackReduction = 2 };
                definition.EditorApply(row, combat, resource, null, penalty, null, "w.xlsx", "Cards");

                var restored = definition.ToCardRow();
                Assert.That(restored.deploymentPenaltyComponent, Is.True);
                Assert.That(restored.deploymentAttackReduction, Is.EqualTo(2));
                var archetype = definition.ToArchetype();
                Assert.That(archetype.DeploymentPenalty, Is.Not.Null);
                Assert.That(archetype.DeploymentPenalty.AttackReduction, Is.EqualTo(2));

                definition.EditorApply(row, combat, resource, null, null, null, "w.xlsx", "Cards");
                Assert.That(definition.ToCardRow().deploymentPenaltyComponent, Is.False);
                Assert.That(definition.ToCardRow().deploymentAttackReduction, Is.Zero);
                Assert.That(definition.ToArchetype().DeploymentPenalty, Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(penalty);
                UnityEngine.Object.DestroyImmediate(resource);
                UnityEngine.Object.DestroyImmediate(combat);
                UnityEngine.Object.DestroyImmediate(definition);
            }
        }

        /// <summary>
        /// 默认工作簿声明并填写两个新列，京都壹号默认 2 张；编译后可开局。
        /// 输入：临时默认工作簿；输出：断言结果。
        /// </summary>
        [Test]
        public void DefaultWorkbookContainsKyotoAndDeploymentPenaltyColumns()
        {
            var path = Path.Combine(Path.GetTempPath(), "spotlight-penalty-" + Guid.NewGuid().ToString("N") + ".xlsx");
            try
            {
                DefaultWorkbookWriter.WriteDefault(path);
                var report = ConfigCompiler.Compile(path, false);
                Assert.That(report.Issues, Is.Empty, string.Join("\n", report.Issues));
                var kyoto = report.Document.cards.Single(c => c.id == "kyoto_01");
                Assert.That(kyoto.name, Is.EqualTo("京都壹号"));
                Assert.That(kyoto.category, Is.EqualTo("resource"));
                Assert.That(new[] { kyoto.hp, kyoto.atk, kyoto.range, kyoto.moveDistance }, Is.EqualTo(new[] { 1, 0, 0, 0 }));
                Assert.That(new[] { kyoto.produce, kyoto.upkeep }, Is.EqualTo(new[] { 3, 0 }));
                Assert.That(kyoto.deploymentPenaltyComponent, Is.True);
                Assert.That(kyoto.deploymentAttackReduction, Is.EqualTo(2));
                Assert.That(report.Document.cards.Where(c => c.id != "kyoto_01").All(c => !c.deploymentPenaltyComponent), Is.True, "其余卡不得隐式获得该能力");
                var configuration = new GameConfiguration(report.Document);
                Assert.That(configuration.Cards["kyoto_01"].DeploymentPenalty.AttackReduction, Is.EqualTo(2));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        /// <summary>
        /// 正式工作簿已按本次任务补齐京都壹号与两个新列（只校验不发布，不改动生成资产）。
        /// 输入：正式工作簿；输出：断言结果。
        /// </summary>
        [Test]
        public void RealWorkbookCompilesWithKyotoCard()
        {
            var report = ConfigCompiler.Compile(null, false);
            Assert.That(report.Issues, Is.Empty, string.Join("\n", report.Issues));
            var kyoto = report.Document.cards.Single(c => c.id == "kyoto_01");
            Assert.That(kyoto.deploymentPenaltyComponent, Is.True);
            Assert.That(kyoto.deploymentAttackReduction, Is.EqualTo(2));
            Assert.That(kyoto.produce, Is.EqualTo(3));
        }

        /// <summary>
        /// 新增中文文案能被项目字体渲染，避免出现方框。
        /// 输入：项目字体资产；输出：缺字断言。
        /// </summary>
        [Test]
        public void NewChineseTextIsSupportedByProjectFont()
        {
            var font = UnityEditor.AssetDatabase.LoadAssetAtPath<Font>(CjkFontSetup.FontPath);
            Assert.That(font, Is.Not.Null);
            const string text = "京都壹号一次性部署减攻待触发已消耗配置减攻量随后成功上场的一个己方攻击类单位攻击力降低最低持续至离场";
            foreach (var character in text.Distinct())
                Assert.That(font.HasCharacter(character), Is.True, "字体缺少：" + character);
        }
    }
}
