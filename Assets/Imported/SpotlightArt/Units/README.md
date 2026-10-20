# 场地卡图（Unit Card Art）

`Cards.cardArtUnit` 列只填文件名时，图片从这个目录解析。

- 棋盘上的战斗单位（场上的卡）显示这里的图；
- 手牌区显示的是 `Cards.cardArt`，目录在 `../CardArt/`。

## 约定

- 文件名直接用卡种稳定 ID：`ge1.png`、`gate.png`、`bossCombat.png`……（ID 见 `Cards.id`）。
- 必须是 `.png`，并且 **Texture Type = Sprite (2D and UI)**；否则「编译全部配置」会报
  `卡种 xxx 的 cardArtUnit 找不到 Sprite：…（PNG 必须以 Sprite (2D and UI) 导入）`。
- 建议比例 **1:1**：棋盘格接近正方形，卡图节点开启了 `preserveAspect`，比例不同只会在内侧留边。
- 建议关闭 Mipmap、Max Size ≥ 1024；带透明通道的图请勾选 **Alpha Is Transparency**。
- 本目录的 PNG 走 Git LFS（`.gitattributes` 里 `*.png filter=lfs`），提交前确认是 LFS 指针。

## 留空表示什么？

`cardArtUnit` 留空 = 这张卡在场上沿用 `cardArt`（手牌图）；两列都留空时卡面回退纯色矩形。
所以“场上与手牌用同一张图”不需要在这里放图，也不必复制一份。

## 与阵营无关

卡图只跟卡种和使用场景有关，跟所属阵营无关。Boss 专用卡（`bossCombat`、`bossSpawner`、
`bossWarship`、`bossGuardType1`、`bossGuardType2`）不会出现在玩家手牌里，
只需要在这里放图并填 `cardArtUnit` 即可。

## 不想用这个目录？

`Cards.cardArtUnit` 也接受以 `Assets/` 开头的完整路径，此时图片可以放在工程内任意位置。
