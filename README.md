# Bishop BSG / BSA Tool

Bishop 引擎 BSS-Graphics（BSG）圖片轉換，以及 BSA 封包解包、修改後重新打包工具。

## BSA 使用方式

```text
Bishop_BSG_Tool -x [archive.bsa] [output_folder]
Bishop_BSG_Tool -a [input_folder] [output.bsa]
Bishop_BSG_Tool -v [archive.bsa]
```

### 解包

```bat
Bishop_BSG_Tool -x embed.bsa
Bishop_BSG_Tool -x embed.bsa embed_unpacked
```

- 自動辨識 BSA v1、v2、v3。
- 未指定輸出資料夾時，解包至 BSA 同目錄下的同名資料夾。
- 解包目標必須不存在或為空資料夾，避免覆蓋既有檔案。
- 解包會產生隱藏中繼資料 `.bsa_manifest.json`，請勿刪除；重新打包時會用它保留原始版本、索引記錄順序、原始檔名字節及目錄標記。

### 修改後重新打包

```bat
Bishop_BSG_Tool -a embed_unpacked embed_modified.bsa
```

以專案內的 `test\embed.bsa` 為例：

```bat
Bishop_BSG_Tool -x test\embed.bsa test\embed_unpacked
```

修改 `test\embed_unpacked\boot.lua` 後：

```bat
Bishop_BSG_Tool -a test\embed_unpacked test\embed_modified.bsa
Bishop_BSG_Tool -v test\embed_modified.bsa
```

- 保留原始 BSA 版本：v1 解包後仍打包為 v1，v2 保持 v2，v3 保持 v3。
- 原有檔案依中繼資料的原始索引順序寫入；新增檔案附加在原有記錄之後；已刪除檔案不再寫入。
- 修改檔案時，保持相同相對路徑與檔名即可替換內容。
- 支援遞迴子資料夾。原始空資料夾會透過中繼資料保留。
- 新增檔案會以 CP932 編碼檔名，並建立必要的目錄標記。
- 沒有 `.bsa_manifest.json` 時，會將資料夾內容建立成新的 BSA v3。
- BSA v1 的檔名欄位固定 32 bytes；超出長度會報錯，不會截斷。

### 自動驗證

打包時不會直接覆蓋目標 BSA：

1. 先寫入同目錄的暫存封包。
2. 重新解析暫存封包的標頭及索引。
3. 檢查版本、索引筆數、檔案路徑與資料範圍。
4. 將每個輸入檔案與封包內對應資料計算 SHA-256。
5. 全部通過後，才以暫存封包取代目標檔案。

也可以獨立檢查封包：

```bat
Bishop_BSG_Tool -v embed_modified.bsa
```

注意：程式內部驗證通過代表 BSA 結構可重新解析、封包資料與來源檔案一致；仍應使用 GARbro 回讀，並在目標遊戲中實際測試，才能確認該遊戲接受此封包。

## BSA 編碼與資料處理

- BSA 內部檔名使用 CP932（日文 Shift-JIS），與 GARbro Bishop `ArcBSA.cs` 的預設讀取方式一致。
- 若檔名無法以 CP932 表示，打包時會明確報錯；不會把無法表示的字元默默轉成 `?`。
- Lua、TXT、BSG、PNG 等封包內部檔案只複製原始位元組，不會因為檔名使用 CP932 就轉換檔案內容編碼。
- 不額外壓縮封包內的檔案資料。
- 解包會拒絕絕對路徑、`..` 路徑及 Windows 不允許的檔名，避免封包路徑寫出指定資料夾。

## BSG 圖片使用方式

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
- BSG 資料夾模式只處理資料夾第一層，不會遞迴子資料夾。
- 輸出檔案會放在來源檔案所在資料夾，並使用相同檔名。

### BSG 自動判斷方式

BSG 標頭中 `0x30` 位移處是 BPP 類型欄位：

| 標頭值 | 格式 | 每像素資料 |
|---|---|---:|
| `0x00` | BPP32（BGRA） | 4 bytes |
| `0x02` | BPP8（灰階／索引資料） | 1 byte |

- BSG 解包：讀取標頭 BPP 欄位。
- PNG 打包：8-bit Grayscale PNG 判定為 BPP8；其他圖片嘗試以 BPP32 處理，必須是 32-bits-per-pixel 圖片才能成功。

## 依賴套件

- SixLabors.ImageSharp 3.1.12
- System.Text.Encoding.CodePages 8.0.0（CP932 檔名編碼）
- 目標框架：.NET 8

ImageSharp 3.1.12 不需要授權金鑰。此版本仍有已公開的安全性公告，建議只處理可信任來源的 PNG／BSG 檔案。
