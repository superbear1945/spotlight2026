# 卡图（Card Art）

`Cards.cardArt` 列只填文件名时，图片从这个目录解析。

## 约定

- 文件名建议直接用卡种稳定 ID：`ge1.png`、`gate.png`、`houyi2.png`……（ID 见 `Cards.id`）。
  文件名不必与 ID 相同，因为映射关系写在表里；只是这样最省事、也最不容易填错。
- 必须是 `.png`，并且 **Texture Type = Sprite (2D and UI)**；否则“编译全部配置”会报
  `卡种 xxx 的 cardArt 找不到 Sprite：…（PNG 必须以 Sprite (2D and UI) 导入）`。
- 建议比例 3:4（如 600×800）：手牌卡面固定 120×160，棋盘上会缩到“格子尺寸 − 8”，
  卡图节点已开启 `preserveAspect`，比例不同只会在内侧留边，不会变形。
- 建议关闭 Mipmap、Max Size ≥ 1024；带透明通道的图请勾选 **Alpha Is Transparency**。
- 本目录的 PNG 走 Git LFS（`.gitattributes` 里 `*.png filter=lfs`），提交前确认是 LFS 指针。

## 不想用这个目录？

`Cards.cardArt` 也接受以 `Assets/` 开头的完整路径，例如
`Assets/Imported/SpotlightArt/Cards/atp.png`，此时图片可以放在工程内任意位置。

## 留空表示什么？

`cardArt` 留空 = 这张卡没有卡图，卡面回退为原来的纯色矩形（分类色 × 阵营色）。
卡图只影响显示，不参与任何规则结算。
