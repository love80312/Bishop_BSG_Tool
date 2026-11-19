
# Bishop BSG Tool　｜byＡＩ

PNG 與 Bishop 引擎 BSS-Graphics（BSG）圖片轉換工具。

## 使用方式

```text
Bishop_BSG_Tool -c -auto  [image.png|folder]
Bishop_BSG_Tool -c -bpp8  [image.png|folder]
Bishop_BSG_Tool -c -bpp32 [image.png|folder]

Bishop_BSG_Tool -e -auto  [image.bsg|folder]
Bishop_BSG_Tool -e -bpp8  [image.bsg|folder]
Bishop_BSG_Tool -e -bpp32 [image.bsg|folder]
```

- `-auto`：自動判斷格式，建議使用。
- `-bpp8`：強制使用 BPP8。
- `-bpp32`：強制使用 BPP32。
- 資料夾模式只處理資料夾第一層，不會遞迴子資料夾。
- 輸出檔案會放在來源檔案所在資料夾，並使用相同檔名。

## 自動判斷方式

### BSG 解包

BSG 標頭中 `0x30` 位移處是 BPP 類型欄位：

| 標頭值 | 格式 | 每像素資料 |
|---|---|---:|
| `0x00` | BPP32（BGRA） | 4 bytes |
| `0x02` | BPP8（灰階／索引資料） | 1 byte |

`-e -auto` 直接讀取這個欄位，不依賴副檔名、檔案大小或肉眼判斷。

### PNG 打包

`-c -auto` 讀取 PNG 內部格式資訊：

- 8-bit Grayscale PNG → BPP8
- 其他圖片會嘗試以 BPP32 處理；必須是 32-bits-per-pixel 圖片才能成功。

若自動判斷不符合來源圖片格式，可改用 `-bpp8` 或 `-bpp32` 明確指定。

## 修正項目

- BPP32 RLE 會分別解碼四個通道。
- RLE 壓縮使用有界限的索引運算，避免讀取資料陣列尾端之外的記憶體。
- RLE 解碼會檢查每個通道的長度、輸出像素數量及截斷資料。
- BSG 解包會驗證簽章、BPP 欄位、資料偏移、資料長度及壓縮方式。
- 批次處理會回報成功／失敗數量；只要有檔案失敗，程序結束碼就會是非零。

## 依賴套件

- SixLabors.ImageSharp 3.1.12
- 目標框架：.NET 8

ImageSharp 3.1.12 不需要授權金鑰。

注意：3.1.12 仍有已公開的安全性公告，不代表已修補所有已知漏洞。此版本先作為免金鑰相容性測試版本，建議只處理可信任來源的 PNG／BSG 檔案。
